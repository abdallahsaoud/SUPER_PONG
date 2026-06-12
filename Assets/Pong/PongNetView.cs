using UnityEngine;
using System.Collections.Generic;

public class PongNetView : MonoBehaviour
{
    public PongClient Client;
    public Transform Ball;
    public PongCircleArena CircleArena;

    [Header("Interpolation")]
    [Tooltip("Render this far behind the latest server time, in ms. Higher = smoother but more lag. "
        + "~100ms hides typical LAN jitter and one or two lost datagrams.")]
    public float InterpolationDelayMs = 100f;
    [Tooltip("Max time the ball may be extrapolated past the last snapshot during a packet gap, in ms.")]
    public float MaxExtrapolationMs = 250f;
    [Tooltip("How long old snapshots are kept in the interpolation buffer, in ms.")]
    public float MaxBufferMs = 1000f;

    public bool IsLineEliminated(int lineIndex)
    {
        return _lineHealth != null
            && lineIndex >= 0
            && lineIndex < _lineHealth.Length
            && _lineHealth[lineIndex] >= CircleArenaConfig.HealthEliminated;
    }

    public bool IsLocalPlayerEliminated()
    {
        return Client != null && Client.LineIndex >= 0 && IsLineEliminated(Client.LineIndex);
    }

    struct Snapshot
    {
        public double TimeMs;
        public Vector2 BallPos;
        public Vector2 BallVel;
        public float[] Angles;
    }

    readonly List<Snapshot> _snaps = new List<Snapshot>(32);
    double _renderTimeMs;
    bool _renderInit;
    float[] _sampleAngles;
    bool _hasBall;

    float[] _targetAngles;
    float[] _displayAngles;
    int[] _lineHealth;
    int[] _lineColorSlots;
    PongClient _subscribedClient;
    PongNetPaddle _localPaddle;
    int _lastSyncedLineIndex = -2;

    void OnEnable()
    {
        _localPaddle = GetComponent<PongNetPaddle>();
    }

    /// <summary>Call after Client reference is set (OnEnable runs before Bootstrap wires Client).</summary>
    public void ForceBindAndSync()
    {
        BindClientEvents();
        SyncFromClientSession();
    }

    /// <summary>
    /// Wipe per-session state (paddle angles, ball, health). Call when the client disconnects
    /// or before reconnecting, otherwise a stale eliminated-flag from a prior game can carry
    /// over and instantly flag the new session as "lost".
    /// </summary>
    public void ResetSessionState()
    {
        ClearSnapshots();
        _targetAngles = null;
        _displayAngles = null;
        _lineHealth = null;
        _lineColorSlots = null;
        _lastSyncedLineIndex = -2;
        if (CircleArena != null) CircleArena.SetPlatformCount(0);
    }

    void ClearSnapshots()
    {
        _snaps.Clear();
        _renderInit = false;
        _hasBall = false;
    }

    void OnDisable()
    {
        UnbindClientEvents();
    }

    void BindClientEvents()
    {
        if (_subscribedClient == Client) return;
        UnbindClientEvents();
        if (Client == null) return;

        Client.OnAssign += HandleAssign;
        Client.OnRoster += HandleRoster;
        Client.OnState += HandleState;
        Client.OnReset += HandleReset;
        Client.OnDamage += HandleDamage;
        Client.OnColors += HandleColors;
        _subscribedClient = Client;
    }

    void UnbindClientEvents()
    {
        if (_subscribedClient == null) return;
        _subscribedClient.OnAssign -= HandleAssign;
        _subscribedClient.OnRoster -= HandleRoster;
        _subscribedClient.OnState -= HandleState;
        _subscribedClient.OnReset -= HandleReset;
        _subscribedClient.OnDamage -= HandleDamage;
        _subscribedClient.OnColors -= HandleColors;
        _subscribedClient = null;
    }

    void HandleAssign(int lineIndex, int lineCount)
    {
        EnsurePlatformCount(lineCount);
        ResizeLineBuffers(lineCount);
        _lastSyncedLineIndex = lineIndex;
        if (_localPaddle != null && lineIndex >= 0) {
            float angle = CircleArenaConfig.GetInitialAngleRad(lineIndex, lineCount);
            if (lineIndex < _targetAngles.Length) {
                _targetAngles[lineIndex] = angle;
                _displayAngles[lineIndex] = angle;
            }
            _localPaddle.SyncAngleFromServer(angle);
        }
        ApplyAllPlatformPoses();
        RefreshAllPlatformVisuals();
    }

    void HandleRoster(int lineCount)
    {
        EnsurePlatformCount(lineCount);
        ResizeLineBuffers(lineCount);
        SyncLocalPlatformAfterRoster(lineCount);
        ApplyAllPlatformPoses();
        RefreshAllPlatformVisuals();
    }

