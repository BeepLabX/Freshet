using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Freshet;

internal static class Motion
{
    public static bool Reduced { get; set; }
    private static bool initialized;
    public static bool Enabled => !Reduced && SystemParameters.ClientAreaAnimation;
    public static void Initialize()
    {
        if (initialized) return; initialized = true;
        EventManager.RegisterClassHandler(typeof(Button), UIElement.MouseEnterEvent, new MouseEventHandler((s, _) => Scale((Button)s, 1.018)));
        EventManager.RegisterClassHandler(typeof(Button), UIElement.MouseLeaveEvent, new MouseEventHandler((s, _) => Scale((Button)s, 1)));
        EventManager.RegisterClassHandler(typeof(Button), UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((s, _) => Scale((Button)s, 0.975)));
        EventManager.RegisterClassHandler(typeof(Button), UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((s, _) => Scale((Button)s, 1.018)));
    }
    private static void Scale(Button button, double value)
    {
        if (!Enabled || !button.IsEnabled) return;
        if (button.RenderTransform is not ScaleTransform) { button.RenderTransformOrigin = new Point(.5, .5); button.RenderTransform = new ScaleTransform(1, 1); }
        var transform = (ScaleTransform)button.RenderTransform;
        var animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation); transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
    public static void Enter(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = 1;
        element.RenderTransform = new TranslateTransform();
        if (!Enabled) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        ((TranslateTransform)element.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
