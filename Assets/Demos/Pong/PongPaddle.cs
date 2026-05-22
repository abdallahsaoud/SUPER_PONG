using UnityEngine;
using UnityEngine.InputSystem;

public enum PongPlayer {
  PlayerLeft = 1,
  PlayerRight = 2
}

public class PongPaddle : MonoBehaviour
{ 
    public PongPlayer Player = PongPlayer.PlayerLeft;
    public float Speed = 1;
    public float MinY = -4;
    public float MaxY = 4;

    PongInput inputActions;
    InputAction PlayerAction;


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        inputActions = new PongInput();
        switch (Player) {
          case PongPlayer.PlayerLeft:
            PlayerAction = inputActions.Pong.Player1;
            break;
          case PongPlayer.PlayerRight:
            PlayerAction = inputActions.Pong.Player2;
            break;
        }

        PlayerAction.Enable();
    }

    // Update is called once per frame
    void Update()
    {
      float direction = PlayerAction.ReadValue<float>();

      Vector3 newPos = transform.position + (Vector3.up * Speed * direction * Time.deltaTime);
      newPos.y = Mathf.Clamp(newPos.y, MinY, MaxY);

      transform.position = newPos;
    }

    void OnDisable() {
      PlayerAction.Disable();
    }
}
