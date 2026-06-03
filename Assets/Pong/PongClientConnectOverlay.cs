using UnityEngine;

/// <summary>
/// Simple connect screen for player builds (no TMP UI required).
/// Shown until TCP connection succeeds.
/// </summary>
public class PongClientConnectOverlay : MonoBehaviour
{
    public PongClient Client;
    public PongNetView View;

    [TextArea(2, 4)]
    public string HelpText =
        "Enter the host PC's LAN IP (not 127.0.0.1).\n" +
        "Host: run Server build first, note IP from console (ipconfig / ifconfig).\n" +
        "Same Wi‑Fi, firewall must allow TCP port 25000 on the host.";

    string _ip = string.Empty;
    string _portText = PongNetworkUtil.DefaultPort.ToString();
    string _status = "Not connected";
    bool _stylesReady;

    GUIStyle _boxStyle;
    GUIStyle _labelStyle;
    GUIStyle _fieldStyle;
    GUIStyle _buttonStyle;

    public void Initialize(PongClient client, PongNetView view, string defaultIp, int defaultPort)
    {
        Client = client;
        View = view;
        _ip = PongNetworkUtil.NormalizeIp(defaultIp);
        _portText = defaultPort.ToString();
    }

    void Update()
    {
        if (Client == null) return;
        if (Client.IsConnected) {
            _status = Client.LineIndex >= 0
                ? "Connected (line " + Client.LineIndex + ")"
                : "Connected, waiting for server…";
            return;
        }

        if (!string.IsNullOrEmpty(Client.LastError)) {
            _status = Client.LastError;
        }
    }

    void OnGUI()
    {
        if (Client != null && Client.IsConnected) return;

        EnsureStyles();

        float w = 420f;
        float h = 300f;
        var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        GUI.Box(rect, "Connect to server", _boxStyle);

        GUILayout.BeginArea(rect);
        GUILayout.Space(28);
        GUILayout.Label(HelpText, _labelStyle);
        GUILayout.Space(8);
        GUILayout.Label("Server IP", _labelStyle);
        _ip = GUILayout.TextField(_ip, _fieldStyle, GUILayout.Height(28));
        GUILayout.Label("Port", _labelStyle);
        _portText = GUILayout.TextField(_portText, _fieldStyle, GUILayout.Height(28));
        GUILayout.Space(8);

        if (GUILayout.Button("Connect", _buttonStyle, GUILayout.Height(36))) {
            TryConnect();
        }

        GUILayout.Space(6);
        GUILayout.Label(_status, _labelStyle);
        GUILayout.EndArea();
    }

    void TryConnect()
    {
        if (Client == null) return;

        string ip = PongNetworkUtil.NormalizeIp(_ip);
        if (!PongNetworkUtil.IsUsableClientTarget(ip)) {
            _status = "Invalid IP. Use the host's LAN address (e.g. 192.168.1.42).";
            return;
        }

        if (!int.TryParse(_portText, out int port) || port <= 0 || port > 65535) {
            _status = "Invalid port.";
            return;
        }

        Client.DestinationIP = ip;
        Client.DestinationPort = port;
        Client.Close();

        if (Client.Connect()) {
            _status = "Connected, syncing…";
            if (View != null) View.ForceBindAndSync();
        } else {
            _status = Client.LastError;
        }
    }

    void EnsureStyles()
    {
        if (_stylesReady) return;
        _stylesReady = true;

        _boxStyle = new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.UpperCenter };
        _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
        _fieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = 14 };
        _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 15 };
    }
}
