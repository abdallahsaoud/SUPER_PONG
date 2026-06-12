using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Simple connect screen for player builds (no TMP UI required).
/// Shown until TCP connection succeeds. While connected, shows "You've lost" and
/// "Match over" panels when the local player is eliminated or the round ends.
/// </summary>
public class PongClientConnectOverlay : MonoBehaviour
{
    const int MinPlayersToStart = 2;

    public PongClient Client;
    public PongNetView View;

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

    // 8-bit pixel theme palette.
    static readonly Color PixelNavy = new Color(0.102f, 0.102f, 0.180f);      // panel fill
    static readonly Color PixelInputNavy = new Color(0.059f, 0.059f, 0.118f); // input fill
    static readonly Color PixelBlue = new Color(0.310f, 0.357f, 1f);          // accent / shadow
    static readonly Color PixelYellow = new Color(1f, 0.882f, 0.302f);        // titles
    static readonly Color PixelRed = new Color(1f, 0.365f, 0.365f);           // danger titles
    static readonly Color PixelTextLight = new Color(0.788f, 0.788f, 1f);     // body text
    static readonly Color PixelTextMuted = new Color(0.561f, 0.608f, 1f);     // field labels

    Texture2D _texPanel;
    Texture2D _texShadow;
    Texture2D _texButtonPrimary;
    Texture2D _texButtonSecondary;
    Texture2D _texInput;

    GUIStyle _boxStyle;
    GUIStyle _labelStyle;
    GUIStyle _fieldLabelStyle;
    GUIStyle _fieldStyle;
    GUIStyle _buttonStyle;
    GUIStyle _secondaryButtonStyle;
    GUIStyle _titleStyle;
    GUIStyle _dangerTitleStyle;

    Font _pixelFont;
    float _uiScale = 1f;

    Texture2D _swatchTexture;

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
        if (state < CircleArenaConfig.HealthEliminated) return;
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
        // Server treats SPECTATE and POSTGAME identically; the local _spectating flag is what
        // suppresses the "You've lost" panel until the round ends.
        Client.SendPostGame();
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

