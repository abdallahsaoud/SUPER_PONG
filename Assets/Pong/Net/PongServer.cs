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
        /// <summary>False while the client is on the end-of-round menu (until READY).</summary>
        public bool InGame = true;
    }

    TcpListener _tcp;
    readonly List<ClientConnection> _connections = new List<ClientConnection>();
    readonly byte[] _readBuffer = new byte[4096];

    public delegate void ClientConnectedHandler(ClientConnection client);
    public delegate void ClientDisconnectedHandler(ClientConnection client);
    public delegate void MessageHandler(ClientConnection client, string message);

    public ClientConnectedHandler OnClientConnected;
    public ClientDisconnectedHandler OnClientDisconnected;
    public MessageHandler OnMessageReceived;

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
            Debug.Log("PongServer listening on port " + ListenPort);
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
    }

    void OnDisable() => CloseInternal();

    void AcceptNewConnections()
    {
        while (_tcp.Pending()) {
            TcpClient tcpClient = _tcp.AcceptTcpClient();
            var conn = new ClientConnection { Tcp = tcpClient };
            _connections.Add(conn);

            var remote = (IPEndPoint)tcpClient.Client.RemoteEndPoint;
            Debug.Log("PongServer: new connection from " + remote.Address);

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
        OnClientDisconnected?.Invoke(conn);
    }

    void CloseInternal()
    {
        if (_tcp != null) {
            try { _tcp.Stop(); } catch { /* ignore */ }
            _tcp = null;
        }
        for (int i = 0; i < _connections.Count; i++) {
            try { _connections[i].Tcp?.Close(); } catch { /* ignore */ }
        }
        _connections.Clear();
    }
}
