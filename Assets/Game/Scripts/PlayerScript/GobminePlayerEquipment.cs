using Inventory;
using System.Text;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// This player's own upgrades (tool tier, suits, air tank, boots) and the shop window.
///
/// Upgrades are per player, not shared: each player buys their own at the shop counter in
/// the spawn room, paying from their own bag. The server owns the upgrade flags and syncs
/// them to everyone (other players' gear can matter for effects). Upgrades last for the
/// session; there are no player accounts yet, so they are not saved per player.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NetworkIdentity), typeof(PlayerInventory))]
public sealed class GobminePlayerEquipment : NetworkBehaviour
{
    [SyncVar] private int unlocked;
    private PlayerInventory _inventory;
    private bool _shopOpen;
    private string _message;
    private float _messageUntil, _nextServerMessage, _nextPurchase;
    private Vector2 _scroll;
    private int _openedFrame;

    private static bool _localShopOpen;
    /// <summary>Set by the inventory window while it is open.</summary>
    public static bool InventoryMenuOpen { get; set; }
    /// <summary>A menu that takes the mouse is open: the shop or the inventory. Stops looking and mining.</summary>
    public static bool LocalMenuOpen => _localShopOpen || InventoryMenuOpen;

    /// <summary>This player's upgrade flags (GobmineResearch bits).</summary>
    public int Unlocks => unlocked;
    public int ToolTier => GobmineExpeditionRules.ToolTier(unlocked);
    public bool Has(GobmineResearch bit) => GobmineExpeditionRules.Has(unlocked, bit);

    /// <summary>The upgrade flags of the player that owns this component, or the world's old shared ones.</summary>
    public static int UnlocksOf(Component player)
    {
        GobminePlayerEquipment gear = player != null ? player.GetComponent<GobminePlayerEquipment>() : null;
        if (gear != null) return gear.unlocked;
        World world = World.Instance;
        return world != null ? world.ExpeditionUnlocks : 0;
    }

    private void Awake() { _inventory = GetComponent<PlayerInventory>(); }

    public override void OnStartServer()
    {
        base.OnStartServer();
        // Worlds saved before upgrades were per player keep their shared unlocks as a starting point.
        World world = World.Instance;
        unlocked = world != null ? world.ExpeditionUnlocks : 0;
    }

    public override void OnStopLocalPlayer() { SetShopOpen(false); base.OnStopLocalPlayer(); }
    private void OnDisable() { if (isLocalPlayer) SetShopOpen(false); }

    private void Update()
    {
        if (!isLocalPlayer || !_shopOpen) return;

        Keyboard keyboard = Keyboard.current;
        // On the host the shop opens in the same frame E was pressed; don't treat that press as "close".
        bool closeKey = Time.frameCount > _openedFrame && keyboard != null &&
            (keyboard.escapeKey.wasPressedThisFrame || keyboard.eKey.wasPressedThisFrame);
        if (closeKey || !ShopCounter.AnyInReach(transform.position)) SetShopOpen(false);
    }

    private void SetShopOpen(bool open)
    {
        if (_shopOpen == open) return;
        _shopOpen = open;
        _localShopOpen = open;
        if (open) _openedFrame = Time.frameCount;
        Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = open;
    }

    // ---------------------------------------------------------------- server

    /// <summary>Server: called when this player presses E on a shop counter.</summary>
    [Server]
    public void ServerOpenShop(ShopCounter counter)
    {
        if (counter == null || !counter.InReach(transform.position)) return;
        var health = GetComponent<GobminePlayerHealth>();
        if (health != null && health.IsDead) return;
        if (connectionToClient != null) TargetOpenShop(connectionToClient);
    }

    [Server]
    private bool ServerCanShop(out string message)
    {
        message = null;
        var health = GetComponent<GobminePlayerHealth>();
        if (health != null && health.IsDead) { message = "A living player is required."; return false; }
        if (!ShopCounter.AnyInReach(transform.position)) { message = "Walk back to the shop counter to buy."; return false; }
        if (Time.unscaledTime < _nextPurchase) return false;
        _nextPurchase = Time.unscaledTime + 0.25f;
        return true;
    }

    [Command]
    private void CmdBuyUpgrade(int recipeIndex)
    {
        if (!ServerCanShop(out string message)) { ServerMessage(message); return; }
        World world = World.Instance;
        if (world == null || !world.ExpeditionEnabled) { ServerMessage("The shop is not open in this world."); return; }
        var recipes = world.ExpeditionProfile.recipes;
        if (recipeIndex < 0 || recipeIndex >= recipes.Length) return;

        var recipe = recipes[recipeIndex];
        if (Has(recipe.unlock)) { ServerMessage("You already have " + recipe.label + "."); return; }
        if (!Has(recipe.prerequisite)) { ServerMessage("Buy the previous upgrade first."); return; }
        if (!_inventory.ServerTrySpend(recipe.costs)) { ServerMessage("Not enough resources in your bag."); return; }

        unlocked |= (int)recipe.unlock;
        ServerMessage("Bought " + recipe.label + ".");
    }

