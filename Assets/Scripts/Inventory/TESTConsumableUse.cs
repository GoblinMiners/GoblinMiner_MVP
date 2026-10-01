using UnityEngine;

// Test-only ****Replace with actual consumable system once built****

public class TESTConsumableUse : MonoBehaviour
{
	private PlayerInventory playerInventory;

	private void Awake()
	{
		playerInventory = GetComponent<PlayerInventory>();

		if (playerInventory == null)
		{
			Debug.LogError("[TESTConsumableUse] Needs a PlayerInventory on the same object.", this);
			enabled = false;
		}
	}

	private void OnEnable()
	{
		if (playerInventory != null)
			playerInventory.HotbarSlotUsed += OnHotbarSlotUsed;
	}

	private void OnDisable()
	{
		if (playerInventory != null)
			playerInventory.HotbarSlotUsed -= OnHotbarSlotUsed;
	}

	private void OnHotbarSlotUsed(int index, ItemSO item)
	{
		if (item == null) return;                          
		if (item.itemType != ItemType.Consumable) return; 

		playerInventory.TakeFromHotbar(index, 1);
		Debug.Log("Used 1 " + item.itemName);
	}
}