using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class SortControlsView : MonoBehaviour
{
	[SerializeField] private TMP_Dropdown dropdown;
	[SerializeField] private Button sortButton;

	public event Action<SortOrder> SortRequested;

	private SortOrder[] orders;

	private void Awake()
	{
		if (dropdown == null || sortButton == null)
		{
			Debug.LogError("[SortControlsView] Dropdown and Sort Button must both be assigned.", this);
			enabled = false;
			return;
		}

		orders = (SortOrder[])Enum.GetValues(typeof(SortOrder));

		var names = new List<string>();
		foreach (SortOrder order in orders)
			names.Add(order.ToString());

		dropdown.ClearOptions();
		dropdown.AddOptions(names);

		sortButton.onClick.AddListener(OnSortClicked);
	}

	private void OnDestroy()
	{
		if (sortButton != null)
			sortButton.onClick.RemoveListener(OnSortClicked);
	}

	private void OnSortClicked()
	{
		SortRequested?.Invoke(orders[dropdown.value]);
	}
}