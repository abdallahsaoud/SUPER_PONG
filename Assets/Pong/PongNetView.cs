using UnityEngine;
using System.Collections.Generic;

public class PongNetView : MonoBehaviour
{
    const int HealthEliminated = 2;

    public PongClient Client;
    public Transform Ball;
    public PongCircleArena CircleArena;

    public float InterpolationRate = 24f;
    public float BallPredictionSeconds = 0.12f;

    public bool IsLineEliminated(int lineIndex)
    {
        return _lineHealth != null
            && lineIndex >= 0
            && lineIndex < _lineHealth.Length
            && _lineHealth[lineIndex] >= HealthEliminated;
    }

    public bool IsLocalPlayerEliminated()
    {
        return Client != null && Client.LineIndex >= 0 && IsLineEliminated(Client.LineIndex);
    }

    public Color ScatteredColor = new Color(1f, 0.55f, 0.1f, 0.85f);

    Vector3? _targetBall;
    Vector3 _targetBallVelocity;
    float _timeSinceBallState;
    float[] _targetAngles;
    float[] _displayAngles;
    int[] _lineHealth;
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
        _targetBall = null;
        _targetBallVelocity = Vector3.zero;
        _timeSinceBallState = 0f;
        if (_lineHealth == null || CircleArena == null) return;

        for (int i = 0; i < _lineHealth.Length; i++) {
            _lineHealth[i] = 0;
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
        bool eliminated = state >= 2;
        CircleArena.SetPlatformActive(lineIndex, !eliminated);
        if (!eliminated) ApplyLineVisual(lineIndex);
    }

    void HandleState(Vector2 ballPos, Vector2 ballVelocity, IList<float> paddleAngles)
    {
        _targetBall = new Vector3(ballPos.x, ballPos.y, Ball != null ? Ball.position.z : 0f);
        _targetBallVelocity = new Vector3(ballVelocity.x, ballVelocity.y, 0f);
        _timeSinceBallState = 0f;

        if (paddleAngles == null) return;

        if (CircleArena != null && CircleArena.Paddles.Length != paddleAngles.Count) {
            EnsurePlatformCount(paddleAngles.Count);
        }

        if (_targetAngles == null || _targetAngles.Length != paddleAngles.Count) {
            ResizeLineBuffers(paddleAngles.Count);
        }

        int n = Mathf.Min(paddleAngles.Count, _targetAngles.Length);
        for (int i = 0; i < n; i++) _targetAngles[i] = paddleAngles[i];
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
            newHealth[i] = _lineHealth != null ? _lineHealth[i] : 0;
        }

        for (int i = copy; i < count; i++) {
            float angle = CircleArenaConfig.GetInitialAngleRad(i, count);
            newTarget[i] = angle;
            newDisplay[i] = angle;
            newHealth[i] = 0;
        }

        _targetAngles = newTarget;
        _displayAngles = newDisplay;
        _lineHealth = newHealth;
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

        int ownedLine = Client != null ? Client.LineIndex : -1;
        bool isLocal = lineIndex == ownedLine;
        Color color = isLocal
            ? CircleArenaConfig.LocalPlatformColor
            : CircleArenaConfig.RemotePlatformColor;

        if (_lineHealth != null && lineIndex < _lineHealth.Length && _lineHealth[lineIndex] == 1) {
            color = ScatteredColor;
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

        float t = 1f - Mathf.Exp(-InterpolationRate * Time.deltaTime);

        if (Ball != null && _targetBall.HasValue) {
            _timeSinceBallState += Time.deltaTime;
            float predictionTime = Mathf.Min(_timeSinceBallState, BallPredictionSeconds);
            Vector3 predictedBall = _targetBall.Value + _targetBallVelocity * predictionTime;
            Ball.position = Vector3.Lerp(Ball.position, predictedBall, t);
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

            _displayAngles[i] = Mathf.LerpAngle(
                _displayAngles[i] * Mathf.Rad2Deg,
                _targetAngles[i] * Mathf.Rad2Deg,
                t) * Mathf.Deg2Rad;

            CircleArena.UpdatePlatformAngle(i, _displayAngles[i]);
            ApplyLineVisual(i);
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
