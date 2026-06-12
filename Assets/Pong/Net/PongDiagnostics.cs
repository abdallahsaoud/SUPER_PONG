using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Lightweight append-only diagnostics log for debugging the server freeze / ball-escape issue.
/// Writes to <c>Application.persistentDataPath/pong-diag.log</c> with AutoFlush so the last line
/// before a hard freeze still reaches disk. Also mirrors to the Unity console.
///
/// Toggle via <see cref="PongServerGame.EnableDiagnostics"/>. Safe to call from the main thread.
/// </summary>
public static class PongDiagnostics
{
    static StreamWriter _writer;
    static string _path = string.Empty;

    public static string Path => _path;
    public static bool IsOpen => _writer != null;

    public static void Init(string role)
    {
        Close();
        try {
            _path = System.IO.Path.Combine(Application.persistentDataPath, "pong-diag.log");
            _writer = new StreamWriter(_path, append: true) { AutoFlush = true };
            Log("==== session start role=" + role + " at=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====");
            Debug.Log("PongDiagnostics: logging to " + _path);
        } catch (Exception e) {
            _writer = null;
            Debug.LogWarning("PongDiagnostics: could not open log file: " + e.Message);
        }
    }

    public static void Log(string message)
    {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message;
        Debug.Log("PONGDIAG " + message);
        try {
            _writer?.WriteLine(line);
        } catch {
            /* never let logging take down the game */
        }
    }

    public static void Warn(string message)
    {
        Debug.LogWarning("PONGDIAG " + message);
        try {
            _writer?.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] WARN " + message);
        } catch {
            /* ignore */
        }
    }

    public static void Close()
    {
        try {
            _writer?.Flush();
            _writer?.Dispose();
        } catch {
            /* ignore */
        }
        _writer = null;
    }
}
