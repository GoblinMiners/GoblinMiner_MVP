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
		public override void OnStartServer()
		{
			base.OnStartServer();

			CopyAll(inventory.Bag, bagSlots);
			CopyAll(inventory.Hotbar, hotbarSlots);

			Debug.Log($"[InventoryNetwork] Server filled {bagSlots.Count} bag slots and {hotbarSlots.Count} hotbar slots.");   // Don't forget to DELETE ME!!!
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

		private void OnBagOpenChanged(bool isOpen)
		{
			GobminePlayerEquipment.LocalMenuOpen = isOpen;
		}
	}
}