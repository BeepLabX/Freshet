using System.Windows;
using System.Windows.Media;

namespace Freshet;

// Original vector artwork, drawn at any DPI; no third-party brand assets.
public sealed class SoftIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(SoftIcon), new FrameworkPropertyMetadata("apps", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static LinearGradientBrush Gradient(string a, string b) => new((Color)ColorConverter.ConvertFromString(a), (Color)ColorConverter.ConvertFromString(b), 65);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double size = Math.Min(ActualWidth, ActualHeight); if (size <= 0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(size / 64, size / 64));
        string pale = Kind switch { "history" => "#EFE8FF", _ => "#E5EEFF" };
        dc.DrawRoundedRectangle(Brush(pale), null, new Rect(3, 3, 58, 58), 20, 20);
        dc.DrawEllipse(Brush("#180C294C"), null, new Point(33, 51), 18, 4);
        if (Kind == "history")
        {
            dc.DrawEllipse(Brush("#9274CD"), null, new(33, 35), 20, 20);
            dc.DrawEllipse(Gradient("#D4B6FF", "#9A7AE0"), new Pen(Brush("#E9DAFF"), 1), new(32, 31), 20, 20);
            dc.DrawGeometry(null, new Pen(Brush("#FFFFFF"), 3.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Geometry.Parse("M32,19 L32,32 L41,37"));
        }
        else if (Kind == "settings")
        {
            dc.DrawRoundedRectangle(Gradient("#C1CCD9", "#879AB4"), null, new Rect(14, 14, 36, 37), 12, 12);
            var pen = new Pen(Brush("#EDF3FA"), 3);
            dc.DrawLine(pen, new(23, 25), new(42, 25)); dc.DrawLine(pen, new(23, 38), new(42, 38));
            dc.DrawEllipse(Brush("#FFFFFF"), null, new(29, 25), 4, 4); dc.DrawEllipse(Brush("#FFFFFF"), null, new(37, 38), 4, 4);
        }
        else
        {
            dc.DrawRoundedRectangle(Brush("#94B8EF"), null, new Rect(20, 13, 32, 34), 10, 10);
            dc.DrawRoundedRectangle(Brush("#2863D4"), null, new Rect(12, 23, 34, 31), 10, 10);
            dc.DrawRoundedRectangle(Gradient("#78ACFF", "#327BF5"), new Pen(Brush("#A6CBFF"), 1), new Rect(12, 19, 34, 31), 10, 10);
            dc.DrawGeometry(null, new Pen(Brush("#FFFFFF"), 3.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Geometry.Parse("M29,40 L29,28 M23,34 L29,28 L35,34"));
        }
        dc.Pop(); dc.Pop();
    }
}
