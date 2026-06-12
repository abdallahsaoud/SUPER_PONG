using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;

/// <summary>One authoritative world snapshot decoded from a STATE datagram.</summary>
public struct PongStateSnapshot
{
    /// <summary>Monotonic sequence from server; used to drop stale/out-of-order UDP datagrams.</summary>
    public uint Seq;
    /// <summary>Server Stopwatch time in ms; drives the interpolation clock in PongNetView.</summary>
    public long ServerTimeMs;
    public Vector2 BallPos;
    /// <summary>Ball velocity (units/s) for dead reckoning when packets are lost.</summary>
    public Vector2 BallVel;
    /// <summary>Authoritative ring angle per line slot (radians).</summary>
    public float[] Angles;
}

/// <summary>
/// Hybrid TCP + UDP client for Multiplayer Pong.
///
/// ── TRANSPORT SPLIT ──────────────────────────────────────────────────────────
///   TCP  — reliable control channel: handshake, UDPTOKEN, ASSIGN, lobby, scores…
///   UDP  — real-time channel: outgoing PADDLE (~30/s), incoming STATE (~30/s)
///
/// ── UDP LIFECYCLE (see Net/README_UDP.md) ────────────────────────────────────
///   Connect() → OpenUdpChannel() → receive UDPTOKEN (TCP) → HELLO (UDP, retried)
///   → first STATE (UDP) → game loop SendPaddle / DispatchUdp
///
/// Adapted from Assets/Demos/TCP/TCPClient.cs (newline framing) and UDP demo helpers.
/// </summary>
public class PongClient : MonoBehaviour
{
    public string DestinationIP = "127.0.0.1";
    public int DestinationPort = 25000;
    [Tooltip("Enable throttled client-side debug logs for incoming messages.")]
    public bool DebugNetworkLogs = false;
    [Tooltip("Max debug log frequency in logs/second.")]
    public float DebugLogRate = 1f;

    [Tooltip("How often to (re)send HELLO until the first STATE arrives, in seconds.")]
    public float HelloInterval = 0.25f;

    TcpClient _tcp;
    readonly PongMessageBuffer _buffer = new PongMessageBuffer();
    readonly byte[] _readBuffer = new byte[4096];
    float _nextDebugStateLogTime;

    // ── UDP real-time channel state ───────────────────────────────────────────
    readonly PongUdpSocket _udp = new PongUdpSocket();
    string _udpToken = string.Empty;          // secret from UDPTOKEN (TCP); included in every PADDLE/HELLO
    IPEndPoint _serverUdpEndpoint;            // server IP:port for outbound datagrams
    uint _lastStateSeq;                       // drop STATE datagrams with seq <= this (UDP reordering)
    bool _gotFirstState;                      // once true, stop retrying HELLO
    float _helloAccumulator;

    public bool IsConnected => _tcp != null && _tcp.Connected;

    // Typed events. The values arrive parsed so the rest of the client code stays simple.
    public delegate void AssignHandler(int lineIndex, int lineCount);
    public delegate void StateHandler(in PongStateSnapshot snapshot);
    public delegate void ScoreHandler(int lineIndex, int score);
    public delegate void DamageHandler(int lineIndex, int state);
    public delegate void WinHandler(int lineIndex);
    public delegate void ResetHandler();
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

