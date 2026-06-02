using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Client-side view: positions the ball and all paddles from server STATE
/// messages, with simple linear interpolation to smooth network jitter.
///
/// The locally-owned paddle is NOT smoothed against the server value - it is
/// authoritative on the client side for zero input lag (the server still has
/// the final say if it clamps differently, but it usually agrees). The owner's
/// PongNetPaddle moves the transform directly each frame.
///
/// Arena geometry is decided on the server. This script uses an inspector list
/// of paddle Transforms in the same order as the server's Lines list so that
/// STATE[i] -> Paddles[i].
/// </summary>
public class PongNetView : MonoBehaviour
{
    [Tooltip("Client component receiving messages from the server.")]
    public PongClient Client;

    [Tooltip("Ball visual to position from STATE.")]
    public Transform Ball;

    [Tooltip("Paddle Transforms in the same order as the server's Lines list (index 0 = line 0, etc.).")]
    public Transform[] Paddles;

    [Tooltip("Interpolation factor per second (higher = snappier, lower = smoother).")]
    public float InterpolationRate = 18f;
    [Tooltip("Enable throttled client view logs for remote paddle application.")]
    public bool DebugViewLogs = false;
    [Tooltip("Max debug log frequency in logs/second.")]
    public float DebugLogRate = 1f;

    [Header("Damage visuals (Milestone 2)")]
    public Color IntactColor    = Color.white;
    public Color ScatteredColor = new Color(1f, 0.55f, 0.1f);
    public Color BrokenColor    = new Color(0.3f, 0.3f, 0.3f, 0.25f);

    Vector3? _targetBall;
    float[] _targetPaddleY;
    int[]   _lineHealth; // 0=Intact, 1=Scattered, 2=Broken
    PongClient _subscribedClient;
    float _nextDebugViewLogTime;

    void OnEnable()
    {
        BindClientEvents();
        int n = Paddles != null ? Paddles.Length : 0;
        _targetPaddleY = new float[n];
        _lineHealth    = new int[n];
        for (int i = 0; i < n; i++) {
            _targetPaddleY[i] = Paddles[i] != null ? Paddles[i].position.y : 0f;
            ApplyLineVisual(i, 0);
        }
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
        Client.OnState += HandleState;
        Client.OnReset += HandleReset;
        Client.OnDamage += HandleDamage;
        Client.OnGeometry += HandleGeometry;
        _subscribedClient = Client;
    }

    void UnbindClientEvents()
    {
        if (_subscribedClient == null) return;

        _subscribedClient.OnAssign -= HandleAssign;
        _subscribedClient.OnState -= HandleState;
        _subscribedClient.OnReset -= HandleReset;
        _subscribedClient.OnDamage -= HandleDamage;
        _subscribedClient.OnGeometry -= HandleGeometry;
        _subscribedClient = null;
    }

    void HandleGeometry(System.Collections.Generic.IList<float> lineXs)
    {
        if (Paddles == null) return;
        int n = Mathf.Min(lineXs.Count, Paddles.Length);
        for (int i = 0; i < n; i++) {
            if (Paddles[i] == null) continue;
            var p = Paddles[i].position;
            p.x = lineXs[i]; // snap X (server-authoritative); Y stays smoothed from STATE
            Paddles[i].position = p;
        }
    }

    void HandleAssign(int lineIndex, int lineCount) { /* nothing per-assign for now */ }

    void HandleReset()
    {
        // Ball will jump to new serve via next STATE; nothing else to do.
        _targetBall = null;
    }

    void HandleDamage(int lineIndex, int state)
    {
        if (Paddles == null) return;
        if (lineIndex < 0 || lineIndex >= Paddles.Length) return;
        if (_lineHealth == null || _lineHealth.Length != Paddles.Length) _lineHealth = new int[Paddles.Length];
        _lineHealth[lineIndex] = state;
        ApplyLineVisual(lineIndex, state);
    }

    void ApplyLineVisual(int lineIndex, int state)
    {
        if (Paddles == null || lineIndex < 0 || lineIndex >= Paddles.Length) return;
        var t = Paddles[lineIndex];
        if (t == null) return;
        var renderer = t.GetComponent<Renderer>();
        if (renderer == null) return;

        switch (state) {
            case 0: SetColor(renderer, IntactColor);    SetActiveSafe(t, true); break;
            case 1: SetColor(renderer, ScatteredColor); SetActiveSafe(t, true); break;
            default:
                // Broken: keep the transform alive (so STATE indexing stays stable)
                // but hide the visual via transparent color or deactivation.
                SetColor(renderer, BrokenColor);
                break;
        }
    }

