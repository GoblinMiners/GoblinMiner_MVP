using Mirror;
using UnityEngine;

namespace Inventory
{
	/// <summary>
	/// A chest every player in the session shares. The server owns the contents and sends
	/// them to every client that can see the chest; clients rebuild their own copy, which the
	/// inventory UI shows. Players open it by pressing E (PlayerInteractor asks the server,
	/// the server calls Interact), and every move is a PlayerInventory Command.
	/// </summary>
	public class StorageBox : NetworkBehaviour, IInteractable
	{
		[SyncVar] private int chestTypeId;
		private readonly SyncList<SlotData> slots = new SyncList<SlotData>();

		public ChestType Type { get; private set; }
		public InventoryContainer Container { get; private set; }

		/// <summary>Server, before spawning: sets the chest type and builds its slots.</summary>
		public void Setup(ChestType type)
		{
			Type = type;
			chestTypeId = type != null ? type.chestTypeId : 0;
			CreateContainer();
		}

		private void CreateContainer()
		{
			if (Type == null || Container != null) return;

			Container = new InventoryContainer(Type.slotCount, Type.allowedTypes);
			Container.SlotChanged += OnServerSlotChanged;
		}

		public override void OnStartServer()
		{
			base.OnStartServer();
			if (Container != null) PlayerInventory.CopyAll(Container, slots);
		}

		public override void OnStartClient()
		{
			base.OnStartClient();
			if (isServer) return;   // The host shows the server's container directly.

			Type = ChestManager.FindType(chestTypeId);
			if (Type == null)
			{
				Debug.LogWarning($"[StorageBox] No chest type in Resources/Chests has ID {chestTypeId}.", this);
				return;
			}

			CreateContainer();
			slots.Callback += OnClientSync;
			PlayerInventory.ReadAll(slots, Container);
		}

		public override void OnStopClient()
		{
			slots.Callback -= OnClientSync;
			base.OnStopClient();
		}

		private void OnServerSlotChanged(int index)
		{
			if (!isServer || index < 0 || index >= slots.Count) return;

			SlotData data = PlayerInventory.ToSlotData(Container, index);
			if (!slots[index].Equals(data)) slots[index] = data;
		}

		private void OnClientSync(SyncList<SlotData>.Operation op, int index, SlotData oldItem, SlotData newItem)
		{
			if (Container != null) PlayerInventory.ReadAll(slots, Container);
		}

		/// <summary>Server: called when a player presses E on this chest.</summary>
		public void Interact(GameObject interactor)
		{
			if (!isServer || Container == null) return;

			PlayerInventory player = interactor.GetComponent<PlayerInventory>();

			if (player == null)
			{
				Debug.LogWarning($"[StorageBox] {interactor.name} has no PlayerInventory, so it can't open {name}.", this);
				return;
			}

			player.ServerOpenStorage(this);
		}
	}
}
