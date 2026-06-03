#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Editor menu: regenerates the two networked Pong scenes
/// (Assets/Pong/PongServer.unity and Assets/Pong/PongClient.unity).
///
/// We do this through Unity's API rather than hand-writing the YAML so the
/// scenes are guaranteed valid. Each scene contains a single GameObject with
/// a PongBootstrap component, configured for Server or Client role; the
/// bootstrap then procedurally builds the camera, ball, and paddles at Play.
/// </summary>
public static class PongSceneGenerator
{
    const string ServerScenePath = "Assets/Pong/PongServer.unity";
    const string ClientScenePath = "Assets/Pong/PongClient.unity";

    [MenuItem("Tools/Pong/Generate Network Scenes")]
    public static void GenerateAll()
    {
        // Save and unload anything currently open so we work on a clean slate.
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        GenerateScene(ServerScenePath, PongBootstrap.Role.Server);
        GenerateScene(ClientScenePath, PongBootstrap.Role.Client);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog(
            "Pong scenes generated",
            "Created:\n" + ServerScenePath + "\n" + ClientScenePath +
            "\n\nOpen one in the editor and press Play.\n" +
            "Tip: Add them to Build Profiles > Scenes in Build for a player build.",
            "OK");
    }

    static void GenerateScene(string path, PongBootstrap.Role role)
    {
        // Build the scene in-memory: a default scene (camera + light) + a single
        // PongBootstrap GameObject; bootstrap procedurally creates ball/paddles at Play.
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        var go = new GameObject("PongBootstrap");
        var bootstrap = go.AddComponent<PongBootstrap>();
        bootstrap.Mode = role;
        bootstrap.BallSize = 0.5f;
        bootstrap.DefaultServerIP = role == PongBootstrap.Role.Server ? "127.0.0.1" : "";
        bootstrap.DefaultPort = PongNetworkUtil.DefaultPort;
        bootstrap.AutoStart = role == PongBootstrap.Role.Server;

        EditorSceneManager.SaveScene(scene, path);
    }
}
#endif