    static void SetColor(Renderer r, Color c)
    {
        // Use a per-instance MaterialPropertyBlock when possible to avoid leaking material instances.
        var mat = r.material;
        if (mat != null) mat.color = c;
    }

    static void SetActiveSafe(Transform t, bool active)
    {
        if (t.gameObject.activeSelf != active) t.gameObject.SetActive(active);
    }

    void HandleState(Vector2 ballPos, IList<float> paddleYs)
    {
        _targetBall = new Vector3(ballPos.x, ballPos.y, Ball != null ? Ball.position.z : 0f);

        if (Paddles == null || Paddles.Length == 0) return;
        if (_targetPaddleY == null || _targetPaddleY.Length != Paddles.Length) {
            _targetPaddleY = new float[Paddles.Length];
        }

        int n = Mathf.Min(paddleYs.Count, Paddles.Length);
        for (int i = 0; i < n; i++) _targetPaddleY[i] = paddleYs[i];
    }

    void Update()
    {
        // PongBootstrap assigns Client after AddComponent, so rebind lazily.
        BindClientEvents();

        float t = 1f - Mathf.Exp(-InterpolationRate * Time.deltaTime); // frame-rate independent lerp

        if (Ball != null && _targetBall.HasValue) {
            Ball.position = Vector3.Lerp(Ball.position, _targetBall.Value, t);
        }

        if (Paddles == null || Paddles.Length == 0) return;

        // Resize target buffers lazily. OnEnable runs at AddComponent time, before
        // PongBootstrap has assigned Paddles, so the initial size can be 0.
        if (_targetPaddleY == null || _targetPaddleY.Length != Paddles.Length) {
            var resizedY = new float[Paddles.Length];
            int copyY = _targetPaddleY != null ? Mathf.Min(_targetPaddleY.Length, resizedY.Length) : 0;
            for (int i = 0; i < copyY; i++) resizedY[i] = _targetPaddleY[i];
            // Initialize new slots from the current paddle Y so we don't snap to 0.
            for (int i = copyY; i < resizedY.Length; i++) {
                resizedY[i] = Paddles[i] != null ? Paddles[i].position.y : 0f;
            }
            _targetPaddleY = resizedY;
        }
        if (_lineHealth == null || _lineHealth.Length != Paddles.Length) {
            var resizedH = new int[Paddles.Length];
            int copyH = _lineHealth != null ? Mathf.Min(_lineHealth.Length, resizedH.Length) : 0;
            for (int i = 0; i < copyH; i++) resizedH[i] = _lineHealth[i];
            _lineHealth = resizedH;
        }

        int ownedLine = Client != null ? Client.LineIndex : -1;
        for (int i = 0; i < Paddles.Length; i++) {
            if (Paddles[i] == null) continue;
            if (i == ownedLine) continue; // local-control: don't fight the owner's input
            Vector3 p = Paddles[i].position;
            p.y = Mathf.Lerp(p.y, _targetPaddleY[i], t);
            Paddles[i].position = p;
        }

        if (DebugViewLogs && Time.time >= _nextDebugViewLogTime) {
            _nextDebugViewLogTime = Time.time + GetDebugInterval();
            string remoteInfo = string.Empty;
            for (int i = 0; i < Paddles.Length; i++) {
                if (i == ownedLine || Paddles[i] == null) continue;
                if (remoteInfo.Length > 0) remoteInfo += " | ";
                remoteInfo += "line " + i + " y=" + Paddles[i].position.y.ToString("0.##")
                    + " target=" + _targetPaddleY[i].ToString("0.##");
            }
            if (string.IsNullOrEmpty(remoteInfo)) remoteInfo = "no remote line visible yet";
            Debug.Log("PongNetView DBG ownedLine=" + ownedLine + " :: " + remoteInfo);
        }
    }

    /// <summary>Used by PongNetPaddle to find which Transform to move locally.</summary>
    public Transform GetLocalPaddleTransform()
    {
        if (Client == null) return null;
        int idx = Client.LineIndex;
        if (Paddles == null || idx < 0 || idx >= Paddles.Length) return null;
        return Paddles[idx];
    }

    float GetDebugInterval()
    {
        return DebugLogRate > 0f ? 1f / DebugLogRate : 1f;
    }
}
