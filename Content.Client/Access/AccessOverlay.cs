using System.Numerics;
using System.Linq;
using Content.Client.Resources;
using Content.Shared.Access;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client.Access;

public sealed class AccessOverlay : Overlay
{
    private const string TextFontPath = "/Fonts/NotoSans/NotoSans-Regular.ttf";
    private const string BoldFontPath = "/Fonts/NotoSans/NotoSans-Bold.ttf";
    private const int TextFontSize = 9;
    private const int SmallTextFontSize = 7;
    private const float MaxContentWidth = 132f;
    private const float ScreenPadding = 4f;
    private const float HorizontalMargin = 5f;
    private const float VerticalMargin = 3f;
    private const float OverlapMargin = 2f;

    private static readonly Vector2 BackgroundPadding = new(3f, 2f);
    private static readonly Color TitleColor = Color.Aquamarine;
    private static readonly Color InfoColor = Color.LightGray.WithAlpha(0.86f);
    private static readonly Color SeparatorColor = Color.Gray;
    private static readonly Color BackgroundColor = new Color(10, 12, 16).WithAlpha(0.72f);
    private static readonly Color OutlineColor = TitleColor.WithAlpha(0.8f);
    private static readonly Color FallbackAccessColor = Color.Gold;

    private readonly IEntityManager _entityManager;
    private readonly AccessReaderSystem _accessReaderSystem;
    private readonly IPrototypeManager _prototype;
    private readonly SharedTransformSystem _transformSystem;
    private readonly Font _font;
    private readonly Font _smallFont;
    private readonly Font _boldFont;
    private readonly List<Token> _tokens = new();
    private readonly List<List<Token>> _lines = new();
    private readonly List<UIBox2> _occupiedRects = new();

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    public AccessOverlay(
        IEntityManager entityManager,
        IResourceCache resourceCache,
        SharedTransformSystem transformSystem,
        AccessReaderSystem accessReaderSystem,
        IPrototypeManager prototype)
    {
        _entityManager = entityManager;
        _accessReaderSystem = accessReaderSystem;
        _prototype = prototype;
        _transformSystem = transformSystem;
        _font = resourceCache.GetFont(TextFontPath, TextFontSize);
        _smallFont = resourceCache.GetFont(TextFontPath, SmallTextFontSize);
        _boldFont = resourceCache.GetFont(BoldFontPath, TextFontSize);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (args.ViewportControl == null)
            return;

        _occupiedRects.Clear();

        var query = _entityManager.EntityQueryEnumerator<AccessReaderComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var accessReader, out var transform))
        {
            if (IsContainedAccessReaderShownByParent(uid, transform))
                continue;

            var displayReader = accessReader;
            if (_accessReaderSystem.GetMainAccessReader(uid, out var mainReader))
                displayReader = mainReader.Value.Comp;

            BuildAccessTokens(displayReader);
            if (_tokens.Count == 0)
                continue;

            var entityName = _entityManager.ToPrettyString(uid);
            var title = TrimToWidth(args.ScreenHandle, _boldFont, entityName.Prototype ?? entityName.Name ?? uid.ToString(), MaxContentWidth);
            var uidText = TrimToWidth(args.ScreenHandle, _smallFont, $"UID:{entityName.Uid.Id} NUID:{entityName.Nuid.Id}", MaxContentWidth);
            var screenPos = args.ViewportControl.WorldToScreen(_transformSystem.GetWorldPosition(transform));

            BuildLines(args.ScreenHandle);
            var contentSize = GetContentSize(args.ScreenHandle, title, uidText);
            var backgroundSize = contentSize + BackgroundPadding * 2f;

            if (!TryGetBackgroundRect(screenPos, backgroundSize, args.Viewport.Size, out var rect))
                continue;

            args.ScreenHandle.DrawRect(rect, BackgroundColor);
            args.ScreenHandle.DrawRect(rect, OutlineColor, filled: false);
            _occupiedRects.Add(rect);

            var drawPos = rect.TopLeft + BackgroundPadding;
            args.ScreenHandle.DrawString(_boldFont, drawPos, title, TitleColor);
            drawPos.Y += args.ScreenHandle.GetDimensions(_boldFont, title, 1f).Y;
            args.ScreenHandle.DrawString(_smallFont, drawPos, uidText, InfoColor);
            drawPos.Y += args.ScreenHandle.GetDimensions(_smallFont, uidText, 1f).Y;

            foreach (var line in _lines)
            {
                var linePos = drawPos;
                var lineHeight = args.ScreenHandle.GetDimensions(_font, "Hg", 1f).Y;

                foreach (var token in line)
                {
                    var size = args.ScreenHandle.DrawString(_font, linePos, token.Text, token.Color);
                    linePos.X += size.X;
                }

                drawPos.Y += lineHeight;
            }
        }
    }

    private bool IsContainedAccessReaderShownByParent(EntityUid uid, TransformComponent transform)
    {
        var parent = transform.ParentUid;
        while (parent.IsValid())
        {
            if (_entityManager.TryGetComponent(parent, out AccessReaderComponent? parentReader) &&
                parentReader.ContainerAccessProvider != null &&
                _accessReaderSystem.GetMainAccessReader(parent, out var parentMainReader) &&
                parentMainReader.Value.Owner == uid)
            {
                return true;
            }

            if (!_entityManager.TryGetComponent(parent, out TransformComponent? parentTransform))
                break;

            parent = parentTransform.ParentUid;
        }

        return false;
    }

    private void BuildAccessTokens(AccessReaderComponent accessReader)
    {
        _tokens.Clear();

        if (!accessReader.Enabled)
        {
            _tokens.Add(new Token(Loc.GetString("access-overlay-reader-disabled"), Color.IndianRed));
            return;
        }

        if (accessReader.AccessLists.Count == 0)
        {
            _tokens.Add(new Token(Loc.GetString("access-overlay-reader-unrestricted"), Color.LightGreen));
        }
        else
        {
            var groupIndex = 0;
            foreach (var accessGroup in accessReader.AccessLists)
            {
                if (accessGroup.Count == 0)
                    continue;

                if (groupIndex > 0)
                    AddSeparator(" / ");

                var accessIndex = 0;
                foreach (var access in accessGroup.OrderBy(x => GetAccessText(x)))
                {
                    if (accessIndex > 0)
                        AddSeparator(" + ");

                    _tokens.Add(GetAccessToken(access));
                    accessIndex++;
                }

                groupIndex++;
            }
        }

        foreach (var key in accessReader.AccessKeys.OrderBy(x => $"{x.OriginStation}:{x.Id}"))
        {
            if (_tokens.Count > 0)
                AddSeparator(" + ");

            _tokens.Add(new Token($"{key.OriginStation}:{key.Id}", FallbackAccessColor));
        }

        foreach (var tag in accessReader.DenyTags.OrderBy(x => x.Id))
        {
            if (_tokens.Count > 0)
                AddSeparator(" / ");

            _tokens.Add(new Token($"-{tag.Id}", Color.IndianRed));
        }
    }

    private void BuildLines(DrawingHandleScreen handle)
    {
        _lines.Clear();

        var currentLine = new List<Token>();
        var currentWidth = 0f;

        foreach (var rawToken in _tokens)
        {
            var token = rawToken with { Text = TrimToWidth(handle, _font, rawToken.Text, MaxContentWidth) };
            var tokenWidth = handle.GetDimensions(_font, token.Text, 1f).X;
            if (currentLine.Count > 0 && currentWidth + tokenWidth > MaxContentWidth)
            {
                TrimTrailingSeparator(currentLine);
                _lines.Add(currentLine);
                currentLine = new List<Token>();
                currentWidth = 0f;

                if (token.IsSeparator)
                    continue;
            }

            currentLine.Add(token);
            currentWidth += tokenWidth;
        }

        TrimTrailingSeparator(currentLine);
        if (currentLine.Count > 0)
            _lines.Add(currentLine);
    }

    private Vector2 GetContentSize(DrawingHandleScreen handle, string title, string uidText)
    {
        var lineHeight = handle.GetDimensions(_font, "Hg", 1f).Y;
        var width = MathF.Max(
            handle.GetDimensions(_boldFont, title, 1f).X,
            handle.GetDimensions(_smallFont, uidText, 1f).X);

        foreach (var line in _lines)
        {
            var lineWidth = 0f;
            foreach (var token in line)
            {
                lineWidth += handle.GetDimensions(_font, token.Text, 1f).X;
            }

            width = MathF.Max(width, lineWidth);
        }

        var height = handle.GetDimensions(_boldFont, title, 1f).Y
                     + handle.GetDimensions(_smallFont, uidText, 1f).Y
                     + lineHeight * _lines.Count;

        return new Vector2(width, height);
    }

    private bool TryGetBackgroundRect(Vector2 screenPos, Vector2 backgroundSize, Vector2 viewportSize, out UIBox2 rect)
    {
        var anchor = UIBox2.FromDimensions(screenPos, Vector2.One);

        return TryResolvePlacement(anchor, backgroundSize, viewportSize, LabelPlacement.Below, true, out rect)
               || TryResolvePlacement(anchor, backgroundSize, viewportSize, LabelPlacement.Above, true, out rect)
               || TryResolvePlacement(anchor, backgroundSize, viewportSize, LabelPlacement.Right, true, out rect)
               || TryResolvePlacement(anchor, backgroundSize, viewportSize, LabelPlacement.Left, true, out rect)
               || TryResolveOffsetPlacement(anchor, backgroundSize, viewportSize, out rect);
    }

    private bool TryResolvePlacement(
        UIBox2 anchor,
        Vector2 backgroundSize,
        Vector2 viewportSize,
        LabelPlacement placement,
        bool checkOverlap,
        out UIBox2 rect)
    {
        rect = GetBackgroundRect(anchor, backgroundSize, placement);

        if (!FitsViewport(rect, viewportSize))
            return false;

        if (!checkOverlap)
            return true;

        foreach (var occupied in _occupiedRects)
        {
            if (Enlarge(occupied, OverlapMargin).Intersects(rect))
                return false;
        }

        return true;
    }

    private static UIBox2 GetBackgroundRect(UIBox2 anchor, Vector2 backgroundSize, LabelPlacement placement)
    {
        return placement switch
        {
            LabelPlacement.Above => UIBox2.FromDimensions(
                new Vector2(anchor.Center.X - backgroundSize.X * 0.5f, anchor.Top - VerticalMargin - backgroundSize.Y),
                backgroundSize),
            LabelPlacement.Right => UIBox2.FromDimensions(
                new Vector2(anchor.Right + HorizontalMargin, anchor.Center.Y - backgroundSize.Y * 0.5f),
                backgroundSize),
            LabelPlacement.Left => UIBox2.FromDimensions(
                new Vector2(anchor.Left - HorizontalMargin - backgroundSize.X, anchor.Center.Y - backgroundSize.Y * 0.5f),
                backgroundSize),
            _ => UIBox2.FromDimensions(
                new Vector2(anchor.Center.X - backgroundSize.X * 0.5f, anchor.Bottom + VerticalMargin),
                backgroundSize),
        };
    }

    private static bool FitsViewport(UIBox2 rect, Vector2 viewportSize)
    {
        return rect.Left >= ScreenPadding &&
               rect.Top >= ScreenPadding &&
               rect.Right <= viewportSize.X - ScreenPadding &&
               rect.Bottom <= viewportSize.Y - ScreenPadding;
    }

    private static UIBox2 Enlarge(UIBox2 rect, float amount)
    {
        return new UIBox2(
            rect.Left - amount,
            rect.Top - amount,
            rect.Right + amount,
            rect.Bottom + amount);
    }

    private Token GetAccessToken(ProtoId<AccessLevelPrototype> access)
    {
        if (!_prototype.TryIndex(access, out var accessPrototype))
            return new Token(access.Id, FallbackAccessColor);

        return ParseAccessMarkup(accessPrototype.GetAccessLevelName(), access.Id);
    }

    private string GetAccessText(ProtoId<AccessLevelPrototype> access)
    {
        return GetAccessToken(access).Text;
    }

    private static Token ParseAccessMarkup(string markup, string fallback)
    {
        var color = FallbackAccessColor;
        var colorStart = markup.IndexOf("[color=", StringComparison.OrdinalIgnoreCase);
        if (colorStart >= 0)
        {
            var valueStart = colorStart + "[color=".Length;
            var valueEnd = markup.IndexOf(']', valueStart);
            if (valueEnd > valueStart)
                color = Color.FromHex(markup[valueStart..valueEnd], FallbackAccessColor);
        }

        var text = FormattedMessage.RemoveMarkupPermissive(markup);
        if (string.IsNullOrWhiteSpace(text))
            text = fallback;

        return new Token(text, color);
    }

    private void AddSeparator(string text)
    {
        _tokens.Add(new Token(text, SeparatorColor, true));
    }

    private static void TrimTrailingSeparator(List<Token> line)
    {
        while (line.Count > 0 && line[^1].IsSeparator)
        {
            line.RemoveAt(line.Count - 1);
        }
    }

    private static string TrimToWidth(DrawingHandleScreen handle, Font font, string text, float maxWidth)
    {
        if (handle.GetDimensions(font, text, 1f).X <= maxWidth)
            return text;

        const string ellipsis = "...";
        var maxTextWidth = maxWidth - handle.GetDimensions(font, ellipsis, 1f).X;
        if (maxTextWidth <= 0f)
            return ellipsis;

        var trimmed = text;
        while (trimmed.Length > 0 && handle.GetDimensions(font, trimmed, 1f).X > maxTextWidth)
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Length == 0 ? ellipsis : trimmed + ellipsis;
    }

    private bool TryResolveOffsetPlacement(UIBox2 anchor, Vector2 backgroundSize, Vector2 viewportSize, out UIBox2 rect)
    {
        var offsets = new[]
        {
            new Vector2(-backgroundSize.X - HorizontalMargin, -backgroundSize.Y - VerticalMargin),
            new Vector2(HorizontalMargin, -backgroundSize.Y - VerticalMargin),
            new Vector2(-backgroundSize.X - HorizontalMargin, VerticalMargin),
            new Vector2(HorizontalMargin, VerticalMargin),
            new Vector2(-backgroundSize.X * 0.5f, -backgroundSize.Y - VerticalMargin * 3f),
            new Vector2(-backgroundSize.X * 0.5f, VerticalMargin * 3f),
        };

        foreach (var offset in offsets)
        {
            rect = UIBox2.FromDimensions(anchor.Center + offset, backgroundSize);
            if (!FitsViewport(rect, viewportSize))
                continue;

            var overlaps = false;
            foreach (var occupied in _occupiedRects)
            {
                if (!Enlarge(occupied, OverlapMargin).Intersects(rect))
                    continue;

                overlaps = true;
                break;
            }

            if (!overlaps)
                return true;
        }

        rect = default;
        return false;
    }

    private readonly record struct Token(string Text, Color Color, bool IsSeparator = false);

    private enum LabelPlacement : byte
    {
        Below,
        Above,
        Right,
        Left,
    }
}
