using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

/// <summary>
/// Thin bidirectional UDP transport shared by PongClient and PongServer.
///
/// ── ROLE IN THE STACK ────────────────────────────────────────────────────────
/// This is the lowest layer of the real-time channel. It knows nothing about
/// PADDLE/STATE/HELLO — it only sends/receives UTF-8 strings as datagrams.
/// Message semantics live in <see cref="PongProtocol"/>; routing lives in
/// <see cref="PongServer"/> / <see cref="PongClient"/>.
///
/// ── TCP vs UDP FRAMING ───────────────────────────────────────────────────────
/// TCP is a byte stream: partial reads and glued messages require
/// <see cref="PongMessageBuffer"/> (newline framing).
/// UDP is message-oriented: one Send() == one Receive() == one game message.
/// Our Format* helpers still append '\n' for TCP compatibility; Poll() strips it.
///
/// ── NON-BLOCKING DESIGN ──────────────────────────────────────────────────────
/// Poll() never blocks the Unity game loop. A slow peer cannot stall the server
/// the way a full TCP send buffer can. MaxDatagramsPerPoll caps work per frame.
///
/// Usage:
///   Server: Bind(25000) on the same port as TcpListener.
///   Client: Bind(0) for an ephemeral local port, Send to server IP:25000.
/// </summary>
public class PongUdpSocket
{
    /// <summary>One received UDP datagram: parsed message + sender endpoint.</summary>
    public struct Datagram
    {
        public string Message;
        public IPEndPoint Source;
    }

    // Safety valve: without a cap, a malicious or buggy peer flooding datagrams could
    // spin Poll() for an entire frame and freeze the game loop.
    const int MaxDatagramsPerPoll = 2048;

    UdpClient _udp;
    IPEndPoint _source = new IPEndPoint(IPAddress.Any, 0);
    readonly List<Datagram> _scratch = new List<Datagram>(8);

    public bool IsOpen => _udp != null;

    /// <summary>Local port this socket is bound to (0 if closed). Useful for diagnostics.</summary>
    public int LocalPort
    {
        get {
            try { return _udp?.Client?.LocalEndPoint is IPEndPoint ep ? ep.Port : 0; }
            catch { return 0; }
        }
    }

    /// <summary>
    /// Bind to a local port. Server passes the game port (25000); client passes 0
    /// so the OS assigns an ephemeral port for outbound datagrams.
    /// </summary>
    public bool Bind(int port)
    {
        if (_udp != null) {
            Debug.LogWarning("PongUdpSocket already bound. Close first.");
            return false;
        }

        try {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.ExclusiveAddressUse = false;
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            // On Windows a UDP socket can surface a spurious ICMP "port unreachable"
            // as a ConnectionReset exception on Receive. Suppress it.
            TrySuppressConnectionReset(_udp);
            return true;
        } catch (System.Exception ex) {
            Debug.LogWarning("PongUdpSocket bind error on port " + port + ": " + ex.Message);
            Close();
            return false;
        }
    }

    /// <summary>
    /// Fire-and-forget send. UDP has no guaranteed delivery — callers must tolerate loss
    /// (STATE is sent every frame; PADDLE is sent only when angle changes).
    /// </summary>
    public void Send(string message, IPEndPoint destination)
    {
        if (_udp == null || destination == null) return;
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        try {
            _udp.Send(bytes, bytes.Length, destination);
        } catch (System.Exception e) {
            Debug.LogWarning("PongUdpSocket send error: " + e.Message);
        }
    }

    /// <summary>
    /// Drain all datagrams currently queued on the socket (non-blocking).
    /// Returns a reused internal list — copy results before the next Poll() if needed.
    /// </summary>
    public List<Datagram> Poll()
    {
        _scratch.Clear();
        if (_udp == null) return _scratch;

        try {
            int budget = MaxDatagramsPerPoll;
            while (_udp.Available > 0 && budget-- > 0) {
                byte[] data = _udp.Receive(ref _source);
                string message = Encoding.UTF8.GetString(data);
                // Format* helpers append '\n' for TCP. On UDP one datagram == one message,
                // so strip trailing newline (and optional '\r') before protocol parsing.
                message = message.TrimEnd('\r', '\n');
                _scratch.Add(new Datagram {
                    Message = message,
                    Source = new IPEndPoint(_source.Address, _source.Port),
                });
            }
        } catch (System.Exception ex) {
            Debug.LogWarning("PongUdpSocket receive error: " + ex.Message);
        }

        return _scratch;
    }

    public void Close()
    {
        if (_udp != null) {
            try { _udp.Close(); } catch { /* ignore */ }
            _udp = null;
        }
    }

    static void TrySuppressConnectionReset(UdpClient udp)
    {
        try {
            const int SIO_UDP_CONNRESET = -1744830452;
            udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
        } catch {
            /* not supported on this platform — harmless */
        }
    }
}
