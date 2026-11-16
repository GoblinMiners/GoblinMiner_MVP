using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>One slot's contents in a networked or saved inventory. ItemId 0 is an empty slot.</summary>
[Serializable]
public struct ItemStack : IEquatable<ItemStack>
{
    public int ItemId;
    public int Count;

    public static ItemStack Of(InventoryContainer container, int index)
    {
        ItemSO item = container.GetItem(index);
        return item == null ? default(ItemStack) : new ItemStack { ItemId = item.itemID, Count = container.GetCount(index) };
    }

    public bool Equals(ItemStack other) => ItemId == other.ItemId && Count == other.Count;
    public override bool Equals(object obj) => obj is ItemStack && Equals((ItemStack)obj);
    public override int GetHashCode() => unchecked(ItemId * 397 ^ Count);
}

/// <summary>Item lookups shared by inventories, storage and saves.</summary>
public static class GobmineItems
{
    /// <summary>The item with this ID from the item database, or from the terrain catalogue for ores.</summary>
    public static ItemSO Find(int itemId)
    {
        if (itemId <= 0) return null;
        ItemSO item = ItemDatabase.Find(itemId);
        if (item == null && World.Instance != null) item = World.Instance.GetInventoryItem(itemId);
        return item;
    }

    /// <summary>Copies a container's slots for the network or a save.</summary>
    public static void Write(InventoryContainer container, SyncList<ItemStack> list)
    {
        for (int i = 0; i < container.SlotCount; i++)
        {
            ItemStack stack = ItemStack.Of(container, i);
            if (i < list.Count) { if (!list[i].Equals(stack)) list[i] = stack; }
            else list.Add(stack);
        }
        while (list.Count > container.SlotCount) list.RemoveAt(list.Count - 1);
    }

    /// <summary>Makes a container show exactly the given slots. Unknown item IDs show as empty.</summary>
    public static void Read(IList<ItemStack> stacks, InventoryContainer container)
    {
        for (int i = 0; i < container.SlotCount; i++)
        {
            ItemStack stack = i < stacks.Count ? stacks[i] : default(ItemStack);
            ItemSO item = Find(stack.ItemId);
            if (item == null && stack.ItemId > 0)
                Debug.LogWarning("[Items] Unknown item ID " + stack.ItemId + ". Rebuild the item database (Tools > Gobmine > Items).");
            container.SetSlot(i, item, stack.Count);
        }
    }
}

/// <summary>
/// The player's bag and hotbar. The server owns the contents; only the owning client
/// receives them (SyncMode.Owner), and every change the client asks for goes through a
/// validated Command. Mining yields fractions of a unit: whole units go into slots and the
/// remainder below one unit is kept on the server until it adds up.
///
/// Resources (ores) live in the bag and in shared storage, never in the hotbar. When there
/// is no room, the caller leaves the rest on the ground as a pickup.
/// </summary>
public class PlayerInventory : NetworkBehaviour, IInventoryOwner
{
    private enum ContainerId : byte { Bag = 0, Hotbar = 1, Storage = 2, None = 255 }

    [Tooltip("Slots in the bag. Bag upgrades will raise this.")]
    [SerializeField, Min(1)] private int bagSlotCount = 4;
    [Tooltip("Slots in the hotbar. Holds consumables and supports only.")]
    [SerializeField, Min(1)] private int hotbarSlotCount = 4;
    [Tooltip("How far away a shared storage box can be opened, in metres.")]
    [SerializeField, Min(0.5f)] private float storageReach = 3f;
    [Tooltip("How far in front of the player dropped items land, in metres.")]
    [SerializeField, Min(0f)] private float dropDistance = 1.2f;

    private readonly SyncList<ItemStack> bagSync = new SyncList<ItemStack>();
    private readonly SyncList<ItemStack> hotbarSync = new SyncList<ItemStack>();

    public static PlayerInventory Local { get; private set; }
    /// <summary>Any slot changed, on the server and on the owning client.</summary>
    public event Action Changed;
    public event Action<int, ItemSO> HotbarSlotUsed;
    public event Action<InventoryContainer> StorageOpened;
    public event Action<InventoryContainer> StorageClosed;
    /// <summary>Server: a hotbar item was used. Hook item effects here.</summary>
    public static event Action<PlayerInventory, ItemSO> ServerItemUsed;
    internal float NextServerMiningTime;