    /// <summary>Request a palette color (0..MaxPlayers-1) for this player's paddle.</summary>
    public void SendColor(int paletteSlot)
    {
        if (!IsConnected) return;
        SendFramed(PongProtocol.FormatColor(paletteSlot));
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
            // Disable Nagle: control messages are tiny and latency-sensitive.
            try { _tcp.NoDelay = true; } catch { /* ignore on restricted platforms */ }
            var result = _tcp.BeginConnect(DestinationIP, DestinationPort, null, null);
            bool completed = result.AsyncWaitHandle.WaitOne(System.TimeSpan.FromSeconds(5));
            if (!completed || !_tcp.Connected) {
                throw new System.TimeoutException(
                    "Timeout — no server at " + DestinationIP + ":" + DestinationPort
                    + ". Check IP, port, firewall, and that the host server is running.");
            }
            _tcp.EndConnect(result);
            // Disable Nagle's algorithm on the client socket too: PADDLE packets are sent at
            // ~30 Hz and are tiny, so Nagle would otherwise hold them for up to ~40 ms waiting
            // to coalesce — adding input lag and contributing to perceived ball jitter.
            ConfigureSocket(_tcp);
            OpenUdpChannel();
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

    /// <summary>
    /// Open the UDP socket after TCP connect succeeds. Binds an ephemeral local port and
    /// remembers the server's UDP endpoint (same IP/port as TCP). HELLO/PADDLE go here.
    /// </summary>
    void OpenUdpChannel()
    {
        _udpToken = string.Empty;
        _lastStateSeq = 0;
        _gotFirstState = false;
        _helloAccumulator = 0f;
        _serverUdpEndpoint = null;

        if (!IPAddress.TryParse(DestinationIP, out var serverAddr)) {
            Debug.LogWarning("PongClient: cannot parse server IP for UDP: " + DestinationIP);
            return;
        }
        _serverUdpEndpoint = new IPEndPoint(serverAddr, DestinationPort);
        if (!_udp.Bind(0)) {
            Debug.LogWarning("PongClient: UDP channel failed to open — real-time updates unavailable.");
        }
    }

    /// <summary>
    /// Send local paddle angle to server over UDP (~30/s from PongNetPaddle).
    /// Requires UDPTOKEN (from TCP) and a bound UDP socket. Silent no-op if not ready.
    /// </summary>
    public void SendPaddle(float y)
    {
        if (!_udp.IsOpen || _serverUdpEndpoint == null || string.IsNullOrEmpty(_udpToken)) return;
        _udp.Send(PongProtocol.FormatPaddle(_udpToken, y), _serverUdpEndpoint);
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

    static void ConfigureSocket(TcpClient tcp)
    {
        if (tcp == null) return;
        try {
            tcp.NoDelay = true;
            tcp.SendBufferSize = 8192;
            tcp.ReceiveBufferSize = 8192;
        } catch (System.Exception ex) {
            Debug.LogWarning("PongClient socket tune error: " + ex.Message);
        }
    }

    void Update()
    {
        if (_tcp == null) return;
        if (!_tcp.Connected) { CloseInternal(); return; }

        try {
            int available = _tcp.Available;
            while (available > 0) {
                int toRead = available < _readBuffer.Length ? available : _readBuffer.Length;
                int read = _tcp.GetStream().Read(_readBuffer, 0, toRead);
                if (read <= 0) break;

                var messages = _buffer.Append(_readBuffer, read);
                for (int i = 0; i < messages.Count; i++) Dispatch(messages[i]);
                available = _tcp.Available;
            }
        } catch (System.Exception ex) {
            Debug.LogWarning("PongClient read error: " + ex.Message);
            CloseInternal();
        }

        PumpUdp();
    }

    /// <summary>
    /// Per-frame UDP pump: retry HELLO until first STATE, then drain incoming datagrams.
    /// Called from Update() after TCP reads.
    /// </summary>
    void PumpUdp()
    {
        if (!_udp.IsOpen) return;

        // Keep announcing our endpoint until the first STATE confirms the server can reach us.
        // After that, PADDLE datagrams keep the mapping fresh on their own.
        if (!_gotFirstState
            && !string.IsNullOrEmpty(_udpToken)
            && _serverUdpEndpoint != null) {
            _helloAccumulator += Time.unscaledDeltaTime;
            if (_helloAccumulator >= HelloInterval) {
                _helloAccumulator = 0f;
                _udp.Send(PongProtocol.FormatHello(_udpToken), _serverUdpEndpoint);
            }
        }

        var datagrams = _udp.Poll();
        for (int i = 0; i < datagrams.Count; i++) {
            DispatchUdp(datagrams[i].Message);
        }
    }

    /// <summary>
    /// Parse an incoming STATE datagram. This is the ONLY UDP message handled on the client.
    /// Stale/out-of-order datagrams (seq &lt;= _lastStateSeq) are discarded before rendering.
    /// Surviving snapshots are forwarded to PongNetView via OnState for interpolation.
    /// </summary>
    void DispatchUdp(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (!PongProtocol.TryGetMessageHead(message, out string head)) return;
        if (head != PongProtocol.MsgState) return;

        string[] parts = message.Split(PongProtocol.FieldSeparator);
        // STATE <seq> <serverTimeMs> <ballX> <ballY> <ballVX> <ballVY> <angle0> ...
        if (parts.Length < 7) return;
        if (!PongProtocol.TryParseUInt(parts[1], out uint seq)) return;
        if (!PongProtocol.TryParseLong(parts[2], out long serverTimeMs)) return;
        if (!PongProtocol.TryParseFloat(parts[3], out float bx)) return;
        if (!PongProtocol.TryParseFloat(parts[4], out float by)) return;
        if (!PongProtocol.TryParseFloat(parts[5], out float bvx)) return;
        if (!PongProtocol.TryParseFloat(parts[6], out float bvy)) return;

        // Drop stale / out-of-order datagrams: UDP can reorder and we only ever want to move forward.
        if (_gotFirstState && seq <= _lastStateSeq) return;
        _lastStateSeq = seq;
        _gotFirstState = true;

        int n = parts.Length - 7;
        var ys = new float[n];
        for (int i = 0; i < n; i++) PongProtocol.TryParseFloat(parts[7 + i], out ys[i]);

        if (DebugNetworkLogs && Time.time >= _nextDebugStateLogTime) {
            _nextDebugStateLogTime = Time.time + GetDebugInterval();
            Debug.Log("PongClient DBG STATE seq=" + seq + " ball=("
                + bx.ToString("0.##") + "," + by.ToString("0.##")
                + ") vel=(" + bvx.ToString("0.##") + "," + bvy.ToString("0.##")
                + ") ownedLine=" + LineIndex + " paddles=["
                + string.Join(",", System.Array.ConvertAll(ys, v => v.ToString("0.##"))) + "]");
        }

        var snapshot = new PongStateSnapshot {
            Seq = seq,
            ServerTimeMs = serverTimeMs,
            BallPos = new Vector2(bx, by),
            BallVel = new Vector2(bvx, bvy),
            Angles = ys,
        };
        OnState?.Invoke(in snapshot);
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
            case PongProtocol.MsgUdpToken: {
                // Step 3 of UDP bootstrap (see PongProtocol header): store token, announce endpoint.
                if (parts.Length >= 2 && !string.IsNullOrEmpty(parts[1])) {
                    _udpToken = parts[1];
                    // Announce our endpoint immediately; PumpUdp keeps retrying until STATE arrives.
                    if (_udp.IsOpen && _serverUdpEndpoint != null) {
                        _udp.Send(PongProtocol.FormatHello(_udpToken), _serverUdpEndpoint);
                    }
                }
                break;
            }
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
        _udp.Close();
        _udpToken = string.Empty;
        _serverUdpEndpoint = null;
        _lastStateSeq = 0;
        _gotFirstState = false;
        _helloAccumulator = 0f;
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
