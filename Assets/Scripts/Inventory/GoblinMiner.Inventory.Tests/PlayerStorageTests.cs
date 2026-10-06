using NUnit.Framework;
using UnityEngine;

public class PlayerStorageTests
{
	private GameObject playerObject;
	private PlayerInventory player;

	[SetUp]
	public void SetUp()
	{
		playerObject = new GameObject("Player");
		player = playerObject.AddComponent<PlayerInventory>();
	}

	[TearDown]
	public void TearDown()
	{
		Object.DestroyImmediate(playerObject);
	}

	[Test]
	public void OpenStorage_AnnouncesTheBox()
	{
		var box = new InventoryContainer(8);
		InventoryContainer announced = null;
		player.StorageOpened += opened => announced = opened;

		player.OpenStorage(box);

		Assert.AreSame(box, announced);
	}

	[Test]
	public void OpenStorage_WhileAnotherIsOpen_ClosesTheOldOneFirst()
	{
		var first = new InventoryContainer(8);
		var second = new InventoryContainer(8);
		player.OpenStorage(first);
		InventoryContainer closed = null;
		player.StorageClosed += c => closed = c;

		player.OpenStorage(second);

		Assert.AreSame(first, closed);
	}

	[Test]
	public void CloseStorage_WhenNothingIsOpen_AnnouncesNothing()
	{
		bool announced = false;
		player.StorageClosed += c => announced = true;

		player.CloseStorage();

		Assert.IsFalse(announced);
	}

	[Test]
	public void DropFromOpenStorage_IsPassedOnByThePlayer()
	{
		var item = ScriptableObject.CreateInstance<ItemSO>();
		item.maxStackSize = 10;
		var box = new InventoryContainer(4);
		box.AddItem(item, 3);
		player.OpenStorage(box);
		int droppedCount = 0;
		player.ItemsDropped += (dropped, count) => droppedCount = count;

		box.DropFromSlot(0, 3);

		Assert.AreEqual(3, droppedCount);
		Object.DestroyImmediate(item);
	}

	[Test]
	public void DropFromClosedStorage_IsNotPassedOn()
	{
		var item = ScriptableObject.CreateInstance<ItemSO>();
		item.maxStackSize = 10;
		var box = new InventoryContainer(4);
		box.AddItem(item, 3);
		player.OpenStorage(box);
		player.CloseStorage();
		bool passedOn = false;
		player.ItemsDropped += (dropped, count) => passedOn = true;

		box.DropFromSlot(0, 3);

		Assert.IsFalse(passedOn);
		Object.DestroyImmediate(item);
	}
}