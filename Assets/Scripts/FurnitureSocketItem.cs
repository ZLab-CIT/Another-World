using UnityEngine;

public class FurnitureSocketItem : MonoBehaviour
{
    [SerializeField] private string socketId = "";

    public string SocketId => socketId;

    public void Initialize(string id)
    {
        socketId = id ?? "";
    }
}
