using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Map;

namespace Content.Client.Sandbox.DeviceLink;

public sealed class DeviceLinkDebugOverlay : Overlay
{
    private const float SourceMarkerRadius = 0.12f;

    [Dependency] private readonly IEntityManager _ent = default!;

    private readonly DeviceLinkOverlaySystem _deviceLink;
    private readonly EntityQuery<TransformComponent> _transformQuery;
    private readonly SharedTransformSystem _transform;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    public DeviceLinkDebugOverlay()
    {
        IoCManager.InjectDependencies(this);

        _deviceLink = _ent.System<DeviceLinkOverlaySystem>();
        _transformQuery = _ent.GetEntityQuery<TransformComponent>();
        _transform = _ent.System<SharedTransformSystem>();
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        return args.Viewport.Eye != null && _deviceLink.Rays.Count > 0;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (args.Space != OverlaySpace.WorldSpace)
            return;

        foreach (var (source, connections) in _deviceLink.Rays)
        {
            if (!_transformQuery.TryComp(source, out var sourceTransform) ||
                sourceTransform.MapID == MapId.Nullspace)
            {
                continue;
            }

            var color = _deviceLink.SourceColors.TryGetValue(source, out var rayColor)
                ? rayColor
                : Color.White;
            var sourcePos = _transform.GetWorldPosition(sourceTransform);

            args.WorldHandle.DrawCircle(sourcePos, SourceMarkerRadius, color);

            foreach (var connection in connections)
            {
                if (!_transformQuery.TryComp(connection, out var destinationTransform) ||
                    destinationTransform.MapID == MapId.Nullspace)
                {
                    continue;
                }

                args.WorldHandle.DrawLine(
                    sourcePos,
                    _transform.GetWorldPosition(destinationTransform),
                    color);
            }
        }
    }
}