    void HandleAssign(int lineIndex, int lineCount, float ringAngleRad)
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
            string baseStatus = "Connected as " + Client.GetPlayerName(Client.LineIndex);
            string colorName = CircleArenaConfig.GetPaletteColorName(Client.MyColorSlot);
            _status = string.IsNullOrEmpty(colorName)
                ? baseStatus
                : baseStatus + " — you are " + colorName;
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
        float w = Scaled(170f);
        float h = Scaled(42f);
        float margin = Scaled(16f);
        var rect = new Rect(Screen.width - w - margin, margin, w, h);
        if (GUI.Button(rect, "Leave server", _buttonStyle)) {
            LeaveServer();
        }
    }

    void DrawRestartCountdownPanel()
    {
        float w = Scaled(480f);
        float h = Scaled(150f);
        var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        DrawPixelPanel(rect);

        BeginPanelContent(rect, 18f, 22f);
        string text = Client != null && Client.IsJoinLobbyCountdown
            ? "Waiting for potential new players: " + _restartCountdownSeconds + "…"
            : "Match starts in " + _restartCountdownSeconds + "…";
        GUILayout.Label(text.ToUpperInvariant(), _titleStyle);
        EndPanelContent(22f);
    }

    void DrawAwaitingPlayersPanel()
    {
        // Centered panel with an explicit "Leave server" escape: without it, a player who
        // stayed after an opponent disconnected would be stranded here with no way to act.
        float w = Scaled(480f);
        float h = Scaled(270f);
        var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        DrawPixelPanel(rect);

        BeginPanelContent(rect);
        GUILayout.Label("LOBBY", _titleStyle);
        GUILayout.Space(Scaled(16));
        GUILayout.Label(
            "Waiting for someone else to join before the next match.",
            _labelStyle);
        GUILayout.Space(Scaled(16));
        if (GUILayout.Button("Leave server", _secondaryButtonStyle, GUILayout.Height(Scaled(50)))) {
            LeaveServer();
        }
        EndPanelContent();
    }

    void DrawConnectPanel()
    {
        float w = Scaled(580f);
        float h = Scaled(450f);
        var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        DrawPixelPanel(rect);

        BeginPanelContent(rect);
        GUILayout.Label("★ Super Pong ★", _titleStyle);
        GUILayout.Space(Scaled(14));
        GUILayout.Label("YOUR NAME", _fieldLabelStyle);
        _playerName = GUILayout.TextField(_playerName, _fieldStyle, GUILayout.Height(Scaled(40)));
        GUILayout.Label("SERVER IP", _fieldLabelStyle);
        _ip = GUILayout.TextField(_ip, _fieldStyle, GUILayout.Height(Scaled(40)));
        GUILayout.Label("PORT", _fieldLabelStyle);
        _portText = GUILayout.TextField(_portText, _fieldStyle, GUILayout.Height(Scaled(40)));
        GUILayout.Space(Scaled(14));

        if (GUILayout.Button("Connect", _buttonStyle, GUILayout.Height(Scaled(50)))) {
            TryConnect();
        }

        GUILayout.Space(Scaled(8));
        GUILayout.Label(_status, _labelStyle);
        EndPanelContent();
    }

    void DrawLostPanel()
    {
        float w = Scaled(500f);
        float h = Scaled(340f);
        var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        DrawPixelPanel(rect);

        DrawLocalColorSwatch(rect);

        BeginPanelContent(rect, 44f);
        GUILayout.Label("Game Over", _dangerTitleStyle);
        GUILayout.Space(Scaled(16));
        GUILayout.Label(
            "Leave the server or keep watching until this match ends.",
            _labelStyle);
        GUILayout.Space(Scaled(22));

        if (GUILayout.Button("Keep watching", _buttonStyle, GUILayout.Height(Scaled(50)))) {
            _spectating = true;
            ConfirmSpectateOnly();
        }

        GUILayout.Space(Scaled(11));

        if (GUILayout.Button("Leave server", _secondaryButtonStyle, GUILayout.Height(Scaled(50)))) {
            LeaveServer();
        }

        EndPanelContent();
    }

    void DrawMatchOverPanel()
    {
        float w = Scaled(520f);
        float h = Scaled(360f);
        var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
        DrawPixelPanel(rect);

        DrawLocalColorSwatch(rect);

        BeginPanelContent(rect, 44f);
        GUILayout.Label(GetMatchOverTitle().ToUpperInvariant(), _titleStyle);
        GUILayout.Space(Scaled(16));
        GUILayout.Label(
            "Leave the server or stay connected for the next match.",
            _labelStyle);
        GUILayout.Space(Scaled(22));

        if (GUILayout.Button("Leave server", _secondaryButtonStyle, GUILayout.Height(Scaled(50)))) {
            LeaveServer();
        }

        GUILayout.Space(Scaled(11));

        if (_awaitingNextMatch) {
            GUILayout.Label(GetAwaitingNextMatchText(), _labelStyle);
        } else if (GUILayout.Button("Stay for next match", _buttonStyle, GUILayout.Height(Scaled(50)))) {
            ConfirmReadyForNextMatch();
            _awaitingNextMatch = true;
        }

        EndPanelContent();
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

    void DrawLocalColorSwatch(Rect panelRect)
    {
        if (Client == null) return;
        int slot = Client.MyColorSlot;
        if (slot < 0) return;

        if (_swatchTexture == null) {
            _swatchTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false) {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
            };
            _swatchTexture.SetPixel(0, 0, Color.white);
            _swatchTexture.Apply();
        }

        Color color = CircleArenaConfig.GetPaletteColor(slot);
        string label = CircleArenaConfig.GetPaletteColorName(slot);

        float padding = Scaled(14f);
        float swatchSize = Scaled(24f);
        var swatchRect = new Rect(panelRect.x + padding, panelRect.y + padding, swatchSize, swatchSize);

        Color prev = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(swatchRect, _swatchTexture, ScaleMode.StretchToFill);
        GUI.color = prev;

        if (!string.IsNullOrEmpty(label)) {
            float gap = Scaled(8f);
            var labelRect = new Rect(
                swatchRect.xMax + gap,
                swatchRect.y - Scaled(3f),
                panelRect.width - swatchSize - padding * 2f - gap,
                swatchSize + Scaled(6f));
            GUI.Label(labelRect, "You: " + label, _labelStyle);
        }
    }

    void OnDestroy()
    {
        if (_swatchTexture != null) {
            Destroy(_swatchTexture);
            _swatchTexture = null;
        }
    }

    float Scaled(float value) => value * _uiScale;

    void EnsureStyles()
    {
        if (_stylesReady) return;
        _stylesReady = true;

        // Tie overlay size to the visible arena ring rather than an arbitrary screen
        // fraction: the orthographic camera maps 2*GetCameraOrthographicSize() world
        // units to Screen.height pixels, so the arena circle (diameter = 2*Radius)
        // occupies (Radius / orthoSize) * Screen.height pixels. Scale so even the
        // smallest panel (480px reference) is at least that big, never shrinking
        // below the 1x baseline.
        float circleDiameterPx = (CircleArenaConfig.Radius / CircleArenaConfig.GetCameraOrthographicSize()) * Screen.height;
        const float ReferencePanelWidth = 480f;
        _uiScale = Mathf.Max(1f, circleDiameterPx / ReferencePanelWidth);

        _pixelFont = Resources.Load<Font>("Fonts/VT323-Regular");

        int PanelTexSize = Mathf.RoundToInt(24 * _uiScale);
        int PanelBorderPx = Mathf.RoundToInt(6 * _uiScale);
        int ControlTexSize = Mathf.RoundToInt(20 * _uiScale);
        int ControlBorderPx = Mathf.RoundToInt(3 * _uiScale);
        _texPanel = MakeBorderedTexture(PanelTexSize, PanelBorderPx, PixelNavy, Color.white);
        _texShadow = MakeSolidTexture(PixelBlue);
        _texButtonPrimary = MakeBorderedTexture(ControlTexSize, ControlBorderPx, PixelBlue, Color.white);
        _texButtonSecondary = MakeBorderedTexture(ControlTexSize, ControlBorderPx, PixelNavy, Color.white);
        _texInput = MakeBorderedTexture(ControlTexSize, ControlBorderPx, PixelInputNavy, PixelBlue);

        _boxStyle = new GUIStyle {
            normal = { background = _texPanel },
            border = new RectOffset(PanelBorderPx, PanelBorderPx, PanelBorderPx, PanelBorderPx),
        };

        _titleStyle = new GUIStyle(GUI.skin.label) {
            font = _pixelFont,
            fontSize = Mathf.RoundToInt(32 * _uiScale),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true,
            normal = { textColor = PixelYellow },
        };
        _dangerTitleStyle = new GUIStyle(_titleStyle) {
            normal = { textColor = PixelRed },
        };

        _labelStyle = new GUIStyle(GUI.skin.label) {
            font = _pixelFont,
            fontSize = Mathf.RoundToInt(19 * _uiScale),
            wordWrap = true,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = PixelTextLight },
        };
        _fieldLabelStyle = new GUIStyle(GUI.skin.label) {
            font = _pixelFont,
            fontSize = Mathf.RoundToInt(16 * _uiScale),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            normal = { textColor = PixelTextMuted },
        };

        int fieldPadH = Mathf.RoundToInt(10 * _uiScale);
        int fieldPadV = Mathf.RoundToInt(6 * _uiScale);
        _fieldStyle = new GUIStyle(GUI.skin.textField) {
            font = _pixelFont,
            fontSize = Mathf.RoundToInt(20 * _uiScale),
            border = new RectOffset(ControlBorderPx, ControlBorderPx, ControlBorderPx, ControlBorderPx),
            padding = new RectOffset(fieldPadH, fieldPadH, fieldPadV, fieldPadV),
            normal = { background = _texInput, textColor = Color.white },
            focused = { background = _texInput, textColor = Color.white },
        };

        _buttonStyle = new GUIStyle(GUI.skin.button) {
            font = _pixelFont,
            fontSize = Mathf.RoundToInt(20 * _uiScale),
            fontStyle = FontStyle.Bold,
            border = new RectOffset(ControlBorderPx, ControlBorderPx, ControlBorderPx, ControlBorderPx),
            normal = { background = _texButtonPrimary, textColor = Color.white },
            hover = { background = _texButtonPrimary, textColor = Color.white },
            active = { background = _texButtonPrimary, textColor = PixelYellow },
        };
        _secondaryButtonStyle = new GUIStyle(_buttonStyle) {
            normal = { background = _texButtonSecondary, textColor = Color.white },
            hover = { background = _texButtonSecondary, textColor = Color.white },
            active = { background = _texButtonSecondary, textColor = PixelYellow },
        };
    }

    static Texture2D MakeSolidTexture(Color color)
    {
        // Our palette constants are hex-derived sRGB values (e.g. 0.102 == #1a). In this
        // project's Linear color space, IMGUI gamma-encodes texture values for display, so
        // feeding it the sRGB value directly re-encodes it again and washes it out (navy ->
        // mid-gray). Pre-convert with .linear so the single encode lands back on the hex color.
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        Color c = color.linear;
        tex.SetPixels(new[] { c, c, c, c });
        tex.Apply(false);
        tex.filterMode = FilterMode.Point;
        return tex;
    }

    static Texture2D MakeBorderedTexture(int size, int borderPx, Color fill, Color border)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        Color fillLinear = fill.linear;
        Color borderLinear = border.linear;
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++) {
            for (int x = 0; x < size; x++) {
                bool edge = x < borderPx || y < borderPx || x >= size - borderPx || y >= size - borderPx;
                pixels[y * size + x] = edge ? borderLinear : fillLinear;
            }
        }
        tex.SetPixels(pixels);
        tex.Apply(false);
        tex.filterMode = FilterMode.Point;
        return tex;
    }

    // Draws the chunky pixel-art panel: an offset blue "shadow" block behind a
    // white-bordered navy box.
    void DrawPixelPanel(Rect rect)
    {
        float offset = Scaled(11f);
        var shadowRect = new Rect(rect.x + offset, rect.y + offset, rect.width, rect.height);
        GUI.DrawTexture(shadowRect, _texShadow);
        GUI.Box(rect, GUIContent.none, _boxStyle);
    }

    void BeginPanelContent(Rect rect, float topPad = 27f, float sidePad = 27f)
    {
        GUILayout.BeginArea(rect);
        GUILayout.Space(Scaled(topPad));
        GUILayout.BeginHorizontal();
        GUILayout.Space(Scaled(sidePad));
        GUILayout.BeginVertical();
    }

    void EndPanelContent(float sidePad = 27f)
    {
        GUILayout.EndVertical();
        GUILayout.Space(Scaled(sidePad));
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }
}
