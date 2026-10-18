using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OpenXLR.UI;

/// <summary>
/// The icons the window can draw by name: the built-in set, with the worn
/// skin's own icons over it. A skin's path data was checked when the skin was
/// read; it becomes a geometry here, on the UI thread, the first time it is
/// drawn, and an entry Avalonia still refuses falls back to the built-in icon
/// of that name, or to nothing.
/// </summary>
public static class LayoutIconRegistry
{
    private static IReadOnlyDictionary<string, string> _skin = new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly Dictionary<string, Geometry?> Cache = new(StringComparer.Ordinal);

    /// <summary>Raised after the worn skin's icons change, so drawn icons can redraw.</summary>
    public static event EventHandler? Changed;

    /// <summary>The worn skin's icons, name to path data.</summary>
    public static IReadOnlyDictionary<string, string> SkinIcons => _skin;

    /// <summary>Use another skin's icons; null or empty for none.</summary>
    public static void UseSkin(IReadOnlyDictionary<string, string>? icons)
    {
        icons ??= new Dictionary<string, string>(StringComparer.Ordinal);
        if (icons.Count == _skin.Count && icons.All(p => _skin.TryGetValue(p.Key, out string? path) && path == p.Value)) return;
        _skin = new Dictionary<string, string>(icons, StringComparer.Ordinal);
        Cache.Clear();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Whether the window can draw this name: a skin icon or a built-in one.</summary>
    public static bool Knows(string name) => _skin.ContainsKey(name) || LayoutIcons.Find(name) is not null;

    /// <summary>Whether the worn skin's own drawing for this name parses.</summary>
    public static bool SkinIconDraws(string name) => _skin.TryGetValue(name, out string? path) && Parse(path) is not null;

    /// <summary>The geometry for a name, the skin's drawing first; null when neither parses.</summary>
    public static Geometry? Geometry(string name)
    {
        if (Cache.TryGetValue(name, out Geometry? cached)) return cached;
        Geometry? geometry = null;
        if (_skin.TryGetValue(name, out string? skinPath)) geometry = Parse(skinPath);
        if (geometry is null && LayoutIcons.Find(name) is { } builtIn) geometry = Parse(builtIn.Path);
        Cache[name] = geometry;
        return geometry;
    }

    // F1 is the nonzero fill rule, which is SVG's default and the one the
    // icon format documents.
    private static Geometry? Parse(string path)
    {
        try { return Avalonia.Media.Geometry.Parse("F1 " + path); }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}

/// <summary>
/// A channel's or mix's icon beside its name: a legacy glyph as text, a named
/// icon as a vector drawing in the text's colour and size, centred on the
/// capitals of the name beside it, nothing for
/// <c>none</c>, and the default by kind for an empty or unknown name. It is a
/// text block to the skin, so the classes that colour and size the name
/// beside it apply to the icon the same way.
/// </summary>
public sealed class LayoutIconBlock : TextBlock
{
    /// <summary>The stored icon, as the layout holds it.</summary>
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<LayoutIconBlock, string?>(nameof(Icon));

    /// <summary>The default icon for the item's kind, drawn when <see cref="Icon"/> is empty or unknown.</summary>
    public static readonly StyledProperty<string?> FallbackProperty =
        AvaloniaProperty.Register<LayoutIconBlock, string?>(nameof(Fallback));

    // Gives a drawn icon the line height of the text it stands beside.
    private const string LineHolder = " ";

    private Geometry? _geometry;

    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Fallback { get => GetValue(FallbackProperty); set => SetValue(FallbackProperty, value); }

    /// <summary>The name or glyph drawn now; empty when nothing is.</summary>
    public string Shown { get; private set; } = "";

    /// <summary>Whether <see cref="Shown"/> is drawn as a vector rather than as text.</summary>
    public bool DrawsPath => _geometry is not null;

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty || change.Property == FallbackProperty) Update();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LayoutIconRegistry.Changed += OnRegistryChanged;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LayoutIconRegistry.Changed -= OnRegistryChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnRegistryChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        Shown = LayoutIcons.Resolve(Icon, Fallback, LayoutIconRegistry.Knows);
        _geometry = Shown.Length == 0 || LayoutIcons.IsGlyph(Shown) ? null : LayoutIconRegistry.Geometry(Shown);
        Text = _geometry is not null ? LineHolder
            : LayoutIcons.IsGlyph(Shown) ? Shown
            : LayoutIcons.Find(Shown)?.Text ?? "";
        InvalidateMeasure();
        InvalidateVisual();
    }

    private double Side => FontSize * 1.15;

    protected override Size MeasureOverride(Size availableSize)
    {
        Size text = base.MeasureOverride(availableSize);
        return _geometry is null ? text : new Size(Math.Max(text.Width, Side + Padding.Left + Padding.Right), text.Height);
    }

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        if (_geometry is null)
        {
            base.RenderTextLayout(context, origin);
            return;
        }
        Rect box = new Rect(Bounds.Size).Deflate(Padding);
        double side = Math.Min(Side, Math.Min(box.Width, box.Height));
        if (side <= 0) return;
        double scale = side / 24;
        double x = box.X + (box.Width - side) / 2;
        // Centred on the capitals of the name beside it, not on the line box:
        // the line leaves room under the baseline for descenders, so a box
        // centred on it sits visibly below the letters. Cap height is close
        // to 0.72 em in the fonts the window uses.
        double baseline = origin.Y + (TextLayout.TextLines.Count > 0 ? TextLayout.TextLines[0].Baseline : box.Height * 0.8);
        double y = baseline - FontSize * 0.36 - side / 2;
        y = Math.Clamp(y, box.Y, Math.Max(box.Y, box.Bottom - side));
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x, y)))
            context.DrawGeometry(Foreground, null, _geometry);
    }
}
