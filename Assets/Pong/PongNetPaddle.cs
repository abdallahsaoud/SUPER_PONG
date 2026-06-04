using UnityEngine;
using UnityEngine.InputSystem;

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
        _ringAngleRad = CircleArenaConfig.NormalizeAngleRad(_ringAngleRad + direction * Speed * Time.deltaTime);

        CircleArenaConfig.UpdateArcPlatform(line, _ringAngleRad);
        View.SetLocalDisplayAngle(idx, _ringAngleRad);
        View.RefreshLocalPlatformVisual();

        _sendAccumulator += Time.deltaTime;
        float interval = SendRate > 0f ? 1f / SendRate : 0.033f;
        if (_sendAccumulator >= interval) {
            _sendAccumulator = 0f;
            if (!Mathf.Approximately(_ringAngleRad, _lastSentAngle)) {
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
                CircleArenaConfig.UpdateArcPlatform(line, _ringAngleRad);
            }
        }
    }
}
