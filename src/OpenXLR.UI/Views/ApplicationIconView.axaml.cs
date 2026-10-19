using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace OpenXLR.UI.Views;

/// <summary>
/// An application's icon in the APPLICATIONS section. <see cref="Icon"/> is
/// the daemon's icon name or image path; the picture is found and decoded off
/// the UI thread at the pixel size the icon is drawn, and the name's initial
/// shows until it arrives, or for good when nothing draws the icon.
/// </summary>
public partial class ApplicationIconView : UserControl
{
    public static readonly StyledProperty<string?> IconProperty =
        AvaloniaProperty.Register<ApplicationIconView, string?>(nameof(Icon));

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<ApplicationIconView, string?>(nameof(Label));

    private int _request;
    private (string? Icon, int Pixels) _shown;

    public ApplicationIconView()
    {
        InitializeComponent();
    }

    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>Whether the theme's picture is drawn rather than the initial.</summary>
    public bool ShowsPicture => Picture.IsVisible;

    /// <summary>The letter the badge shows: the name's first letter or digit, as a capital.</summary>
    public static string InitialOf(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "";
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(label.Trim());
        string? first = null;
        while (elements.MoveNext())
        {
            string element = (string)elements.Current;
            first ??= element;
            if (char.IsLetterOrDigit(element, 0)) return element.ToUpper(CultureInfo.CurrentCulture);
        }
        return first ?? "";
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty) Initial.Text = InitialOf(Label);
        else if (change.Property == IconProperty || change.Property == BoundsProperty) Request();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Request();
    }

    private async void Request()
    {
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        double side = Math.Min(Bounds.Width, Bounds.Height);
        int pixels = side > 0 ? (int)Math.Ceiling(side * scaling) : 0;
        string? icon = Icon;
        if (string.IsNullOrWhiteSpace(icon)) { Show(null); _shown = (null, 0); return; }
        if (pixels == 0 || _shown == (icon, pixels)) return;
        _shown = (icon, pixels);
        int request = ++_request;
        Bitmap? picture;
        try { picture = await ApplicationIconImages.Load(icon, pixels).ConfigureAwait(true); }
        catch (Exception) { picture = null; }   // no icon is a badge, never an error
        if (request != _request) return;   // a newer icon or size asked since
        Show(picture);
    }

    private void Show(Bitmap? picture)
    {
        _request++;
        Picture.Source = picture;
        Picture.IsVisible = picture is not null;
        Badge.IsVisible = picture is null;
    }
}
