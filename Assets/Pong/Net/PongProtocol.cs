using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// Newline-delimited UTF-8 message protocol shared by PongServer and PongClient.
/// Each message is a single line ending with '\n'. Fields are space-separated.
/// Floats use invariant culture so '.' is the decimal separator on every locale.
///
/// Client -> Server:
///   PADDLE <y>
///
/// Server -> Client:
///   ASSIGN <lineIndex> <lineCount>
///   STATE  <ballX> <ballY> <y0> <y1> ... <yN-1>
///   SCORE  <lineIndex> <score>
///   DAMAGE <lineIndex> <state>            (state: 0=Intact, 1=Scattered, 2=Broken)
///   WIN    <lineIndex>
///   RESET
/// </summary>
public static class PongProtocol
{
    public const char MessageDelimiter = '\n';
    public const char FieldSeparator = ' ';

    public const string MsgPaddle   = "PADDLE";
    public const string MsgAssign   = "ASSIGN";
    public const string MsgState    = "STATE";
    public const string MsgScore    = "SCORE";
    public const string MsgDamage   = "DAMAGE";
    public const string MsgWin      = "WIN";
    public const string MsgReset    = "RESET";
    public const string MsgGeometry = "GEOMETRY";

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

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

    public static string FormatWin(int lineIndex)
        => MsgWin + FieldSeparator + lineIndex.ToString(Inv) + MessageDelimiter;

    public static string FormatReset()
        => MsgReset + MessageDelimiter;

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
