using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

public class SlotView : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
{
	[SerializeField] private Image iconImage;
	[SerializeField] private TextMeshProUGUI amountTxt;
	[SerializeField] private GameObject highlight;
	[SerializeField] private GameObject activeMarker;

	public event Action<SlotView, PointerEventData.InputButton> DragStarted;
	public event Action<Vector2> Dragged;
	public event Action<SlotView> DragEnded;
	public event Action<SlotView> DroppedOn;
	public event Action<SlotView> HoverStarted;
	public event Action<SlotView> HoverEnded;
	public event Action<SlotView, PointerEventData.InputButton> Clicked;

	public void Refresh(ItemSO item, int count)
	{
		if (item == null || count <= 0)
		{
			iconImage.enabled = false;
			amountTxt.text = "";
			return;
		}

		iconImage.enabled = true;
		iconImage.sprite = item.icon;
		amountTxt.text = count.ToString();
	}

	public void OnBeginDrag(PointerEventData eventData) => DragStarted?.Invoke(this, eventData.button);
	public void OnDrag(PointerEventData eventData) => Dragged?.Invoke(eventData.position);
	public void OnEndDrag(PointerEventData eventData) => DragEnded?.Invoke(this);
	public void OnDrop(PointerEventData eventData) => DroppedOn?.Invoke(this);
	public void OnPointerEnter(PointerEventData eventData) => HoverStarted?.Invoke(this);
	public void OnPointerExit(PointerEventData eventData) => HoverEnded?.Invoke(this);
	public void OnPointerClick(PointerEventData eventData) => Clicked?.Invoke(this, eventData.button);

	public void SetHighlighted(bool highlighted)
	{
		if (highlight != null)
			highlight.SetActive(highlighted);
	}

	public void ShowActiveMarker(bool show)
	{
		if (activeMarker != null)
			activeMarker.SetActive(show);
	}
}