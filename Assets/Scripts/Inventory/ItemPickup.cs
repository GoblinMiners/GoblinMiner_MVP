using UnityEngine;
using Inventory;


public class ItemPickup : MonoBehaviour
{
	private PlayerInventory playerInventory;

	private void Awake()
	{
		playerInventory = GetComponentInParent<PlayerInventory>();

		if (playerInventory == null)
			Debug.LogError("[ItemPickup] Needs a PlayerInventory on this object or a parent.", this);
	}

	private void OnTriggerEnter(Collider other)
	{
		if (playerInventory == null || playerInventory != PlayerInventory.Local) return;

		WorldItem worldItem = other.GetComponent<WorldItem>();
		if (worldItem == null) return;

		int leftover = playerInventory.AddItem(worldItem.Item, worldItem.Count);
		int taken = worldItem.Count - leftover;

		if (taken > 0)
			worldItem.Reduce(taken);
	}
}