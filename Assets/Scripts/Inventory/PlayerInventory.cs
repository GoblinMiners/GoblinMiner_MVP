using UnityEngine;

public class PlayerInventory : MonoBehaviour
{
	[Tooltip("Slots in the backpack. Bag upgrades will raise this.")]
	[SerializeField] private int backpackSlotCount = 4;

	[Tooltip("Slots in the hotbar. Fixed at 4 by design.")]
	[SerializeField] private int hotbarSlotCount = 4;

	public int BackpackSlotCount => backpackSlotCount;
	public int HotbarSlotCount => hotbarSlotCount;

	private InventoryContainer container;

	public InventoryContainer Container
	{
		get
		{
			if (container == null)
				container = new InventoryContainer(backpackSlotCount + hotbarSlotCount);

			return container;
		}
	}
}