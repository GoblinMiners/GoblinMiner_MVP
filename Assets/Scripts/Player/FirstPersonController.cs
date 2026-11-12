using UnityEngine;
using UnityEngine.InputSystem;

namespace ShopkeepOre.Player
{
    /// <summary>
    /// Local-only first-person controller, used as a test harness for other systems.
    /// Moves with a CharacterController and reads input from the project-wide
    /// Input System actions (the InputSystem_Actions asset).
    ///
    /// This script knows nothing about inventory, stamina, or encumbrance.
    /// Other systems change its behaviour through three modifiers:
    /// SpeedMultiplier, CanJump, and CanSprint. (GDD section 9.)
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class FirstPersonController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The camera at eye height. Up/down look (pitch) is applied to this.")]
        [SerializeField] private Transform cameraRoot;

        [Header("Movement")]
        [SerializeField] private float walkSpeed = 4f;
        [SerializeField] private float sprintSpeed = 7f;
        [Tooltip("How fast horizontal speed changes, in m/s per second. Higher = snappier.")]
        [SerializeField] private float acceleration = 40f;

        [Header("Jumping & Gravity")]
        [SerializeField] private float jumpHeight = 1.3f;
        [Tooltip("Must be negative. Stronger than real gravity (-9.81) feels less floaty.")]
        [SerializeField] private float gravity = -20f;

        [Header("Look")]
        [Tooltip("Degrees of rotation per pixel of mouse movement. Tuned for mouse, not gamepad.")]
        [SerializeField] private float mouseSensitivity = 0.1f;
        [SerializeField] private float maxPitch = 85f;

        [Header("External Modifiers (set by other systems; editable in Play mode)")]
        [SerializeField, Range(0f, 2f)] private float speedMultiplier = 1f;
        [SerializeField] private bool canJump = true;
        [SerializeField] private bool canSprint = true;


        public float SpeedMultiplier
        {
            get => speedMultiplier;
            set => speedMultiplier = Mathf.Max(0f, value);
        }

        public bool CanJump
        {
            get => canJump;
            set => canJump = value;
        }

        public bool CanSprint
        {
            get => canSprint;
            set => canSprint = value;
        }

        public bool IsGrounded => controller != null && controller.isGrounded;

        /// <summary>True only while sprint is held AND the player is actually moving.</summary>
        public bool IsSprinting { get; private set; }

        // ----- Internal state -----

        private CharacterController controller;

        private InputAction moveAction;
        private InputAction lookAction;
        private InputAction jumpAction;
        private InputAction sprintAction;

        private Vector3 horizontalVelocity;
        private float verticalVelocity;
        private float pitch;

        private void Awake()
        {
            controller = GetComponent<CharacterController>();

            if (cameraRoot == null)
            {
                Debug.LogError("[FirstPersonController] Camera Root is not assigned.", this);
                enabled = false;
                return;
            }

            InputActionAsset actions = InputSystem.actions;
            if (actions == null)
            {
                Debug.LogError("[FirstPersonController] No project-wide input actions found. " +
                               "Check Project Settings > Input System Package > Project-wide Actions.", this);
                enabled = false;
                return;
            }

            // "Map/Action" names, matching the default InputSystem_Actions asset.
            moveAction = actions.FindAction("Player/Move");
            lookAction = actions.FindAction("Player/Look");
            jumpAction = actions.FindAction("Player/Jump");
            sprintAction = actions.FindAction("Player/Sprint");

            if (moveAction == null || lookAction == null || jumpAction == null || sprintAction == null)
            {
                Debug.LogError("[FirstPersonController] Missing one of Player/Move, Look, Jump or Sprint " +
                               "in the input actions asset.", this);
                enabled = false;
            }
        }

        private void Start()
        {
            SetCursorLocked(true);
        }

        private void OnDisable()
        {
            // Never leave the mouse trapped if this script gets switched off.
            SetCursorLocked(false);
        }

        private void Update()
        {
            HandleLook();
            HandleMovement();
        }

        /// <summary>
        /// Locked = mouse controls the camera. Unlocked = mouse is free (for menus/UI).
        /// The inventory UI will call this later.
        /// </summary>
        public void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        private void HandleLook()
        {
            // Don't turn the view while the cursor is free (e.g. a menu is open).
            if (Cursor.lockState != CursorLockMode.Locked)
                return;

            // Mouse delta is already "pixels moved this frame", so no Time.deltaTime here.
            Vector2 look = lookAction.ReadValue<Vector2>() * mouseSensitivity;

            // Yaw: turn the whole player left/right, so "forward" for movement follows the view.
            transform.Rotate(0f, look.x, 0f);

            // Pitch: tilt only the camera up/down, clamped so the view can't flip over.
            pitch = Mathf.Clamp(pitch - look.y, -maxPitch, maxPitch);
            cameraRoot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }

        private void HandleMovement()
        {
            // isGrounded reflects the result of LAST frame's Move call.
            bool grounded = controller.isGrounded;

            // ----- Horizontal -----
            Vector2 input = moveAction.ReadValue<Vector2>();

            // Convert 2D input into a world direction relative to where the player faces.
            Vector3 inputDirection = transform.right * input.x + transform.forward * input.y;
            inputDirection = Vector3.ClampMagnitude(inputDirection, 1f); // no faster diagonals

            bool isMoving = input.sqrMagnitude > 0.01f;
            IsSprinting = canSprint && isMoving && sprintAction.IsPressed();

            float targetSpeed = (IsSprinting ? sprintSpeed : walkSpeed) * speedMultiplier;
            Vector3 targetVelocity = inputDirection * targetSpeed;

            // Ease toward the target speed instead of snapping to it.
            horizontalVelocity = Vector3.MoveTowards(
                horizontalVelocity, targetVelocity, acceleration * Time.deltaTime);

            // ----- Vertical -----
            if (grounded && verticalVelocity < 0f)
            {
                // A small constant downward push keeps the controller pressed onto
                // slopes and steps, which keeps isGrounded reliable.
                verticalVelocity = -2f;
            }

            if (grounded && canJump && jumpAction.WasPressedThisFrame())
            {
                // Launch speed needed to reach jumpHeight under this gravity: v = sqrt(2 * g * h)
                verticalVelocity = Mathf.Sqrt(2f * -gravity * jumpHeight);
            }

            verticalVelocity += gravity * Time.deltaTime;

            // ----- Apply -----
            // Exactly one Move call per frame. Calling Move twice makes isGrounded unreliable.
            Vector3 velocity = horizontalVelocity + Vector3.up * verticalVelocity;
            CollisionFlags flags = controller.Move(velocity * Time.deltaTime);

            // Hit a ceiling while rising? Stop rising, instead of sticking to it.
            // (Matters a lot in low mine tunnels.)
            if ((flags & CollisionFlags.Above) != 0 && verticalVelocity > 0f)
            {
                verticalVelocity = 0f;
            }
        }
    }
}
