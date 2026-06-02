using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Server-side authoritative Pong simulation.
///
/// - Holds the global game state (ball position/direction, per-line paddle Y,
///   per-line score, per-line health for Milestone 2).
/// - Assigns each connecting client a free line index, frees it on disconnect.
/// - Receives PADDLE messages and updates that client's paddle Y (client has
///   local control: server trusts the value, only clamps it to the arena).
/// - Simulates the ball, checks collisions, broadcasts STATE at a fixed tick.
/// - Broadcasts SCORE / DAMAGE / WIN / RESET as discrete reliable events.
///
/// Arena geometry follows the original Pong demo:
///   Top y=+5, Bottom y=-5, Left x=-6, Right x=+6
///   Paddle X positions: line 0 at x=-5 (left), line 1 at x=+5 (right)
///   Paddle Y clamp: [-4, 4], paddle half-height ~1
/// The line list is data-driven (LineConfig) so it scales to N players later.
/// </summary>
[RequireComponent(typeof(PongServer))]
public class PongServerGame : MonoBehaviour
{
    const int MinPlayersToPlay = 2;

    [Header("Arena")]
    public float ArenaHalfWidth = 6f;
    public float ArenaHalfHeight = 5f;
    public float PaddleHalfHeight = 1f;
    public float PaddleClampY = 4f;

    [Header("Ball")]
    public float BallSpeed = 4f;
    public float BallRadius = 0.25f;
    public Vector2 BallStart = Vector2.zero;

    [Header("Networking")]
    [Tooltip("How many STATE updates per second to broadcast.")]
    public float StateUpdateRate = 30f;
    [Tooltip("Enable throttled server-side debug logs for PADDLE/STATE flow.")]
    public bool DebugNetworkLogs = false;
    [Tooltip("Max debug log frequency in logs/second.")]
    public float DebugLogRate = 1f;

    [Header("Game")]
    [Tooltip("Lines defined for this match. Index = line id. Scales from 2 to N.")]
    public List<LineConfig> Lines = new List<LineConfig>();
    [Tooltip("Enable milestone-2 line damage/breaking rules. Keep off for classic 2-player Pong.")]
    public bool EnableLineDamage = false;

    /// <summary>Configuration for one line (paddle/wall) around the arena.</summary>
    [System.Serializable]
    public class LineConfig
    {
        /// <summary>X position of this line. Negative = left side, positive = right side.</summary>
        public float X;
        /// <summary>+1 = ball must travel in +X to score on this line (right line), -1 = -X (left line).</summary>
        public int FacingSign;
    }

    /// <summary>Per-line runtime state (separate from config so it can be reset).</summary>
    public class LineRuntime
    {
        public bool Assigned;
        public PongServer.ClientConnection Owner;
        public float PaddleY;
        public int Score;
        /// <summary>0=Intact, 1=Scattered, 2=Broken. Milestone 2 will exercise this.</summary>
        public int Health;
    }

    public enum BallState { WaitingForPlayers, Playing, Won }

    PongServer _server;
    LineRuntime[] _runtime;
    Vector2 _ballPos;
    Vector2 _ballDir;
    BallState _state = BallState.Playing;
    int _winnerLine = -1;
    float _stateAccumulator;
    float _nextDebugPaddleLogTime;
    float _nextDebugStateLogTime;

    void Awake()
    {
        _server = GetComponent<PongServer>();
    }

    /// <summary>
    /// Build the per-line runtime array from the current Lines list. Call after editing
    /// Lines from code (e.g. from PongBootstrap). Called automatically on first use.
    /// </summary>
    public void RebuildRuntime()
    {
        if (Lines.Count == 0) {
            // Default to classic 2-player Pong.
            Lines.Add(new LineConfig { X = -5f, FacingSign = -1 });
            Lines.Add(new LineConfig { X =  5f, FacingSign = +1 });
        }
        _runtime = new LineRuntime[Lines.Count];
        for (int i = 0; i < _runtime.Length; i++) _runtime[i] = new LineRuntime();
    }

    void EnsureRuntime()
    {
        if (_runtime == null || _runtime.Length != Lines.Count) RebuildRuntime();
    }

    void OnEnable()
    {
        _server.OnClientConnected += HandleClientConnected;
        _server.OnClientDisconnected += HandleClientDisconnected;
        _server.OnMessageReceived += HandleMessage;
    }