    void SyncFromClientSession()
    {
        if (Client == null) return;
        if (Client.LastRosterCount > 0) {
            HandleRoster(Client.LastRosterCount);
        }
        if (Client.LineIndex >= 0) {
            _lastSyncedLineIndex = Client.LineIndex;
            HandleAssign(Client.LineIndex, Client.LineCount);
        }
    }

    void SyncLocalPlatformAfterRoster(int lineCount)
    {
        if (Client == null || Client.LineIndex < 0 || _localPaddle == null) return;
        int idx = Client.LineIndex;
        float angle = CircleArenaConfig.GetInitialAngleRad(idx, lineCount);
        if (_targetAngles != null && idx < _targetAngles.Length) {
            angle = _targetAngles[idx];
        }
        _localPaddle.SyncAngleFromServer(angle);
    }

    void HandleReset()
    {
        ClearSnapshots();
        if (_lineHealth == null || CircleArena == null) return;

        for (int i = 0; i < _lineHealth.Length; i++) {
            _lineHealth[i] = CircleArenaConfig.HealthIntact;
            CircleArena.SetPlatformActive(i, true);
        }
        ApplyAllPlatformPoses();
        RefreshAllPlatformVisuals();
    }

    void HandleDamage(int lineIndex, int state)
    {
        if (CircleArena == null) return;
        if (lineIndex < 0) return;
        if (_lineHealth == null || lineIndex >= _lineHealth.Length) return;

        _lineHealth[lineIndex] = state;
        bool eliminated = state >= CircleArenaConfig.HealthEliminated;
        CircleArena.SetPlatformActive(lineIndex, !eliminated);
        if (!eliminated) ApplyLineVisual(lineIndex);
    }

    void HandleColors(IList<int> colorSlots)
    {
        if (colorSlots == null) return;
        if (_lineColorSlots == null || _lineColorSlots.Length != colorSlots.Count) {
            _lineColorSlots = new int[colorSlots.Count];
        }
        for (int i = 0; i < colorSlots.Count; i++) {
            _lineColorSlots[i] = colorSlots[i];
        }
        RefreshAllPlatformVisuals();
    }

    void HandleState(in PongStateSnapshot snap)
    {
        float[] angles = snap.Angles;
        if (angles != null) {
            if (CircleArena != null && CircleArena.Paddles.Length != angles.Length) {
                EnsurePlatformCount(angles.Length);
            }
            if (_targetAngles == null || _targetAngles.Length != angles.Length) {
                // Roster size changed: old snapshots have a different angle count, so drop them.
                ResizeLineBuffers(angles.Length);
            }
            int n = Mathf.Min(angles.Length, _targetAngles.Length);
            for (int i = 0; i < n; i++) _targetAngles[i] = angles[i];
        }

        EnqueueSnapshot(snap);
    }

    void EnqueueSnapshot(in PongStateSnapshot snap)
    {
        // The client already dropped stale/out-of-order datagrams, so snapshots arrive in
        // increasing server time — a plain append keeps the buffer ordered.
        _snaps.Add(new Snapshot {
            TimeMs = snap.ServerTimeMs,
            BallPos = snap.BallPos,
            BallVel = snap.BallVel,
            Angles = snap.Angles,
        });
        _hasBall = true;

        double cutoff = snap.ServerTimeMs - MaxBufferMs;
        int removeCount = 0;
        while (removeCount < _snaps.Count - 2 && _snaps[removeCount].TimeMs < cutoff) {
            removeCount++;
        }
        if (removeCount > 0) _snaps.RemoveRange(0, removeCount);
    }

    void EnsurePlatformCount(int count)
    {
        if (CircleArena == null) return;
        if (CircleArena.Paddles == null || CircleArena.Paddles.Length != count) {
            CircleArena.SetPlatformCount(count);
        }
    }

    void ResizeLineBuffers(int count)
    {
        var newTarget = new float[count];
        var newDisplay = new float[count];
        var newHealth = new int[count];

        int copy = _targetAngles != null ? Mathf.Min(_targetAngles.Length, count) : 0;
        for (int i = 0; i < copy; i++) {
            newTarget[i] = _targetAngles[i];
            newDisplay[i] = _displayAngles != null ? _displayAngles[i] : _targetAngles[i];
            newHealth[i] = _lineHealth != null ? _lineHealth[i] : CircleArenaConfig.HealthIntact;
        }

        for (int i = copy; i < count; i++) {
            float angle = CircleArenaConfig.GetInitialAngleRad(i, count);
            newTarget[i] = angle;
            newDisplay[i] = angle;
            newHealth[i] = CircleArenaConfig.HealthIntact;
        }

        _targetAngles = newTarget;
        _displayAngles = newDisplay;
        _lineHealth = newHealth;
        ClearSnapshots();
    }

