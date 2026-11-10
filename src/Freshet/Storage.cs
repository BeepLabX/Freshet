using System.IO;
using System.Text;
using System.Text.Json;

namespace Freshet;

public sealed class Storage
{
    public string Root { get; }
    public string Logs => Path.Combine(Root, "logs");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public Storage(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Freshet");
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Path.Combine(Root, "history"));
    }
    public Preferences ReadPreferences()
    {
        var path = Path.Combine(Root, "preferences.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? new() : new(); }
        catch (JsonException)
        {
            // Keep the damaged file for recovery, then fall back to the backup written by the previous save.
            File.Copy(path, path + ".corrupt", true);
            try { return File.Exists(path + ".bak") ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path + ".bak")) ?? new() : new(); }
            catch (JsonException) { return new(); }
        }
    }
    public void PruneScanLogs(int keep = 30)
    {
        // Names start with a sortable timestamp, so name order is time order.
        foreach (var old in new DirectoryInfo(Logs).EnumerateFiles("scan-*.log").OrderByDescending(f => f.Name).Skip(keep))
            try { old.Delete(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public void SavePreferences(Preferences p)
    {
        string path = Path.Combine(Root, "preferences.json");
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(p, Json), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
        else File.Move(temp, path);
    }
    public List<HistoryEntry> ReadHistory()
    {
        var result = new List<HistoryEntry>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "history"), "*.json"))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<HistoryEntry>(File.ReadAllText(file));
                if (entry is null) continue;
                if (entry.Outcome == "running")
                {
                    entry.Outcome = "interrupted";
                    entry.Detail = "이전 실행이 완료되기 전에 종료되었습니다. 설치 상태를 다시 조회하세요.";
                    SaveEntry(entry);
                }
                // Interpret legacy deferred installs for display only. Keep original history/log bytes intact.
                if (entry.Outcome == "unverified" && entry.ExitCode == 0 && Guid.TryParseExact(entry.RecordId, "N", out _))
                {
                    string log = Path.Combine(Logs, entry.RecordId + ".log");
                    try
                    {
                        if (File.Exists(log) && new FileInfo(log).Length <= 1_000_000 && UpdateSignals.RequiresAppRestart(File.ReadAllText(log)))
                        {
                            entry.Outcome = "app-restart";
                            entry.Detail += "\n로그 재판독: winget이 앱 재시작을 요청했습니다. 해당 앱을 완전히 종료하고 다시 연 뒤 목록을 새로고침하세요. 원본 이력은 변경하지 않았습니다.";
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                result.Add(entry);
            }
            catch (JsonException) { /* Preserve the original file for recovery. */ }
        }
        return result.OrderByDescending(x => x.Started).ToList();
    }
    public void SaveEntry(HistoryEntry h) => AtomicWrite(Path.Combine(Root, "history", h.RecordId + ".json"), JsonSerializer.Serialize(h, Json));
    public static void AtomicWrite(string path, string content)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        // Antivirus and indexers briefly lock freshly written files; retry before failing the save.
        for (int attempt = 1; ; attempt++)
        {
            try { File.Move(temp, path, true); return; }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(50 * attempt); }
        }
    }
    public static void ExportCsv(string path, IEnumerable<HistoryEntry> entries)
    {
        static string Quote(string value)
        {
            // Spreadsheet programs must not interpret package names as formulas.
            if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var lines = new List<string> { "시간,PC,프로그램,패키지 ID,원본,이전 버전,목표 버전,확인된 버전,결과,종료 코드,세부 정보" };
        lines.AddRange(entries.Select(h => string.Join(",", new[] {
            h.Started.ToString("O"), h.Computer, h.Name, h.Id, h.Source, h.Before, h.Target, h.After,
            h.OutcomeText, h.ExitCode?.ToString() ?? "", h.Detail }.Select(Quote))));
        File.WriteAllLines(path, lines, new UTF8Encoding(true));
    }
}
