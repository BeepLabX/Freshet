using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using System.ComponentModel;

namespace Freshet;

public sealed class ReviewWindow : Window
{
    public ReviewWindow(IReadOnlyList<PackageEntry> packages)
    {
        Title = "선택한 업데이트 확인";
        Width = 580; Height = 600; MinWidth = 500; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Theme.Brush("Bg"); Foreground = Theme.Brush("Text");
        Theme.TitleBar(this);
        FontFamily = new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Pretendard");
        FontSize = 13;
        var panel = new DockPanel();
        Content = new Border { Padding = new Thickness(26), Background = Background, Child = panel };
        var header = new TextBlock { Text = $"{packages.Count}개 앱을 업데이트합니다", FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 18) };
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); panel.Children.Add(bottom);
        var summary = new TextBlock { Text = DownloadSizes.Summary(packages, selectedOnly: false), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("AccentText"), Margin = new Thickness(0, 12, 0, 6) };
        bottom.Children.Add(summary);
        bottom.Children.Add(new TextBlock { Text = "설치 파일의 예상 용량입니다. 추가 다운로드·의존 패키지는 별도이며 실제 전송량은 달라질 수 있습니다.", TextWrapping = TextWrapping.Wrap, Foreground = Theme.Brush("Muted"), FontSize = 11 });
        void UpdateSummary(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(PackageEntry.EstimatedBytes) or nameof(PackageEntry.SizeState)) summary.Text = DownloadSizes.Summary(packages, selectedOnly: false);
        }
        foreach (var p in packages) p.PropertyChanged += UpdateSummary;
        Closed += (_, _) => { foreach (var p in packages) p.PropertyChanged -= UpdateSummary; };
        bottom.Children.Add(new TextBlock { Text = "진행 중인 앱은 종료되거나 설치 창이 표시될 수 있습니다. 실행 전 작업을 저장하세요.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 16) });
        var agree = new CheckBox { Content = new TextBlock { Text = "선택한 패키지와 winget 원본의 사용 조건에 동의합니다.", TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 0, 0, 20) };
        bottom.Children.Add(agree);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "취소", Padding = new Thickness(20, 10, 20, 10), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var run = new Button { Content = "업데이트 시작", Padding = new Thickness(20, 10, 20, 10), Background = Theme.Brush("Accent"), Foreground = Theme.Brush("OnAccent"), BorderThickness = new Thickness(0), IsEnabled = false };
        agree.Checked += (_, _) => run.IsEnabled = true;
        agree.Unchecked += (_, _) => run.IsEnabled = false;
        run.Click += (_, _) => DialogResult = true;
        cancel.Click += (_, _) => DialogResult = false;
        actions.Children.Add(cancel); actions.Children.Add(run); bottom.Children.Add(actions);
        var list = new StackPanel();
        foreach (var p in packages)
        {
            list.Children.Add(new TextBlock { Text = p.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 3) });
            list.Children.Add(new TextBlock { Text = $"{p.Installed}  →  {p.Available}\n{p.Id}  ·  {p.Source}", Foreground = Theme.Brush("Muted"), TextWrapping = TextWrapping.Wrap });
            var size = new TextBlock { Foreground = Theme.Brush("AccentText"), Margin = new Thickness(0, 4, 0, 0), DataContext = p };
            size.SetBinding(TextBlock.TextProperty, new Binding(nameof(PackageEntry.SizeText)));
            size.SetBinding(ToolTipProperty, new Binding(nameof(PackageEntry.SizeNote)));
            list.Children.Add(size);
        }
        panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Theme.Brush("Surface"), Padding = new Thickness(14) });
    }
}