    // Server: the real contents. Owning client: a mirror rebuilt from the sync lists.
    private InventoryContainer bag, hotbar;
    private readonly Dictionary<int, double> fractions = new Dictionary<int, double>();
    private SharedStorage serverStorage, clientStorage;
    private GameObject localUI;
    private float nextStorageCheck;

    public int BagSlotCount => bagSlotCount;
    public int HotbarSlotCount => hotbarSlotCount;
    public InventoryContainer Bag { get { CreateContainers(); return bag; } }
    public InventoryContainer Hotbar { get { CreateContainers(); return hotbar; } }
    public float DropDistance => dropDistance;

    private void Awake()
    {
        syncMode = SyncMode.Owner;
        CreateContainers();
    }

    private void CreateContainers()
    {
        if (bag != null) return;
        bag = new InventoryContainer(bagSlotCount);
        hotbar = new InventoryContainer(hotbarSlotCount, ItemType.Consumable, ItemType.Support);
        bag.SlotChanged += index => OnSlotChanged(bag, bagSync);
        hotbar.SlotChanged += index => OnSlotChanged(hotbar, hotbarSync);
    }

    private void OnSlotChanged(InventoryContainer container, SyncList<ItemStack> list)
    {
        if (isServer) GobmineItems.Write(container, list);
        Changed?.Invoke();
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        GobmineItems.Write(bag, bagSync);
        GobmineItems.Write(hotbar, hotbarSync);
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        if (isServer) return; // The host shows the server's containers directly.
        bagSync.Callback += OnBagSync;
        hotbarSync.Callback += OnHotbarSync;
        GobmineItems.Read(bagSync, bag);
        GobmineItems.Read(hotbarSync, hotbar);
    }

    public override void OnStopClient()
    {
        bagSync.Callback -= OnBagSync;
        hotbarSync.Callback -= OnHotbarSync;
        base.OnStopClient();
    }

    private void OnBagSync(SyncList<ItemStack>.Operation op, int index, ItemStack oldItem, ItemStack newItem) => GobmineItems.Read(bagSync, bag);
    private void OnHotbarSync(SyncList<ItemStack>.Operation op, int index, ItemStack oldItem, ItemStack newItem) => GobmineItems.Read(hotbarSync, hotbar);

    public override void OnStartLocalPlayer()
    {
        base.OnStartLocalPlayer();
        Local = this;
        GameObject prefab = Resources.Load<GameObject>("Gobmine/InventoryUI");
        if (prefab == null)
            Debug.LogWarning("[Inventory] No inventory UI at Resources/Gobmine/InventoryUI. Run Tools > Gobmine > Items > Set Up Items.", this);
        else
        {
            localUI = Instantiate(prefab);
            localUI.name = "InventoryUI (local player)";
            InventoryView view = localUI.GetComponentInChildren<InventoryView>(true);
            if (view != null) view.Bind(this);
        }
        Changed?.Invoke();
    }

    public override void OnStopLocalPlayer()
    {
        if (Local == this) Local = null;
        if (localUI != null) Destroy(localUI);
        clientStorage = null;
        base.OnStopLocalPlayer();
    }

    public override void OnStopServer()
    {
        serverStorage = null;
        // Without authenticated persistent account IDs, keep loot in the world rather
        // than trusting a client-selected save key or deleting a departing player's items.
        if (World.Instance != null) World.Instance.PreserveDepartingInventory(this);
        base.OnStopServer();
    }

    private void Update()
    {
        if (isServer && serverStorage != null && Time.unscaledTime >= nextStorageCheck)
        {
            nextStorageCheck = Time.unscaledTime + 0.25f;
            if (serverStorage == null || !StorageInReach(serverStorage, 1f)) ServerCloseStorage(true);
        }
        if (isLocalPlayer) ReadLocalInput();
    }

