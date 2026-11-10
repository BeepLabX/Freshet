using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Freshet;

public partial class MainWindow : Window
{
    private readonly Storage storage;
    private readonly Winget winget = new();
    private readonly Preferences preferences;
    private readonly ObservableCollection<PackageEntry> packages = [];
    private readonly ObservableCollection<HistoryEntry> history = [];
    private readonly ObservableCollection<HiddenPackage> hiddenPackages = [];
    private bool ready, busy, upgrading, stopAfterCurrent, scanFailed;
    private readonly CancellationTokenSource lifetime = new();
    private readonly bool preview;
    private readonly DownloadMetadata metadata;
    private CancellationTokenSource? metadataLifetime;

    public MainWindow(Storage data, bool preview = false)
    {
        this.preview = preview;
        storage = data;
        metadata = new DownloadMetadata(winget, storage.Root);
        preferences = storage.ReadPreferences();
        Theme.Apply();
        InitializeComponent();
        if (Application.Current is not null)
            foreach (System.Collections.DictionaryEntry resource in Resources) Application.Current.Resources[resource.Key] = resource.Value;
        Motion.Initialize(); Motion.Reduced = preferences.ReduceMotion;
        if (!preview)
        {
            Width = Math.Min(Width, SystemParameters.WorkArea.Width - 24);
            Height = Math.Min(Height, SystemParameters.WorkArea.Height - 24);
            MinWidth = Math.Min(MinWidth, Width);
            MinHeight = Math.Min(MinHeight, Height);
        }
        string version = typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
        VersionLabel.Text = $"Freshet  /  {version}";
        AboutVersion.Text = $"Freshet {version}";
        ComputerLabel.Text = Environment.MachineName;
        StorageLabel.Text = storage.Root;
        PackagesGrid.ItemsSource = packages;
        var packageView = CollectionViewSource.GetDefaultView(packages);
        packageView.SortDescriptions.Add(new SortDescription(nameof(PackageEntry.Excluded), ListSortDirection.Ascending));
        packageView.SortDescriptions.Add(new SortDescription(nameof(PackageEntry.ListOrder), ListSortDirection.Ascending));
        packageView.Filter = value => value is PackageEntry p && !p.Hidden &&
            (p.Name.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase) || p.Id.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase));
        HistoryGrid.ItemsSource = history;
        HiddenGrid.ItemsSource = hiddenPackages;
        foreach (var h in preferences.HiddenPackages) hiddenPackages.Add(h);
        foreach (var h in storage.ReadHistory()) history.Add(h);
        ScanOnLaunch.IsChecked = preferences.ScanOnLaunch;
        SilentInstall.IsChecked = preferences.SilentInstall;
        ReduceMotion.IsChecked = preferences.ReduceMotion;
        LastScanLabel.Text = preferences.LastScan;
        ready = true;
        SetPage(0);
        RefreshCounters();
        if (preview) LoadPreview();
        else Loaded += async (_, _) => { if (preferences.ScanOnLaunch) await ScanAsync(); };
        Closed += (_, _) => { lifetime.Cancel(); metadata.Dispose(); };
    }
    private void LoadPreview()
    {
        foreach (var p in new[] {
            new PackageEntry { Name="Git",Id="Git.Git",Installed="2.55.0.5",Available="2.56.0.3",Source="winget",Selected=true },
            new PackageEntry { Name="GitHub CLI",Id="GitHub.cli",Installed="2.103.0",Available="2.103.3",Source="winget",Selected=true },
            new PackageEntry { Name="Linux용 Windows 하위 시스템",Id="Microsoft.WSL",Installed="2.7.13.0",Available="2.7.14",Source="winget" },
            new PackageEntry { Name="Microsoft Edge",Id="Microsoft.Edge",Installed="155.0.4283.62",Available="156.0.4314.42",Source="winget" },
            new PackageEntry { Name="Node.js",Id="OpenJS.NodeJS.LTS",Installed="24.21.0",Available="24.22.1",Source="winget" } }) AddPackage(p);
        packages[0].EstimatedBytes = 66_837_216; packages[0].SizeState = "known";
        packages[1].EstimatedBytes = 16_800_000; packages[1].SizeState = "known";
        packages[2].EstimatedBytes = 1_250_000_000; packages[2].SizeState = "known";
        packages[3].SizeState = "unavailable";
        packages[4].EstimatedBytes = 32_400_000; packages[4].SizeState = "known";
        LastScanLabel.Text = "미리보기 · 예시 데이터";
        MetadataLabel.Text = "설치 파일 예상 용량 · 추가 다운로드·의존 패키지는 별도입니다.";
        StatusLabel.Text = "미리보기 모드 · 실제 업데이트는 실행되지 않습니다";
        RefreshCounters();
    }
    private int page;
    private void SetPage(int page)
    {
        this.page = page;
        UpdatesPage.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        HiddenPage.Visibility = page == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch { 1 => "업데이트 이력", 2 => "설정", 3 => "숨김 목록", _ => "앱 업데이트" };
        Button[] nav = [UpdatesNav, HistoryNav, SettingsNav, HiddenNav];
        for (int i = 0; i < nav.Length; i++)
        {
            nav[i].Background = i == page ? Theme.Brush("AccentSoft") : Brushes.Transparent;
            nav[i].Foreground = Theme.Brush(i == page ? "Accent" : "Muted");
            nav[i].FontWeight = i == page ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (!preview) Motion.Enter(page switch { 1 => HistoryPage, 2 => SettingsPage, 3 => HiddenPage, _ => UpdatesPage });
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowHook);
        StateChanged += (_, _) => {
            // A chromeless maximized window extends past the monitor by its resize frame.
            var frame = SystemParameters.WindowResizeBorderThickness;
            RootGrid.Margin = WindowState == WindowState.Maximized ? frame : new Thickness(0);
            MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
        };
    }
    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x1A, WM_NCHITTEST = 0x84, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2, HTMAXBUTTON = 9;
        if (msg == WM_SETTINGCHANGE) ApplyTheme();
        // Reporting the maximize button to Windows enables the Windows 11 Snap Layouts flyout on hover.
        else if (msg == WM_NCHITTEST && OverMaximizeButton(lParam)) { handled = true; return HTMAXBUTTON; }
        else if (msg == WM_NCLBUTTONDOWN && wParam == HTMAXBUTTON) handled = true;
        else if (msg == WM_NCLBUTTONUP && wParam == HTMAXBUTTON) { handled = true; Maximize_Click(this, new RoutedEventArgs()); }
        return IntPtr.Zero;
    }
    internal void ApplyTheme(bool? dark = null)
    {
        Theme.Override = dark;
        Theme.Apply();
        SetPage(page);
        foreach (var p in packages) p.ThemeChanged();
    }
    private bool OverMaximizeButton(IntPtr lParam)
    {
        long value = lParam.ToInt64();
        var screen = new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
        if (!MaximizeButton.IsVisible) return false;
        var local = MaximizeButton.PointFromScreen(screen);
        return local.X >= 0 && local.Y >= 0 && local.X < MaximizeButton.ActualWidth && local.Y < MaximizeButton.ActualHeight;
    }
    private void Updates_Click(object sender, RoutedEventArgs e) => SetPage(0);
    private void History_Click(object sender, RoutedEventArgs e) => SetPage(1);
    private void Hidden_Click(object sender, RoutedEventArgs e) => SetPage(3);
    private void Settings_Click(object sender, RoutedEventArgs e) => SetPage(2);
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void InstallWinget_Click(object sender, RoutedEventArgs e) => OpenPath("ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1");
    private void WingetHelp_Click(object sender, RoutedEventArgs e) => OpenPath("https://learn.microsoft.com/windows/package-manager/winget/");
    private void FontsLicense_Click(object sender, RoutedEventArgs e)
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("Freshet.Assets.Fonts.OFL.txt");
        using var reader = new StreamReader(stream!);
        var license = new Window { Owner = this, Title = "Pretendard · SIL Open Font License", Background = Theme.Brush("Bg"), Width = 680, Height = 540, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = FontFamily, Content = new TextBox { Text = reader.ReadToEnd(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20) } };
        Theme.TitleBar(license);
        license.ShowDialog();
    }
    private void AddPackage(PackageEntry p)
    {
        p.Hidden = preferences.HiddenPackages.Any(h => h.Matches(p));
        p.Excluded = preferences.ExcludedIds.Contains(p.Source + "/" + p.Id, StringComparer.OrdinalIgnoreCase);
        if (p.Excluded) p.SetStatus("보류됨");
        p.ListOrder = packages.Count;
        p.PropertyChanged += Package_Changed;
        packages.Add(p);
    }
    private void Package_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PackageEntry.Excluded) or nameof(PackageEntry.Hidden)) CollectionViewSource.GetDefaultView(packages).Refresh();
        if (e.PropertyName is nameof(PackageEntry.Selected) or nameof(PackageEntry.EstimatedBytes) or nameof(PackageEntry.SizeState) or nameof(PackageEntry.Excluded) or nameof(PackageEntry.Hidden)) RefreshCounters();
    }
    private void PackagesGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        string property = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(property)) return;
        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        foreach (var column in PackagesGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = direction;
        var view = CollectionViewSource.GetDefaultView(packages);
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(nameof(PackageEntry.Excluded), ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(property, direction));
            view.SortDescriptions.Add(new SortDescription(nameof(PackageEntry.ListOrder), ListSortDirection.Ascending));
        }
    }
    private void RefreshCounters()
    {
        if (!ready) return;
        int selected = packages.Count(p => p.Selected && !p.Excluded && !p.Hidden);
        AvailableCount.Text = scanFailed ? "—" : packages.Count(p => !p.Hidden).ToString();
        CompletedCount.Text = history.Count(h => h.Outcome == "success").ToString();
        SelectedLabel.Text = $"{selected}개 선택됨";
        SizeSummaryLabel.Text = DownloadSizes.Summary(packages.Where(p => !p.Hidden));
        UpgradeButton.IsEnabled = !busy && !scanFailed && selected > 0;
        RefreshButton.IsEnabled = SearchBox.IsEnabled = !busy;
        SettingsNav.IsEnabled = HiddenGrid.IsEnabled = RestoreAllButton.IsEnabled = !busy;
        foreach (var p in packages) p.Locked = busy;
        var selectable = CollectionViewSource.GetDefaultView(packages).Cast<PackageEntry>().Where(p => p.CanSelect).ToList();
        HeaderSelectAll.IsEnabled = !busy && selectable.Count > 0;
        HeaderSelectAll.IsChecked = selectable.Count == 0 || selectable.All(p => !p.Selected) ? false : selectable.All(p => p.Selected) ? true : null;
        HiddenEmpty.Visibility = hiddenPackages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = upgrading ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmpty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool hasVisible = CollectionViewSource.GetDefaultView(packages).Cast<object>().Any();
        EmptyState.Visibility = hasVisible ? Visibility.Collapsed : Visibility.Visible;
        if (!hasVisible && !busy && !scanFailed) { EmptyTitle.Text = SearchBox.Text.Length > 0 ? "검색 결과 없음" : "표시할 업데이트 없음"; EmptyDetail.Text = SearchBox.Text.Length > 0 ? "이름 또는 패키지 ID를 확인하세요." : "숨긴 앱은 숨김 목록에서 확인할 수 있습니다."; }
    }
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!ready) return;
        CollectionViewSource.GetDefaultView(packages).Refresh();
        RefreshCounters();
    }
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var visible = CollectionViewSource.GetDefaultView(packages).Cast<PackageEntry>().Where(p => p.CanSelect).ToList();
        bool select = visible.Any(p => !p.Selected);
        foreach (var p in visible) p.Selected = select;
        RefreshCounters();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ScanAsync();
    private void AppendLog(string text)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => {
            LogBox.AppendText(text);
            if (LogBox.Text.Length > 120_000) LogBox.Text = LogBox.Text[^90_000..];
            LogBox.ScrollToEnd();
        });
    }
    internal async Task ScanAsync(bool acceptSource = false, Func<Task<ScanResult>>? scanOverride = null)
    {
        if (busy || (preview && scanOverride is null)) return;
        metadataLifetime?.Cancel();
        busy = true;
        scanFailed = false;
        StatusLabel.Text = "winget에서 업데이트 목록을 조회하고 있습니다…";
        EmptyTitle.Text = "업데이트를 확인하고 있습니다";
        EmptyDetail.Text = "원본과 네트워크 상태에 따라 잠시 걸릴 수 있습니다.";
        RefreshCounters();
        string file = Path.Combine(storage.Logs, $"scan-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        try
        {
            storage.PruneScanLogs();
            File.WriteAllText(file, $"Freshet scan · {DateTimeOffset.Now:O}\n", Encoding.UTF8);
            object logLock = new();
            var result = scanOverride is not null ? await scanOverride() : await winget.ScanAsync(acceptSource, text => {
                lock (logLock) File.AppendAllText(file, text, Encoding.UTF8);
                AppendLog(text);
            }, lifetime.Token);
            foreach (var p in packages) p.PropertyChanged -= Package_Changed;
            packages.Clear();
            foreach (var p in result.Packages)
            {
                p.Excluded = preferences.ExcludedIds.Contains(p.Source + "/" + p.Id, StringComparer.OrdinalIgnoreCase);
                if (p.Excluded) p.SetStatus("보류됨");
                else
                {
                    var last = history.FirstOrDefault(h => h.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase) &&
                        h.Source.Equals(p.Source, StringComparison.OrdinalIgnoreCase));
                    if (last is not null && last.Before == p.Installed && last.Target == p.Available && last.Outcome != "success") p.SetOutcome(last);
                }
                AddPackage(p);
            }
            preferences.LastScan = $"마지막 조회  {DateTime.Now:MM.dd HH:mm}";
            storage.SavePreferences(preferences);
            LastScanLabel.Text = preferences.LastScan;
            WingetHelp.Visibility = Visibility.Collapsed;
            StatusLabel.Text = result.Command.ExitCode == 0 || packages.Count == 0 ? $"조회 완료 · {packages.Count(p => !p.Hidden)}개 앱 · {DateTime.Now:HH:mm}" :
                $"일부 원본 조회에 문제가 있습니다 (0x{result.Command.ExitCode:X8}). 실행 로그를 확인하세요.";
            if (result.Skipped > 0) StatusLabel.Text += $" · 정보가 잘린 {result.Skipped}개 항목은 안전하게 제외했습니다.";
            EmptyTitle.Text = "표시할 업데이트 없음";
            EmptyDetail.Text = "현재 winget에서 표시하는 업데이트가 없습니다.";
            if (!preview) _ = LoadMetadataAsync(packages.Where(p => !p.Hidden).ToList());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            scanFailed = true;
            WingetHelp.Visibility = Visibility.Visible;
            StatusLabel.Text = ex.Message;
            EmptyTitle.Text = "목록을 불러오지 못했습니다";
            EmptyDetail.Text = "실행 로그와 설정의 원본 약관 항목을 확인하세요.";
            AppendLog("\n" + ex.Message + "\n");
        }
        finally { busy = false; RefreshCounters(); }
    }
    private async Task LoadMetadataAsync(List<PackageEntry> snapshot)
    {
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        metadataLifetime = batch;
        MetadataLabel.Text = snapshot.Count == 0 ? "설치 파일 예상 용량 · 추가 다운로드·의존 패키지는 별도입니다." : "앱 아이콘과 예상 용량을 조회하고 있습니다…";
        int done = 0;
        using var slots = new SemaphoreSlim(3);
        async Task Sizes()
        {
            await Task.WhenAll(snapshot.Select(async p => {
                await slots.WaitAsync(batch.Token);
                try
                {
                    var estimate = await metadata.EstimateAsync(p, batch.Token);
                    if (batch.IsCancellationRequested || !ReferenceEquals(metadataLifetime, batch)) return;
                    p.SizeNote = estimate.Note;
                    p.EstimatedBytes = estimate.Bytes;
                    p.SizeState = estimate.Bytes.HasValue ? "known" : "unavailable";
                    done++;
                    MetadataLabel.Text = $"예상 용량 확인 {done}/{snapshot.Count} · 추가 다운로드·의존 패키지는 별도입니다.";
                }
                catch (OperationCanceledException) when (batch.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    if (!batch.IsCancellationRequested && ReferenceEquals(metadataLifetime, batch))
                    { p.SizeNote = "용량 조회 실패: " + ex.Message; p.SizeState = "unavailable"; done++; }
                }
                finally { slots.Release(); }
            }));
        }
        async Task Icons()
        {
            try
            {
                var icons = await new AppIcons().LoadAsync(snapshot, batch.Token);
                if (batch.IsCancellationRequested || !ReferenceEquals(metadataLifetime, batch)) return;
                foreach (var (p, icon) in icons) { p.IconOrigin = icon.Origin; p.Icon = icon.Image; }
            }
            catch (OperationCanceledException) when (batch.IsCancellationRequested) { }
            catch (Exception ex) { AppendLog("\n아이콘 조회: " + ex.Message + "\n"); }
        }
        try { await Task.WhenAll(Sizes(), Icons()); }
        catch (OperationCanceledException) when (batch.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(metadataLifetime, batch))
            {
                metadataLifetime = null;
                MetadataLabel.Text = batch.IsCancellationRequested ? "용량 조회를 중지했습니다. 새로고침하면 다시 조회합니다." :
                    $"설치 파일 용량 {snapshot.Count(p => p.EstimatedBytes.HasValue)}/{snapshot.Count}개 확인 · 추가 다운로드·의존 패키지는 별도입니다.";
            }
        }
    }
    internal void LoadDiagnosticSnapshot(IReadOnlyList<PackageEntry> snapshot)
    {
        if (!preview) throw new InvalidOperationException("읽기 전용 미리보기에서만 사용할 수 있습니다.");
        foreach (var p in packages) p.PropertyChanged -= Package_Changed;
        packages.Clear();
        foreach (var p in snapshot) AddPackage(p);
        LastScanLabel.Text = "실제 PC · 읽기 전용 검증";
        MetadataLabel.Text = $"설치 파일 용량 {snapshot.Count(p => p.EstimatedBytes.HasValue)}/{snapshot.Count}개 확인 · 추가 다운로드·의존 패키지는 별도입니다.";
        StatusLabel.Text = "실제 winget 조회 결과 · 업데이트는 실행되지 않았습니다";
        RefreshCounters();
    }
    private async void Upgrade_Click(object sender, RoutedEventArgs e)
    {
        var selected = packages.Where(p => p.Selected && !p.Excluded && !p.Hidden).ToList();
        if (busy || scanFailed || selected.Count == 0 || preview) return;
        var review = new ReviewWindow(selected) { Owner = this };
        if (review.ShowDialog() != true) return;
        await RunQueueAsync(selected);
    }
    internal async Task RunQueueAsync(List<PackageEntry> selected, IUpdateCommands? commands = null, Func<Task<ScanResult>>? scanOverride = null)
    {
        // Preview/test calls must explicitly supply both mock boundaries.
        if (busy || selected.Count == 0 || (preview && (commands is null || scanOverride is null))) return;
        metadataLifetime?.Cancel();
        foreach (var p in packages.Where(p => p.SizeState == "pending")) { p.SizeState = "unavailable"; p.SizeNote = "업데이트를 시작하여 용량 조회를 중지했습니다. 새로고침하면 다시 조회합니다."; }
        busy = upgrading = true;
        stopAfterCurrent = false;
        StopButton.IsEnabled = true;
        RunPanel.Visibility = Visibility.Visible;
        if (!preview) Motion.Enter(RunPanel);
        RunDismiss.Visibility = Visibility.Collapsed;
        QueueProgress.Maximum = selected.Count;
        QueueProgress.Value = 0;
        foreach (var item in selected) item.SetStatus("대기 중", "AccentText");
        RefreshCounters();
        int done = 0, succeeded = 0, needsCheck = 0, failed = 0, restart = 0;
        string? queueError = null;
        var elapsed = Stopwatch.StartNew();
        UpdateProgress? signal = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        void RenderProgress()
        {
            RunElapsed.Text = $"{(int)elapsed.Elapsed.TotalMinutes:00}:{elapsed.Elapsed.Seconds:00} 경과";
            if (signal is null) return;
            var state = signal.Snapshot();
            RunStage.Text = state.Phase;
            RunDetail.Text = state.Detail;
            if (elapsed.Elapsed.TotalSeconds >= 45 && state.Phase == "설치 중")
                RunDetail.Text += " 오래 걸리면 작업 표시줄의 설치 창이나 앱 종료 요청을 확인하세요.";
        }
        timer.Tick += (_, _) => RenderProgress();
        timer.Start();
        try
        {
            foreach (var p in selected)
            {
                if (stopAfterCurrent) break;
                p.SetStatus("업데이트 중…", "AccentText");
                signal = new UpdateProgress();
                elapsed.Restart();
                RunIcon.Source = p.Icon;
                RunTitle.Text = $"{p.Name}  ·  {done + 1}/{selected.Count}";
                RenderProgress();
                PackagesGrid.ScrollIntoView(p);
                QueueLabel.Text = $"{done + 1} / {selected.Count}  ·  {p.Name}";
                StatusLabel.Text = $"{p.Name} 업데이트 중 · 설치가 완료될 때까지 기다려 주세요.";
                var engine = new UpdateEngine(storage, commands ?? winget, preview ? () => Task.CompletedTask : null);
                var currentSignal = signal;
                var entry = await engine.ExecuteAsync(p, preferences.SilentInstall, h => history.Insert(0, h),
                    text => { currentSignal.Feed(text); AppendLog(text); },
                    () => { currentSignal.Verify(); RenderProgress(); StatusLabel.Text = $"{p.Name} 설치 버전을 다시 확인하고 있습니다…"; });
                if (entry.Outcome == "success") succeeded++;
                else if (entry.Outcome == "failed") failed++;
                else if (entry.Outcome == "app-restart") restart++;
                else needsCheck++;
                p.SetOutcome(entry);
                p.Selected = false;
                done++;
                QueueProgress.Value = done;
                HistoryGrid.Items.Refresh();
                RefreshCounters();
            }
        }
        catch (Exception ex)
        {
            scanFailed = true;
            queueError = "기록 저장 또는 실행 오류로 대기열을 중지했습니다: " + ex.Message;
            AppendLog("\n" + queueError + "\n");
        }
        finally
        {
            timer.Stop();
            signal = null;
            string summary = $"완료 {succeeded}개 · 앱 재시작 필요 {restart}개 · 적용 확인 필요 {needsCheck}개 · 실패 {failed}개" +
                (done < selected.Count ? $" · 미실행 {selected.Count - done}개" : "");
            QueueLabel.Text = summary;
            RunTitle.Text = "업데이트 결과";
            RunElapsed.Text = "";
            RunStage.Text = "최신 목록 확인 중…";
            RunDetail.Text = summary;
            busy = false;
            // Keep the close/stop guard active until the authoritative scan finishes.
            await ScanAsync(scanOverride: scanOverride);
            upgrading = false;
            RunDismiss.Visibility = Visibility.Visible;
            RunStage.Text = scanFailed ? "목록 새로고침 실패 · 새로고침으로 다시 확인하세요" : "목록 자동 새로고침 완료";
            RunDetail.Text = queueError ?? summary + (restart > 0 ? "\n앱 재시작이 필요한 항목은 목록에 남습니다. 해당 앱을 완전히 종료하고 다시 연 뒤 새로고침하세요." : "\n상세 결과와 원본 로그는 업데이트 이력에서 확인할 수 있습니다.");
            if (!scanFailed) StatusLabel.Text = queueError ?? $"최신 목록을 반영했습니다 · 남은 업데이트 {packages.Count(p => !p.Hidden)}개";
            RefreshCounters();
        }
    }
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        stopAfterCurrent = true;
        StopButton.IsEnabled = false;
        StatusLabel.Text = "현재 앱의 설치와 버전 확인이 끝나면 나머지 대기열을 중지합니다.";
    }
    private void DismissRun_Click(object sender, RoutedEventArgs e)
    {
        if (!upgrading) RunPanel.Visibility = Visibility.Collapsed;
    }
    private void Hold_Click(object sender, RoutedEventArgs e)
    {
        if (busy || sender is not Button { Tag: PackageEntry p } button) return;
        var menu = new ContextMenu { PlacementTarget = button };
        var hold = new MenuItem { Header = p.Excluded ? "보류 해제" : "보류" };
        hold.Click += (_, _) => ToggleHold(p);
        var hide = new MenuItem { Header = "숨김" };
        hide.Click += (_, _) => HidePackage(p);
        menu.Items.Add(hold);
        menu.Items.Add(hide);
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }
    internal void ToggleHold(PackageEntry p)
    {
        if (busy) return;
        var previous = preferences.ExcludedIds.ToList();
        string key = p.Source + "/" + p.Id;
        preferences.ExcludedIds.RemoveAll(x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (!p.Excluded) preferences.ExcludedIds.Add(key);
        if (!SavePreferencesSafely()) { preferences.ExcludedIds = previous; return; }
        p.Excluded = !p.Excluded;
        if (p.Excluded) p.SetStatus("보류됨"); else p.SetStatus("업데이트 가능", "Success");
        RefreshCounters();
    }
    internal void HidePackage(PackageEntry p)
    {
        if (busy || p.Hidden) return;
        var hidden = new HiddenPackage(p.Name, p.Id, p.Source);
        preferences.HiddenPackages.Add(hidden);
        if (!SavePreferencesSafely()) { preferences.HiddenPackages.Remove(hidden); return; }
        hiddenPackages.Add(hidden);
        p.Hidden = true;
        RefreshCounters();
    }
    private void RestoreHidden_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HiddenPackage hidden }) RestoreHidden([hidden]);
    }
    private void RestoreAll_Click(object sender, RoutedEventArgs e) => RestoreHidden(hiddenPackages.ToList());
    internal void RestoreHidden(IReadOnlyList<HiddenPackage> entries)
    {
        if (busy) return;
        var previous = preferences.HiddenPackages.ToList();
        foreach (var h in entries) preferences.HiddenPackages.Remove(h);
        if (!SavePreferencesSafely()) { preferences.HiddenPackages = previous; return; }
        foreach (var h in entries) hiddenPackages.Remove(h);
        foreach (var p in packages) p.Hidden = preferences.HiddenPackages.Any(h => h.Matches(p));
        StatusLabel.Text = "숨김 해제 완료 · 목록에 없는 앱은 새로고침으로 확인하세요.";
        RefreshCounters();
        if (!preview && packages.Any(p => !p.Hidden && p.SizeState == "pending"))
        {
            metadataLifetime?.Cancel();
            _ = LoadMetadataAsync(packages.Where(p => !p.Hidden).ToList());
        }
    }
    private void Preferences_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        preferences.SilentInstall = SilentInstall.IsChecked == true;
        preferences.ScanOnLaunch = ScanOnLaunch.IsChecked == true;
        preferences.ReduceMotion = ReduceMotion.IsChecked == true;
        Motion.Reduced = preferences.ReduceMotion;
        SavePreferencesSafely();
    }
    private bool SavePreferencesSafely()
    {
        try { storage.SavePreferences(preferences); return true; }
        catch (Exception ex) { MessageBox.Show(this, "설정을 저장하지 못했습니다: " + ex.Message, "저장 오류", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }
    private async void SourceAgreement_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "winget 원본 약관에 동의하고 조회할까요?\n\nMicrosoft Store 원본은 서비스 약관 동의와 기기의 두 자리 국가/지역 코드 전송을 요구할 수 있습니다. 첫 조회의 원본 로그에서 안내된 약관을 확인하세요.\n\n이 작업은 앱을 설치하거나 업데이트하지 않습니다.", "원본 약관 동의", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        SetPage(0);
        await ScanAsync(true);
    }
    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenPath(storage.Root);
    private void ClearHolds_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var previous = preferences.ExcludedIds.ToList();
        preferences.ExcludedIds.Clear();
        if (!SavePreferencesSafely()) { preferences.ExcludedIds = previous; return; }
        foreach (var p in packages) { p.Excluded = false; p.SetStatus("업데이트 가능", "Success"); }
        StatusLabel.Text = "보류 목록을 초기화했습니다.";
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV 파일 (*.csv)|*.csv", FileName = $"Freshet-{Environment.MachineName}-{DateTime.Now:yyyyMMdd}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try { Storage.ExportCsv(dialog.FileName, history); StatusLabel.Text = "CSV 이력을 저장했습니다."; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "내보내기 오류", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void History_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not HistoryEntry h) return;
        string code = h.ExitCode is int exit ? $"0x{exit:X8}" : "없음";
        string detail = $"{h.Name}\n{h.Id}  ·  {h.Source}\n\n시작: {h.Started.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n종료: {h.Finished?.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n이전: {h.Before}\n목표: {h.Target}\n확인: {(h.After.Length > 0 ? h.After : "확인되지 않음")}\n결과: {h.OutcomeText}\n종료 코드: {code}\n\n{h.Detail}\n\n원본 로그를 열까요?";
        if (MessageBox.Show(this, detail, "업데이트 상세", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes && File.Exists(h.LogFile)) OpenPath(h.LogFile);
    }
    private void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "파일 열기 오류"); }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (upgrading)
        {
            e.Cancel = true;
            MessageBox.Show(this, "업데이트가 진행 중입니다. ‘현재 앱 완료 후 중지’를 누르고 설치가 끝나면 종료해 주세요.", "설치 진행 중", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else lifetime.Cancel();
    }
}
