using UnityEngine;

namespace Inventory
{
	[CreateAssetMenu(fileName = "ChestType", menuName = "Goblin Miner/Chest Type")]
	public class ChestType : ScriptableObject
	{
		[Tooltip("ID number used when saving. Never change or reuse one once assigned.")]
		public int chestTypeId;

		[Tooltip("How many slots this chest has.")]
		[Min(1)] public int slotCount = 24;

		[Tooltip("Item types this chest accepts. Leave empty(0) to accept anything.")]
		public ItemType[] allowedTypes;

		public GameObject prefab;
	}
}