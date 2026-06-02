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

    public bool IsConnected => _tcp != null && _tcp.Connected;

    // Typed events. The values arrive parsed so the rest of the client code stays simple.
    public delegate void AssignHandler(int lineIndex, int lineCount);
    public delegate void StateHandler(Vector2 ballPos, IList<float> paddleYs);
    public delegate void ScoreHandler(int lineIndex, int score);
    public delegate void DamageHandler(int lineIndex, int state);
    public delegate void WinHandler(int lineIndex);
    public delegate void ResetHandler();
    public delegate void GeometryHandler(IList<float> lineXs);

    public AssignHandler   OnAssign;
    public StateHandler    OnState;
    public ScoreHandler    OnScore;
    public DamageHandler   OnDamage;
    public WinHandler      OnWin;
    public ResetHandler    OnReset;
    public GeometryHandler OnGeometry;

    /// <summary>Assigned line index after ASSIGN arrives (-1 until then).</summary>
    public int LineIndex { get; private set; } = -1;
    /// <summary>Total line count in the match (after ASSIGN arrives, 0 until then).</summary>
    public int LineCount { get; private set; } = 0;

    public bool Connect()
    {
        if (_tcp != null) {
            Debug.LogWarning("PongClient already connected. Close first.");
            return false;
        }

        try {
            _tcp = new TcpClient();
            _tcp.Connect(DestinationIP, DestinationPort);
            Debug.Log("PongClient connected to " + DestinationIP + ":" + DestinationPort);
            return true;
        } catch (System.Exception ex) {
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
    }

    void OnDisable() => CloseInternal();

    void Dispatch(string message)
    {
        if (string.IsNullOrEmpty(message)) return;

        string[] parts = message.Split(PongProtocol.FieldSeparator);
        if (parts.Length == 0) return;
        string head = parts[0];

        switch (head) {
            case PongProtocol.MsgAssign: {
                if (parts.Length >= 3
                    && PongProtocol.TryParseInt(parts[1], out int idx)
                    && PongProtocol.TryParseInt(parts[2], out int count)) {
                    LineIndex = idx;
                    LineCount = count;
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
                if (parts.Length >= 2 && PongProtocol.TryParseInt(parts[1], out int idx)) {
                    OnWin?.Invoke(idx);
                }
                break;
            }
            case PongProtocol.MsgReset: {
                OnReset?.Invoke();
                break;
            }
            case PongProtocol.MsgGeometry: {
                int n = parts.Length - 1;
                var xs = new float[n];
                for (int i = 0; i < n; i++) PongProtocol.TryParseFloat(parts[1 + i], out xs[i]);
                OnGeometry?.Invoke(xs);
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
    }

    float GetDebugInterval()
    {
        return DebugLogRate > 0f ? 1f / DebugLogRate : 1f;
    }
}
