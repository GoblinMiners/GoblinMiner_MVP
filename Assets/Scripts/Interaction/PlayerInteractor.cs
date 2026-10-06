using UnityEngine;

public class PlayerInteractor : MonoBehaviour
{
	[SerializeField] private Transform viewPoint;
	[SerializeField] private float reach = 3f;

	private void Update()
	{
		if (Input.GetKeyDown(KeyCode.E))
			TryInteract();
	}

	public void TryInteract()
	{
		if (viewPoint == null) return;

		if (!Physics.Raycast(viewPoint.position, viewPoint.forward, out RaycastHit hit,
				reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
			return;

		IInteractable target = hit.collider.GetComponentInParent<IInteractable>();
		if (target != null)
			target.Interact(gameObject);
	}
}