    [Command]
    private void CmdBuyItem(int offerIndex)
    {
        if (!ServerCanShop(out string message)) { ServerMessage(message); return; }
        ShopCatalog catalog = ShopCatalog.Current;
        if (catalog == null || offerIndex < 0 || offerIndex >= catalog.items.Length) return;

        var offer = catalog.items[offerIndex];
        if (offer == null || offer.item == null || offer.amount <= 0) return;
        if (!Has(offer.requiresUpgrade)) { ServerMessage("Buy the upgrade it needs first."); return; }
        if (!_inventory.Bag.HasRoomOrStackFor(offer.item) && !_inventory.Hotbar.HasRoomOrStackFor(offer.item))
        { ServerMessage("No room in your bag."); return; }
        if (!_inventory.ServerTrySpend(offer.costs)) { ServerMessage("Not enough resources in your bag."); return; }

        int leftover = _inventory.AddItem(offer.item, offer.amount);
        // Anything that did not fit is put on the ground in front of the buyer, never lost.
        if (leftover > 0 && World.Instance != null) World.Instance.ServerDropItem(_inventory, offer.item, leftover);
        ServerMessage("Bought " + offer.amount + " " + offer.item.itemName + ".");
    }

    /// <summary>Server, editor test tools only.</summary>
    [Server]
    public void ServerGrantForTesting(int mask) { unlocked |= mask & 511; }

    [Server]
    public void ServerMessage(string message)
    {
        if (string.IsNullOrEmpty(message) || Time.unscaledTime < _nextServerMessage || connectionToClient == null) return;
        _nextServerMessage = Time.unscaledTime + 0.5f;
        TargetMessage(connectionToClient, message);
    }

    [TargetRpc]
    private void TargetMessage(NetworkConnectionToClient target, string message)
    { _message = message; _messageUntil = Time.unscaledTime + 4f; }

    [TargetRpc]
    private void TargetOpenShop(NetworkConnectionToClient target) => SetShopOpen(true);

    // ---------------------------------------------------------------- local UI

    private static string ItemName(int itemId)
    {
        ItemSO item = PlayerInventory.FindItem(itemId);
        return item != null ? item.itemName : "item " + itemId;
    }

    private void AppendCosts(StringBuilder text, GobmineExpeditionSettings.Cost[] costs)
    {
        if (costs == null || costs.Length == 0) { text.Append("free"); return; }
        for (int i = 0; i < costs.Length; i++)
        {
            if (i > 0) text.Append(", ");
            long have = _inventory.GetWholeQuantity(costs[i].itemId);
            text.Append(costs[i].amount).Append(' ').Append(ItemName(costs[i].itemId)).Append(" (have ").Append(have).Append(')');
        }
    }

    private bool CanAffordLocally(GobmineExpeditionSettings.Cost[] costs)
    {
        if (costs == null) return true;
        foreach (var c in costs)
            if (_inventory.GetWholeQuantity(c.itemId) < c.amount) return false;
        return true;
    }

    private void OnGUI()
    {
        World world = World.Instance;
        if (!isLocalPlayer || world == null || !world.ExpeditionEnabled) return;

        GUI.Box(new Rect(12, Screen.height - 74, 450, 60),
            $"Depth {world.DepthAt(transform.position):0.0} m | Tool tier {ToolTier}\nShop: press E at the counter in the spawn room");
        if (Time.unscaledTime < _messageUntil) GUI.Box(new Rect(12, Screen.height - 116, 620, 38), _message);
        if (!_shopOpen) return;

        var settings = world.ExpeditionProfile;
        GUILayout.BeginArea(new Rect(20, 100, Mathf.Min(680, Screen.width - 40), Mathf.Min(620, Screen.height - 210)), GUI.skin.box);
        GUILayout.Label("SHOP - pay with what is in your bag. Upgrades are yours alone.");
        _scroll = GUILayout.BeginScrollView(_scroll);

        GUILayout.Label("UPGRADES");
        var owned = new StringBuilder();
        for (int i = 0; i < settings.recipes.Length; i++)
        {
            var r = settings.recipes[i];
            if (Has(r.unlock)) { owned.Append(owned.Length > 0 ? ", " : "").Append(r.label); continue; }
            if (!Has(r.prerequisite)) continue;   // Only the next step in each line is for sale.

            var text = new StringBuilder(r.label).Append("  -  ");
            AppendCosts(text, r.costs);
            GUI.enabled = CanAffordLocally(r.costs);
            if (GUILayout.Button(text.ToString())) CmdBuyUpgrade(i);
            GUI.enabled = true;
        }
        GUILayout.Label("Owned: " + (owned.Length > 0 ? owned.ToString() : "nothing yet"));

        ShopCatalog catalog = ShopCatalog.Current;
        if (catalog != null && catalog.items.Length > 0)
        {
            GUILayout.Space(8);
            GUILayout.Label("ITEMS");
            for (int i = 0; i < catalog.items.Length; i++)
            {
                var offer = catalog.items[i];
                if (offer == null || offer.item == null || !Has(offer.requiresUpgrade)) continue;

                var text = new StringBuilder().Append(offer.amount).Append(' ').Append(offer.item.itemName).Append("  -  ");
                AppendCosts(text, offer.costs);
                GUI.enabled = CanAffordLocally(offer.costs);
                if (GUILayout.Button(text.ToString())) CmdBuyItem(i);
                GUI.enabled = true;
            }
        }

        GUILayout.EndScrollView();
        GUILayout.Label("E or Esc closes the shop.");
        GUILayout.EndArea();
    }
}
