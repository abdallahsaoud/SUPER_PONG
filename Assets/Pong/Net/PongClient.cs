using UnityEngine;
using System.Net.Sockets;
using System.Collections.Generic;

/// <summary>
/// TCP client for Multiplayer Pong. One persistent connection to the server.
///
/// Outgoing: PADDLE <y> (sent by PongNetPaddle ~30/s).
/// Incoming: ASSIGN / STATE / SCORE / DAMAGE / WIN / RESET (parsed and exposed
/// as typed events for PongNetView / PongNetPaddle / UI to consume).
///
/// Adapted from Assets/Demos/TCP/TCPClient.cs with newline message framing.
/// </summary>
public class PongClient : MonoBehaviour
{
    public string DestinationIP = "127.0.0.1";
    public int DestinationPort = 25000;
    [Tooltip("Enable throttled client-side debug logs for incoming messages.")]
    public bool DebugNetworkLogs = false;
    [Tooltip("Max debug log frequency in logs/second.")]
    public float DebugLogRate = 1f;

    TcpClient _tcp;
    readonly PongMessageBuffer _buffer = new PongMessageBuffer();
    readonly byte[] _readBuffer = new byte[4096];
    float _nextDebugStateLogTime;
    // #region agent log
    float _dbgLastStateFrameTime;
    // #endregion

    public bool IsConnected => _tcp != null && _tcp.Connected;

    // Typed events. The values arrive parsed so the rest of the client code stays simple.
    public delegate void AssignHandler(int lineIndex, int lineCount);
    public delegate void StateHandler(Vector2 ballPos, IList<float> paddleYs);
    public delegate void ScoreHandler(int lineIndex, int score);
    public delegate void DamageHandler(int lineIndex, int state);
    public delegate void WinHandler(int lineIndex);
    public delegate void ResetHandler();
    public delegate void GeometryHandler(IList<float> lineXs);
    public delegate void RosterHandler(int lineCount);
    public delegate void NamesHandler(IList<string> playerNames);
    public delegate void ColorsHandler(IList<int> colorSlots);
    public delegate void CountdownHandler(int secondsRemaining);

    public AssignHandler   OnAssign;
    public RosterHandler   OnRoster;
    public StateHandler    OnState;
    public ScoreHandler    OnScore;
    public DamageHandler   OnDamage;
    public WinHandler      OnWin;
    public ResetHandler    OnReset;
    public GeometryHandler OnGeometry;
    public NamesHandler    OnNames;
    public ColorsHandler   OnColors;
    public CountdownHandler OnCountdown;

    string[] _playerNames = new string[0];
    int[] _colorSlots = new int[0];

