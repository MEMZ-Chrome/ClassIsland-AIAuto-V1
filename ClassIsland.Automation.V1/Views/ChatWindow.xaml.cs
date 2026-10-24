using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClassIsland.Automation.Core.Models;
using ClassIsland.Automation.Core.Services;
using ClassIsland.Core.Abstractions.Services;
using Microsoft.Win32;

namespace ClassIsland.Automation.V1.Views;

/// <summary>
/// 聊天窗口仅负责展示与输入：会话状态与进行中的对话回合全部由
/// <see cref="ChatSessionManager"/> 单例持有，因此关闭窗口后任务继续后台执行，
/// 重新打开窗口附着回同一份状态。
/// </summary>
public partial class ChatWindow : Window
{
    private byte[]? _selectedImageBytes;
    private string _imageMimeType = "image/png";
    private readonly IProfileService? _profileService;
    private readonly IUriNavigationService? _uriService;
    private readonly ObservableCollection<ChatMessageItem> _displayMessages = new();
    private PluginSettings _settings = new();
    private bool _isUpdatingModelCombo = false;
    private bool _isSending = false;
    private bool _sidebarExpanded = true;
    private ChatMessageItem? _currentTurnBubble;

    private ChatSessionManager Manager => ChatSessionManager.Instance;

