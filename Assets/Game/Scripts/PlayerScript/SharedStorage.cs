using System.Collections.Generic;
using Mirror;
using UnityEngine;

/// <summary>
/// A storage box every player in the session shares. The server owns the contents and
/// sends them to every client that can see the box. Players open it with E (see
/// PlayerInventory) and move items with the inventory UI; every move is a server Command.
/// Contents are saved with the world under <see cref="StorageKey"/>.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkIdentity))]
public sealed class SharedStorage : NetworkBehaviour
{
    [Tooltip("Number of slots in this box.")]
    [SerializeField, Min(1)] private int slotCount = 24;
    [Tooltip("Save key. Every storage box in a scene needs a different one, and it must not change once a world is saved.")]
    [SerializeField] private string storageKey = "shared-1";

    private readonly SyncList<ItemStack> slotsSync = new SyncList<ItemStack>();
    private InventoryContainer container;

    private static readonly List<SharedStorage> all = new List<SharedStorage>();
    private static readonly Dictionary<string, List<ItemStack>> pendingLoads = new Dictionary<string, List<ItemStack>>();

    /// <summary>Every storage box in the loaded scenes.</summary>
    public static IReadOnlyList<SharedStorage> All => all;
    public string StorageKey => storageKey;
    public InventoryContainer Container { get { Create(); return container; } }

    private void Awake()
    {
        Create();
        if (!all.Contains(this)) all.Add(this);
    }

    // Removed on destroy, not disable, so a shutdown save still finds the box.
    private void OnDestroy() => all.Remove(this);

    private void Create()
    {
        if (container != null) return;
        container = new InventoryContainer(slotCount);
        container.SlotChanged += index => { if (isServer) GobmineItems.Write(container, slotsSync); };
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        List<ItemStack> saved;
        if (pendingLoads.TryGetValue(storageKey, out saved))
        {
            pendingLoads.Remove(storageKey);
            GobmineItems.Read(saved, container);
        }
        GobmineItems.Write(container, slotsSync);
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        if (isServer) return; // The host shows the server's container directly.
        slotsSync.Callback += OnSync;
        GobmineItems.Read(slotsSync, container);
    }

    public override void OnStopClient()
    {
        slotsSync.Callback -= OnSync;
        base.OnStopClient();
    }

    private void OnSync(SyncList<ItemStack>.Operation op, int index, ItemStack oldItem, ItemStack newItem) => GobmineItems.Read(slotsSync, container);

    /// <summary>Server: the slots as they should be saved.</summary>
    public List<ItemStack> Snapshot()
    {
        var stacks = new List<ItemStack>(container.SlotCount);
        for (int i = 0; i < container.SlotCount; i++) stacks.Add(ItemStack.Of(container, i));
        return stacks;
    }

    /// <summary>
    /// Server, while loading a save: fills the box with this key now if it is running,
    /// otherwise as soon as it starts.
    /// </summary>
    public static void LoadWhenReady(string key, List<ItemStack> stacks)
    {
        foreach (SharedStorage storage in all)
        {
            if (storage == null || storage.storageKey != key || !storage.isServer) continue;
            GobmineItems.Read(stacks, storage.Container);
            return;
        }
        pendingLoads[key] = stacks;
    }

    public static void ClearPendingLoads() => pendingLoads.Clear();
}
