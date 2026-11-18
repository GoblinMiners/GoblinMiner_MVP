using Mirror;
using UnityEngine;

namespace Inventory
{
	[RequireComponent(typeof(PlayerInventory))]
	public class InventoryNetwork : NetworkBehaviour
	{
		[Tooltip("The InventoryUI prefab. Only created for the player at this computer.")]
		[SerializeField] private GameObject inventoryUIPrefab;

		private PlayerInventory inventory;
		private GameObject inventoryUI;
		private InventoryWindow window;

		private void Awake()
		{
			inventory = GetComponent<PlayerInventory>();
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