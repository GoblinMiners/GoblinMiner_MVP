using UnityEngine;

[CreateAssetMenu(fileName = "Item", menuName = "NewItem")]
public class ItemSO : ScriptableObject
{
	[Tooltip("Stable ID used in saves and over the network. Unique per item, and never renumber an item a saved world already holds. Tools > Gobmine > Items > Assign Missing Item IDs fills in free ones.")]
	public int itemID;
    public string itemName;
	public ItemType itemType = ItemType.Unassigned;
	public Sprite icon;
    public int maxStackSize = 1;
	public GameObject itemPrefab;
	[Tooltip("How heavy 1 of this thing is")]
	[Min(0)] public float itemWeight = 1f;

	[Header("Tooltip")]
	[Tooltip("What this sells for at the shops.")]
	[Min(0)] public int baseSellValue = 0;

	[Tooltip("A short descriptive blurb shown when hovering over the item.")]
	[TextArea(2, 5)] public string description;

	private void OnValidate()
	{
		if (itemType == ItemType.Unassigned)
			Debug.LogWarning("Item '" + name + "' has no Item Type set.", this);
	}

}
