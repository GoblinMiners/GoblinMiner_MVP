using UnityEngine;

[CreateAssetMenu(fileName = "Item", menuName = "NewItem")]
public class ItemSO : ScriptableObject
{
    public string itemName;
	public ItemType itemType = ItemType.Unassigned;
	public Sprite icon;
    public int maxStackSize = 1;
    public GameObject itemPrefab;
    public GameObject handItemPrefab;

	private void OnValidate()
	{
		if (itemType == ItemType.Unassigned)
			Debug.LogWarning("Item '" + name + "' has no Item Type set.", this);
	}

}