    void OnDisable()
    {
        if (_server != null) {
            _server.OnClientConnected -= HandleClientConnected;
            _server.OnClientDisconnected -= HandleClientDisconnected;
            _server.OnMessageReceived -= HandleMessage;
        }
    }

    void Start()
    {
        EnsureRuntime();
        EnterWaitingForPlayers("startup");
    }

    void Update()
    {
        if (!_server.IsListening) return;
        EnsureRuntime();
        float dt = Time.unscaledDeltaTime;
        int assignedCount = GetAssignedCount();

        if (assignedCount < MinPlayersToPlay) {
            if (_state != BallState.WaitingForPlayers) {
                EnterWaitingForPlayers("player disconnected");
            }
        } else if (_state == BallState.WaitingForPlayers) {
            StartMatch();
        }

        if (_state == BallState.Playing) {
            StepBall(dt);
        }

        _stateAccumulator += dt;
        float interval = StateUpdateRate > 0f ? 1f / StateUpdateRate : 0.033f;
        if (_stateAccumulator >= interval) {
            _stateAccumulator = 0f;
            BroadcastState();
        }
    }

    // ----- Connection lifecycle -----

    void HandleClientConnected(PongServer.ClientConnection client)
    {
        EnsureRuntime();
        int free = FindFreeLine();
        if (free < 0) {
            Debug.Log("PongServerGame: no free line for new client, dropping.");
            try { client.Tcp.Close(); } catch { /* ignore */ }
            return;
        }

        client.LineIndex = free;
        _runtime[free].Assigned = true;
        _runtime[free].Owner = client;
        _runtime[free].PaddleY = 0f;
        _runtime[free].Score = 0;
        _runtime[free].Health = 0;

        _server.Send(client, PongProtocol.FormatAssign(free, Lines.Count));
        _server.Send(client, PongProtocol.FormatScore(free, 0));
        Debug.Log("PongServerGame: assigned line " + free + " to new client.");
    }

    void HandleClientDisconnected(PongServer.ClientConnection client)
    {
        if (client.LineIndex >= 0 && client.LineIndex < _runtime.Length) {
            var rt = _runtime[client.LineIndex];
            rt.Assigned = false;
            rt.Owner = null;
            rt.PaddleY = 0f;
        }
        if (GetAssignedCount() < MinPlayersToPlay && _state != BallState.WaitingForPlayers) {
            EnterWaitingForPlayers("player disconnected");
        }
    }

    void HandleMessage(PongServer.ClientConnection client, string message)
    {
        if (string.IsNullOrEmpty(message)) return;

        int sp = message.IndexOf(PongProtocol.FieldSeparator);
        string head = sp < 0 ? message : message.Substring(0, sp);
        string tail = sp < 0 ? string.Empty : message.Substring(sp + 1);

        if (head == PongProtocol.MsgPaddle) {
            if (PongProtocol.TryParseFloat(tail, out float y)) {
                if (client.LineIndex >= 0 && client.LineIndex < _runtime.Length) {
                    float clamped = Mathf.Clamp(y, -PaddleClampY, PaddleClampY);
                    _runtime[client.LineIndex].PaddleY = clamped;
                    if (DebugNetworkLogs && Time.time >= _nextDebugPaddleLogTime) {
                        _nextDebugPaddleLogTime = Time.time + GetDebugInterval();
                        Debug.Log("PongServerGame DBG PADDLE line=" + client.LineIndex + " y=" + clamped.ToString("0.###"));
                    }
                }
            }
        }
    }

    // ----- Simulation -----

    void ServeBall()
    {
        if (GetAssignedCount() < MinPlayersToPlay) {
            EnterWaitingForPlayers("not enough players");
            return;
        }

        _ballPos = BallStart;
        _ballDir = new Vector2(
            Random.Range(0.5f, 1f),
            Random.Range(-0.5f, 0.5f)
        );
        _ballDir.x *= Mathf.Sign(Random.Range(-100, 100));
        _ballDir.Normalize();
        _state = BallState.Playing;
        _winnerLine = -1;

        if (_server != null && _server.IsListening) {
            _server.Broadcast(PongProtocol.FormatReset());
        }
    }

    void StartMatch()
    {
        Debug.Log("PongServerGame: MATCH_STARTED");
        ServeBall();
    }

    void EnterWaitingForPlayers(string reason)
    {
        CancelInvoke(nameof(ServeBall));
        _state = BallState.WaitingForPlayers;
        _winnerLine = -1;
        _ballPos = BallStart;
        _ballDir = Vector2.zero;
        if (_server != null && _server.IsListening) {
            _server.Broadcast(PongProtocol.FormatReset());
        }
        Debug.Log("PongServerGame: MATCH_ENDED_WAITING (" + GetAssignedCount() + "/" + MinPlayersToPlay + ") reason=" + reason);
    }

