using UnityEngine;

[CreateAssetMenu(fileName = "Item", menuName = "NewItem")]
public class ItemSO : ScriptableObject
{
    public string itemName;
	[Tooltip("ID number. Minerals to use 1-999, other items or consumables 1000+, 9000+ for test items (to be deleted later) . Don't change or reuse one once an item has been assigned.")]
	public int itemId;
	public ItemType itemType = ItemType.Unassigned;
	public Sprite icon;
    public int maxStackSize = 1;
	public GameObject itemPrefab;
	[Tooltip("How heavy 1 of this thing is")]
	[Min(0)] public float itemWeight = 1f;

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
