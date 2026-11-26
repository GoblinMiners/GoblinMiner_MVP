using System;
using UnityEngine;

/// <summary>
/// The items the shop sells. Upgrades (tool tiers, suits, air tank, boots) come from the
/// expedition settings' recipes; this list is for items that go into the buyer's bag.
/// Kept at Resources/Shop/ShopCatalog so the server and every client read the same list.
/// </summary>
[CreateAssetMenu(fileName = "ShopCatalog", menuName = "Goblin Miner/Shop Catalog")]
public sealed class ShopCatalog : ScriptableObject
{
    public const string ResourcePath = "Shop/ShopCatalog";

    [Serializable]
    public sealed class ItemOffer
    {
        [Tooltip("What the buyer gets.")]
        public ItemSO item;
        [Tooltip("How many of the item one purchase gives.")]
        [Min(1)] public int amount = 1;
        [Tooltip("What it costs, by item ID (ores are 1-6).")]
        public GobmineExpeditionSettings.Cost[] costs = new GobmineExpeditionSettings.Cost[0];
        [Tooltip("Optional. Only offered once the buyer has this upgrade, so offers can follow a progression.")]
        public GobmineResearch requiresUpgrade = GobmineResearch.None;
    }

    public ItemOffer[] items = new ItemOffer[0];

    private static ShopCatalog loaded;
    private static bool searched;

    /// <summary>The catalogue in Resources, or null (the shop then only sells upgrades).</summary>
    public static ShopCatalog Current
    {
        get
        {
            if (!searched)
            {
                loaded = Resources.Load<ShopCatalog>(ResourcePath);
                searched = true;
            }
            return loaded;
        }
    }
}
