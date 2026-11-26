using Mirror;
using UnityEngine;

/// <summary>
/// Press E to use what you are looking at (a chest, the shop). The local player sends the
/// look direction; the server does the raycast from this player's own view point and calls
/// IInteractable.Interact, so a client cannot open things it is not actually looking at.
/// </summary>
public class PlayerInteractor : NetworkBehaviour
{
	[SerializeField] private Transform viewPoint;
	[SerializeField] private float reach = 3f;

	private float nextServerInteract;

	private void Update()
	{
		if (!isLocalPlayer || GobminePlayerEquipment.LocalMenuOpen) return;

		if (Input.GetKeyDown(KeyCode.E))
			TryInteract();
	}

	public void TryInteract()
	{
		if (!isLocalPlayer) return;

		Transform view = viewPoint != null ? viewPoint : transform;
		CmdInteract(view.forward);
	}

	[Command]
	private void CmdInteract(Vector3 direction)
	{
		if (Time.unscaledTime < nextServerInteract) return;
		nextServerInteract = Time.unscaledTime + 0.2f;

		if (float.IsNaN(direction.x) || float.IsNaN(direction.y) || float.IsNaN(direction.z) ||
			direction.sqrMagnitude < 0.25f || direction.sqrMagnitude > 4f) return;

		GobminePlayerHealth health = GetComponent<GobminePlayerHealth>();
		if (health != null && health.IsDead) return;

		Vector3 origin = viewPoint != null ? viewPoint.position : transform.position + Vector3.up * 1.6f;
		if (Vector3.Distance(origin, transform.position) > 3f) return;   // View point must be on this player.

		if (!Physics.Raycast(origin, direction.normalized, out RaycastHit hit,
				reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
			return;

		IInteractable target = hit.collider.GetComponentInParent<IInteractable>();
		if (target != null)
			target.Interact(gameObject);
	}
}
