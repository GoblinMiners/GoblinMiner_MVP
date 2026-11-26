using UnityEngine;

namespace Inventory
{
	public class ChestSpawnPoint : MonoBehaviour
	{
		[Tooltip("Which kind of chest to spawn here.")]
		[SerializeField] private ChestType chestType;

		public ChestType Type => chestType;

		private void OnDrawGizmos()
		{
			Gizmos.color = Color.yellow;
			Gizmos.DrawWireCube(transform.position, Vector3.one);
		}
	}
}