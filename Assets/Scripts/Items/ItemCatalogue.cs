using System.Collections.Generic;
using UnityEngine;

public class ItemCatalogue
{
	private const string ResourcesFolder = "Items";

	private static ItemCatalogue all;
	private readonly Dictionary<int, ItemSO> byId = new Dictionary<int, ItemSO>();

	public List<string> Problems { get; } = new List<string>();

	public static ItemCatalogue All
	{
		get
		{
			if (all == null)
				all = LoadFromResources();
			return all;
		}
	}

	public ItemCatalogue(IEnumerable<ItemSO> items)
	{
		foreach (ItemSO item in items)
			Add(item);
	}

	public ItemSO Find(int itemId)
	{
		byId.TryGetValue(itemId, out ItemSO item);
		return item;
	}

	private void Add(ItemSO item)
	{
		if (item == null) return;

		if (item.itemId <= 0)
		{
			Problems.Add($"'{item.name}' has no Item Id.");
			return;
		}

		if (byId.TryGetValue(item.itemId, out ItemSO existing))
		{
			Problems.Add($"'{item.name}' and '{existing.name}' both use Item Id {item.itemId}.");
			return;
		}

		byId.Add(item.itemId, item);
	}

	private static ItemCatalogue LoadFromResources()
	{
		var catalogue = new ItemCatalogue(Resources.LoadAll<ItemSO>(ResourcesFolder));

		foreach (string problem in catalogue.Problems)
			Debug.LogWarning("[ItemCatalogue] " + problem);

		return catalogue;
	}
}