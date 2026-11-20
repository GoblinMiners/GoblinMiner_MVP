using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Purely cosmetic Fall Guys-style body: squash on landing, stretch on jumps, lean into
/// acceleration, a waddle while running, dive/hang poses and wobbly googly eyes.
/// Runs on EVERY client: remote goblins animate from their synced transform and pose, so
/// nothing extra is sent over the network. Never touches the CharacterController.
/// Added automatically by PlayerMovement if it is not already on the prefab.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(200)]
public class PlayerCartoonBody : MonoBehaviour
{
    [Header("Body (auto-built from the root capsule if left empty)")]
    [Tooltip("Optional: your own model, as a CHILD of the player. Its pivot should be at the feet.")]
    public Transform visualRoot;
    public bool googlyEyes = true;
    public Color eyeWhite = Color.white;
    public Color pupilColor = new Color(0.05f, 0.05f, 0.05f);

    [Header("Squash & stretch")]
    public float springStiffness = 220f;
    public float springDamping = 13f;
    [Tooltip("Squash per m/s of landing speed.")]
    public float landSquashPerMps = 0.035f;
    [UnityEngine.Range(0f, 0.6f)] public float maxSquash = 0.45f;
    [UnityEngine.Range(0f, 0.6f)] public float jumpStretch = 0.28f;
    [Tooltip("Stretch a little while rising, squash a little while falling.")]
    public float airStretchPerMps = 0.02f;

    [Header("Lean & waddle")]
    public float leanPerMps2 = 1.1f;
    public float maxLeanDegrees = 16f;
    public float waddleDegrees = 7f;
    public float waddleStepsPerMetre = 1.4f;
    public float bobHeight = 0.07f;

    [Header("Poses")]
    public float diveTiltDegrees = 80f;
    public float swimTiltDegrees = 55f;
    public float downedTiltDegrees = 90f;

    private PlayerMovement _motor;
    private Transform _feet, _centre;
    private readonly List<Renderer> _bodyRenderers = new List<Renderer>();
    private readonly List<Renderer> _eyeRenderers = new List<Renderer>();
    private readonly List<Material> _ownedMaterials = new List<Material>();
    private Transform[] _pupils;
    private Vector3[] _pupilRest;
    private bool _hidden, _hiddenInitialised;

    private Vector3 _lastPosition, _velocity, _lean, _pupilOffset, _pupilVelocity;
    private float _stretch = 1f, _stretchVelocity, _waddlePhase, _waddleAmount, _tilt;
    private bool _primed;

    private void Start()
    {
        _motor = GetComponent<PlayerMovement>();
        if (visualRoot != null && visualRoot != transform && visualRoot.IsChildOf(transform)) AdoptVisual(visualRoot);
        else BuildFromRootRenderer();
        if (_feet == null) { enabled = false; return; }
        _lastPosition = transform.position;
    }

    private void AdoptVisual(Transform visual)
    {
        _feet = new GameObject("CartoonFeet").transform;
        _feet.SetParent(transform, false);
        _feet.localPosition = transform.InverseTransformPoint(visual.position);
        _centre = new GameObject("CartoonCentre").transform;
        _centre.SetParent(_feet, false);
        _centre.localPosition = Vector3.up * BodyHeight() * 0.5f;
        visual.SetParent(_centre, true);
        _bodyRenderers.AddRange(visual.GetComponentsInChildren<Renderer>(true));
    }