    void StepBall(float dt)
    {
        _ballPos += _ballDir * BallSpeed * dt;

        // Top/bottom walls
        if (_ballPos.y >  ArenaHalfHeight - BallRadius && _ballDir.y > 0f) _ballDir.y = -_ballDir.y;
        if (_ballPos.y < -ArenaHalfHeight + BallRadius && _ballDir.y < 0f) _ballDir.y = -_ballDir.y;

        // Lines (paddles)
        for (int i = 0; i < Lines.Count; i++) {
            var cfg = Lines[i];
            var rt = _runtime[i];
            if (rt.Health >= 2) continue; // broken: ball passes through

            bool approaching = (cfg.FacingSign > 0 && _ballDir.x > 0f && _ballPos.x >= cfg.X - BallRadius)
                            || (cfg.FacingSign < 0 && _ballDir.x < 0f && _ballPos.x <= cfg.X + BallRadius);
            if (!approaching) continue;

            // Check if ball is within paddle vertical extent.
            float dy = _ballPos.y - rt.PaddleY;
            if (Mathf.Abs(dy) <= PaddleHalfHeight + BallRadius) {
                _ballDir.x = -_ballDir.x;
                // Nudge ball outside paddle to avoid sticking.
                _ballPos.x = cfg.X - cfg.FacingSign * (BallRadius + 0.01f);
                OnBallHitLine(i);
                return;
            }
        }

        // Out of bounds horizontally => the player on the OPPOSITE line scores.
        if (_ballPos.x >  ArenaHalfWidth) { OnBallExitedSide(+1); return; }
        if (_ballPos.x < -ArenaHalfWidth) { OnBallExitedSide(-1); return; }
    }

    /// <summary>
    /// Milestone 2: increment the line's damage on every ball contact.
    ///   0 Intact   -> 1 Scattered (still bounces)
    ///   1 Scattered-> 2 Broken    (no longer blocks - StepBall skips it)
    /// Broadcast DAMAGE over TCP so clients update visuals; if the new state is
    /// Broken, also trigger gap-merge and elimination/win logic.
    /// </summary>
    protected virtual void OnBallHitLine(int lineIndex)
    {
        if (!EnableLineDamage) return;
        var rt = _runtime[lineIndex];
        if (rt.Health >= 2) return; // already broken: shouldn't have been hit, but safe-guard

        rt.Health++;
        _server.Broadcast(PongProtocol.FormatDamage(lineIndex, rt.Health));

        if (rt.Health >= 2) {
            OnLineBroken(lineIndex);
        }
    }

    /// <summary>Server logic when a line becomes Broken: slide neighbors to cover the gap,
    /// then check whether only one line remains and declare a winner.</summary>
    protected virtual void OnLineBroken(int brokenIndex)
    {
        SlideNeighborsOverGap(brokenIndex);
        CheckForLastLineStanding();
    }

    /// <summary>Ball left the arena past x = +/- ArenaHalfWidth. Award to the line on the opposite side.</summary>
    void OnBallExitedSide(int exitSign)
    {
        // Find the line whose FacingSign == -exitSign (the defender on the opposite side scores).
        int winner = -1;
        for (int i = 0; i < Lines.Count; i++) {
            if (Lines[i].FacingSign == -exitSign && _runtime[i].Assigned) { winner = i; break; }
        }

        if (winner >= 0) {
            _runtime[winner].Score++;
            _server.Broadcast(PongProtocol.FormatScore(winner, _runtime[winner].Score));
            _state = BallState.Won;
            _winnerLine = winner;
            _server.Broadcast(PongProtocol.FormatWin(winner));
            Invoke(nameof(ServeBall), 1.5f);
        } else {
            // No defender (or solo session): just reserve serve.
            Invoke(nameof(ServeBall), 1.0f);
            _state = BallState.Won;
        }
    }

