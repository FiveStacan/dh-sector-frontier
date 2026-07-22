using System.Numerics;
using Robust.Shared.Maths;

namespace Content.Server._Lua.Interserver;

/// <summary>Pure placement helpers kept separate so collision/reservation behavior can be tested without a server.</summary>
public static class InterserverPlacement
{
    public static Box2 BoundsAt(Vector2 center, Vector2 size, float margin)
    {
        var half = Vector2.Max(size * 0.5f, Vector2.One) + new Vector2(Math.Max(0, margin));
        return new Box2(center - half, center + half);
    }

    public static bool IsFree(Box2 candidate, IEnumerable<Box2> occupied)
    {
        foreach (var bounds in occupied)
        {
            if (candidate.Intersects(bounds))
                return false;
        }

        return true;
    }

    public static Vector2? FindFree(
        Vector2 size,
        float minRadius,
        float maxRadius,
        float margin,
        int attempts,
        Func<Vector2> nextCandidate,
        IReadOnlyCollection<Box2> occupied)
    {
        if (attempts <= 0 || maxRadius <= 0 || maxRadius < minRadius)
            return null;

        for (var i = 0; i < attempts; i++)
        {
            var candidate = nextCandidate();
            var radius = candidate.Length();
            if (radius < minRadius || radius > maxRadius)
                continue;
            if (IsFree(BoundsAt(candidate, size, margin), occupied))
                return candidate;
        }

        return null;
    }
}
