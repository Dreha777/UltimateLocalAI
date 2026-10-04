using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using UltimateLocalAI.Models;
using UltimateLocalAI.Services;

namespace UltimateLocalAI;

public partial class MainWindow : Window
{
    private readonly ConfigService _configService = new();
    private readonly ChatRepository _chatRepo = new();
    private readonly HardwareDetector _hardwareDetector = new();
    private readonly BackendSelector _backendSelector = new();
    private readonly AutoRuntimeConfigurator _autoRuntimeConfigurator = new();
    private readonly LlamaServerManager _server = new();
    private readonly LlamaApiClient _api = new();
    private readonly FileTextExtractor _extractor = new();
    private readonly KnowledgeBaseService _knowledge;
    private readonly ResourceMonitorService _resourceMonitor = new();
    private readonly System.Windows.Threading.DispatcherTimer _resourceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _resourceRefreshBusy;

    private AppConfig _config;
    private HardwareInfo _hardware = new();
    private ChatSession? _currentChat;
    private CancellationTokenSource? _generationCts;
    private bool _loadingChat;
    private bool _modelStarting;
    private bool _modelReady;
    private int _activeContextSize = 4096;

    public ObservableCollection<ChatSession> Chats { get; } = [];
    public ObservableCollection<UiMessage> Messages { get; } = [];
    public ObservableCollection<AttachmentInfo> PendingAttachments { get; } = [];
    public ObservableCollection<string> BackendModes { get; } = ["Auto", "CPU AVX", "CPU AVX2", "CUDA Pascal", "CUDA Modern", "CUDA Blackwell"];
    public ObservableCollection<string> RuntimeChannels { get; } = ["Stable", "Latest", "Custom"];
    public ObservableCollection<string> CacheTypes { get; } = ["f16", "q8_0", "q5_1", "q5_0", "q4_1", "q4_0"];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _config = _configService.Load();
        _activeContextSize = Math.Max(512, _config.Runtime.ContextSize);
        _knowledge = new KnowledgeBaseService(_extractor);
        _server.StatusChanged += s => Dispatcher.Invoke(() =>
        {
            RuntimeStatusText.Text = s;
            if (s.StartsWith("Backend остановлен", StringComparison.OrdinalIgnoreCase))
            {
                _modelReady = false;
                SendButton.IsEnabled = false;
                UnloadButton.IsEnabled = false;
            }
            if (ModelLoadProgress.Visibility == Visibility.Visible)
                TitleStatusText.Text = s;
        });
        _resourceTimer.Tick += async (_, _) => await RefreshResourceUsageAsync();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            TitleStatusText.Text = "Определение оборудования…";
            _hardware = await _hardwareDetector.DetectAsync();
            HardwareText.Text = _hardware.ShortSummary;
            TitleStatusText.Text = "Готов";
            LoadSettingsToUi();
            ApplyApplicationTheme(_config.WorkspaceBackground);
            RefreshModelHeader();
            ReloadChats();
            if (Chats.Count > 0) ChatsList.SelectedItem = Chats[0];
            else CreateAndSelectChat();

            _resourceTimer.Start();
            await RefreshResourceUsageAsync();

