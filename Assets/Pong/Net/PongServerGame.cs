using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Authoritative circular arena: ball stays inside the ring, platforms move freely
/// on the circumference, touch eliminates a player, last survivor wins.
/// </summary>
[RequireComponent(typeof(PongServer))]
public class PongServerGame : MonoBehaviour
{
    const int MinPlayersToPlay = 2;
    const int HealthAlive = 0;
    const int HealthEliminated = 2;

    [Header("Ball")]
    public float BallSpeed = CircleArenaConfig.DefaultBallSpeed;
    public float BallRadius = 0.25f;
    public Vector2 BallStart = Vector2.zero;

    [Header("Networking")]
    public float StateUpdateRate = 30f;
    public bool DebugNetworkLogs = false;
    public float DebugLogRate = 1f;

    public List<LineConfig> Lines = new List<LineConfig>();

    [System.Serializable]
    public class LineConfig
    {
        public float RingAngleRad;
    }

    public class LineRuntime
    {
        public bool Assigned;
        public PongServer.ClientConnection Owner;
        public float RingAngleRad;
        public int Health;
    }

    public enum BallState { WaitingForPlayers, Playing, Won }

    PongServer _server;
    LineRuntime[] _runtime;
    Vector2 _ballPos;
    Vector2 _ballDir;
    BallState _state = BallState.WaitingForPlayers;
    int _winnerLine = -1;
    float _stateAccumulator;
    float _nextDebugPaddleLogTime;
    float _nextDebugStateLogTime;

    void Awake()
    {
        _server = GetComponent<PongServer>();
    }

    public void RebuildRuntime()
    {
        _runtime = new LineRuntime[Lines.Count];
        for (int i = 0; i < _runtime.Length; i++) {
            _runtime[i] = new LineRuntime {
                RingAngleRad = Lines[i].RingAngleRad,
            };
        }
    }

    void ExtendRuntimeForNewLine(int idx, float angle)
    {
        var next = new LineRuntime[Lines.Count];
        if (_runtime != null) {
            for (int i = 0; i < _runtime.Length && i < idx; i++) next[i] = _runtime[i];
        }
        next[idx] = new LineRuntime { RingAngleRad = angle };
        _runtime = next;
    }

    void ShrinkRuntimeAfterRemove(int removedIndex)
    {
        if (_runtime == null) return;
        var next = new LineRuntime[Lines.Count];
        int w = 0;
        for (int i = 0; i < _runtime.Length; i++) {
            if (i == removedIndex) continue;
            next[w++] = _runtime[i];
        }
        _runtime = next;
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
        Lines.Clear();
        RebuildRuntime();
        EnterWaitingForPlayers("startup");
    }