    void BroadcastState()
    {
        if (_runtime == null || _runtime.Length == 0) return;
        var paddleYs = new float[_runtime.Length];
        for (int i = 0; i < _runtime.Length; i++) paddleYs[i] = _runtime[i].PaddleY;

        _server.Broadcast(PongProtocol.FormatState(_ballPos.x, _ballPos.y, paddleYs));
        if (DebugNetworkLogs && Time.time >= _nextDebugStateLogTime) {
            _nextDebugStateLogTime = Time.time + GetDebugInterval();
            Debug.Log("PongServerGame DBG STATE ball=("
                + _ballPos.x.ToString("0.##") + ","
                + _ballPos.y.ToString("0.##") + ") paddles=["
                + string.Join(",", System.Array.ConvertAll(paddleYs, v => v.ToString("0.##"))) + "]");
        }
    }

    float GetDebugInterval()
    {
        return DebugLogRate > 0f ? 1f / DebugLogRate : 1f;
    }

    int FindFreeLine()
    {
        for (int i = 0; i < _runtime.Length; i++) {
            if (!_runtime[i].Assigned) return i;
        }
        return -1;
    }

    int GetAssignedCount()
    {
        if (_runtime == null) return 0;
        int count = 0;
        for (int i = 0; i < _runtime.Length; i++) {
            if (_runtime[i].Assigned) count++;
        }
        return count;
    }

    /// <summary>
    /// Milestone 2: when line <paramref name="brokenIndex"/> becomes Broken, slide its two
    /// nearest still-alive neighbors (in arena X order) inward to cover the gap.
    /// Same overall arena shape, fewer / larger segments. Broadcasts GEOMETRY to clients.
    /// </summary>
    void SlideNeighborsOverGap(int brokenIndex)
    {
        if (Lines.Count < 3) return; // no neighbors to merge with (classic 2-line Pong)

        // Order lines by X to find geometric neighbors.
        var order = new List<int>(Lines.Count);
        for (int i = 0; i < Lines.Count; i++) order.Add(i);
        order.Sort((a, b) => Lines[a].X.CompareTo(Lines[b].X));

        int slot = order.IndexOf(brokenIndex);
        int leftNeighbor  = FindAliveNeighbor(order, slot, -1);
        int rightNeighbor = FindAliveNeighbor(order, slot, +1);

        float brokenX = Lines[brokenIndex].X;

        if (leftNeighbor >= 0 && rightNeighbor >= 0) {
            // Both sides exist: midpoint of broken line is covered by each neighbor moving halfway in.
            float midL = (Lines[leftNeighbor].X + brokenX) * 0.5f;
            float midR = (brokenX + Lines[rightNeighbor].X) * 0.5f;
            Lines[leftNeighbor].X  = midL;
            Lines[rightNeighbor].X = midR;
        } else if (leftNeighbor >= 0) {
            Lines[leftNeighbor].X = brokenX; // single neighbor covers the gap
        } else if (rightNeighbor >= 0) {
            Lines[rightNeighbor].X = brokenX;
        }

        // Broadcast updated geometry so clients can move their paddle visuals.
        var xs = new float[Lines.Count];
        for (int i = 0; i < Lines.Count; i++) xs[i] = Lines[i].X;
        _server.Broadcast(PongProtocol.FormatGeometry(xs));
    }

    int FindAliveNeighbor(List<int> orderedIndices, int slot, int dir)
    {
        for (int s = slot + dir; s >= 0 && s < orderedIndices.Count; s += dir) {
            int idx = orderedIndices[s];
            if (_runtime[idx].Health < 2) return idx;
        }
        return -1;
    }

    /// <summary>Declare the last line standing the winner (broadcast WIN, end the round).</summary>
    void CheckForLastLineStanding()
    {
        int aliveCount = 0;
        int lastAlive = -1;
        for (int i = 0; i < _runtime.Length; i++) {
            if (_runtime[i].Health < 2) { aliveCount++; lastAlive = i; }
        }
        if (aliveCount <= 1 && _state == BallState.Playing) {
            _state = BallState.Won;
            _winnerLine = lastAlive;
            if (lastAlive >= 0) _server.Broadcast(PongProtocol.FormatWin(lastAlive));
        }
    }

    // ----- Public read-only state (useful for a server HUD if you want one) -----

    public int LineCount => Lines.Count;
    public Vector2 BallPosition => _ballPos;
    public float GetPaddleY(int lineIndex) => _runtime[lineIndex].PaddleY;
    public int GetScore(int lineIndex) => _runtime[lineIndex].Score;
    public int GetHealth(int lineIndex) => _runtime[lineIndex].Health;
    public LineConfig GetLineConfig(int lineIndex) => Lines[lineIndex];
    public bool IsLineAssigned(int lineIndex) => _runtime[lineIndex].Assigned;
}
