using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// Newline-delimited UTF-8 message protocol shared by PongServer and PongClient.
/// Each message is a single line ending with '\n'. Fields are space-separated.
/// Floats use invariant culture so '.' is the decimal separator on every locale.
///
/// ── HYBRID TRANSPORT (presentation anchor) ───────────────────────────────────
/// The game uses TWO transports on the SAME port (default 25000):
///
///   TCP  = reliable, ordered byte stream  → control / events (lobby, scores, disconnect)
///   UDP  = unreliable, message-oriented   → real-time channel (~30 Hz)
///
/// Why not TCP-only for everything?
///   • Nagle's algorithm can delay tiny packets (~40 ms) → input lag on PADDLE.
///   • Head-of-line blocking: one lost TCP segment stalls ALL later STATE updates
///     → visible "teleport" of ball and remote paddles on a bad link.
///   • UDP lets us drop a single stale datagram without blocking fresher ones.
///
/// Why keep TCP at all?
///   • Connection lifecycle (accept/disconnect) is naturally reliable.
///   • Lobby messages (READY, ASSIGN, WIN) must not be lost.
///   • The UDP token is delivered once over TCP before any real-time traffic starts.
///
/// ── CONNECTION SETUP (chronological) ─────────────────────────────────────────
///   1. Client TCP-connects to server.
///   2. Client binds an ephemeral UDP socket (PongUdpSocket.Bind(0)).
///   3. Server sends UDPTOKEN over TCP (per-connection random 64-bit secret).
///   4. Client sends HELLO over UDP with that token → server records UdpEndpoint.
///   5. Client retries HELLO until first STATE arrives (NAT / firewall tolerance).
///   6. Game loop: PADDLE (client→server) and STATE (server→clients) over UDP.
///
/// ── CLIENT → SERVER (TCP) ────────────────────────────────────────────────────
///   NAME <displayName>                       (tail may contain spaces)
///   READY                                      (explicit opt-in to the next match)
///   POSTGAME                                   (on end-of-round menu, not readied)
///   COLOR <paletteSlot>                        (request palette color; server swaps on conflict)
///
/// ── CLIENT → SERVER (UDP) ────────────────────────────────────────────────────
///   HELLO  <token>                           (registers/refreshes this client's UDP endpoint)
///   PADDLE <token> <ringAngleRadians>        (token binds datagram to TCP connection; ~30/s)
///
/// ── SERVER → CLIENT (TCP) ────────────────────────────────────────────────────
///   UDPTOKEN <token>                         (hand out before any UDP traffic)
///   ASSIGN <lineIndex> <lineCount>
///   NAMES <name0><tab><name1>...
///   COLORS <slot0> <slot1> ... <slotN-1>       (-1 = unassigned)
///   ROSTER <lineCount>
///   SCORE  <lineIndex> <score>
///   DAMAGE <lineIndex> <state>            (0=Intact, 2=Eliminated)
///   WIN    <lineIndex> <winnerName>
///   RESET
///   COUNTDOWN / JOINCOUNTDOWN <secondsRemaining>
///
/// ── SERVER → CLIENT (UDP) ────────────────────────────────────────────────────
///   STATE <seq> <serverTimeMs> <ballX> <ballY> <ballVX> <ballVY> <angle0> ... <angleN-1>
///
///   Field guide (STATE):
///     seq           — monotonic; client drops datagrams with seq &lt;= lastSeq (UDP reordering)
///     serverTimeMs  — Stopwatch on server; drives interpolation clock on PongNetView
///     ballVX/VY     — lets client extrapolate ball through packet gaps (dead reckoning)
///     angleN        — authoritative ring angle per line (remote paddles; local uses input)
///
/// See also: Net/README_UDP.md (presentation walkthrough) and Assets/Pong/README.md (setup).
/// </summary>
public static class PongProtocol
{
    public const char MessageDelimiter = '\n';
    public const char FieldSeparator = ' ';

    // ── UDP real-time message identifiers (see class header for wire format) ──
    public const string MsgPaddle    = "PADDLE";
    public const string MsgHello     = "HELLO";
    public const string MsgUdpToken  = "UDPTOKEN";   // TCP-only: bootstrap for UDP channel
    public const string MsgName      = "NAME";
    public const string MsgReady     = "READY";
    public const string MsgPostGame  = "POSTGAME";
    public const string MsgColor     = "COLOR";
    public const string MsgAssign   = "ASSIGN";
    public const string MsgState    = "STATE";
    public const string MsgScore    = "SCORE";
    public const string MsgDamage   = "DAMAGE";
    public const string MsgWin      = "WIN";
    public const string MsgReset    = "RESET";
    public const string MsgRoster   = "ROSTER";
    public const string MsgNames      = "NAMES";
    public const string MsgColors     = "COLORS";
    public const string MsgCountdown  = "COUNTDOWN";
    public const string MsgJoinCountdown = "JOINCOUNTDOWN";

