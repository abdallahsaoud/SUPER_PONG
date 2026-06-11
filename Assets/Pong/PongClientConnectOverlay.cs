using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Simple connect screen for player builds (no TMP UI required).
/// Shown until TCP connection succeeds. While connected, shows "You've lost" and
/// "Match over" panels when the local player is eliminated or the round ends.
/// </summary>
public class PongClientConnectOverlay : MonoBehaviour
{
    const int HealthEliminated = 2;
    const int MinPlayersToStart = 2;

    public PongClient Client;
    public PongNetView View;

    [TextArea(2, 4)]
    public string HelpText =
        "Enter the host PC's LAN IP (not 127.0.0.1).\n" +
        "Host: run Server build first, note IP from console (ipconfig / ifconfig).\n" +
        "Same Wi‑Fi, firewall must allow TCP port 25000 on the host.";

    string _ip = string.Empty;
    string _portText = PongNetworkUtil.DefaultPort.ToString();
    string _playerName = "Player";
    string _status = "Not connected";
    bool _stylesReady;

    bool _lost;
    bool _matchOver;
    bool _spectating;
    int _winnerLine = -1;
    string _winnerName = string.Empty;
    int _restartCountdownSeconds;
    bool _postGameSent;
    bool _awaitingNextMatch;
    bool _freshSessionAssignPending;

    PongClient _boundClient;

    GUIStyle _boxStyle;
    GUIStyle _labelStyle;
    GUIStyle _fieldStyle;
    GUIStyle _buttonStyle;
    GUIStyle _titleStyle;

    public void Initialize(PongClient client, PongNetView view, string defaultIp, int defaultPort)
    {
        Client = client;
        View = view;
        _ip = PongNetworkUtil.NormalizeIp(defaultIp);
        _portText = defaultPort.ToString();
        BindClientEvents();
    }

    void OnEnable() => BindClientEvents();

    void OnDisable() => UnbindClientEvents();

    void BindClientEvents()
    {
        if (_boundClient == Client) return;
        UnbindClientEvents();
        if (Client == null) return;

        Client.OnDamage += HandleDamage;
        Client.OnWin += HandleWin;
        Client.OnReset += HandleReset;
        Client.OnAssign += HandleAssign;
        Client.OnNames += HandleNames;
        Client.OnCountdown += HandleCountdown;
        _boundClient = Client;
    }

    void UnbindClientEvents()
    {
        if (_boundClient == null) return;
        _boundClient.OnDamage -= HandleDamage;
        _boundClient.OnWin -= HandleWin;
        _boundClient.OnReset -= HandleReset;
        _boundClient.OnAssign -= HandleAssign;
        _boundClient.OnNames -= HandleNames;
        _boundClient.OnCountdown -= HandleCountdown;
        _boundClient = null;
    }

    void ClearMatchUiState()
    {
        _lost = false;
        _matchOver = false;
        _spectating = false;
        _winnerLine = -1;
        _winnerName = string.Empty;
        _restartCountdownSeconds = 0;
        _postGameSent = false;
        _awaitingNextMatch = false;
        _freshSessionAssignPending = false;
    }

    void HandleDamage(int lineIndex, int state)
    {
        if (state < HealthEliminated) return;
        // Once the match is over, the match-over popup takes over: don't re-flag the local
        // player as "lost" (which would otherwise resurface the You've-lost panel).
        if (_matchOver) return;
        if (Client != null && lineIndex == Client.LineIndex) {
            _lost = true;
            _spectating = false;
            NotifyPostGameMenu();
            return;
        }
        SyncLostFromView();
    }

    void HandleWin(int lineIndex)
    {
        _winnerLine = lineIndex;
        _winnerName = Client != null ? Client.LastWinnerName : string.Empty;
        _matchOver = true;
        // The match is over: the "You've lost" popup (with its Keep watching button) must
        // not stay up alongside or behind the match-over popup. Clearing _lost / _spectating
        // here guarantees only the match-over panel renders, even if anything else
        // re-evaluated those flags afterwards.
        _lost = false;
        _spectating = false;
        NotifyPostGameMenu();
    }

    void NotifyPostGameMenu()
    {
        if (Client == null || _postGameSent) return;
        _postGameSent = true;
        Client.SendPostGame();
    }

    void ConfirmReadyForNextMatch()
    {
        if (Client == null) return;
        Client.SendReady();
    }

    void ConfirmSpectateOnly()
    {
        if (Client == null) return;
        Client.SendSpectate();
    }

    void HandleNames(IList<string> playerNames)
    {
        if (_winnerLine >= 0 && Client != null) {
            _winnerName = Client.GetPlayerName(_winnerLine);
        }
    }

