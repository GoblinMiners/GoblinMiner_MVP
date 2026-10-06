using UnityEngine;
using System.Collections;
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
	[SerializeField] private ItemDetailsView detailsView;
	[SerializeField] private float markerFlashTime = 0.3f;
	[SerializeField] private SortControlsView sortControls;
	[SerializeField] private GameObject storagePanel;
	[SerializeField] private Transform storageSlotParent;

	private List<SlotView> inventoryViews = new List<SlotView>();
	private List<SlotView> hotbarViews = new List<SlotView>();
	private List<SlotView> allViews = new List<SlotView>();
	private InventoryContainer bag;
	private InventoryContainer hotbar;
	private InventoryContainer dragFromContainer;
	private int dragFromIndex = -1;
	private int dragAmount = 0;
	private InventoryContainer selectedContainer;
	private int selectedIndex = -1;
	private Coroutine flashRoutine;
	private InventoryContainer storage;
	private List<SlotView> storageViews = new List<SlotView>();

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
			view.Clicked += OnSlotClicked;
		}

		bag.SlotChanged += OnBagSlotChanged;
		hotbar.SlotChanged += OnHotbarSlotChanged;
		playerInventory.HotbarSlotUsed += OnHotbarSlotUsed;
		playerInventory.StorageOpened += OnStorageOpened;
		playerInventory.StorageClosed += OnStorageClosed;

		if (storagePanel != null)
			storagePanel.SetActive(false);

		if (sortControls != null)
			sortControls.SortRequested += OnSortRequested;

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
			view.Clicked -= OnSlotClicked;
		}

		if (bag != null)
			bag.SlotChanged -= OnBagSlotChanged;

		if (hotbar != null)
			hotbar.SlotChanged -= OnHotbarSlotChanged;

		if (playerInventory != null)
		{
			playerInventory.HotbarSlotUsed -= OnHotbarSlotUsed;
			playerInventory.StorageOpened -= OnStorageOpened;
			playerInventory.StorageClosed -= OnStorageClosed;
		}

		if (storage != null)
			storage.SlotChanged -= OnStorageSlotChanged;

		if (sortControls != null)
			sortControls.SortRequested -= OnSortRequested;

		if (window != null)
			window.OpenStateChanged -= OnWindowOpenStateChanged;
	}

	private void OnBagSlotChanged(int index)
	{
		if (index < 0 || index >= inventoryViews.Count) return;
		inventoryViews[index].Refresh(bag.GetItem(index), bag.GetCount(index));

		if (selectedContainer == bag && selectedIndex == index)
			ShowSelected();
	}

	private void OnHotbarSlotChanged(int index)
	{
		if (index < 0 || index >= hotbarViews.Count) return;
		hotbarViews[index].Refresh(hotbar.GetItem(index), hotbar.GetCount(index));

		if (selectedContainer == hotbar && selectedIndex == index)
			ShowSelected();
	}

	private void OnStorageOpened(InventoryContainer opened)
	{
		if (storagePanel == null || storageSlotParent == null)
		{
			Debug.LogError("[InventoryView] Storage Panel or Storage Slot Parent is not assigned.", this);
			return;
		}

		storage = opened;
		storage.SlotChanged += OnStorageSlotChanged;

		for (int i = 0; i < storage.SlotCount; i++)
		{
			storageViews.Add(Instantiate(slotPrefab, storageSlotParent));
			OnStorageSlotChanged(i);
		}

		storagePanel.SetActive(true);

		if (window != null)
			window.SetOpen(true);
	}

	private void OnStorageClosed(InventoryContainer closed)
	{
		if (storage != null)
			storage.SlotChanged -= OnStorageSlotChanged;

		foreach (SlotView view in storageViews)
			Destroy(view.gameObject);

		storageViews.Clear();
		storage = null;

		if (storagePanel != null)
			storagePanel.SetActive(false);
	}

	private void OnStorageSlotChanged(int index)
	{
		if (index < 0 || index >= storageViews.Count) return;
		storageViews[index].Refresh(storage.GetItem(index), storage.GetCount(index));
	}
	private void OnHotbarSlotUsed(int index, ItemSO item)
	{
		if (flashRoutine != null)
			StopCoroutine(flashRoutine);

		flashRoutine = StartCoroutine(FlashMarker(index));
	}
	private IEnumerator FlashMarker(int index)
	{
		for (int i = 0; i < hotbarViews.Count; i++)
			hotbarViews[i].ShowActiveMarker(i == index);

		yield return new WaitForSeconds(markerFlashTime); 

		if (index < hotbarViews.Count)
			hotbarViews[index].ShowActiveMarker(false);

		flashRoutine = null;
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

		if (button == PointerEventData.InputButton.Left && shiftHeld)
			dragAmount = 1;
		else if (button == PointerEventData.InputButton.Left)
			dragAmount = count;
		else if (button == PointerEventData.InputButton.Right)
			dragAmount = Mathf.CeilToInt(count / 2f);
		else
			return;
			  
		dragFromContainer = from;
		dragFromIndex = index;
		dragIcon.sprite = item.icon;
		dragAmountTxt.text = dragAmount.ToString();
		dragIcon.gameObject.SetActive(true);

		if (detailsView != null)
			detailsView.Hide();

		Deselect();
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

	private void OnWindowOpenStateChanged(bool isOpen)
	{
		if (isOpen) return;

		if (detailsView != null)
			detailsView.Hide();

		CancelDrag();
		Deselect();
		playerInventory.CloseStorage();
	}

	private void OnSlotClicked(SlotView view, PointerEventData.InputButton button)
	{
		if (button != PointerEventData.InputButton.Left) return;
		Select(ContainerOf(view), IndexOf(view));
	}

	private void Select(InventoryContainer container, int index)
	{
		SlotView previous = ViewOf(selectedContainer, selectedIndex);
		if (previous != null)
			previous.SetHighlighted(false);

		selectedContainer = container;
		selectedIndex = index;

		SlotView current = ViewOf(selectedContainer, selectedIndex);
		if (current != null)
			current.SetHighlighted(true);

		ShowSelected();
	}

	private SlotView ViewOf(InventoryContainer container, int index)
	{
		List<SlotView> views = null;
		if (container == bag) views = inventoryViews;
		else if (container == hotbar) views = hotbarViews;

		if (views == null || index < 0 || index >= views.Count) return null;
		return views[index];
	}

	private void Deselect()
	{
		Select(null, -1);
	}

	private void OnSortRequested(SortOrder order)
	{
		Deselect();
		playerInventory.SortBag(order);
	}
	private void ShowSelected()
	{
		if (detailsView == null) return;

		ItemSO item = null;
		if (selectedContainer != null)
			item = selectedContainer.GetItem(selectedIndex);

		detailsView.Show(item);
	}
}