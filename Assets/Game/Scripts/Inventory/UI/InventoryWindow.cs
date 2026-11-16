using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

public class InventoryWindow : MonoBehaviour
{
	[SerializeField] private GameObject container;
	[SerializeField] private KeyCode toggleKey = KeyCode.Tab;
	[SerializeField] private KeyCode closeKey = KeyCode.Escape;
	[SerializeField] private GameObject hotbarContainer;

	public bool IsOpen { get; private set; }

	private static readonly HashSet<InventoryWindow> openWindows = new HashSet<InventoryWindow>();
	/// <summary>True while any inventory window is open. Gameplay uses it to stop looking and mining.</summary>
	public static bool AnyOpen => openWindows.Count > 0;

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

	}

	private void OnDisable()
	{
		openWindows.Remove(this);
	}

	public void SetOpen(bool open)
	{
		IsOpen = open;
		if (open) openWindows.Add(this);
		else openWindows.Remove(this);

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