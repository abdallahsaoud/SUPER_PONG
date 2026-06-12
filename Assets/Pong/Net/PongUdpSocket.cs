using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

/// <summary>
/// Thin bidirectional UDP transport shared by the Pong client and server for the
/// real-time channel (STATE / PADDLE / HELLO). Unlike TCP, UDP is message-oriented:
/// one datagram in == one datagram out, so there is no need for the newline framing
/// used by <see cref="PongMessageBuffer"/>.
///
/// Receive is non-blocking: <see cref="Poll"/> drains every datagram currently
/// queued on the socket and returns them with their source endpoint. Send never
/// blocks meaningfully — a slow/lossy peer cannot stall the game loop the way a
/// full TCP send buffer can.
/// </summary>
public class PongUdpSocket
{
    public struct Datagram
    {
        public string Message;
        public IPEndPoint Source;
    }

    UdpClient _udp;
    IPEndPoint _source = new IPEndPoint(IPAddress.Any, 0);
    readonly List<Datagram> _scratch = new List<Datagram>(8);

    public bool IsOpen => _udp != null;

    /// <summary>Bind to a local port (server). Pass 0 for an ephemeral port (client).</summary>
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

    /// <summary>Drain all datagrams currently queued. Never blocks.</summary>
    public List<Datagram> Poll()
    {
        _scratch.Clear();
        if (_udp == null) return _scratch;

        try {
            while (_udp.Available > 0) {
                byte[] data = _udp.Receive(ref _source);
                string message = Encoding.UTF8.GetString(data);
                // Format* helpers append a trailing '\n' (TCP framing). On UDP a datagram is
                // already one message, so strip it (plus a tolerated '\r') before parsing.
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
