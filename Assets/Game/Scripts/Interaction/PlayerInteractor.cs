using Mirror;
using UnityEngine;

/// <summary>
/// Press E to use something close to you (a chest, the shop). An "[E] ..." pop-up shows
/// above whatever E would use right now.
///
/// Target rule (same on the local player and the server): what you look straight at within
/// reach; otherwise the nearest interactable within reach that is roughly in front of you
/// and not behind a wall. The local player sends only its look direction; the server picks
/// the target itself from this player's own view point, so a client cannot open things that
/// are far away.
/// </summary>
public class PlayerInteractor : NetworkBehaviour
{
	[SerializeField] private Transform viewPoint;
	[SerializeField] private float reach = 3f;

	[Tooltip("How far to the side something can be and still count as in front of you (degrees from where you look).")]
	[SerializeField, Range(10f, 90f)] private float frontAngle = 60f;

	private static readonly Collider[] nearby = new Collider[32];

	private float nextServerInteract;
	private Component promptTarget;
	private string promptText;
	private GUIStyle promptStyle;

	private void Update()
	{
		if (!isLocalPlayer) return;

		promptTarget = null;
		if (GobminePlayerEquipment.LocalMenuOpen || IsDead()) return;

		IInteractable target = FindTarget(ViewOrigin(), ViewDirection());
		if (target is Component component)
		{
			promptTarget = component;
			promptText = target is IInteractPrompt prompt && !string.IsNullOrEmpty(prompt.PromptText)
				? prompt.PromptText
				: "Interact";
		}

		if (target != null && Input.GetKeyDown(KeyCode.E))
			TryInteract();
	}

	public void TryInteract()
	{
		if (!isLocalPlayer) return;
		CmdInteract(ViewDirection());
	}

	[Command]
	private void CmdInteract(Vector3 direction)
	{
		if (Time.unscaledTime < nextServerInteract) return;
		nextServerInteract = Time.unscaledTime + 0.2f;

		if (float.IsNaN(direction.x) || float.IsNaN(direction.y) || float.IsNaN(direction.z) ||
			direction.sqrMagnitude < 0.25f || direction.sqrMagnitude > 4f) return;

		if (IsDead()) return;

		Vector3 origin = ViewOrigin();
		if (Vector3.Distance(origin, transform.position) > 3f) return;   // View point must be on this player.

		IInteractable target = FindTarget(origin, direction.normalized);
		if (target != null)
			target.Interact(gameObject);
	}

	private Vector3 ViewOrigin() =>
		viewPoint != null ? viewPoint.position : transform.position + Vector3.up * 1.6f;

	private Vector3 ViewDirection() =>
		viewPoint != null ? viewPoint.forward : transform.forward;

	private bool IsDead()
	{
		GobminePlayerHealth health = GetComponent<GobminePlayerHealth>();
		return health != null && health.IsDead;
	}

	private IInteractable FindTarget(Vector3 origin, Vector3 direction)
	{
		// 1. Whatever you are looking straight at.
		if (Physics.Raycast(origin, direction, out RaycastHit hit, reach,
				Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
		{
			IInteractable looked = hit.collider.GetComponentInParent<IInteractable>();
			if (looked != null && !IsSelf(looked)) return looked;
		}

		// 2. Otherwise the nearest one within reach that is in front of you and in plain view.
		int count = Physics.OverlapSphereNonAlloc(origin, reach, nearby,
			Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
		float minDot = Mathf.Cos(frontAngle * Mathf.Deg2Rad);
		IInteractable best = null;
		float bestDistance = float.MaxValue;

		for (int i = 0; i < count; i++)
		{
			Collider collider = nearby[i];
			IInteractable candidate = collider.GetComponentInParent<IInteractable>();
			if (candidate == null || IsSelf(candidate)) continue;

			Vector3 closest = collider.ClosestPoint(origin);
			Vector3 toTarget = closest - origin;
			float distance = toTarget.magnitude;
			if (distance > reach || distance >= bestDistance) continue;

			// Use the collider centre for the angle, so standing right against it still counts.
			Vector3 toCentre = collider.bounds.center - origin;
			if (toCentre.sqrMagnitude > 0.0001f && Vector3.Dot(toCentre.normalized, direction) < minDot) continue;

			if (distance > 0.05f && Physics.Raycast(origin, toTarget / distance, out RaycastHit block, distance - 0.02f,
					Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) &&
				block.collider.GetComponentInParent<IInteractable>() != candidate &&
				!block.collider.transform.IsChildOf(transform))
				continue;   // Something is in the way.

			best = candidate;
			bestDistance = distance;
		}

		return best;
	}

	private bool IsSelf(IInteractable candidate) =>
		candidate is Component c && c != null && c.transform.IsChildOf(transform);

	private void OnGUI()
	{
		if (!isLocalPlayer || promptTarget == null) return;

		Camera cam = Camera.main;
		if (cam == null) return;

		// Pop-up floats just above the top of the object; falls back to below the crosshair.
		Vector2 screen = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f + 60f);
		Collider collider = promptTarget.GetComponentInChildren<Collider>();
		Vector3 anchor = collider != null
			? new Vector3(collider.bounds.center.x, collider.bounds.max.y + 0.25f, collider.bounds.center.z)
			: promptTarget.transform.position + Vector3.up;
		Vector3 projected = cam.WorldToScreenPoint(anchor);
		if (projected.z > 0f)
			screen = new Vector2(projected.x, Screen.height - projected.y);

		if (promptStyle == null)
		{
			promptStyle = new GUIStyle(GUI.skin.box)
			{
				fontSize = 18,
				fontStyle = FontStyle.Bold,
				alignment = TextAnchor.MiddleCenter,
				padding = new RectOffset(12, 12, 6, 6)
			};
			promptStyle.normal.textColor = Color.white;
		}

		GUIContent content = new GUIContent("[E]  " + promptText);
		Vector2 size = promptStyle.CalcSize(content);
		Rect rect = new Rect(screen.x - size.x * 0.5f, screen.y - size.y, size.x, size.y);
		rect.x = Mathf.Clamp(rect.x, 4f, Screen.width - size.x - 4f);
		rect.y = Mathf.Clamp(rect.y, 4f, Screen.height - size.y - 4f);
		GUI.Box(rect, content, promptStyle);
	}
}
