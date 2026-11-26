using Mirror;
using UnityEngine;

public class ChangeRoomManager : NetworkRoomManager
{
    // The waiting room shows four player slots.
    public const int MaxPlayers = 4;

    public override void Awake()
    {
        maxConnections = Mathf.Min(maxConnections, MaxPlayers);
        base.Awake();
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        // Chests are spawned at runtime; clients must know their prefabs first.
        Inventory.ChestManager.RegisterClientPrefabs();
    }

    // Intentionally empty: the host starts the game with the Start button (MenuController).
    public override void OnRoomServerPlayersReady()
    {
        
        
        
        
    }
}

