using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Freshet;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] is "--diagnose" or "--metadata-diagnose")
            {
                var w = new Winget();
                var raw = w.RunAsync(["upgrade", "--disable-interactivity"], timeout: TimeSpan.FromMinutes(3)).GetAwaiter().GetResult();
                var parsed = WingetTable.Upgrades(raw.Output);
                var checkedVersion = parsed.Packages.Count > 0 ? w.InstalledVersionAsync(parsed.Packages[0]).GetAwaiter().GetResult() : "";
                if (args[0] == "--metadata-diagnose")
                {
                    using var service = new DownloadMetadata(w, Path.GetDirectoryName(Path.GetFullPath(args[1])));
                    using var slots = new SemaphoreSlim(3);
                    var sizes = Task.WhenAll(parsed.Packages.Select(async p => {
                        await slots.WaitAsync();
                        try { var estimate = await service.EstimateAsync(p); p.SizeNote = estimate.Note; p.EstimatedBytes = estimate.Bytes; p.SizeState = estimate.Bytes.HasValue ? "known" : "unavailable"; }
                        finally { slots.Release(); }
                    }));
                    var icons = new AppIcons().LoadAsync(parsed.Packages, CancellationToken.None);
                    Task.WhenAll(sizes, icons).GetAwaiter().GetResult();
                    foreach (var (p, icon) in icons.Result) { p.Icon = icon.Image; p.IconOrigin = icon.Origin; }
                    var diagnosticApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    var window = new MainWindow(new Storage(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "diagnostic-data")), true) { ShowActivated = false, ShowInTaskbar = false };
                    window.LoadDiagnosticSnapshot(parsed.Packages);
                    foreach (var p in parsed.Packages.Take(3)) p.Selected = true;
                    window.Show(); diagnosticApp.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    SelfTests.Capture(window, Path.ChangeExtension(args[1], ".png")); window.Close(); diagnosticApp.Shutdown();
                }
                File.WriteAllText(args[1], JsonSerializer.Serialize(new { raw.ExitCode, raw.Output, raw.Error, parsed.Packages, parsed.Skipped, parsed.HadTable, CheckedPackage = parsed.Packages.FirstOrDefault()?.Id, CheckedVersion = checkedVersion }, new JsonSerializerOptions { WriteIndented = true }));
                return raw.ExitCode;
            }
            if (args.Length >= 2 && args[0] == "--self-test") return SelfTests.Run(args[1]);
            using var single = new Mutex(true, @"Local\Freshet-" + Environment.UserName, out bool first);
            if (!first)
            {
                MessageBox.Show("Freshet이 이미 실행 중입니다. 작업 표시줄에서 기존 창을 확인하세요.", "Freshet");
                return 0;
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (_, e) => {
                MessageBox.Show("작업 중 오류가 발생했습니다.\n\n" + e.Exception.Message, "Freshet", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            return app.Run(new MainWindow(new Storage()));
        }
        catch (Exception ex)
        {
            if (args.Length >= 2)
            {
                File.WriteAllText(args[1] + ".error.txt", ex.ToString());
                return 1;
            }
            MessageBox.Show("Freshet을 시작하지 못했습니다.\n\n" + ex.Message, "Freshet", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}

public static class SelfTests
{
    public static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var results = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            results.Add("PASS: " + name);
        }
        static string Row(params string[] cells)
        {
            int[] widths = [51, 40, 20, 20, 12];
            return string.Concat(cells.Select((cell, i) => cell + new string(' ', Math.Max(2, widths[i] - WingetTable.DisplayWidth(cell))))).TrimEnd();
        }
        string fixture = Row("이름", "장치 ID", "버전", "사용 가능", "원본") + "\n" + new string('-', 143) + "\n" +
            Row("Linux용 Windows 하위 시스템", "Microsoft.WSL", "2.7.13.0", "2.7.14", "winget") + "\n" +
            Row("긴 이름 …", "Microsoft.DotNet.DesktopRuntime.10", "10.0.13", "10.0.14", "winget") + "\n" +
            Row("잘린 ID", "Microsoft.VeryLong…", "1.0", "2.0", "winget") + "\n" +
            Row("Store App", "9ABCDEF12345", "1.0", "2.0", "msstore") + "\n4 업그레이드를 사용할 수 있습니다.\n";
        var parsed = WingetTable.Upgrades("\u001b[32m" + fixture + "\u001b[0m");
        Check(parsed.HadTable && parsed.Packages.Count == 3 && parsed.Skipped == 1, "Korean table, ANSI sequences, truncated ID rejected");
        Check(parsed.Packages[0].Id == "Microsoft.WSL" && parsed.Packages[0].Installed == "2.7.13.0", "CJK display width aligns the version columns");
        Check(parsed.Packages[1].Id == "Microsoft.DotNet.DesktopRuntime.10", "Long package IDs remain intact");
        Check(parsed.Packages[2].Source == "msstore", "Source is preserved");
        string english = Row("Name", "Id", "Version", "Available", "Source") + "\n" + new string('-', 143) + "\n" + Row("Git", "Git.Git", "2.0", "3.0", "winget");
        Check(WingetTable.Upgrades(english).Packages.Single().Available == "3.0", "English table headers");
        var compactKorean = WingetTable.Parse("이름 장치 ID 버전     사용 가능\n-------------------------------\nGit  Git.Git 2.55.0.5 2.56.0.3\n", 3);
        Check(compactKorean.Rows.Single()[1] == "Git.Git" && compactKorean.Rows.Single()[2] == "2.55.0.5", "Compact Korean installed-version table with single-space headers");
        var compactEnglish = WingetTable.Parse("Name Id      Version\n--------------------\nGit  Git.Git 2.55.0.5\n", 3);
        Check(compactEnglish.Rows.Single()[2] == "2.55.0.5", "Compact English installed-version table");
        Check(!WingetTable.Upgrades("Network error: 0x80072EE7").HadTable, "Errors are not interpreted as an empty update list");
        Check(!WingetTable.IsIdentifier("--all") && !WingetTable.IsIdentifier("Git.Git\n--all"), "Malformed package identifiers are rejected");
        var args = Winget.UpgradeArguments(parsed.Packages[0], true);
        Check(args.Contains("--exact") && args[args.IndexOf("--id") + 1] == "Microsoft.WSL" && args[args.IndexOf("--version") + 1] == "2.7.14" && args.Contains("--silent") && !args.Contains("--all") && !args.Contains("--allow-reboot"), "Exact ID, source and target version; no all/reboot switches");
        Check(!Winget.UpgradeArguments(parsed.Packages[0], false).Contains("--silent"), "Installer UI preference");
        Check(!Winget.UpgradeArguments(parsed.Packages[2], true).Contains("--version"), "Store packages are not pinned to a version");
        var data = new Storage(Path.Combine(root, "data"));
        var p = new Preferences { SilentInstall = false, ExcludedIds = ["winget/Git.Git"] };
        data.SavePreferences(p);
        Check(!data.ReadPreferences().SilentInstall && data.ReadPreferences().ExcludedIds.Count == 1, "Preferences and held packages round trip");
        data.SavePreferences(p); // The second save leaves preferences.json.bak.
        File.WriteAllText(Path.Combine(data.Root, "preferences.json"), "{ broken");
        Check(!data.ReadPreferences().SilentInstall && File.Exists(Path.Combine(data.Root, "preferences.json.corrupt")), "Damaged preferences fall back to the backup and keep the damaged copy");
        for (int i = 0; i < 35; i++) File.WriteAllText(Path.Combine(data.Logs, $"scan-20260101-0000{i:00}-x.log"), "");
        data.PruneScanLogs();
        Check(Directory.GetFiles(data.Logs, "scan-*.log").Length == 30 && File.Exists(Path.Combine(data.Logs, "scan-20260101-000034-x.log")), "Old scan logs are pruned, newest kept");
        var entry = new HistoryEntry { Name = "=test", Before = "1", Target = "2", Outcome = "running" };
        data.SaveEntry(entry);
        Check(data.ReadHistory().Single().Outcome == "interrupted", "Unfinished records recover as interrupted");
        entry.Outcome = "success"; entry.After = "2"; data.SaveEntry(entry);
        Storage.ExportCsv(Path.Combine(root, "test.csv"), data.ReadHistory());
        Check(File.ReadAllText(Path.Combine(root, "test.csv")).Contains("'=test"), "CSV formula injection protection");
        var fake = new TestCommands { After = "2.7.14" };
        var engine = new UpdateEngine(new Storage(Path.Combine(root, "engine-data")), fake, () => Task.CompletedTask);
        var completed = engine.ExecuteAsync(parsed.Packages[0], true).GetAwaiter().GetResult();
        Check(completed.Outcome == "success" && completed.Before == "2.7.13.0" && completed.After == "2.7.14" && completed.Finished is not null,
            "Simulated installer: changed version is recorded as success");
        Check(File.ReadAllText(completed.LogFile).Contains("simulated installer"), "Installer output persists in the per-update log");
        fake.After = "2.7.13.0";
        Check(engine.ExecuteAsync(parsed.Packages[0], true).GetAwaiter().GetResult().Outcome == "unverified", "Exit zero with unchanged version needs verification");
        fake.After = "";
        Check(engine.ExecuteAsync(parsed.Packages[0], true).GetAwaiter().GetResult().Outcome == "unverified", "Missing installed version is not counted as success");
        fake.After = "2.7.13.1";
        Check(engine.ExecuteAsync(parsed.Packages[0], true).GetAwaiter().GetResult().Outcome == "unverified", "Changed version below target is not success");
        fake.After = "2.7.13.0";
        fake.Output = "설치되었습니다. 업그레이드를 완료하려면 애플리케이션을 다시 시작하세요.";
        var deferred = engine.ExecuteAsync(parsed.Packages[0], true).GetAwaiter().GetResult();
        Check(deferred.Outcome == "app-restart" && !deferred.OutcomeText.Contains("완료"), "Deferred MSIX activation is not reported as complete");
        fake.Output = "Successfully installed. Restart the application to complete the upgrade.";
        Check(engine.ExecuteAsync(parsed.Packages[0], true).GetAwaiter().GetResult().Outcome == "app-restart", "English deferred application restart signal");
        fake.After = "2.7.14";
        Check(engine.ExecuteAsync(parsed.Packages[0], true).GetAwaiter().GetResult().Outcome == "success", "Actual target version confirmation takes precedence over restart advice");
        fake.Output = "simulated installer output";
        fake.Versions = new Queue<string>(["2.7.13.0", "2.7.13.0", "2.7.14"]);
        Check(engine.ExecuteAsync(parsed.Packages[0], true).GetAwaiter().GetResult().Outcome == "success" && fake.Versions.Count == 0, "Version registration delay is retried before result classification");
        Check(UpdateEngine.TargetReached("v2.7.14.0", "2.7.14") && UpdateEngine.TargetReached("2.7.15", "2.7.14") && !UpdateEngine.TargetReached("unknown", "2.7.14") && !UpdateEngine.TargetReached("2.7.14-beta", "2.7.14"), "Conservative target comparison supports padding and newer numeric versions");
        Check(!new HistoryEntry { Outcome = "unverified" }.OutcomeText.Contains("완료"), "Legacy unverified history no longer claims completion");
        var legacyStore = new Storage(Path.Combine(root, "legacy-history"));
        var legacy = new HistoryEntry { Outcome = "unverified", ExitCode = 0 };
        legacyStore.SaveEntry(legacy);
        string legacyPath = Path.Combine(legacyStore.Root, "history", legacy.RecordId + ".json");
        var originalBytes = File.ReadAllBytes(legacyPath);
        File.WriteAllText(Path.Combine(legacyStore.Logs, legacy.RecordId + ".log"), "설치되었습니다. 업그레이드를 완료하려면 애플리케이션을 다시 시작하세요.");
        Check(legacyStore.ReadHistory().Single().Outcome == "app-restart" && File.ReadAllBytes(legacyPath).SequenceEqual(originalBytes), "Legacy deferred-activation history is reinterpreted for display without rewriting original records");
        var progress = new UpdateProgress();
        progress.Feed("Downloading\r 5.0 Mi"); progress.Feed("B / 10.0 MiB");
        Check(progress.Snapshot().Phase == "다운로드 중" && progress.Snapshot().Detail.Contains("5.2 MB / 10.5 MB"), "Split carriage-return download output converts binary units to decimal MB");
        progress.Feed("\nStarting package install..."); progress.Feed("\r10 MB / 10 MB");
        Check(progress.Snapshot().Phase == "설치 중", "Late transfer output cannot regress the installation stage");
        progress.Verify();
        Check(progress.Snapshot().Phase == "설치 버전 확인", "Verification is a distinct progress stage");
        fake.Exit = unchecked((int)0x8A15002B);
        var failed = engine.ExecuteAsync(parsed.Packages[0], true).GetAwaiter().GetResult();
        Check(failed.Outcome == "failed" && failed.ExitCode == unchecked((int)0x8A15002B), "Installer failure preserves HRESULT");
        fake.Exit = 3010;
        Check(engine.ExecuteAsync(parsed.Packages[0], true).GetAwaiter().GetResult().Outcome == "reboot", "Reboot-needed result is recorded separately");
        fake.Throw = true;
        Check(engine.ExecuteAsync(parsed.Packages[0], true).GetAwaiter().GetResult().Outcome == "failed", "Process launch errors produce a finished failure record");
        MetadataTests.Run(root, Check);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow(new Storage(Path.Combine(root, "preview-data")), true) { ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var grid = (DataGrid)window.FindName("PackagesGrid");
        Check(grid.Items.Count == 5, "WPF updates screen loads");
        checkSizeSummary();
        void checkSizeSummary()
        {
            var summary = (TextBlock)window.FindName("SizeSummaryLabel");
            Check(summary.Text.Contains("83.6 MB"), "WPF shows the selected size total");
            var github = (PackageEntry)grid.Items[1];
            github.EstimatedBytes = null; github.SizeState = "unavailable";
            Check(summary.Text.Contains("66.8 MB") && summary.Text.Contains("미확인 1개"), "WPF total updates when metadata becomes unavailable");
            github.EstimatedBytes = 16_800_000; github.SizeState = "known";
        }
        var search = (TextBox)window.FindName("SearchBox");
        search.Text = "GitHub";
        Check(grid.Items.Count == 1, "Live search filters packages");
        ((CheckBox)window.FindName("HeaderSelectAll")).RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Check(!((PackageEntry)grid.Items[0]).Selected, "Selection toggle applies to filtered results");
        search.Text = "";
        var held = (PackageEntry)grid.Items[0];
        held.Excluded = true;
        Check(!held.Selected && !held.CanSelect, "Held packages cannot remain selected");
        Check(ReferenceEquals(grid.Items[grid.Items.Count - 1], held), "Held packages move to the bottom");
        held.Excluded = false;
        Check(!ReferenceEquals(grid.Items[grid.Items.Count - 1], held), "Released packages return to the active group");
        ((PackageEntry)grid.Items[0]).Selected = true;
        ((PackageEntry)grid.Items[1]).Selected = true;
        app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Capture(window, Path.Combine(root, "Freshet-updates.png"));
        ((Button)window.FindName("HistoryNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(((FrameworkElement)window.FindName("HistoryPage")).Visibility == Visibility.Visible, "History navigation");
        Capture(window, Path.Combine(root, "Freshet-history.png"));
        ((Button)window.FindName("SettingsNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(((FrameworkElement)window.FindName("SettingsPage")).Visibility == Visibility.Visible, "Settings navigation");
        Capture(window, Path.Combine(root, "Freshet-settings.png"));
        ((Button)window.FindName("UpdatesNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.ApplyTheme(true);
        Check(Theme.IsDark && Theme.Brush("Surface").Color.R < 0x40, "Dark theme recolors the shared brushes");
        Capture(window, Path.Combine(root, "Freshet-updates-dark.png"));
        window.ApplyTheme(false);
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        Capture(window, Path.Combine(root, "Freshet-min-size.png"));
        window.Width = 1330; window.Height = 880;
        ((Button)window.FindName("SettingsNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var review = new ReviewWindow(parsed.Packages) { ShowActivated = false, ShowInTaskbar = false };
        review.Show();
        Capture(review, Path.Combine(root, "Freshet-review.png"));
        review.Close();
        PackageManagementTests.Run(window, root, Check);
        QueueTests.Run(window, root, Check);
        window.Close();
        app.Shutdown();
        File.WriteAllLines(Path.Combine(root, "results.txt"), results);
        return 0;
    }
    public static void Capture(Window window, string path)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(content);
        // A Window paints its background outside Content's visual subtree.
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var rect = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
            drawing.DrawRectangle(window.Background, null, rect);
            drawing.DrawImage(image, rect);
        }
        var composed = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        composed.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(composed));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private sealed class TestCommands : IUpdateCommands
    {
        public int Exit { get; set; }
        public string After { get; set; } = "";
        public bool Throw { get; set; }
        public string Output { get; set; } = "simulated installer output";
        public Queue<string> Versions { get; set; } = new();
        public Task<CommandResult> RunAsync(IEnumerable<string> args, Action<string>? progress = null, CancellationToken token = default, TimeSpan? timeout = null)
        {
            if (Throw) throw new IOException("simulated process launch error");
            progress?.Invoke("simulated installer output\n");
            return Task.FromResult(new CommandResult(Exit, Output, ""));
        }
        public Task<string> InstalledVersionAsync(PackageEntry p) => Task.FromResult(Versions.Count > 0 ? Versions.Dequeue() : After);
    }
}
