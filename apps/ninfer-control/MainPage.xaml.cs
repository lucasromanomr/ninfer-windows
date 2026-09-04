using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using UiDispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using UiDispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace NInferControl;

public sealed partial class MainPage : Page
{
    private readonly UiDispatcherQueue _dispatcherQueue;
    private readonly StringBuilder _log = new();
    private Process? _serverProcess;
    private LogWindow? _logWindow;
    private readonly UiDispatcherQueueTimer _saveTimer;
    private bool _stopRequested;
    private bool _isNarrowServerLayout;
    private bool _settingsLoaded;
    private string _lastCommand = string.Empty;

    public MainPage()
    {
        InitializeComponent();
        _dispatcherQueue = UiDispatcherQueue.GetForCurrentThread();
        _saveTimer = _dispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(650);
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveSettingsAsync();
        };

        ServerPathBox.Text = FindServerExecutable() ?? string.Empty;
        ServerPathBox.TextChanged += (_, _) => RefreshCommandPreview();
        ModelPathBox.TextChanged += (_, _) => RefreshCommandPreview();
        HostBox.TextChanged += (_, _) => RefreshCommandPreview();
        // NumberBox reports edits through ValueChanged; the toggles are wired in XAML to
        // Option_Toggled so the preview is not refreshed twice per interaction.
        foreach (var box in new[] { PortBox, ConcurrencyBox, StatsIntervalBox, MaxContextBox, DefaultTokensBox, KvCapacityBox, DraftTokensBox })
        {
            box.ValueChanged += (_, _) => RefreshCommandPreview();
        }
        KvDtypeBox.SelectionChanged += (_, _) => RefreshCommandPreview();
        Loaded += MainPage_Loaded;
    }

    internal void Shutdown()
    {
        StopServer();
        _logWindow?.Close();
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainPage_Loaded;
        await LoadSettingsAsync();
        _settingsLoaded = true;
        RefreshCommandPreview();
    }

    private async void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".ninfer");
        InitializeWithWindow(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ModelPathBox.Text = file.Path;
            ModelHint.SetDescriptionText($"Selecionado: {file.Name}");
        }
    }

    private async void BrowseServer_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ServerPathBox.Text = file.Path;
            ServerHint.SetDescriptionText("Executável selecionado manualmente.");
        }
    }

    private void InitializeWithWindow(object picker)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    private void NavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if ((args.SelectedItem as NavigationViewItem)?.Tag is string destination)
        {
            ShowDestination(destination);
        }
    }

    private void ShowLogs_Click(object sender, RoutedEventArgs e)
    {
        MainNavigation.SelectedItem = LogsNavItem;
        ShowDestination("logs");
    }

    private void ShowDestination(string destination)
    {
        ServerView.Visibility = destination == "server" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedView.Visibility = destination == "advanced" ? Visibility.Visible : Visibility.Collapsed;
        LogsView.Visibility = destination == "logs" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ServerLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Below this the 420px command column would squeeze the form past readability, so the two
        // panes stack instead.
        var narrow = e.NewSize.Width < 1080;
        if (_isNarrowServerLayout == narrow)
        {
            return;
        }

        _isNarrowServerLayout = narrow;
        ServerLayout.ColumnSpacing = narrow ? 0 : 16;
        ServerLeftColumn.Width = new GridLength(1, GridUnitType.Star);
        ServerRightColumn.Width = narrow ? new GridLength(0) : new GridLength(420);
        ServerLayout.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        ServerLayout.RowDefinitions[1].Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(ServerFormPane, 0);
        Grid.SetRow(ServerFormPane, 0);
        Grid.SetColumn(ServerCommandPane, narrow ? 0 : 1);
        Grid.SetRow(ServerCommandPane, narrow ? 1 : 0);
        ServerFormPane.Padding = narrow ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 4, 0);
    }

    private void KvAuto_Toggled(object sender, RoutedEventArgs e)
    {
        if (KvCapacityBox is null || KvCapacityCard is null)
        {
            return;
        }

        var manual = !KvAutoBox.IsOn;
        KvCapacityBox.IsEnabled = manual;
        KvCapacityCard.IsEnabled = manual;
        if (_settingsLoaded)
        {
            RefreshCommandPreview();
        }
    }

    // Toggled fires as the parser assigns IsOn, long before the rest of the page exists, so every
    // toggle handler has to no-op until the page has finished loading its saved settings.
    private void Option_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded)
        {
            return;
        }

        RefreshCommandPreview();
    }

    private void Spec_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpecBox is null || DraftTokensBox is null || LmHeadDraftBox is null)
        {
            return;
        }
        var backend = SelectedTag(SpecBox) ?? "off";
        ApplyBackendAvailability(backend);
        DraftTokensBox.Maximum = backend == "dflash" ? 15 : 5;
        if (DraftTokensBox.Value > DraftTokensBox.Maximum)
        {
            DraftTokensBox.Value = DraftTokensBox.Maximum;
        }
        RefreshCommandPreview();
    }

    // Controls that do not apply to the selected backend are dimmed rather than hidden, so the
    // page does not reflow while the user compares backends.
    private void ApplyBackendAvailability(string backend)
    {
        DraftTokensBox.IsEnabled = backend != "off";
        LmHeadDraftBox.IsEnabled = backend == "mtp";
        if (DraftTokensCard is not null) DraftTokensCard.IsEnabled = backend != "off";
        if (LmHeadDraftCard is not null) LmHeadDraftCard.IsEnabled = backend == "mtp";
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_serverProcess is not null && !_serverProcess.HasExited)
        {
            return;
        }

        if (!TryReadConfiguration(out var configuration))
        {
            return;
        }

        ClearValidation();
        await SaveSettingsAsync();
        _log.Clear();
        AppendLog("[NInfer Control] iniciando ninfer-serve.exe...");
        SetStatus("Iniciando", "O modelo está sendo carregado na GPU...", "AccentBrush", busy: true);
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        OpenBrowserButton.IsEnabled = false;
        _stopRequested = false;

        var startInfo = new ProcessStartInfo
        {
            FileName = configuration.ServerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(configuration.ServerPath) ?? AppContext.BaseDirectory,
        };
        foreach (var argument in BuildArguments(configuration))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += (_, _) => OnServerExited(process);
            if (!process.Start())
            {
                throw new InvalidOperationException("O processo não pôde ser iniciado.");
            }

            _serverProcess = process;
            _ = PumpOutputAsync(process.StandardOutput, "OUT");
            _ = PumpOutputAsync(process.StandardError, "ERR");
            SetStatus("Carregando", "Acompanhe o progresso no log.", "AccentBrush", busy: true);
            ShowLogs_Click(sender, e);
        }
        catch (Exception ex)
        {
            AppendLog($"[ERRO] {ex.Message}");
            SetStatus("Erro", "Não foi possível iniciar o servidor.", "DangerBrush");
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopServer();

    private void OpenLogWindow_Click(object sender, RoutedEventArgs e)
    {
        if (_logWindow is null)
        {
            _logWindow = new LogWindow();
            _logWindow.Closed += (_, _) => _logWindow = null;
        }

        _logWindow.SetLog(_log.ToString());
        _logWindow.Activate();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        LogTextBox.Text = string.Empty;
        _logWindow?.SetLog(string.Empty);
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var data = new DataPackage();
        data.SetText(_log.ToString());
        Clipboard.SetContent(data);
        StatusDetail.Text = "Log copiado para a área de transferência.";
    }

    private void WrapToggle_Click(object sender, RoutedEventArgs e)
        => LogTextBox.TextWrapping = WrapToggle.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;

    private void StopServer()
    {
        var process = _serverProcess;
        if (process is null)
        {
            return;
        }

        _stopRequested = true;
        SetStatus("Parando", "Encerrando o processo do servidor...", "MutedTextBrush", busy: true);
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            OnServerExited(process);
        }
    }

    private void OnServerExited(Process process)
    {
        var exitCode = -1;
        try { exitCode = process.ExitCode; } catch (InvalidOperationException) { }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_serverProcess, process))
            {
                return;
            }

            _serverProcess = null;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            OpenBrowserButton.IsEnabled = false;
            var detail = _stopRequested
                ? "Servidor parado pelo usuário."
                : $"Servidor finalizado com código {exitCode}.";
            SetStatus(_stopRequested ? "Parado" : "Finalizado", detail,
                _stopRequested ? "MutedTextBrush" : "DangerBrush");
            AppendLog($"[NInfer Control] processo finalizado ({exitCode}).");
        });
    }

    private async Task PumpOutputAsync(StreamReader reader, string channel)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            AppendLog($"[{channel}] {line}");
            if (channel == "OUT" && line.Contains("listening", StringComparison.OrdinalIgnoreCase))
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    SetStatus("Online", "API disponível para conexões.", "AccentBrush");
                    OpenBrowserButton.IsEnabled = true;
                });
            }
            else if (channel == "ERR" && line.Contains("listening", StringComparison.OrdinalIgnoreCase))
            {
                _dispatcherQueue.TryEnqueue(() =>
                {
                    SetStatus("Online", "API disponível para conexões.", "AccentBrush");
                    OpenBrowserButton.IsEnabled = true;
                });
            }
        }
    }

    private void AppendLog(string line)
    {
        void Append()
        {
            _log.AppendLine(line);
            if (_log.Length > 300_000)
            {
                _log.Remove(0, _log.Length - 250_000);
            }
            var text = _log.ToString();
            LogTextBox.Text = text;
            // Moving the caret is what scrolls a read-only TextBox; skip it when the user turned
            // auto-scroll off so they can read back through the buffer while output keeps arriving.
            if (AutoScrollToggle.IsChecked == true)
            {
                LogTextBox.Select(text.Length, 0);
            }
            _logWindow?.SetLog(text);
        }

        if (_dispatcherQueue.HasThreadAccess) Append();
        else _dispatcherQueue.TryEnqueue(Append);
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        RefreshCommandPreview();
        var data = new DataPackage();
        data.SetText(_lastCommand);
        Clipboard.SetContent(data);
        StatusDetail.Text = "Comando copiado para a área de transferência.";
    }

    private async void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (host is "0.0.0.0" or "::") host = "127.0.0.1";
        if (!int.TryParse(PortBox.Text, out var port)) return;
        await Launcher.LaunchUriAsync(new Uri($"http://{host}:{port}/v1"));
    }

    private void Configuration_TextChanged(object sender, TextChangedEventArgs e) => RefreshCommandPreview();

    private void RefreshCommandPreview()
    {
        if (CommandPreview is null || CorsBox is null) return;
        var values = new List<string>
        {
            "ninfer-serve.exe",
            Quote(ModelPathBox.Text.Trim()),
            "--host", Quote(HostBox.Text.Trim()),
            "--port", Num(PortBox).ToString(),
            "--max-context", Num(MaxContextBox).ToString(),
            "--default-max-tokens", Num(DefaultTokensBox).ToString(),
            "--kv-dtype", SelectedTag(KvDtypeBox) ?? "int8",
            "--kv-capacity", KvAutoBox.IsOn ? "auto" : Num(KvCapacityBox).ToString(),
            "--max-concurrency", Num(ConcurrencyBox).ToString(),
            "--log-stats-interval-ms", Num(StatsIntervalBox).ToString(),
        };

        var backend = SelectedTag(SpecBox) ?? "off";
        if (backend != "off")
        {
            values.Add("--spec");
            values.Add(backend);
            values.Add("--draft-tokens");
            values.Add(Num(DraftTokensBox).ToString());
            if (backend == "mtp" && LmHeadDraftBox.IsOn) values.Add("--lm-head-draft");
        }
        if (VisionBox.IsOn) values.Add("--vision");
        if (NoThinkingBox.IsOn) values.Add("--no-thinking");
        if (PreserveThinkingBox.IsOn) values.Add("--preserve-thinking");
        if (CorsBox.IsOn) values.Add("--cors");

        _lastCommand = string.Join(" ", values);
        CommandPreview.Text = _lastCommand;
        ScheduleSettingsSave();
    }

    private void ScheduleSettingsSave()
    {
        if (!_settingsLoaded)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private bool TryReadConfiguration(out ServerConfiguration configuration)
    {
        configuration = new ServerConfiguration();
        var modelPath = ModelPathBox.Text.Trim();
        var serverPath = ServerPathBox.Text.Trim();
        if (!File.Exists(modelPath) || !modelPath.EndsWith(".ninfer", StringComparison.OrdinalIgnoreCase))
        {
            ShowValidation("Selecione um arquivo .ninfer existente.", ServerNavItem);
            return false;
        }
        if (!File.Exists(serverPath))
        {
            ShowValidation("Selecione um ninfer-serve.exe existente.", ServerNavItem);
            return false;
        }

        var port = (int)Num(PortBox);
        var maxContext = Num(MaxContextBox);
        var defaultTokens = (int)Num(DefaultTokensBox);
        var concurrency = Num(ConcurrencyBox);
        var statsInterval = Num(StatsIntervalBox);
        if (port is < 1 or > 65535 || maxContext == 0 || defaultTokens <= 0 || concurrency is < 1 or > 8)
        {
            ShowValidation("Revise porta, contexto, tokens e concorrência.", AdvancedNavItem);
            return false;
        }

        var kvCapacity = KvAutoBox.IsOn ? 0u : Num(KvCapacityBox);
        if (!KvAutoBox.IsOn && (kvCapacity == 0 || kvCapacity < maxContext))
        {
            ShowValidation($"A capacidade do KV ({kvCapacity}) deve ser igual ou maior que o contexto máximo ({maxContext}).", AdvancedNavItem);
            return false;
        }

        var backend = SelectedTag(SpecBox) ?? "off";
        var draftTokens = Num(DraftTokensBox);
        var maxDraft = backend == "dflash" ? 15u : 5u;
        if (backend != "off" && (draftTokens is < 1 || draftTokens > maxDraft))
        {
            ShowValidation($"Draft tokens deve ficar entre 1 e {maxDraft} para {backend}.", AdvancedNavItem);
            return false;
        }
        if (backend == "dflash" && VisionBox.IsOn)
        {
            ShowValidation("DFlash não pode ser combinado com Vision.", AdvancedNavItem);
            return false;
        }

        configuration = new ServerConfiguration
        {
            ModelPath = modelPath,
            ServerPath = serverPath,
            Host = HostBox.Text.Trim(),
            Port = port,
            MaxContext = maxContext,
            DefaultTokens = defaultTokens,
            KvDtype = SelectedTag(KvDtypeBox) ?? "int8",
            KvCapacity = kvCapacity,
            KvAuto = KvAutoBox.IsOn,
            Concurrency = concurrency,
            StatsInterval = statsInterval,
            Backend = backend,
            DraftTokens = draftTokens,
            LmHeadDraft = LmHeadDraftBox.IsOn && backend == "mtp",
            Vision = VisionBox.IsOn,
            NoThinking = NoThinkingBox.IsOn,
            PreserveThinking = PreserveThinkingBox.IsOn,
            Cors = CorsBox.IsOn,
        };
        return true;
    }

    private IEnumerable<string> BuildArguments(ServerConfiguration c)
    {
        yield return c.ModelPath;
        yield return "--host"; yield return c.Host;
        yield return "--port"; yield return c.Port.ToString();
        yield return "--max-context"; yield return c.MaxContext.ToString();
        yield return "--default-max-tokens"; yield return c.DefaultTokens.ToString();
        yield return "--kv-dtype"; yield return c.KvDtype;
        yield return "--kv-capacity"; yield return c.KvAuto ? "auto" : c.KvCapacity.ToString();
        yield return "--max-concurrency"; yield return c.Concurrency.ToString();
        yield return "--log-stats-interval-ms"; yield return c.StatsInterval.ToString();
        if (c.Backend != "off")
        {
            yield return "--spec"; yield return c.Backend;
            yield return "--draft-tokens"; yield return c.DraftTokens.ToString();
            if (c.LmHeadDraft) yield return "--lm-head-draft";
        }
        if (c.Vision) yield return "--vision";
        if (c.NoThinking) yield return "--no-thinking";
        if (c.PreserveThinking) yield return "--preserve-thinking";
        if (c.Cors) yield return "--cors";
    }

    private void SetStatus(string status, string detail, string brushKey, bool busy = false)
    {
        StatusText.Text = status;
        StatusDetail.Text = detail;
        StatusDot.Fill = (Brush)Application.Current.Resources[brushKey];
        StatusDot.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        StatusRing.IsActive = busy;
        StatusRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(StatusBadge, $"Status do servidor: {status}. {detail}");
    }

    // Every validation failure lands in the same InfoBar, and the nav item that owns the offending
    // field is named so the user knows where to go instead of hunting for red text.
    private void ShowValidation(string message, NavigationViewItem origin)
    {
        ValidationBar.Title = ReferenceEquals(origin, AdvancedNavItem) ? "Ajustes avançados" : "Servidor";
        ValidationBar.Message = message;
        ValidationBar.Severity = InfoBarSeverity.Error;
        ValidationBar.IsOpen = true;
        if (!ReferenceEquals(MainNavigation.SelectedItem, origin))
        {
            MainNavigation.SelectedItem = origin;
            ShowDestination((string)origin.Tag);
        }
        SetStatus("Revise os campos", message, "DangerBrush");
    }

    private void ClearValidation() => ValidationBar.IsOpen = false;

    private static uint Num(NumberBox box) => double.IsNaN(box.Value) || box.Value < 0 ? 0u : (uint)box.Value;

    private static string? SelectedTag(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static string Quote(string value)
        => $"\"{value.Replace("\"", "\\\"")}\"";

    // The shipped server sits next to the executable: beside NInferControl.exe in an MSIX install,
    // and in the runtime's extraction directory for the single-file portable build, which is what
    // AppContext.BaseDirectory reports in both cases. It wins over a saved path, which may point at
    // an older build.
    private static string? BundledServerExecutable()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "ninfer-serve.exe");
        return File.Exists(bundled) ? bundled : null;
    }

    private static string? FindServerExecutable()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && current is not null; i++, current = current.Parent)
        {
            var candidates = new[]
            {
                Path.Combine(current.FullName, "ninfer-serve.exe"),
                Path.Combine(current.FullName, "build-ninja", "apps", "Release", "ninfer-serve.exe"),
                Path.Combine(current.FullName, "build-windows", "apps", "Release", "ninfer-serve.exe"),
            };
            var found = candidates.FirstOrDefault(File.Exists);
            if (found is not null) return found;
        }
        return null;
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var path = GetSettingsPath();
            if (!File.Exists(path)) return;
            var snapshot = JsonSerializer.Deserialize<SettingsSnapshot>(await File.ReadAllTextAsync(path));
            if (snapshot is null) return;
            if (!string.IsNullOrWhiteSpace(snapshot.ModelPath)) ModelPathBox.Text = snapshot.ModelPath;
            if (BundledServerExecutable() is null && !string.IsNullOrWhiteSpace(snapshot.ServerPath) && File.Exists(snapshot.ServerPath)) ServerPathBox.Text = snapshot.ServerPath;
            if (!string.IsNullOrWhiteSpace(snapshot.Host)) HostBox.Text = snapshot.Host;
            PortBox.Value = snapshot.Port;
            MaxContextBox.Value = snapshot.MaxContext;
            DefaultTokensBox.Value = snapshot.DefaultTokens;
            KvCapacityBox.Value = snapshot.KvCapacity;
            KvAutoBox.IsOn = snapshot.KvAuto;
            KvCapacityBox.IsEnabled = !snapshot.KvAuto;
            KvCapacityCard.IsEnabled = !snapshot.KvAuto;
            ConcurrencyBox.Value = snapshot.Concurrency;
            StatsIntervalBox.Value = snapshot.StatsInterval;
            KvDtypeBox.SelectedIndex = snapshot.KvDtype == "bf16" ? 0 : 1;
            SpecBox.SelectedIndex = snapshot.Backend switch { "mtp" => 1, "dflash" => 2, _ => 0 };
            VisionBox.IsOn = snapshot.Vision;
            NoThinkingBox.IsOn = snapshot.NoThinking;
            PreserveThinkingBox.IsOn = snapshot.PreserveThinking;
            CorsBox.IsOn = snapshot.Cors;
            LmHeadDraftBox.IsOn = snapshot.LmHeadDraft;
            var backend = SelectedTag(SpecBox) ?? "off";
            // Maximum is set before Value so a saved DFlash draft count is not clamped to the MTP limit.
            DraftTokensBox.Maximum = backend == "dflash" ? 15 : 5;
            DraftTokensBox.Value = snapshot.DraftTokens;
            ApplyBackendAvailability(backend);
        }
        catch (Exception ex)
        {
            AppendLog($"[aviso] não foi possível restaurar as configurações: {ex.Message}");
        }
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            var snapshot = new SettingsSnapshot
            {
                ModelPath = ModelPathBox.Text.Trim(), ServerPath = ServerPathBox.Text.Trim(), Host = HostBox.Text.Trim(),
                Port = Num(PortBox), MaxContext = Num(MaxContextBox), DefaultTokens = Num(DefaultTokensBox),
                KvDtype = SelectedTag(KvDtypeBox) ?? "int8", KvCapacity = Num(KvCapacityBox), KvAuto = KvAutoBox.IsOn,
                Concurrency = Num(ConcurrencyBox), StatsInterval = Num(StatsIntervalBox), Backend = SelectedTag(SpecBox) ?? "off",
                DraftTokens = Num(DraftTokensBox), LmHeadDraft = LmHeadDraftBox.IsOn, Vision = VisionBox.IsOn,
                NoThinking = NoThinkingBox.IsOn, PreserveThinking = PreserveThinkingBox.IsOn, Cors = CorsBox.IsOn,
            };
            var path = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppendLog($"[aviso] não foi possível salvar as configurações: {ex.Message}");
        }
    }

    private static string GetSettingsPath()
    {
        var settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NInferControl");
        return Path.Combine(settingsDirectory, "settings.json");
    }

    private sealed class ServerConfiguration
    {
        public string ModelPath { get; init; } = string.Empty;
        public string ServerPath { get; init; } = string.Empty;
        public string Host { get; init; } = "127.0.0.1";
        public int Port { get; init; }
        public uint MaxContext { get; init; }
        public int DefaultTokens { get; init; }
        public string KvDtype { get; init; } = "bf16";
        public uint KvCapacity { get; init; }
        public bool KvAuto { get; init; }
        public uint Concurrency { get; init; }
        public uint StatsInterval { get; init; }
        public string Backend { get; init; } = "off";
        public uint DraftTokens { get; init; }
        public bool LmHeadDraft { get; init; }
        public bool Vision { get; init; }
        public bool NoThinking { get; init; }
        public bool PreserveThinking { get; init; }
        public bool Cors { get; init; }
    }

    private sealed class SettingsSnapshot
    {
        public string ModelPath { get; set; } = string.Empty;
        public string ServerPath { get; set; } = string.Empty;
        public string Host { get; set; } = "0.0.0.0";
        public uint Port { get; set; } = 8088;
        public uint MaxContext { get; set; } = 210000;
        public uint DefaultTokens { get; set; } = 80000;
        public string KvDtype { get; set; } = "int8";
        public uint KvCapacity { get; set; } = 210000;
        public bool KvAuto { get; set; }
        public uint Concurrency { get; set; } = 1;
        public uint StatsInterval { get; set; } = 1500;
        public string Backend { get; set; } = "mtp";
        public uint DraftTokens { get; set; } = 3;
        public bool LmHeadDraft { get; set; } = true;
        public bool Vision { get; set; }
        public bool NoThinking { get; set; }
        public bool PreserveThinking { get; set; }
        public bool Cors { get; set; }
    }
}
