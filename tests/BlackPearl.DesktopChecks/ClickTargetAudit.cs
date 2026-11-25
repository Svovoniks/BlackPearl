using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

// Finds buttons that a click near their edge passes through: the press reaches the page behind the button instead of the
// button. A template whose drawn layers are all IsHitTestVisible="False" and whose root has no fill takes clicks only on
// its text or icon, so only the middle of the button worked. Each button is clicked (hit tested) just inside each edge's
// middle and a quarter of the way in from its corners; a point another element covers is skipped, and so is a button that
// is clipped, disabled, or under an element that takes no input.
static class ClickTargetAudit
{
    private const double Inset = 2.5;

    public static List<string> Find(UIElement root)
    {
        var found = new List<string>();
        foreach (var button in Buttons(root))
        {
            if (Visible(root, button) is not { } bounds || bounds.Width < 3 * Inset || bounds.Height < 3 * Inset) continue;
            var missed = new List<string>();
            foreach (var (name, at) in Points(bounds))
            {
                var hit = root.InputHitTest(at) as DependencyObject;
                if (hit is null || IsAncestor(hit, button)) missed.Add(name);
            }
            if (missed.Count > 0) found.Add($"{Describe(button)} {bounds.Width:0.#}x{bounds.Height:0.#}: clicks pass through at {string.Join(", ", missed)}");
        }
        return found.Distinct().ToList();
    }

    private static IEnumerable<ButtonBase> Buttons(DependencyObject node)
    {
        if (node is UIElement { Visibility: not Visibility.Visible } or UIElement { IsHitTestVisible: false } or UIElement { IsEnabled: false }) yield break;
        if (node is ButtonBase button && button.ActualWidth > 0 && button.ActualHeight > 0) yield return button;
        // A combo box's own toggle fills it, and is checked as the combo box's button.
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var nested in Buttons(VisualTreeHelper.GetChild(node, i))) yield return nested;
    }

    // The button's rectangle in the root's coordinates, when no clipping ancestor cuts any of it off.
    private static Rect? Visible(UIElement root, FrameworkElement button)
    {
        Rect bounds;
        try { bounds = button.TransformToAncestor(root).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight)); }
        catch (InvalidOperationException) { return null; }
        if (!new Rect(root.RenderSize).Contains(bounds)) return null;
        for (var parent = VisualTreeHelper.GetParent(button) as Visual; parent is not null && !ReferenceEquals(parent, root); parent = VisualTreeHelper.GetParent(parent) as Visual)
        {
            if (VisualTreeHelper.GetClip(parent) is not { } clip) continue;
            Rect view;
            try { view = parent.TransformToAncestor(root).TransformBounds(clip.Bounds); } catch (InvalidOperationException) { return null; }
            view.Inflate(0.5, 0.5);
            if (!view.Contains(bounds)) return null;
        }
        return bounds;
    }

    private static IEnumerable<(string, Point)> Points(Rect r)
    {
        double left = r.Left + Inset, right = r.Right - Inset, top = r.Top + Inset, bottom = r.Bottom - Inset;
        double midX = r.Left + r.Width / 2, midY = r.Top + r.Height / 2, quarterX = r.Width / 4, quarterY = r.Height / 4;
        yield return ("left", new Point(left, midY));
        yield return ("right", new Point(right, midY));
        yield return ("top", new Point(midX, top));
        yield return ("bottom", new Point(midX, bottom));
        yield return ("top left", new Point(r.Left + quarterX, top));
        yield return ("bottom right", new Point(r.Right - quarterX, bottom));
        yield return ("upper left", new Point(left, r.Top + quarterY));
        yield return ("lower right", new Point(right, r.Bottom - quarterY));
    }

    // Whether the click landed on something that contains the button: it went through the button to what holds it.
    private static bool IsAncestor(DependencyObject hit, DependencyObject button)
    {
        if (hit is not Visual and not System.Windows.Media.Media3D.Visual3D) return false;
        for (var node = VisualTreeHelper.GetParent(button); node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, hit)) return true;
        return false;
    }

    private static string Describe(FrameworkElement button)
    {
        var name = !string.IsNullOrEmpty(button.Name) ? button.Name
            : System.Windows.Automation.AutomationProperties.GetName(button) is { Length: > 0 } automation ? automation
            : button is ContentControl { Content: string text } ? text : "";
        var style = button.Style is { } s ? StyleKey(button, s) : "";
        return $"{button.GetType().Name}{(style.Length > 0 ? " [" + style + "]" : "")}{(name.Length > 0 ? " \"" + name + "\"" : "")}";
    }

    private static readonly Dictionary<Style, string> styleNames = [];
    private static string StyleKey(FrameworkElement element, Style style)
    {
        if (styleNames.Count == 0)
            foreach (var dictionary in new[] { Application.Current.Resources }.Concat(Application.Current.Resources.MergedDictionaries))
                foreach (var key in dictionary.Keys)
                    if (dictionary[key] is Style named) styleNames.TryAdd(named, key?.ToString() ?? "");
        if (styleNames.TryGetValue(style, out var found)) return found;
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement { Resources: { Count: > 0 } resources })
                foreach (var key in resources.Keys)
                    if (ReferenceEquals(resources[key], style)) return key?.ToString() ?? "";
        return "";
    }
}
