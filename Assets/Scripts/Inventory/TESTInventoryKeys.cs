using UnityEngine;

public class TESTInventoryKeys : MonoBehaviour
{
	[SerializeField] private ItemSO oreItem;
	[SerializeField] private ItemSO consumableItem;
	[SerializeField] private ItemSO supportItem;

	private PlayerInventory playerInventory;

	private void Awake()
	{
		playerInventory = GetComponent<PlayerInventory>();

		if (playerInventory == null)
		{
			Debug.LogError("[TESTInventoryKeys] Needs a PlayerInventory on the same object.", this);
			enabled = false;
		}
	}

	private void Update()
	{
		if (Input.GetKeyDown(KeyCode.P))
			playerInventory.AddItem(oreItem, 3);
		else if (Input.GetKeyDown(KeyCode.O))
			playerInventory.AddItem(consumableItem, 2);
		else if (Input.GetKeyDown(KeyCode.I))
			playerInventory.AddItem(supportItem, 1);
	}
}