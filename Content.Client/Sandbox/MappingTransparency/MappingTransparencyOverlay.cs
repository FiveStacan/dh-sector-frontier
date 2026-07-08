using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Timing;

namespace Content.Client.Sandbox.MappingTransparency;

public sealed class MappingTransparencyOverlay : Overlay
{
    [Dependency] private readonly IEntityManager _ent = default!;
    [Dependency] private readonly IEyeManager _eye = default!;

    private readonly EntityLookupSystem _lookup;
    private readonly SpriteSystem _sprite;
    private readonly List<(Entity<SpriteComponent> Ent, float BaseAlpha)> _cachedAlphas = new();

    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    public MappingTransparencyOverlay()
    {
        IoCManager.InjectDependencies(this);
        _lookup = _ent.System<EntityLookupSystem>();
        _sprite = _ent.System<SpriteSystem>();
    }

    public void ResetTransparency()
    {
        RestoreTransparency();
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        RestoreTransparency();
        ApplyTransparency();
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        return false;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
    }

    protected override void DisposeBehavior()
    {
        base.DisposeBehavior();

        RestoreTransparency();
    }

    private void ApplyTransparency()
    {
        var currentMap = _eye.CurrentEye.Position.MapId;
        var viewport = _eye.GetWorldViewport();
        var query = _ent.AllEntityQueryEnumerator<SpriteComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var sprite, out var xform))
        {
            if (!xform.Anchored || xform.MapID != currentMap)
                continue;

            if (!_lookup.GetWorldAABB(uid, xform).Intersects(viewport))
                continue;

            var targetAlpha = sprite.Color.A * (1f - MappingTransparencySystem.DefaultTransparencyPercent / 100f);
            if (MathHelper.CloseTo(sprite.Color.A, targetAlpha))
                continue;

            _cachedAlphas.Add(((uid, sprite), sprite.Color.A));
            _sprite.SetColor(((Entity<SpriteComponent>) (uid, sprite)).AsNullable(), sprite.Color.WithAlpha(targetAlpha));
        }
    }

    private void RestoreTransparency()
    {
        foreach (var (ent, baseAlpha) in _cachedAlphas)
        {
            if (MathHelper.CloseTo(ent.Comp.Color.A, baseAlpha))
                continue;

            _sprite.SetColor(ent.AsNullable(), ent.Comp.Color.WithAlpha(baseAlpha));
        }

        _cachedAlphas.Clear();
    }
}
