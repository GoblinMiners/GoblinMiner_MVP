using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class TooltipView : MonoBehaviour
{
	[Tooltip("The popup that gets shown and hidden.")]
	[SerializeField] private GameObject panel;
	[SerializeField] private TextMeshProUGUI titleText;
	[SerializeField] private TextMeshProUGUI detailsText;
	[SerializeField] private TextMeshProUGUI descriptionText;
	[SerializeField] private Vector2 mouseOffset = new Vector2(16f, 16f);
	private RectTransform panelRect;

	private void Awake()
	{
		if (panel == null)
		{
			Debug.LogError("[TooltipView] Panel is not assigned.", this);
			enabled = false;
			return;
		}

		panelRect = panel.GetComponent<RectTransform>();
		Hide();
	}

	public void Show(ItemSO item)
	{
		if (item == null)
		{
			Hide();
			return;
		}

		titleText.text = item.itemName;
		detailsText.text = item.itemType + "\nWorth: " + item.baseSellValue;

		bool hasDescription = !string.IsNullOrEmpty(item.description);
		descriptionText.gameObject.SetActive(hasDescription);
		descriptionText.text = item.description;

		panel.SetActive(true);

		LayoutRebuilder.ForceRebuildLayoutImmediate(panelRect);
		FollowMouse();
	}

	public void Hide()
	{
		panel.SetActive(false);
	}
	private void LateUpdate()
	{
		if (panel.activeSelf)
			FollowMouse();
	}
	private void FollowMouse()
	{
		Vector2 mouse = Input.mousePosition;
		Vector2 size = Vector2.Scale(panelRect.rect.size, panelRect.lossyScale);

		float pivotX = 0f;   
		float pivotY = 1f;   
		float x = mouse.x + mouseOffset.x;
		float y = mouse.y - mouseOffset.y;

		if (x + size.x > Screen.width)
		{
			pivotX = 1f;  
			x = mouse.x - mouseOffset.x;
		}

		if (y - size.y < 0f)
		{
			pivotY = 0f;   
			y = mouse.y + mouseOffset.y;
		}

		panelRect.pivot = new Vector2(pivotX, pivotY);
		panelRect.position = new Vector2(x, y);
	}
}