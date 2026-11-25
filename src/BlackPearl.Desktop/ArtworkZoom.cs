using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BlackPearl.Desktop;

// Pointing at a card zooms its picture a little inside the artwork's frame, which itself stays where it is: the frame's rounded
// clip (RoundedClip) crops the zoomed picture, so the card does not move or grow and its badges, progress light and selection
// mark keep their size. Set Hovered on the frame (a style trigger on the card's IsMouseOver does); each Image inside it zooms
// from its own middle. An image that already has a transform of its own is left alone.
public static class ArtworkZoom
{
    private const double Zoomed = 1.06;
    private static readonly Duration In = new(TimeSpan.FromMilliseconds(260)), Out = new(TimeSpan.FromMilliseconds(320));
    private static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    public static readonly DependencyProperty HoveredProperty = DependencyProperty.RegisterAttached(
        "Hovered", typeof(bool), typeof(ArtworkZoom), new PropertyMetadata(false, Changed));
    public static bool GetHovered(DependencyObject element) => (bool)element.GetValue(HoveredProperty);
    public static void SetHovered(DependencyObject element, bool value) => element.SetValue(HoveredProperty, value);

    // The zoom this class gave an image, so it never takes over a transform set by anything else.
    private static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached(
        "Scale", typeof(ScaleTransform), typeof(ArtworkZoom));

    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement frame) return;
        var to = (bool)args.NewValue ? Zoomed : 1;
        foreach (var image in Images(frame))
        {
            if (image.GetValue(ScaleProperty) is not ScaleTransform scale)
            {
                if (to == 1 || !image.RenderTransform.Value.IsIdentity) continue;
                scale = new ScaleTransform();
                image.SetValue(ScaleProperty, scale);
                image.RenderTransformOrigin = new Point(0.5, 0.5);
                image.RenderTransform = scale;
            }
            else if (!ReferenceEquals(image.RenderTransform, scale)) continue;
            if (!SystemParameters.ClientAreaAnimation)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = scale.ScaleY = to;
                continue;
            }
            var duration = to > 1 ? In : Out;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, duration) { EasingFunction = Ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, duration) { EasingFunction = Ease });
        }
    }

    private static IEnumerable<Image> Images(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is Image image) yield return image;
            foreach (var nested in Images(child)) yield return nested;
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }
}