    private void BuildFromRootRenderer()
    {
        MeshFilter rootFilter = GetComponent<MeshFilter>();
        MeshRenderer rootRenderer = GetComponent<MeshRenderer>();
        if (rootFilter == null || rootRenderer == null || rootFilter.sharedMesh == null) return;
        float height = BodyHeight();
        CharacterController controller = GetComponent<CharacterController>();
        Vector3 feet = (controller != null ? controller.center : Vector3.zero) - Vector3.up * (height * 0.5f);

        // Feet pivot (squash, lean, waddle) -> centre pivot (dive/hang tilt) -> mesh (original placement).
        _feet = new GameObject("CartoonFeet").transform;
        _feet.SetParent(transform, false);
        _feet.localPosition = feet;
        _centre = new GameObject("CartoonCentre").transform;
        _centre.SetParent(_feet, false);
        _centre.localPosition = Vector3.up * (height * 0.5f);
        Transform body = new GameObject("CartoonBody").transform;
        body.SetParent(_centre, false);
        body.localPosition = -(feet + Vector3.up * (height * 0.5f)); // keeps the mesh exactly where it was

        body.gameObject.AddComponent<MeshFilter>().sharedMesh = rootFilter.sharedMesh;
        MeshRenderer bodyRenderer = body.gameObject.AddComponent<MeshRenderer>();
        bodyRenderer.sharedMaterials = rootRenderer.sharedMaterials;
        bodyRenderer.shadowCastingMode = rootRenderer.shadowCastingMode;
        bodyRenderer.receiveShadows = rootRenderer.receiveShadows;
        rootRenderer.enabled = false;
        _bodyRenderers.Add(bodyRenderer);

        if (googlyEyes) BuildEyes(body, rootFilter.sharedMesh.bounds, rootRenderer.sharedMaterial);
    }

    private float BodyHeight()
    {
        CharacterController controller = GetComponent<CharacterController>();
        return controller != null ? Mathf.Max(0.2f, controller.height) : 2f;
    }

