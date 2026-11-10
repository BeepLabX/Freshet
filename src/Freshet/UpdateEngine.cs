using System.IO;
using System.Text;

namespace Freshet;

public sealed class UpdateEngine(Storage storage, IUpdateCommands commands, Func<Task>? verificationDelay = null)
{
    public async Task<HistoryEntry> ExecuteAsync(PackageEntry p, bool silent, Action<HistoryEntry>? started = null,
        Action<string>? progress = null, Action? verifying = null)
    {
        var entry = new HistoryEntry { Name = p.Name, Id = p.Id, Source = p.Source, Before = p.Installed, Target = p.Available };
        entry.LogFile = Path.Combine(storage.Logs, entry.RecordId + ".log");
        // If durable history cannot be written, never start an installer.
        storage.SaveEntry(entry);
        File.WriteAllText(entry.LogFile, $"Freshet · {entry.Started:O}\n{p.Id} [{p.Source}] {p.Installed} -> {p.Available}\n\n", Encoding.UTF8);
        started?.Invoke(entry);
        object logLock = new();
        try
        {
            // ponytail: fixed 60-minute ceiling; a per-package setting if large installers hit it.
            var result = await commands.RunAsync(Winget.UpgradeArguments(p, silent), timeout: TimeSpan.FromMinutes(60), progress: text => {
                lock (logLock) File.AppendAllText(entry.LogFile, text, Encoding.UTF8);
                progress?.Invoke(text);
            });
            entry.ExitCode = result.ExitCode;
            File.AppendAllText(entry.LogFile, $"\n[winget 종료 코드] 0x{result.ExitCode:X8}\n", Encoding.UTF8);
            if (result.ExitCode == 0 || result.ExitCode is 3010 or 1641)
            {
                verifying?.Invoke();
                bool restartApp = UpdateSignals.RequiresAppRestart(result.Output + "\n" + result.Error);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try { entry.After = await commands.InstalledVersionAsync(p); entry.Detail = ""; }
                    catch (Exception ex) { entry.Detail = "버전 재확인 실패: " + ex.Message; }
                    File.AppendAllText(entry.LogFile, $"[버전 확인 {attempt + 1}/3] 설치={entry.After}, 목표={entry.Target} {entry.Detail}\n", Encoding.UTF8);
                    if (TargetReached(entry.After, entry.Target) || restartApp || result.ExitCode is 3010 or 1641) break;
                    if (attempt < 2) await (verificationDelay?.Invoke() ?? Task.Delay(2000));
                }
                bool reached = TargetReached(entry.After, entry.Target);
                entry.Outcome = result.ExitCode is 3010 or 1641 ? "reboot" : reached ? "success" : restartApp ? "app-restart" : "unverified";
                if (entry.Detail.Length == 0) entry.Detail = entry.Outcome switch {
                    "success" => "목표 버전 이상이 설치된 것을 확인했습니다.",
                    "app-restart" => "실행 중인 앱 때문에 업데이트 적용이 대기 중입니다. 해당 앱을 완전히 종료한 뒤 다시 열고 목록을 새로고침하세요. 이전 버전이 계속 표시되면 앱을 닫은 상태에서 다시 업데이트하세요.",
                    "reboot" => "Windows를 다시 시작한 뒤 설치된 버전을 확인하세요.",
                    _ => "업데이트 적용을 확인하지 못했습니다. 목표 버전과 일치하지 않아 완료로 집계하지 않습니다. 실행 로그를 확인하세요."
                };
            }
            else
            {
                entry.Outcome = "failed";
                entry.Detail = $"winget 종료 코드 0x{result.ExitCode:X8}. 원본 로그를 확인하세요.";
            }
        }
        catch (Exception ex) { entry.Outcome = "failed"; entry.Detail = ex.Message; }
        entry.Finished = DateTimeOffset.Now;
        storage.SaveEntry(entry);
        File.AppendAllText(entry.LogFile, $"\n\n[{entry.OutcomeText}] {entry.Detail}\n확인된 버전: {entry.After}\n", Encoding.UTF8);
        return entry;
    }
    private static string Normalize(string s) => s.Trim().TrimStart('v', 'V');
    public static bool TargetReached(string after, string target)
    {
        if (!IsKnownVersion(after) || !IsKnownVersion(target)) return false;
        after = Normalize(after); target = Normalize(target);
        if (after.Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
        // Only plain numeric versions are ordered. Never guess prerelease/vendor version ordering.
        if (!System.Text.RegularExpressions.Regex.IsMatch(after, @"^\d+(\.\d+)*$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(target, @"^\d+(\.\d+)*$")) return false;
        var a = after.Split('.').Select(System.Numerics.BigInteger.Parse).ToArray();
        var t = target.Split('.').Select(System.Numerics.BigInteger.Parse).ToArray();
        for (int i = 0; i < Math.Max(a.Length, t.Length); i++)
        {
            int c = (i < a.Length ? a[i] : 0).CompareTo(i < t.Length ? t[i] : 0);
            if (c != 0) return c > 0;
        }
        return true;
    }
    private static bool IsKnownVersion(string value) => value.Length > 0 && !new[] { "unknown", "알 수 없음", "<", ">", "…" }
        .Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase));
}
