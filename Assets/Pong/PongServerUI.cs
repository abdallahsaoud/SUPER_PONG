using UnityEngine;

/// <summary>
/// Minimal server-side UI: a port field, Listen / Close buttons, and a live
/// connection count. Adapted from Assets/Demos/TCP/TCPServerUI.cs.
/// Intended for the headless-or-windowed server build; safe to omit on a
/// truly headless build (no UI required for the server to function).
/// </summary>
public class PongServerUI : MonoBehaviour
{
    public PongServer Server;
    public TMPro.TMP_InputField InpPort;
    public TMPro.TMP_Text TxtStatus;

    public GameObject BtnListen;
    public GameObject PanListening;

    void Start()
    {
        if (Server != null && InpPort != null) InpPort.text = Server.ListenPort.ToString();
    }

    void Update()
    {
        if (Server == null) return;
        if (BtnListen != null) BtnListen.SetActive(!Server.IsListening);
        if (PanListening != null) PanListening.SetActive(Server.IsListening);
        if (TxtStatus != null) {
            TxtStatus.text = Server.IsListening
                ? ("Listening on " + Server.ListenPort + " - " + Server.ConnectionCount + " client(s) connected")
                : "Stopped";
        }
    }

    public void Listen()
    {
        if (Server == null) return;
        if (InpPort != null && int.TryParse(InpPort.text, out int port)) Server.ListenPort = port;
        Server.Listen();
    }

    public void Close()
    {
        if (Server == null) return;
        Server.Close();
    }
}
