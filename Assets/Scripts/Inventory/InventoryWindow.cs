using System;
using UnityEngine;
using UnityEngine.EventSystems;

public class InventoryWindow : MonoBehaviour
{
	[SerializeField] private GameObject container;
	[SerializeField] private KeyCode toggleKey = KeyCode.Tab;
	[SerializeField] private KeyCode closeKey = KeyCode.Escape;
	[SerializeField] private GameObject hotbarContainer;

	public bool IsOpen { get; private set; }

	public event Action<bool> OpenStateChanged;

	private void Start()
	{
		SetOpen(false);
	}

	private void Update()
	{
		if (Input.GetKeyDown(toggleKey))
			SetOpen(!IsOpen);

		else if (IsOpen && Input.GetKeyDown(closeKey))
			SetOpen(false);

		if (Input.GetKeyDown(KeyCode.L))
			Debug.Log("Over panel: " + IsPointerOverPanel());
	}

	private void SetOpen(bool open)
	{
		IsOpen = open;

		container.SetActive(open);
		Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
		Cursor.visible = open;

		OpenStateChanged?.Invoke(open);
	}

	public bool IsPointerOverPanel()
	{
		if (!IsOpen) return false;

		return IsPointerOver(container) || IsPointerOver(hotbarContainer);
	}

	private bool IsPointerOver(GameObject panel)
	{
		if (panel == null || !panel.activeInHierarchy) return false;

		RectTransform rect = panel.transform as RectTransform;
		if (rect == null) return false;

		return RectTransformUtility.RectangleContainsScreenPoint(
			rect, Input.mousePosition);
	}

}