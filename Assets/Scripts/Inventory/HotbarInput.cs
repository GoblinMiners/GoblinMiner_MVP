using UnityEngine;

public class HotbarInput : MonoBehaviour
{
	private PlayerInventory playerInventory;

	private void Awake()
	{
		playerInventory = GetComponent<PlayerInventory>();

		if (playerInventory == null)
		{
			Debug.LogError("[HotbarInput] Needs a PlayerInventory on the same object.", this);
			enabled = false;
		}
	}

	private void Update()
	{
		if (Input.GetKeyDown(KeyCode.Alpha1)) playerInventory.UseHotbarSlot(0);
		else if (Input.GetKeyDown(KeyCode.Alpha2)) playerInventory.UseHotbarSlot(1);
		else if (Input.GetKeyDown(KeyCode.Alpha3)) playerInventory.UseHotbarSlot(2);
		else if (Input.GetKeyDown(KeyCode.Alpha4)) playerInventory.UseHotbarSlot(3);
	}
}