using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Freshet;

public sealed record CommandResult(int ExitCode, string Output, string Error);
public sealed record ScanResult(List<PackageEntry> Packages, CommandResult Command, int Skipped, bool HadTable);

public static class WingetTable
{
    public static string Clean(string text) => Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "").Replace("\b", "");
    public static int Width(int code) => code is >= 0x1100 and <= 0x115F or >= 0x2329 and <= 0x232A
        or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF
        or >= 0xFE10 and <= 0xFE19 or >= 0xFE30 and <= 0xFE6F or >= 0xFF01 and <= 0xFF60
        or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFF ? 2 : 1;
    public static int DisplayWidth(string text) => text.EnumerateRunes().Sum(r =>
        Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format ? 0 : Width(r.Value));
    private static string Slice(string text, int start, int end)
    {
        var sb = new StringBuilder();
        int position = 0;
        foreach (var r in text.EnumerateRunes())
        {
            if (position >= end) break;
            if (position >= start) sb.Append(r.ToString());
            position += Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format ? 0 : Width(r.Value);
        }
        return sb.ToString().Trim();
    }
    public static (List<string[]> Rows, bool HadTable) Parse(string text, int minimumColumns)
    {
        string[] lines = Clean(text).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var rows = new List<string[]>();
        int[]? starts = null;
        bool hadTable = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i].Trim(), @"^-{10,}$") && i > 0)
            {
                var header = lines[i - 1];
                // Compact `list --id --exact` tables can have only one space
                // between headers. Localized labels also contain internal spaces.
                var known = Regex.Matches(header, @"(?<!\S)(?:장치 ID|사용 가능|이름|버전|원본|Name|Id|Version|Available|Source)(?=\s|$)", RegexOptions.IgnoreCase);
                var headers = known.Count >= minimumColumns ? known : Regex.Matches(header, @"\S(?:.*?\S)?(?= {2,}|$)");
                starts = headers.Select(m => DisplayWidth(header[..m.Index])).ToArray();
                if (starts.Length < minimumColumns) { starts = null; continue; }
                hadTable = true;
                continue;
            }
            if (starts is null || string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = starts.Select((s, index) => Slice(lines[i], s, index + 1 < starts.Length ? starts[index + 1] : int.MaxValue)).ToArray();
            if (cells.Length >= minimumColumns && cells.Take(minimumColumns).All(c => c.Length > 0)) rows.Add(cells);
        }
        return (rows, hadTable);
    }
    public static (List<PackageEntry> Packages, int Skipped, bool HadTable) Upgrades(string text)
    {
        var parsed = Parse(text, 5);
        int skipped = 0;
        var packages = new List<PackageEntry>();
        foreach (var c in parsed.Rows)
        {
            if (!IsIdentifier(c[1]) || !IsIdentifier(c[4]) || c[2].Any(char.IsWhiteSpace) || c[3].Any(char.IsWhiteSpace))
            { skipped++; continue; }
            packages.Add(new() { Name = c[0], Id = c[1], Installed = c[2], Available = c[3], Source = c[4] });
        }
        return (packages.DistinctBy(p => (p.Id, p.Source)).ToList(), skipped, parsed.HadTable);
    }
    public static bool IsIdentifier(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 300 &&
        !value.Contains('…') && !value.Contains("...") && !value.StartsWith('-') &&
        !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}

public interface IUpdateCommands
{
    Task<CommandResult> RunAsync(IEnumerable<string> args, Action<string>? progress = null, CancellationToken token = default, TimeSpan? timeout = null);
    Task<string> InstalledVersionAsync(PackageEntry p);
}

