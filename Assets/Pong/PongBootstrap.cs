using UnityEngine;

/// <summary>
/// Procedural scene setup for Multiplayer Pong. Lets us ship a working
/// server/client without hand-authoring detailed .unity assets - the scene
/// only needs one GameObject with this component on it.
///
/// The bootstrap creates the arena (camera + ball + paddles), wires the
/// PongServer + PongServerGame (server role) or PongClient + PongNetView +
/// PongNetPaddle (client role), and exposes the resulting transforms so
/// connection UIs (if present) can hook into them.
///
/// Headless server builds work fine: the script still creates the components,
/// just without a camera/renderer. The paddle/ball visuals are pure cosmetic
/// primitives - the authoritative simulation lives entirely in PongServerGame.
/// </summary>
public class PongBootstrap : MonoBehaviour
{
    public enum Role { Server, Client }

    [Header("Role")]
    public Role Mode = Role.Client;

    [Header("Arena geometry (must match server)")]
    public float ArenaHalfWidth = 6f;
    public float ArenaHalfHeight = 5f;
    public float PaddleLineX = 5f;          // X distance of each side line from arena centre
    public Vector2 PaddleSize = new Vector2(0.3f, 2f);
    public float BallSize = 0.5f;

    [Header("Line count (scale hook)")]
    [Tooltip("Number of lines/players. 2 = classic Pong (left/right). >2 places additional lines as vertical lines spaced across the arena - replace this with N-sided polygon geometry for the 'massive' version.")]
    [Min(2)] public int LineCount = 2;

    [Header("Networking defaults")]
    public string DefaultServerIP = "127.0.0.1";
    public int DefaultPort = 25000;
    public bool AutoStart = true;

    [Header("References populated at runtime")]
    public Transform Ball;
    public Transform[] Paddles;
    public PongServer Server;
    public PongServerGame ServerGame;
    public PongClient Client;
    public PongNetView View;
    public PongNetPaddle LocalPaddle;

    void Awake()
    {
        EnsureCamera();
        BuildArenaVisuals();

        switch (Mode) {
            case Role.Server: BuildServer(); break;
            case Role.Client: BuildClient(); break;
        }
    }

    void Start()
    {
        if (!AutoStart) return;
        if (Mode == Role.Server && Server != null) Server.Listen();
        if (Mode == Role.Client && Client != null) Client.Connect();
    }

    void EnsureCamera()
    {
        if (Camera.main != null) return;
        var go = new GameObject("Main Camera");
        var cam = go.AddComponent<Camera>();
        go.tag = "MainCamera";
        cam.orthographic = true;
        cam.orthographicSize = ArenaHalfHeight + 1f;
        cam.backgroundColor = new Color(0.05f, 0.05f, 0.08f);
        cam.clearFlags = CameraClearFlags.SolidColor;
        go.transform.position = new Vector3(0f, 0f, -10f);
    }

    void BuildArenaVisuals()
    {
        // Ball visual
        var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ball.name = "PongBall";
        ball.transform.position = Vector3.zero;
        ball.transform.localScale = Vector3.one * BallSize;
        DestroyCollider(ball);
        Ball = ball.transform;

        // Build LineCount paddle Transforms. The X positions match what BuildServer
        // / BuildClient will tell the server about so STATE[i] -> Paddles[i].
        int n = Mathf.Max(2, LineCount);
        Paddles = new Transform[n];
        var xs = ComputeLineXs(n);
        for (int i = 0; i < n; i++) {
            Paddles[i] = MakePaddle("Paddle" + i, new Vector3(xs[i], 0f, 0f));
        }

        // Decorative walls (top/bottom)
        MakeWall("WallTop",    new Vector3(0f,  ArenaHalfHeight + 0.1f, 0f), new Vector3(ArenaHalfWidth * 2f, 0.2f, 1f));
        MakeWall("WallBottom", new Vector3(0f, -ArenaHalfHeight - 0.1f, 0f), new Vector3(ArenaHalfWidth * 2f, 0.2f, 1f));
    }

    /// <summary>
    /// Compute X coordinates for N lines. For N=2 this is the classic [-PaddleLineX, +PaddleLineX].
    /// For N>2 this evenly distributes lines across the arena width as a placeholder for a future
    /// N-sided polygon arena. The protocol/code already supports arbitrary geometry; this is just
    /// the default visual placement.
    /// </summary>
    public float[] ComputeLineXs(int n)
    {
        var xs = new float[n];
        if (n == 2) {
            xs[0] = -PaddleLineX;
            xs[1] = +PaddleLineX;
            return xs;
        }
        for (int i = 0; i < n; i++) {
            float t = (n == 1) ? 0.5f : (float)i / (n - 1);
            xs[i] = Mathf.Lerp(-PaddleLineX, +PaddleLineX, t);
        }
        return xs;
    }

    Transform MakePaddle(string name, Vector3 position)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.position = position;
        go.transform.localScale = new Vector3(PaddleSize.x, PaddleSize.y, 1f);
        DestroyCollider(go);
        return go.transform;
    }

    void MakeWall(string name, Vector3 position, Vector3 scale)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.position = position;
        go.transform.localScale = scale;
        DestroyCollider(go);
    }

    void DestroyCollider(GameObject go)
    {
        var c = go.GetComponent<Collider>();
        if (c != null) Destroy(c);
    }

    void BuildServer()
    {
        Server = gameObject.AddComponent<PongServer>();
        Server.ListenPort = DefaultPort;

        ServerGame = gameObject.AddComponent<PongServerGame>();
        ServerGame.ArenaHalfWidth = ArenaHalfWidth;
        ServerGame.ArenaHalfHeight = ArenaHalfHeight;
        ServerGame.Lines.Clear();

        int n = Mathf.Max(2, LineCount);
        var xs = ComputeLineXs(n);
        for (int i = 0; i < n; i++) {
            ServerGame.Lines.Add(new PongServerGame.LineConfig {
                X = xs[i],
                FacingSign = xs[i] >= 0f ? +1 : -1,
            });
        }
        ServerGame.RebuildRuntime();
    }

    void BuildClient()
    {
        Client = gameObject.AddComponent<PongClient>();
        Client.DestinationIP = DefaultServerIP;
        Client.DestinationPort = DefaultPort;

        View = gameObject.AddComponent<PongNetView>();
        View.Client = Client;
        View.Ball = Ball;
        View.Paddles = Paddles;

        LocalPaddle = gameObject.AddComponent<PongNetPaddle>();
        LocalPaddle.Client = Client;
        LocalPaddle.View = View;
    }
}
