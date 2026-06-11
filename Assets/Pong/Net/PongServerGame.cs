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
        rt.Health = CircleArenaConfig.HealthIntact;
        rt.DisplayName = ResolveDisplayName(client, idx);
        rt.ColorSlot = PickUnusedColorSlot(idx);
        rt.RingAngleRad = ClampPlatformAngleAgainstPlayers(idx, angle, angle);
        Lines[idx].RingAngleRad = rt.RingAngleRad;

        BroadcastRosterAndAssign();
        Debug.Log("PongServerGame: " + rt.DisplayName + " joined line " + idx + " (total " + Lines.Count + ").");

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

        Lines.RemoveAt(idx);
        ShrinkRuntimeAfterRemove(idx);
        ReassignLineIndices();
        BroadcastRosterAndAssign();

        client.LineIndex = -1;

        // Mid-match: if only one alive player remains (or zero), end the round with a WIN
        // for the survivor so they don't get silently dumped into "waiting for players".
        // Eliminated paddles already don't count as alive, so this also covers the case
        // where every other player has already lost when the leaver disconnects.
        if (_state == BallState.Playing) {
            CheckForLastPlayerStanding();
        }

        if (GetAssignedCount() < MinPlayersToPlay
            && _state != BallState.WaitingForPlayers) {
            EnterWaitingForPlayers("player disconnected");
        }
    }

    void ReassignLineIndices()
    {
        // Keep surviving paddles at their current angles. Only the logical line index changes
        // after a disconnect; re-spreading every paddle makes the whole arena rotate at once.
    }

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

        if (head == PongProtocol.MsgPostGame || head == PongProtocol.MsgSpectate) {
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

        if (head == PongProtocol.MsgPaddle) {
            if (!client.InGame) return;
            if (PongProtocol.TryParseFloat(tail, out float angleRad)) {
                if (client.LineIndex >= 0 && client.LineIndex < _runtime.Length) {
                    var rt = _runtime[client.LineIndex];
                    if (rt.Health >= CircleArenaConfig.HealthEliminated) return;
                    float clamped = ClampPlatformAngleAgainstPlayers(
                        client.LineIndex,
                        rt.RingAngleRad,
                        angleRad);
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
        _ballDir = Random.insideUnitCircle;
        if (_ballDir.sqrMagnitude < 1e-4f) _ballDir = Random.insideUnitCircle;
        if (_ballDir.sqrMagnitude < 1e-4f) _ballDir = Vector2.right;
        _ballDir.Normalize();
        BallSpeed = CircleArenaConfig.DefaultBallSpeed;
        _wallBouncesSincePlayerHit = 0;
        _state = BallState.Playing;
        _winnerLine = -1;
        _server.Broadcast(PongProtocol.FormatReset());
    }

    void StartMatch()
    {
        if (GetInGameCount() < MinPlayersToPlay) {
            EnterWaitingForPlayers("not enough ready players");
            return;
        }

        _joinLobbyCountdownRemaining = 0f;
        _preMatchCountdownRemaining = 0f;
        _postRoundLobbyRemaining = 0f;
        // Readiness is consumed by the match start; the next round will require a fresh opt-in.
        ClearAllReadyForNextMatch();
        BroadcastCountdown(0);
        for (int i = 0; i < _runtime.Length; i++) {
            _runtime[i].Health = CircleArenaConfig.HealthIntact;
        }
        BroadcastRosterAndAssign();
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

            while (CircleArenaConfig.ReflectBallOffRing(ref _ballPos, ref _ballDir, BallRadius)) {
                HandleWallBounceWithoutPlayerHit();
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
            _server.Send(rt.Owner, PongProtocol.FormatAssign(i, count, rt.RingAngleRad));
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