public sealed class Winget : IUpdateCommands
{
    public static string Locate()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias)) return alias;
        // Resolve only the current user's OS-registered Microsoft package, never a random PATH executable.
        using var packages = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        if (packages is not null)
        foreach (var name in packages.GetSubKeyNames().Where(n => n.StartsWith("Microsoft.DesktopAppInstaller_", StringComparison.Ordinal) && n.EndsWith("__8wekyb3d8bbwe", StringComparison.Ordinal)).OrderByDescending(n => n))
        {
            using var key = packages.OpenSubKey(name);
            string? root = key?.GetValue("PackageRootFolder") as string;
            if (TrustedPackageRoot(root, name) && File.Exists(Path.Combine(root!, "winget.exe"))) return Path.Combine(root!, "winget.exe");
        }
        throw new FileNotFoundException("winget이 준비되지 않았습니다. 앱 설치 관리자를 설치·복구한 뒤 다시 확인하세요.");
    }
    internal static bool TrustedPackageRoot(string? root, string packageName)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) return false;
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps", packageName);
        return Path.GetFullPath(root).TrimEnd('\\').Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
    public static List<string> UpgradeArguments(PackageEntry p, bool silent)
    {
        if (!WingetTable.IsIdentifier(p.Id) || !WingetTable.IsIdentifier(p.Source) || string.IsNullOrWhiteSpace(p.Available) || p.Available.Contains('…'))
            throw new InvalidOperationException("패키지 정보가 불완전합니다. 목록을 새로 조회하세요.");
        var args = new List<string> { "upgrade", "--id", p.Id, "--exact", "--source", p.Source,
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" };
        // The Microsoft Store source always serves its current release; pinning a version is only meaningful for manifest sources.
        if (!p.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase)) args.AddRange(["--version", p.Available]);
        if (silent) args.Add("--silent");
        return args;
    }
    public async Task<CommandResult> RunAsync(IEnumerable<string> args, Action<string>? progress = null, CancellationToken token = default, TimeSpan? timeout = null)
    {
        var info = new ProcessStartInfo(Locate()) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        var output = new StringBuilder();
        var errors = new StringBuilder();
        using var process = new Process { StartInfo = info };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (timeout is not null) limit.CancelAfter(timeout.Value);
        process.Start();
        async Task Read(StreamReader reader, StringBuilder target)
        {
            var buffer = new char[2048];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
            {
                var chunk = new string(buffer, 0, count);
                target.Append(chunk);
                progress?.Invoke(WingetTable.Clean(chunk));
            }
        }
        Task stdout = Read(process.StandardOutput, output), stderr = Read(process.StandardError, errors);
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            if (!token.IsCancellationRequested) throw new TimeoutException("winget 작업 시간이 초과되었습니다. 네트워크 상태와 로그를 확인하세요.");
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        return new(process.ExitCode, WingetTable.Clean(output.ToString()), WingetTable.Clean(errors.ToString()));
    }
    public async Task<ScanResult> ScanAsync(bool acceptSource, Action<string>? progress = null, CancellationToken token = default)
    {
        var args = new List<string> { "upgrade", "--disable-interactivity" };
        if (acceptSource) args.Add("--accept-source-agreements");
        var result = await RunAsync(args, progress, token, TimeSpan.FromMinutes(3));
        var parsed = WingetTable.Upgrades(result.Output);
        bool knownEmpty = result.ExitCode == unchecked((int)0x8A15002B) || (result.ExitCode == 0 && Regex.IsMatch(result.Output,
            @"No (available upgrade|installed package)|No applicable upgrade|사용 가능한 업그레이드.*(없|찾)|업그레이드.*(없습니다|찾을 수 없)", RegexOptions.IgnoreCase));
        if ((result.ExitCode != 0 || !parsed.HadTable) && !knownEmpty)
            throw new InvalidOperationException($"winget 목록을 읽지 못했습니다 (0x{result.ExitCode:X8}). 실행 로그에서 원본 약관 또는 네트워크 오류를 확인하세요.");
        return new(parsed.Packages, result, parsed.Skipped, parsed.HadTable);
    }
    public async Task<string> InstalledVersionAsync(PackageEntry p)
    {
        var r = await RunAsync(["list", "--id", p.Id, "--exact", "--source", p.Source, "--disable-interactivity"], timeout: TimeSpan.FromSeconds(60));
        if (r.ExitCode != 0) return "";
        return WingetTable.Parse(r.Output, 3).Rows.FirstOrDefault(c => c[1].Equals(p.Id, StringComparison.OrdinalIgnoreCase))?[2] ?? "";
    }
}
