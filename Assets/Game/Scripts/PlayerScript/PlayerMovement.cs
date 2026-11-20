using System;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>What the motor is doing. Also synced to other clients so they can pose the cartoon body.</summary>
public enum PlayerMotorState : byte
{
    Normal = 0, Diving = 1, Sliding = 2, LedgeHang = 3, LedgeClimb = 4, Swimming = 5, Downed = 6
}

/// <summary>
/// Owning-client CharacterController motor with a bouncy, Fall Guys-style feel:
/// momentum, a floaty jump arc with variable height, coyote time + jump buffering,
/// a dive / belly slide, ledge grab + climb / shimmy / kick-off, and a first-person
/// or third-person camera (toggle with V / gamepad Select).
/// Mirror's existing Client To Server NetworkTransform distributes poses.
/// It is NOT server-authoritative anti-cheat.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(CharacterController))]
[DefaultExecutionOrder(100)]
public class PlayerMovement : NetworkBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 5f; // Existing prefab field preserved.
    [Min(0f)] public float jumpHeight = 1.1f;
    [Tooltip("Must be negative. CharacterController.Move does not supply gravity.")]
    public float gravity = -24f;
    [Min(1f)] public float terminalFallSpeed = 25f;
    [UnityEngine.Range(0f, 0.3f)] public float jumpBufferSeconds = 0.12f;
    [Tooltip("Grace period to still jump after running off an edge.")]
    [UnityEngine.Range(0f, 0.3f)] public float coyoteSeconds = 0.15f;
    [UnityEngine.Range(0f, 0.3f)] public float groundSnapDistance = 0.15f;
    [Tooltip("Cap motor catch-up during a hitch. This can slow movement below 20 FPS rather than making a giant teleport.")]
    [UnityEngine.Range(0.02f, 0.1f)] public float maximumMotorDeltaTime = 0.05f;

    [Header("Fall Guys feel - momentum")]
    [Min(0f)] public float groundAcceleration = 45f;
    [Min(0f)] public float groundDeceleration = 35f;
    [Min(0f)] public float airAcceleration = 16f;
    [Tooltip("Low = you keep your momentum in the air (floaty, committed jumps).")]
    [Min(0f)] public float airDeceleration = 3f;
    [Tooltip("Extra grip when reversing direction, so turnarounds feel snappy.")]
    [Min(1f)] public float turnAroundMultiplier = 1.6f;

    [Header("Fall Guys feel - jump arc")]
    [Tooltip("Gravity multiplier while falling. >1 = snappier landings.")]
    [UnityEngine.Range(1f, 3f)] public float fallGravityMultiplier = 1.4f;
    [Tooltip("Gravity multiplier near the top of the jump. <1 = a little cartoon 'hang' at the apex.")]
    [UnityEngine.Range(0.2f, 1f)] public float apexGravityMultiplier = 0.55f;
    [Min(0f)] public float apexSpeedBand = 1.5f;
    [Tooltip("Releasing jump early while rising multiplies upward speed by this (short hops).")]
    [UnityEngine.Range(0.1f, 1f)] public float jumpReleaseCut = 0.5f;

    [Header("Fall Guys feel - dive (Left Shift / gamepad West)")]
    public bool enableDive = true;
    [Min(0f)] public float diveForwardSpeed = 8.5f;
    [Min(0f)] public float diveUpSpeed = 3.5f;
    [Tooltip("How much you can steer mid-dive (direction only, never adds speed).")]
    [Min(0f)] public float diveSteering = 3f;
    [Min(0f)] public float slideDeceleration = 11f;
    [Min(0f)] public float slideMinSeconds = 0.25f;
    [Min(0f)] public float slideMaxSeconds = 0.8f;
    [Tooltip("Brief wobbly get-up after a belly slide.")]
    [Min(0f)] public float getUpSeconds = 0.18f;

    [Header("Ledge grab")]
    public bool enableLedgeGrab = true;
    [Tooltip("Only grab while holding a direction toward the ledge, so you can still choose to fall.")]
    public bool requireInputTowardLedge = true;
    [UnityEngine.Range(0.05f, 1f)] public float ledgeWallReach = 0.35f;
    [UnityEngine.Range(0.1f, 1.2f)] public float ledgeReachAboveHands = 0.55f;
    [UnityEngine.Range(0f, 1f)] public float ledgeReachBelowHands = 0.35f;
    [UnityEngine.Range(0f, 0.5f)] public float handsBelowHeadTop = 0.1f;
    [Tooltip("Can grab near the top of a jump, not only while falling.")]
    public float ledgeGrabMaxRisingSpeed = 1.5f;
    [Min(0.05f)] public float climbSeconds = 0.38f;
    [Min(0f)] public float shimmySpeed = 1.6f;
    [Tooltip("Hold away from the wall + Jump to kick off the ledge.")]
    [Min(0f)] public float ledgeKickSpeed = 5.5f;
    [Min(0f)] public float regrabCooldown = 0.35f;
    public LayerMask ledgeLayers = ~0;

    [Header("Input")]
    [Tooltip("Assign the existing PlayerControls.inputactions asset. It is cloned at runtime; your asset/bindings are not overwritten.")]
    public InputActionAsset inputActions;
    [Tooltip("Only used when the asset has no bound Player/Jump, Player/Dive or Player/ToggleView action.")]
    public bool useJumpFallback = true;

    [Header("Look / Camera")]
    public float lookSensitivity = 0.2f; // Existing mouse sensitivity preserved.
    public float gamepadLookDegreesPerSecond = 150f;
    public Transform cameraPivot;
    public GameObject localCamera;
    public GameObject localCinemachine;

    [Header("Camera view (V / gamepad Select toggles)")]
    public bool startInThirdPerson;
    [Min(0.5f)] public float thirdPersonDistance = 4f;
    public float thirdPersonShoulder = 0.45f;
    public float thirdPersonHeight = 0.35f;
    [Min(0.01f)] public float cameraCollisionRadius = 0.2f;
    public Vector2 thirdPersonPitchLimits = new Vector2(-35f, 70f);
    [Tooltip("Third person: how fast the goblin turns to face where it is running.")]
    [Min(0f)] public float thirdPersonTurnSpeed = 720f;
    [Min(0f)] public float thirdPersonDiveTurnSpeed = 160f;
    [Tooltip("Third person gets a bit more air control, because you can see your landing.")]
    [Min(0f)] public float thirdPersonAirControl = 1.25f;
    [Tooltip("Adds the cosmetic squash/stretch/waddle body at runtime if it is not on the prefab.")]
    public bool addCartoonBody = true;

    [Header("Falling-chunk knockback")]
    [Min(1f)] public float knockbackDeceleration = 10f;
    [Header("Terrain safety")]
    public bool guardTerrainStreaming = true;
    public bool rollbackSolidPenetration = true;

    private struct LedgeInfo
    {
        public Vector3 point;       // top surface just past the edge
        public Vector3 normal;      // flat, points out of the wall toward the player
        public Vector3 hangCentre;  // controller centre while hanging
        public Vector3 standCentre; // controller centre after climbing up
    }

    private CharacterController _controller;
    private GobminePlayerHealth _health;
    private InputActionAsset _runtimeInput;
    private InputAction _move, _look, _jump, _dive, _toggleView, _descend;
    private InputAction _fallbackJump, _fallbackDive, _fallbackToggle;
    private readonly PlayerJumpBuffer _jumpBuffer = new PlayerJumpBuffer();
    private readonly RaycastHit[] _groundHits = new RaycastHit[32];
    private readonly RaycastHit[] _probeHits = new RaycastHit[16];
    private readonly Collider[] _overlaps = new Collider[8];
    private float _verticalPitch, _verticalVelocity, _configuredStepOffset;
    private bool _localStarted, _grounded, _swimUp, _swimDown, _swimming;
    private int _collisionMask;
    private Vector3 _chunkImpulseVelocity;
    private float _nextWarning;

    // Fall Guys state
    private PlayerMotorState _state = PlayerMotorState.Normal;
    private Vector3 _planarVelocity;
    private float _stateTimer, _getUpTimer, _regrabTimer;
    private bool _jumpedThisAir, _jumpCutRequested, _divePressed, _diveUsed;
    private LedgeInfo _ledge;
    private Vector3 _climbFrom;

    // Camera state
    private bool _thirdPerson;
    private float _cameraYaw, _cameraDistance;
    private Transform _cameraRig;
    private Vector3 _rigDefaultLocalPosition;
    private Quaternion _rigDefaultLocalRotation;

    [SyncVar] private byte _syncedPose;
    private byte _lastSentPose = byte.MaxValue;

    public bool IsGrounded => _grounded;
    public float VerticalVelocity => _verticalVelocity;
    public Vector3 PlanarVelocity => _planarVelocity;
    public bool IsThirdPerson => _thirdPerson;
    /// <summary>Local player: live motor state. Remote players: the last state their owner sent.</summary>
    public PlayerMotorState State => isLocalPlayer || (!isServer && !isClient) ? CurrentPose() : (PlayerMotorState)_syncedPose;
    public event Action<bool> ViewModeChanged;
    public string MovementStatus { get; private set; } = "Not local / not started";
    public int TerrainWaitFrames { get; private set; }
    public int PenetrationRollbacks { get; private set; }

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        _health = GetComponent<GobminePlayerHealth>();
        if (addCartoonBody && GetComponent<PlayerCartoonBody>() == null) gameObject.AddComponent<PlayerCartoonBody>();
    }

    public override void OnStartLocalPlayer()
    {
        base.OnStartLocalPlayer();
        if (!ValidateMotor()) { enabled = false; return; }
        _localStarted = true;
        _configuredStepOffset = _controller.stepOffset;
        _controller.minMoveDistance = 0f;
        _controller.detectCollisions = true;
        _controller.enableOverlapRecovery = true;
        _collisionMask = 0;
        for (int layer = 0; layer < 32; layer++)
            if (!Physics.GetIgnoreLayerCollision(gameObject.layer, layer)) _collisionMask |= 1 << layer;
        if (localCamera != null) localCamera.SetActive(true);
        if (localCinemachine != null) localCinemachine.SetActive(true);
        if (cameraPivot != null) _verticalPitch = Mathf.DeltaAngle(0f, cameraPivot.localEulerAngles.x);
        _cameraRig = localCinemachine != null ? localCinemachine.transform : localCamera != null ? localCamera.transform : null;
        if (_cameraRig != null) { _rigDefaultLocalPosition = _cameraRig.localPosition; _rigDefaultLocalRotation = _cameraRig.localRotation; }
        _cameraYaw = transform.eulerAngles.y;
        Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        try { CreateInput(); }
        catch (Exception ex)
        {
            Debug.LogError("[Gobmine Movement] Input setup failed: " + ex.Message, this);
            DisposeInput(); _localStarted = false; enabled = false;
            return;
        }
        if (startInThirdPerson) SetThirdPerson(true);
    }

    private bool ValidateMotor()
    {
        _controller = GetComponent<CharacterController>();
        Rigidbody body = GetComponent<Rigidbody>();
        if (_controller == null || !_controller.enabled || (body != null && !body.isKinematic))
        {
            Debug.LogError("[Gobmine Movement] A working CharacterController and no dynamic Rigidbody are required. Run Tools > Gobmine > Movement > Install Player Controller.", this);
            return false;
        }
        foreach (Collider collider in GetComponents<Collider>())
            if (collider != _controller && collider.enabled && !collider.isTrigger)
            {
                Debug.LogError("[Gobmine Movement] A second solid ROOT collider conflicts with the controller. Run the movement installer; keep only the CharacterController as the root movement shape.", this);
                return false;
            }
        if (!PlayerMotorRules.Finite(gravity) || gravity >= 0f || !PlayerMotorRules.Finite(jumpHeight) || jumpHeight < 0f ||
            !PlayerMotorRules.Finite(moveSpeed) || moveSpeed < 0f || !PlayerMotorRules.Finite(terminalFallSpeed) || terminalFallSpeed <= 0f)
        { Debug.LogError("[Gobmine Movement] Invalid speed, jump height, or gravity.", this); return false; }
        Vector3 scale = transform.lossyScale;
        if (Vector3.Distance(scale, Vector3.one) > 0.001f || Mathf.Abs(Vector3.Dot(transform.up, Vector3.up) - 1f) > 0.001f)
        { Debug.LogError("[Gobmine Movement] Player root must be upright and have world scale (1,1,1). Scale a visual child instead.", this); return false; }
        return true;
    }

    private void CreateInput()
    {
        _runtimeInput = inputActions != null ? Instantiate(inputActions) : CreateDefaultInput();
        _move = _runtimeInput.FindAction("Player/Move", true);
        _look = _runtimeInput.FindAction("Player/Look", true);
        _jump = _runtimeInput.FindAction("Player/Jump", false);
        _dive = _runtimeInput.FindAction("Player/Dive", false);
        _toggleView = _runtimeInput.FindAction("Player/ToggleView", false);
        _descend = _runtimeInput.FindAction("Player/Descend", false);
        if ((_jump == null || _jump.bindings.Count == 0) && useJumpFallback)
        {
            _fallbackJump = new InputAction("GobmineJumpFallback", InputActionType.Button, "<Keyboard>/space");
            _fallbackJump.AddBinding("<Gamepad>/buttonSouth");
            _jump = _fallbackJump;
            Debug.LogWarning("[Gobmine Movement] No bound Player/Jump action found. Using Space / gamepad south button until you bind Jump in your asset.", this);
        }
        if ((_dive == null || _dive.bindings.Count == 0) && useJumpFallback)
        {
            _fallbackDive = new InputAction("GobmineDiveFallback", InputActionType.Button, "<Keyboard>/leftShift");
            _fallbackDive.AddBinding("<Gamepad>/buttonWest");
            _dive = _fallbackDive;
        }
        if ((_toggleView == null || _toggleView.bindings.Count == 0) && useJumpFallback)
        {
            _fallbackToggle = new InputAction("GobmineToggleViewFallback", InputActionType.Button, "<Keyboard>/v");
            _fallbackToggle.AddBinding("<Gamepad>/select");
            _toggleView = _fallbackToggle;
        }
        EnableInput();
    }

    private void EnableInput()
    {
        _move?.Enable(); _look?.Enable(); _jump?.Enable(); _dive?.Enable(); _toggleView?.Enable(); _descend?.Enable();
    }

    private static InputActionAsset CreateDefaultInput()
    {
        var asset = ScriptableObject.CreateInstance<InputActionAsset>();
        var map = new InputActionMap("Player"); asset.AddActionMap(map);
        InputAction move = map.AddAction("Move", InputActionType.Value, expectedControlLayout: "Vector2");
        move.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s")
            .With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
        move.AddBinding("<Gamepad>/leftStick");
        InputAction look = map.AddAction("Look", InputActionType.Value, expectedControlLayout: "Vector2");
        look.AddBinding("<Mouse>/delta"); look.AddBinding("<Gamepad>/rightStick");
        InputAction jump = map.AddAction("Jump", InputActionType.Button, "<Keyboard>/space");
        jump.AddBinding("<Gamepad>/buttonSouth");
        InputAction dive = map.AddAction("Dive", InputActionType.Button, "<Keyboard>/leftShift");
        dive.AddBinding("<Gamepad>/buttonWest");
        InputAction toggle = map.AddAction("ToggleView", InputActionType.Button, "<Keyboard>/v");
        toggle.AddBinding("<Gamepad>/select");
        return asset;
    }

    public override void OnStopLocalPlayer()
    {
        _localStarted = false; DisposeInput();
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        base.OnStopLocalPlayer();
    }
    private void OnEnable()
    {
        if (!_localStarted) return;
        EnableInput();
    }
    private void OnDisable()
    {
        _runtimeInput?.Disable(); _fallbackJump?.Disable(); _fallbackDive?.Disable(); _fallbackToggle?.Disable();
        _jumpBuffer.Reset(); _verticalVelocity = 0f; _grounded = false; _chunkImpulseVelocity = Vector3.zero;
        _planarVelocity = Vector3.zero; _state = PlayerMotorState.Normal; _stateTimer = _getUpTimer = _regrabTimer = 0f;
        _jumpedThisAir = _jumpCutRequested = _divePressed = _diveUsed = false;
        if (_controller != null && _localStarted) _controller.stepOffset = _configuredStepOffset;
    }
    private void OnDestroy() { DisposeInput(); }
    private void DisposeInput()
    {
        if (_runtimeInput != null) { _runtimeInput.Disable(); Destroy(_runtimeInput); _runtimeInput = null; }
        if (_fallbackJump != null) { _fallbackJump.Disable(); _fallbackJump.Dispose(); _fallbackJump = null; }
        if (_fallbackDive != null) { _fallbackDive.Disable(); _fallbackDive.Dispose(); _fallbackDive = null; }
        if (_fallbackToggle != null) { _fallbackToggle.Disable(); _fallbackToggle.Dispose(); _fallbackToggle = null; }
        _move = _look = _jump = _dive = _toggleView = _descend = null;
    }

    // ------------------------------------------------------------------ frame loop

    private void Update()
    {
        if (!isLocalPlayer || !_localStarted || _controller == null || !_controller.enabled || _move == null) return;
        bool menuOpen = GobminePlayerEquipment.LocalMenuOpen;
        if (!menuOpen && _toggleView != null && _toggleView.WasPressedThisFrame()) SetThirdPerson(!_thirdPerson);
        if (!menuOpen) UpdateLook();
        UpdateMotor(menuOpen);
        UpdateBodyFacing();
        UpdateCameraRig();
        SyncPose();
    }

    private void UpdateMotor(bool menuOpen)
    {
        World world = World.Instance;
        if (world == null || !world.IsReady)
        { MovementStatus = "Waiting for world / terrain snapshot"; TerrainWaitFrames++; return; }
        if (Physics.GetIgnoreLayerCollision(gameObject.layer, world.terrainLayer) ||
            (_controller.excludeLayers.value & (1 << world.terrainLayer)) != 0)
        {
            MovementStatus = "Player/terrain collision is disabled in Physics layers or controller overrides";
            WarnThrottled(MovementStatus); return;
        }
        bool dead = _health != null && _health.IsDead;
        if (dead) { _jumpBuffer.Reset(); _divePressed = false; }
        else if (!menuOpen)
        {
            if (_jump != null && _jump.WasPressedThisFrame()) _jumpBuffer.Press(Time.time);
            if (_jump != null && _jump.WasReleasedThisFrame()) _jumpCutRequested = true;
            if (_dive != null && _dive.WasPressedThisFrame()) _divePressed = true;
        }
        _swimUp = !dead && !menuOpen && _jump != null && _jump.IsPressed();
        _swimDown = !dead && !menuOpen && ((_descend != null && _descend.IsPressed()) ||
            (Keyboard.current != null && Keyboard.current.leftCtrlKey.isPressed) || (Gamepad.current != null && Gamepad.current.rightShoulder.isPressed));
        Vector2 input = dead || menuOpen ? Vector2.zero : _move.ReadValue<Vector2>();
        Vector3 right, forward;
        MovementBasis(out right, out forward);
        Vector3 horizontal = PlayerMotorRules.HorizontalMotion(input, right, forward, moveSpeed);
        float frameDt = Mathf.Min(Time.deltaTime, Mathf.Clamp(maximumMotorDeltaTime, 0.02f, 0.1f));
        if (frameDt <= 0f) return;
        // Short swept moves bound work and help with thin voxel ledges and low FPS.
        float estimatedSpeed = Mathf.Max(Mathf.Abs(_verticalVelocity), PlayerMotorRules.JumpSpeed(jumpHeight, gravity)) +
            Mathf.Max(Mathf.Max(horizontal.magnitude, _planarVelocity.magnitude), enableDive ? diveForwardSpeed : 0f) + _chunkImpulseVelocity.magnitude;
        int steps = Mathf.Clamp(Mathf.Max(Mathf.CeilToInt(frameDt / (1f / 90f)), Mathf.CeilToInt(estimatedSpeed * frameDt / 0.15f)), 1, 16);
        float dt = frameDt / steps;
        for (int i = 0; i < steps; i++) if (!MoveStep(world, horizontal, dt, dead)) break;
    }

    private void MovementBasis(out Vector3 right, out Vector3 forward)
    {
        if (_thirdPerson)
        {
            // Third person: input is relative to the CAMERA, the body turns to follow.
            Quaternion yaw = Quaternion.Euler(0f, _cameraYaw, 0f);
            right = yaw * Vector3.right; forward = yaw * Vector3.forward;
        }
        else { right = transform.right; forward = transform.forward; }
    }

    private void UpdateLook()
    {
        Vector2 look = _look.ReadValue<Vector2>();
        bool stick = _look.activeControl != null && _look.activeControl.device is Gamepad;
        float sensitivity = stick ? gamepadLookDegreesPerSecond * Time.deltaTime : lookSensitivity;
        if (_thirdPerson) _cameraYaw = Mathf.Repeat(_cameraYaw + look.x * sensitivity, 360f);
        else transform.Rotate(Vector3.up * (look.x * sensitivity));
        Vector2 limits = PitchLimits();
        _verticalPitch = Mathf.Clamp(_verticalPitch - look.y * sensitivity, limits.x, limits.y);
    }

    private Vector2 PitchLimits() => _thirdPerson
        ? new Vector2(Mathf.Min(thirdPersonPitchLimits.x, thirdPersonPitchLimits.y), Mathf.Max(thirdPersonPitchLimits.x, thirdPersonPitchLimits.y))
        : new Vector2(-89f, 89f);

    /// <summary>Switch between first and third person. Safe to call from UI or other scripts.</summary>
    public void SetThirdPerson(bool on)
    {
        if (_thirdPerson == on) return;
        _thirdPerson = on;
        if (on) { _cameraYaw = transform.eulerAngles.y; _cameraDistance = thirdPersonDistance; }
        else
        {
            // Snap the body back under the camera so first-person aim matches what you were looking at.
            transform.rotation = Quaternion.Euler(0f, _cameraYaw, 0f);
            if (_cameraRig != null) { _cameraRig.localPosition = _rigDefaultLocalPosition; _cameraRig.localRotation = _rigDefaultLocalRotation; }
        }
        Vector2 limits = PitchLimits();
        _verticalPitch = Mathf.Clamp(_verticalPitch, limits.x, limits.y);
        UpdateCameraRig();
        ViewModeChanged?.Invoke(on);
    }

    private void UpdateBodyFacing()
    {
        if (!_thirdPerson) return;
        Vector3 face = Vector3.zero;
        float turnSpeed = thirdPersonTurnSpeed;
        if (_state == PlayerMotorState.LedgeHang || _state == PlayerMotorState.LedgeClimb) face = -_ledge.normal;
        else
        {
            if (_planarVelocity.sqrMagnitude > 0.09f) face = _planarVelocity;
            if (_state == PlayerMotorState.Diving || _state == PlayerMotorState.Sliding) turnSpeed = thirdPersonDiveTurnSpeed;
        }
        face.y = 0f;
        if (face.sqrMagnitude < 1e-4f) return;
        Quaternion target = Quaternion.LookRotation(face.normalized, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, target, turnSpeed * Time.deltaTime);
    }

    private void UpdateCameraRig()
    {
        if (cameraPivot == null) return;
        if (!_thirdPerson)
        {
            cameraPivot.localRotation = Quaternion.Euler(_verticalPitch, 0f, 0f);
            return;
        }
        Quaternion look = Quaternion.Euler(_verticalPitch, _cameraYaw, 0f);
        // Mining aims along cameraPivot.forward, so the pivot always looks where the camera looks.
        cameraPivot.rotation = look;
        if (_cameraRig == null) return;
        // Orbit around the body centre line (not the pivot, which sits in front of the face).
        Vector3 origin = transform.position + Vector3.up * (cameraPivot.localPosition.y + thirdPersonHeight);
        Vector3 desired = origin + look * new Vector3(thirdPersonShoulder, 0f, -thirdPersonDistance);
        Vector3 offset = desired - origin;
        float wanted = offset.magnitude;
        float allowed = wanted;
        if (wanted > 0.001f)
        {
            Vector3 dir = offset / wanted;
            RaycastHit hit;
            if (ClosestHit(origin, cameraCollisionRadius, dir, wanted, _collisionMask, out hit))
                allowed = Mathf.Max(0.3f, hit.distance - 0.05f);
            // Pull in instantly when blocked, ease back out so it never pops.
            _cameraDistance = allowed < _cameraDistance ? allowed : Mathf.MoveTowards(_cameraDistance, allowed, 6f * Time.deltaTime);
            _cameraRig.position = origin + dir * _cameraDistance;
        }
        _cameraRig.rotation = look;
    }

    // ------------------------------------------------------------------ motor step

    private bool MoveStep(World world, Vector3 horizontal, float dt, bool dead)
    {
        Vector3 bodyCentre = transform.TransformPoint(_controller.center);
        bool swimming = world != null && world.FluidAt(bodyCentre) == GobmineFluidKind.Water;
        GobmineFluidKind feetFluid = world != null
            ? world.FluidAt(bodyCentre - Vector3.up * (_controller.height * 0.5f - 0.08f)) : GobmineFluidKind.None;
        _swimming = swimming;
        if (swimming)
        {
            horizontal *= 0.55f; _jumpBuffer.Reset(); _divePressed = false;
            if (_state == PlayerMotorState.LedgeHang || _state == PlayerMotorState.LedgeClimb) DropFromLedge();
            if (_state != PlayerMotorState.Normal) EnterState(PlayerMotorState.Normal);
        }
        else if (feetFluid == GobmineFluidKind.Water) horizontal *= 0.7f;
        else if (feetFluid == GobmineFluidKind.Lava) horizontal *= 0.25f;

        if (_regrabTimer > 0f) _regrabTimer -= dt;
        if (_state == PlayerMotorState.LedgeHang) return HangStep(world, horizontal, dt, dead);
        if (_state == PlayerMotorState.LedgeClimb) return ClimbStep(world, dt, dead);

        bool menuOpen = GobminePlayerEquipment.LocalMenuOpen;
        bool gettingUp = _getUpTimer > 0f;
        if (gettingUp) { _getUpTimer -= dt; horizontal *= 0.35f; }
        bool wantsJump = _state == PlayerMotorState.Normal && !gettingUp && !swimming && !dead && !menuOpen &&
            _jumpBuffer.CanConsume(Time.time, jumpBufferSeconds, coyoteSeconds);
        bool startDive = false;
        if (_divePressed)
        {
            _divePressed = false;
            startDive = enableDive && _state == PlayerMotorState.Normal && !_diveUsed && !gettingUp && !swimming && !dead && !menuOpen && !wantsJump;
        }
        bool wasGrounded = _grounded && !swimming && !startDive;

        float velocity = _verticalVelocity;
        bool cutJump = false;
        if (wantsJump) velocity = PlayerMotorRules.JumpSpeed(jumpHeight, gravity);
        else if (_jumpCutRequested)
        {
            cutJump = _jumpedThisAir && velocity > 0f && _state == PlayerMotorState.Normal;
            if (cutJump) velocity *= jumpReleaseCut;
        }
        Vector3 planar = _planarVelocity;
        if (startDive)
        {
            Vector3 dir = horizontal.sqrMagnitude > 0.01f ? horizontal.normalized : FlatForward();
            planar = dir * Mathf.Max(diveForwardSpeed, Vector3.Dot(planar, dir));
            velocity = Mathf.Max(velocity, diveUpSpeed);
        }
        if (wasGrounded && !wantsJump && velocity < 0f) velocity = -2f;
        float nextVelocity = Mathf.Max(-terminalFallSpeed, velocity + gravity * GravityScale(velocity, wasGrounded) * dt);
        if (swimming)
        {
            float target = _swimUp ? 3f : _swimDown ? -3f : 0f;
            velocity = Mathf.Clamp(velocity, -4f, 4f);
            nextVelocity = Mathf.MoveTowards(velocity, target, 14f * dt);
        }
        if (!startDive) planar = UpdatePlanar(planar, horizontal, wasGrounded, swimming, dt);

        Vector3 displacement = (planar + _chunkImpulseVelocity) * dt +
            Vector3.up * ((velocity + nextVelocity) * 0.5f * dt);
        Vector3 centre = transform.TransformPoint(_controller.center);
        Bounds sweep = PlayerMotorRules.SweptBounds(centre, _controller.radius, _controller.height,
            displacement, _configuredStepOffset, groundSnapDistance, Mathf.Max(0.02f, _controller.skinWidth));
        string waitingReason;
        if (guardTerrainStreaming && world != null && !world.TryPreparePlayerCollision(sweep, out waitingReason))
        {
            MovementStatus = waitingReason; TerrainWaitFrames++;
            if (startDive) _divePressed = true; // retry the dive once collision is ready
            // No catch-up gravity and no accumulated displacement while a collider is unavailable.
            return false;
        }
        if (rollbackSolidPenetration && world != null && world.PlayerCentreInsideSolidCell(centre))
        {
            if (startDive) _divePressed = true;
            MovementStatus = "Player centre already inside solid terrain; check spawn/teleport position";
            WarnThrottled(MovementStatus); return false;
        }

        // Commit this step's decisions only once we know the move will happen.
        if (wantsJump) { _jumpBuffer.Consume(); _grounded = false; _jumpedThisAir = true; _jumpCutRequested = false; }
        else if (_jumpCutRequested) _jumpCutRequested = false;
        if (startDive)
        {
            _diveUsed = true; _jumpBuffer.Reset(); _grounded = false; _jumpedThisAir = false;
            EnterState(PlayerMotorState.Diving);
        }
        _stateTimer += dt;

        float stepOffset = wasGrounded && !wantsJump && _state != PlayerMotorState.Diving ? _configuredStepOffset : 0f;
        if (!Mathf.Approximately(_controller.stepOffset, stepOffset)) _controller.stepOffset = stepOffset;
        Vector3 previousPosition = transform.position;
        CollisionFlags flags = _controller.Move(displacement);
        _verticalVelocity = nextVelocity;
        if ((flags & CollisionFlags.Above) != 0 && _verticalVelocity > 0f) _verticalVelocity = 0f;
        if ((flags & CollisionFlags.Sides) != 0) planar = WallResponse(planar, transform.position - previousPosition, dt);
        _planarVelocity = planar;
        _grounded = !swimming && (flags & CollisionFlags.Below) != 0 && _verticalVelocity <= 0f;
        // A short probe prevents bouncing on descending voxel slopes. Never snap upward
        // while jumping, or attach a falling player to a distant floor.
        if (!swimming && !_grounded && !wantsJump && _verticalVelocity <= 0f && wasGrounded)
        {
            float drop;
            if (TryGroundDrop(out drop))
            {
                CollisionFlags snap = _controller.Move(Vector3.down * Mathf.Max(0.005f, drop + 0.02f));
                _grounded = (snap & CollisionFlags.Below) != 0;
            }
        }
        if (rollbackSolidPenetration && world != null && world.PlayerCentreInsideSolidCell(transform.TransformPoint(_controller.center)))
        {
            // Exceptional recovery only. Normal locomotion never writes position directly.
            _controller.enabled = false; transform.position = previousPosition; _controller.enabled = true;
            _verticalVelocity = 0f; _grounded = false; _jumpBuffer.Reset(); PenetrationRollbacks++;
            MovementStatus = "Rolled back a move into solid terrain"; WarnThrottled(MovementStatus); return false;
        }
        _chunkImpulseVelocity = Vector3.MoveTowards(_chunkImpulseVelocity, Vector3.zero, Mathf.Max(0.2f, knockbackDeceleration) * dt);

        if (_grounded)
        {
            _verticalVelocity = -2f; _jumpedThisAir = false; _diveUsed = false;
            if (_state == PlayerMotorState.Diving) EnterState(PlayerMotorState.Sliding);
            else if (_state == PlayerMotorState.Sliding &&
                (_stateTimer >= slideMaxSeconds || (_stateTimer >= slideMinSeconds && _planarVelocity.magnitude < 1.5f)))
            {
                EnterState(PlayerMotorState.Normal); _getUpTimer = getUpSeconds;
            }
            if (!dead && _state == PlayerMotorState.Normal) _jumpBuffer.Grounded(Time.time);
        }
        else
        {
            if (_state == PlayerMotorState.Sliding) EnterState(PlayerMotorState.Diving); // slid off an edge
            if (!swimming && !dead && !menuOpen && CanTryLedgeGrab() && TryGrabLedge(world, horizontal)) return true;
        }
        MovementStatus = dead ? "Downed (gravity only)" : swimming ? "Swimming" :
            _state == PlayerMotorState.Diving ? "Diving" : _state == PlayerMotorState.Sliding ? "Belly slide" :
            _grounded ? "Grounded" : _verticalVelocity > 0f ? "Jumping" : "Falling";
        return true;
    }

    private float GravityScale(float velocity, bool grounded)
    {
        if (grounded) return 1f;
        if (Mathf.Abs(velocity) < apexSpeedBand) return apexGravityMultiplier;
        return velocity < 0f ? fallGravityMultiplier : 1f;
    }

    private Vector3 UpdatePlanar(Vector3 planar, Vector3 desired, bool grounded, bool swimming, float dt)
    {
        desired.y = 0f; planar.y = 0f;
        bool hasInput = desired.sqrMagnitude > 0.0001f;
        if (_state == PlayerMotorState.Diving || _state == PlayerMotorState.Sliding)
        {
            float speed = planar.magnitude;
            if (_state == PlayerMotorState.Sliding) speed = Mathf.MoveTowards(speed, 0f, slideDeceleration * dt);
            else speed = Mathf.MoveTowards(speed, 0f, 0.5f * dt); // tiny air drag
            Vector3 steered = planar + (hasInput ? desired.normalized * (diveSteering * dt * Mathf.Max(1f, speed)) : Vector3.zero);
            return steered.sqrMagnitude > 1e-6f ? steered.normalized * speed : Vector3.zero;
        }
        float accel;
        if (swimming || grounded) accel = hasInput ? groundAcceleration : groundDeceleration;
        else accel = (hasInput ? airAcceleration : airDeceleration) * (_thirdPerson ? thirdPersonAirControl : 1f);
        if (hasInput && Vector3.Dot(desired, planar) < 0f) accel *= turnAroundMultiplier;
        return Vector3.MoveTowards(planar, desired, accel * dt);
    }

    /// <summary>Kill momentum that pushes into a wall (keep the sliding part). Dives into walls bonk.</summary>
    private Vector3 WallResponse(Vector3 planar, Vector3 moved, float dt)
    {
        moved.y = 0f;
        Vector3 impulse = _chunkImpulseVelocity; impulse.y = 0f;
        Vector3 actual = moved / Mathf.Max(1e-5f, dt) - impulse;
        if (actual.sqrMagnitude >= planar.sqrMagnitude) return planar;
        if (_state == PlayerMotorState.Diving && planar.magnitude > 4f && actual.magnitude < planar.magnitude * 0.35f)
            return -planar.normalized * 2f; // cartoon bonk
        return Vector3.ClampMagnitude(actual, planar.magnitude);
    }

    private Vector3 FlatForward()
    {
        Vector3 f = transform.forward; f.y = 0f;
        return f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
    }

    private void EnterState(PlayerMotorState state) { _state = state; _stateTimer = 0f; }

    // ------------------------------------------------------------------ ledges

    private bool CanTryLedgeGrab() =>
        enableLedgeGrab && _regrabTimer <= 0f && _verticalVelocity <= ledgeGrabMaxRisingSpeed &&
        (_state == PlayerMotorState.Normal || _state == PlayerMotorState.Diving);

    private bool TryGrabLedge(World world, Vector3 desired)
    {
        desired.y = 0f;
        Vector3 probeDir;
        if (desired.sqrMagnitude > 0.01f) probeDir = desired.normalized;
        else if (requireInputTowardLedge) return false;
        else if (_planarVelocity.sqrMagnitude > 0.25f) probeDir = new Vector3(_planarVelocity.x, 0f, _planarVelocity.z).normalized;
        else probeDir = FlatForward();

        Vector3 centre = transform.TransformPoint(_controller.center);
        if (!LedgeCollisionReady(world, centre)) return false;
        LedgeInfo ledge;
        if (!TryFindLedge(centre, probeDir, true, out ledge)) return false;
        Vector3 delta = ledge.hangCentre - centre;
        if (delta.sqrMagnitude > 1.2f * 1.2f) return false;
        _controller.stepOffset = 0f;
        _controller.Move(delta); // collision-respecting, never a teleport
        Vector3 after = transform.TransformPoint(_controller.center);
        if ((after - ledge.hangCentre).sqrMagnitude > 0.2f * 0.2f) return false; // couldn't fit; keep falling

        _ledge = ledge;
        _planarVelocity = Vector3.zero; _verticalVelocity = 0f; _grounded = false;
        _jumpBuffer.Reset(); _jumpCutRequested = false; _divePressed = false; _diveUsed = false; _jumpedThisAir = false;
        EnterState(PlayerMotorState.LedgeHang);
        MovementStatus = "Grabbed ledge";
        return true;
    }

    private bool HangStep(World world, Vector3 desired, float dt, bool dead)
    {
        _stateTimer += dt;
        _verticalVelocity = 0f; _planarVelocity = Vector3.zero; _grounded = false;
        if (dead || _chunkImpulseVelocity.sqrMagnitude > 1f) { DropFromLedge(); return true; }
        Vector3 centre = transform.TransformPoint(_controller.center);
        if (!LedgeCollisionReady(world, centre)) { MovementStatus = "Waiting for ledge collision"; TerrainWaitFrames++; return false; }
        // The ledge can be mined away or collapse under your hands.
        LedgeInfo fresh;
        if (!TryFindLedge(centre, -_ledge.normal, false, out fresh)) { DropFromLedge(); return true; }
        _ledge = fresh;
        MovementStatus = "Hanging on ledge";
        if (_stateTimer < 0.1f) return true; // let the grab register before reading inputs

        desired.y = 0f;
        if (_swimDown || _divePressed) { _divePressed = false; DropFromLedge(); return true; }
        if (_jumpBuffer.PressedWithin(Time.time, jumpBufferSeconds))
        {
            _jumpBuffer.ClearPress();
            bool pullingAway = desired.sqrMagnitude > 0.01f && Vector3.Dot(desired.normalized, _ledge.normal) > 0.5f;
            if (pullingAway) LedgeKick(); else StartClimb();
            return true;
        }

        Vector3 tangent = Vector3.Cross(Vector3.up, _ledge.normal);
        float side = moveSpeed > 0.01f ? Vector3.Dot(desired, tangent) / moveSpeed : 0f;
        if (Mathf.Abs(side) > 0.2f && shimmySpeed > 0f)
        {
            Vector3 before = transform.position;
            _controller.stepOffset = 0f;
            _controller.Move(tangent * (Mathf.Clamp(side, -1f, 1f) * shimmySpeed * dt));
            Vector3 moved = transform.TransformPoint(_controller.center);
            LedgeInfo next;
            if (TryFindLedge(moved, -_ledge.normal, false, out next))
            {
                _ledge = next;
                Vector3 fix = next.hangCentre - moved;
                if (fix.sqrMagnitude < 0.25f * 0.25f) _controller.Move(fix);
                MovementStatus = "Shimmying";
            }
            else _controller.Move(before - transform.position); // end of the ledge: stay put
        }
        else
        {
            Vector3 fix = _ledge.hangCentre - centre;
            if (fix.sqrMagnitude > 0.0004f && fix.sqrMagnitude < 0.0625f) _controller.Move(fix);
        }
        return true;
    }

    private void StartClimb()
    {
        Vector3 centre = transform.TransformPoint(_controller.center);
        LedgeInfo fresh;
        if (!TryFindLedge(centre, -_ledge.normal, true, out fresh)) { MovementStatus = "No room to climb up here"; return; }
        _ledge = fresh; _climbFrom = centre;
        EnterState(PlayerMotorState.LedgeClimb);
    }

    private bool ClimbStep(World world, float dt, bool dead)
    {
        if (dead) { DropFromLedge(); return true; }
        Vector3 centre = transform.TransformPoint(_controller.center);
        if (!LedgeCollisionReady(world, centre)) { MovementStatus = "Waiting for ledge collision"; TerrainWaitFrames++; return false; }
        _stateTimer += dt;
        float t = Mathf.Clamp01(_stateTimer / Mathf.Max(0.05f, climbSeconds));
        Vector3 target = ClimbPath(Mathf.SmoothStep(0f, 1f, t));
        _controller.stepOffset = 0f;
        _controller.Move(target - centre);
        _verticalVelocity = 0f; _planarVelocity = Vector3.zero; _grounded = false;
        Vector3 after = transform.TransformPoint(_controller.center);
        if ((after - target).sqrMagnitude > 0.3f * 0.3f)
        {
            // Something is in the way (another goblin, falling rock): give up and fall.
            EnterState(PlayerMotorState.Normal); _regrabTimer = regrabCooldown;
            MovementStatus = "Climb blocked"; return true;
        }
        MovementStatus = "Climbing";
        if (t >= 1f)
        {
            EnterState(PlayerMotorState.Normal);
            CollisionFlags land = _controller.Move(Vector3.down * 0.12f);
            _grounded = (land & CollisionFlags.Below) != 0;
            _verticalVelocity = _grounded ? -2f : 0f;
            if (_grounded) _jumpBuffer.Grounded(Time.time);
        }
        return true;
    }

    /// <summary>Up first (hands pull you over the lip), then forward onto the top.</summary>
    private Vector3 ClimbPath(float t)
    {
        Vector3 lifted = new Vector3(_climbFrom.x, _ledge.standCentre.y, _climbFrom.z);
        const float upShare = 0.6f;
        if (t <= upShare) return Vector3.Lerp(_climbFrom, lifted, t / upShare);
        return Vector3.Lerp(lifted, _ledge.standCentre, (t - upShare) / (1f - upShare));
    }

    private void DropFromLedge()
    {
        EnterState(PlayerMotorState.Normal);
        _regrabTimer = regrabCooldown; _verticalVelocity = 0f; _grounded = false;
        _planarVelocity = _ledge.normal * 0.8f;
        MovementStatus = "Let go of ledge";
    }

    private void LedgeKick()
    {
        EnterState(PlayerMotorState.Normal);
        _regrabTimer = regrabCooldown; _grounded = false; _jumpedThisAir = true;
        _verticalVelocity = PlayerMotorRules.JumpSpeed(jumpHeight, gravity) * 0.85f;
        _planarVelocity = _ledge.normal * ledgeKickSpeed;
        MovementStatus = "Kicked off ledge";
    }

    private bool LedgeCollisionReady(World world, Vector3 centre)
    {
        if (world == null || !guardTerrainStreaming) return true;
        float reach = _controller.radius + ledgeWallReach + 0.6f;
        Vector3 size = new Vector3(reach * 2f, _controller.height + 2f * (ledgeReachAboveHands + 0.5f), reach * 2f);
        string reason;
        return world.TryPreparePlayerCollision(new Bounds(centre + Vector3.up * 0.5f, size), out reason);
    }

    /// <summary>
    /// Finds a grabbable lip in front of the body: a steep wall at hand height, a walkable top within
    /// reach of the hands, and (when <paramref name="requireRoom"/>) space to hang and to stand on top.
    /// </summary>
    private bool TryFindLedge(Vector3 centre, Vector3 probeDir, bool requireRoom, out LedgeInfo info)
    {
        info = default;
        probeDir.y = 0f;
        if (probeDir.sqrMagnitude < 1e-4f) return false;
        probeDir.Normalize();
        int mask = _collisionMask & ledgeLayers.value;
        float r = _controller.radius, h = _controller.height, skin = Mathf.Max(0.01f, _controller.skinWidth);
        float hands = centre.y + h * 0.5f - handsBelowHeadTop;

        // 1. A wall in front, just under hand height.
        Vector3 wallOrigin = new Vector3(centre.x, hands - 0.15f, centre.z);
        RaycastHit wall;
        if (!ClosestHit(wallOrigin, 0.1f, probeDir, r + ledgeWallReach, mask, out wall)) return false;
        if (Mathf.Abs(wall.normal.y) > 0.65f) return false;
        Vector3 n = wall.normal; n.y = 0f;
        if (n.sqrMagnitude < 1e-4f) return false;
        n.Normalize();
        if (Vector3.Dot(n, probeDir) > -0.35f) return false; // must roughly face us
        Vector3 into = -n;

        // 2. A walkable top just past the edge, within reach of the hands.
        Vector3 downOrigin = wall.point + into * 0.15f;
        downOrigin.y = hands + ledgeReachAboveHands;
        if (Blocked(downOrigin, downOrigin, 0.05f, mask)) return false; // wall keeps going up: no lip in reach
        RaycastHit top;
        if (!ClosestHit(downOrigin, 0.05f, Vector3.down, ledgeReachAboveHands + ledgeReachBelowHands + 0.15f, mask, out top)) return false;
        if (top.normal.y < Mathf.Cos(_controller.slopeLimit * Mathf.Deg2Rad)) return false;
        float ledgeY = top.point.y;
        if (ledgeY < hands - ledgeReachBelowHands || ledgeY > hands + ledgeReachAboveHands) return false;

        // 3. The exact wall face just below the lip, so the hang spot hugs the wall.
        Vector3 facePoint = wall.point;
        RaycastHit face;
        Vector3 faceOrigin = new Vector3(centre.x, ledgeY - 0.08f, centre.z);
        if (ClosestHit(faceOrigin, 0.02f, into, r + ledgeWallReach + 0.3f, mask, out face) && Mathf.Abs(face.normal.y) < 0.7f)
            facePoint = face.point;

        Vector3 hang = facePoint + n * (r + skin + 0.02f);
        hang.y = ledgeY - (h * 0.5f - handsBelowHeadTop);
        Vector3 stand = new Vector3(top.point.x, ledgeY + h * 0.5f + skin + 0.03f, top.point.z) + into * (r * 0.6f);
        if (requireRoom)
        {
            Vector3 lifted = new Vector3(hang.x, stand.y, hang.z);
            if (CapsuleBlocked(hang, mask) || CapsuleBlocked(lifted, mask) || CapsuleBlocked(stand, mask)) return false;
        }
        info.point = top.point; info.normal = n; info.hangCentre = hang; info.standCentre = stand;
        return true;
    }

    private bool CapsuleBlocked(Vector3 centre, int mask)
    {
        float r = Mathf.Max(0.05f, _controller.radius - 0.03f);
        float half = Mathf.Max(0f, _controller.height * 0.5f - _controller.radius);
        return Blocked(centre - Vector3.up * half, centre + Vector3.up * half, r, mask);
    }

    private bool Blocked(Vector3 a, Vector3 b, float radius, int mask)
    {
        PhysicsScene physics = gameObject.scene.GetPhysicsScene();
        int count = physics.OverlapCapsule(a, b, radius, _overlaps, mask, QueryTriggerInteraction.Ignore);
        if (count >= _overlaps.Length) return true; // truncated: assume blocked
        for (int i = 0; i < count; i++)
            if (_overlaps[i] != null && !_overlaps[i].transform.IsChildOf(transform)) return true;
        return false;
    }

    private bool ClosestHit(Vector3 origin, float radius, Vector3 direction, float distance, int mask, out RaycastHit best)
    {
        best = default;
        PhysicsScene physics = gameObject.scene.GetPhysicsScene();
        int count = physics.SphereCast(origin, radius, direction, _probeHits, distance, mask, QueryTriggerInteraction.Ignore);
        float bestDistance = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = _probeHits[i];
            if (hit.collider == null || hit.collider.transform.IsChildOf(transform)) continue;
            if (hit.distance <= 0f && hit.point == Vector3.zero) continue; // started overlapping: no usable normal
            if (hit.distance < bestDistance) { bestDistance = hit.distance; best = hit; }
        }
        return bestDistance < float.PositiveInfinity;
    }

    // ------------------------------------------------------------------ helpers

    private bool TryGroundDrop(out float drop)
    {
        drop = 0f;
        if (groundSnapDistance <= 0f) return false;
        float radius = Mathf.Max(0.01f, _controller.radius - _controller.skinWidth);
        float lift = 0.05f;
        Vector3 origin = transform.TransformPoint(_controller.center) -
            Vector3.up * Mathf.Max(0f, _controller.height * 0.5f - _controller.radius) + Vector3.up * lift;
        float offset = lift + _controller.radius - radius;
        PhysicsScene physics = gameObject.scene.GetPhysicsScene();
        int count = physics.SphereCast(origin, radius, Vector3.down, _groundHits,
            groundSnapDistance + offset, _collisionMask, QueryTriggerInteraction.Ignore);
        if (count == _groundHits.Length) return false; // A truncated query is not proof of support.
        float best = float.PositiveInfinity;
        float normalLimit = Mathf.Cos(_controller.slopeLimit * Mathf.Deg2Rad);
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = _groundHits[i];
            if (hit.collider == null || hit.collider.transform.IsChildOf(transform) || hit.normal.y < normalLimit) continue;
            float distance = Mathf.Max(0f, hit.distance - offset);
            if (distance < best) best = distance;
        }
        if (best > groundSnapDistance) return false;
        drop = best; return true;
    }

    private PlayerMotorState CurrentPose()
    {
        if (_health != null && _health.IsDead) return PlayerMotorState.Downed;
        if (_swimming) return PlayerMotorState.Swimming;
        return _state;
    }

    private void SyncPose()
    {
        byte pose = (byte)CurrentPose();
        if (pose == _lastSentPose) return;
        if (isServer) { _syncedPose = pose; _lastSentPose = pose; }
        else if (isOwned && NetworkClient.ready) { CmdSetPose(pose); _lastSentPose = pose; }
    }

    [Command]
    private void CmdSetPose(byte pose)
    {
        if (pose <= (byte)PlayerMotorState.Downed) _syncedPose = pose;
    }

    [TargetRpc]
    public void TargetReceiveChunkKnockback(NetworkConnectionToClient owner, Vector3 worldVelocity)
    {
        if (!isLocalPlayer || !PlayerMotorRules.Finite(worldVelocity)) return;
        _chunkImpulseVelocity = Vector3.ClampMagnitude(_chunkImpulseVelocity + new Vector3(worldVelocity.x, 0f, worldVelocity.z), 18f);
        if (worldVelocity.y > 0f) _verticalVelocity = Mathf.Max(_verticalVelocity, Mathf.Min(worldVelocity.y, 8f));
    }

    private void WarnThrottled(string message)
    {
        if (Time.unscaledTime < _nextWarning) return;
        _nextWarning = Time.unscaledTime + 3f;
        Debug.LogWarning("[Gobmine Movement] " + message + " at " + transform.position, this);
    }
}
