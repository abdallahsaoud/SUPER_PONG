using UnityEngine;

/// <summary>
/// Circular ring and curved arc platforms (LineRenderer).
/// </summary>
public class PongCircleArena : MonoBehaviour
{
    LineRenderer _ring;
    LineRenderer[] _platformLines = System.Array.Empty<LineRenderer>();

    public Transform[] Paddles => GetPlatformTransforms();

    public Transform[] BuildRingOnly()
    {
        transform.localPosition = Vector3.zero;
        BuildRing();
        SetPlatformCount(0);
        return Paddles;
    }

    Transform[] GetPlatformTransforms()
    {
        var result = new Transform[_platformLines.Length];
        for (int i = 0; i < _platformLines.Length; i++) {
            result[i] = _platformLines[i] != null ? _platformLines[i].transform : null;
        }
        return result;
    }

    public void SetPlatformCount(int count)
    {
        if (_platformLines != null) {
            for (int i = 0; i < _platformLines.Length; i++) {
                if (_platformLines[i] != null) {
                    Destroy(_platformLines[i].gameObject);
                }
            }
        }

        count = Mathf.Clamp(count, 0, CircleArenaConfig.MaxPlayers);
        _platformLines = new LineRenderer[count];
        for (int i = 0; i < count; i++) {
            var go = new GameObject("Platform" + i);
            go.transform.SetParent(transform, false);

            var line = go.AddComponent<LineRenderer>();
            CircleArenaConfig.ConfigureArcLineRenderer(line);
            float angle = CircleArenaConfig.GetInitialAngleRad(i, count);
            CircleArenaConfig.UpdateArcPlatform(line, angle, count);
            SetArcColor(line, CircleArenaConfig.RemotePlatformColor);

            _platformLines[i] = line;
        }
    }

    public LineRenderer GetPlatformLine(int index)
    {
        if (_platformLines == null || index < 0 || index >= _platformLines.Length) return null;
        return _platformLines[index];
    }

    public static void SetPlatformColor(Transform platform, Color color)
    {
        if (platform == null) return;
        var line = platform.GetComponent<LineRenderer>();
        if (line != null) SetArcColor(line, color);
    }

    static void SetArcColor(LineRenderer line, Color color)
    {
        CircleArenaConfig.SetArcPlatformColor(line, color);
    }

    public void UpdatePlatformAngle(int index, float angleRad)
    {
        var line = GetPlatformLine(index);
        if (line == null) return;
        CircleArenaConfig.UpdateArcPlatform(line, angleRad, _platformLines.Length);
    }

    public void SetPlatformActive(int index, bool active)
    {
        var line = GetPlatformLine(index);
        if (line == null) return;
        line.gameObject.SetActive(active);
    }

    void BuildRing()
    {
        if (_ring != null) Destroy(_ring.gameObject);

        var ringGo = new GameObject("ArenaRing");
        ringGo.transform.SetParent(transform, false);

        _ring = ringGo.AddComponent<LineRenderer>();
        _ring.useWorldSpace = false;
        _ring.loop = true;
        int seg = CircleArenaConfig.RingSegments;
        _ring.positionCount = seg;
        _ring.widthMultiplier = CircleArenaConfig.RingLineWidth;

        var shader = Shader.Find("Sprites/Default");
        if (shader != null) _ring.material = new Material(shader);
        _ring.startColor = CircleArenaConfig.RingColor;
        _ring.endColor = CircleArenaConfig.RingColor;

        for (int i = 0; i < seg; i++) {
            float t = (float)i / seg * Mathf.PI * 2f;
            _ring.SetPosition(i, new Vector3(
                Mathf.Cos(t) * CircleArenaConfig.Radius,
                Mathf.Sin(t) * CircleArenaConfig.Radius,
                0f));
        }
    }
}
