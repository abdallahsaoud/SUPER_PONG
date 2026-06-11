using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// Newline-delimited UTF-8 message protocol shared by PongServer and PongClient.
/// Each message is a single line ending with '\n'. Fields are space-separated.
/// Floats use invariant culture so '.' is the decimal separator on every locale.
///
/// Client -> Server:
///   PADDLE <ringAngleRadians>
///   NAME <displayName>                       (tail may contain spaces)
///   READY                                      (explicit opt-in to the next match; required from each
///                                               participant before a finished round can restart)
///   POSTGAME                                   (player is on the end-of-round menu, not readied)
///   SPECTATE                                   (player is watching only, not queued)
///
/// Server -> Client:
///   ASSIGN <lineIndex> <lineCount>
///   NAMES <name0><tab><name1>...               (tab-separated, one per line slot)
///   ROSTER <lineCount>
///   STATE  <ballX> <ballY> <angle0> <angle1> ... <angleN-1>
///   SCORE  <lineIndex> <score>
///   DAMAGE <lineIndex> <state>            (state: 0=Intact, 1=Scattered, 2=Broken)
///   WIN    <lineIndex> <winnerName>
///   RESET
///   COUNTDOWN <secondsRemaining>           (0 = hide restart timer)
///   JOINCOUNTDOWN <secondsRemaining>       (lobby waits for more players)
/// </summary>
public static class PongProtocol
{
    public const char MessageDelimiter = '\n';
    public const char FieldSeparator = ' ';

    public const string MsgPaddle    = "PADDLE";
    public const string MsgName      = "NAME";
    public const string MsgReady     = "READY";
    public const string MsgPostGame  = "POSTGAME";
    public const string MsgSpectate  = "SPECTATE";
    public const string MsgAssign   = "ASSIGN";
    public const string MsgState    = "STATE";
    public const string MsgScore    = "SCORE";
    public const string MsgDamage   = "DAMAGE";
    public const string MsgWin      = "WIN";
    public const string MsgReset    = "RESET";
    public const string MsgGeometry = "GEOMETRY";
    public const string MsgRoster   = "ROSTER";
    public const string MsgNames      = "NAMES";
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
    public static string FormatSpectate() => MsgSpectate + MessageDelimiter;

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

    public static string FormatPaddle(float y)
        => MsgPaddle + FieldSeparator + y.ToString("0.###", Inv) + MessageDelimiter;

    public static string FormatAssign(int lineIndex, int lineCount)
        => MsgAssign + FieldSeparator + lineIndex.ToString(Inv) + FieldSeparator + lineCount.ToString(Inv) + MessageDelimiter;

    public static string FormatState(float ballX, float ballY, IList<float> paddleYs)
    {
        var sb = new StringBuilder(64);
        sb.Append(MsgState);
        sb.Append(FieldSeparator).Append(ballX.ToString("0.###", Inv));
        sb.Append(FieldSeparator).Append(ballY.ToString("0.###", Inv));
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

    /// <summary>Broadcast new X positions for every line. Sent after gap merge (line broken).</summary>
    public static string FormatGeometry(IList<float> lineXs)
    {
        var sb = new StringBuilder(32);
        sb.Append(MsgGeometry);
        for (int i = 0; i < lineXs.Count; i++) {
            sb.Append(FieldSeparator).Append(lineXs[i].ToString("0.###", Inv));
        }
        sb.Append(MessageDelimiter);
        return sb.ToString();
    }

    public static bool TryParseFloat(string s, out float value)
        => float.TryParse(s, NumberStyles.Float, Inv, out value);

    public static bool TryParseInt(string s, out int value)
        => int.TryParse(s, NumberStyles.Integer, Inv, out value);
}

/// <summary>
/// Accumulates bytes received on a TCP stream and yields complete '\n'-delimited messages.
/// TCP is a stream: a single read can return part of a message or several messages glued together.
/// This buffer keeps the leftover until the next read completes a line.
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