    private void ReadLocalInput()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null) return;
        if (keyboard.digit1Key.wasPressedThisFrame) UseHotbarSlot(0);
        else if (keyboard.digit2Key.wasPressedThisFrame) UseHotbarSlot(1);
        else if (keyboard.digit3Key.wasPressedThisFrame) UseHotbarSlot(2);
        else if (keyboard.digit4Key.wasPressedThisFrame) UseHotbarSlot(3);
        if (keyboard.eKey.wasPressedThisFrame && clientStorage == null && !GobminePlayerEquipment.LocalMenuOpen)
        {
            Transform pivot = LookPivot();
            CmdOpenStorage(pivot != null ? pivot.forward : transform.forward);
        }
    }

    private Transform LookPivot()
    {
        PlayerMovement movement = GetComponent<PlayerMovement>();
        return movement != null && movement.cameraPivot != null ? movement.cameraPivot : null;
    }

    // ---------------------------------------------------------------- queries (server and owner)

    public ItemSO FindItem(int itemId) => GobmineItems.Find(itemId);

    /// <summary>Whole units in slots, plus the server's fraction below one unit.</summary>
    public double GetQuantity(int itemId)
    {
        ItemSO item = FindItem(itemId);
        if (item == null) return 0d;
        double total = bag.CountOf(item) + hotbar.CountOf(item);
        double fraction;
        if (isServer && fractions.TryGetValue(itemId, out fraction)) total += fraction;
        return total;
    }

    public long GetWholeQuantity(int itemId) => (long)Math.Floor(GetQuantity(itemId));

    /// <summary>Everything carried, by item ID, including the server's fractions.</summary>
    public Dictionary<int, double> Totals
    {
        get
        {
            var totals = new Dictionary<int, double>();
            AddTotals(bag, totals);
            AddTotals(hotbar, totals);
            if (isServer)
                foreach (var pair in fractions)
                {
                    double previous;
                    totals.TryGetValue(pair.Key, out previous);
                    totals[pair.Key] = previous + pair.Value;
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
            double previous;
            totals.TryGetValue(item.itemID, out previous);
            totals[item.itemID] = previous + container.GetCount(i);
        }
    }

    // ---------------------------------------------------------------- server changes

    /// <summary>
    /// Adds mined or collected units. Whole units fill matching stacks, then empty slots
    /// (hotbar stacks first, as in the sandbox inventory). Returns the units that did not
    /// fit, which the caller leaves on the ground. When the item cannot be kept at all, the
    /// whole amount comes back, including any fraction.
    /// </summary>
    [Server]
    public double ServerAddUnits(int itemId, double units)
    {
        if (units <= 0d || double.IsNaN(units) || double.IsInfinity(units)) return 0d;
        ItemSO item = FindItem(itemId);
        if (item == null) return units;
        double fraction;
        fractions.TryGetValue(itemId, out fraction);
        double total = fraction + units;
        bool canKeep = bag.HasRoomOrStackFor(item) || hotbar.HasRoomOrStackFor(item);
        if (!canKeep)
        {
            fractions.Remove(itemId);
            return total;
        }
        int whole = (int)Math.Min(Math.Floor(total), int.MaxValue / 2);
        int leftover = AddWhole(item, whole);
        double rest = total - whole;
        if (rest > 1e-9) fractions[itemId] = rest; else fractions.Remove(itemId);
        int placed = whole - leftover;
        if (placed > 0 && connectionToClient != null) TargetCollected(connectionToClient, itemId, placed);
        return leftover;
    }

    private int AddWhole(ItemSO item, int amount)
    {
        int left = hotbar.TopUpStacks(item, amount);
        left = bag.TopUpStacks(item, left);
        left = bag.FillEmptySlots(item, left);
        return hotbar.FillEmptySlots(item, left);
    }

    /// <summary>Takes whole units for a purchase. Nothing is taken unless every cost can be paid.</summary>
    [Server]
    public bool ServerTrySpend(GobmineExpeditionSettings.Cost[] costs)
    {
        if (costs == null) return false;
        var totals = new Dictionary<int, int>();
        foreach (var c in costs)
        {
            if (c.itemId <= 0 || c.amount <= 0 || FindItem(c.itemId) == null) return false;
            int previous;
            totals.TryGetValue(c.itemId, out previous);
            totals[c.itemId] = previous + c.amount;
        }
        foreach (var c in totals)
        {
            ItemSO item = FindItem(c.Key);
            if (bag.CountOf(item) + hotbar.CountOf(item) < c.Value) return false;
        }
        foreach (var c in totals)
        {
            ItemSO item = FindItem(c.Key);
            int removed = bag.RemoveItem(item, c.Value);
            hotbar.RemoveItem(item, c.Value - removed);
        }
        return true;
    }

    /// <summary>Empties the inventory and returns what it held, by item ID, including fractions.</summary>
    [Server]
    public Dictionary<int, double> ServerTakeAll()
    {
        Dictionary<int, double> totals = Totals;
        for (int i = 0; i < bag.SlotCount; i++) bag.SetSlot(i, null, 0);
        for (int i = 0; i < hotbar.SlotCount; i++) hotbar.SetSlot(i, null, 0);
        fractions.Clear();
        return totals;
    }

    // ---------------------------------------------------------------- IInventoryOwner (owning client)

    public void UseHotbarSlot(int index)
    {
        if (index < 0 || index >= hotbarSlotCount) return;
        HotbarSlotUsed?.Invoke(index, hotbar.GetItem(index)); // Instant UI feedback.
        CmdUseHotbar(index);
    }

    public void MoveItem(InventoryContainer from, int fromIndex, InventoryContainer to, int toIndex, int amount)
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
        return 0; // The server decides; the slot updates when it syncs.
    }

    public void SortBag(SortOrder order) => CmdSort((byte)ContainerId.Bag, (byte)order);

    public void SortStorage(SortOrder order)
    {
        if (clientStorage != null) CmdSort((byte)ContainerId.Storage, (byte)order);
    }

    public void CloseStorage()
    {
        if (clientStorage == null) return;
        CloseClientStorageView();
        CmdCloseStorage();
    }

    private ContainerId IdOf(InventoryContainer container)
    {
        if (container == null) return ContainerId.None;
        if (container == bag) return ContainerId.Bag;
        if (container == hotbar) return ContainerId.Hotbar;
        if (clientStorage != null && container == clientStorage.Container) return ContainerId.Storage;
        return ContainerId.None;
    }

    // ---------------------------------------------------------------- commands

    private InventoryContainer ServerContainer(byte id)
    {
        switch ((ContainerId)id)
        {
            case ContainerId.Bag: return bag;
            case ContainerId.Hotbar: return hotbar;
            case ContainerId.Storage: return serverStorage != null && StorageInReach(serverStorage, 1f) ? serverStorage.Container : null;
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
            container.AddItem(item, removed); // World could not take it: put it back.
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
        if (!CanAct() || !hotbar.IsValidIndex(index)) return;
        ItemSO item = hotbar.GetItem(index);
        if (item == null) return;
        // Consumables are used up; supports stay until a placement system takes them.
        if (item.itemType == ItemType.Consumable) hotbar.TakeFromSlot(index, 1);
        ServerItemUsed?.Invoke(this, item);
    }

    [Command]
    private void CmdOpenStorage(Vector3 direction)
    {
        if (!CanAct() || float.IsNaN(direction.x) || float.IsNaN(direction.y) || float.IsNaN(direction.z) ||
            direction.sqrMagnitude < 0.25f || direction.sqrMagnitude > 4f) return;
        Transform pivot = LookPivot();
        Vector3 origin = pivot != null ? pivot.position : transform.position + Vector3.up * 1.6f;
        if (Vector3.Distance(origin, transform.position) > 3f) return;
        RaycastHit hit;
        if (!Physics.Raycast(origin, direction.normalized, out hit, storageReach + 1f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return;
        SharedStorage storage = hit.collider.GetComponentInParent<SharedStorage>();
        if (storage == null || !StorageInReach(storage, 0.5f)) return;
        if (serverStorage == storage) return;
        serverStorage = storage;
        TargetStorageOpened(connectionToClient, storage.netIdentity);
    }

    [Command]
    private void CmdCloseStorage() => ServerCloseStorage(false);

    [Server]
    private void ServerCloseStorage(bool tellOwner)
    {
        if (serverStorage == null && !tellOwner) return;
        serverStorage = null;
        if (tellOwner && connectionToClient != null) TargetStorageClosed(connectionToClient);
    }

    private bool StorageInReach(SharedStorage storage, float slack)
    {
        if (storage == null) return false;
        Collider collider = storage.GetComponentInChildren<Collider>();
        Vector3 closest = collider != null ? collider.ClosestPoint(transform.position) : storage.transform.position;
        return Vector3.Distance(transform.position, closest) <= storageReach + slack;
    }

    // ---------------------------------------------------------------- owner RPCs

    [TargetRpc]
    private void TargetCollected(NetworkConnectionToClient target, int itemId, int amount)
    {
        ItemSO item = FindItem(itemId);
        if (item != null) GameEvents.TriggerItemCollected(item, amount);
    }

    [TargetRpc]
    private void TargetStorageOpened(NetworkConnectionToClient target, NetworkIdentity storageIdentity)
    {
        SharedStorage storage = storageIdentity != null ? storageIdentity.GetComponent<SharedStorage>() : null;
        if (storage == null || storage == clientStorage) return;
        CloseClientStorageView();
        clientStorage = storage;
        StorageOpened?.Invoke(storage.Container);
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
}