    void ApplyAllPlatformPoses()
    {
        if (CircleArena == null || _displayAngles == null) return;
        var paddles = CircleArena.Paddles;
        int n = Mathf.Min(paddles.Length, _displayAngles.Length);
        for (int i = 0; i < n; i++) {
            CircleArena.UpdatePlatformAngle(i, _displayAngles[i]);
        }
    }

    void ApplyLineVisual(int lineIndex)
    {
        if (CircleArena == null) return;
        var line = CircleArena.GetPlatformLine(lineIndex);
        if (line == null || !line.gameObject.activeSelf) return;

        int colorSlot = _lineColorSlots != null && lineIndex >= 0 && lineIndex < _lineColorSlots.Length
            ? _lineColorSlots[lineIndex]
            : -1;

        // Fallback while the first COLORS message hasn't arrived yet — old local/remote
        // shading keeps the player's own paddle visually distinct until palette info lands.
        Color color;
        if (colorSlot >= 0) {
            color = CircleArenaConfig.GetPaletteColor(colorSlot);
        } else {
            int ownedLine = Client != null ? Client.LineIndex : -1;
            color = lineIndex == ownedLine
                ? CircleArenaConfig.LocalPlatformColor
                : CircleArenaConfig.RemotePlatformColor;
        }

        CircleArenaConfig.SetArcPlatformColor(line, color);
    }

    void Update()
    {
        BindClientEvents();

        if (Client != null && Client.LineIndex != _lastSyncedLineIndex) {
            if (Client.LineIndex >= 0) {
                HandleAssign(Client.LineIndex, Client.LineCount);
            } else {
                _lastSyncedLineIndex = -1;
            }
        }

        if (CircleArena == null) return;

        AdvanceRenderClock(Time.deltaTime);
        bool sampled = SampleWorld(out Vector2 ballPos);

        if (Ball != null && _hasBall && sampled) {
            Ball.position = new Vector3(ballPos.x, ballPos.y, Ball.position.z);
        }

        if (_targetAngles == null || _displayAngles == null) return;
        var paddles = CircleArena.Paddles;
        if (paddles.Length == 0) return;
        if (_targetAngles.Length != paddles.Length) return;

        int ownedLine = Client != null ? Client.LineIndex : -1;
        for (int i = 0; i < paddles.Length; i++) {
            if (paddles[i] == null || !paddles[i].gameObject.activeSelf) continue;

            if (i == ownedLine) {
                var ownedLineRenderer = CircleArena.GetPlatformLine(i);
                if (ownedLineRenderer != null && _localPaddle != null) {
                    CircleArenaConfig.UpdateArcPlatform(ownedLineRenderer, GetLocalDisplayAngle(i), paddles.Length);
                }
                ApplyLineVisual(i);
                continue;
            }

            // Remote paddle: position comes straight from the interpolated snapshot sample.
            if (_sampleAngles != null && i < _sampleAngles.Length) {
                _displayAngles[i] = _sampleAngles[i];
            }
            CircleArena.UpdatePlatformAngle(i, _displayAngles[i]);
            ApplyLineVisual(i);
        }
    }

    void AdvanceRenderClock(float dt)
    {
        if (_snaps.Count == 0) return;
        double latest = _snaps[_snaps.Count - 1].TimeMs;

        if (!_renderInit) {
            _renderTimeMs = latest - InterpolationDelayMs;
            _renderInit = true;
            return;
        }

        _renderTimeMs += dt * 1000.0;

        // If we fell well behind (editor hitch, GC, etc.), jump back to the target lag so latency
        // doesn't accumulate over time.
        double target = latest - InterpolationDelayMs;
        if (_renderTimeMs < target - InterpolationDelayMs) {
            _renderTimeMs = target;
        }

        // During a packet gap the clock would run away; cap it so the ball only extrapolates a
        // bounded amount past the last snapshot.
        double maxAhead = latest + MaxExtrapolationMs;
        if (_renderTimeMs > maxAhead) _renderTimeMs = maxAhead;
    }

    /// <summary>
    /// Sample ball position and remote paddle angles (into <see cref="_sampleAngles"/>) at the
    /// current render time: linear interpolation between the two bracketing snapshots, or bounded
    /// ball extrapolation when the render time is past the most recent snapshot.
    /// </summary>
    bool SampleWorld(out Vector2 ballPos)
    {
        ballPos = default;
        int count = _snaps.Count;
        if (count == 0) return false;

        double rt = _renderTimeMs;
        var first = _snaps[0];
        var last = _snaps[count - 1];

        if (rt <= first.TimeMs) {
            ballPos = first.BallPos;
            CopySampleAngles(first.Angles);
            return true;
        }

        if (rt >= last.TimeMs) {
            ballPos = ExtrapolateBall(last, rt);
            CopySampleAngles(last.Angles);
            return true;
        }

        for (int i = count - 1; i >= 1; i--) {
            var a = _snaps[i - 1];
            var b = _snaps[i];
            if (rt >= a.TimeMs && rt <= b.TimeMs) {
                double span = b.TimeMs - a.TimeMs;
                float t = span > 1e-3 ? (float)((rt - a.TimeMs) / span) : 0f;
                ballPos = Vector2.Lerp(a.BallPos, b.BallPos, t);
                LerpSampleAngles(a.Angles, b.Angles, t);
                return true;
            }
        }

        ballPos = last.BallPos;
        CopySampleAngles(last.Angles);
        return true;
    }

