using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Client-side controller for the locally-owned line (paddle).
///
/// Implements the course's "client with local control" paradigm: this paddle
/// moves immediately from local input (no input lag) and streams its Y to the
/// server. The server clamps and rebroadcasts; PongNetView ignores the server
/// value for the locally-owned line so local input stays authoritative on screen.
///
/// Adapted from Assets/Demos/Pong/PongPaddle.cs.
/// </summary>
public class PongNetPaddle : MonoBehaviour
{
    [Tooltip("Movement speed in Unity units / second.")]
    public float Speed = 6f;
    public float MinY = -4f;
    public float MaxY = 4f;

    [Tooltip("How many PADDLE updates per second to send to the server.")]
    public float SendRate = 30f;

    [Tooltip("The client component to send PADDLE messages through. Required.")]
    public PongClient Client;

    [Tooltip("Reference to the scene view so we know which paddle Transform we own. Required.")]
    public PongNetView View;

    PongInput _inputActions;
    InputAction _moveAction;
    Transform _paddle;
    float _sendAccumulator;
    float _lastSentY = float.NaN;

    void OnEnable()
    {
        _inputActions = new PongInput();
        // Use Player1 mapping for the local player (W/S). Player2 (arrows) stays
        // available if someone wants to play two clients on the same machine.
        _moveAction = _inputActions.Pong.Player1;
        _moveAction.Enable();
    }

    void OnDisable()
    {
        if (_moveAction != null) _moveAction.Disable();
        _inputActions?.Dispose();
        _inputActions = null;
        _moveAction = null;
    }

    void Update()
    {
        if (Client == null || View == null) return;
        if (!Client.IsConnected || Client.LineIndex < 0) return;

        _paddle = View.GetLocalPaddleTransform();
        if (_paddle == null) return;

        float direction = _moveAction.ReadValue<float>();
        Vector3 newPos = _paddle.position + Vector3.up * (Speed * direction * Time.deltaTime);
        newPos.y = Mathf.Clamp(newPos.y, MinY, MaxY);
        _paddle.position = newPos;

        _sendAccumulator += Time.deltaTime;
        float interval = SendRate > 0f ? 1f / SendRate : 0.033f;
        if (_sendAccumulator >= interval) {
            _sendAccumulator = 0f;
            if (!Mathf.Approximately(newPos.y, _lastSentY)) {
                Client.SendPaddle(newPos.y);
                _lastSentY = newPos.y;
            }
        }
    }
}