    public ChatWindow(IProfileService? profileService = null, IUriNavigationService? uriService = null)
    {
        InitializeComponent();
        _profileService = profileService;
        _uriService = uriService;

        if (MessagesItemsControl != null)
        {
            MessagesItemsControl.ItemsSource = _displayMessages;
        }

        if (SelectImageButton != null) SelectImageButton.Click += OnSelectImageClicked;
        if (ClearImageButton != null) ClearImageButton.Click += OnClearImageClicked;
        if (SendButton != null) SendButton.Click += (s, e) => SendCurrentMessage();
        if (NewSessionButton != null) NewSessionButton.Click += OnNewSessionClicked;
        if (SidebarNewSessionButton != null) SidebarNewSessionButton.Click += OnNewSessionClicked;
        if (SidebarToggleButton != null) SidebarToggleButton.Click += OnSidebarToggleClicked;

        // 下拉框切换只订阅一次。放在 InitModelSelector 里会随每次外部配置刷新
        // 叠加一个匿名处理器，用户每切一次模型就多并发执行一轮保存与写盘。
        if (ModelSelectComboBox != null)
        {
            ModelSelectComboBox.SelectionChanged += (s, e) =>
            {
                if (_isUpdatingModelCombo) return;
                if (ModelSelectComboBox.SelectedItem is ActiveModelOption selected)
                {
                    _settings.SelectActiveModel(selected.ProviderId, selected.ModelName);
                    var configDir = Plugin.Instance?.PluginConfigFolder ?? AppContext.BaseDirectory;
                    var settingsPath = Path.Combine(configDir, "settings.json");
                    SaveSettingsToFile(_settings, settingsPath);
                }
            };
        }

        // 回车发送，Shift+Enter 换行。
        // 通过隧道阶段 PreviewKeyDown 拦截，避免 TextBox 先插入换行。
        // 中文输入法输入过程中 e.Key 会是 Key.ImeProcessed，此时放行给输入法上屏候选词。
        if (InputTextBox != null)
        {
            InputTextBox.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Enter && e.Key != Key.Return) return;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;

                e.Handled = true;
                SendCurrentMessage();
            };
        }

        _settings = LoadPluginSettings();
        InitModelSelector();

        // 附着到全局会话管理器：状态与进行中的回合都在管理器里存活
        var configDir = Plugin.Instance?.PluginConfigFolder ?? AppContext.BaseDirectory;
        Manager.Initialize(Path.Combine(configDir, "sessions.json"), _profileService, _uriService);
        Manager.IsWindowVisible = true;
        Manager.SessionsChanged += OnSessionsChanged;
        Manager.TurnStarted += OnTurnStarted;
        Manager.TurnProgress += OnTurnProgress;
        Manager.TurnEnded += OnTurnEnded;

        UpdateSidebarVisibility();
        RenderSessionList();
        RebuildDisplayFromContext();

        // 设置页或 AI 工具改了供应商/模型后，下拉框要跟着刷新，
        // 否则会继续持有已被删除的 ActiveModelOption，发送时又把它写回配置。
        SettingsManager.SettingsChanged += OnSettingsChangedExternally;
        Closed += (_, _) =>
        {
            SettingsManager.SettingsChanged -= OnSettingsChangedExternally;
            Manager.SessionsChanged -= OnSessionsChanged;
            Manager.TurnStarted -= OnTurnStarted;
            Manager.TurnProgress -= OnTurnProgress;
            Manager.TurnEnded -= OnTurnEnded;
            Manager.IsWindowVisible = false;
            // 不取消进行中的回合：任务继续后台执行，结果由管理器落盘并按需发系统通知
        };
    }

    // ==================== 管理器事件 → 界面更新 ====================

    private void OnSessionsChanged()
    {
        RenderSessionList();
        RebuildDisplayFromContext();
    }

    private void OnTurnStarted()
    {
        _currentTurnBubble = new ChatMessageItem
        {
            Role = ChatRole.Assistant,
            TimeString = DateTime.Now.ToString("HH:mm"),
            Content = "正在思考中...",
            IsThinking = true
        };
        _displayMessages.Add(_currentTurnBubble);
        ScrollToBottom();
    }

    private void OnTurnProgress(string badge)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _currentTurnBubble?.AddToolBadge(badge);
            ScrollToBottom();
        });
    }

    private void OnTurnEnded()
    {
        Dispatcher.InvokeAsync(() =>
        {
            _currentTurnBubble = null;
            RebuildDisplayFromContext();
            if (SendButton != null) SendButton.IsEnabled = true;
        });
    }

    private void OnSettingsChangedExternally(PluginSettings updated)
    {
        if (_isUpdatingModelCombo) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (_isUpdatingModelCombo) return;
            var previous = (ModelSelectComboBox?.SelectedItem as ActiveModelOption)?.DisplayText;
            InitModelSelector();

            // 尽量恢复用户原先选中的模型
            if (previous != null && ModelSelectComboBox != null)
            {
                var match = (ModelSelectComboBox.ItemsSource as System.Collections.IEnumerable)?
                    .OfType<ActiveModelOption>()
                    .FirstOrDefault(o => o.DisplayText == previous);
                if (match != null) ModelSelectComboBox.SelectedItem = match;
            }

            // 多会话开关可能被外部修改，侧栏显隐与会话列表要跟着刷新
            UpdateSidebarVisibility();
            RenderSessionList();
        });
    }

    private void InitModelSelector()
    {
        if (ModelSelectComboBox == null) return;
        _isUpdatingModelCombo = true;
        try
        {
            _settings.EnsureProvidersInitialized();
            var options = _settings.GetEnabledModelOptions();
            ModelSelectComboBox.ItemsSource = options;

            if (options.Count > 0)
            {
                var matched = options.FirstOrDefault(o =>
                    o.ProviderId == _settings.ActiveProviderId && o.ModelName == _settings.ActiveModelName);
                ModelSelectComboBox.SelectedItem = matched ?? options[0];
                if (matched == null)
                {
                    _settings.SelectActiveModel(options[0].ProviderId, options[0].ModelName);
                }
            }
            else
            {
                ModelSelectComboBox.ItemsSource = new List<string> { "(暂无已勾选模型)" };
                ModelSelectComboBox.SelectedIndex = 0;
            }
        }
        finally
        {
            _isUpdatingModelCombo = false;
        }
    }

    private PluginSettings LoadPluginSettings()
    {
        // 统一走 SettingsManager，保证对话窗口与设置页操作的是同一份配置对象。
        var settings = SettingsManager.Current;
        settings.EnsureProvidersInitialized();
        return settings;
    }

    private void SaveSettingsToFile(PluginSettings settings, string path)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }

    private void ScrollToBottom()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            ChatScrollViewer?.ScrollToEnd();
        }));
    }

    // ==================== 发送 ====================

    private async void SendCurrentMessage()
    {
        var prompt = InputTextBox?.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(prompt) && (_selectedImageBytes == null || _selectedImageBytes.Length == 0))
        {
            return;
        }

        // 防重入：连按回车会并发发起多次请求；后台回合未结束时也不允许新回合
        if (_isSending || Manager.IsTurnInFlight)
        {
            return;
        }
        _isSending = true;
        try
        {
            var settings = SettingsManager.Current;
            if (ModelSelectComboBox?.SelectedItem is ActiveModelOption opt)
            {
                settings.SelectActiveModel(opt.ProviderId, opt.ModelName);
            }

            if (string.IsNullOrWhiteSpace(settings.CurrentModel))
            {
                _displayMessages.Add(new ChatMessageItem
                {
                    Role = ChatRole.System,
                    TimeString = DateTime.Now.ToString("HH:mm"),
                    Content = "未配置可用模型！请先在【设置 -> IslandAgent设置 -> 模型服务】中配置供应商并勾选所需模型。"
                });
                ScrollToBottom();
                return;
            }

            var textContent = string.IsNullOrWhiteSpace(prompt) ? "请分析这张课表图片，并调用相应工具完成课表配置。" : prompt;
            var imageBytes = _selectedImageBytes;
            var imageMime = _imageMimeType;

            _displayMessages.Add(new ChatMessageItem
            {
                Role = ChatRole.User,
                TimeString = DateTime.Now.ToString("HH:mm"),
                Content = textContent
            });
            if (InputTextBox != null) InputTextBox.Text = "";
            OnClearImageClicked(null, null);
            if (SendButton != null) SendButton.IsEnabled = false;

            // 回合在管理器内执行：即使本窗口提前关闭，任务也会继续并落盘
            await Manager.SendUserTurnAsync(textContent, imageBytes, imageMime);
        }
        finally
        {
            _isSending = false;
        }
    }

    // ==================== 展示重建 ====================

    /// <summary>从管理器上下文重建聊天气泡（工具消息只服务于模型上下文，不在界面重放）。</summary>
    private void RebuildDisplayFromContext()
    {
        _displayMessages.Clear();

        foreach (var node in Manager.Context)
        {
            if (node is not JsonObject o) continue;
            var role = o["role"]?.GetValue<string>();
            if (role != "user" && role != "assistant") continue;
            var content = ChatSessionManager.NormalizeContentToString(o["content"]);
            if (string.IsNullOrWhiteSpace(content)) continue;

            _displayMessages.Add(new ChatMessageItem
            {
                Role = role == "user" ? ChatRole.User : ChatRole.Assistant,
                TimeString = "",
                Content = content
            });
        }

        // 失败回合的上下文已被管理器回滚，气泡单独重放，让用户看到失败原因
        if (Manager.LastFailedTurn is { } failed)
        {
            _displayMessages.Add(new ChatMessageItem { Role = ChatRole.User, TimeString = "", Content = failed.UserText });
            _displayMessages.Add(new ChatMessageItem { Role = ChatRole.Assistant, TimeString = "", Content = failed.Error });
        }

        // 空会话显示欢迎语
        if (_displayMessages.Count == 0)
        {
            _displayMessages.Add(new ChatMessageItem
            {
                Role = ChatRole.Assistant,
                TimeString = DateTime.Now.ToString("HH:mm"),
                Content = "你好，我是 ClassIsland AI 助手。"
            });
        }

        // 窗口打开时仍有回合在后台进行：挂上思考气泡接续进度
        if (Manager.IsTurnInFlight)
        {
            _currentTurnBubble = new ChatMessageItem
            {
                Role = ChatRole.Assistant,
                TimeString = DateTime.Now.ToString("HH:mm"),
                Content = "后台任务进行中...",
                IsThinking = true
            };
            _displayMessages.Add(_currentTurnBubble);
        }

        ScrollToBottom();
    }

    // ==================== 图片选择 ====================

    private void OnSelectImageClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择课表图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp;*.bmp|所有文件|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            var filePath = dialog.FileName;

            // 上限 8 MB：图片会被 Base64 后随每次请求发送（体积再涨约 1/3），
            // 手机拍摄的高清课表照片常有 10MB+，不拦截会卡死 UI 线程甚至 OOM。
            const long maxBytes = 8 * 1024 * 1024;
            long fileLength = 0;
            try { fileLength = new FileInfo(filePath).Length; } catch { }
            if (fileLength > maxBytes)
            {
                _selectedImageBytes = null;
                if (ImageStatusTextBlock != null)
                {
                    ImageStatusTextBlock.Text = $"图片过大（{fileLength / 1024 / 1024} MB），请压缩到 8 MB 以内";
                }
                if (ClearImageButton != null) ClearImageButton.Visibility = Visibility.Collapsed;
                return;
            }

            byte[] imageBytes;
            try { imageBytes = File.ReadAllBytes(filePath); }
            catch
            {
                if (ImageStatusTextBlock != null) ImageStatusTextBlock.Text = "读取图片失败";
                return;
            }
            if (imageBytes.Length > maxBytes)
            {
                _selectedImageBytes = null;
                if (ImageStatusTextBlock != null) ImageStatusTextBlock.Text = "图片过大，请压缩到 8 MB 以内";
                if (ClearImageButton != null) ClearImageButton.Visibility = Visibility.Collapsed;
                return;
            }

            _selectedImageBytes = imageBytes;

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            _imageMimeType = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => "image/png"
            };

            var fileName = Path.GetFileName(filePath);
            if (ImageStatusTextBlock != null) ImageStatusTextBlock.Text = $"已选择: {fileName}";
            if (ClearImageButton != null) ClearImageButton.Visibility = Visibility.Visible;
        }
    }

    private void OnClearImageClicked(object? sender, RoutedEventArgs? e)
    {
        _selectedImageBytes = null;
        if (ImageStatusTextBlock != null) ImageStatusTextBlock.Text = "未选择图片";
        if (ClearImageButton != null) ClearImageButton.Visibility = Visibility.Collapsed;
    }

    // ==================== 会话侧栏 ====================

    private void OnNewSessionClicked(object? sender, RoutedEventArgs e)
    {
        // 回合进行中不允许新建/切换/删除，避免与后台执行竞争
        if (Manager.IsTurnInFlight) return;
        Manager.CreateNewSession();
    }

    private void RenderSessionList()
    {
        if (SessionListStackPanel == null) return;
        SessionListStackPanel.Children.Clear();

        foreach (var session in Manager.Sessions.OrderByDescending(s => s.LastActiveAt))
        {
            var isActive = session.Id == Manager.ActiveSessionId;
            var captured = session; // 避免闭包捕获循环变量

            var title = new TextBlock
            {
                Text = session.Title,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            if (isActive) title.FontWeight = FontWeights.SemiBold;
            title.ToolTip = $"{session.Title}（{session.LastActiveAt:MM-dd HH:mm}）";
            title.MouseLeftButtonUp += (_, _) => Manager.ActivateSession(captured.Id);

            var deleteButton = new Button
            {
                Content = "✕",
                FontSize = 11,
                Padding = new Thickness(6, 2, 6, 2)
            };
            deleteButton.Click += (_, _) => Manager.DeleteSession(captured.Id);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(deleteButton, 1);
            grid.Children.Add(title);
            grid.Children.Add(deleteButton);

            var border = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 2, 0, 2)
            };
            if (isActive)
            {
                // 用资源引用而非 TryFindResource 赋值，深浅主题切换时才能跟随更新
                border.SetResourceReference(Border.BackgroundProperty, "MaterialDesignCardBackground");
            }
            else
            {
                border.Background = Brushes.Transparent;
            }
            border.Child = grid;

            SessionListStackPanel.Children.Add(border);
        }
    }

    /// <summary>多会话模式：显示侧栏与「会话列表」开关、隐藏顶部新建按钮；单会话模式反之。</summary>
    private void UpdateSidebarVisibility()
    {
        var multi = _settings.IsMultiSessionEnabled;
        if (SessionSidebarBorder != null)
        {
            SessionSidebarBorder.Visibility = multi && _sidebarExpanded ? Visibility.Visible : Visibility.Collapsed;
        }
        if (SidebarToggleButton != null)
        {
            SidebarToggleButton.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
            SidebarToggleButton.Content = _sidebarExpanded ? "收起会话" : "会话列表";
        }
        if (NewSessionButton != null)
        {
            NewSessionButton.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void OnSidebarToggleClicked(object? sender, RoutedEventArgs e)
    {
        _sidebarExpanded = !_sidebarExpanded;
        UpdateSidebarVisibility();
    }
}
