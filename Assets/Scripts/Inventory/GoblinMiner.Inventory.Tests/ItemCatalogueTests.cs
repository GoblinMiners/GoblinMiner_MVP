using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class ItemCatalogueTests
{
	private readonly List<ItemSO> made = new List<ItemSO>();

	private ItemSO MakeItem(int id)
	{
		var item = ScriptableObject.CreateInstance<ItemSO>();
		item.itemId = id;
		made.Add(item);
		return item;
	}

	[TearDown]
	public void TearDown()
	{
		foreach (ItemSO item in made)
			Object.DestroyImmediate(item);
		made.Clear();
	}

	[Test]
	public void Find_ReturnsTheItemWithThatId()
	{
		ItemSO ore = MakeItem(9000);
		var catalogue = new ItemCatalogue(new[] { ore, MakeItem(9001) });

		Assert.AreSame(ore, catalogue.Find(9000));
	}

	[Test]
	public void Find_UnknownId_ReturnsNothing()
	{
		var catalogue = new ItemCatalogue(new[] { MakeItem(9000) });

		Assert.IsNull(catalogue.Find(1234));
	}

	[Test]
	public void TwoItemsWithTheSameId_AreReported()
	{
		var catalogue = new ItemCatalogue(new[] { MakeItem(9000), MakeItem(9000) });

		Assert.AreEqual(1, catalogue.Problems.Count);
	}
}