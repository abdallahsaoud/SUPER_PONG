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
    public TMPro.TMP_InputField InpPlayerName;
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

    [Header("Lost panel")]
    public GameObject LostPanel;
    public TMPro.TMP_Text TxtLost;

    [Header("Lobby")]
    public GameObject AwaitingPanel;
    public TMPro.TMP_Text TxtRestartCountdown;

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
        if (LostPanel != null) LostPanel.SetActive(false);
        if (AwaitingPanel != null) AwaitingPanel.SetActive(false);
    }

    void OnEnable()
    {
        if (Client == null) return;
        Client.OnAssign += HandleAssign;
        Client.OnRoster += HandleRoster;
        Client.OnCountdown += HandleCountdown;
        Client.OnScore += HandleScore;
        Client.OnDamage += HandleDamage;
        Client.OnWin += HandleWin;
        Client.OnReset += HandleReset;
    }

    void OnDisable()
    {
        if (Client == null) return;
        Client.OnAssign -= HandleAssign;
        Client.OnRoster -= HandleRoster;
        Client.OnCountdown -= HandleCountdown;
        Client.OnScore -= HandleScore;
        Client.OnDamage -= HandleDamage;
        Client.OnWin -= HandleWin;
        Client.OnReset -= HandleReset;
    }

    void Update()
    {
        if (Client == null) return;
        bool connected = Client.IsConnected;
        if (ConnectPanel != null) ConnectPanel.SetActive(!connected);
        if (GamePanel != null) GamePanel.SetActive(connected);
        RefreshAwaitingPanel(connected);
        RefreshRestartCountdown(connected);
        if (TxtStatus != null) {
            if (!connected) {
                TxtStatus.text = "Not connected";
            } else if (IsAwaitingMorePlayers()) {
                TxtStatus.text = "Awaiting more players to start…";
            } else {
                TxtStatus.text = "Connected as " + (Client != null && _myLine >= 0
                    ? Client.GetPlayerName(_myLine)
                    : "?");
            }
        }
        RefreshScores();
    }

    void HandleRoster(int lineCount)
    {
        _lineCount = lineCount;
        RefreshAwaitingPanel(Client != null && Client.IsConnected);
    }

    bool IsAwaitingMorePlayers()
    {
        return _lineCount > 0 && _lineCount < 2;
    }

    void RefreshAwaitingPanel(bool connected)
    {
        if (AwaitingPanel == null) return;
        bool show = connected && IsAwaitingMorePlayers()
            && (WinPanel == null || !WinPanel.activeSelf)
            && (LostPanel == null || !LostPanel.activeSelf);
        AwaitingPanel.SetActive(show);
    }

    void HandleCountdown(int secondsRemaining) => RefreshRestartCountdown(Client != null && Client.IsConnected);

    void RefreshRestartCountdown(bool connected)
    {
        int seconds = connected && Client != null ? Client.RestartCountdownSeconds : 0;
        if (TxtRestartCountdown == null) return;
        if (seconds <= 0) {
            TxtRestartCountdown.gameObject.SetActive(false);
            return;
        }
        TxtRestartCountdown.gameObject.SetActive(true);
        TxtRestartCountdown.text = Client != null && Client.IsJoinLobbyCountdown
            ? "Waiting for potential new players: " + seconds + "…"
            : "Match starts in " + seconds + "…";
    }

    public void Connect()
    {
        if (Client == null) return;
        if (InpIP != null) Client.DestinationIP = PongNetworkUtil.NormalizeIp(InpIP.text);
        if (InpPort != null && int.TryParse(InpPort.text, out int port)) Client.DestinationPort = port;
        Client.Close();
        if (Client.Connect()) {
            string name = InpPlayerName != null ? InpPlayerName.text : string.Empty;
            Client.SendName(name);
            Client.SendReady();
        }
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

    void HandleAssign(int lineIndex, int lineCount, float ringAngleRad)
    {
        _myLine = lineIndex;
        _lineCount = lineCount;
        if (_scores.Length != lineCount) _scores = new int[lineCount];
        if (WinPanel != null) WinPanel.SetActive(false);
        if (LostPanel != null) LostPanel.SetActive(false);
        RefreshAwaitingPanel(Client != null && Client.IsConnected);
    }

    void HandleDamage(int lineIndex, int state)
    {
        if (lineIndex != _myLine || state < 2) return;
        if (Client != null) Client.SendPostGame();
        ShowLostPanel();
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
        if (AwaitingPanel != null) AwaitingPanel.SetActive(false);
        if (Client != null) Client.SendPostGame();
        if (lineIndex == _myLine) {
            if (LostPanel != null) LostPanel.SetActive(false);
            if (WinPanel != null) WinPanel.SetActive(true);
            if (TxtWin != null) TxtWin.text = "You win!";
            return;
        }

        ShowLostPanel();
        if (WinPanel != null) WinPanel.SetActive(true);
        if (TxtWin != null) {
            string who = Client != null && !string.IsNullOrEmpty(Client.LastWinnerName)
                ? Client.LastWinnerName
                : (Client != null
                    ? Client.GetPlayerName(lineIndex)
                    : PongProtocol.DefaultPlayerName(lineIndex));
            TxtWin.text = who + " wins!";
        }
    }

    void HandleReset()
    {
        if (WinPanel != null) WinPanel.SetActive(false);
        if (LostPanel != null) LostPanel.SetActive(false);
        RefreshAwaitingPanel(Client != null && Client.IsConnected);
        RefreshRestartCountdown(Client != null && Client.IsConnected);
    }

    void ShowLostPanel()
    {
        if (LostPanel != null) LostPanel.SetActive(true);
        if (TxtLost != null) TxtLost.text = "You've lost";
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
