using Mirror;
using UnityEngine;
namespace Inventory
{


	public class StorageBox : NetworkBehaviour, IInteractable
	{
		public ChestType Type { get; private set; }
		public InventoryContainer Container { get; private set; }
		public void Setup(ChestType type)
		{
			Type = type;
			Container = new InventoryContainer(type.slotCount, type.allowedTypes);
		}
		public void Interact(GameObject interactor)
		{
			if (Container == null) return;

			PlayerInventory player = interactor.GetComponent<PlayerInventory>();

			if (player == null)
			{
				Debug.LogWarning($"[StorageBox] {interactor.name} has no PlayerInventory, so it can't open {name}.", this);
				return;
			}

			player.OpenStorage(Container);
		}
	}
}