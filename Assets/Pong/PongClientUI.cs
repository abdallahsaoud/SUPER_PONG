using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Client-side UI:
///   - Pre-connect: IP/port fields + Connect button (reuses the demo TCP UI pattern).
///   - In-game: shows assigned line, live scores, and a win panel on WIN.
/// Adapted from Assets/Demos/TCP/TCPClientUI.cs + Assets/Demos/Pong/PongWinUI.cs.
/// </summary>
public class PongClientUI : MonoBehaviour
{
    public PongClient Client;

    [Header("Connect panel")]
    public TMPro.TMP_InputField InpIP;
    public TMPro.TMP_InputField InpPort;
    public GameObject ConnectPanel;
    public GameObject GamePanel;

    [Header("Status")]
    public TMPro.TMP_Text TxtStatus;
    public TMPro.TMP_Text TxtScores;

    [Header("Win panel")]
    public GameObject WinPanel;
    public TMPro.TMP_Text TxtWin;

    int[] _scores = new int[0];
    int _myLine = -1;
    int _lineCount = 0;

    void Start()
    {
        if (Client != null) {
            if (InpIP != null) InpIP.text = Client.DestinationIP;
            if (InpPort != null) InpPort.text = Client.DestinationPort.ToString();
        }
        if (WinPanel != null) WinPanel.SetActive(false);
    }

    void OnEnable()
    {
        if (Client == null) return;
        Client.OnAssign += HandleAssign;
        Client.OnScore += HandleScore;
        Client.OnWin += HandleWin;
        Client.OnReset += HandleReset;
    }

    void OnDisable()
    {
        if (Client == null) return;
        Client.OnAssign -= HandleAssign;
        Client.OnScore -= HandleScore;
        Client.OnWin -= HandleWin;
        Client.OnReset -= HandleReset;
    }

    void Update()
    {
        if (Client == null) return;
        bool connected = Client.IsConnected;
        if (ConnectPanel != null) ConnectPanel.SetActive(!connected);
        if (GamePanel != null) GamePanel.SetActive(connected);
        if (TxtStatus != null) {
            TxtStatus.text = connected
                ? ("Connected as line " + (_myLine >= 0 ? _myLine.ToString() : "?"))
                : "Not connected";
        }
        RefreshScores();
    }

    public void Connect()
    {
        if (Client == null) return;
        if (InpIP != null) Client.DestinationIP = PongNetworkUtil.NormalizeIp(InpIP.text);
        if (InpPort != null && int.TryParse(InpPort.text, out int port)) Client.DestinationPort = port;
        Client.Close();
        Client.Connect();
    }

    public void Disconnect()
    {
        if (Client == null) return;
        Client.Close();
    }

    public void Replay()
    {
        // Simple replay: reload the current scene locally to reconnect.
        if (WinPanel != null) WinPanel.SetActive(false);
        SceneManager.LoadScene(SceneManager.GetActiveScene().name);
    }

    void HandleAssign(int lineIndex, int lineCount)
    {
        _myLine = lineIndex;
        _lineCount = lineCount;
        if (_scores.Length != lineCount) _scores = new int[lineCount];
        if (WinPanel != null) WinPanel.SetActive(false);
    }

    void HandleScore(int lineIndex, int score)
    {
        if (_scores.Length <= lineIndex) {
            var resized = new int[lineIndex + 1];
            for (int i = 0; i < _scores.Length; i++) resized[i] = _scores[i];
            _scores = resized;
        }
        _scores[lineIndex] = score;
    }

    void HandleWin(int lineIndex)
    {
        if (WinPanel != null) WinPanel.SetActive(true);
        if (TxtWin != null) {
            string who = (lineIndex == _myLine) ? "You win!" : ("Line " + lineIndex + " wins!");
            TxtWin.text = who;
        }
    }

    void HandleReset()
    {
        if (WinPanel != null) WinPanel.SetActive(false);
    }

    void RefreshScores()
    {
        if (TxtScores == null) return;
        if (_scores.Length == 0) { TxtScores.text = string.Empty; return; }
        var sb = new System.Text.StringBuilder(32);
        for (int i = 0; i < _scores.Length; i++) {
            if (i > 0) sb.Append("  |  ");
            sb.Append("L").Append(i).Append(": ").Append(_scores[i]);
        }
        TxtScores.text = sb.ToString();
    }
}
