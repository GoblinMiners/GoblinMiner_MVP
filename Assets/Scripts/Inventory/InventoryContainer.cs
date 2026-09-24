using System;
using System.Collections.Generic;
using UnityEngine;

// The contents of one inventory: a player's bag now, a storage box later.
// It keeps the slots, applies the rules, and announces what changed.
// It knows nothing about screens, icons, or the mouse.
public class InventoryContainer
{
	private readonly List<InventorySlot> slots = new List<InventorySlot>();

	// Announced when a slot's contents change. Sends that slot's index.
	public event Action<int> SlotChanged;

	// Announced when items leave this inventory into the world.
	public event Action<ItemSO, int> ItemsDropped;

	public int SlotCount => slots.Count;

	public InventoryContainer(int slotCount)
	{
		for (int i = 0; i < slotCount; i++)
			slots.Add(new InventorySlot());
	}

	// Read-only access. Other scripts can look at a slot,
	// but only this class can change one, so every change gets announced.
	public ItemSO GetItem(int index) => IsValidIndex(index) ? slots[index].Item : null;
	public int GetCount(int index) => IsValidIndex(index) ? slots[index].Count : 0;

	// Returns how many didn't fit.
	public int AddItem(ItemSO itemToAdd, int amount)
	{
		if (itemToAdd == null) return amount;
		if (amount <= 0) return 0;

		int remaining = amount;

		// First pass: top up stacks of the same item.
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

		// Second pass: start new stacks in empty slots.
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

		if (remaining > 0)
			Debug.Log("Inventory full, could not add " + remaining + " " + itemToAdd.itemName);

		return remaining;
	}

	public void MoveOrSwap(int fromIndex, int toIndex, int amount)
	{
		if (!IsValidIndex(fromIndex) || !IsValidIndex(toIndex)) return;
		if (fromIndex == toIndex || amount <= 0) return;

		InventorySlot from = slots[fromIndex];
		InventorySlot to = slots[toIndex];

		if (from.IsEmpty) return;

		amount = Mathf.Min(amount, from.Count);
		bool wholeStack = amount == from.Count;

		if (to.Item == from.Item)
		{
			// Same item: merge as much as fits.
			int leftover = to.AddAmount(amount);
			from.RemoveAmount(amount - leftover);
		}
		else if (to.IsEmpty)
		{
			// Empty target: move.
			to.SetItem(from.Item, amount);
			from.RemoveAmount(amount);
		}
		else if (wholeStack)
		{
			// Different item, whole stack: swap.
			ItemSO tempItem = to.Item;
			int tempCount = to.Count;

			to.SetItem(from.Item, from.Count);
			from.SetItem(tempItem, tempCount);
		}
		else
		{
			// Part of a stack onto a different item: do nothing.
			return;
		}

		SlotChanged?.Invoke(fromIndex);
		SlotChanged?.Invoke(toIndex);
	}

	// Takes items out of a slot and announces that they left the inventory.
	// Returns how many actually came out.
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