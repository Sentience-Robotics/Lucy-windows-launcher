using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfButton = System.Windows.Controls.Button;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;

namespace LucyWindowsLauncher;

public partial class MainWindow : Window
{
    private sealed class LauncherConfig { public List<LauncherPackage> Packages { get; set; } = []; }
    private sealed class LauncherPackage
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string> Dependencies { get; set; } = [];
        public List<string> Conflicts { get; set; } = [];
        public bool DefaultOn { get; set; }
    }

    private sealed class ProcessSession
    {
        public required string Id { get; init; }
        public required Process Process { get; init; }
        public required WpfTextBox Output { get; init; }
        public required TabItem Tab { get; init; }
    }

    private readonly Dictionary<string, WpfCheckBox> _checks;
    private readonly Dictionary<string, ProcessSession> _sessions = [];
    private readonly Dictionary<string, string> _tasks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["core"] = "core", ["control_panel"] = "control-panel", ["lucy_cli"] = "lucy-cli", ["rqt"] = "rqt"
    };
    private List<LauncherPackage> _packages = [];
    private bool _updatingSelection;

    public MainWindow()
    {
        InitializeComponent();
        _checks = new()
        {
            ["core"] = CoreCheckBox, ["robot_inmoov"] = RobotInmoovCheckBox, ["gazebo"] = GazeboCheckBox,
            ["headless"] = HeadlessCheckBox, ["rviz"] = RvizCheckBox, ["real"] = RealCheckBox,
            ["control_panel"] = ControlPanelCheckBox, ["lucy_cli"] = LucyCliCheckBox,
            ["rqt"] = RqtCheckBox, ["console"] = ConsoleCheckBox
        };
        WorkspaceTextBox.Text = FindWorkspace();
        LoadConfiguration();
        UpdateSummary();
    }

    private static string FindWorkspace()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var current = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "lucy_ws"),
            Path.Combine(current, "lucy_ws"),
            Path.Combine(current, "..", "..", "..", "..", "lucy_ws"),
            Path.Combine(home, "lucy_ws"),
            Path.Combine(home, "Documents", "lucy_ws"),
            Path.Combine(home, "source", "lucy_ws"),
            Path.Combine(home, "Projects", "lucy_ws"),
            Path.Combine(home, "Dev", "lucy_ws")
        };
        return candidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(IsLucyWorkspace)
            ?? Path.Combine(home, "lucy_ws");
    }

    private static bool IsLucyWorkspace(string path) =>
        Directory.Exists(path) && File.Exists(Path.Combine(path, "pixi.toml"));

    private void LoadConfiguration()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "config", "launcher_config.json");
        if (!File.Exists(configPath)) { AppendOutput("all", $"Configuration not found: {configPath}"); return; }
        try
        {
            var config = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(configPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            _packages = config?.Packages ?? [];
            foreach (var package in _packages)
            {
                if (_checks.TryGetValue(package.Id, out var check))
                {
                    check.Content = package.Name;
                    check.ToolTip = package.Description;
                    check.IsChecked = package.DefaultOn;
                }
            }
        }
        catch (JsonException ex) { AppendOutput("all", $"Invalid launcher configuration: {ex.Message}"); }
    }

    private void ToolCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingSelection) return;
        var changed = (WpfCheckBox)sender;
        var id = _checks.First(pair => ReferenceEquals(pair.Value, changed)).Key;
        var package = _packages.FirstOrDefault(item => item.Id == id);
        if (changed.IsChecked == true && package is not null)
        {
            _updatingSelection = true;
            foreach (var dependency in package.Dependencies)
                if (_checks.TryGetValue(dependency, out var dependencyCheck)) dependencyCheck.IsChecked = true;
            foreach (var conflict in package.Conflicts)
                if (_checks.TryGetValue(conflict, out var conflictCheck)) conflictCheck.IsChecked = false;
            _updatingSelection = false;
        }
        HeadlessCheckBox.IsEnabled = GazeboCheckBox.IsChecked == true;
        if (!HeadlessCheckBox.IsEnabled) HeadlessCheckBox.IsChecked = false;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var selected = _checks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList();
        SelectionSummaryTextBlock.Text = selected.Count == 0
            ? "No tools selected"
            : $"{selected.Count} tool{(selected.Count == 1 ? "" : "s")} selected: {string.Join(", ", selected)}";
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var workspace = WorkspaceTextBox.Text.Trim();
        if (!Directory.Exists(workspace))
        {
            System.Windows.MessageBox.Show("Select an existing lucy_ws workspace folder.", "Workspace not found",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var selected = _checks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show("Select at least one tool to start.", "Nothing selected",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetRunning(true);
        AppendOutput("all", $"Workspace: {workspace}");
        var starts = BuildTaskSelection(selected)
            .Where(invocation => invocation.Task == "console" || !_sessions.ContainsKey(invocation.Id))
            .Select(invocation => invocation.Task == "console"
                ? Task.Run(() => StartConsole(workspace))
                : StartTaskAsync(invocation.Id, invocation.Task, invocation.Arguments, workspace));
        await Task.WhenAll(starts);
    }

    private IEnumerable<(string Id, string Task, string Arguments)> BuildTaskSelection(IReadOnlyCollection<string> selected)
    {
        if (selected.Contains("core"))
        {
            var task = selected.Contains("gazebo") && selected.Contains("rviz") ? "sim-rviz"
                : selected.Contains("gazebo") && selected.Contains("headless") ? "sim-headless"
                : selected.Contains("gazebo") ? "sim"
                : selected.Contains("rviz") ? "rviz" : "core";
            var args = new List<string>();
            if (selected.Contains("robot_inmoov")) args.Add("robot_package:=inmoov_urdf");
            if (selected.Contains("real")) args.Add("real:=true");
            yield return ("core", task, string.Join(" ", args));
        }
        foreach (var id in selected.Where(id => id is "control_panel" or "lucy_cli" or "rqt" or "console"))
            yield return (id, id, "");
    }

    private async Task StartTaskAsync(string id, string task, string arguments, string workspace)
    {
        var pixiTask = _tasks.TryGetValue(task, out var mappedTask) ? mappedTask : task;
        var argumentSuffix = string.IsNullOrWhiteSpace(arguments) ? "" : $" {arguments}";
        var output = CreateOutputBox();
        var stopButton = new WpfButton { Content = "Stop this command", Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Style = (Style)FindResource("SecondaryButton") };
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(output, 1);
        Grid.SetRow(stopButton, 0);
        content.Children.Add(stopButton);
        content.Children.Add(output);
        var tab = new TabItem { Header = id.ToUpperInvariant(), Content = content };
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /s /c \"pixi run {pixiTask}{argumentSuffix}\"",
                WorkingDirectory = workspace, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        var session = new ProcessSession { Id = id, Process = process, Output = output, Tab = tab };
        stopButton.Click += (_, _) => StopSession(id);
        process.OutputDataReceived += (_, args) => AppendOutputOnUiThread(id, args.Data);
        process.ErrorDataReceived += (_, args) => AppendOutputOnUiThread(id, args.Data);
        process.Exited += (_, _) => Dispatcher.BeginInvoke(() => SessionExited(session));
        try
        {
            process.Start();
            _sessions[id] = session;
            ProcessTabs.Items.Add(tab);
            ProcessTabs.SelectedItem = tab;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            AppendOutput(id, $"> pixi run {pixiTask}{argumentSuffix}");
            await Task.CompletedTask;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            AppendOutput(id, $"Could not start command: {ex.Message}");
            process.Dispose();
        }
    }

    private WpfTextBox CreateOutputBox() => new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Background = new SolidColorBrush(MediaColor.FromRgb(5, 5, 5)), Foreground = new SolidColorBrush(MediaColor.FromRgb(0, 255, 65)),
        BorderBrush = new SolidColorBrush(MediaColor.FromRgb(48, 48, 48)), Margin = new Thickness(0, 8, 0, 0)
    };

    private void SessionExited(ProcessSession session)
    {
        if (!_sessions.Remove(session.Id))
        {
            session.Process.Dispose();
            return;
        }
        AppendOutput(session.Id, $"Process exited with code {session.Process.ExitCode}.");
        ProcessTabs.Items.Remove(session.Tab);
        session.Tab.Header = $"{session.Id.ToUpperInvariant()}  [STOPPED]";
        session.Process.Dispose();
        if (_sessions.Count == 0) SetRunning(false);
    }

    private void StopSession(string id)
    {
        if (_sessions.TryGetValue(id, out var session) && !session.Process.HasExited)
        {
            AppendOutput(id, "Stopping process tree...");
            _sessions.Remove(id);
            ProcessTabs.Items.Remove(session.Tab);
            session.Process.Kill(entireProcessTree: true);
            if (_sessions.Count == 0) SetRunning(false);
        }
    }

    private void StartConsole(string workspace)
    {
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k cd /d \"{workspace}\"") { UseShellExecute = true, WorkingDirectory = workspace });
        AppendOutput("all", "> opened interactive console");
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var id in _sessions.Keys.ToList()) StopSession(id);
        AppendOutput("all", "Stopping all command process trees...");
    }

    private void SetRunning(bool running)
    {
        StopButton.IsEnabled = running || _sessions.Count > 0;
        StatusTextBlock.Text = running ? "RUNNING" : "READY";
        StatusTextBlock.Foreground = running ? MediaBrushes.Yellow : MediaBrushes.Gray;
    }

    private void BrowseWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Select the lucy_ws workspace", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) WorkspaceTextBox.Text = dialog.SelectedPath;
    }

    private void AppendOutput(string id, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (id == "all") return;
        if (_sessions.TryGetValue(id, out var session))
        {
            session.Output.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
            session.Output.ScrollToEnd();
        }
    }

    private void AppendOutputOnUiThread(string id, string? text) =>
        Dispatcher.BeginInvoke(() => AppendOutput(id, text), DispatcherPriority.Background);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var id in _sessions.Keys.ToList()) StopSession(id);
        Close();
    }
}
