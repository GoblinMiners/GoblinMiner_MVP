using UnityEngine;


public class WorldItem : MonoBehaviour
{
	[SerializeField] private ItemSO item;
	[SerializeField] private int count = 1;

	public ItemSO Item => item;
	public int Count => count;


	public void Reduce(int amountTaken)
	{
		count -= amountTaken;

		if (count <= 0)
			Destroy(gameObject);
	}

	public void Setup(ItemSO newItem, int newCount)
	{
		item = newItem;
		count = newCount;
	}
}