    /// <summary>Palette slot for a given line index, or -1 if unknown.</summary>
    public int GetColorSlot(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= _colorSlots.Length) return -1;
        return _colorSlots[lineIndex];
    }

    /// <summary>Palette slot for the local player (-1 until COLORS arrives).</summary>
    public int MyColorSlot => GetColorSlot(LineIndex);

    /// <summary>Seconds until the next match starts (0 = no countdown shown).</summary>
    public int RestartCountdownSeconds { get; private set; }
    public bool IsJoinLobbyCountdown { get; private set; }

    /// <summary>Assigned line index after ASSIGN arrives (-1 until then).</summary>
    public int LineIndex { get; private set; } = -1;
    /// <summary>Total line count in the match (after ASSIGN arrives, 0 until then).</summary>
    public int LineCount { get; private set; } = 0;

    /// <summary>Last roster size received (for replay when view subscribes late).</summary>
    public int LastRosterCount { get; private set; }

    /// <summary>Human-readable result of the last Connect() attempt.</summary>
    public string LastError { get; private set; } = string.Empty;
    public string LastWinnerName { get; private set; } = string.Empty;

    public string GetPlayerName(int lineIndex)
    {
        if (lineIndex >= 0 && lineIndex < _playerNames.Length) {
            string name = _playerNames[lineIndex];
            if (!string.IsNullOrEmpty(name)) return name;
        }
        return PongProtocol.DefaultPlayerName(lineIndex);
    }

    public void SendName(string displayName)
    {
        if (!IsConnected) return;
        SendFramed(PongProtocol.FormatName(displayName));
    }

    /// <summary>True when the server counts this client toward the next match.</summary>
    public bool ParticipatesInGame { get; private set; } = true;

    public void SendReady()
    {
        if (!IsConnected) return;
        ParticipatesInGame = true;
        SendFramed(PongProtocol.FormatReady());
    }

    public void SendPostGame()
    {
        if (!IsConnected) return;
        ParticipatesInGame = false;
        SendFramed(PongProtocol.FormatPostGame());
    }

    public void SendSpectate()
    {
        if (!IsConnected) return;
        ParticipatesInGame = false;
        SendFramed(PongProtocol.FormatSpectate());
    }

    public bool Connect()
    {
        if (_tcp != null) {
            LastError = "Already connected. Disconnect first.";
            Debug.LogWarning("PongClient: " + LastError);
            return false;
        }

        DestinationIP = PongNetworkUtil.NormalizeIp(DestinationIP);
        if (!PongNetworkUtil.IsUsableClientTarget(DestinationIP)) {
            LastError = "Invalid server IP.";
            Debug.LogWarning("PongClient: " + LastError);
            return false;
        }

        try {
            _tcp = new TcpClient();
            _tcp.ReceiveTimeout = 5000;
            _tcp.SendTimeout = 5000;
            // Disable Nagle's algorithm so small PADDLE/STATE frames are sent immediately
            // instead of being batched (Nagle + delayed-ACK cause the laggy movement).
            _tcp.NoDelay = true;
            var result = _tcp.BeginConnect(DestinationIP, DestinationPort, null, null);
            bool completed = result.AsyncWaitHandle.WaitOne(System.TimeSpan.FromSeconds(5));
            if (!completed || !_tcp.Connected) {
                throw new System.TimeoutException(
                    "Timeout — no server at " + DestinationIP + ":" + DestinationPort
                    + ". Check IP, port, firewall, and that the host server is running.");
            }
            _tcp.EndConnect(result);
            LastError = string.Empty;
            Debug.Log("PongClient connected to " + DestinationIP + ":" + DestinationPort);
            return true;
        } catch (System.Exception ex) {
            LastError = ex.Message;
            Debug.LogWarning("PongClient connect error: " + ex.Message);
            CloseInternal();
            return false;
        }
    }

    public void Close() => CloseInternal();

    /// <summary>Send a paddle position to the server. Cheap; safe to call ~30/s.</summary>
    public void SendPaddle(float y)
    {
        if (!IsConnected) return;
        SendFramed(PongProtocol.FormatPaddle(y));
    }

    void SendFramed(string framedMessage)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(framedMessage);
        try {
            _tcp.GetStream().Write(bytes, 0, bytes.Length);
        } catch (System.Exception e) {
            Debug.LogWarning("PongClient send error: " + e.Message);
        }
    }

    void Update()
    {
        if (_tcp == null) return;
        if (!_tcp.Connected) { CloseInternal(); return; }

        try {
            int available = _tcp.Available;
            // #region agent log
            int _dbgStateCount = 0;
            int _dbgTotalMsgs = 0;
            // #endregion
            while (available > 0) {
                int toRead = available < _readBuffer.Length ? available : _readBuffer.Length;
                int read = _tcp.GetStream().Read(_readBuffer, 0, toRead);
                if (read <= 0) break;

                var messages = _buffer.Append(_readBuffer, read);
                // #region agent log
                _dbgTotalMsgs += messages.Count;
                for (int _di = 0; _di < messages.Count; _di++) {
                    if (messages[_di] != null && messages[_di].StartsWith(PongProtocol.MsgState)) _dbgStateCount++;
                }
                // #endregion
                for (int i = 0; i < messages.Count; i++) Dispatch(messages[i]);
                available = _tcp.Available;
            }
            // #region agent log
            if (_dbgStateCount > 0) {
                float _now = Time.realtimeSinceStartup;
                float _gap = _dbgLastStateFrameTime > 0f ? (_now - _dbgLastStateFrameTime) * 1000f : -1f;
                _dbgLastStateFrameTime = _now;
                PongDebugLog.Write("AB", "PongClient.cs:195",
                    "client receive frame",
                    "{\"stateMsgs\":" + _dbgStateCount + ",\"totalMsgs\":" + _dbgTotalMsgs
                    + ",\"gapMsSincePrevStateFrame\":" + _gap.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "}");
            }
            // #endregion
        } catch (System.Exception ex) {
            Debug.LogWarning("PongClient read error: " + ex.Message);
            CloseInternal();
        }
    }

    void OnDisable() => CloseInternal();

    void Dispatch(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (!PongProtocol.TryGetMessageHead(message, out string head)) return;

        if (head == PongProtocol.MsgNames) {
            string payload = message.Substring(PongProtocol.MsgNames.Length);
            _playerNames = PongProtocol.ParseNamesPayload(payload);
            OnNames?.Invoke(_playerNames);
            return;
        }

        string[] parts = message.Split(PongProtocol.FieldSeparator);

        switch (head) {
            case PongProtocol.MsgRoster: {
                if (parts.Length >= 2 && PongProtocol.TryParseInt(parts[1], out int count)) {
                    LineCount = count;
                    LastRosterCount = count;
                    OnRoster?.Invoke(count);
                }
                break;
            }
            case PongProtocol.MsgAssign: {
                if (parts.Length >= 3
                    && PongProtocol.TryParseInt(parts[1], out int idx)
                    && PongProtocol.TryParseInt(parts[2], out int count)) {
                    LineIndex = idx;
                    LineCount = count;
                    LastRosterCount = count;
                    if (DebugNetworkLogs) {
                        Debug.Log("PongClient DBG ASSIGN line=" + idx + " lineCount=" + count);
                    }
                    OnAssign?.Invoke(idx, count);
                }
                break;
            }
            case PongProtocol.MsgState: {
                if (parts.Length >= 3
                    && PongProtocol.TryParseFloat(parts[1], out float bx)
                    && PongProtocol.TryParseFloat(parts[2], out float by)) {
                    int n = parts.Length - 3;
                    var ys = new float[n];
                    for (int i = 0; i < n; i++) PongProtocol.TryParseFloat(parts[3 + i], out ys[i]);
                    if (DebugNetworkLogs && Time.time >= _nextDebugStateLogTime) {
                        _nextDebugStateLogTime = Time.time + GetDebugInterval();
                        Debug.Log("PongClient DBG STATE ball=("
                            + bx.ToString("0.##") + "," + by.ToString("0.##")
                            + ") ownedLine=" + LineIndex + " paddles=["
                            + string.Join(",", System.Array.ConvertAll(ys, v => v.ToString("0.##"))) + "]");
                    }
                    OnState?.Invoke(new Vector2(bx, by), ys);
                }
                break;
            }
            case PongProtocol.MsgScore: {
                if (parts.Length >= 3
                    && PongProtocol.TryParseInt(parts[1], out int idx)
                    && PongProtocol.TryParseInt(parts[2], out int score)) {
                    OnScore?.Invoke(idx, score);
                }
                break;
            }
            case PongProtocol.MsgDamage: {
                if (parts.Length >= 3
                    && PongProtocol.TryParseInt(parts[1], out int idx)
                    && PongProtocol.TryParseInt(parts[2], out int state)) {
                    OnDamage?.Invoke(idx, state);
                }
                break;
            }
            case PongProtocol.MsgWin: {
                if (PongProtocol.TryParseWin(message, out int idx, out string winnerName)) {
                    LastWinnerName = string.IsNullOrEmpty(winnerName)
                        ? GetPlayerName(idx)
                        : winnerName;
                    OnWin?.Invoke(idx);
                }
                break;
            }
            case PongProtocol.MsgReset: {
                RestartCountdownSeconds = 0;
                IsJoinLobbyCountdown = false;
                OnReset?.Invoke();
                break;
            }
            case PongProtocol.MsgCountdown: {
                if (parts.Length >= 2 && PongProtocol.TryParseInt(parts[1], out int seconds)) {
                    RestartCountdownSeconds = seconds;
                    IsJoinLobbyCountdown = false;
                    OnCountdown?.Invoke(seconds);
                }
                break;
            }
            case PongProtocol.MsgJoinCountdown: {
                if (parts.Length >= 2 && PongProtocol.TryParseInt(parts[1], out int seconds)) {
                    RestartCountdownSeconds = seconds;
                    IsJoinLobbyCountdown = seconds > 0;
                    OnCountdown?.Invoke(seconds);
                }
                break;
            }
            case PongProtocol.MsgGeometry: {
                int n = parts.Length - 1;
                var xs = new float[n];
                for (int i = 0; i < n; i++) PongProtocol.TryParseFloat(parts[1 + i], out xs[i]);
                OnGeometry?.Invoke(xs);
                break;
            }
            case PongProtocol.MsgColors: {
                int n = parts.Length - 1;
                var slots = new int[n];
                for (int i = 0; i < n; i++) {
                    if (!PongProtocol.TryParseInt(parts[1 + i], out slots[i])) slots[i] = -1;
                }
                _colorSlots = slots;
                OnColors?.Invoke(slots);
                break;
            }
        }
    }

    void CloseInternal()
    {
        if (_tcp != null) {
            try { _tcp.Close(); } catch { /* ignore */ }
            _tcp = null;
        }
        LineIndex = -1;
        LineCount = 0;
        LastRosterCount = 0;
        LastWinnerName = string.Empty;
        RestartCountdownSeconds = 0;
        IsJoinLobbyCountdown = false;
        ParticipatesInGame = true;
        _playerNames = new string[0];
        _colorSlots = new int[0];
    }

    float GetDebugInterval()
    {
        return DebugLogRate > 0f ? 1f / DebugLogRate : 1f;
    }
}
