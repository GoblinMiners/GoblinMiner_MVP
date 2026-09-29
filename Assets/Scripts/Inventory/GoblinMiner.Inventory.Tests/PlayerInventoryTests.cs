using NUnit.Framework;
using UnityEngine;

public class PlayerInventoryTests
{
	private GameObject playerObject;
	private PlayerInventory inventory;
	private ItemSO ore;
	private ItemSO potion;

	[SetUp]
	public void SetUp()
	{
		// A throwaway object with a PlayerInventory on it, just for testing.
		// It uses the default counts: 4 bag slots and 4 hotbar slots.
		playerObject = new GameObject("Test Player");
		inventory = playerObject.AddComponent<PlayerInventory>();

		ore = ScriptableObject.CreateInstance<ItemSO>();
		ore.itemName = "Test Ore";
		ore.maxStackSize = 5;
		ore.itemType = ItemType.Resource;

		potion = ScriptableObject.CreateInstance<ItemSO>();
		potion.itemName = "Test Potion";
		potion.maxStackSize = 10;
		potion.itemType = ItemType.Consumable;
	}

	[TearDown]
	public void TearDown()
	{
		Object.DestroyImmediate(playerObject);
		Object.DestroyImmediate(ore);
		Object.DestroyImmediate(potion);
	}

	[Test]
	public void Hotbar_AskedForTwice_IsTheSameList()
	{
		// Catches the "new bag every time" bug from earlier.
		Assert.AreSame(inventory.Hotbar, inventory.Hotbar);
	}

	[Test]
	public void AddItem_BagFull_OreStaysOutOfHotbar()
	{
		// The bag holds 4 stacks of 5 = 20 ore. Try to add 25.
		int leftover = inventory.AddItem(ore, 25);

		Assert.AreEqual(5, leftover);   // the extra 5 are refused...
		for (int i = 0; i < inventory.Hotbar.SlotCount; i++)
			Assert.IsNull(inventory.Hotbar.GetItem(i));   // ...not squeezed into the hotbar
	}

	[Test]
	public void AddItem_Consumable_TopsUpHotbarStackFirst()
	{
		inventory.Hotbar.AddItem(potion, 2);   // a potion stack already on the hotbar

		inventory.AddItem(potion, 3);

		Assert.AreEqual(5, inventory.Hotbar.GetCount(0));
	}
}