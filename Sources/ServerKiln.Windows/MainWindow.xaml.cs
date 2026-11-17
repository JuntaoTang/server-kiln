using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using System.Windows.Media.Imaging;

namespace ServerKiln.Windows;

public partial class MainWindow : Window
{
    private App AppState => App.Instance;
    private HistoryRange historyRange = HistoryRange.Day;
    private bool historyPerGpu;
    private GpuHistoryMetric historyMetric;
    private DateTimeOffset historyUpdated;
    private Guid? historyServer;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = AppState.ViewModel;
        AppState.ViewModel.SelectionChanged += () => { historyServer = null; RefreshHistoryIfVisible(); };
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!AppState.Exiting) { e.Cancel = true; Hide(); }
    }
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && IsInitialized) RefreshHistoryIfVisible();
    }
    private void OpenSettings(object sender, RoutedEventArgs e) => new SettingsWindow { Owner = this }.ShowDialog();
    private void AddServer(object sender, RoutedEventArgs e) => Edit(null);
    private void EditServer(object sender, RoutedEventArgs e) => Edit(AppState.ViewModel.SelectedServer?.Config);
    private void Edit(ServerConfig? config)
    {
        var editor = new ServerEditor(config) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is not { } result) return;
        try
        {
            var servers = AppState.Preferences.Servers.ToList();
            var index = servers.FindIndex(s => s.Id == result.Id);
            if (index < 0) servers.Add(result); else servers[index] = result;
            AppState.SavePreferences(AppState.Preferences with { Servers = servers.ToArray() });
            AppState.ViewModel.SelectedServer = AppState.ViewModel.Servers.First(s => s.Config.Id == result.Id);
        }
        catch (Exception ex) { AppState.ShowError(ex); }
    }

    private void RemoveServer(object sender, RoutedEventArgs e)
    {
        if (AppState.ViewModel.SelectedServer is not { } selected) return;
        if (MessageBox.Show(this, Localizer.Current["RemoveConfirm"], selected.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            AppState.SavePreferences(AppState.Preferences with { Servers = AppState.Preferences.Servers.Where(s => s.Id != selected.Config.Id).ToArray() });
            AppState.History.Delete(selected.Config.Id);
        }
        catch (Exception ex) { AppState.ShowError(ex); }
    }

    private void ImportServers(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = Localizer.Current["Import"], CheckFileExists = true,
            Filter = "SSH configuration|*|All files|*.*", FileName = string.IsNullOrWhiteSpace(AppState.Preferences.SshConfigPath)
                ? SshConfigParser.DefaultPath : AppState.Preferences.SshConfigPath };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var entries = SshConfigParser.Load(dialog.FileName).Where(h => !AppState.Preferences.Servers.Any(s => s.Target == h.Alias)).ToArray();
            if (entries.Length == 0) { MessageBox.Show(this, Localizer.Current["ImportEmpty"], "ServerKiln"); return; }
            var picker = new ImportWindow(entries) { Owner = this };
            if (picker.ShowDialog() != true) return;
            var servers = AppState.Preferences.Servers.Concat(picker.Selected.Select(h => new ServerConfig { Name = h.Alias, Target = h.Alias })).ToArray();
            // Import and connect through the same config, including nondefault paths.
            AppState.SavePreferences(AppState.Preferences with { Servers = servers, SshConfigPath = dialog.FileName });
        }
        catch (Exception ex) { AppState.ShowError(ex); }
    }

    private async void Refresh(object sender, RoutedEventArgs e)
    {
        try { await AppState.RefreshAsync(); RefreshHistoryIfVisible(force: true); }
        catch (Exception ex) { AppState.ShowError(ex); }
    }
    private void ToggleLanguage(object sender, RoutedEventArgs e)
    {
        try { AppState.SavePreferences(AppState.Preferences with { Language = Localizer.Current.Chinese ? "en" : "zh" }); }
        catch (Exception ex) { AppState.ShowError(ex); }
    }
    private void ToggleTheme(object sender, RoutedEventArgs e)
    {
        try { AppState.SavePreferences(AppState.Preferences with { Theme = AppState.Preferences.Theme == "light" ? "dark" : "light" }); Chart.InvalidateVisual(); }
        catch (Exception ex) { AppState.ShowError(ex); }
    }
    private void CopyCommand(object sender, RoutedEventArgs e)
    {
        try { if (ProcessGrid.SelectedItem is ProcessViewModel process) Clipboard.SetText(process.Command); }
        catch (Exception ex) { AppState.ShowError(ex); }
    }
    private async void OpenTerminal(object sender, RoutedEventArgs e)
    {
        if (AppState.Demo) return;
        if (AppState.ViewModel.SelectedServer?.Config is not { } server) return;
        try
        {
            var info = await SshSession.CreatePreparedStartInfoAsync(server, AppState.Preferences.SshExecutable,
                AppState.Preferences.SshConfigPath, interactive: true);
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex) { AppState.ShowError(ex); }
    }

    private void ChangeHistoryRange(object sender, RoutedEventArgs e)
    { historyRange = Enum.Parse<HistoryRange>((string)((Button)sender).Tag); RefreshHistoryIfVisible(force: true); }
    private void ChangeHistoryMode(object sender, RoutedEventArgs e)
    { historyPerGpu = bool.Parse((string)((Button)sender).Tag); RefreshHistoryIfVisible(force: true); }
    private void ChangeHistoryMetric(object sender, RoutedEventArgs e)
    { historyMetric = Enum.Parse<GpuHistoryMetric>((string)((Button)sender).Tag); RefreshHistoryIfVisible(force: true); }
    private void TabChanged(object sender, SelectionChangedEventArgs e)
    { if (e.Source == DetailTabs && IsInitialized) RefreshHistoryIfVisible(force: true); }
    public void RefreshHistoryIfVisible(bool force = false)
    {
        if (Chart is null || HistoryTab is null || !HistoryTab.IsSelected || AppState.ViewModel.SelectedServer is not { } selected) return;
        var now = DateTimeOffset.UtcNow;
        var refreshSeconds = historyRange == HistoryRange.TenMinutes ? 3 : 30;
        UpdateHistoryControls();
        if (!force && historyServer == selected.Config.Id && now - historyUpdated < TimeSpan.FromSeconds(refreshSeconds)) { Chart.InvalidateVisual(); return; }
        try
        {
            var since = now - historyRange.Duration();
            var points = historyRange == HistoryRange.TenMinutes
                ? AppState.Recent.Query(selected.Config.Id, since, historyPerGpu)
                : AppState.History.Query(selected.Config.Id, since, historyRange.BucketSeconds(), historyPerGpu);
            Chart.SetPoints(points, since, now, historyRange.BucketSeconds(), historyPerGpu, historyMetric);
            historyUpdated = now; historyServer = selected.Config.Id;
        }
        catch (Exception ex) { AppState.ViewModel.StatusMessage = ex.Message; }
    }

    private void UpdateHistoryControls()
    {
        void Select(Button button, bool selected) => button.SetResourceReference(Control.BorderBrushProperty, selected ? "AccentBrush" : "BorderBrush");
        foreach (var button in new[] { RangeLive, RangeDay, RangeWeek, RangeMonth })
            Select(button, (string)button.Tag == historyRange.ToString());
        Select(ModeOverall, !historyPerGpu); Select(ModePerGpu, historyPerGpu);
        Select(MetricUtilization, historyMetric == GpuHistoryMetric.Utilization); Select(MetricMemory, historyMetric == GpuHistoryMetric.Memory);
        GpuMetricPicker.Visibility = historyPerGpu ? Visibility.Visible : Visibility.Collapsed;
        HistoryHint.Text = Localizer.Current[historyRange == HistoryRange.TenMinutes ? "LiveHistoryHint" : "StoredHistoryHint"];
    }

    public async Task CaptureSnapshotsAsync(string directory)
    {
        if (!AppState.Demo) throw new InvalidOperationException("Snapshots require isolated demo data.");
        Directory.CreateDirectory(directory);
        Width = 1220; Height = 830; WindowState = WindowState.Normal;
        AppState.ViewModel.SelectedServer = AppState.ViewModel.Servers.First();
        AppState.SavePreferences(AppState.Preferences with { Theme = "dark" });
        DetailTabs.SelectedIndex = 0;
        await SaveSnapshotAsync(Path.Combine(directory, "overview-dark.png"));
        DetailTabs.SelectedItem = HistoryTab; historyRange = HistoryRange.Day; historyPerGpu = true; historyMetric = GpuHistoryMetric.Utilization;
        RefreshHistoryIfVisible(force: true);
        await SaveSnapshotAsync(Path.Combine(directory, "history-per-gpu-dark.png"));
        AppState.SavePreferences(AppState.Preferences with { Theme = "light" });
        historyRange = HistoryRange.TenMinutes; historyMetric = GpuHistoryMetric.Memory;
        RefreshHistoryIfVisible(force: true);
        await SaveSnapshotAsync(Path.Combine(directory, "history-live-light.png"));
    }

    private async Task SaveSnapshotAsync(string path)
    {
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var root = (FrameworkElement)Content;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, root.ActualWidth + root.Margin.Left + root.Margin.Right, root.ActualHeight + root.Margin.Top + root.Margin.Bottom);
            context.DrawRectangle(Background, null, bounds);
            context.DrawRectangle(new VisualBrush(root), null, new Rect(root.Margin.Left, root.Margin.Top, root.ActualWidth, root.ActualHeight));
        }
        var image = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight - 40), 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); encoder.Save(file);
    }

    public async Task RunSmokeChecksAsync()
    {
        // Render real templates, chart and dialogs with synthetic data. No external I/O.
        foreach (var language in new[] { "en", "zh" })
            foreach (var theme in new[] { "dark", "light" })
            {
                AppState.SavePreferences(AppState.Preferences with { Language = language, Theme = theme });
                foreach (var server in AppState.ViewModel.Servers)
                {
                    AppState.ViewModel.SelectedServer = server;
                    DetailTabs.SelectedIndex = 0; UpdateLayout();
                    DetailTabs.SelectedItem = HistoryTab;
                    foreach (var range in Enum.GetValues<HistoryRange>())
                        foreach (var perGpu in new[] { false, true })
                            foreach (var metric in Enum.GetValues<GpuHistoryMetric>())
                            {
                                historyRange = range; historyPerGpu = perGpu; historyMetric = metric;
                                RefreshHistoryIfVisible(force: true); UpdateLayout();
                            }
                }
                var settings = new SettingsWindow { Owner = this }; settings.Show(); settings.UpdateLayout(); settings.Close();
                var editor = new ServerEditor(null) { Owner = this }; editor.Show(); editor.UpdateLayout(); editor.Close();
                var importer = new ImportWindow([new SshHostEntry("fixture", "gpu.example.com", "researcher", 22, "synthetic")]) { Owner = this };
                importer.Show(); importer.UpdateLayout(); importer.Close();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        Width = MinWidth; Height = MinHeight;
        AppState.SavePreferences(AppState.Preferences with { Servers = [] });
        UpdateLayout();
        if (AppState.ViewModel.EmptyVisibility != Visibility.Visible) throw new InvalidOperationException("Empty fleet is not visible.");
        var waiting = new ServerConfig { Name = "Fixture", Target = "gpu.example.com" };
        AppState.SavePreferences(AppState.Preferences with { Servers = [waiting] });
        DetailTabs.SelectedIndex = 0; UpdateLayout();
        AppState.ViewModel.Apply(new ServerReading(waiting, Error: "Synthetic SSH failure", Failures: 3, Activity: ServerActivity.Offline));
        UpdateLayout();
        AppState.ViewModel.Apply(new ServerReading(waiting,
            Error: "No RSA host key is known for [gpu.example.com]:2222 and you have requested strict checking.\nHost key verification failed.",
            Failures: 3, Activity: ServerActivity.Offline));
        UpdateLayout();
        DetailTabs.SelectedItem = HistoryTab; RefreshHistoryIfVisible(force: true); UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Hide();
        if (!await AppState.ReactivateForSmokeTestAsync()) throw new InvalidOperationException("Shortcut activation failed.");
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (!IsVisible || WindowState != WindowState.Normal) throw new InvalidOperationException("Shortcut did not restore the main window.");
    }
}
