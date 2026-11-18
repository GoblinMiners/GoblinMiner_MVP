using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Inventory;

public class LocalPlayerTests
{
	private readonly List<GameObject> made = new List<GameObject>();

	private PlayerInventory MakePlayer()
	{
		var playerObject = new GameObject("Player");
		made.Add(playerObject);
		return playerObject.AddComponent<PlayerInventory>();
	}

	[TearDown]
	public void TearDown()
	{
		foreach (GameObject playerObject in made)
		{
			playerObject.GetComponent<PlayerInventory>().ClearLocal();
			Object.DestroyImmediate(playerObject);
		}
		made.Clear();
	}

	[Test]
	public void MakeLocal_SetsLocal_AndAnnouncesIt()
	{
		PlayerInventory me = MakePlayer();
		PlayerInventory announced = null;
		System.Action<PlayerInventory> listener = p => announced = p;
		PlayerInventory.LocalChanged += listener;

		me.MakeLocal();
		PlayerInventory.LocalChanged -= listener;

		Assert.AreSame(me, PlayerInventory.Local);
		Assert.AreSame(me, announced);
	}

	[Test]
	public void ClearLocal_OnAnotherPlayer_DoesNothing()
	{
		PlayerInventory me = MakePlayer();
		PlayerInventory other = MakePlayer();
		me.MakeLocal();

		other.ClearLocal();

		Assert.AreSame(me, PlayerInventory.Local);
	}

	[Test]
	public void ClearLocal_OnYourself_ClearsIt()
	{
		PlayerInventory me = MakePlayer();
		me.MakeLocal();

		me.ClearLocal();

		Assert.IsNull(PlayerInventory.Local);
	}
}