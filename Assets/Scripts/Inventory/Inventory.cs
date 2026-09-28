using UnityEngine;
using System.Collections.Generic;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

public class Inventory : MonoBehaviour
{
	[SerializeField] private PlayerInventory playerInventory;
	[SerializeField] private ItemSO oreItem;
	[SerializeField] private ItemSO pickaxeItem;
	[SerializeField] private SlotView slotPrefab;
	[SerializeField] private Transform backpackSlotParent;
	[SerializeField] private Transform hotbarSlotParent;
	[SerializeField] private Image dragIcon;
	[SerializeField] private TextMeshProUGUI dragAmountTxt;
	[SerializeField] private InventoryWindow window;

	private List<SlotView> inventoryViews = new List<SlotView>();
	private List<SlotView> hotbarViews = new List<SlotView>();
	private List<SlotView> allViews = new List<SlotView>();

	private InventoryContainer container;

	private int dragFromIndex = -1;
	private int dragAmount = 0;

	private void Awake()
	{
		if (playerInventory == null)
		{
			Debug.LogError("[Inventory] Player Inventory is not assigned.", this);
			enabled = false;
			return;
		}

		container = playerInventory.Container;

		for (int i = 0; i < playerInventory.BackpackSlotCount; i++)
			inventoryViews.Add(Instantiate(slotPrefab, backpackSlotParent));

		for (int i = 0; i < playerInventory.HotbarSlotCount; i++)
			hotbarViews.Add(Instantiate(slotPrefab, hotbarSlotParent));

		allViews.AddRange(inventoryViews);
		allViews.AddRange(hotbarViews);

		foreach (SlotView view in allViews)
		{
			view.DragStarted += OnSlotDragStarted;
			view.Dragged += OnSlotDragged;
			view.DragEnded += OnSlotDragEnded;
			view.DroppedOn += OnSlotDroppedOn;
		}

		container.SlotChanged += OnSlotChanged;

		dragIcon.raycastTarget = false;
		dragIcon.gameObject.SetActive(false);

		RefreshAll();
	}

	private void OnDestroy()
	{
		foreach (SlotView view in allViews)
		{
			view.DragStarted -= OnSlotDragStarted;
			view.Dragged -= OnSlotDragged;
			view.DragEnded -= OnSlotDragEnded;
			view.DroppedOn -= OnSlotDroppedOn;
		}

		if (container != null)
			container.SlotChanged -= OnSlotChanged;
	}

	private void Update()
	{
		if (Input.GetKeyDown(KeyCode.P))
			container.AddItem(oreItem, 3);
		else if (Input.GetKeyDown(KeyCode.O))
			container.AddItem(pickaxeItem, 4);
	}

	private void OnSlotChanged(int index)
	{
		if (index < 0 || index >= allViews.Count) return;
		allViews[index].Refresh(container.GetItem(index), container.GetCount(index));
	}

	private void RefreshAll()
	{
		for (int i = 0; i < allViews.Count; i++)
			OnSlotChanged(i);
	}

	private void OnSlotDragStarted(SlotView view, PointerEventData.InputButton button)
	{
		int index = allViews.IndexOf(view);
		ItemSO item = container.GetItem(index);
		int count = container.GetCount(index);
		if (item == null) return;

		bool shiftHeld = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

		if (button == PointerEventData.InputButton.Left)
			dragAmount = count;
		else if (button == PointerEventData.InputButton.Right && shiftHeld)
			dragAmount = 1;
		else if (button == PointerEventData.InputButton.Right)
			dragAmount = Mathf.CeilToInt(count / 2f);
		else
			return;

		dragFromIndex = index;
		dragIcon.sprite = item.icon;
		dragAmountTxt.text = dragAmount.ToString();
		dragIcon.gameObject.SetActive(true);
	}

	private void OnSlotDragged(Vector2 screenPosition)
	{
		if (dragFromIndex < 0) return;
		dragIcon.transform.position = screenPosition;
	}

	private void OnSlotDragEnded(SlotView view)
	{
		if (dragFromIndex >= 0 && !window.IsPointerOverPanel())
			container.DropFromSlot(dragFromIndex, dragAmount);

		dragFromIndex = -1;
		dragAmount = 0;
		dragIcon.gameObject.SetActive(false);
	}

	private void OnSlotDroppedOn(SlotView view)
	{
		if (dragFromIndex < 0) return;
		container.MoveOrSwap(dragFromIndex, allViews.IndexOf(view), dragAmount);
	}
}