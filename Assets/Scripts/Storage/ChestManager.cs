using Mirror;
using UnityEngine;

namespace Inventory
{
	public static class ChestManager
	{
		public static void OnWorldLoaded(MaterialWorldPersistence.Snapshot saved)
		{
			try
			{
				SpawnStarterChests();
			}
			catch (System.Exception ex)
			{
				Debug.LogError("[ChestManager] " + ex);
			}
		}

		private static void SpawnStarterChests()
		{
			ChestSpawnPoint[] points = Object.FindObjectsByType<ChestSpawnPoint>(FindObjectsSortMode.None);

			foreach (ChestSpawnPoint point in points)
				SpawnChest(point.Type, point.transform.position, point.transform.rotation);

			Debug.Log($"[ChestManager] Spawned {points.Length} starter chest(s).");   // TEMP: delete after testing
		}

		public static StorageBox SpawnChest(ChestType type, Vector3 position, Quaternion rotation)
		{
			if (type == null || type.prefab == null)
			{
				Debug.LogWarning("[ChestManager] A chest type, or its prefab, is missing.");
				return null;
			}

			GameObject chest = Object.Instantiate(type.prefab, position, rotation);
			StorageBox box = chest.GetComponent<StorageBox>();

			if (box == null)
			{
				Debug.LogWarning($"[ChestManager] {type.prefab.name} has no StorageBox on it.");
				Object.Destroy(chest);
				return null;
			}

			box.Setup(type);
			NetworkServer.Spawn(chest);
			return box;
		}
	}
}