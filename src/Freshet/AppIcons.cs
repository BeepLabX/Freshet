using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Freshet;

public sealed record AppIconResult(ImageSource Image, string Origin);
public sealed record InstalledIcon(string Name, string Identifier, string Reference, bool ShellItem = false);

public sealed class AppIcons
{
    public static ImageSource Default { get; } = MakeDefault();
    private static readonly Brush LightBackground = FrozenBrush(240, 243, 249);
    private static readonly Brush DarkBackground = FrozenBrush(47, 61, 82);
    private static Brush FrozenBrush(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
    public static Brush BackgroundFor(ImageSource image)
    {
        if (image is BitmapSource bitmap)
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight]; converted.CopyPixels(pixels, stride, 0);
            int visible = 0, white = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                if (pixels[i + 3] > 32) { visible++; if (pixels[i] > 225 && pixels[i + 1] > 225 && pixels[i + 2] > 225) white++; }
            if (visible > 0 && white > visible * 0.8) return DarkBackground;
        }
        return LightBackground;
    }
    private static ImageSource MakeDefault()
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(232, 238, 254)), null, new Rect(0, 0, 36, 36), 8, 8);
            foreach (var (x, y) in new[] { (9, 9), (19, 9), (9, 19), (19, 19) })
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(95, 124, 211)), null, new Rect(x, y, 8, 8), 1, 1);
        }
        drawing.Freeze();
        var image = new DrawingImage(drawing); image.Freeze(); return image;
    }
    public static string NormalizeName(string name)
    {
        name = Regex.Replace(name, @"\s*[\(（](?:x64|x86|64.bit|32.bit|arm64)[\)）]", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\s+v?\d+(?:\.\d+){1,}(?:\s|$).*$", "", RegexOptions.IgnoreCase);
        return Regex.Replace(name.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
    }
    public static InstalledIcon? Match(PackageEntry p, IEnumerable<InstalledIcon> entries)
    {
        var all = entries.ToList();
        var ids = all.Where(e => e.Identifier.Equals(p.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (ids.Count == 1) return ids[0];
        static string FullName(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
        var exact = all.Where(e => FullName(e.Name) == FullName(p.Name)).DistinctBy(e => e.Reference, StringComparer.OrdinalIgnoreCase).ToList();
        if (exact.Count == 1) return exact[0];
        string name = NormalizeName(p.Name);
        if (name.Length < 2) return null;
        var matches = all.Where(e => NormalizeName(e.Name) == name).ToList();
        // Distinct registrations may point to the same icon (HKLM/HKCU or both views).
        var unique = matches.DistinctBy(e => e.Reference, StringComparer.OrdinalIgnoreCase).ToList();
        return unique.Count == 1 ? unique[0] : null;
    }
    public static (string Path, int Index)? ParseReference(string reference)
    {
        var match = Regex.Match(Environment.ExpandEnvironmentVariables(reference.Trim()), "^(?:\"(?<path>[^\"]+)\"|(?<path>.+?))(?:\\s*,\\s*(?<index>-?\\d+))?$");
        if (!match.Success) return null;
        string path = match.Groups["path"].Value.Trim();
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || path.Contains('"') || path.Length < 3 || path[1] != ':') return null;
        if (!new[] { ".exe", ".dll", ".ico", ".lnk" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return null;
        return (path, int.TryParse(match.Groups["index"].Value, out int index) ? index : 0);
    }
    public Task<IReadOnlyList<InstalledIcon>> ReadRegistryAsync(CancellationToken token) => Task.Run<IReadOnlyList<InstalledIcon>>(() => {
        var result = new List<InstalledIcon>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (string keyName in uninstall.GetSubKeyNames())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        using var key = uninstall.OpenSubKey(keyName);
                        string name = key?.GetValue("DisplayName") as string ?? "";
                        string icon = key?.GetValue("DisplayIcon") as string ?? "";
                        string id = key?.GetValue("WinGetPackageIdentifier") as string ?? keyName;
                        if (name.Length > 0 && ParseReference(icon) is not null) result.Add(new(name, id, icon));
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return result;
    }, token);

    public Task<Dictionary<PackageEntry, AppIconResult>> LoadAsync(IReadOnlyList<PackageEntry> packages, CancellationToken token)
    {
        var completion = new TaskCompletionSource<Dictionary<PackageEntry, AppIconResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try
            {
                var registry = ReadRegistryAsync(token).GetAwaiter().GetResult();
                var result = new Dictionary<PackageEntry, AppIconResult>();
                var missing = new List<PackageEntry>();
                foreach (var p in packages)
                {
                    token.ThrowIfCancellationRequested();
                    var match = Match(p, registry);
                    var image = match is null ? null : LoadReference(match.Reference);
                    if (image is not null) result[p] = new(image, "로컬 설치 정보"); else missing.Add(p);
                }
                if (missing.Count > 0)
                {
                    var shell = ReadShellApps(token);
                    foreach (var p in missing)
                    {
                        token.ThrowIfCancellationRequested();
                        var match = Match(p, shell);
                        var image = match is null ? null : LoadShellImage(match.Reference);
                        result[p] = new(image ?? Default, image is null ? "기본 아이콘" : "Windows 앱 목록");
                    }
                }
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "Freshet app icons" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
    private static List<InstalledIcon> ReadShellApps(CancellationToken token)
    {
        var result = new List<InstalledIcon>();
        object? shell = null, folder = null, items = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return result;
            shell = Activator.CreateInstance(type);
            folder = ((dynamic)shell!).NameSpace("shell:AppsFolder");
            if (folder is null) return result;
            items = ((dynamic)folder).Items();
            int count = ((dynamic)items).Count;
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                object? item = null;
                try
                {
                    item = ((dynamic)items).Item(i);
                    string name = ((dynamic)item).Name;
                    string path = ((dynamic)item).Path;
                    if (name.Length > 0 && path.Length > 0) result.Add(new(name, path, @"shell:AppsFolder\" + path, true));
                }
                finally { Release(item); }
            }
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally { Release(items); Release(folder); Release(shell); }
        return result;
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    public static ImageSource? LoadReference(string reference)
    {
        var parsed = ParseReference(reference);
        if (parsed is not { } r || !File.Exists(r.Path)) return null;
        IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
        try
        {
            if (ExtractIconEx(r.Path, r.Index, out large, out small, 1) == 0) return null;
            IntPtr handle = large != IntPtr.Zero ? large : small;
            if (handle == IntPtr.Zero) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            image.Freeze(); return image;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or COMException) { return null; }
        finally { if (large != IntPtr.Zero) DestroyIcon(large); if (small != IntPtr.Zero && small != large) DestroyIcon(small); }
    }
    private static ImageSource? LoadShellImage(string parsingName)
    {
        IShellItemImageFactory? factory = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out factory) != 0 || factory is null) return null;
            if (factory.GetImage(new NativeSize { Width = 32, Height = 32 }, 0x4 | 0x1, out bitmap) != 0 || bitmap == IntPtr.Zero) return null;
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); return image;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException) { return null; }
        finally { if (bitmap != IntPtr.Zero) DeleteObject(bitmap); Release(factory); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width; public int Height; }
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHCreateItemFromParsingName(string name, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);
}