    void HandleCountdown(int secondsRemaining)
    {
        _restartCountdownSeconds = secondsRemaining;
    }

    void HandleReset()
    {
        _restartCountdownSeconds = 0;
        // A RESET means the previous round is fully torn down: either the next match is being
        // served or the server fell back to waiting. In both cases the end-of-round menu
        // (lost / match-over / "waiting to restart") must clear so we don't strand the player.
        if (_awaitingNextMatch || !(_lost || _matchOver)) {
            ClearMatchUiState();
            return;
        }
        // Local player lost / match still flagged over but hasn't opted in: keep their menu,
        // just make sure no stale countdown lingers.
    }

    void HandleAssign(int lineIndex, int lineCount)
    {
        // ASSIGN is rebroadcast whenever the roster changes (joins/leaves), including while the
        // end-of-round menu is up. Mid-session ASSIGNs must NOT dismiss that menu — only an
        // actual match restart (RESET) or an explicit player action clears it. Otherwise a
        // player joining the lobby would yank a still-deciding player out of the match-over
        // screen.
        //
        // Exception: the *first* ASSIGN of a fresh session (after a reconnect) means the
        // server is treating us as a brand-new player. Any leftover "You've lost" / match-over
        // flag from the previous session must be wiped now, otherwise the popup would persist
        // even though the server has no idea we are the same person.
        if (_freshSessionAssignPending) {
            _freshSessionAssignPending = false;
            ClearMatchUiState();
        }
    }

    void LeaveServer()
    {
        if (Client != null) Client.Close();
        if (View != null) View.ResetSessionState();
        ClearMatchUiState();
    }

    void Update()
    {
        BindClientEvents();
        if (Client == null) return;

        if (!Client.IsConnected) {
            ClearMatchUiState();
            if (View != null) View.ResetSessionState();
            if (!string.IsNullOrEmpty(Client.LastError)) {
                _status = Client.LastError;
            }
            return;
        }

        if (IsAwaitingMorePlayers()) {
            _status = "Awaiting more players to start…";
        } else if (Client.LineIndex >= 0) {
            _status = "Connected as " + Client.GetPlayerName(Client.LineIndex);
        } else {
            _status = "Connected, waiting for server…";
        }

        SyncLostFromView();
        _restartCountdownSeconds = Client.RestartCountdownSeconds;
    }

    bool IsAwaitingMorePlayers()
    {
        if (Client == null || !Client.IsConnected) return false;
        if (_matchOver) return false;
        if (_lost && !_spectating) return false;
        int count = Client.LineCount > 0 ? Client.LineCount : Client.LastRosterCount;
        return count > 0 && count < MinPlayersToStart;
    }

    void SyncLostFromView()
    {
        if (_matchOver) return;
        if (View != null && View.IsLocalPlayerEliminated()) {
            _lost = true;
            if (!_spectating) NotifyPostGameMenu();
        }
    }

    void OnGUI()
    {
        EnsureStyles();

        bool connected = Client != null && Client.IsConnected;
        if (!connected) {
            DrawConnectPanel();
            return;
        }

        // Always-on escape so no connected state can ever trap the player: even if every other
        // panel is suppressed (e.g. a stale countdown hides the awaiting panel), this stays
        // clickable.
        DrawLeaveCornerButton();

        // Once the round is over, everyone (winner or loser) gets the match-over menu so they
        // can opt into the next match. The "you've lost" panel is only for players eliminated
        // while the round is still being played out by others.
        if (_matchOver) {
            DrawMatchOverPanel();
            return;
        }

        if (_lost && !_spectating) {
            DrawLostPanel();
            return;
        }

        if (IsAwaitingMorePlayers()) {
            DrawAwaitingPlayersPanel();
        }

        if (_restartCountdownSeconds > 0) {
            DrawRestartCountdownPanel();
        }
    }

    void DrawLeaveCornerButton()
    {
        float w = 130f;
        float h = 30f;
        var rect = new Rect(Screen.width - w - 12f, 12f, w, h);
        if (GUI.Button(rect, "Leave server", _buttonStyle)) {
            LeaveServer();
        }
    }

    void DrawRestartCountdownPanel()
    {
        float w = 320f;
        float h = 64f;
        var rect = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.2f, w, h);
        GUI.Box(rect, string.Empty, _boxStyle);

