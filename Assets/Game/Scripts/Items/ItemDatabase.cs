using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Every item the game can save or send over the network, looked up by <see cref="ItemSO.itemID"/>.
/// Kept at Resources/Gobmine/ItemDatabase so the server and every client find the same list.
/// Rebuild it with Tools > Gobmine > Items > Rebuild Item Database after adding items.
/// </summary>
[CreateAssetMenu(fileName = "ItemDatabase", menuName = "MiningGame/Item Database")]
public sealed class ItemDatabase : ScriptableObject
{
	public const string ResourcePath = "Gobmine/ItemDatabase";

	public List<ItemSO> items = new List<ItemSO>();

	private Dictionary<int, ItemSO> byId;
	private static ItemDatabase instance;
	private static bool searched;

	public static ItemDatabase Instance
	{
		get
		{
			if (instance == null && !searched)
			{
				instance = Resources.Load<ItemDatabase>(ResourcePath);
				searched = true;
				if (instance == null)
					Debug.LogWarning("[Items] No ItemDatabase at Resources/" + ResourcePath + ". Run Tools > Gobmine > Items > Set Up Items.");
			}
			return instance;
		}
	}

	/// <summary>The item with this ID, or null. Never throws.</summary>
	public static ItemSO Find(int itemId) => itemId > 0 && Instance != null ? Instance.Get(itemId) : null;

	private void OnEnable() { byId = null; }
	private void OnValidate() { byId = null; }

	public ItemSO Get(int itemId)
	{
		if (itemId <= 0) return null;
		if (byId == null) BuildIndex();
		ItemSO item;
		return byId.TryGetValue(itemId, out item) ? item : null;
	}

	/// <summary>Null when valid, otherwise a description of the first problem found.</summary>
	public string Validate()
	{
		var seen = new Dictionary<int, ItemSO>();
		foreach (ItemSO item in items)
		{
			if (item == null) continue;
			if (item.itemID <= 0) return item.name + " has no item ID.";
			ItemSO other;
			if (seen.TryGetValue(item.itemID, out other) && other != item)
				return item.name + " and " + other.name + " share item ID " + item.itemID + ".";
			seen[item.itemID] = item;
		}
		return null;
	}

	private void BuildIndex()
	{
		byId = new Dictionary<int, ItemSO>();
		foreach (ItemSO item in items)
			if (item != null && item.itemID > 0 && !byId.ContainsKey(item.itemID))
				byId.Add(item.itemID, item);
	}
}
