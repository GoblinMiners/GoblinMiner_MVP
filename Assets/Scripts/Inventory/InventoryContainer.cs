using System;
using System.Collections.Generic;
using UnityEngine;

public class InventoryContainer
{
	private readonly List<InventorySlot> slots = new List<InventorySlot>();
	public event Action<int> SlotChanged;
	public event Action<ItemSO, int> ItemsDropped;
	public int SlotCount => slots.Count;
	private readonly ItemType[] allowedTypes;

	public InventoryContainer(int slotCount, params ItemType[] allowedTypes)
	{
		this.allowedTypes = allowedTypes;

		for (int i = 0; i < slotCount; i++)
			slots.Add(new InventorySlot());
	}

	public bool Accepts(ItemSO item)
	{
		if (item == null) return false;
		if (allowedTypes == null || allowedTypes.Length == 0) return true;

		foreach (ItemType type in allowedTypes)
		{
			if (item.itemType == type)
				return true;
		}

		return false;
	}

	public ItemSO GetItem(int index) => IsValidIndex(index) ? slots[index].Item : null;
	public int GetCount(int index) => IsValidIndex(index) ? slots[index].Count : 0;


	public int AddItem(ItemSO itemToAdd, int amount)
	{
		int remaining = TopUpStacks(itemToAdd, amount);
		return FillEmptySlots(itemToAdd, remaining);
	}

	public int TopUpStacks(ItemSO itemToAdd, int amount)
	{
		if (amount <= 0) return 0;
		if (!Accepts(itemToAdd)) return amount;

		int remaining = amount;

		for (int i = 0; i < slots.Count && remaining > 0; i++)
		{
			if (slots[i].Item == itemToAdd)
			{
				int before = remaining;
				remaining = slots[i].AddAmount(remaining);

				if (remaining != before)
					SlotChanged?.Invoke(i);
			}
		}

		return remaining;
	}

	public int FillEmptySlots(ItemSO itemToAdd, int amount)
	{
		if (amount <= 0) return 0;
		if (!Accepts(itemToAdd)) return amount;

		int remaining = amount;

		for (int i = 0; i < slots.Count && remaining > 0; i++)
		{
			if (slots[i].IsEmpty)
			{
				int toPlace = Mathf.Min(itemToAdd.maxStackSize, remaining);
				slots[i].SetItem(itemToAdd, toPlace);
				remaining -= toPlace;
				SlotChanged?.Invoke(i);
			}
		}

		return remaining;
	}

	public void MoveOrSwap(int fromIndex, int toIndex, int amount)
	{
		MoveBetween(this, fromIndex, this, toIndex, amount);
	}
	public static void MoveBetween(InventoryContainer from, int fromIndex,
		InventoryContainer to, int toIndex, int amount)
	{
		if (from == null || to == null) return;
		if (!from.IsValidIndex(fromIndex) || !to.IsValidIndex(toIndex)) return;
		if (from == to && fromIndex == toIndex) return;
		if (amount <= 0) return;

		InventorySlot fromSlot = from.slots[fromIndex];
		InventorySlot toSlot = to.slots[toIndex];

		if (fromSlot.IsEmpty) return;

		if (!to.Accepts(fromSlot.Item)) return;

		amount = Mathf.Min(amount, fromSlot.Count);
		bool wholeStack = amount == fromSlot.Count;

		if (toSlot.Item == fromSlot.Item)
		{
			int leftover = toSlot.AddAmount(amount);
			fromSlot.RemoveAmount(amount - leftover);
		}
		else if (toSlot.IsEmpty)
		{
			toSlot.SetItem(fromSlot.Item, amount);
			fromSlot.RemoveAmount(amount);
		}
		else if (wholeStack && from.Accepts(toSlot.Item))
		{
			ItemSO tempItem = toSlot.Item;
			int tempCount = toSlot.Count;

			toSlot.SetItem(fromSlot.Item, fromSlot.Count);
			fromSlot.SetItem(tempItem, tempCount);
		}
		else
		{
			return;
		}

		from.SlotChanged?.Invoke(fromIndex);
		to.SlotChanged?.Invoke(toIndex);
	}

	public int DropFromSlot(int index, int amount)
	{
		if (!IsValidIndex(index) || amount <= 0) return 0;

		InventorySlot slot = slots[index];
		ItemSO item = slot.Item;
		int removed = slot.RemoveAmount(amount);

		if (removed > 0)
		{
			SlotChanged?.Invoke(index);
			ItemsDropped?.Invoke(item, removed);
		}

		return removed;
	}

	private bool IsValidIndex(int index) => index >= 0 && index < slots.Count;
}