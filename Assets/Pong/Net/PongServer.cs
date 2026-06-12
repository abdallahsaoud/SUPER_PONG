using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;

/// <summary>
/// TCP server for Multiplayer Pong. One persistent connection per client.
/// Handles accept/disconnect, message framing, and broadcast/send primitives.
/// Game rules (ball, scoring, line assignment) live in PongServerGame, which
/// uses the OnClientConnected / OnLineMessage callbacks below.
///
/// Adapted from Assets/Demos/TCP/TCPServer.cs but with newline-framed messages
/// and per-connection state so we know which paddle each client owns.
/// </summary>
public class PongServer : MonoBehaviour
{
    public int ListenPort = 25000;

    /// <summary>Per-client state stored on the server.</summary>
    public class ClientConnection
    {
        public TcpClient Tcp;
        public PongMessageBuffer Buffer = new PongMessageBuffer();
        /// <summary>Line index assigned to this client by PongServerGame, or -1 if none.</summary>
        public int LineIndex = -1;
        public string DisplayName = string.Empty;
        /// <summary>Per-connection secret used to address/identify this client on the UDP channel.</summary>
        public string UdpToken = string.Empty;
        /// <summary>Source endpoint of the client's UDP channel, learned from HELLO/PADDLE. Null until then.</summary>
        public IPEndPoint UdpEndpoint;
        /// <summary>False while the client is on the end-of-round menu (until READY).</summary>
        public bool InGame = true;
        /// <summary>
        /// True only after the client has *explicitly* opted into the next match via READY
        /// while the previous round was over. Reset every time a round ends so a stale value
        /// can never make a single ready player start a match meant for several.
        /// </summary>
        public bool ReadyForNextMatch;
    }

    TcpListener _tcp;
    readonly List<ClientConnection> _connections = new List<ClientConnection>();
    readonly byte[] _readBuffer = new byte[4096];

    readonly PongUdpSocket _udp = new PongUdpSocket();
    readonly Dictionary<string, ClientConnection> _byToken = new Dictionary<string, ClientConnection>();

    public delegate void ClientConnectedHandler(ClientConnection client);
    public delegate void ClientDisconnectedHandler(ClientConnection client);
    public delegate void MessageHandler(ClientConnection client, string message);
    /// <summary>Real-time paddle update received over UDP, already mapped to its owning client.</summary>
    public delegate void UdpPaddleHandler(ClientConnection client, float angleRad);

    public ClientConnectedHandler OnClientConnected;
    public ClientDisconnectedHandler OnClientDisconnected;
    public MessageHandler OnMessageReceived;
    public UdpPaddleHandler OnUdpPaddle;

    public bool IsListening => _tcp != null;
    public int ConnectionCount => _connections.Count;
    public IReadOnlyList<ClientConnection> Connections => _connections;

    public bool Listen()
    {
        if (_tcp != null) {
            Debug.LogWarning("PongServer already listening. Close first.");
            return false;
        }

        try {
            _tcp = new TcpListener(IPAddress.Any, ListenPort);
            _tcp.Start();
            if (!_udp.Bind(ListenPort)) {
                Debug.LogWarning("PongServer: UDP channel failed to bind on port " + ListenPort
                    + " — real-time updates will not be delivered.");
            }
            Debug.Log("PongServer listening on port " + ListenPort + " (TCP control + UDP real-time)");
            return true;
        } catch (System.Exception ex) {
            Debug.LogWarning("PongServer listen error: " + ex.Message);
            CloseInternal();
            return false;
        }
    }

    public void Close() => CloseInternal();

    /// <summary>Send a single framed message to one client. Message must already include the trailing '\n'.</summary>
    public void Send(ClientConnection client, string framedMessage)
    {
        if (client == null || client.Tcp == null || !client.Tcp.Connected) return;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(framedMessage);
        try {
            client.Tcp.GetStream().Write(bytes, 0, bytes.Length);
        } catch (System.Exception e) {
            Debug.LogWarning("PongServer send error: " + e.Message);
        }
    }

    /// <summary>Broadcast a framed message to every connected client.</summary>
    public void Broadcast(string framedMessage)
    {
        if (_tcp == null) return;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(framedMessage);
        for (int i = 0; i < _connections.Count; i++) {
            var c = _connections[i];
            if (c.Tcp == null || !c.Tcp.Connected) continue;
            try {
                c.Tcp.GetStream().Write(bytes, 0, bytes.Length);
            } catch (System.Exception e) {
                Debug.LogWarning("PongServer broadcast error: " + e.Message);
            }
        }
    }

