using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The physical shop. Look at it and press E: the server checks you are close enough and
/// opens the shop window for you (GobminePlayerEquipment). It is not a network object:
/// every machine builds the same counter at the same place, so it needs no spawning.
/// Place your own prefab with this component in the scene to replace the default counter.
/// </summary>
[DisallowMultipleComponent]
public sealed class ShopCounter : MonoBehaviour, IInteractable
{
    [Tooltip("How close, in metres, a player must stay to buy.")]
    [SerializeField, Min(1f)] private float reach = 3.5f;

    private static readonly List<ShopCounter> all = new List<ShopCounter>();
    public static IReadOnlyList<ShopCounter> All => all;

    private void OnEnable() { if (!all.Contains(this)) all.Add(this); }
    private void OnDisable() => all.Remove(this);

    /// <summary>Server: called when a player presses E on the counter.</summary>
    public void Interact(GameObject interactor)
    {
        GobminePlayerEquipment shopper = interactor != null ? interactor.GetComponent<GobminePlayerEquipment>() : null;
        if (shopper != null) shopper.ServerOpenShop(this);
    }

    /// <summary>True if this position is close enough to the counter to buy.</summary>
    public bool InReach(Vector3 position)
    {
        Collider collider = GetComponentInChildren<Collider>();
        Vector3 closest = collider != null ? collider.ClosestPoint(position) : transform.position;
        return Vector3.Distance(position, closest) <= reach;
    }

    /// <summary>True if any shop counter is close enough to this position.</summary>
    public static bool AnyInReach(Vector3 position)
    {
        foreach (ShopCounter counter in all)
            if (counter != null && counter.InReach(position)) return true;
        return false;
    }

    /// <summary>
    /// Builds the default counter at this position unless the scene already has one.
    /// Called by the World on the server and on every client, so all machines agree.
    /// </summary>
    public static void EnsureExists(Vector3 position)
    {
        if (all.Count > 0) return;

        GameObject counter = GameObject.CreatePrimitive(PrimitiveType.Cube);
        counter.name = "Shop Counter";
        counter.transform.position = position;
        counter.transform.localScale = new Vector3(2f, 1f, 0.8f);
        var renderer = counter.GetComponent<Renderer>();
        if (renderer != null) renderer.material.color = new Color(0.55f, 0.36f, 0.18f);

        var sign = new GameObject("Sign");
        sign.transform.SetParent(counter.transform, false);
        sign.transform.localPosition = new Vector3(0f, 1.3f, 0f);
        sign.transform.localScale = new Vector3(0.5f, 1f, 1.25f);
        var text = sign.AddComponent<TextMesh>();
        text.text = "SHOP\n[E]";
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        text.characterSize = 0.12f;
        text.fontSize = 48;
        text.color = Color.yellow;
        sign.AddComponent<ShopSignFacesCamera>();

        counter.AddComponent<ShopCounter>();
    }
}
