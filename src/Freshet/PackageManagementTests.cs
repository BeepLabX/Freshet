using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Freshet;

internal static class PackageManagementTests
{
    public static void Run(MainWindow window, string root, Action<bool, string> check)
    {
        var grid = (DataGrid)window.FindName("PackagesGrid");
        var header = (CheckBox)window.FindName("HeaderSelectAll");
        var search = (TextBox)window.FindName("SearchBox");
        var hiddenGrid = (DataGrid)window.FindName("HiddenGrid");
        ((Button)window.FindName("UpdatesNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static PackageEntry Package(string id, string source = "winget") => new() { Name = id, Id = id, Source = source, Installed = "1", Available = "2" };
        var a = Package("Visible.A"); var b = Package("Visible.B"); var c = Package("Other.C");
        window.LoadDiagnosticSnapshot([a, b, c]);
        window.ToggleHold(b);
        search.Text = "Visible";
        header.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        check(a.Selected && !b.Selected && !c.Selected && header.IsChecked == true, "Header selects filtered eligible apps and excludes held apps");
        search.Text = "";
        check(header.IsChecked is null, "Header shows partial selection after the filter changes");
        header.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        check(a.Selected && c.Selected && !b.Selected, "Header selects all eligible apps from a partial selection");
        header.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        check(!a.Selected && !c.Selected && header.IsChecked == false, "Header clears all visible eligible selections");

        // Exercise the actual row button, including its context menu actions.
        grid.ScrollIntoView(b); window.UpdateLayout();
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(b);
        static Button? FindButton(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Button { Tag: PackageEntry } button) return button;
                if (FindButton(child) is { } found) return found;
            }
            return null;
        }
        var action = FindButton(row)!;
        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var menu = action.ContextMenu!;
        check(menu.Items.Count == 2 && ((MenuItem)menu.Items[0]).Header.ToString() == "보류 해제" && ((MenuItem)menu.Items[1]).Header.ToString() == "숨김", "Held row offers both release and hide actions");
        ((MenuItem)menu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        check(grid.Items.Count == 2 && !grid.Items.Contains(b) && hiddenGrid.Items.Count == 1, "Hide removes an app from the update list and adds it to the manager");
        a.Selected = true; window.HidePackage(a);
        check(!a.Selected && !a.CanSelect && ((TextBlock)window.FindName("AvailableCount")).Text == "1" && ((TextBlock)window.FindName("SelectedLabel")).Text == "0개 선택됨", "Hiding a selected app updates selection and visible counts");
        ((Button)window.FindName("HiddenNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(((FrameworkElement)window.FindName("HiddenPage")).Visibility == Visibility.Visible, "Hidden manager navigation");
        SelfTests.Capture(window, Path.Combine(root, "Freshet-hidden.png"));

        // A fresh window and actual scan path must apply persisted identities before display.
        var store = new Storage(Path.Combine(root, "preview-data"));
        var persisted = store.ReadPreferences();
        check(persisted.HiddenPackages.Count == 2 && persisted.ExcludedIds.Count == 1, "Hidden and held settings persist independently");
        check(File.Exists(Path.Combine(store.Root, "preferences.json.bak")), "Preference writes retain the previous file as a backup");
        var fresh = new MainWindow(store, true) { ShowActivated = false, ShowInTaskbar = false };
        var same = Package("visible.a", "WINGET"); var differentSource = Package("Visible.A", "msstore");
        fresh.ScanAsync(scanOverride: () => Task.FromResult(new ScanResult([same, differentSource, Package("Visible.B")], new(0, "", ""), 0, true))).GetAwaiter().GetResult();
        var freshGrid = (DataGrid)fresh.FindName("PackagesGrid");
        check(freshGrid.Items.Count == 1 && freshGrid.Items.Contains(differentSource), "Reload and scan exclude hidden identities case-insensitively without hiding another source");
        fresh.RestoreHidden([persisted.HiddenPackages.Single(h => h.Id == "Visible.B")]);
        var released = freshGrid.Items.Cast<PackageEntry>().Single(p => p.Id == "Visible.B");
        check(released.Excluded && !released.Selected, "Unhiding retains the original hold and does not select the app");
        fresh.ScanAsync(scanOverride: () => Task.FromResult(new ScanResult([], new(0, "", ""), 0, true))).GetAwaiter().GetResult();
        check(((DataGrid)fresh.FindName("HiddenGrid")).Items.Count == 1, "Hidden manager retains packages absent from the current winget result");
        ((Button)fresh.FindName("RestoreAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(store.ReadPreferences().HiddenPackages.Count == 0, "Restore all clears persisted hidden identities even when packages are absent");
        fresh.Close();
        // Restore the original window's independent snapshot for subsequent queue tests.
        window.RestoreHidden(hiddenGrid.Items.Cast<HiddenPackage>().ToList());
        window.ToggleHold(b);
        ((Button)window.FindName("UpdatesNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var legacyStore = new Storage(Path.Combine(root, "legacy-preferences"));
        string legacyPath = Path.Combine(legacyStore.Root, "preferences.json");
        const string legacy = "{\"SilentInstall\":false,\"ScanOnLaunch\":false,\"ReduceMotion\":true,\"ExcludedIds\":[\"winget/Legacy.App\"]}";
        File.WriteAllText(legacyPath, legacy);
        var old = legacyStore.ReadPreferences();
        old.HiddenPackages.Add(new("Legacy", "Legacy.App", "winget")); legacyStore.SavePreferences(old);
        var roundTrip = legacyStore.ReadPreferences();
        check(!roundTrip.SilentInstall && !roundTrip.ScanOnLaunch && roundTrip.ReduceMotion && roundTrip.ExcludedIds.Single() == "winget/Legacy.App" && File.ReadAllText(legacyPath + ".bak") == legacy, "Legacy settings remain intact and original bytes are backed up when hidden support is added");
    }
}
