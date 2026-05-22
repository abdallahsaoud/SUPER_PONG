using UnityEngine;

public class UDPReceiverUI : MonoBehaviour
{
    public UDPReceiver Receiver;
    public TMPro.TMP_InputField InpPort;

    public TMPro.TMP_Text TxtMessage;

    public GameObject BtnListen;
    public GameObject BtnStop;

    void Start() {
        InpPort.text = Receiver.ListenPort.ToString();
    }

    void Update() {
        BtnListen.SetActive(!Receiver.IsListening);
        BtnStop.SetActive(Receiver.IsListening);
    }
    
    public void Listen() {
        int port = 0;
        if (!int.TryParse(InpPort.text, out port)) {
            Debug.LogWarning("Invalid port: " + InpPort.text);
            return;
        }

        Receiver.ListenPort = port;
        Receiver.Listen((string message) => {
            TxtMessage.text = message;
        });
        
    }

    public void Close() {
        Receiver.Close();
    }
}
