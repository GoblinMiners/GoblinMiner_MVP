using System;
using Mirror;
using UnityEngine;

namespace Inventory
{
	public struct SlotData
	{
		public int itemId;
		public int count;
	}
	public class PlayerInventory : NetworkBehaviour
	{
		[Tooltip("The InventoryUI prefab. Only created for the Local player.")]
		[SerializeField] private GameObject inventoryUIPrefab;
		[Tooltip("Slots in the bag. Bag upgrades will raise this.")]
		[SerializeField] private int bagSlotCount = 4;

		[Tooltip("Slots in the hotbar. Fixed at 4.")]
		[SerializeField] private int hotbarSlotCount = 4;

		public int BagSlotCount => bagSlotCount;
		public int HotbarSlotCount => hotbarSlotCount;
		public event Action<ItemSO, int> ItemsDropped;
		public event Action<int, ItemSO> HotbarSlotUsed;
		public event Action<InventoryContainer> StorageOpened;
		public event Action<InventoryContainer> StorageClosed;
		public static PlayerInventory Local { get; private set; }
		public static event Action<PlayerInventory> LocalChanged;
		private InventoryContainer bag;
		private InventoryContainer hotbar;
		private InventoryContainer openStorage;
		private GameObject inventoryUI;
		private InventoryWindow window;
		private readonly SyncList<SlotData> bagSlots = new SyncList<SlotData>();
		private readonly SyncList<SlotData> hotbarSlots = new SyncList<SlotData>();

		private void Awake()
		{
			syncMode = SyncMode.Owner;
		}

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

			if (leftover > 0)
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

		public void SortBag(SortOrder order)
		{
			Bag.Sort(order);
		}

		public void SortStorage(SortOrder order)
		{
			if (openStorage != null)
				openStorage.Sort(order);
		}

		public void OpenStorage(InventoryContainer storage)
		{
			if (storage == null || storage == openStorage) return;

			CloseStorage();
			openStorage = storage;
			openStorage.ItemsDropped += RelayItemsDropped;
			StorageOpened?.Invoke(storage);
		}

		public void CloseStorage()
		{
			if (openStorage == null) return;

			InventoryContainer closing = openStorage;
			openStorage = null;
			StorageClosed?.Invoke(closing);
			closing.ItemsDropped -= RelayItemsDropped;
		}

		public void MoveItem(InventoryContainer from, int fromIndex,
		InventoryContainer to, int toIndex, int amount)
		{
			if (!CanReach(from) || !CanReach(to)) return;

			InventoryContainer.MoveBetween(from, fromIndex, to, toIndex, amount);
		}

		public int DropFromSlot(InventoryContainer container, int index, int amount)
		{
			if (!CanReach(container)) return 0;

			return container.DropFromSlot(index, amount);
		}

		private bool CanReach(InventoryContainer container)
		{
			if (container == null) return false;
			return container == Bag || container == Hotbar || container == openStorage;
		}

		private void RelayItemsDropped(ItemSO item, int count)
		{
			ItemsDropped?.Invoke(item, count);
		}

		public void MakeLocal()
		{
			if (Local == this) return;

			Local = this;
			LocalChanged?.Invoke(this);
		}

		public void ClearLocal()
		{
			if (Local != this) return;

			Local = null;
			LocalChanged?.Invoke(null);
		}

		private void OnDestroy()
		{
			ClearLocal();
		}
		public override void OnStartLocalPlayer()
		{
			base.OnStartLocalPlayer();
			MakeLocal();

			if (inventoryUIPrefab == null) return;

			inventoryUI = Instantiate(inventoryUIPrefab);
			window = inventoryUI.GetComponentInChildren<InventoryWindow>();

			if (window != null)
				window.OpenStateChanged += OnBagOpenChanged;
		}

		public override void OnStopLocalPlayer()
		{
			if (window != null)
				window.OpenStateChanged -= OnBagOpenChanged;

			OnBagOpenChanged(false);
			ClearLocal();

			if (inventoryUI != null)
				Destroy(inventoryUI);

			base.OnStopLocalPlayer();
		}

		private void OnBagOpenChanged(bool isOpen)
		{
			GobminePlayerEquipment.LocalMenuOpen = isOpen;
		}

		public override void OnStartServer()
		{
			base.OnStartServer();

			CopyAll(Bag, bagSlots);
			CopyAll(Hotbar, hotbarSlots);

			Bag.SlotChanged += OnServerBagSlotChanged;
			Hotbar.SlotChanged += OnServerHotbarSlotChanged;
		}

		public override void OnStopServer()
		{
			Bag.SlotChanged -= OnServerBagSlotChanged;
			Hotbar.SlotChanged -= OnServerHotbarSlotChanged;

			base.OnStopServer();
		}

		private void OnServerBagSlotChanged(int index)
		{
			bagSlots[index] = ToSlotData(Bag, index);

			Debug.Log($"[PlayerInventory] Bag slot {index} is now item {bagSlots[index].itemId} x{bagSlots[index].count}");   // TEMP: delete after testing
		}

		private void OnServerHotbarSlotChanged(int index)
		{
			hotbarSlots[index] = ToSlotData(Hotbar, index);
		}
		private static void CopyAll(InventoryContainer container, SyncList<SlotData> list)
		{
			list.Clear();

			for (int i = 0; i < container.SlotCount; i++)
				list.Add(ToSlotData(container, i));
		}
		private static SlotData ToSlotData(InventoryContainer container, int index)
		{
			ItemSO item = container.GetItem(index);

			return new SlotData
			{
				itemId = item != null ? item.itemId : 0,
				count = container.GetCount(index)
			};
		}

	}
}