using UnityEngine;

/// <summary>
/// Shared circular arena geometry for visuals, paddle sync, and ball bounds.
/// </summary>
public static class CircleArenaConfig
{
    public const float Radius = 5f;
    public const int MaxPlayers = 8;

    public const float PlatformArcDegrees = 34f;
    public const float PlatformLineWidth = 0.42f;
    public const int PlatformArcSegments = 14;
    public const float RingLineWidth = 0.06f;
    public const int RingSegments = 96;
    public const float FirstSlotDegrees = 90f;

    public const float BallSpeedAccelPerBounce = 1.08f;
    public const float DefaultBallSpeed = 4f;
    public const int BallPhysicsSubsteps = 6;

    public static readonly Color RingColor = new Color(0.25f, 0.9f, 1f, 0.95f);
    public static readonly Color LocalPlatformColor = new Color(0.95f, 0.98f, 1f, 1f);
    public static readonly Color RemotePlatformColor = new Color(0.72f, 0.78f, 0.88f, 0.62f);

    public static float PlatformHalfArcRad => PlatformArcDegrees * 0.5f * Mathf.Deg2Rad;

    public static float NormalizeAngleRad(float angleRad)
    {
        float twoPi = Mathf.PI * 2f;
        angleRad %= twoPi;
        if (angleRad < 0f) angleRad += twoPi;
        return angleRad;
    }

    public static float GetInitialAngleRad(int index, int totalPlayers)
    {
        if (totalPlayers <= 0) return FirstSlotDegrees * Mathf.Deg2Rad;
        float stepDeg = 360f / totalPlayers;
        return NormalizeAngleRad((FirstSlotDegrees - index * stepDeg) * Mathf.Deg2Rad);
    }

    public static float GetBounceRadius(float ballRadius)
        => Mathf.Max(0.5f, Radius - ballRadius);

    public static void ConfigureArcLineRenderer(LineRenderer line)
    {
        line.useWorldSpace = true;
        line.loop = false;
        line.widthMultiplier = PlatformLineWidth;
        line.numCapVertices = 4;
        line.numCornerVertices = 4;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;

        var shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader != null) line.material = new Material(shader);
    }

    public static void UpdateArcPlatform(LineRenderer line, float centerAngleRad)
    {
        if (line == null) return;

        int pointCount = PlatformArcSegments;
        line.positionCount = pointCount;
        float halfArc = PlatformHalfArcRad;

        for (int i = 0; i < pointCount; i++) {
            float t = pointCount <= 1 ? 0.5f : (float)i / (pointCount - 1);
            float a = centerAngleRad - halfArc + t * (halfArc * 2f);
            line.SetPosition(i, new Vector3(
                Mathf.Cos(a) * Radius,
                Mathf.Sin(a) * Radius,
                -0.02f));
        }
    }

    public static void SetArcPlatformColor(LineRenderer line, Color color)
    {
        if (line == null) return;
        ConfigureArcLineRenderer(line);
        line.startColor = color;
        line.endColor = color;
    }

    public static float GetCameraOrthographicSize()
        => Radius + 1.8f;

    public static bool ReflectBallOffRing(ref Vector2 pos, ref Vector2 dir, float ballRadius, ref float speed)
    {
        float maxDist = GetBounceRadius(ballRadius);
        float dist = pos.magnitude;
        if (dist < 1e-4f) return false;
        if (dist <= maxDist) return false;

        Vector2 normal = pos / dist;
        pos = normal * maxDist;

        float dot = Vector2.Dot(dir, normal);
        if (dot > 0f) {
            dir = dir - 2f * dot * normal;
            if (dir.sqrMagnitude > 1e-6f) dir.Normalize();
            speed *= BallSpeedAccelPerBounce;
        }
        return true;
    }

    public static bool BallHitsPlatform(Vector2 ballPos, float ballRadius, float platformAngleRad)
    {
        float dist = ballPos.magnitude;
        float ballAngle = Mathf.Atan2(ballPos.y, ballPos.x);
        float angleDiff = Mathf.Abs(Mathf.DeltaAngle(
            ballAngle * Mathf.Rad2Deg,
            platformAngleRad * Mathf.Rad2Deg)) * Mathf.Deg2Rad;

        float radialTol = PlatformLineWidth * 0.5f + ballRadius;
        if (Mathf.Abs(dist - Radius) > radialTol) return false;

        float angularTol = PlatformHalfArcRad + Mathf.Atan2(ballRadius, Mathf.Max(Radius, 0.01f));
        return angleDiff <= angularTol;
    }
}
