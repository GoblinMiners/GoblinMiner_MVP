using NUnit.Framework;
using UnityEngine;

public class InventoryContainerTests
{
	private ItemSO ore;
	private ItemSO rope;

	
	[SetUp]
	public void SetUp()
	{
		ore = ScriptableObject.CreateInstance<ItemSO>();
		ore.itemName = "Test Ore";
		ore.maxStackSize = 5;
		ore.itemType = ItemType.Resource;

		rope = ScriptableObject.CreateInstance<ItemSO>();
		rope.itemName = "Test Rope";
		rope.maxStackSize = 5;
		rope.itemType = ItemType.Consumable;
	}

	[TearDown]
	public void TearDown()
	{
		Object.DestroyImmediate(ore);
		Object.DestroyImmediate(rope);
	}

	[Test]
	public void AddItem_WhenBagFills_ReturnsWhatDidNotFit()
	{
		var container = new InventoryContainer(2);   

		int leftover = container.AddItem(ore, 12);

		Assert.AreEqual(2, leftover);
		Assert.AreEqual(5, container.GetCount(0));
		Assert.AreEqual(5, container.GetCount(1));
	}

	[Test]
	public void DropFromSlot_RemovesItemsAndAnnouncesThem()
	{
		var container = new InventoryContainer(1);
		container.AddItem(ore, 4);

		ItemSO droppedItem = null;
		int droppedCount = 0;
		container.ItemsDropped += (item, count) => { droppedItem = item; droppedCount = count; };

		container.DropFromSlot(0, 3);

		Assert.AreEqual(ore, droppedItem);
		Assert.AreEqual(3, droppedCount);
		Assert.AreEqual(1, container.GetCount(0));
	}

	[Test]
	public void MoveOrSwap_PartOfStackOntoDifferentItem_ChangesNothing()
	{
		var container = new InventoryContainer(2);
		container.AddItem(ore, 4);    
		container.AddItem(rope, 2);   

		
		int announcements = 0;
		container.SlotChanged += index => announcements++;

		container.MoveOrSwap(0, 1, 2);   

		Assert.AreEqual(ore, container.GetItem(0));
		Assert.AreEqual(4, container.GetCount(0));
		Assert.AreEqual(rope, container.GetItem(1));
		Assert.AreEqual(2, container.GetCount(1));
		Assert.AreEqual(0, announcements);
	}

	[Test]
	public void AddItem_TypeNotAllowed_AddsNothing()
	{
		var hotbar = new InventoryContainer(4, ItemType.Consumable, ItemType.Support);

		int leftover = hotbar.AddItem(ore, 3);

		Assert.AreEqual(3, leftover);
		Assert.IsNull(hotbar.GetItem(0));
	}

	[Test]
	public void MoveBetween_ConsumableIntoHotbar_Moves()
	{
		var bag = new InventoryContainer(2);
		var hotbar = new InventoryContainer(2, ItemType.Consumable, ItemType.Support);
		bag.AddItem(rope, 3);

		InventoryContainer.MoveBetween(bag, 0, hotbar, 0, 3);

		Assert.IsNull(bag.GetItem(0));
		Assert.AreEqual(rope, hotbar.GetItem(0));
		Assert.AreEqual(3, hotbar.GetCount(0));
	}

	[Test]
	public void MoveBetween_SwapWouldPutOreInHotbar_ChangesNothing()
	{
		var bag = new InventoryContainer(2);
		var hotbar = new InventoryContainer(2, ItemType.Consumable, ItemType.Support);
		bag.AddItem(ore, 4);   
		hotbar.AddItem(rope, 2);    

		InventoryContainer.MoveBetween(hotbar, 0, bag, 0, 2);

		Assert.AreEqual(rope, hotbar.GetItem(0));
		Assert.AreEqual(2, hotbar.GetCount(0));
		Assert.AreEqual(ore, bag.GetItem(0));
		Assert.AreEqual(4, bag.GetCount(0));
	}

	[Test]
	public void TakeFromSlot_RemovesWithoutDropping()
	{
		var container = new InventoryContainer(1);
		container.AddItem(ore, 4);

		bool dropped = false;
		container.ItemsDropped += (item, count) => dropped = true;

		int taken = container.TakeFromSlot(0, 1);

		Assert.AreEqual(1, taken);
		Assert.AreEqual(3, container.GetCount(0));
		Assert.IsFalse(dropped);
	}

	[Test]
	public void Sort_ByType_MergesStacksAndPacksToFront()
	{
		var container = new InventoryContainer(4);
		container.AddItem(rope, 2);              // slot 0: rope x2
		container.AddItem(ore, 3);               // slot 1: ore x3
		container.MoveOrSwap(0, 3, 1);           // split the rope: slot 0 x1, slot 3 x1

		container.Sort(SortOrder.Type);

		Assert.AreEqual(ore, container.GetItem(0));    // Resource sorts before Consumable
		Assert.AreEqual(3, container.GetCount(0));
		Assert.AreEqual(rope, container.GetItem(1));   // the two ropes merged back together
		Assert.AreEqual(2, container.GetCount(1));
		Assert.IsNull(container.GetItem(2));
		Assert.IsNull(container.GetItem(3));
	}

	[Test]
	public void Sort_ByValue_MostValuableFirst()
	{
		ore.baseSellValue = 3;
		rope.baseSellValue = 10;

		var container = new InventoryContainer(2);
		container.AddItem(ore, 1);    // slot 0
		container.AddItem(rope, 1);   // slot 1

		container.Sort(SortOrder.Value);

		Assert.AreEqual(rope, container.GetItem(0));
		Assert.AreEqual(ore, container.GetItem(1));
	}

	[Test]
	public void Sort_SplitStacks_RefillsFullStacksFirst()
	{
		var container = new InventoryContainer(3);
		container.AddItem(ore, 7);               // slot 0: x5, slot 1: x2
		container.MoveOrSwap(0, 2, 1);           // slot 0: x4, slot 1: x2, slot 2: x1

		container.Sort(SortOrder.Type);

		Assert.AreEqual(5, container.GetCount(0));     // a full stack first
		Assert.AreEqual(2, container.GetCount(1));     // then the rest
		Assert.IsNull(container.GetItem(2));           // nothing lost or left behind
	}

	[Test]
	public void Sort_ByWeight_HeaviestFirst()
	{
		ore.itemWeight = 5f;
		rope.itemWeight = 0.5f;

		var container = new InventoryContainer(2);
		container.AddItem(rope, 1);   // slot 0
		container.AddItem(ore, 1);    // slot 1

		container.Sort(SortOrder.Weight);

		Assert.AreEqual(ore, container.GetItem(0));
		Assert.AreEqual(rope, container.GetItem(1));
	}


}