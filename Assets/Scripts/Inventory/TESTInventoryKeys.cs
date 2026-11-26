using UnityEngine;
namespace Inventory
{


	/// <summary>Test keys (editor and development builds only): P/O/I/U ask the server for test items, K sorts the bag.</summary>
	public class TESTInventoryKeys : MonoBehaviour
	{
		[SerializeField] private ItemSO oreItem;
		[SerializeField] private ItemSO consumableItem;
		[SerializeField] private ItemSO supportItem;
		[SerializeField] private ItemSO toolItem;

		private PlayerInventory playerInventory;
		private SortOrder nextSortOrder = SortOrder.Type;

		private void Awake()
		{
			playerInventory = GetComponent<PlayerInventory>();

			if (playerInventory == null)
			{
				Debug.LogError("[TESTInventoryKeys] Needs a PlayerInventory on the same object.", this);
				enabled = false;
			}


		}

		private static SortOrder NextOrder(SortOrder order)
		{
			if (order == SortOrder.Type) return SortOrder.Value;
			if (order == SortOrder.Value) return SortOrder.Weight;
			return SortOrder.Type;
		}

		private void Update()
		{
			if (Input.GetKeyDown(KeyCode.P))
				playerInventory.RequestTestItem(oreItem, 3);
			else if (Input.GetKeyDown(KeyCode.O))
				playerInventory.RequestTestItem(consumableItem, 2);
			else if (Input.GetKeyDown(KeyCode.I))
				playerInventory.RequestTestItem(supportItem, 1);
			else if (Input.GetKeyDown(KeyCode.U))
				playerInventory.RequestTestItem(toolItem, 1);

			else if (Input.GetKeyDown(KeyCode.K))
			{
				playerInventory.SortBag(nextSortOrder);
				Debug.Log("Sorted bag by " + nextSortOrder);
				nextSortOrder = NextOrder(nextSortOrder);
			}
		}
	}
}