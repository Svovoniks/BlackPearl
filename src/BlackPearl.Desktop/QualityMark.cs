using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace BlackPearl.Desktop;

// How sharp a video is, as a solid rounded plate with its label ("4K", "FHD") cut out of it, so the page shows through
// the letters. The letters are centered on their ink rather than their line box, and the plate's size is whole device
// pixels, so its edges stay crisp at every display scaling.
public sealed class QualityMark : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(QualityMark),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(QualityMark),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(QualityMark),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(QualityMark),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    // Space between the letters' ink and the plate's edge.
    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(nameof(Padding), typeof(Thickness), typeof(QualityMark),
        new FrameworkPropertyMetadata(new Thickness(5, 4, 5, 4), FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(QualityMark),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    // The mark is small, so Full HD is written the short way.
    public static string Short(string? label) => label == "Full HD" ? "FHD" : label ?? "";

    private Geometry? shape;

    protected override Size MeasureOverride(Size availableSize)
    {
        shape = null;
        if (string.IsNullOrEmpty(Text)) return default;
        var dpi = VisualTreeHelper.GetDpi(this);
        var face = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Black, FontStretches.Normal);
        var text = new FormattedText(Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, FontSize, Brushes.White, dpi.PixelsPerDip);
        var letters = text.BuildGeometry(new Point());
        var ink = letters.Bounds;
        if (ink.IsEmpty) return default;
        static double Snap(double value, double scale) => Math.Ceiling(value * scale - 0.01) / scale;
        var padding = Padding;
        var size = new Size(Snap(ink.Width + padding.Left + padding.Right, dpi.DpiScaleX), Snap(ink.Height + padding.Top + padding.Bottom, dpi.DpiScaleY));
        letters.Transform = new TranslateTransform((size.Width - ink.Width) / 2 - ink.X, (size.Height - ink.Height) / 2 - ink.Y);
        var plate = new RectangleGeometry(new Rect(size), CornerRadius, CornerRadius);
        shape = new CombinedGeometry(GeometryCombineMode.Exclude, plate, letters);
        shape.Freeze();
        return size;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (shape is not null) drawingContext.DrawGeometry(Fill, null, shape);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) => InvalidateMeasure();
}
