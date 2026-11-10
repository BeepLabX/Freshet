using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Freshet;

// Theme tokens live in Application resources (XAML uses DynamicResource). WPF freezes brushes stored there,
// so a theme switch publishes new brushes; code that holds a brush re-reads it after Apply.
public static class Theme
{
    private static readonly string[] Keys = ["Bg", "Surface", "SurfaceAlt", "AltRow", "HeldRow", "Text", "Muted", "Subtle", "Border",
        "Accent", "AccentText", "AccentSoft", "AccentBorder", "Selection", "FocusRing", "OnAccent",
        "Warning", "WarnSoft", "Success", "Danger", "ScrollGrip", "ScrollGripHover", "Hero1", "Hero2"];
    private static readonly Dictionary<string, string> Light = Palette(
        "#F5F7FA #FFFFFF #F3F6FB #FAFBFE #EEF1F5 #263445 #718096 #A3ACBA #E4EAF3 #3182F6 #4169C4 #EAF0FF #CFDBFC #EDF2FF #165FC0 #FFFFFF #A46B14 #FFF4E5 #477163 #BE4545 #BAC6D8 #7C91B3 #EDF5FF #F0F2FF");
    private static readonly Dictionary<string, string> Dark = Palette(
        "#14181F #1C222B #232A35 #1F252F #262C36 #E6EAF0 #9AA6B8 #6E7A8C #2E3744 #4C94FF #8AB4F8 #1E2B42 #2F4670 #22324D #8AB4F8 #FFFFFF #E8B45A #3A2E1A #7FC2A8 #F28B82 #3A4454 #56637A #1B2740 #1F2238");
    private static readonly Dictionary<string, SolidColorBrush> Brushes = [];
    public static bool IsDark { get; private set; }
    public static bool? Override { get; set; }

    static Theme() => Apply();
    private static Dictionary<string, string> Palette(string colors) => Keys.Zip(colors.Split(' ')).ToDictionary(x => x.First, x => x.Second);
    public static SolidColorBrush Brush(string key) => Brushes[key];

    public static void Apply()
    {
        IsDark = Override ?? SystemUsesDarkApps();
        var palette = IsDark ? Dark : Light;
        var colors = Keys.ToDictionary(k => k, k => (Color)ColorConverter.ConvertFromString(palette[k]));
        if (SystemParameters.HighContrast) ApplyHighContrast(colors);
        foreach (var key in Keys) { var brush = new SolidColorBrush(colors[key]); brush.Freeze(); Brushes[key] = brush; }
        var hero = new LinearGradientBrush(colors["Hero1"], colors["Hero2"], new Point(0, 0), new Point(1, 1));
        hero.Freeze();
        if (Application.Current is { } app)
        {
            foreach (var key in Keys) app.Resources[key] = Brushes[key];
            app.Resources["Hero"] = hero;
            foreach (Window window in app.Windows) TitleBar(window);
        }
    }
    private static void ApplyHighContrast(Dictionary<string, Color> colors)
    {
        void Set(string key, Color color) => colors[key] = color;
        foreach (var key in new[] { "Bg", "Surface", "AltRow", "AccentSoft", "WarnSoft", "Hero1", "Hero2" }) Set(key, SystemColors.WindowColor);
        foreach (var key in new[] { "SurfaceAlt", "HeldRow", "Selection" }) Set(key, SystemColors.ControlColor);
        foreach (var key in new[] { "Text", "Border", "AccentBorder", "Warning", "Success", "Danger", "ScrollGripHover" }) Set(key, SystemColors.WindowTextColor);
        foreach (var key in new[] { "Muted", "Subtle", "ScrollGrip" }) Set(key, SystemColors.GrayTextColor);
        foreach (var key in new[] { "Accent", "FocusRing" }) Set(key, SystemColors.HighlightColor);
        Set("AccentText", SystemColors.HotTrackColor);
        Set("OnAccent", SystemColors.HighlightTextColor);
    }
    private static bool SystemUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (System.Security.SecurityException) { return false; }
    }
    // Standard-chrome windows (dialogs) get a matching dark title bar on Windows 10 20H1+ and 11.
    public static void TitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) { window.SourceInitialized += (_, _) => TitleBar(window); return; }
        int dark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
