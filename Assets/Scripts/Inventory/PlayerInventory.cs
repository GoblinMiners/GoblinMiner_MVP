using System;
using UnityEngine;

public class PlayerInventory : MonoBehaviour
{
	[Tooltip("Slots in the bag. Bag upgrades will raise this.")]
	[SerializeField] private int bagSlotCount = 4;

	[Tooltip("Slots in the hotbar. Fixed at 4 by design.")]
	[SerializeField] private int hotbarSlotCount = 4;

	public int BagSlotCount => bagSlotCount;
	public int HotbarSlotCount => hotbarSlotCount;
	public event Action<ItemSO, int> ItemsDropped;
	public event Action<int, ItemSO> HotbarSlotUsed;

	private InventoryContainer bag;
	private InventoryContainer hotbar;

	public InventoryContainer Bag
	{
		get
		{
			CreateContainersIfNeeded();
			return bag;
		}
	}

	public InventoryContainer Hotbar
	{
		get
		{
			CreateContainersIfNeeded();
			return hotbar;
		}
	}

	private void CreateContainersIfNeeded()
	{
		if (bag != null) return;

		bag = new InventoryContainer(bagSlotCount);
		hotbar = new InventoryContainer(hotbarSlotCount, ItemType.Consumable, ItemType.Support);

		bag.ItemsDropped += RelayItemsDropped;
		hotbar.ItemsDropped += RelayItemsDropped;
	}

	public int AddItem(ItemSO item, int amount)
	{
		if (item == null)
		{
			Debug.LogWarning("[PlayerInventory] AddItem was given no item. Check the field it came from.", this);
			return amount;
		}

		int leftover = Hotbar.TopUpStacks(item, amount);
		leftover = Bag.TopUpStacks(item, leftover);
		leftover = Bag.FillEmptySlots(item, leftover);
		leftover = Hotbar.FillEmptySlots(item, leftover);

		if (leftover > 0 && item != null)
			Debug.Log("Inventory full, could not add " + leftover + " " + item.itemName);

		return leftover;
	}

	public void UseHotbarSlot(int index)
	{
		if (index < 0 || index >= hotbarSlotCount) return;   // no such slot

		HotbarSlotUsed?.Invoke(index, Hotbar.GetItem(index));
	}

	public int TakeFromHotbar(int index, int amount)
	{
		return Hotbar.TakeFromSlot(index, amount);
	}

	private void RelayItemsDropped(ItemSO item, int count)
	{
		ItemsDropped?.Invoke(item, count);
	}

}