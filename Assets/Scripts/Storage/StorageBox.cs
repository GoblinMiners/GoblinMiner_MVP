using UnityEngine;

public class StorageBox : MonoBehaviour, IInteractable
{
	[Tooltip("Number of slots in this box. Customizable for different storage sizes")]
	[SerializeField, Min(1)] private int slotCount = 24;

	private InventoryContainer container;

	private void Awake()
	{
		container = new InventoryContainer(slotCount);
	}

	public void Interact(GameObject interactor)
	{
		PlayerInventory player = interactor.GetComponent<PlayerInventory>();
		if (player == null)
		{
			Debug.LogWarning($"[StorageBox] {interactor.name} has no PlayerInventory, so it can't open {name}.", this);
			return;
		}

		player.OpenStorage(container);
		Debug.Log($"[StorageBox] {interactor.name} opened {name} ({container.SlotCount} slots)", this);
	}
}