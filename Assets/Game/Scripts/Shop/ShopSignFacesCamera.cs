using UnityEngine;

/// <summary>Keeps the shop sign readable from wherever the local player stands.</summary>
public sealed class ShopSignFacesCamera : MonoBehaviour
{
    private void LateUpdate()
    {
        Camera camera = Camera.main;
        if (camera == null) return;
        Vector3 away = transform.position - camera.transform.position;
        away.y = 0f;
        if (away.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(away);
    }
}
