using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Freshet;

internal static class QueueTests
{
    public static void Run(MainWindow window, string root, Action<bool, string> check)
    {
        // Self-tests render without Application.Run; install the production UI context explicitly.
        var originalContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        var grid = (DataGrid)window.FindName("PackagesGrid");
        ((Button)window.FindName("UpdatesNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static PackageEntry Package(string id) => new() { Name = id == "Restart" ? "Sample App" : id, Id = id, Source = "winget", Installed = "1.0", Available = "2.0" };
        var good = Package("Success"); var restart = Package("Restart"); var bad = Package("Failure");
        good.Selected = restart.Selected = bad.Selected = true;
        var initial = new[] { good, restart, bad }.Concat(Enumerable.Range(1, 14).Select(i => Package("대기 앱 " + i))).ToList();
        window.LoadDiagnosticSnapshot(initial);
        var fake = new Commands();
        int scans = 0;
        var task = window.RunQueueAsync([good, restart, bad], fake, () => {
            scans++;
            return Task.FromResult(new ScanResult([Package("Restart"), Package("Failure")], new(0, "", ""), 0, true));
        });
        check(grid.IsEnabled && !good.CanSelect && !good.CanHold, "Active queue permits list scrolling but locks selection and hold actions");
        // The stage label refreshes on a 250 ms timer; poll instead of relying on one fixed delay.
        for (int i = 0; i < 30 && ((TextBlock)window.FindName("RunStage")).Text != "다운로드 중"; i++) Pump(Task.Delay(100));
        check(((TextBlock)window.FindName("RunStage")).Text == "다운로드 중", "Live queue displays parsed download stage");
        check(grid.RowHeight == 48 && grid.ActualHeight > 250, "Compact rows leave usable list height while progress is visible");
        SelfTests.Capture(window, Path.Combine(root, "Freshet-running.png"));
        fake.Gate.SetResult();
        Pump(task);
        check(scans == 1 && grid.Items.Count == 2 && !grid.Items.Cast<PackageEntry>().Any(p => p.Id == "Success"), "Automatic scan removes the installed package from the update list");
        check(grid.Items.Cast<PackageEntry>().All(p => !p.Selected), "Refreshed candidates require a new explicit selection");
        check(grid.Items.Cast<PackageEntry>().Single(p => p.Id == "Restart").Status == "앱 재시작 필요", "Pending activation remains in refreshed list with actionable status");
        check(grid.Items.Cast<PackageEntry>().Single(p => p.Id == "Failure").Status == "실패", "Failed package remains in refreshed list");
        check(((TextBlock)window.FindName("QueueLabel")).Text.Contains("완료 1개 · 앱 재시작 필요 1개") && ((TextBlock)window.FindName("RunStage")).Text.Contains("자동 새로고침 완료"), "Batch summary separates confirmed installs from pending activation");
        SelfTests.Capture(window, Path.Combine(root, "Freshet-results.png"));
        ((Button)window.FindName("RunDismiss")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(((FrameworkElement)window.FindName("RunPanel")).Visibility == Visibility.Collapsed, "Result summary can collapse to restore list space");

        var retry = Package("Failure");
        window.LoadDiagnosticSnapshot([retry]);
        Pump(window.RunQueueAsync([retry], new Commands(false), () => Task.FromException<ScanResult>(new IOException("simulated source failure"))));
        retry.Selected = true;
        check(grid.Items.Count == 1 && !((Button)window.FindName("UpgradeButton")).IsEnabled && ((TextBlock)window.FindName("RunStage")).Text.Contains("실패"), "Refresh failure retains old list and blocks resubmission until successful scan");

        good = Package("Success"); bad = Package("Failure");
        window.LoadDiagnosticSnapshot([good, bad]);
        fake = new Commands(); scans = 0;
        task = window.RunQueueAsync([good, bad], fake, () => { scans++; return Task.FromResult(new ScanResult([Package("Failure")], new(0, "", ""), 0, true)); });
        ((Button)window.FindName("StopButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        fake.Gate.SetResult(); Pump(task);
        check(fake.Calls == 1 && scans == 1 && ((TextBlock)window.FindName("QueueLabel")).Text.Contains("미실행 1개"), "Stop finishes current app, skips remainder, and still refreshes the list");
        SynchronizationContext.SetSynchronizationContext(originalContext);
    }
    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private sealed class Commands(bool pause = true) : IUpdateCommands
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async Task<CommandResult> RunAsync(IEnumerable<string> args, Action<string>? progress = null, CancellationToken token = default, TimeSpan? timeout = null)
        {
            var argv = args.ToList(); string id = argv[argv.IndexOf("--id") + 1];
            Calls++;
            progress?.Invoke("Downloading\r 12 MB / 24 MB");
            if (pause && Calls == 1) await Gate.Task;
            return id switch {
                "Restart" => new(0, "설치되었습니다. 업그레이드를 완료하려면 애플리케이션을 다시 시작하세요.", ""),
                "Failure" => new(-1, "", "simulated installer failure"),
                _ => new(0, "Successfully installed", "")
            };
        }
        public Task<string> InstalledVersionAsync(PackageEntry p) => Task.FromResult(p.Id == "Success" ? "2.0" : "1.0");
    }
}
