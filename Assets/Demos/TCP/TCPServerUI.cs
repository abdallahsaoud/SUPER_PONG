using UnityEngine;

public class TCPServerUI : MonoBehaviour
{
    public TCPServer Server;
    public TMPro.TMP_InputField InpPort;

    public TMPro.TMP_InputField InpMessage;

    public TMPro.TMP_Text TxtMessage;
    public TMPro.TMP_Text TxtConnections;

    public GameObject BtnListen;
    public GameObject PanConnected;

    void Start() {
        InpPort.text = Server.ListenPort.ToString();
    }

    void Update() {
        BtnListen.SetActive(!Server.IsListening);
        PanConnected.SetActive(Server.IsListening);
        TxtConnections.text = "There are " + Server.ConnectionCount.ToString()  + " clients connected";
    }
    
    public void Listen() {
        int port = 0;
        if (!int.TryParse(InpPort.text, out port)) {
            Debug.LogWarning("Invalid port: " + InpPort.text);
            return;
        }

        Server.ListenPort = port;
        Server.Listen((string message) => {
            TxtMessage.text = message;
        });
        
    }

    public void Close() {
        Server.Close();
    }

    public void BroadcastMessageViaTCP() {
        if (!Server.IsListening) { return; }
        Server.BroadcastTCPMessage(InpMessage.text);
    }
}
