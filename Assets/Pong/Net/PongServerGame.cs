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

    [Header("Ball")]
    public float BallSpeed = CircleArenaConfig.DefaultBallSpeed;
    public float BallRadius = 0.25f;
    public Vector2 BallStart = Vector2.zero;

    [Header("Networking")]
    public float StateUpdateRate = 30f;
    public bool DebugNetworkLogs = false;
    public float DebugLogRate = 1f;

    [Header("Diagnostics")]
    [Tooltip("Write a periodic + on-anomaly diagnostics log to Application.persistentDataPath/pong-diag.log.")]
    public bool EnableDiagnostics = true;

    // Hard cap on bounce resolutions per substep. A healthy ball bounces at most once or twice;
    // anything more means a degenerate (NaN / escaped) state that would otherwise loop forever.
    const int MaxBounceIterationsPerSubstep = 8;
    // Finite ball that somehow got this far from the centre is treated as escaped and re-served.
    const float BallEscapeRadius = CircleArenaConfig.Radius * 2f;

    [Header("Post-round lobby")]
    [Tooltip("After a 2-player round ends, wait this long for more players before the next match.")]
    public float PostRoundLobbySeconds = 10f;
    [Tooltip("After the second player joins, wait this long for more players. Reset when another player joins.")]
    public float JoinLobbyCountdownSeconds = 10f;
    [Tooltip("Countdown shown to every connected client before a match starts.")]
    public float PreMatchCountdownSeconds = 5f;

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
        public string DisplayName = string.Empty;
        public int ColorSlot = -1;
    }

    public enum BallState { WaitingForPlayers, LobbyCountdown, Starting, Playing, Won }

    PongServer _server;
    LineRuntime[] _runtime;
    Vector2 _ballPos;
    Vector2 _ballDir;
    BallState _state = BallState.WaitingForPlayers;
    int _winnerLine = -1;
    int _wallBouncesSincePlayerHit;
    float _stateAccumulator;
    float _joinLobbyCountdownRemaining;
    float _preMatchCountdownRemaining;
    float _postRoundLobbyRemaining;
    int _lastBroadcastCountdown = -1;
    float _nextDebugPaddleLogTime;
    float _nextDebugStateLogTime;
    uint _stateSeq;
    readonly System.Diagnostics.Stopwatch _clock = new System.Diagnostics.Stopwatch();
    float _diagAccum;
    float _diagMaxFrameDt;

    void Awake()
    {
        _server = GetComponent<PongServer>();
        _clock.Start();
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
        _server.OnUdpPaddle += HandleUdpPaddle;
    }

    void OnDisable()
    {
        if (_server != null) {
            _server.OnClientConnected -= HandleClientConnected;
            _server.OnClientDisconnected -= HandleClientDisconnected;
            _server.OnMessageReceived -= HandleMessage;
            _server.OnUdpPaddle -= HandleUdpPaddle;
        }
        PongDiagnostics.Close();
    }

    void Start()
    {
        if (EnableDiagnostics) PongDiagnostics.Init("server");
        Lines.Clear();
        RebuildRuntime();
        EnterWaitingForPlayers("startup");
    }

    void Update()
    {
        if (!_server.IsListening) return;
        EnsureRuntime();
        float dt = Time.unscaledDeltaTime;
        if (EnableDiagnostics) TickDiagnostics(dt);
        int connectedCount = GetAssignedCount();
        int inGameCount = GetInGameCount();

        if (connectedCount < MinPlayersToPlay) {
            if (_state != BallState.WaitingForPlayers) {
                EnterWaitingForPlayers("not enough players");
            }
        } else if (_state == BallState.WaitingForPlayers && inGameCount >= MinPlayersToPlay) {
            BeginJoinLobbyCountdown("minimum players reached");
        }

        if (_state == BallState.LobbyCountdown) {
            TickJoinLobbyCountdown(dt, inGameCount);
        } else if (_state == BallState.Starting) {
            TickPreMatchCountdown(dt, inGameCount);
        } else if (_state == BallState.Playing) {
            StepBall(dt);
        } else if (_state == BallState.Won) {
            TickPostRoundLobby(dt, connectedCount, inGameCount);
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
        rt.DisplayName = ResolveDisplayName(client, idx);
        rt.ColorSlot = PickUnusedColorSlot(idx);

        if (MatchInProgress) {
            // A round is already running: don't reshuffle the live players. The newcomer joins as
            // a spectator (eliminated for the current round, so the ball ignores them and their
            // platform stays hidden client-side) and is folded into the next round by StartMatch,
            // which is the only place that re-spreads angles. NOT re-spreading here is what keeps
            // existing paddles from teleporting / overlapping mid-match.
            rt.Health = CircleArenaConfig.HealthEliminated;
            BroadcastRosterAndAssign();
            _server.Broadcast(PongProtocol.FormatDamage(idx, CircleArenaConfig.HealthEliminated));
            Debug.Log("PongServerGame: " + rt.DisplayName + " joined mid-match as spectator on line "
                + idx + " (total " + Lines.Count + ").");
            if (EnableDiagnostics) {
                PongDiagnostics.Log("ClientConnected line=" + idx + " total=" + Lines.Count
                    + " conns=" + _server.ConnectionCount + " spectator=1");
            }
            return;
        }

        rt.Health = CircleArenaConfig.HealthIntact;
        RespreadPlayerAngles();

        BroadcastRosterAndAssign();
        Debug.Log("PongServerGame: " + rt.DisplayName + " joined line " + idx + " (total " + Lines.Count + ").");
        if (EnableDiagnostics) {
            PongDiagnostics.Log("ClientConnected line=" + idx + " total=" + Lines.Count
                + " conns=" + _server.ConnectionCount);
        }

        if ((_state == BallState.WaitingForPlayers
                || _state == BallState.LobbyCountdown)
            && GetInGameCount() >= MinPlayersToPlay) {
            BeginJoinLobbyCountdown("player joined");
            return;
        }

        if (_state == BallState.Won) {
            TryStartMatchWhenReady();
            if (_postRoundLobbyRemaining > 0f) {
                _server.Send(client, PongProtocol.FormatCountdown(GetPostRoundCountdownSeconds()));
            }
        }
    }

    void HandleClientDisconnected(PongServer.ClientConnection client)
    {
        int idx = client.LineIndex;
        if (idx < 0 || idx >= Lines.Count) return;

        if (EnableDiagnostics) {
            PongDiagnostics.Log("ClientDisconnected line=" + idx + " remaining=" + (Lines.Count - 1));
        }

        client.LineIndex = -1;

        // Remove the line immediately so a reconnect can't resurrect a stale "ghost" paddle.
        Lines.RemoveAt(idx);
        ShrinkRuntimeAfterRemove(idx);

        if (_state == BallState.Playing) {
            // Mid-match: do NOT teleport survivors to canonical angles. Doing so would snap
            // their paddle onto the in-flight ball and trigger a phantom elimination, which
            // is exactly what makes another player wrongly "lose" when the eliminated leaver
            // disconnects. We still need to make sure two survivors aren't overlapping under
            // the (now wider) platform arc for the smaller player count.
            EnforceSurvivorSeparation();
        } else {
            RespreadPlayerAngles();
        }

        BroadcastRosterAndAssign();

        // A survivor (or nobody) left alive: end the round with a WIN rather than silently
        // dropping into "waiting".
        if (_state == BallState.Playing) {
            CheckForLastPlayerStanding();
        }

        if (GetAssignedCount() < MinPlayersToPlay
            && _state != BallState.WaitingForPlayers) {
            EnterWaitingForPlayers("player disconnected");
        }
    }

    /// <summary>
    /// True while a round's roster is locked (pre-match countdown or active play). A player who
    /// joins during this window must not re-spread angles (which would teleport live paddles); they
    /// spectate the current round and are folded in at the next round boundary.
    /// </summary>
    bool MatchInProgress => _state == BallState.Starting || _state == BallState.Playing;

    void HandleMessage(PongServer.ClientConnection client, string message)
    {
        if (string.IsNullOrEmpty(message)) return;

        int sp = message.IndexOf(PongProtocol.FieldSeparator);
        string head = sp < 0 ? message : message.Substring(0, sp);
        string tail = sp < 0 ? string.Empty : message.Substring(sp + 1);

        if (head == PongProtocol.MsgName) {
            ApplyClientDisplayName(client, tail);
            return;
        }

        if (head == PongProtocol.MsgReady) {
            client.ReadyForNextMatch = true;
            SetClientInGame(client, true);
            return;
        }

        if (head == PongProtocol.MsgPostGame) {
            client.ReadyForNextMatch = false;
            SetClientInGame(client, false);
            return;
        }

        if (head == PongProtocol.MsgColor) {
            if (PongProtocol.TryParseInt(tail, out int slot)) {
                ApplyColorChoice(client, slot);
            }
            return;
        }
    }

    /// <summary>
    /// Paddle update from the real-time UDP channel. The client is already resolved from its
    /// token by PongServer; we only validate game state and clamp against neighbours (anti-cheat).
    /// </summary>
    void HandleUdpPaddle(PongServer.ClientConnection client, float angleRad)
    {
        if (client == null || !client.InGame) return;
        EnsureRuntime();
        if (client.LineIndex < 0 || _runtime == null || client.LineIndex >= _runtime.Length) return;

        var rt = _runtime[client.LineIndex];
        if (rt.Health >= CircleArenaConfig.HealthEliminated) return;

        float clamped = ClampPlatformAngleAgainstPlayers(client.LineIndex, rt.RingAngleRad, angleRad);
        rt.RingAngleRad = clamped;
        Lines[client.LineIndex].RingAngleRad = clamped;

        if (DebugNetworkLogs && Time.time >= _nextDebugPaddleLogTime) {
            _nextDebugPaddleLogTime = Time.time + GetDebugInterval();
            Debug.Log("PongServerGame DBG PADDLE(udp) line=" + client.LineIndex
                + " angle=" + clamped.ToString("0.###"));
        }
    }

    void ServeBall()
    {
        if (GetAliveAssignedCount() < MinPlayersToPlay) {
            EnterWaitingForPlayers("not enough players");
            return;
        }

        _ballPos = BallStart;
        _ballDir = Random.insideUnitCircle;
        if (_ballDir.sqrMagnitude < 1e-4f) _ballDir = Random.insideUnitCircle;
        if (_ballDir.sqrMagnitude < 1e-4f) _ballDir = Vector2.right;
        _ballDir.Normalize();
        BallSpeed = CircleArenaConfig.DefaultBallSpeed;
        _wallBouncesSincePlayerHit = 0;
        _state = BallState.Playing;
        _winnerLine = -1;
        _server.Broadcast(PongProtocol.FormatReset());
        if (EnableDiagnostics) {
            PongDiagnostics.Log(string.Format(
                "ServeBall dir=({0:0.00},{1:0.00}) speed={2:0.0} players={3}",
                _ballDir.x, _ballDir.y, BallSpeed, GetAliveAssignedCount()));
        }
    }

    void StartMatch()
    {
        // Only players who explicitly opted into THIS match (READY — sent on connect and again from
        // the end-of-round menu) take part. A player idle on the end-of-round modal never readied,
        // so they must stay a spectator instead of being silently dragged into the next round.
        if (GetReadyForNextMatchCount() < MinPlayersToPlay) {
            EnterWaitingForPlayers("not enough ready players");
            return;
        }

        _joinLobbyCountdownRemaining = 0f;
        _preMatchCountdownRemaining = 0f;
        _postRoundLobbyRemaining = 0f;

        // Decide participation BEFORE clearing readiness: readied owners play (Intact), everyone
        // else spectates this round (Eliminated → ball ignores them, platform hidden client-side).
        for (int i = 0; i < _runtime.Length; i++) {
            var rt = _runtime[i];
            bool participates = rt.Assigned && rt.Owner != null && rt.Owner.ReadyForNextMatch;
            rt.Health = participates ? CircleArenaConfig.HealthIntact : CircleArenaConfig.HealthEliminated;
        }
        // Readiness is consumed by the match start; the next round will require a fresh opt-in.
        ClearAllReadyForNextMatch();
        BroadcastCountdown(0);
        // Re-spread everyone exactly once, at the round boundary. This is the only point where
        // angles change for an established player.
        RespreadPlayerAngles();
        BroadcastRosterAndAssign();
        // Tell clients which lines are spectating so their platforms are hidden for the round.
        for (int i = 0; i < _runtime.Length; i++) {
            if (_runtime[i].Assigned && _runtime[i].Health >= CircleArenaConfig.HealthEliminated) {
                _server.Broadcast(PongProtocol.FormatDamage(i, CircleArenaConfig.HealthEliminated));
            }
        }
        Debug.Log("PongServerGame: MATCH_STARTED");
        ServeBall();
    }

    void BeginJoinLobbyCountdown(string reason)
    {
        if (GetInGameCount() < MinPlayersToPlay) return;
        if (_state == BallState.Playing || _state == BallState.Won) return;

        _state = BallState.LobbyCountdown;
        _winnerLine = -1;
        _ballPos = BallStart;
        _ballDir = Vector2.zero;
        _preMatchCountdownRemaining = 0f;
        _joinLobbyCountdownRemaining = JoinLobbyCountdownSeconds > 0f ? JoinLobbyCountdownSeconds : 10f;
        _lastBroadcastCountdown = -1;
        BroadcastJoinCountdown(GetJoinLobbyCountdownSeconds());
        BroadcastRosterAndAssign();
        Debug.Log("PongServerGame: LOBBY_COUNTDOWN reset to "
            + _joinLobbyCountdownRemaining.ToString("0.#") + "s reason=" + reason);
    }

    void TickJoinLobbyCountdown(float dt, int inGameCount)
    {
        if (inGameCount < MinPlayersToPlay) {
            EnterWaitingForPlayers("not enough ready players during lobby countdown");
            return;
        }

        _joinLobbyCountdownRemaining -= dt;
        BroadcastJoinCountdown(GetJoinLobbyCountdownSeconds());

        if (_joinLobbyCountdownRemaining <= 0f) {
            BroadcastJoinCountdown(0);
            BeginPreMatchCountdown();
        }
    }

    void BeginPreMatchCountdown()
    {
        if (_state == BallState.Starting || _state == BallState.Playing) return;
        if (GetInGameCount() < MinPlayersToPlay) return;

        _state = BallState.Starting;
        _winnerLine = -1;
        _ballPos = BallStart;
        _ballDir = Vector2.zero;
        _preMatchCountdownRemaining = PreMatchCountdownSeconds > 0f ? PreMatchCountdownSeconds : 5f;
        _lastBroadcastCountdown = -1;
        BroadcastCountdown(GetPreMatchCountdownSeconds());
        BroadcastRosterAndAssign();
        Debug.Log("PongServerGame: MATCH_STARTING in "
            + _preMatchCountdownRemaining.ToString("0.#") + "s.");
    }

    void TickPreMatchCountdown(float dt, int inGameCount)
    {
        if (inGameCount < MinPlayersToPlay) {
            EnterWaitingForPlayers("not enough ready players during countdown");
            return;
        }

        _preMatchCountdownRemaining -= dt;
        int seconds = GetPreMatchCountdownSeconds();
        BroadcastCountdown(seconds);

        if (_preMatchCountdownRemaining <= 0f) {
            StartMatch();
        }
    }

    void BeginPostRoundLobby()
    {
        _postRoundLobbyRemaining = PostRoundLobbySeconds > 0f ? PostRoundLobbySeconds : 10f;
        _lastBroadcastCountdown = -1;
        BroadcastCountdown(GetPostRoundCountdownSeconds());
        Debug.Log("PongServerGame: waiting " + _postRoundLobbyRemaining.ToString("0.#")
            + "s for more players before next match.");
    }

    void ScheduleNextMatchAfterWin()
    {
        int connectedCount = GetAssignedCount();
        if (connectedCount < MinPlayersToPlay) {
            EnterWaitingForPlayers("not enough players after win");
            return;
        }

        // Open the post-round lobby. The next match only starts once enough players have
        // explicitly readied (see TickPostRoundLobby / TryStartMatchWhenReady).
        BeginPostRoundLobby();
    }

    void TickPostRoundLobby(float dt, int connectedCount, int inGameCount)
    {
        if (connectedCount < MinPlayersToPlay) {
            EnterWaitingForPlayers("not enough players");
            return;
        }

        // The next match may only begin once the grace period has elapsed AND enough players
        // have *explicitly* readied. Counting readiness (not the default InGame flag) is what
        // prevents a single "Stay for next match" click from launching the round on its own.
        if (_postRoundLobbyRemaining > 0f) {
            _postRoundLobbyRemaining -= dt;
            BroadcastCountdown(GetPostRoundCountdownSeconds());
            return;
        }

        if (GetReadyForNextMatchCount() >= MinPlayersToPlay) {
            BeginPreMatchCountdown();
            return;
        }

        // Grace period is over but we still don't have enough ready players: hold the lobby
        // open (timer pinned at 0) instead of decrementing into negatives forever.
        _postRoundLobbyRemaining = 0f;
        BroadcastCountdown(0);
    }

    void SetClientInGame(PongServer.ClientConnection client, bool inGame)
    {
        if (client.InGame == inGame) return;
        client.InGame = inGame;
        string who = client.LineIndex >= 0
            ? ResolveDisplayName(client, client.LineIndex)
            : PongProtocol.SanitizePlayerName(client.DisplayName);
        if (string.IsNullOrEmpty(who)) who = "Player";
        Debug.Log("PongServerGame: " + who
            + (inGame ? " joined the queue." : " left the queue (post-game menu / spectating)."));
        TryStartMatchWhenReady();
    }

    void TryStartMatchWhenReady()
    {
        if (GetInGameCount() < MinPlayersToPlay) return;

        if (_state == BallState.WaitingForPlayers) {
            BeginJoinLobbyCountdown("players ready");
            return;
        }

        if (_state == BallState.LobbyCountdown) {
            return;
        }

        // After a round, only restart once the grace period elapsed *and* enough players have
        // explicitly readied. This is the path hit when the last needed player clicks
        // "Stay for next match"; one ready player must never be enough.
        if (_state == BallState.Won
            && _postRoundLobbyRemaining <= 0f
            && GetReadyForNextMatchCount() >= MinPlayersToPlay) {
            BeginPreMatchCountdown();
        }
    }

    int GetJoinLobbyCountdownSeconds()
    {
        if (_joinLobbyCountdownRemaining <= 0f) return 0;
        return Mathf.CeilToInt(_joinLobbyCountdownRemaining);
    }

    int GetPreMatchCountdownSeconds()
    {
        if (_preMatchCountdownRemaining <= 0f) return 0;
        return Mathf.CeilToInt(_preMatchCountdownRemaining);
    }

    int GetPostRoundCountdownSeconds()
    {
        if (_postRoundLobbyRemaining <= 0f) return 0;
        return Mathf.CeilToInt(_postRoundLobbyRemaining);
    }

    void BroadcastCountdown(int seconds)
    {
        if (seconds == _lastBroadcastCountdown) return;
        _lastBroadcastCountdown = seconds;
        _server.Broadcast(PongProtocol.FormatCountdown(seconds));
    }

    void BroadcastJoinCountdown(int seconds)
    {
        if (seconds == _lastBroadcastCountdown) return;
        _lastBroadcastCountdown = seconds;
        _server.Broadcast(PongProtocol.FormatJoinCountdown(seconds));
    }

    void EnterWaitingForPlayers(string reason)
    {
        CancelInvoke(nameof(ServeBall));
        _joinLobbyCountdownRemaining = 0f;
        _preMatchCountdownRemaining = 0f;
        _postRoundLobbyRemaining = 0f;
        BroadcastCountdown(0);
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

            // Safeguard: a NaN/Inf or wildly escaped ball must never enter the bounce loop, which
            // would otherwise spin forever (NaN compares false against every bound) and freeze the
            // server. Detect and re-serve instead.
            if (!IsBallStateFinite() || _ballPos.magnitude > BallEscapeRadius) {
                RecoverBall(IsBallStateFinite() ? "ball-escaped" : "ball-nonfinite");
                return;
            }

            int bounceGuard = 0;
            while (CircleArenaConfig.ReflectBallOffRing(ref _ballPos, ref _ballDir, BallRadius)) {
                HandleWallBounceWithoutPlayerHit();
                if (++bounceGuard >= MaxBounceIterationsPerSubstep) {
                    RecoverBall("bounce-loop");
                    return;
                }
            }

            for (int i = 0; i < Lines.Count; i++) {
                var rt = _runtime[i];
                if (!rt.Assigned || rt.Health >= CircleArenaConfig.HealthEliminated) continue;

                if (!CircleArenaConfig.BallHitsPlatform(_ballPos, BallRadius, rt.RingAngleRad, Lines.Count)) continue;

                EliminatePlayer(i);
                _wallBouncesSincePlayerHit = 0;

                Vector2 away = (_ballPos.sqrMagnitude > 1e-4f) ? _ballPos.normalized : Vector2.up;
                _ballPos = away * (CircleArenaConfig.GetBounceRadius(BallRadius) - 0.02f);
                if (_ballDir.sqrMagnitude > 1e-4f) {
                    _ballDir = Vector2.Reflect(_ballDir, away).normalized;
                }
                return;
            }
        }
    }

    static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

    bool IsBallStateFinite()
        => IsFinite(_ballPos.x) && IsFinite(_ballPos.y) && IsFinite(_ballDir.x) && IsFinite(_ballDir.y);

    /// <summary>
    /// Bring a degenerate ball back to a sane, in-bounds state instead of letting the simulation
    /// freeze. Keeps the match running; logged so we can see how often (and why) it triggers.
    /// </summary>
    void RecoverBall(string reason)
    {
        if (EnableDiagnostics) {
            PongDiagnostics.Warn(string.Format(
                "RecoverBall reason={0} pos=({1},{2}) dir=({3},{4}) speed={5} state={6}",
                reason, _ballPos.x, _ballPos.y, _ballDir.x, _ballDir.y, BallSpeed, _state));
        }

        _ballPos = BallStart;
        _ballDir = Random.insideUnitCircle;
        if (_ballDir.sqrMagnitude < 1e-4f) _ballDir = Vector2.right;
        _ballDir.Normalize();
        BallSpeed = Mathf.Clamp(BallSpeed, CircleArenaConfig.DefaultBallSpeed, CircleArenaConfig.MaxBallSpeed);
        _wallBouncesSincePlayerHit = 0;
    }

    void TickDiagnostics(float dt)
    {
        if (dt > _diagMaxFrameDt) _diagMaxFrameDt = dt;
        _diagAccum += dt;
        if (_diagAccum < 1f) return;

        long mem = System.GC.GetTotalMemory(false);
        PongDiagnostics.Log(string.Format(
            "tick state={0} ball=({1:0.00},{2:0.00}) |dir|={3:0.000} speed={4:0.0} conns={5} udpSeq={6} maxFrameMs={7:0.0} mem={8:0.0}MB",
            _state, _ballPos.x, _ballPos.y, _ballDir.magnitude, BallSpeed,
            _server.ConnectionCount, _stateSeq, _diagMaxFrameDt * 1000f, mem / 1048576.0));

        _diagAccum = 0f;
        _diagMaxFrameDt = 0f;
    }

    void HandleWallBounceWithoutPlayerHit()
    {
        _wallBouncesSincePlayerHit++;
        AddWallBounceJitter();

        if (_wallBouncesSincePlayerHit < CircleArenaConfig.WallBouncesBeforeSpeedUp) return;

        _wallBouncesSincePlayerHit = 0;
        BallSpeed = Mathf.Min(
            BallSpeed * CircleArenaConfig.BallSpeedAccelAfterMisses,
            CircleArenaConfig.MaxBallSpeed);
    }

    void AddWallBounceJitter()
    {
        if (_ballDir.sqrMagnitude < 1e-6f) return;

        float jitter = Random.Range(
            -CircleArenaConfig.WallBounceAngleJitterDegrees,
            CircleArenaConfig.WallBounceAngleJitterDegrees);
        _ballDir = Quaternion.Euler(0f, 0f, jitter) * _ballDir;
        _ballDir.Normalize();
    }

    float ClampPlatformAngleAgainstPlayers(int lineIndex, float currentAngleRad, float desiredAngleRad)
    {
        desiredAngleRad = CircleArenaConfig.NormalizeAngleRad(desiredAngleRad);
        if (_runtime == null || lineIndex < 0 || lineIndex >= _runtime.Length) return desiredAngleRad;

        float moveDelta = CircleArenaConfig.SignedAngleDeltaRad(currentAngleRad, desiredAngleRad);
        float fallbackSign = moveDelta >= 0f ? 1f : -1f;

        for (int pass = 0; pass < 2; pass++) {
            for (int i = 0; i < _runtime.Length; i++) {
                if (i == lineIndex) continue;
                var other = _runtime[i];
                if (other == null || !other.Assigned || other.Health >= CircleArenaConfig.HealthEliminated) continue;

                desiredAngleRad = CircleArenaConfig.ClampOutsidePlatform(
                    desiredAngleRad,
                    other.RingAngleRad,
                    _runtime.Length,
                    fallbackSign);
            }
        }

        return desiredAngleRad;
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

    /// <summary>
    /// Keeps mid-match survivors at their current ring angles when a player leaves, only
    /// nudging them apart if the wider 2-player arc would now make them overlap. This is the
    /// no-teleport alternative to <see cref="RespreadPlayerAngles"/> — using respread mid-play
    /// would snap a paddle onto the in-flight ball and cause a phantom elimination.
    /// </summary>
    void EnforceSurvivorSeparation()
    {
        if (_runtime == null) return;
        for (int i = 0; i < _runtime.Length; i++) {
            var rt = _runtime[i];
            if (rt == null || !rt.Assigned) continue;
            if (rt.Health >= CircleArenaConfig.HealthEliminated) continue;

            float clamped = ClampPlatformAngleAgainstPlayers(i, rt.RingAngleRad, rt.RingAngleRad);
            if (!Mathf.Approximately(clamped, rt.RingAngleRad)) {
                rt.RingAngleRad = clamped;
                Lines[i].RingAngleRad = clamped;
            }
        }
    }

    void BroadcastRosterAndAssign()
    {
        int count = Lines.Count;
        _server.Broadcast(PongProtocol.FormatRoster(count));
        BroadcastNames();
        BroadcastColors();

        for (int i = 0; i < _runtime.Length; i++) {
            var rt = _runtime[i];
            if (!rt.Assigned || rt.Owner == null) continue;
            // After a disconnect-driven shift, the runtime entry may have moved to a new index.
            // Resync the connection's stored LineIndex so PADDLE / READY messages from this
            // client target the right runtime slot. Without this, a surviving player whose
            // index shifted would silently control a ghost slot.
            rt.Owner.LineIndex = i;
            _server.Send(rt.Owner, PongProtocol.FormatAssign(i, count));
        }
    }

    void BroadcastNames()
    {
        if (_runtime == null || _runtime.Length == 0) return;
        var names = new string[_runtime.Length];
        for (int i = 0; i < _runtime.Length; i++) {
            names[i] = _runtime[i].DisplayName;
        }
        _server.Broadcast(PongProtocol.FormatNames(names));
    }

    void BroadcastColors()
    {
        if (_runtime == null || _runtime.Length == 0) return;
        var slots = new int[_runtime.Length];
        for (int i = 0; i < _runtime.Length; i++) {
            slots[i] = _runtime[i].Assigned ? _runtime[i].ColorSlot : -1;
        }
        _server.Broadcast(PongProtocol.FormatColors(slots));
    }

    /// <summary>
    /// Lowest palette index not currently held by another assigned runtime. The newly-joining
    /// runtime at <paramref name="excludeIndex"/> is skipped because its slot is being decided
    /// right now. Falls back to a wrap-around index if the palette is somehow exhausted.
    /// </summary>
    int PickUnusedColorSlot(int excludeIndex)
    {
        var palette = CircleArenaConfig.PlayerPalette;
        if (palette == null || palette.Length == 0) return -1;

        for (int slot = 0; slot < palette.Length; slot++) {
            bool taken = false;
            if (_runtime != null) {
                for (int i = 0; i < _runtime.Length; i++) {
                    if (i == excludeIndex) continue;
                    var other = _runtime[i];
                    if (other != null && other.Assigned && other.ColorSlot == slot) {
                        taken = true;
                        break;
                    }
                }
            }
            if (!taken) return slot;
        }

        return excludeIndex % palette.Length;
    }

    /// <summary>
    /// Client-requested palette color. If another assigned player already holds that slot,
    /// swap the two colors so every assigned slot stays unique and the request always takes
    /// effect (instead of silently failing).
    /// </summary>
    void ApplyColorChoice(PongServer.ClientConnection client, int slot)
    {
        var palette = CircleArenaConfig.PlayerPalette;
        if (palette == null || slot < 0 || slot >= palette.Length) return;
        if (client.LineIndex < 0 || client.LineIndex >= _runtime.Length) return;

        var rt = _runtime[client.LineIndex];
        if (!rt.Assigned || rt.ColorSlot == slot) return;

        for (int i = 0; i < _runtime.Length; i++) {
            if (i == client.LineIndex) continue;
            var other = _runtime[i];
            if (other != null && other.Assigned && other.ColorSlot == slot) {
                other.ColorSlot = rt.ColorSlot;
                break;
            }
        }

        rt.ColorSlot = slot;
        BroadcastColors();
    }

    void ApplyClientDisplayName(PongServer.ClientConnection client, string rawName)
    {
        string safe = PongProtocol.SanitizePlayerName(rawName);
        if (string.IsNullOrEmpty(safe)) return;

        client.DisplayName = safe;
        if (client.LineIndex >= 0 && client.LineIndex < _runtime.Length) {
            _runtime[client.LineIndex].DisplayName = safe;
            BroadcastNames();
        }
    }

    static string ResolveDisplayName(PongServer.ClientConnection client, int lineIndex)
    {
        string safe = PongProtocol.SanitizePlayerName(client.DisplayName);
        return string.IsNullOrEmpty(safe) ? PongProtocol.DefaultPlayerName(lineIndex) : safe;
    }

    void EliminatePlayer(int lineIndex)
    {
        var rt = _runtime[lineIndex];
        if (rt.Health >= CircleArenaConfig.HealthEliminated) return;

        rt.Health = CircleArenaConfig.HealthEliminated;
        _server.Broadcast(PongProtocol.FormatDamage(lineIndex, CircleArenaConfig.HealthEliminated));
        Debug.Log("PongServerGame: line " + lineIndex + " eliminated.");
        if (EnableDiagnostics) {
            PongDiagnostics.Log("EliminatePlayer line=" + lineIndex
                + " ball=(" + _ballPos.x.ToString("0.00") + "," + _ballPos.y.ToString("0.00") + ")");
        }
        CheckForLastPlayerStanding();
    }

    void CheckForLastPlayerStanding()
    {
        int alive = 0;
        int lastAlive = -1;
        for (int i = 0; i < _runtime.Length; i++) {
            if (!_runtime[i].Assigned || _runtime[i].Health >= CircleArenaConfig.HealthEliminated) continue;
            alive++;
            lastAlive = i;
        }

        if (alive <= 1 && _state == BallState.Playing) {
            _state = BallState.Won;
            _winnerLine = lastAlive;
            // A new round must be opted into explicitly. Clear every stale readiness flag so
            // the next match can only start once enough players actively choose to restart.
            ClearAllReadyForNextMatch();
            if (lastAlive >= 0) {
                string winnerName = lastAlive < _runtime.Length
                    ? _runtime[lastAlive].DisplayName
                    : string.Empty;
                _server.Broadcast(PongProtocol.FormatWin(lastAlive, winnerName));
            }
            Debug.Log("PongServerGame: winner line " + lastAlive);
            ScheduleNextMatchAfterWin();
        }
    }

    void BroadcastState()
    {
        if (_runtime == null || _runtime.Length == 0) return;
        var angles = new float[_runtime.Length];
        for (int i = 0; i < _runtime.Length; i++) {
            angles[i] = _runtime[i].RingAngleRad;
        }
        // Ball velocity lets the client extrapolate through dropped/late datagrams; it is zero
        // whenever the ball is not in play (_ballDir == 0).
        Vector2 vel = _ballDir * BallSpeed;
        long serverTimeMs = _clock.ElapsedMilliseconds;
        _server.BroadcastStateUdp(PongProtocol.FormatState(
            _stateSeq++, serverTimeMs,
            _ballPos.x, _ballPos.y, vel.x, vel.y,
            angles));
    }

    float GetDebugInterval()
        => DebugLogRate > 0f ? 1f / DebugLogRate : 1f;

    int GetAssignedCount()
    {
        if (_runtime == null) return 0;
        int count = 0;
        for (int i = 0; i < _runtime.Length; i++) {
            // Skip vacated ghosts (a line whose owner disconnected mid-match but isn't removed yet),
            // so "connected players" reflects who's really here, not stale roster slots.
            if (_runtime[i].Assigned && _runtime[i].Owner != null) count++;
        }
        return count;
    }

    int GetInGameCount()
    {
        if (_runtime == null) return 0;
        int count = 0;
        for (int i = 0; i < _runtime.Length; i++) {
            var rt = _runtime[i];
            if (!rt.Assigned || rt.Owner == null || !rt.Owner.InGame) continue;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Number of assigned players who have *explicitly* readied for the next match
    /// since the current round ended. Unlike <see cref="GetInGameCount"/>, this never
    /// counts a connection's default/stale state, so it is the authority for restarting.
    /// </summary>
    int GetReadyForNextMatchCount()
    {
        if (_runtime == null) return 0;
        int count = 0;
        for (int i = 0; i < _runtime.Length; i++) {
            var rt = _runtime[i];
            if (!rt.Assigned || rt.Owner == null || !rt.Owner.ReadyForNextMatch) continue;
            count++;
        }
        return count;
    }

    void ClearAllReadyForNextMatch()
    {
        if (_server == null) return;
        var conns = _server.Connections;
        for (int i = 0; i < conns.Count; i++) {
            if (conns[i] != null) conns[i].ReadyForNextMatch = false;
        }
    }

    int GetAliveAssignedCount()
    {
        if (_runtime == null) return 0;
        int count = 0;
        for (int i = 0; i < _runtime.Length; i++) {
            if (_runtime[i].Assigned && _runtime[i].Health < CircleArenaConfig.HealthEliminated) count++;
        }
        return count;
    }

    public int LineCount => Lines.Count;
}
