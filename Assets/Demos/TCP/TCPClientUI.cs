using UnityEngine;

public class TCPClientUI : MonoBehaviour
{
    public TCPClient Client;
    public TMPro.TMP_InputField InpMessage;
    public TMPro.TMP_InputField InpIP;
    public TMPro.TMP_InputField InpPort;
    public TMPro.TMP_Text TxtMessage;

    public GameObject BtnConnect;
    public GameObject ConnectedPanel;

    void Start() {
        InpIP.text = Client.DestinationIP;
        InpPort.text = Client.DestinationPort.ToString();
    }

    public void Connect() {
        int port = 0;
        if (!int.TryParse(InpPort.text, out port)) {
            Debug.LogWarning("Invalid port: " + InpPort.text);
            return;
        }

        Client.DestinationIP = InpIP.text;
        Client.DestinationPort = port;
        Client.Connect((string message) => {
            TxtMessage.text = message;
        });
        
    }

    public void Close() {
        Client.Close();
    }

     void Update() {
        BtnConnect.SetActive(!Client.IsConnected);
        ConnectedPanel.SetActive(Client.IsConnected);
    }

    public void SendMessageViaTCP() {
        if (!Client.IsConnected) { return; }
        Client.SendTCPMessage(InpMessage.text);
    }

}
