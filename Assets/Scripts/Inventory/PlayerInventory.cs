using UnityEngine;

public class PlayerInventory : MonoBehaviour
{
	[Tooltip("Total slots, backpack + hotbar. For now this must match the number of slot squares on screen.")]
	[SerializeField] private int slotCount = 8;

	private InventoryContainer container;

	public InventoryContainer Container
	{
		get
		{
			if (container == null)
				container = new InventoryContainer(slotCount);

			return container;
		}
	}
}