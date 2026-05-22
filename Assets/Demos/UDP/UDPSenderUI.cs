using UnityEngine;

public class UDPSenderUI : MonoBehaviour
{
    public UDPSender Sender;
    public TMPro.TMP_InputField InpMessage;
    public TMPro.TMP_InputField InpIP;
    public TMPro.TMP_InputField InpPort;

    void Start() {
        InpIP.text = Sender.DestinationIP;
        InpPort.text = Sender.DestinationPort.ToString();
    }

    public void SendMessageViaUDP() {

        string IP = InpIP.text;
        int port = 0;
        if (!int.TryParse(InpPort.text, out port)) {
            Debug.LogWarning("Invalid port: " + InpPort.text);
            return;
        }

        string message = InpMessage.text;

        Sender.DestinationIP = IP;
        Sender.DestinationPort = port;
        Sender.SendUDPMessage(message);
    }
}