    Vector2 ExtrapolateBall(in Snapshot s, double renderMs)
    {
        double dt = (renderMs - s.TimeMs) / 1000.0;
        if (dt < 0.0) dt = 0.0;
        double cap = MaxExtrapolationMs / 1000.0;
        if (dt > cap) dt = cap;
        Vector2 predicted = s.BallPos + s.BallVel * (float)dt;

        // During a packet gap we extrapolate in a straight line, which would otherwise push the
        // ball visibly outside the ring. Clamp the predicted position to the arena radius so the
        // client never shows an escaped ball (the authoritative server position will reconcile it).
        float maxR = CircleArenaConfig.Radius;
        if (predicted.magnitude > maxR) predicted = predicted.normalized * maxR;
        return predicted;
    }

    void EnsureSampleAngles(int len)
    {
        if (_sampleAngles == null || _sampleAngles.Length != len) _sampleAngles = new float[len];
    }

    void CopySampleAngles(float[] src)
    {
        if (src == null) { _sampleAngles = null; return; }
        EnsureSampleAngles(src.Length);
        System.Array.Copy(src, _sampleAngles, src.Length);
    }

    void LerpSampleAngles(float[] a, float[] b, float t)
    {
        if (a == null || b == null) { CopySampleAngles(a ?? b); return; }
        int len = Mathf.Min(a.Length, b.Length);
        EnsureSampleAngles(len);
        for (int i = 0; i < len; i++) {
            _sampleAngles[i] = Mathf.LerpAngle(
                a[i] * Mathf.Rad2Deg,
                b[i] * Mathf.Rad2Deg,
                t) * Mathf.Deg2Rad;
        }
    }

    public void RefreshLocalPlatformVisual()
    {
        if (Client == null) return;
        ApplyLineVisual(Client.LineIndex);
    }

    public void RefreshAllPlatformVisuals()
    {
        if (CircleArena == null) return;
        for (int i = 0; i < CircleArena.Paddles.Length; i++) {
            ApplyLineVisual(i);
        }
    }

    public Transform GetLocalPaddleTransform()
    {
        if (Client == null || CircleArena == null) return null;
        int idx = Client.LineIndex;
        if (idx < 0 || idx >= CircleArena.Paddles.Length) return null;
        return CircleArena.Paddles[idx];
    }

    public void SetLocalDisplayAngle(int lineIndex, float angleRad)
    {
        if (_displayAngles == null || lineIndex < 0 || lineIndex >= _displayAngles.Length) return;
        _displayAngles[lineIndex] = angleRad;
        if (_targetAngles != null && lineIndex < _targetAngles.Length) {
            _targetAngles[lineIndex] = angleRad;
        }
    }

    public float ClampLocalAngleAgainstPlayers(int lineIndex, float currentAngleRad, float desiredAngleRad)
    {
        desiredAngleRad = CircleArenaConfig.NormalizeAngleRad(desiredAngleRad);
        if (_displayAngles == null || lineIndex < 0 || lineIndex >= _displayAngles.Length) return desiredAngleRad;

        float moveDelta = CircleArenaConfig.SignedAngleDeltaRad(currentAngleRad, desiredAngleRad);
        float fallbackSign = moveDelta >= 0f ? 1f : -1f;
        int totalPlayers = _displayAngles.Length;

        for (int pass = 0; pass < 2; pass++) {
            for (int i = 0; i < _displayAngles.Length; i++) {
                if (i == lineIndex) continue;
                if (IsLineEliminated(i)) continue;

                desiredAngleRad = CircleArenaConfig.ClampOutsidePlatform(
                    desiredAngleRad,
                    _displayAngles[i],
                    totalPlayers,
                    fallbackSign);
            }
        }

        return desiredAngleRad;
    }

    float GetLocalDisplayAngle(int lineIndex)
    {
        if (_localPaddle != null && Client != null && lineIndex == Client.LineIndex) {
            return _localPaddle.CurrentAngleRad;
        }
        if (_displayAngles != null && lineIndex >= 0 && lineIndex < _displayAngles.Length) {
            return _displayAngles[lineIndex];
        }
        return 0f;
    }
}
