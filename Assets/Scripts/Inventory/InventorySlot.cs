using UnityEngine;

public class InventorySlot
{
	public ItemSO Item { get; private set; }
	public int Count { get; private set; }

	public bool IsEmpty => Item == null;
	public bool IsFull => !IsEmpty && Count >= Item.maxStackSize;

	public void SetItem(ItemSO item, int count)
	{
		if (item == null || count <= 0)
		{
			Clear();
			return;
		}

		Item = item;
		Count = Mathf.Min(count, item.maxStackSize);
	}

	public void Clear() 
	{
		Item = null;
		Count = 0;
	}

	public int AddAmount(int amount)
	{
		if (IsEmpty)
			return amount;              

		int spaceLeft = Item.maxStackSize - Count;
		int amountToAdd = Mathf.Min(spaceLeft, amount);

		Count += amountToAdd;
		return amount - amountToAdd;   
	}

	public int RemoveAmount(int amount)
	{
		int amountToRemove = Mathf.Min(amount, Count);
		Count -= amountToRemove;

		if (Count <= 0)
			Clear();

		return amountToRemove;
	}
}