    public const char NameListSeparator = '\t';
    public const int MaxPlayerNameLength = 24;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string SanitizePlayerName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var trimmed = raw.Trim();
        if (trimmed.Length > MaxPlayerNameLength) {
            trimmed = trimmed.Substring(0, MaxPlayerNameLength);
        }
        trimmed = trimmed
            .Replace(NameListSeparator, ' ')
            .Replace(MessageDelimiter, ' ');
        return trimmed;
    }

    public static string DefaultPlayerName(int lineIndex)
        => "Player " + (lineIndex + 1).ToString(Inv);

    public static string FormatName(string displayName)
    {
        string safe = SanitizePlayerName(displayName);
        if (string.IsNullOrEmpty(safe)) safe = "Player";
        return MsgName + FieldSeparator + safe + MessageDelimiter;
    }

    public static string FormatReady() => MsgReady + MessageDelimiter;
    public static string FormatPostGame() => MsgPostGame + MessageDelimiter;

    public static string FormatColor(int paletteSlot)
        => MsgColor + FieldSeparator + paletteSlot.ToString(Inv) + MessageDelimiter;

    public static string FormatNames(IList<string> names)
    {
        var sb = new StringBuilder(64);
        sb.Append(MsgNames);
        for (int i = 0; i < names.Count; i++) {
            sb.Append(NameListSeparator);
            string safe = SanitizePlayerName(names[i]);
            if (string.IsNullOrEmpty(safe)) safe = DefaultPlayerName(i);
            sb.Append(safe);
        }
        sb.Append(MessageDelimiter);
        return sb.ToString();
    }

    public static string[] ParseNamesPayload(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return new string[0];

        while (payload.Length > 0
            && (payload[0] == NameListSeparator || payload[0] == FieldSeparator)) {
            payload = payload.Substring(1);
        }
        if (payload.Length == 0) return new string[0];

        string[] parts = payload.Split(NameListSeparator);
        var names = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++) {
            string safe = SanitizePlayerName(parts[i]);
            names[i] = string.IsNullOrEmpty(safe) ? DefaultPlayerName(i) : safe;
        }
        return names;
    }

    /// <summary>UDP paddle update. The token identifies the owning client to the server.</summary>
    public static string FormatPaddle(string token, float y)
        => MsgPaddle + FieldSeparator + token + FieldSeparator + y.ToString("0.###", Inv) + MessageDelimiter;

    /// <summary>UDP registration: tells the server which endpoint owns this token.</summary>
    public static string FormatHello(string token)
        => MsgHello + FieldSeparator + token + MessageDelimiter;

    /// <summary>TCP message handing the per-connection UDP token to the client.</summary>
    public static string FormatUdpToken(string token)
        => MsgUdpToken + FieldSeparator + token + MessageDelimiter;

    public static string FormatAssign(int lineIndex, int lineCount, bool queuedForNextMatch = false)
    {
        var sb = new StringBuilder(32);
        sb.Append(MsgAssign);
        sb.Append(FieldSeparator).Append(lineIndex.ToString(Inv));
        sb.Append(FieldSeparator).Append(lineCount.ToString(Inv));
        if (queuedForNextMatch) sb.Append(FieldSeparator).Append('1');
        sb.Append(MessageDelimiter);
        return sb.ToString();
    }

    /// <summary>
    /// Build a STATE datagram for UDP broadcast. Called ~30/s by PongServerGame.BroadcastState.
    /// The client parses this in PongClient.DispatchUdp and feeds PongNetView for interpolation.
    /// </summary>
    public static string FormatState(
        uint seq, long serverTimeMs,
        float ballX, float ballY, float ballVX, float ballVY,
        IList<float> paddleYs)
    {
        var sb = new StringBuilder(96);
        sb.Append(MsgState);
        sb.Append(FieldSeparator).Append(seq.ToString(Inv));
        sb.Append(FieldSeparator).Append(serverTimeMs.ToString(Inv));
        sb.Append(FieldSeparator).Append(ballX.ToString("0.###", Inv));
        sb.Append(FieldSeparator).Append(ballY.ToString("0.###", Inv));
        sb.Append(FieldSeparator).Append(ballVX.ToString("0.###", Inv));
        sb.Append(FieldSeparator).Append(ballVY.ToString("0.###", Inv));
        for (int i = 0; i < paddleYs.Count; i++) {
            sb.Append(FieldSeparator).Append(paddleYs[i].ToString("0.###", Inv));
        }
        sb.Append(MessageDelimiter);
        return sb.ToString();
    }

    public static string FormatScore(int lineIndex, int score)
        => MsgScore + FieldSeparator + lineIndex.ToString(Inv) + FieldSeparator + score.ToString(Inv) + MessageDelimiter;

    public static string FormatDamage(int lineIndex, int state)
        => MsgDamage + FieldSeparator + lineIndex.ToString(Inv) + FieldSeparator + state.ToString(Inv) + MessageDelimiter;

    public static string FormatWin(int lineIndex, string displayName)
    {
        string safe = SanitizePlayerName(displayName);
        if (string.IsNullOrEmpty(safe)) safe = DefaultPlayerName(lineIndex);
        return MsgWin + FieldSeparator + lineIndex.ToString(Inv) + FieldSeparator + safe + MessageDelimiter;
    }

    public static bool TryGetMessageHead(string message, out string head)
    {
        head = string.Empty;
        if (string.IsNullOrEmpty(message)) return false;

        int sep = message.IndexOf(FieldSeparator);
        if (sep < 0) sep = message.IndexOf(NameListSeparator);
        head = sep < 0 ? message : message.Substring(0, sep);
        return head.Length > 0;
    }

    public static bool TryParseWin(string message, out int lineIndex, out string winnerName)
    {
        lineIndex = -1;
        winnerName = string.Empty;
        if (!TryGetMessageHead(message, out string head) || head != MsgWin) return false;

        string rest = message.Substring(head.Length);
        while (rest.Length > 0
            && (rest[0] == FieldSeparator || rest[0] == NameListSeparator)) {
            rest = rest.Substring(1);
        }
        if (rest.Length == 0) return false;

        int sep = rest.IndexOf(FieldSeparator);
        if (sep < 0) {
            if (!TryParseInt(rest, out lineIndex)) return false;
            return true;
        }

        if (!TryParseInt(rest.Substring(0, sep), out lineIndex)) return false;
        winnerName = SanitizePlayerName(rest.Substring(sep + 1));
        return true;
    }

    public static string FormatReset()
        => MsgReset + MessageDelimiter;

    public static string FormatCountdown(int secondsRemaining)
    {
        int seconds = secondsRemaining < 0 ? 0 : secondsRemaining;
        return MsgCountdown + FieldSeparator + seconds.ToString(Inv) + MessageDelimiter;
    }

    public static string FormatJoinCountdown(int secondsRemaining)
    {
        int seconds = secondsRemaining < 0 ? 0 : secondsRemaining;
        return MsgJoinCountdown + FieldSeparator + seconds.ToString(Inv) + MessageDelimiter;
    }

    public static string FormatRoster(int lineCount)
        => MsgRoster + FieldSeparator + lineCount.ToString(Inv) + MessageDelimiter;

    /// <summary>One palette slot per line (in line-index order). <c>-1</c> means unassigned.</summary>
    public static string FormatColors(IList<int> slots)
    {
        var sb = new StringBuilder(32);
        sb.Append(MsgColors);
        for (int i = 0; i < slots.Count; i++) {
            sb.Append(FieldSeparator).Append(slots[i].ToString(Inv));
        }
        sb.Append(MessageDelimiter);
        return sb.ToString();
    }

    public static bool TryParseFloat(string s, out float value)
        => float.TryParse(s, NumberStyles.Float, Inv, out value);

    public static bool TryParseInt(string s, out int value)
        => int.TryParse(s, NumberStyles.Integer, Inv, out value);

    public static bool TryParseUInt(string s, out uint value)
        => uint.TryParse(s, NumberStyles.Integer, Inv, out value);

    public static bool TryParseLong(string s, out long value)
        => long.TryParse(s, NumberStyles.Integer, Inv, out value);

    /// <summary>
    /// Generate a unique per-TCP-connection secret. The server maps token → ClientConnection
    /// so incoming HELLO/PADDLE datagrams can be attributed without trusting source IP alone.
    /// </summary>
    public static string NewUdpToken()
    {
        var bytes = new byte[8];
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) {
            rng.GetBytes(bytes);
        }
        ulong value = System.BitConverter.ToUInt64(bytes, 0);
        if (value == 0) value = 1;
        return value.ToString(Inv);
    }
}

