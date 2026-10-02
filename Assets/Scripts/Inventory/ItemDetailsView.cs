using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class ItemDetailsView : MonoBehaviour
{
	[Tooltip("The popup that gets shown and hidden.")]
	[SerializeField] private GameObject panel;
	[SerializeField] private TextMeshProUGUI titleText;
	[SerializeField] private TextMeshProUGUI detailsText;
	[SerializeField] private TextMeshProUGUI descriptionText;
	[SerializeField] private Image iconImage;
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
		if (iconImage != null)
		{
			iconImage.sprite = item.icon;
			iconImage.enabled = item.icon != null;
		}
		detailsText.text = item.itemType + "\nWorth: " + item.baseSellValue + "\nWeight: " + item.itemWeight;

		bool hasDescription = !string.IsNullOrEmpty(item.description);
		descriptionText.gameObject.SetActive(hasDescription);
		descriptionText.text = item.description;

		panel.SetActive(true);

		LayoutRebuilder.ForceRebuildLayoutImmediate(panelRect);

	}

	public void Hide()
	{
		panel.SetActive(false);
	}

}