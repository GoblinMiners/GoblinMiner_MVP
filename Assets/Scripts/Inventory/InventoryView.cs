using UnityEngine;
using System.Collections.Generic;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

public class InventoryView : MonoBehaviour
{
	[SerializeField] private PlayerInventory playerInventory;
	[SerializeField] private SlotView slotPrefab;
	[SerializeField] private Transform bagSlotParent;
	[SerializeField] private Transform hotbarSlotParent;
	[SerializeField] private Image dragIcon;
	[SerializeField] private TextMeshProUGUI dragAmountTxt;
	[SerializeField] private InventoryWindow window;
	[SerializeField] private TooltipView tooltip;

	private List<SlotView> inventoryViews = new List<SlotView>();
	private List<SlotView> hotbarViews = new List<SlotView>();
	private List<SlotView> allViews = new List<SlotView>();
	private InventoryContainer bag;
	private InventoryContainer hotbar;
	private InventoryContainer dragFromContainer;
	private int dragFromIndex = -1;
	private int dragAmount = 0;

	private void Awake()
	{
		if (playerInventory == null)
		{
			Debug.LogError("[InventoryView] Player Inventory is not assigned.", this);
			enabled = false;
			return;
		}

		bag = playerInventory.Bag;
		hotbar = playerInventory.Hotbar;

		for (int i = 0; i < playerInventory.BagSlotCount; i++)
			inventoryViews.Add(Instantiate(slotPrefab, bagSlotParent));

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
			view.HoverStarted += OnSlotHoverStarted;
			view.HoverEnded += OnSlotHoverEnded;
		}

		bag.SlotChanged += OnBagSlotChanged;
		hotbar.SlotChanged += OnHotbarSlotChanged;

		if (window != null)
			window.OpenStateChanged += OnWindowOpenStateChanged;

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
			view.HoverStarted -= OnSlotHoverStarted;
			view.HoverEnded -= OnSlotHoverEnded;
		}

		if (bag != null)
			bag.SlotChanged -= OnBagSlotChanged;

		if (hotbar != null)
			hotbar.SlotChanged -= OnHotbarSlotChanged;

		if (window != null)
			window.OpenStateChanged -= OnWindowOpenStateChanged;
	}

	private void OnBagSlotChanged(int index)
	{
		if (index < 0 || index >= inventoryViews.Count) return;
		inventoryViews[index].Refresh(bag.GetItem(index), bag.GetCount(index));
	}

	private void OnHotbarSlotChanged(int index)
	{
		if (index < 0 || index >= hotbarViews.Count) return;
		hotbarViews[index].Refresh(hotbar.GetItem(index), hotbar.GetCount(index));
	}


	private void RefreshAll()
	{
		for (int i = 0; i < inventoryViews.Count; i++)
			OnBagSlotChanged(i);

		for (int i = 0; i < hotbarViews.Count; i++)
			OnHotbarSlotChanged(i);
	}

	private InventoryContainer ContainerOf(SlotView view)
	{
		if (inventoryViews.Contains(view)) return bag;
		if (hotbarViews.Contains(view)) return hotbar;
		return null;
	}

	private int IndexOf(SlotView view)
	{
		int index = inventoryViews.IndexOf(view);
		if (index >= 0) return index;
		return hotbarViews.IndexOf(view);
	}

	private void OnSlotDragStarted(SlotView view, PointerEventData.InputButton button)
	{
		InventoryContainer from = ContainerOf(view);
		if (from == null) return;

		int index = IndexOf(view);
		ItemSO item = from.GetItem(index);
		int count = from.GetCount(index);
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

		dragFromContainer = from;
		dragFromIndex = index;
		dragIcon.sprite = item.icon;
		dragAmountTxt.text = dragAmount.ToString();
		dragIcon.gameObject.SetActive(true);

		if (tooltip != null)
			tooltip.Hide();
	}

	private void OnSlotDragged(Vector2 screenPosition)
	{
		if (dragFromIndex < 0) return;
		dragIcon.transform.position = screenPosition;
	}

	private void OnSlotDragEnded(SlotView view)
	{
		if (dragFromContainer != null && !window.IsPointerOverPanel())
			dragFromContainer.DropFromSlot(dragFromIndex, dragAmount);

		CancelDrag();
	}
	private void CancelDrag()
	{
		dragFromContainer = null;
		dragFromIndex = -1;
		dragAmount = 0;
		dragIcon.gameObject.SetActive(false);
	}


	private void OnSlotDroppedOn(SlotView view)
	{
		if (dragFromContainer == null) return;

		InventoryContainer.MoveBetween(dragFromContainer, dragFromIndex,
			ContainerOf(view), IndexOf(view), dragAmount);
	}

	private void OnSlotHoverStarted(SlotView view)
	{
		if (tooltip == null) return;
		if (dragFromContainer != null) return; 

		InventoryContainer container = ContainerOf(view);
		if (container == null) return;
		
		tooltip.Show(container.GetItem(IndexOf(view)));
	}

	private void OnSlotHoverEnded(SlotView view)
	{
		if (tooltip != null)
			tooltip.Hide();
	}

	private void OnWindowOpenStateChanged(bool isOpen)
	{
		if (isOpen) return;

		if (!isOpen && tooltip != null)
			tooltip.Hide();

		CancelDrag();
	}
}