    void Update()
    {
        if (_tcp == null) return;
        AcceptNewConnections();
        PollConnections();
        PollUdp();
    }

    /// <summary>Send a real-time STATE datagram to every client whose UDP endpoint is known.</summary>
    public void BroadcastStateUdp(string framedMessage)
    {
        if (!_udp.IsOpen) return;
        for (int i = 0; i < _connections.Count; i++) {
            var c = _connections[i];
            if (c.UdpEndpoint == null) continue;
            _udp.Send(framedMessage, c.UdpEndpoint);
        }
    }

    void PollUdp()
    {
        if (!_udp.IsOpen) return;
        var datagrams = _udp.Poll();
        for (int i = 0; i < datagrams.Count; i++) {
            HandleUdpDatagram(datagrams[i]);
        }
    }

    void HandleUdpDatagram(PongUdpSocket.Datagram datagram)
    {
        string message = datagram.Message;
        if (!PongProtocol.TryGetMessageHead(message, out string head)) return;

        string[] parts = message.Split(PongProtocol.FieldSeparator);

        // HELLO <token>: register/refresh this client's UDP endpoint.
        if (head == PongProtocol.MsgHello) {
            if (parts.Length >= 2 && _byToken.TryGetValue(parts[1], out var helloClient)) {
                helloClient.UdpEndpoint = datagram.Source;
            }
            return;
        }

        // PADDLE <token> <angle>: identify the owner by token, refresh endpoint, forward angle.
        if (head == PongProtocol.MsgPaddle) {
            if (parts.Length >= 3
                && _byToken.TryGetValue(parts[1], out var paddleClient)
                && PongProtocol.TryParseFloat(parts[2], out float angle)) {
                paddleClient.UdpEndpoint = datagram.Source;
                OnUdpPaddle?.Invoke(paddleClient, angle);
            }
        }
    }

    void OnDisable() => CloseInternal();

    void AcceptNewConnections()
    {
        while (_tcp.Pending()) {
            TcpClient tcpClient = _tcp.AcceptTcpClient();
            // Disable Nagle: control messages are tiny and latency-sensitive, we never
            // want them buffered waiting for more bytes.
            try { tcpClient.NoDelay = true; } catch { /* ignore on restricted platforms */ }
            var conn = new ClientConnection { Tcp = tcpClient };
            conn.UdpToken = PongProtocol.NewUdpToken();
            _connections.Add(conn);
            _byToken[conn.UdpToken] = conn;

            var remote = (IPEndPoint)tcpClient.Client.RemoteEndPoint;
            Debug.Log("PongServer: new connection from " + remote.Address);

            // Hand the UDP token over the reliable channel before any game state, so the client
            // can start registering its UDP endpoint as soon as it is assigned.
            Send(conn, PongProtocol.FormatUdpToken(conn.UdpToken));
            OnClientConnected?.Invoke(conn);
        }
    }

    void PollConnections()
    {
        for (int i = _connections.Count - 1; i >= 0; i--) {
            var conn = _connections[i];
            if (conn.Tcp == null || !conn.Tcp.Connected) {
                HandleDisconnect(i);
                continue;
            }

            try {
                int available = conn.Tcp.Available;
                while (available > 0) {
                    int toRead = available < _readBuffer.Length ? available : _readBuffer.Length;
                    int read = conn.Tcp.GetStream().Read(_readBuffer, 0, toRead);
                    if (read <= 0) break;

                    var messages = conn.Buffer.Append(_readBuffer, read);
                    for (int m = 0; m < messages.Count; m++) {
                        OnMessageReceived?.Invoke(conn, messages[m]);
                    }
                    available = conn.Tcp.Available;
                }
            } catch (System.Exception ex) {
                Debug.LogWarning("PongServer read error: " + ex.Message);
                HandleDisconnect(i);
            }
        }
    }

    void HandleDisconnect(int index)
    {
        var conn = _connections[index];
        Debug.Log("PongServer: client disconnected (line " + conn.LineIndex + ")");
        try { conn.Tcp?.Close(); } catch { /* ignore */ }
        _connections.RemoveAt(index);
        if (!string.IsNullOrEmpty(conn.UdpToken)) _byToken.Remove(conn.UdpToken);
        OnClientDisconnected?.Invoke(conn);
    }

    void CloseInternal()
    {
        if (_tcp != null) {
            try { _tcp.Stop(); } catch { /* ignore */ }
            _tcp = null;
        }
        _udp.Close();
        for (int i = 0; i < _connections.Count; i++) {
            try { _connections[i].Tcp?.Close(); } catch { /* ignore */ }
        }
        _connections.Clear();
        _byToken.Clear();
    }
}