        GUILayout.BeginArea(rect);
        GUILayout.Space(14);
        string text = Client != null && Client.IsJoinLobbyCountdown
            ? "Waiting for potential new players: " + _restartCountdownSeconds + "…"
            : "Match starts in " + _restartCountdownSeconds + "…";
        GUILayout.Label(text, _titleStyle);
        GUILayout.EndArea();
    }

    void DrawAwaitingPlayersPanel()
    {
        // Centered panel with an explicit "Leave server" escape: without it, a player who
        // stayed after an opponent disconnected would be stranded here with no way to act.
        float w = 360f;
        float h = 168f;
        var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        GUI.Box(rect, string.Empty, _boxStyle);

        GUILayout.BeginArea(rect);
        GUILayout.Space(18);
        GUILayout.Label("Awaiting more players to start…", _titleStyle);
        GUILayout.Space(16);
        GUILayout.Label(
            "Waiting for someone else to join before the next match.",
            _labelStyle);
        GUILayout.Space(12);
        if (GUILayout.Button("Leave server", _buttonStyle, GUILayout.Height(36))) {
            LeaveServer();
        }
        GUILayout.EndArea();
    }

    void DrawConnectPanel()
    {
        float w = 420f;
        float h = 340f;
        var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        GUI.Box(rect, "Connect to server", _boxStyle);

        GUILayout.BeginArea(rect);
        GUILayout.Space(28);
        GUILayout.Label(HelpText, _labelStyle);
        GUILayout.Space(8);
        GUILayout.Label("Your name", _labelStyle);
        _playerName = GUILayout.TextField(_playerName, _fieldStyle, GUILayout.Height(28));
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

    void DrawLostPanel()
    {
        float w = 380f;
        float h = 220f;
        var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        GUI.Box(rect, string.Empty, _boxStyle);

        GUILayout.BeginArea(rect);
        GUILayout.Space(16);
        GUILayout.Label("You've lost", _titleStyle);
        GUILayout.Space(12);
        GUILayout.Label(
            "Leave the server or keep watching until this match ends.",
            _labelStyle);
        GUILayout.Space(16);

        if (GUILayout.Button("Leave server", _buttonStyle, GUILayout.Height(36))) {
            LeaveServer();
        }

        GUILayout.Space(8);

        if (GUILayout.Button("Keep watching", _buttonStyle, GUILayout.Height(36))) {
            _spectating = true;
            ConfirmSpectateOnly();
        }

        GUILayout.EndArea();
    }

    void DrawMatchOverPanel()
    {
        float w = 400f;
        float h = 240f;
        var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        GUI.Box(rect, string.Empty, _boxStyle);

        GUILayout.BeginArea(rect);
        GUILayout.Space(16);
        GUILayout.Label(GetMatchOverTitle(), _titleStyle);
        GUILayout.Space(12);
        GUILayout.Label(
            "Leave the server or stay connected for the next match.",
            _labelStyle);
        GUILayout.Space(16);

        if (GUILayout.Button("Leave server", _buttonStyle, GUILayout.Height(36))) {
            LeaveServer();
        }

        GUILayout.Space(8);

        if (_awaitingNextMatch) {
            GUILayout.Label(GetAwaitingNextMatchText(), _labelStyle);
        } else if (GUILayout.Button("Stay for next match", _buttonStyle, GUILayout.Height(36))) {
            ConfirmReadyForNextMatch();
            _awaitingNextMatch = true;
        }

        GUILayout.EndArea();
    }

    string GetAwaitingNextMatchText()
    {
        if (_restartCountdownSeconds > 0) {
            return Client != null && Client.IsJoinLobbyCountdown
                ? "Ready! Waiting for more players: " + _restartCountdownSeconds + "…"
                : "Ready! Match starts in " + _restartCountdownSeconds + "…";
        }
        return "Ready! Waiting for other players to restart…";
    }

    string GetMatchOverTitle()
    {
        if (Client != null && _winnerLine == Client.LineIndex) {
            return "Match over — You win!";
        }
        if (_winnerLine >= 0) {
            string who = !string.IsNullOrEmpty(_winnerName)
                ? _winnerName
                : (Client != null ? Client.GetPlayerName(_winnerLine) : PongProtocol.DefaultPlayerName(_winnerLine));
            return "Match over — " + who + " wins!";
        }
        return "Match over";
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
        // Wipe view-side state so a stale health flag from a previous session can't make the
        // freshly-reconnected player immediately appear "eliminated".
        if (View != null) View.ResetSessionState();
        ClearMatchUiState();
        // Arm a one-shot clear that triggers on the first ASSIGN of this new session, so any
        // leftover lost/match-over state from before the disconnect is guaranteed to be wiped
        // once the server has actually accepted us as a new player.
        _freshSessionAssignPending = true;

        if (Client.Connect()) {
            Client.SendName(_playerName);
            Client.SendReady();
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
        _titleStyle = new GUIStyle(GUI.skin.label) {
            fontSize = 20,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
        };
        _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
        _fieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = 14 };
        _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 15 };
    }
}
