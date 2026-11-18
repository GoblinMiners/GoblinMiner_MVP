using UnityEngine;

namespace Inventory
{
	[RequireComponent(typeof(PlayerInventory))]
	public class OfflineLocalPlayer : MonoBehaviour
	{
		private void Awake()
		{
			GetComponent<PlayerInventory>().MakeLocal();
		}
	}
}