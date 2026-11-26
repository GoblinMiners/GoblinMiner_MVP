using System.Collections.Generic;
using UnityEngine;

namespace Inventory
{
	public class ChestRecord
	{
		public int TypeId;
		public Vector3 Position;
		public Quaternion Rotation;
		public List<SlotData> Slots = new List<SlotData>();
	}
}