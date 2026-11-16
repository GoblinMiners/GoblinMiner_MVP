using System;

/// <summary>
/// What the inventory UI needs from whoever owns the bag and hotbar. The sandbox test
/// inventory and the networked game inventory both implement it, so one UI serves both.
/// </summary>
public interface IInventoryOwner
{
	int BagSlotCount { get; }
	int HotbarSlotCount { get; }
	InventoryContainer Bag { get; }
	InventoryContainer Hotbar { get; }

	event Action<int, ItemSO> HotbarSlotUsed;
	event Action<InventoryContainer> StorageOpened;
	event Action<InventoryContainer> StorageClosed;

	void MoveItem(InventoryContainer from, int fromIndex, InventoryContainer to, int toIndex, int amount);
	int DropFromSlot(InventoryContainer container, int index, int amount);
	void SortBag(SortOrder order);
	void SortStorage(SortOrder order);
	void CloseStorage();
}
