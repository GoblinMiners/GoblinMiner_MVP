using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace Inventory
{
	public struct SlotData
	{
		public int itemId;
		public int count;
	}

	/// <summary>
	/// The player's bag and hotbar, and the only inventory in the game.
	///
	/// The server owns the contents. Only the owning client receives them (SyncMode.Owner)
	/// and rebuilds its own copy of the bag and hotbar from the sync lists, which is what the
	/// UI shows. Every change a client asks for (move, drop, sort, use, close storage) is a
	/// Command the server checks before doing it.
	///
	/// Mining yields fractions of a unit: whole units go into slots and the remainder below
	/// one unit is kept on the server until it adds up. Resources never go in the hotbar.
	/// When there is no room, the caller (World) leaves the rest on the ground.
	/// </summary>
	public class PlayerInventory : NetworkBehaviour
	{
		private enum ContainerId : byte { Bag = 0, Hotbar = 1, Storage = 2, None = 255 }

		[Tooltip("The InventoryUI prefab. Only created for the Local player.")]
		[SerializeField] private GameObject inventoryUIPrefab;
		[Tooltip("Slots in the bag. Bag upgrades will raise this.")]
		[SerializeField] private int bagSlotCount = 4;

		[Tooltip("Slots in the hotbar. Fixed at 4.")]
		[SerializeField] private int hotbarSlotCount = 4;

		[Tooltip("How far away, in metres, a chest stays usable once opened.")]
		[SerializeField, Min(0.5f)] private float storageReach = 3.5f;

		[Tooltip("How far in front of the player dropped items land, in metres.")]
		[SerializeField, Min(0f)] private float dropDistance = 1.2f;

		public int BagSlotCount => bagSlotCount;
		public int HotbarSlotCount => hotbarSlotCount;
		public float DropDistance => dropDistance;

		/// <summary>Kept for old scripts; drops now go to the World as network pickups, so this never fires.</summary>
		public event Action<ItemSO, int> ItemsDropped { add { } remove { } }
		public event Action<int, ItemSO> HotbarSlotUsed;
		public event Action<InventoryContainer> StorageOpened;
		public event Action<InventoryContainer> StorageClosed;
		/// <summary>Any slot changed, on the server and on the owning client.</summary>
		public event Action Changed;
		/// <summary>Server: a hotbar item was used. Hook item effects here.</summary>
		public static event Action<PlayerInventory, ItemSO> ServerItemUsed;

		public static PlayerInventory Local { get; private set; }
		public static event Action<PlayerInventory> LocalChanged;

		/// <summary>Server: the earliest time this player may mine again (World's mining rate limit).</summary>
		internal float NextServerMiningTime;

		// Server: the real contents. Owning client: a copy rebuilt from the sync lists.
		private InventoryContainer bag;
		private InventoryContainer hotbar;
		private readonly Dictionary<int, double> fractions = new Dictionary<int, double>();
		private StorageBox serverStorage;   // Server: the chest this player has open.
		private StorageBox clientStorage;   // Owning client: the chest the UI shows.
		private float nextStorageCheck;
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

			bag.SlotChanged += index => OnSlotChanged(bag, bagSlots, index);
			hotbar.SlotChanged += index => OnSlotChanged(hotbar, hotbarSlots, index);
		}

		private void OnSlotChanged(InventoryContainer container, SyncList<SlotData> list, int index)
		{
			if (isServer && index >= 0 && index < list.Count)
			{
				SlotData data = ToSlotData(container, index);
				if (!list[index].Equals(data)) list[index] = data;
			}
			Changed?.Invoke();
		}

		// ------------------------------------------------------------------ lifecycle

		public override void OnStartServer()
		{
			base.OnStartServer();
			CopyAll(Bag, bagSlots);
			CopyAll(Hotbar, hotbarSlots);
		}

		public override void OnStopServer()
		{
			serverStorage = null;
			// Without persistent player accounts, keep a departing player's items in the
			// world as pickups rather than trusting a client-chosen save key or deleting them.
			if (World.Instance != null) World.Instance.PreserveDepartingInventory(this);
			base.OnStopServer();
		}

		public override void OnStartClient()
		{
			base.OnStartClient();
			if (isServer) return;   // The host shows the server's containers directly.

			bagSlots.Callback += OnBagSync;
			hotbarSlots.Callback += OnHotbarSync;
			ReadAll(bagSlots, Bag);
			ReadAll(hotbarSlots, Hotbar);
		}

		public override void OnStopClient()
		{
			bagSlots.Callback -= OnBagSync;
			hotbarSlots.Callback -= OnHotbarSync;
			base.OnStopClient();
		}

		private void OnBagSync(SyncList<SlotData>.Operation op, int index, SlotData oldItem, SlotData newItem) => ReadAll(bagSlots, Bag);
		private void OnHotbarSync(SyncList<SlotData>.Operation op, int index, SlotData oldItem, SlotData newItem) => ReadAll(hotbarSlots, Hotbar);

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
			clientStorage = null;
			ClearLocal();

			if (inventoryUI != null)
				Destroy(inventoryUI);

			base.OnStopLocalPlayer();
		}

		private void OnBagOpenChanged(bool isOpen)
		{
			GobminePlayerEquipment.InventoryMenuOpen = isOpen;
		}

		private void Update()
		{
			if (!isServer || serverStorage == null || Time.unscaledTime < nextStorageCheck) return;

			nextStorageCheck = Time.unscaledTime + 0.25f;
			if (!InReach(serverStorage, 1f)) ServerCloseStorage(true);
		}

		// ------------------------------------------------------------------ queries

		/// <summary>Finds an item by ID in Resources/Items.</summary>
		public static ItemSO FindItem(int itemId) => itemId > 0 ? ItemCatalogue.All.Find(itemId) : null;

		/// <summary>Whole units in slots, plus (on the server) the fraction below one unit.</summary>
		public double GetQuantity(int itemId)
		{
			ItemSO item = FindItem(itemId);
			if (item == null) return 0d;

			double total = Bag.CountOf(item) + Hotbar.CountOf(item);
			if (isServer && fractions.TryGetValue(itemId, out double fraction)) total += fraction;
			return total;
		}

		public long GetWholeQuantity(int itemId) => (long)Math.Floor(GetQuantity(itemId));

		/// <summary>Everything carried, by item ID, including the server's fractions.</summary>
		public Dictionary<int, double> Totals
		{
			get
			{
				var totals = new Dictionary<int, double>();
				AddTotals(Bag, totals);
				AddTotals(Hotbar, totals);

				if (isServer)
				{
					foreach (var pair in fractions)
					{
						totals.TryGetValue(pair.Key, out double previous);
						totals[pair.Key] = previous + pair.Value;
					}
				}
				return totals;
			}
		}

		private static void AddTotals(InventoryContainer container, Dictionary<int, double> totals)
		{
			for (int i = 0; i < container.SlotCount; i++)
			{
				ItemSO item = container.GetItem(i);
				if (item == null) continue;

				totals.TryGetValue(item.itemId, out double previous);
				totals[item.itemId] = previous + container.GetCount(i);
			}
		}

		// ------------------------------------------------------------------ server changes

		/// <summary>Server: adds whole items (hotbar stacks first, then the bag). Returns what did not fit.</summary>
		public int AddItem(ItemSO item, int amount)
		{
			if (item == null)
			{
				Debug.LogWarning("[PlayerInventory] AddItem was given no item. Check the field it came from.", this);
				return amount;
			}
			if (!isServer)
			{
				Debug.LogWarning("[PlayerInventory] AddItem only works on the server.", this);
				return amount;
			}

			int leftover = Hotbar.TopUpStacks(item, amount);
			leftover = Bag.TopUpStacks(item, leftover);
			leftover = Bag.FillEmptySlots(item, leftover);
			return Hotbar.FillEmptySlots(item, leftover);
		}

		/// <summary>
		/// Server: adds mined or collected units. Whole units go into slots; the fraction below
		/// one is kept. Returns the units that did not fit, which the caller leaves on the
		/// ground. When the item cannot be kept at all, the whole amount comes back.
		/// </summary>
		[Server]
		public double ServerAddUnits(int itemId, double units)
		{
			if (units <= 0d || double.IsNaN(units) || double.IsInfinity(units)) return 0d;

			ItemSO item = FindItem(itemId);
			if (item == null)
			{
				Debug.LogWarning($"[PlayerInventory] No item in Resources/Items has ID {itemId}, so it stays on the ground.", this);
				return units;
			}

			fractions.TryGetValue(itemId, out double fraction);
			double total = fraction + units;

			if (!Bag.HasRoomOrStackFor(item) && !Hotbar.HasRoomOrStackFor(item))
			{
				fractions.Remove(itemId);
				return total;
			}

			int whole = (int)Math.Min(Math.Floor(total), int.MaxValue / 2);
			int leftover = AddItem(item, whole);
			double rest = total - whole;

			if (rest > 1e-9) fractions[itemId] = rest;
			else fractions.Remove(itemId);

			int placed = whole - leftover;
			if (placed > 0 && connectionToClient != null) TargetCollected(connectionToClient, itemId, placed);
			return leftover;
		}

		/// <summary>Server: true if the bag and hotbar hold at least these whole amounts.</summary>
		[Server]
		public bool ServerCanAfford(GobmineExpeditionSettings.Cost[] costs)
		{
			Dictionary<int, int> totals = SumCosts(costs);
			if (totals == null) return false;

			foreach (var c in totals)
			{
				ItemSO item = FindItem(c.Key);
				if (Bag.CountOf(item) + Hotbar.CountOf(item) < c.Value) return false;
			}
			return true;
		}

		/// <summary>Server: takes whole items for a purchase. Nothing is taken unless everything can be paid.</summary>
		[Server]
		public bool ServerTrySpend(GobmineExpeditionSettings.Cost[] costs)
		{
			if (!ServerCanAfford(costs)) return false;

			foreach (var c in SumCosts(costs))
			{
				ItemSO item = FindItem(c.Key);
				int removed = Bag.RemoveItem(item, c.Value);
				Hotbar.RemoveItem(item, c.Value - removed);
			}
			return true;
		}

		private static Dictionary<int, int> SumCosts(GobmineExpeditionSettings.Cost[] costs)
		{
			if (costs == null) return null;

			var totals = new Dictionary<int, int>();
			foreach (var c in costs)
			{
				if (c.itemId <= 0 || c.amount <= 0 || FindItem(c.itemId) == null) return null;
				totals.TryGetValue(c.itemId, out int previous);
				totals[c.itemId] = previous + c.amount;
			}
			return totals;
		}

		/// <summary>Server: empties the inventory and returns what it held, by item ID, including fractions.</summary>
		[Server]
		public Dictionary<int, double> ServerTakeAll()
		{
			Dictionary<int, double> totals = Totals;
			Bag.Clear();
			Hotbar.Clear();
			fractions.Clear();
			return totals;
		}

		/// <summary>Server: removes from a hotbar slot. Returns the amount removed.</summary>
		public int TakeFromHotbar(int index, int amount)
		{
			return isServer ? Hotbar.TakeFromSlot(index, amount) : 0;
		}

		// ------------------------------------------------------------------ owning client requests

		public void UseHotbarSlot(int index)
		{
			if (index < 0 || index >= hotbarSlotCount) return;   // no such slot

			HotbarSlotUsed?.Invoke(index, Hotbar.GetItem(index));   // Instant UI feedback.
			CmdUseHotbar(index);
		}

		public void SortBag(SortOrder order)
		{
			CmdSort((byte)ContainerId.Bag, (byte)order);
		}

		public void SortStorage(SortOrder order)
		{
			if (clientStorage != null)
				CmdSort((byte)ContainerId.Storage, (byte)order);
		}

		public void CloseStorage()
		{
			if (clientStorage == null) return;

			CloseClientStorageView();
			CmdCloseStorage();
		}

		public void MoveItem(InventoryContainer from, int fromIndex,
		InventoryContainer to, int toIndex, int amount)
		{
			ContainerId a = IdOf(from), b = IdOf(to);
			if (a == ContainerId.None || b == ContainerId.None || amount <= 0) return;

			CmdMove((byte)a, fromIndex, (byte)b, toIndex, amount);
		}

		public int DropFromSlot(InventoryContainer container, int index, int amount)
		{
			ContainerId id = IdOf(container);
			if (id == ContainerId.None || amount <= 0) return 0;

			CmdDrop((byte)id, index, amount);
			return 0;   // The server decides; the slot updates when it syncs.
		}

		/// <summary>Editor and development builds only: asks the server for a test item.</summary>
		public void RequestTestItem(ItemSO item, int amount)
		{
			if (item != null && amount > 0) CmdGiveTestItem(item.itemId, amount);
		}

		private ContainerId IdOf(InventoryContainer container)
		{
			if (container == null) return ContainerId.None;
			if (container == Bag) return ContainerId.Bag;
			if (container == Hotbar) return ContainerId.Hotbar;
			if (clientStorage != null && container == clientStorage.Container) return ContainerId.Storage;
			return ContainerId.None;
		}

		// ------------------------------------------------------------------ commands

		private InventoryContainer ServerContainer(byte id)
		{
			switch ((ContainerId)id)
			{
				case ContainerId.Bag: return Bag;
				case ContainerId.Hotbar: return Hotbar;
				case ContainerId.Storage: return serverStorage != null && InReach(serverStorage, 1f) ? serverStorage.Container : null;
				default: return null;
			}
		}

		private bool CanAct()
		{
			GobminePlayerHealth health = GetComponent<GobminePlayerHealth>();
			return health == null || !health.IsDead;
		}

		[Command]
		private void CmdMove(byte fromId, int fromIndex, byte toId, int toIndex, int amount)
		{
			if (!CanAct() || amount <= 0) return;

			InventoryContainer from = ServerContainer(fromId), to = ServerContainer(toId);
			if (from == null || to == null) return;

			InventoryContainer.MoveBetween(from, fromIndex, to, toIndex, amount);
		}

		[Command]
		private void CmdDrop(byte containerId, int index, int amount)
		{
			if (!CanAct() || amount <= 0) return;

			InventoryContainer container = ServerContainer(containerId);
			World world = World.Instance;
			if (container == null || world == null || !container.IsValidIndex(index)) return;

			ItemSO item = container.GetItem(index);
			if (item == null) return;

			int removed = container.TakeFromSlot(index, amount);
			if (removed > 0 && !world.ServerDropItem(this, item, removed))
				container.AddItem(item, removed);   // The world could not take it: put it back.
		}

		[Command]
		private void CmdSort(byte containerId, byte order)
		{
			if (containerId == (byte)ContainerId.Hotbar || !Enum.IsDefined(typeof(SortOrder), (int)order)) return;

			InventoryContainer container = ServerContainer(containerId);
			if (container != null) container.Sort((SortOrder)order);
		}

		[Command]
		private void CmdUseHotbar(int index)
		{
			if (!CanAct() || !Hotbar.IsValidIndex(index)) return;

			ItemSO item = Hotbar.GetItem(index);
			if (item == null) return;

			// Consumables are used up; supports stay until a placement system takes them.
			if (item.itemType == ItemType.Consumable) Hotbar.TakeFromSlot(index, 1);
			ServerItemUsed?.Invoke(this, item);
		}

		[Command]
		private void CmdCloseStorage() => ServerCloseStorage(false);

		[Command]
		private void CmdGiveTestItem(int itemId, int amount)
		{
			if (!Application.isEditor && !Debug.isDebugBuild) return;   // Never in release builds.

			ItemSO item = FindItem(itemId);
			if (item != null) AddItem(item, Mathf.Clamp(amount, 1, 1000));
		}

		// ------------------------------------------------------------------ chests (server)

		/// <summary>Server: opens a chest for this player (called by StorageBox.Interact).</summary>
		[Server]
		public void ServerOpenStorage(StorageBox box)
		{
			if (box == null || box.Container == null || !CanAct() || !InReach(box, 0.5f)) return;
			if (serverStorage == box) return;

			serverStorage = box;
			if (connectionToClient != null) TargetStorageOpened(connectionToClient, box.netIdentity);
		}

		[Server]
		private void ServerCloseStorage(bool tellOwner)
		{
			if (serverStorage == null && !tellOwner) return;

			serverStorage = null;
			if (tellOwner && connectionToClient != null) TargetStorageClosed(connectionToClient);
		}

		private bool InReach(Component target, float slack)
		{
			if (target == null) return false;

			Collider collider = target.GetComponentInChildren<Collider>();
			Vector3 closest = collider != null ? collider.ClosestPoint(transform.position) : target.transform.position;
			return Vector3.Distance(transform.position, closest) <= storageReach + slack;
		}

		// ------------------------------------------------------------------ owner RPCs

		[TargetRpc]
		private void TargetCollected(NetworkConnectionToClient target, int itemId, int amount)
		{
			ItemSO item = FindItem(itemId);
			if (item != null) GameEvents.TriggerItemCollected(item, amount);
		}

		[TargetRpc]
		private void TargetStorageOpened(NetworkConnectionToClient target, NetworkIdentity boxIdentity)
		{
			StorageBox box = boxIdentity != null ? boxIdentity.GetComponent<StorageBox>() : null;
			if (box == null || box.Container == null || box == clientStorage) return;

			CloseClientStorageView();
			clientStorage = box;
			StorageOpened?.Invoke(box.Container);
		}

		[TargetRpc]
		private void TargetStorageClosed(NetworkConnectionToClient target) => CloseClientStorageView();

		private void CloseClientStorageView()
		{
			if (clientStorage == null) return;

			InventoryContainer closing = clientStorage.Container;
			clientStorage = null;
			StorageClosed?.Invoke(closing);
		}

		// ------------------------------------------------------------------ sync helpers

		internal static void CopyAll(InventoryContainer container, SyncList<SlotData> list)
		{
			for (int i = 0; i < container.SlotCount; i++)
			{
				SlotData data = ToSlotData(container, i);
				if (i < list.Count) { if (!list[i].Equals(data)) list[i] = data; }
				else list.Add(data);
			}
			while (list.Count > container.SlotCount) list.RemoveAt(list.Count - 1);
		}

		/// <summary>Makes a container show exactly the given slots. Unknown item IDs show as empty.</summary>
		internal static void ReadAll(IList<SlotData> list, InventoryContainer container)
		{
			for (int i = 0; i < container.SlotCount; i++)
			{
				SlotData data = i < list.Count ? list[i] : default(SlotData);
				ItemSO item = FindItem(data.itemId);
				if (item == null && data.itemId > 0)
					Debug.LogWarning($"[PlayerInventory] No item in Resources/Items has ID {data.itemId}.");
				container.SetSlot(i, item, data.count);
			}
		}

		internal static SlotData ToSlotData(InventoryContainer container, int index)
		{
			ItemSO item = container.GetItem(index);

			return new SlotData
			{
				itemId = item != null ? item.itemId : 0,
				count = item != null ? container.GetCount(index) : 0
			};
		}
	}
}
