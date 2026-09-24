using UnityEngine;

public class ItemPickup : MonoBehaviour
{
	[SerializeField] private PlayerInventory playerInventory;

	private void Awake()
	{
		if (playerInventory == null)
			Debug.LogError("[ItemPickup] Player Inventory is not assigned.", this);
	}

	private void OnTriggerEnter(Collider other)
	{
		if (playerInventory == null) return;

		WorldItem worldItem = other.GetComponent<WorldItem>();
		if (worldItem == null) return;

		int leftover = playerInventory.Container.AddItem(worldItem.Item, worldItem.Count);
		int taken = worldItem.Count - leftover;

		if (taken > 0)
			worldItem.Reduce(taken);
	}
}