            if (_config.AutoStartLastModel && File.Exists(_config.ModelPath))
                await StartModelInternalAsync(showErrors: false);
        }
        catch (Exception ex)
        {
            LogService.Error("Startup failed", ex);
            MessageBox.Show(ex.Message, "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _resourceTimer.Stop();
        try { _configService.Save(_config); } catch { }
        _generationCts?.Cancel();
        await _server.StopAsync();
    }

    private void ReloadChats()
    {
        Chats.Clear();
        foreach (var chat in _chatRepo.GetChats()) Chats.Add(chat);
    }

    private void CreateAndSelectChat()
    {
        var chat = _chatRepo.CreateChat(_config.ModelPath);
        Chats.Insert(0, chat);
        ChatsList.SelectedItem = chat;
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _generationCts?.Cancel();
        CreateAndSelectChat();
        PromptBox.Focus();
    }

    private void ChatsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingChat || ChatsList.SelectedItem is not ChatSession chat) return;
        _generationCts?.Cancel();
        LoadChat(chat);
    }

    private void LoadChat(ChatSession chat)
    {
        _currentChat = chat;
        Messages.Clear();
        foreach (var m in _chatRepo.GetMessages(chat.Id))
        {
            Messages.Add(new UiMessage
            {
                Role = m.Role,
                Content = m.Content,
                AttachmentSummary = m.Attachments.Count == 0 ? "" : "📎 " + string.Join(" · ", m.Attachments.Select(a => a.FileName)),
                CreatedAt = m.CreatedAt
            });
        }
        if (!string.IsNullOrWhiteSpace(chat.ModelPath) && File.Exists(chat.ModelPath))
        {
            _config.ModelPath = chat.ModelPath;
            RefreshModelHeader();
        }
        ScrollToBottom();
    }

    private void ChatMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ChatSession chat) return;
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "Переименовать" };
        rename.Click += (_, _) =>
        {
            var dialog = new TextPromptWindow("Название чата", "Введите новое название:", chat.Title) { Owner = this };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResultText))
            {
                _chatRepo.RenameChat(chat.Id, dialog.ResultText.Trim());
                ReloadChatsAndReselect(chat.Id);
            }
        };
        var export = new MenuItem { Header = "Экспорт в Markdown" };
        export.Click += (_, _) => ExportChat(chat);
        var delete = new MenuItem { Header = "Удалить" };
        delete.Click += (_, _) =>
        {
            if (MessageBox.Show($"Удалить чат «{chat.Title}»?", "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _chatRepo.DeleteChat(chat.Id);
            ReloadChats();
            if (Chats.Count > 0) ChatsList.SelectedItem = Chats[0]; else CreateAndSelectChat();
        };
        menu.Items.Add(rename); menu.Items.Add(export); menu.Items.Add(new Separator()); menu.Items.Add(delete);
        btn.ContextMenu = menu; menu.IsOpen = true;
    }


    private void ExportChat(ChatSession chat)
    {
        try
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
                FileName = string.Concat(chat.Title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)) + ".md"
            };
            if (dlg.ShowDialog(this) != true) return;
            var sb = new StringBuilder();
            sb.AppendLine($"# {chat.Title}").AppendLine();
            sb.AppendLine($"Экспорт: {DateTime.Now:yyyy-MM-dd HH:mm}").AppendLine();
            foreach (var m in _chatRepo.GetMessages(chat.Id))
            {
                sb.AppendLine(m.Role == "user" ? "## Вы" : "## Локальный ИИ");
                sb.AppendLine().AppendLine(m.Content).AppendLine();
                if (m.Attachments.Count > 0) sb.AppendLine("Файлы: " + string.Join(", ", m.Attachments.Select(a => a.FileName))).AppendLine();
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            RuntimeStatusText.Text = "Чат экспортирован";
        }
        catch (Exception ex)
        {
            LogService.Error("Chat export failed", ex);
            MessageBox.Show(ex.Message, "Экспорт", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ReloadChatsAndReselect(string id)
    {
        _loadingChat = true;
        try
        {
            ReloadChats();
            ChatsList.SelectedItem = Chats.FirstOrDefault(x => x.Id == id);
        }
        finally { _loadingChat = false; }
        if (ChatsList.SelectedItem is ChatSession c) LoadChat(c);
    }

    private void SelectModel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "GGUF модели (*.gguf)|*.gguf|Все файлы (*.*)|*.*", Title = "Выберите локальную GGUF-модель" };
        if (File.Exists(_config.ModelPath)) dlg.InitialDirectory = Path.GetDirectoryName(_config.ModelPath);
        if (dlg.ShowDialog(this) != true) return;
        _config.ModelPath = dlg.FileName;
        _configService.Save(_config);
        if (_currentChat is not null)
        {
            _currentChat.ModelPath = dlg.FileName;
            _chatRepo.UpdateModelPath(_currentChat.Id, dlg.FileName);
        }
        RefreshModelHeader();
    }

    private void RefreshModelHeader()
    {
        if (!File.Exists(_config.ModelPath))
        {
            ModelNameText.Text = "Модель не выбрана";
            BackendText.Text = "Укажите путь к GGUF — файл никуда не копируется";
            return;
        }
        var fi = new FileInfo(_config.ModelPath);
        ModelNameText.Text = fi.Name;
        BackendText.Text = $"{fi.Length / 1024d / 1024d / 1024d:0.00} ГБ · {fi.DirectoryName}";
    }

    private async void StartModel_Click(object sender, RoutedEventArgs e) => await StartModelInternalAsync(showErrors: true);

    private async void UnloadModel_Click(object sender, RoutedEventArgs e)
    {
        _generationCts?.Cancel();
        _modelReady = false;
        StartButton.IsEnabled = false;
        UnloadButton.IsEnabled = false;
        SendButton.IsEnabled = false;
        ModelLoadProgress.Visibility = Visibility.Collapsed;
        TitleStatusText.Text = "Выгрузка модели…";
        RuntimeStatusText.Text = "Остановка llama-server…";

        try
        {
            await _server.StopAsync();
            TitleStatusText.Text = "Модель выгружена";
            RuntimeStatusText.Text = "Backend не запущен";
            StartButton.Content = "Запустить";
            RefreshModelHeader();
            PerfText.Text = "";
        }
        catch (Exception ex)
        {
            LogService.Error("Model unload failed", ex);
            TitleStatusText.Text = "Ошибка выгрузки";
            RuntimeStatusText.Text = "Ошибка остановки backend";
            MessageBox.Show(ex.Message, "Не удалось выгрузить модель", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            StartButton.IsEnabled = true;
            UnloadButton.IsEnabled = _server.IsRunning;
            SendButton.IsEnabled = _server.IsRunning && _generationCts is null;
        }
    }

    private string DescribeActiveBackend(BackendChoice backend)
    {
        if (backend.UsesCuda)
        {
            var offload = _server.GpuOffloadSummary;
            var detail = string.IsNullOrWhiteSpace(offload)
                ? "CUDA backend активен; число GPU-слоёв см. в Logs"
                : offload;
            return $"Активный backend: {backend.Name} · GPU: {_hardware.GpuName} · {detail}";
        }

        return $"Активный backend: {backend.Name} · вычисления на CPU";
    }

    private async Task<bool> StartModelInternalAsync(bool showErrors)
    {
        if (_modelStarting)
        {
            RuntimeStatusText.Text = "Модель уже загружается…";
            return false;
        }

        if (!File.Exists(_config.ModelPath))
        {
            if (showErrors) MessageBox.Show("Сначала выберите GGUF-модель.", "Модель", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        ApplySettingsFromUi(save: true);
        _modelStarting = true;
        _modelReady = false;
        StartButton.IsEnabled = false;
        SendButton.IsEnabled = false;
        UnloadButton.IsEnabled = false;
        ModelLoadProgress.Visibility = Visibility.Visible;
        PerfText.Text = "";
        BackendChoice? activeBackend = null;
        try
        {
            var backend = _backendSelector.Select(_hardware, _config);
            activeBackend = backend;
            var launchConfig = CreateLaunchConfig(backend);
            TitleStatusText.Text = "Загрузка модели…";
            BackendText.Text = backend.Name + " · " + backend.Reason;
            try
            {
                await _server.StartAsync(backend, launchConfig);
            }
            catch (Exception ex) when (backend.UsesCuda && _config.AutoFallbackToCpu)
            {
                LogService.Warn("CUDA backend failed, CPU fallback: " + ex.Message);
                var cpu = _backendSelector.CpuFallback(_hardware, _config);
                activeBackend = cpu;
                launchConfig = CreateLaunchConfig(cpu);
                BackendText.Text = $"{cpu.Name} · CUDA не запустилась, применён безопасный CPU fallback";
                await _server.StartAsync(cpu, launchConfig);
            }
            _modelReady = true;
            TitleStatusText.Text = "Модель готова";
            ModelLoadProgress.Visibility = Visibility.Collapsed;
            StartButton.Content = "Перезапустить";
            UnloadButton.IsEnabled = true;
            SendButton.IsEnabled = true;
            if (activeBackend is not null)
                BackendText.Text = DescribeActiveBackend(activeBackend);
            PromptBox.Focus();
            return true;
        }
        catch (Exception ex)
        {
            _modelReady = false;
            LogService.Error("Model start failed", ex);
            await _server.StopAsync();
            TitleStatusText.Text = "Ошибка загрузки";
            RuntimeStatusText.Text = "Backend не запущен";
            ModelLoadProgress.Visibility = Visibility.Collapsed;
            StartButton.Content = "Запустить";
            UnloadButton.IsEnabled = false;
            SendButton.IsEnabled = false;
            if (showErrors)
            {
                var hint = ex is FileNotFoundException && ex.Message.Contains("Backend", StringComparison.OrdinalIgnoreCase)
                    ? "\n\nСоберите backend'ы скриптом BUILD.cmd."
                    : "\n\nПодробности: Logs\\localai_YYYYMMDD.log";
                MessageBox.Show(ex.Message + hint, "Не удалось запустить модель", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return false;
        }
        finally
        {
            _modelStarting = false;
            StartButton.IsEnabled = true;
        }
    }

    private AppConfig CreateLaunchConfig(BackendChoice backend)
    {
        if (!string.Equals(_config.BackendMode, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            _activeContextSize = Math.Max(512, _config.Runtime.ContextSize);
            LogService.Info(
                $"MANUAL-RUNTIME backend=\"{backend.Name}\" context={_config.Runtime.ContextSize} " +
                $"threads={_config.Runtime.Threads} threads_batch={_config.Runtime.ThreadsBatch} " +
                $"batch={_config.Runtime.BatchSize} ubatch={_config.Runtime.UBatchSize} gpu_layers={_config.Runtime.GpuLayers} " +
                $"priority={_config.Runtime.Priority}");
            return _config;
        }

        HardwareDetector.RefreshMemory(_hardware);
        var profile = _autoRuntimeConfigurator.Create(_hardware, backend, _config.Runtime, _config.ModelPath);
        _activeContextSize = profile.Runtime.ContextSize;
        LogService.Info(AutoRuntimeConfigurator.ToLogLine(_hardware, backend, profile));

        return new AppConfig
        {
            ModelPath = _config.ModelPath,
            BackendMode = _config.BackendMode,
            RuntimeChannel = _config.RuntimeChannel,
            CustomRuntimePath = _config.CustomRuntimePath,
            Port = _config.Port,
            AutoStartLastModel = _config.AutoStartLastModel,
            AutoFallbackToCpu = _config.AutoFallbackToCpu,
            UseKnowledgeBase = _config.UseKnowledgeBase,
            WorkspaceBackground = _config.WorkspaceBackground,
            Runtime = profile.Runtime,
            Generation = _config.Generation
        };
    }

    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Поддерживаемые файлы|*.pdf;*.docx;*.xlsx;*.txt;*.md;*.csv;*.json;*.xml;*.yaml;*.yml;*.cs;*.cpp;*.c;*.h;*.hpp;*.py;*.js;*.ts;*.html;*.css;*.sql;*.java;*.go;*.rs;*.ps1;*.bat;*.log|Все файлы|*.*",
            Title = "Прикрепить файлы к сообщению"
        };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var file in dlg.FileNames)
        {
            try
            {
                RuntimeStatusText.Text = "Чтение: " + Path.GetFileName(file);
                var a = await _extractor.ExtractAsync(file);
                PendingAttachments.Add(a);
            }
            catch (Exception ex)
            {
                LogService.Error("Attachment extraction failed", ex);
                MessageBox.Show($"{Path.GetFileName(file)}: {ex.Message}", "Файл не добавлен", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        RuntimeStatusText.Text = _server.IsRunning ? "Модель готова" : "Backend не запущен";
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_generationCts is not null)
        {
            _generationCts.Cancel();
            return;
        }
        await SendCurrentAsync();
    }

    private async Task SendCurrentAsync()
    {
        var prompt = PromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt) && PendingAttachments.Count == 0) return;
        if (_modelStarting)
        {
            RuntimeStatusText.Text = "Дождитесь завершения загрузки модели…";
            return;
        }
        if ((!_server.IsRunning || !_modelReady) && !await StartModelInternalAsync(showErrors: true)) return;
        if (_currentChat is null) CreateAndSelectChat();
        if (_currentChat is null) return;

        var attachments = PendingAttachments.ToList();
        var context = new StringBuilder();
        var contextBudgetChars = Math.Max(4_000, _activeContextSize * 2);
        var remainingChars = contextBudgetChars;
        foreach (var a in attachments)
        {
            if (remainingChars <= 0) break;
            var header = $"\n### Файл: {a.FileName}\n";
            var take = Math.Min(a.ExtractedText.Length, Math.Max(0, remainingChars - header.Length));
            context.Append(header);
            context.Append(a.ExtractedText.AsSpan(0, take));
            if (take < a.ExtractedText.Length) context.Append("\n[Файл сокращён для текущего контекста; для полного документа добавьте его в Базу знаний]");
            context.AppendLine();
            remainingChars = contextBudgetChars - context.Length;
        }
        if (_config.UseKnowledgeBase && !string.IsNullOrWhiteSpace(prompt) && remainingChars > 1000)
        {
            var hits = _knowledge.Search(prompt, 5);
            if (hits.Count > 0)
            {
                context.AppendLine("\n### Фрагменты локальной базы знаний");
                foreach (var hit in hits)
                {
                    remainingChars = contextBudgetChars - context.Length;
                    if (remainingChars <= 500) break;
                    var block = $"\nИсточник: {Path.GetFileName(hit.SourcePath)}\n{hit.Content}\n";
                    context.Append(block.AsSpan(0, Math.Min(block.Length, remainingChars)));
                }
            }
        }

        var userMessage = new ChatMessage
        {
            ChatId = _currentChat.Id,
            Role = "user",
            Content = string.IsNullOrWhiteSpace(prompt) ? "Проанализируй прикреплённые материалы." : prompt,
            ContextText = context.ToString(),
            CreatedAt = DateTime.Now,
            Attachments = attachments
        };
        _chatRepo.SaveMessage(userMessage);
        Messages.Add(new UiMessage { Role = "user", Content = userMessage.Content, CreatedAt = userMessage.CreatedAt, AttachmentSummary = attachments.Count == 0 ? "" : "📎 " + string.Join(" · ", attachments.Select(x => x.FileName)) });

        if (_currentChat.Title == "Новый чат")
        {
            var title = MakeChatTitle(userMessage.Content);
            _chatRepo.RenameChat(_currentChat.Id, title);
            _currentChat.Title = title;
            ReloadChatsAndReselect(_currentChat.Id);
        }

        PromptBox.Clear(); PendingAttachments.Clear(); ScrollToBottom();
        var assistantUi = new UiMessage { Role = "assistant", Content = "", CreatedAt = DateTime.Now };
        Messages.Add(assistantUi);
        ScrollToBottom();

        _generationCts = new CancellationTokenSource();
        SendButton.Content = "■";
        SendButton.IsEnabled = true;
        StartButton.IsEnabled = false;
        RuntimeStatusText.Text = "Генерация ответа…";

        var sw = Stopwatch.StartNew();
        var pendingText = new StringBuilder();
        var pendingLock = new object();
        var nextScrollAtMs = 0L;
        var streamTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };

        void FlushPendingText(bool forceScroll = false)
        {
            string chunk;
            lock (pendingLock)
            {
                if (pendingText.Length == 0) return;
                chunk = pendingText.ToString();
                pendingText.Clear();
            }

            assistantUi.Content += chunk;

            if (forceScroll || sw.ElapsedMilliseconds >= nextScrollAtMs)
            {
                ScrollToBottom();
                nextScrollAtMs = sw.ElapsedMilliseconds + 250;
            }
        }

        streamTimer.Tick += (_, _) => FlushPendingText();
        streamTimer.Start();

        try
        {
            var history = _chatRepo.GetMessages(_currentChat.Id);
            await _api.StreamChatAsync(_config.Port, history, _config.Generation, delta =>
            {
                lock (pendingLock)
                    pendingText.Append(delta);
            }, _generationCts.Token);

            FlushPendingText(forceScroll: true);
        }
        catch (OperationCanceledException)
        {
            FlushPendingText(forceScroll: true);
            if (string.IsNullOrWhiteSpace(assistantUi.Content)) assistantUi.Content = "[Генерация остановлена]";
        }
        catch (Exception ex)
        {
            FlushPendingText(forceScroll: true);
            LogService.Error("Generation failed", ex);
            assistantUi.Content = string.IsNullOrWhiteSpace(assistantUi.Content) ? "Ошибка генерации: " + ex.Message : assistantUi.Content + "\n\n[Ошибка: " + ex.Message + "]";
        }
        finally
        {
            streamTimer.Stop();
            FlushPendingText(forceScroll: true);
            sw.Stop();
            if (!string.IsNullOrWhiteSpace(assistantUi.Content))
            {
                _chatRepo.SaveMessage(new ChatMessage { ChatId = _currentChat.Id, Role = "assistant", Content = assistantUi.Content, CreatedAt = DateTime.Now });
                var tokens = await _api.TokenCountAsync(_config.Port, assistantUi.Content);
                PerfText.Text = $"≈ {tokens / Math.Max(0.01, sw.Elapsed.TotalSeconds):0.00} ток/с · {sw.Elapsed.TotalSeconds:0.0} с";
            }
            _generationCts.Dispose(); _generationCts = null;
            SendButton.Content = "➤";
            SendButton.IsEnabled = _server.IsRunning && _modelReady;
            StartButton.IsEnabled = true;
            RuntimeStatusText.Text = _server.IsRunning && _modelReady ? "Модель готова" : "Backend остановлен";
            ScrollToBottom();
        }
    }

    private void PromptBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            _ = SendCurrentAsync();
        }
    }

    private void ScrollToBottom() => Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd());
    private static string MakeChatTitle(string text)
    {
        var s = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 42 ? s[..42] + "…" : s;
    }

    private void WorkspaceBackgroundBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorkspaceBackgroundBox.SelectedItem is ComboBoxItem item)
            ApplyApplicationTheme(item.Content?.ToString());
    }

    private void ApplyApplicationTheme(string? mode)
    {
        var palette = mode switch
        {
            "Белый" => ("#FFFFFF", "#F5F5F7", "#FAFAFA", "#FFFFFF"),
            "Тёплый" => ("#F6F1E7", "#EEE6D8", "#F3EDE3", "#FFFDF8"),
            "Холодный" => ("#EEF3F8", "#E2EAF2", "#E8EFF6", "#F8FBFE"),
            _ => ("#F5F5F7", "#ECECF1", "#ECECF1", "#FFFFFF")
        };

        SetBrushColor("BgBrush", palette.Item1);
        SetBrushColor("SidebarBrush", palette.Item2);
        SetBrushColor("PanelBrush", palette.Item4);
        Background = BrushFromHex(palette.Item1);
        ChatSurface.Background = BrushFromHex(palette.Item3);
    }

    private static void SetBrushColor(string key, string hex)
    {
        if (Application.Current.Resources[key] is System.Windows.Media.SolidColorBrush brush)
            brush.Color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
    }

    private static System.Windows.Media.Brush BrushFromHex(string hex) =>
        (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;

    private async Task RefreshResourceUsageAsync()
    {
        if (_resourceRefreshBusy) return;
        _resourceRefreshBusy = true;
        try
        {
            var snapshot = await _resourceMonitor.ReadAsync(_hardware);
            if (snapshot.RamTotalMb > 0)
            {
                _hardware.TotalRamMb = snapshot.RamTotalMb;
                _hardware.AvailableRamMb = snapshot.RamAvailableMb;
                RamUsageBar.Value = Math.Clamp(snapshot.RamPercent, 0, 100);
                RamUsageText.Text = $"{snapshot.RamUsedMb / 1024.0:0.0}/{snapshot.RamTotalMb / 1024.0:0.0} ГБ";
            }

            if (snapshot.CpuPercent >= 0)
            {
                CpuUsageBar.Value = snapshot.CpuPercent;
                CpuUsageText.Text = $"{snapshot.CpuPercent:0}%";
            }

            if (snapshot.GpuPercent >= 0)
            {
                GpuUsageBar.Value = snapshot.GpuPercent;
                var temp = snapshot.GpuTemperatureC >= 0 ? $" · {snapshot.GpuTemperatureC}°C" : "";
                var vram = snapshot.VramTotalMb > 0
                    ? $" · {snapshot.VramUsedMb / 1024.0:0.0}/{snapshot.VramTotalMb / 1024.0:0.0} ГБ"
                    : "";
                GpuUsageText.Text = $"{snapshot.GpuPercent:0}%{vram}{temp}";
            }
            else
            {
                GpuUsageBar.Value = 0;
                GpuUsageText.Text = _hardware.NvidiaDetected ? "нет данных" : "CPU режим";
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("Resource monitor: " + ex.Message);
        }
        finally
        {
            _resourceRefreshBusy = false;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) { LoadSettingsToUi(); SettingsPanel.Visibility = Visibility.Visible; }
    private void CloseSettings_Click(object sender, RoutedEventArgs e) => SettingsPanel.Visibility = Visibility.Collapsed;

    private void LoadSettingsToUi()
    {
        RuntimeChannelBox.SelectedItem = RuntimeChannels.Contains(_config.RuntimeChannel) ? _config.RuntimeChannel : "Stable";
        CustomRuntimePathBox.Text = _config.CustomRuntimePath;
        BackendModeBox.SelectedItem = BackendModes.Contains(_config.BackendMode) ? _config.BackendMode : "Auto";
        ContextBox.Text = _config.Runtime.ContextSize.ToString();
        ThreadsBox.Text = _config.Runtime.Threads.ToString();
        ThreadsBatchBox.Text = _config.Runtime.ThreadsBatch.ToString();
        GpuLayersBox.Text = _config.Runtime.GpuLayers;
        BatchBox.Text = _config.Runtime.BatchSize.ToString();
        UBatchBox.Text = _config.Runtime.UBatchSize.ToString();
        SelectComboByText(FlashBox, _config.Runtime.FlashAttention);
        CacheKBox.SelectedItem = _config.Runtime.CacheTypeK;
        CacheVBox.SelectedItem = _config.Runtime.CacheTypeV;
        SelectComboByText(ReasoningModeBox, _config.Runtime.ReasoningMode);
        SelectComboByText(ReasoningEffortBox, _config.Runtime.ReasoningEffort);
        TemperatureBox.Text = _config.Generation.Temperature.ToString(CultureInfo.CurrentCulture);
        TopPBox.Text = _config.Generation.TopP.ToString(CultureInfo.CurrentCulture);
        TopKBox.Text = _config.Generation.TopK.ToString();
        MinPBox.Text = _config.Generation.MinP.ToString(CultureInfo.CurrentCulture);
        RepeatPenaltyBox.Text = _config.Generation.RepeatPenalty.ToString(CultureInfo.CurrentCulture);
        MaxTokensBox.Text = _config.Generation.MaxTokens.ToString();
        SeedBox.Text = _config.Generation.Seed.ToString();
        SystemPromptBox.Text = _config.Generation.SystemPrompt;
        SelectComboByText(WorkspaceBackgroundBox, _config.WorkspaceBackground);
        KnowledgeCheck.IsChecked = _config.UseKnowledgeBase;
    }

    private void ApplySettingsFromUi(bool save)
    {
        _config.RuntimeChannel = RuntimeChannelBox.SelectedItem?.ToString() ?? "Stable";
        _config.CustomRuntimePath = CustomRuntimePathBox.Text.Trim();
        _config.BackendMode = BackendModeBox.SelectedItem?.ToString() ?? "Auto";
        _config.Runtime.ContextSize = ParseInt(ContextBox.Text, _config.Runtime.ContextSize, 512, 1_048_576);
        _config.Runtime.Threads = ParseInt(ThreadsBox.Text, _config.Runtime.Threads, 1, 512);
        _config.Runtime.ThreadsBatch = ParseInt(ThreadsBatchBox.Text, _config.Runtime.ThreadsBatch, 1, 512);
        _config.Runtime.GpuLayers = string.IsNullOrWhiteSpace(GpuLayersBox.Text) ? "auto" : GpuLayersBox.Text.Trim();
        _config.Runtime.BatchSize = ParseInt(BatchBox.Text, _config.Runtime.BatchSize, 32, 8192);
        _config.Runtime.UBatchSize = ParseInt(UBatchBox.Text, _config.Runtime.UBatchSize, 16, 8192);
        _config.Runtime.FlashAttention = (FlashBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "auto";
        _config.Runtime.CacheTypeK = CacheKBox.SelectedItem?.ToString() ?? "q8_0";
        _config.Runtime.CacheTypeV = CacheVBox.SelectedItem?.ToString() ?? "q8_0";
        _config.Runtime.ReasoningMode = (ReasoningModeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "auto";
        _config.Runtime.ReasoningEffort = (ReasoningEffortBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "default";
        _config.Generation.Temperature = ParseDouble(TemperatureBox.Text, _config.Generation.Temperature, 0, 2);
        _config.Generation.TopP = ParseDouble(TopPBox.Text, _config.Generation.TopP, 0, 1);
        _config.Generation.TopK = ParseInt(TopKBox.Text, _config.Generation.TopK, 0, 1000);
        _config.Generation.MinP = ParseDouble(MinPBox.Text, _config.Generation.MinP, 0, 1);
        _config.Generation.RepeatPenalty = ParseDouble(RepeatPenaltyBox.Text, _config.Generation.RepeatPenalty, 0.5, 2);
        _config.Generation.MaxTokens = ParseInt(MaxTokensBox.Text, _config.Generation.MaxTokens, 16, 131072);
        _config.Generation.Seed = ParseInt(SeedBox.Text, _config.Generation.Seed, -1, int.MaxValue);
        _config.Generation.SystemPrompt = SystemPromptBox.Text.Trim();
        _config.WorkspaceBackground = (WorkspaceBackgroundBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Светло-серый";
        _config.UseKnowledgeBase = KnowledgeCheck.IsChecked == true;
        ApplyApplicationTheme(_config.WorkspaceBackground);
        if (save) _configService.Save(_config);
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi(save: true);
        SettingsPanel.Visibility = Visibility.Collapsed;
        RuntimeStatusText.Text = _server.IsRunning ? "Настройки сохранены. Для runtime-параметров перезапустите модель." : "Настройки сохранены";
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        _config.Runtime = new RuntimeSettings
        {
            ContextSize = _hardware.TotalRamMb > 24_000 ? 8192 : 4096,
            Threads = Math.Max(1, _hardware.LogicalProcessors - 2),
            ThreadsBatch = Math.Max(1, _hardware.LogicalProcessors),
            GpuLayers = "auto", BatchSize = 512, UBatchSize = 256, FlashAttention = "auto", CacheTypeK = "q8_0", CacheTypeV = "q8_0"
        };
        _config.Generation = new GenerationSettings();
        _config.BackendMode = "Auto";
        _config.RuntimeChannel = "Stable";
        _config.CustomRuntimePath = "";
        _config.WorkspaceBackground = "Светло-серый";
        LoadSettingsToUi();
        ApplyWorkspaceBackground(_config.WorkspaceBackground);
    }


    private async void Benchmark_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUi(save: true);
        if (_modelStarting) return;
        if ((!_server.IsRunning || !_modelReady) && !await StartModelInternalAsync(showErrors: true)) return;
        RuntimeStatusText.Text = "Тест скорости…";
        var test = new ChatMessage { Role = "user", Content = "Кратко перечисли числа от 1 до 100 словами через запятую. Не добавляй пояснений." };
        var settings = new GenerationSettings
        {
            Temperature = 0.0, TopP = 1.0, TopK = 1, MinP = 0.0, RepeatPenalty = 1.0,
            MaxTokens = 160, Seed = 42, SystemPrompt = "Отвечай только на поставленную задачу."
        };
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await _api.StreamChatAsync(_config.Port, new[] { test }, settings, d => sb.Append(d), cts.Token);
            sw.Stop();
            var tokens = await _api.TokenCountAsync(_config.Port, sb.ToString());
            var speed = tokens / Math.Max(0.01, sw.Elapsed.TotalSeconds);
            RuntimeStatusText.Text = $"Тест: ≈ {speed:0.00} ток/с";
            MessageBox.Show($"Результат на текущей конфигурации:\n\nВремя: {sw.Elapsed.TotalSeconds:0.0} с\nТокенов ответа: {tokens}\nСкорость: ≈ {speed:0.00} ток/с\n\nЭто измерение именно этого ПК, backend и модели.", "Тест скорости", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LogService.Error("Benchmark failed", ex);
            RuntimeStatusText.Text = "Тест скорости завершился ошибкой";
            MessageBox.Show(ex.Message, "Тест скорости", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Knowledge_Click(object sender, RoutedEventArgs e)
    {
        var win = new KnowledgeBaseWindow(_knowledge) { Owner = this };
        win.ShowDialog();
    }

    private static int ParseInt(string? text, int fallback, int min, int max) => int.TryParse(text, out var v) ? Math.Clamp(v, min, max) : fallback;
    private static double ParseDouble(string? text, double fallback, double min, double max)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) || double.TryParse(text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            return Math.Clamp(v, min, max);
        return fallback;
    }
    private static void SelectComboByText(ComboBox box, string text)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>()) if (string.Equals(item.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase)) { box.SelectedItem = item; return; }
        box.SelectedIndex = 0;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize(); else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