/// <summary>
/// Accumulates bytes received on a TCP stream and yields complete '\n'-delimited messages.
///
/// TCP is a byte stream, NOT message-oriented: a single read can return half a message
/// or several messages glued together. This buffer keeps leftover bytes until the next
/// read completes a line. UDP does NOT use this class — each datagram is one message.
/// </summary>
public class PongMessageBuffer
{
    readonly StringBuilder _buffer = new StringBuilder(256);

    /// <summary>Append received bytes; returns 0..N complete messages (without the trailing '\n').</summary>
    public List<string> Append(byte[] data, int length)
    {
        var messages = new List<string>();
        if (length <= 0) return messages;

        _buffer.Append(Encoding.UTF8.GetString(data, 0, length));

        while (true) {
            int newline = -1;
            for (int i = 0; i < _buffer.Length; i++) {
                if (_buffer[i] == PongProtocol.MessageDelimiter) { newline = i; break; }
            }
            if (newline < 0) break;

            string line = _buffer.ToString(0, newline);
            _buffer.Remove(0, newline + 1);
            // tolerate CRLF
            if (line.Length > 0 && line[line.Length - 1] == '\r') line = line.Substring(0, line.Length - 1);
            if (line.Length > 0) messages.Add(line);
        }

        return messages;
    }
}
