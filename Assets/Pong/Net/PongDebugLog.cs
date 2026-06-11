// #region agent log
using System.Globalization;
using System.IO;

/// <summary>
/// Lightweight NDJSON debug logger for the active debug session. Appends one JSON line per
/// call to the session log file in the project root. TEMPORARY DEBUG INSTRUMENTATION.
/// </summary>
public static class PongDebugLog
{
    const string LogPath = @"C:\Users\thiba\Projects\SUPER_PONG\debug-de8893.log";
    const string SessionId = "de8893";
    static readonly object Gate = new object();

    public static void Write(string hypothesisId, string location, string message, string dataJson)
    {
        try {
            long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string line = "{\"sessionId\":\"" + SessionId + "\",\"hypothesisId\":\"" + hypothesisId
                + "\",\"location\":\"" + location + "\",\"message\":\"" + message
                + "\",\"data\":" + (string.IsNullOrEmpty(dataJson) ? "{}" : dataJson)
                + ",\"timestamp\":" + ts.ToString(CultureInfo.InvariantCulture) + "}";
            lock (Gate) {
                File.AppendAllText(LogPath, line + "\n");
            }
        } catch { /* never let logging break the game */ }
    }
}
// #endregion