    void Update()
    {
        if (!_server.IsListening) return;
        EnsureRuntime();
        float dt = Time.unscaledDeltaTime;
        int playingCount = GetAliveAssignedCount();

        if (playingCount < MinPlayersToPlay) {
            if (_state != BallState.WaitingForPlayers) {
                EnterWaitingForPlayers("not enough players");
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

    void HandleClientConnected(PongServer.ClientConnection client)
    {
        if (Lines.Count >= CircleArenaConfig.MaxPlayers) {
            Debug.Log("PongServerGame: arena full, dropping client.");
            try { client.Tcp.Close(); } catch { /* ignore */ }
            return;
        }

        EnsureRuntime();
        int idx = Lines.Count;
        float angle = CircleArenaConfig.GetInitialAngleRad(idx, idx + 1);
        Lines.Add(new LineConfig { RingAngleRad = angle });
        ExtendRuntimeForNewLine(idx, angle);

        client.LineIndex = idx;
        var rt = _runtime[idx];
        rt.Assigned = true;
        rt.Owner = client;
        rt.Health = HealthAlive;
        RespreadPlayerAngles();

        BroadcastRosterAndAssign();
        Debug.Log("PongServerGame: player joined line " + idx + " (total " + Lines.Count + ").");
    }

    void HandleClientDisconnected(PongServer.ClientConnection client)
    {
        int idx = client.LineIndex;
        if (idx < 0 || idx >= Lines.Count) return;

        Lines.RemoveAt(idx);
        ShrinkRuntimeAfterRemove(idx);
        ReassignLineIndices();
        BroadcastRosterAndAssign();

        client.LineIndex = -1;

        if (GetAssignedCount() < MinPlayersToPlay) {
            EnterWaitingForPlayers("player disconnected");
        } else {
            CheckForLastPlayerStanding();
        }
    }

    void ReassignLineIndices()
    {
        RespreadPlayerAngles();
        BroadcastRosterAndAssign();
    }

    void HandleMessage(PongServer.ClientConnection client, string message)
    {
        if (string.IsNullOrEmpty(message)) return;

        int sp = message.IndexOf(PongProtocol.FieldSeparator);
        string head = sp < 0 ? message : message.Substring(0, sp);
        string tail = sp < 0 ? string.Empty : message.Substring(sp + 1);

        if (head == PongProtocol.MsgPaddle) {
            if (PongProtocol.TryParseFloat(tail, out float angleRad)) {
                if (client.LineIndex >= 0 && client.LineIndex < _runtime.Length) {
                    var rt = _runtime[client.LineIndex];
                    if (rt.Health >= HealthEliminated) return;
                    float clamped = CircleArenaConfig.NormalizeAngleRad(angleRad);
                    rt.RingAngleRad = clamped;
                    Lines[client.LineIndex].RingAngleRad = clamped;
                    if (DebugNetworkLogs && Time.time >= _nextDebugPaddleLogTime) {
                        _nextDebugPaddleLogTime = Time.time + GetDebugInterval();
                        Debug.Log("PongServerGame DBG PADDLE line=" + client.LineIndex
                            + " angle=" + clamped.ToString("0.###"));
                    }
                }
            }
        }
    }

    void ServeBall()
    {
        if (GetAliveAssignedCount() < MinPlayersToPlay) {
            EnterWaitingForPlayers("not enough players");
            return;
        }

        _ballPos = BallStart;
        _ballDir = Random.insideUnitCircle.normalized;
        if (_ballDir.sqrMagnitude < 1e-4f) _ballDir = Vector2.right;
        BallSpeed = CircleArenaConfig.DefaultBallSpeed;
        _state = BallState.Playing;
        _winnerLine = -1;
        _server.Broadcast(PongProtocol.FormatReset());
    }

    void StartMatch()
    {
        for (int i = 0; i < _runtime.Length; i++) {
            _runtime[i].Health = HealthAlive;
        }
        BroadcastRosterAndAssign();
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
        _server.Broadcast(PongProtocol.FormatReset());
        Debug.Log("PongServerGame: waiting (" + GetAssignedCount() + " connected) reason=" + reason);
    }

    void StepBall(float dt)
    {
        int steps = CircleArenaConfig.BallPhysicsSubsteps;
        float subDt = dt / steps;

        for (int step = 0; step < steps; step++) {
            _ballPos += _ballDir * BallSpeed * subDt;

            while (CircleArenaConfig.ReflectBallOffRing(ref _ballPos, ref _ballDir, BallRadius, ref BallSpeed)) { }

            for (int i = 0; i < Lines.Count; i++) {
                var rt = _runtime[i];
                if (!rt.Assigned || rt.Health >= HealthEliminated) continue;

                if (!CircleArenaConfig.BallHitsPlatform(_ballPos, BallRadius, rt.RingAngleRad)) continue;

                EliminatePlayer(i);

                Vector2 away = (_ballPos.sqrMagnitude > 1e-4f) ? _ballPos.normalized : Vector2.up;
                _ballPos = away * (CircleArenaConfig.GetBounceRadius(BallRadius) - 0.02f);
                if (_ballDir.sqrMagnitude > 1e-4f) {
                    _ballDir = Vector2.Reflect(_ballDir, away).normalized;
                }
                BallSpeed *= CircleArenaConfig.BallSpeedAccelPerBounce;
                return;
            }
        }
    }

    void RespreadPlayerAngles()
    {
        int n = Lines.Count;
        for (int i = 0; i < n; i++) {
            float angle = CircleArenaConfig.GetInitialAngleRad(i, n);
            Lines[i].RingAngleRad = angle;
            if (_runtime != null && i < _runtime.Length) {
                _runtime[i].RingAngleRad = angle;
            }
        }
    }

    void BroadcastRosterAndAssign()
    {
        int count = Lines.Count;
        _server.Broadcast(PongProtocol.FormatRoster(count));

        for (int i = 0; i < _runtime.Length; i++) {
            var rt = _runtime[i];
            if (!rt.Assigned || rt.Owner == null) continue;
            _server.Send(rt.Owner, PongProtocol.FormatAssign(i, count));
        }
    }

    void EliminatePlayer(int lineIndex)
    {
        var rt = _runtime[lineIndex];
        if (rt.Health >= HealthEliminated) return;

        rt.Health = HealthEliminated;
        _server.Broadcast(PongProtocol.FormatDamage(lineIndex, HealthEliminated));
        Debug.Log("PongServerGame: line " + lineIndex + " eliminated.");
        CheckForLastPlayerStanding();
    }

    void CheckForLastPlayerStanding()
    {
        int alive = 0;
        int lastAlive = -1;
        for (int i = 0; i < _runtime.Length; i++) {
            if (!_runtime[i].Assigned || _runtime[i].Health >= HealthEliminated) continue;
            alive++;
            lastAlive = i;
        }

        if (alive <= 1 && _state == BallState.Playing) {
            _state = BallState.Won;
            _winnerLine = lastAlive;
            if (lastAlive >= 0) {
                _server.Broadcast(PongProtocol.FormatWin(lastAlive));
            }
            Debug.Log("PongServerGame: winner line " + lastAlive);
        }
    }

    void BroadcastState()
    {
        if (_runtime == null || _runtime.Length == 0) return;
        var angles = new float[_runtime.Length];
        for (int i = 0; i < _runtime.Length; i++) {
            angles[i] = _runtime[i].RingAngleRad;
        }
        _server.Broadcast(PongProtocol.FormatState(_ballPos.x, _ballPos.y, angles));
    }

    float GetDebugInterval()
        => DebugLogRate > 0f ? 1f / DebugLogRate : 1f;

    int GetAssignedCount()
    {
        if (_runtime == null) return 0;
        int count = 0;
        for (int i = 0; i < _runtime.Length; i++) {
            if (_runtime[i].Assigned) count++;
        }
        return count;
    }

    int GetAliveAssignedCount()
    {
        if (_runtime == null) return 0;
        int count = 0;
        for (int i = 0; i < _runtime.Length; i++) {
            if (_runtime[i].Assigned && _runtime[i].Health < HealthEliminated) count++;
        }
        return count;
    }

    public int LineCount => Lines.Count;
}
