using UnityEngine;

// *****NOTE TO REPLACE PROXY ITEMS WITH ITEMS ONCE THEY ARE MADE OR DELETE THIS FOR BETTER SCRIPT******
public class ItemDropSpawner : MonoBehaviour
{
	[SerializeField] private PlayerInventory playerInventory;
	[SerializeField] private Transform player;

	[Tooltip("How far in front of the player dropped items appear.")]
	[SerializeField] private float dropDistance = 1.5f;

	[Tooltip("How high above the player's base they appear, so they don't spawn in the floor.")]
	[SerializeField] private float dropHeight = 1f;

	private void OnEnable()
	{
		if (playerInventory == null)
		{
			Debug.LogError("[ItemDropSpawner] Player Inventory is not assigned.", this);
			return;
		}
		
		playerInventory.ItemsDropped += OnItemsDropped;
	}

	private void OnDisable()
	{
		if (playerInventory != null)
			playerInventory.ItemsDropped -= OnItemsDropped;
	}

	private void OnItemsDropped(ItemSO item, int count)
	{
		Vector3 spawnPoint = player.position
			+ player.forward * dropDistance
			+ Vector3.up * dropHeight;

		GameObject dropped;

		if (item.itemPrefab != null)
		{
			dropped = Instantiate(item.itemPrefab, spawnPoint, Quaternion.identity);
		}
		else
		{
			// *****NOTE JUST SPAWNS CUBES RIGHT NOW UNTIL ITEMS/PREFABS ARE BUILT AND ADDED******
			dropped = GameObject.CreatePrimitive(PrimitiveType.Cube);
			dropped.transform.position = spawnPoint;
			dropped.transform.localScale = Vector3.one * 0.3f;
			dropped.AddComponent<Rigidbody>();
		}

		WorldItem worldItem = dropped.GetComponent<WorldItem>();
		if (worldItem == null)
			worldItem = dropped.AddComponent<WorldItem>();

		worldItem.Setup(item, count);

		dropped.name = item.itemName + " x" + count;
	}
}