    private void BuildEyes(Transform body, Bounds bounds, Material baseMaterial)
    {
        GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Mesh sphere = temp.GetComponent<MeshFilter>().sharedMesh;
        DestroyImmediate(temp); // gone before physics ever sees its collider
        if (sphere == null || baseMaterial == null) return;
        Material white = TintedCopy(baseMaterial, eyeWhite);
        Material black = TintedCopy(baseMaterial, pupilColor);

        float radius = Mathf.Min(bounds.extents.x, bounds.extents.z);
        float eyeSize = radius * 0.5f;
        float y = bounds.center.y + bounds.extents.y * 0.45f;
        float z = bounds.center.z + bounds.extents.z * 0.82f;
        _pupils = new Transform[2];
        _pupilRest = new Vector3[2];
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            Transform eye = MakeSphere("GooglyEye", body, sphere, white);
            eye.localPosition = new Vector3(bounds.center.x + side * radius * 0.36f, y, z);
            eye.localScale = Vector3.one * eyeSize;
            Transform pupil = MakeSphere("Pupil", eye, sphere, black);
            pupil.localScale = Vector3.one * 0.48f;
            _pupilRest[i] = new Vector3(0f, 0f, 0.32f);
            pupil.localPosition = _pupilRest[i];
            _pupils[i] = pupil;
        }
    }

    private Transform MakeSphere(string name, Transform parent, Mesh mesh, Material material)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        _eyeRenderers.Add(renderer);
        return go.transform;
    }

    private Material TintedCopy(Material source, Color color)
    {
        var material = new Material(source) { name = source.name + " (cartoon eye)" };
        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", null);
        if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", null);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.8f);
        _ownedMaterials.Add(material);
        return material;
    }

    private void OnDestroy()
    {
        foreach (Material material in _ownedMaterials) if (material != null) Destroy(material);
        _ownedMaterials.Clear();
    }

    private void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (_feet == null || dt <= 1e-5f) return;
        UpdateVisibility();

        Vector3 position = transform.position;
        Vector3 raw = (position - _lastPosition) / dt;
        _lastPosition = position;
        if (!_primed || raw.sqrMagnitude > 40f * 40f) { raw = _primed ? _velocity : Vector3.zero; _primed = true; } // teleport guard
        Vector3 previous = _velocity;
        _velocity = Vector3.Lerp(_velocity, raw, 1f - Mathf.Exp(-20f * dt)); // smooths network interpolation jitter
        Vector3 accel = (_velocity - previous) / dt;

        PlayerMotorState pose = _motor != null ? _motor.State : PlayerMotorState.Normal;
        bool onGround = _motor != null && _motor.isLocalPlayer ? _motor.IsGrounded : Mathf.Abs(_velocity.y) < 0.5f;
        bool normalPose = pose == PlayerMotorState.Normal;

        // Pops: squash on landing, stretch on take-off.
        if (previous.y < -3f && _velocity.y > -1f) _stretch -= Mathf.Min(maxSquash, -previous.y * landSquashPerMps);
        if (previous.y < 1.5f && _velocity.y > 3f) _stretch += jumpStretch;

        float target = 1f;
        if (pose == PlayerMotorState.LedgeHang) target = 1.12f;
        else if (!onGround && normalPose) target += Mathf.Clamp(_velocity.y * airStretchPerMps, -0.08f, 0.14f);
        float force = (target - _stretch) * springStiffness - _stretchVelocity * springDamping;
        _stretchVelocity += force * dt;
        _stretch = Mathf.Clamp(_stretch + _stretchVelocity * dt, 0.5f, 1.6f);

        // Lean into acceleration (in body space).
        Vector3 flatAccel = new Vector3(accel.x, 0f, accel.z);
        Vector3 localAccel = transform.InverseTransformDirection(flatAccel);
        Vector3 leanTarget = normalPose ? Vector3.ClampMagnitude(new Vector3(localAccel.z, 0f, -localAccel.x) * leanPerMps2, maxLeanDegrees) : Vector3.zero;
        _lean = Vector3.Lerp(_lean, leanTarget, 1f - Mathf.Exp(-10f * dt));

        // Waddle while running on the ground.
        float speed = new Vector2(_velocity.x, _velocity.z).magnitude;
        float waddleTarget = onGround && normalPose ? Mathf.Clamp01(speed / 4f) : 0f;
        _waddleAmount = Mathf.MoveTowards(_waddleAmount, waddleTarget, 6f * dt);
        _waddlePhase = Mathf.Repeat(_waddlePhase + speed * waddleStepsPerMetre * Mathf.PI * dt, Mathf.PI * 2f);
        float roll = Mathf.Sin(_waddlePhase) * waddleDegrees * _waddleAmount;
        float bob = Mathf.Abs(Mathf.Sin(_waddlePhase)) * bobHeight * _waddleAmount;

        // Whole-body poses.
        float tiltTarget = 0f;
        if (pose == PlayerMotorState.Diving || pose == PlayerMotorState.Sliding) tiltTarget = diveTiltDegrees;
        else if (pose == PlayerMotorState.Swimming) tiltTarget = swimTiltDegrees;
        else if (pose == PlayerMotorState.Downed) tiltTarget = downedTiltDegrees;
        else if (pose == PlayerMotorState.LedgeHang) tiltTarget = -8f;
        else if (pose == PlayerMotorState.LedgeClimb) tiltTarget = 25f;
        _tilt = Mathf.Lerp(_tilt, tiltTarget, 1f - Mathf.Exp(-14f * dt));

        float inv = 1f / Mathf.Sqrt(_stretch); // keep the volume roughly constant
        _feet.localScale = new Vector3(inv, _stretch, inv);
        _feet.localRotation = Quaternion.Euler(_lean.x, 0f, _lean.z + roll);
        _centre.localPosition = new Vector3(0f, BodyHeight() * 0.5f + bob / Mathf.Max(0.5f, _stretch), 0f);
        _centre.localRotation = Quaternion.Euler(_tilt, 0f, 0f);

        UpdatePupils(localAccel, dt);
    }

    private void UpdatePupils(Vector3 localAccel, float dt)
    {
        if (_pupils == null) return;
        // Pupils lag behind motion on a loose spring, so they jiggle after every hop and stop.
        Vector3 targetOffset = Vector3.ClampMagnitude(new Vector3(-localAccel.x, -localAccel.y - _velocity.y * 0.5f, 0f) * 0.004f, 0.16f);
        Vector3 force = (targetOffset - _pupilOffset) * 140f - _pupilVelocity * 7f;
        _pupilVelocity += force * dt;
        _pupilOffset = Vector3.ClampMagnitude(_pupilOffset + _pupilVelocity * dt, 0.18f);
        for (int i = 0; i < _pupils.Length; i++)
            if (_pupils[i] != null) _pupils[i].localPosition = _pupilRest[i] + _pupilOffset;
    }

    private void UpdateVisibility()
    {
        // In first person the local goblin only casts a shadow (no eyeballs in your face).
        bool hide = _motor != null && _motor.isLocalPlayer && !_motor.IsThirdPerson;
        if (_hiddenInitialised && hide == _hidden) return;
        _hiddenInitialised = true; _hidden = hide;
        foreach (Renderer renderer in _bodyRenderers)
            if (renderer != null) renderer.shadowCastingMode = hide ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
        foreach (Renderer renderer in _eyeRenderers)
            if (renderer != null) renderer.enabled = !hide;
    }
}
