using UnityEngine;
using System.Net;
using System.Net.Sockets;

public class UDPSender : MonoBehaviour
{
    public int DestinationPort = 25000;
    public string DestinationIP = "127.0.0.1";

    UdpClient udp;
    IPEndPoint localEP;

    public void SendUDPMessage(string message) {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(message);
        SendUDPBytes(bytes);
    }

    public void Close() {
        CloseUDP();
    }

    private void SendUDPBytes(byte[] bytes) {
        if (udp == null) {
            udp = new UdpClient();
            localEP = new IPEndPoint(IPAddress.Any, 0);
            udp.Client.Bind(localEP);
        }

        try {
            IPEndPoint destEP = new IPEndPoint(IPAddress.Parse(DestinationIP), DestinationPort);
            udp.Send(bytes, bytes.Length, destEP);
            
        } catch (SocketException e)
        {
            Debug.LogWarning(e.Message);
        }
    }

    void OnDisable() {
        CloseUDP();
    }

    private void CloseUDP() {
        if (udp != null) {
            udp.Close();
            udp = null;
        }
    }

}
