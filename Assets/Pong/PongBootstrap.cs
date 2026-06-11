using UnityEngine;

public class PongBootstrap : MonoBehaviour
{
    public enum Role { Server, Client }

    [Header("Role")]
    public Role Mode = Role.Client;

    [Header("Arena")]
    public float BallSize = 0.5f;
    public string BackgroundResourcePath = "PongBackground";

    [Header("Networking defaults")]
    [Tooltip("Leave empty for LAN builds — the connect overlay will ask for the host IP.")]
    public string DefaultServerIP = "";
    public int DefaultPort = PongNetworkUtil.DefaultPort;

    [Tooltip("If true, tries to connect on start when DefaultServerIP is set (or -serverIP CLI arg).")]
    public bool AutoStart = false;

    [Header("References populated at runtime")]
    public Transform Ball;
    public Transform Background;
    public Transform[] Paddles;
    public PongCircleArena CircleArena;
    public PongServer Server;
    public PongServerGame ServerGame;
    public PongClient Client;
    public PongNetView View;
    public PongNetPaddle LocalPaddle;
    public PongClientConnectOverlay ConnectOverlay;

    void Awake()
    {
        Application.runInBackground = true;
        ApplyCommandLineOverrides();
        EnsureCamera();
        BuildBackground();
        BuildArenaVisuals();

        switch (Mode) {
            case Role.Server: BuildServer(); break;
            case Role.Client: BuildClient(); break;
        }
    }

    void Start()
    {
        if (Mode == Role.Server) {
            if (AutoStart && Server != null) {
                Server.Listen();
                string hint = PongNetworkUtil.FormatHostHint(DefaultPort);
                Debug.Log("PongServer: listening. " + hint);
            }
            return;
        }

        if (!AutoStart || !PongNetworkUtil.IsUsableClientTarget(DefaultServerIP)) return;
        StartCoroutine(ConnectClientNextFrame());
    }

    void ApplyCommandLineOverrides()
    {
        string[] args = System.Environment.GetCommandLineArgs();
        if (!PongNetworkUtil.TryApplyConnectArgs(args, out string ip, out int port)) return;

        if (!string.IsNullOrEmpty(ip)) DefaultServerIP = ip;
        if (port > 0) DefaultPort = port;
        if (Mode == Role.Client && PongNetworkUtil.IsUsableClientTarget(DefaultServerIP)) {
            AutoStart = true;
        }
    }

    System.Collections.IEnumerator ConnectClientNextFrame()
    {
        yield return null;
        if (View != null) View.ForceBindAndSync();
        if (Client != null && Client.Connect()) {
            Client.SendName("Player");
            Client.SendReady();
        }
        yield return null;
        if (View != null) View.ForceBindAndSync();
    }

    void EnsureCamera()
    {
        if (Camera.main != null) {
            Camera.main.orthographic = true;
            Camera.main.orthographicSize = CircleArenaConfig.GetCameraOrthographicSize();
            Camera.main.backgroundColor = new Color(0.04f, 0.05f, 0.09f);
            Camera.main.clearFlags = CameraClearFlags.SolidColor;
            return;
        }

        var go = new GameObject("Main Camera");
        var cam = go.AddComponent<Camera>();
        go.tag = "MainCamera";
        cam.orthographic = true;
        cam.orthographicSize = CircleArenaConfig.GetCameraOrthographicSize();
        cam.backgroundColor = new Color(0.04f, 0.05f, 0.09f);
        cam.clearFlags = CameraClearFlags.SolidColor;
        go.transform.position = new Vector3(0f, 0f, -10f);
    }

    void BuildBackground()
    {
        if (string.IsNullOrWhiteSpace(BackgroundResourcePath)) return;

        var texture = Resources.Load<Texture2D>(BackgroundResourcePath);
        if (texture == null) {
            Debug.LogWarning("PongBootstrap: background resource not found: " + BackgroundResourcePath);
            return;
        }

        var sprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            new Vector2(0.5f, 0.5f),
            100f);

        var go = new GameObject("PongBackground");
        go.transform.SetParent(transform, false);
        go.transform.position = new Vector3(0f, 0f, 2f);

        var renderer = go.AddComponent<SpriteRenderer>();
        renderer.sprite = sprite;
        renderer.sortingOrder = -1000;

        float worldHeight = CircleArenaConfig.GetCameraOrthographicSize() * 2f;
        float aspect = Camera.main != null ? Camera.main.aspect : (16f / 9f);
        float worldWidth = worldHeight * aspect;
        float spriteWidth = texture.width / 100f;
        float spriteHeight = texture.height / 100f;
        float scale = Mathf.Max(worldWidth / spriteWidth, worldHeight / spriteHeight);
        go.transform.localScale = Vector3.one * scale;

        Background = go.transform;
    }

    void BuildArenaVisuals()
    {
        var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ball.name = "PongBall";
        ball.transform.position = Vector3.zero;
        ball.transform.localScale = Vector3.one * BallSize;
        DestroyCollider(ball);
        Ball = ball.transform;

        var arenaGo = new GameObject("CircleArena");
        arenaGo.transform.SetParent(transform, false);
        CircleArena = arenaGo.AddComponent<PongCircleArena>();
        Paddles = CircleArena.BuildRingOnly();
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
        ServerGame.Lines.Clear();
        ServerGame.RebuildRuntime();
    }

    void BuildClient()
    {
        Client = gameObject.AddComponent<PongClient>();
        Client.DestinationIP = string.IsNullOrEmpty(DefaultServerIP)
            ? PongNetworkUtil.DefaultLoopback
            : DefaultServerIP;
        Client.DestinationPort = DefaultPort;

        View = gameObject.AddComponent<PongNetView>();
        View.Client = Client;
        View.Ball = Ball;
        View.CircleArena = CircleArena;
        View.ForceBindAndSync();

        LocalPaddle = gameObject.AddComponent<PongNetPaddle>();
        LocalPaddle.Client = Client;
        LocalPaddle.View = View;

        ConnectOverlay = gameObject.AddComponent<PongClientConnectOverlay>();
        ConnectOverlay.Initialize(View.Client, View, DefaultServerIP, DefaultPort);
    }
}
