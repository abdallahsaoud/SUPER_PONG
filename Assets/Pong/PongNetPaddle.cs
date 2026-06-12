using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Locally-controlled paddle (client-with-local-control paradigm).
///
/// The player's own paddle moves instantly from keyboard input (zero input lag).
/// Position is streamed to the server via UDP PADDLE messages (~30/s through
/// PongClient.SendPaddle). Remote paddles are rendered by PongNetView from
/// interpolated STATE snapshots instead.
/// </summary>
public class PongNetPaddle : MonoBehaviour
{
    public float Speed = 2.8f;
    public float SendRate = 30f;

    public PongClient Client;
    public PongNetView View;

    float _ringAngleRad;
    float _sendAccumulator;
    float _lastSentAngle = float.NaN;

    public float CurrentAngleRad => _ringAngleRad;

    void Update()
    {
        if (Client == null || View == null) return;
        if (!Client.IsConnected || !Client.ParticipatesInGame || Client.LineIndex < 0) return;

        int idx = Client.LineIndex;
        if (View.CircleArena == null) return;
        var line = View.CircleArena.GetPlatformLine(idx);
        if (line == null || !line.gameObject.activeSelf) return;

        float direction = ReadMoveDirection();
        float desiredAngle = CircleArenaConfig.NormalizeAngleRad(_ringAngleRad + direction * Speed * Time.deltaTime);
        _ringAngleRad = View.ClampLocalAngleAgainstPlayers(idx, _ringAngleRad, desiredAngle);

        int playerCount = View.CircleArena.Paddles.Length;
        CircleArenaConfig.UpdateArcPlatform(line, _ringAngleRad, playerCount);
        View.SetLocalDisplayAngle(idx, _ringAngleRad);
        View.RefreshLocalPlatformVisual();

        _sendAccumulator += Time.deltaTime;
        float interval = SendRate > 0f ? 1f / SendRate : 0.033f;
        if (_sendAccumulator >= interval) {
            _sendAccumulator = 0f;
            if (!Mathf.Approximately(_ringAngleRad, _lastSentAngle)) {
                // Real-time input path: UDP PADDLE (not TCP) — see PongClient.SendPaddle.
                Client.SendPaddle(_ringAngleRad);
                _lastSentAngle = _ringAngleRad;
            }
        }
    }

    static float ReadMoveDirection()
    {
        var kb = Keyboard.current;
        if (kb == null) return 0f;
        float dir = 0f;
        if (kb.leftArrowKey.isPressed) dir -= 1f;
        if (kb.rightArrowKey.isPressed) dir += 1f;
        return dir;
    }

    public void SyncAngleFromServer(float angleRad)
    {
        _ringAngleRad = CircleArenaConfig.NormalizeAngleRad(angleRad);
        _lastSentAngle = _ringAngleRad;

        if (View != null && View.CircleArena != null && Client != null && Client.LineIndex >= 0) {
            var line = View.CircleArena.GetPlatformLine(Client.LineIndex);
            if (line != null) {
                int playerCount = View.CircleArena.Paddles.Length;
                CircleArenaConfig.UpdateArcPlatform(line, _ringAngleRad, playerCount);
            }
        }
    }
}
