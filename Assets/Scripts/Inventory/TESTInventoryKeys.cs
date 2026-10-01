using UnityEngine;

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
			playerInventory.AddItem(oreItem, 3);
		else if (Input.GetKeyDown(KeyCode.O))
			playerInventory.AddItem(consumableItem, 2);
		else if (Input.GetKeyDown(KeyCode.I))
			playerInventory.AddItem(supportItem, 1);
		else if (Input.GetKeyDown(KeyCode.U))
			playerInventory.AddItem(toolItem, 1);
		
		else if (Input.GetKeyDown(KeyCode.K))
		{
			playerInventory.SortBag(nextSortOrder);
			Debug.Log("Sorted bag by " + nextSortOrder);
			nextSortOrder = NextOrder(nextSortOrder);
		}
	}
}