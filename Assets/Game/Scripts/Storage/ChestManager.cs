using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace Inventory
{
	public static class ChestManager
	{
		private static readonly List<StorageBox> chests = new List<StorageBox>();
		public static void OnWorldLoaded(MaterialWorldPersistence.Snapshot saved)
		{
			try
			{
				chests.Clear();

				if (saved != null && saved.Chests.Count > 0)
					LoadSavedChests(saved.Chests);
				else
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
			chests.Add(box);
			return box;
		}
		private static void LoadSavedChests(List<ChestRecord> records)
		{
			foreach (ChestRecord record in records)
			{
				ChestType type = FindType(record.TypeId);
				if (type == null)
				{
					Debug.LogWarning($"[ChestManager] No chest type has ID {record.TypeId}, so one saved chest was skipped.");
					continue;
				}

				StorageBox box = SpawnChest(type, record.Position, record.Rotation);
				if (box == null) continue;

				int slotsToFill = Mathf.Min(record.Slots.Count, box.Container.SlotCount);

				for (int i = 0; i < slotsToFill; i++)
				{
					SlotData slot = record.Slots[i];
					if (slot.itemId == 0 || slot.count <= 0) continue;

					ItemSO item = ItemCatalogue.All.Find(slot.itemId);
					if (item == null)
					{
						Debug.LogWarning($"[ChestManager] No item has ID {slot.itemId}, so it was left out of a saved chest.");
						continue;
					}

					box.Container.SetSlot(i, item, slot.count);
				}
			}

		}
		public static List<ChestRecord> CollectForSave()
		{
			var records = new List<ChestRecord>();

			try
			{
				foreach (StorageBox box in chests)
				{
					if (box == null || box.Type == null || box.Container == null) continue;

					if (box.Type.chestTypeId <= 0)
					{
						Debug.LogWarning($"[ChestManager] {box.Type.name} has no Chest Type Id, so it can't be saved.");
						continue;
					}

					var record = new ChestRecord
					{
						TypeId = box.Type.chestTypeId,
						Position = box.transform.position,
						Rotation = box.transform.rotation
					};

					for (int i = 0; i < box.Container.SlotCount; i++)
					{
						ItemSO item = box.Container.GetItem(i);
						record.Slots.Add(new SlotData
						{
							itemId = item != null ? item.itemId : 0,
							count = box.Container.GetCount(i)
						});
					}

					records.Add(record);
				}
			}
			catch (System.Exception ex)
			{
				Debug.LogError("[ChestManager] " + ex);
			}

			return records;
		}
		private static ChestType[] types;

		private static ChestType[] AllTypes => types ??= Resources.LoadAll<ChestType>("Chests");

		/// <summary>The chest type with this ID from Resources/Chests, or null.</summary>
		public static ChestType FindType(int chestTypeId)
		{
			foreach (ChestType type in AllTypes)
				if (type != null && type.chestTypeId == chestTypeId) return type;

			return null;
		}

		/// <summary>
		/// Client: lets Mirror spawn every chest prefab. Chests are spawned at runtime, so their
		/// prefabs must be known before the first spawn message arrives (called from the
		/// network manager's OnStartClient).
		/// </summary>
		public static void RegisterClientPrefabs()
		{
			foreach (ChestType type in AllTypes)
			{
				if (type == null || type.prefab == null) continue;
				if (!type.prefab.TryGetComponent(out NetworkIdentity identity)) continue;
				if (NetworkClient.prefabs.ContainsKey(identity.assetId)) continue;
				NetworkClient.RegisterPrefab(type.prefab);
			}
		}
	}
}