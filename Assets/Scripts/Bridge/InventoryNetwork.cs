using Mirror;
using UnityEngine;

namespace Inventory
{
	public struct SlotData
	{
		public int itemId;
		public int count;
	}

	[RequireComponent(typeof(PlayerInventory))]
	public class InventoryNetwork : NetworkBehaviour
	{
		[SerializeField] private GameObject inventoryUIPrefab;

		private PlayerInventory inventory;
		private GameObject inventoryUI;
		private InventoryWindow window;

		private readonly SyncList<SlotData> bagSlots = new SyncList<SlotData>();
		private readonly SyncList<SlotData> hotbarSlots = new SyncList<SlotData>();

		private void Awake()
		{
			inventory = GetComponent<PlayerInventory>();
			syncMode = SyncMode.Owner;
		}

		public override void OnStartLocalPlayer()
		{
			base.OnStartLocalPlayer();
			inventory.MakeLocal();

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
			inventory.ClearLocal();

			if (inventoryUI != null)
				Destroy(inventoryUI);

			base.OnStopLocalPlayer();
		}

		private void OnBagOpenChanged(bool isOpen)
		{
			GobminePlayerEquipment.LocalMenuOpen = isOpen;
		}
	}
}