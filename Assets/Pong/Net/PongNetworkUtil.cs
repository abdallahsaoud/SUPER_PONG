using System;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;

/// <summary>Helpers for LAN play: local IP discovery, CLI overrides, IP validation.</summary>
public static class PongNetworkUtil
{
    public const string DefaultLoopback = "127.0.0.1";
    public const int DefaultPort = 25000;

    public static string NormalizeIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return string.Empty;
        return ip.Trim();
    }

    public static bool IsUsableClientTarget(string ip)
    {
        ip = NormalizeIp(ip);
        if (string.IsNullOrEmpty(ip)) return false;
        return IPAddress.TryParse(ip, out _);
    }

    /// <summary>Returns the first private IPv4 address of this machine (for hosting).</summary>
    public static string GetLocalLanIPv4()
    {
        try {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()) {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var props = ni.GetIPProperties();
                foreach (var addr in props.UnicastAddresses) {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = addr.Address.ToString();
                    if (IsPrivateLan(ip)) return ip;
                }
            }
        } catch {
            /* ignore on restricted platforms */
        }

        try {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var addr in host.AddressList) {
                if (addr.AddressFamily != AddressFamily.InterNetwork) continue;
                string ip = addr.ToString();
                if (IsPrivateLan(ip)) return ip;
            }
        } catch {
            /* ignore */
        }

        return string.Empty;
    }

    static bool IsPrivateLan(string ip)
    {
        if (!IPAddress.TryParse(ip, out var parsed)) return false;
        byte[] b = parsed.GetAddressBytes();
        if (b[0] == 10) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        return false;
    }

    public static bool TryApplyConnectArgs(string[] args, out string ip, out int port)
    {
        ip = null;
        port = 0;
        if (args == null) return false;

        for (int i = 0; i < args.Length; i++) {
            string a = args[i];
            if ((a == "-serverIP" || a == "-connectHost" || a == "-ip") && i + 1 < args.Length) {
                ip = NormalizeIp(args[i + 1]);
                i++;
            } else if ((a == "-serverPort" || a == "-connectPort" || a == "-port") && i + 1 < args.Length) {
                if (int.TryParse(args[i + 1], out int p)) port = p;
                i++;
            }
        }

        return !string.IsNullOrEmpty(ip) || port > 0;
    }

    public static string FormatHostHint(int listenPort)
    {
        var sb = new StringBuilder(128);
        sb.Append("Port ").Append(listenPort);
        string lan = GetLocalLanIPv4();
        if (!string.IsNullOrEmpty(lan)) {
            sb.Append(" — give clients IP: ").Append(lan);
        } else {
            sb.Append(" — use this machine's LAN IP (ipconfig / ifconfig)");
        }
        return sb.ToString();
    }
}
