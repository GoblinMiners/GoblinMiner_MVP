using Mirror;
using UnityEngine;

public class ChangeRoomManager : NetworkRoomManager
{
    // The waiting-room UI has four player slots, so never accept more connections than that,
    // whatever Max Connections is set to on the component.
    public const int MaxPlayers = 4;

    public override void Awake()
    {
        maxConnections = Mathf.Min(maxConnections, MaxPlayers);
        base.Awake();
    }

    // Intentionally empty: the base class would start the game as soon as everyone is ready.
    // Here the host starts it with the Start button (see MenuController).
    public override void OnRoomServerPlayersReady()
    {
    }
}

