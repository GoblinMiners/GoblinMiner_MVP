using UnityEngine;

[CreateAssetMenu(fileName = "Item", menuName = "NewItem")]
public class ItemSO : ScriptableObject
{
    public string itemName;
    public Sprite icon;
    public int maxStackSize = 1;
    public GameObject itemPrefab;
    public GameObject handItemPrefab;

}
