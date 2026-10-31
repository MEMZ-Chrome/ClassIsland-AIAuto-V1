using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;
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
        if (SidebarToggleButton != null) SidebarToggleButton.Click += OnSidebarExpandClicked;
        if (SidebarCollapseButton != null) SidebarCollapseButton.Click += OnSidebarCollapseClicked;

        // 整窗接收拖拽：图片作为附件，文本文件内容填进输入框
        AllowDrop = true;
        DragOver += OnWindowDragOver;
        DragLeave += OnWindowDragLeave;
        Drop += OnWindowDrop;

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
        // 侧栏初始状态取设置里的「默认展开」，用户本次的展开/收起不会被记住
        _sidebarExpanded = _settings.IsSessionSidebarExpandedByDefault;
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

    // ==================== 图片选择 / 拖拽导入 ====================

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };

    private static readonly string[] TextExtensions =
    {
        ".txt", ".md", ".markdown", ".json", ".csv", ".xml", ".yml", ".yaml", ".log", ".cs", ".xaml", ".axaml"
    };

    /// <summary>文本文件直接塞进输入框，体积上限 256 KB，避免把超长日志灌进上下文。</summary>
    private const long MaxTextBytes = 256 * 1024;

    private void SetImageStatus(string text)
    {
        if (ImageStatusTextBlock != null) ImageStatusTextBlock.Text = text;
    }

    private void OnSelectImageClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择课表图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp;*.bmp|所有文件|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            TryAttachImage(dialog.FileName);
        }
    }

    /// <summary>
    /// 把图片读入待发送区。上限 8 MB：图片会被 Base64 后随每次请求发送（体积再涨约 1/3），
    /// 手机拍摄的高清课表照片常有 10MB+，不拦截会卡死 UI 线程甚至 OOM。
    /// </summary>
    private bool TryAttachImage(string filePath)
    {
        const long maxBytes = 8 * 1024 * 1024;

        long fileLength = 0;
        try { fileLength = new FileInfo(filePath).Length; } catch { }
        if (fileLength > maxBytes)
        {
            _selectedImageBytes = null;
            SetImageStatus($"图片过大（{fileLength / 1024 / 1024} MB），请压缩到 8 MB 以内");
            if (ClearImageButton != null) ClearImageButton.Visibility = Visibility.Collapsed;
            return false;
        }

        byte[] imageBytes;
        try { imageBytes = File.ReadAllBytes(filePath); }
        catch
        {
            SetImageStatus("读取图片失败");
            return false;
        }
        if (imageBytes.Length > maxBytes)
        {
            _selectedImageBytes = null;
            SetImageStatus("图片过大，请压缩到 8 MB 以内");
            if (ClearImageButton != null) ClearImageButton.Visibility = Visibility.Collapsed;
            return false;
        }

        _selectedImageBytes = imageBytes;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        _imageMimeType = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "image/png"
        };

        SetImageStatus($"已选择: {Path.GetFileName(filePath)}");
        if (ClearImageButton != null) ClearImageButton.Visibility = Visibility.Visible;
        return true;
    }

    private void OnClearImageClicked(object? sender, RoutedEventArgs? e)
    {
        _selectedImageBytes = null;
        SetImageStatus("未选择图片，可直接把文件拖进窗口");
        if (ClearImageButton != null) ClearImageButton.Visibility = Visibility.Collapsed;
    }

    // ---- 拖拽导入 ----

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        var accepts = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        if (DropOverlay != null)
        {
            DropOverlay.Visibility = accepts ? Visibility.Visible : Visibility.Collapsed;
        }
        e.Handled = true;
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        if (DropOverlay != null) DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (DropOverlay != null) DropOverlay.Visibility = Visibility.Collapsed;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        if (paths.Length > 1)
        {
            SetImageStatus($"一次只能拖入一个文件（收到 {paths.Length} 个）");
            return;
        }

        HandleDroppedFile(paths[0]);
    }

    private void HandleDroppedFile(string path)
    {
        if (Directory.Exists(path))
        {
            SetImageStatus("文件夹暂不支持，请拖入单个文件");
            return;
        }
        if (!File.Exists(path))
        {
            SetImageStatus("这个文件不存在");
            return;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ImageExtensions.Contains(ext))
        {
            TryAttachImage(path);
            return;
        }
        if (TextExtensions.Contains(ext))
        {
            InsertTextFile(path);
            return;
        }

        SetImageStatus($"不支持的类型「{(string.IsNullOrEmpty(ext) ? "无扩展名" : ext)}」，可拖入图片或文本文件");
    }

    private void InsertTextFile(string path)
    {
        long length = 0;
        try { length = new FileInfo(path).Length; } catch { }
        if (length > MaxTextBytes)
        {
            SetImageStatus($"文件超过 256 KB（{length / 1024} KB），请截取需要的部分");
            return;
        }

        string content;
        try { content = File.ReadAllText(path); }
        catch
        {
            SetImageStatus("读取文件失败，可能不是纯文本");
            return;
        }

        var name = Path.GetFileName(path);
        var block = $"【文件 {name}】\n{content.Trim()}";
        if (InputTextBox != null)
        {
            var existing = InputTextBox.Text ?? "";
            var merged = string.IsNullOrWhiteSpace(existing) ? block : existing.TrimEnd() + "\n\n" + block;
            InputTextBox.Text = merged;
            InputTextBox.CaretIndex = merged.Length;
            InputTextBox.Focus();
        }
        SetImageStatus($"已把「{name}」的内容填进输入框");
    }

    // ==================== 会话侧栏 ====================

    private void OnNewSessionClicked(object? sender, RoutedEventArgs e)
    {
        // 回合进行中不允许新建/切换/删除，避免与后台执行竞争
        if (Manager.IsTurnInFlight) return;
        Manager.CreateNewSession();
    }

    // 会话列表用的图标（Material Design 24×24 路径）
    private const string IconChatFilled = "M20 2H4c-1.1 0-2 .9-2 2v18l4-4h14c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2z";
    private const string IconChatOutline = "M20 2H4c-1.1 0-2 .9-2 2v18l4-4h14c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm0 14H5.17L4 17.17V4h16v12z";
    private const string IconClose = "M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z";

    private void RenderSessionList()
    {
        if (SessionListStackPanel == null) return;
        SessionListStackPanel.Children.Clear();

        var sessions = Manager.Sessions.OrderByDescending(s => s.LastActiveAt).ToList();

        if (SessionCountText != null) SessionCountText.Text = sessions.Count.ToString();
        if (SessionEmptyState != null)
        {
            SessionEmptyState.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var session in sessions)
        {
            var isActive = session.Id == Manager.ActiveSessionId;
            var captured = session; // 避免闭包捕获循环变量

            // 会话图标：选中实心 + 强调色，未选中描边 + 次要色
            var icon = new Shapes.Path
            {
                Data = Geometry.Parse(isActive ? IconChatFilled : IconChatOutline),
                Width = 13,
                Height = 13,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            icon.SetResourceReference(
                Shapes.Shape.FillProperty,
                isActive ? "PrimaryHueMidBrush" : "MaterialDesignBody");
            icon.Opacity = isActive ? 1.0 : 0.45;

            var title = new TextBlock
            {
                Text = session.Title,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (isActive)
            {
                title.FontWeight = FontWeights.SemiBold;
                title.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryHueMidBrush");
            }
            else
            {
                title.Opacity = 0.85;
            }

            // 时间与删除共用右侧一格：平时显示时间，悬停时时间隐去、删除浮现。
            // 这样每行只留「图标 + 标题」，不会因为常驻的 ✕ 变成一排乱码。
            var time = new TextBlock
            {
                Text = FormatSessionTime(session.LastActiveAt),
                FontSize = 10.5,
                Opacity = 0.4,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            var closeIcon = new Shapes.Path
            {
                Data = Geometry.Parse(IconClose),
                Width = 8,
                Height = 8,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            closeIcon.SetResourceReference(Shapes.Shape.FillProperty, "MaterialDesignBody");

            var deleteHost = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Opacity = 0,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Child = closeIcon
            };
            ToolTipService.SetToolTip(deleteHost, "删除会话");
            deleteHost.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true; // 阻止冒泡，否则会顺带切换会话
                Manager.DeleteSession(captured.Id);
            };

            var slot = new Grid { Width = 46 };
            slot.Children.Add(time);
            slot.Children.Add(deleteHost);

            // 选中态的强调色底纹：空 Border + 低透明度，避免直接给条目刷不透明色块
            var tint = new Border { CornerRadius = new CornerRadius(8), IsHitTestVisible = false };
            if (isActive)
            {
                tint.SetResourceReference(Border.BackgroundProperty, "PrimaryHueMidBrush");
                tint.Opacity = 0.12;
            }
            else
            {
                tint.Opacity = 0;
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(icon, 0);
            Grid.SetColumn(title, 1);
            Grid.SetColumn(slot, 2);
            grid.Children.Add(icon);
            grid.Children.Add(title);
            grid.Children.Add(slot);

            var layer = new Grid();
            layer.Children.Add(tint);
            layer.Children.Add(grid);

            var row = new Border
            {
                Style = (Style)FindResource("SessionItemBorderStyle"),
                Cursor = Cursors.Hand,
                Height = 34,
                Child = layer
            };
            ToolTipService.SetToolTip(row, $"{session.Title}（{session.LastActiveAt:MM-dd HH:mm}）");
            row.MouseEnter += (_, _) =>
            {
                time.Opacity = 0;
                deleteHost.Opacity = 0.85;
            };
            row.MouseLeave += (_, _) =>
            {
                time.Opacity = 0.4;
                deleteHost.Opacity = 0;
            };
            row.MouseLeftButtonUp += (_, _) => Manager.ActivateSession(captured.Id);

            SessionListStackPanel.Children.Add(row);
        }
    }

    /// <summary>会话列表里的相对时间：刚刚 / N 分钟前 / 今天 HH:mm / 昨天 HH:mm / N 天前 / MM-dd。</summary>
    private static string FormatSessionTime(DateTime time)
    {
        var now = DateTime.Now;
        var delta = now - time;
        if (delta.TotalSeconds < 60) return "刚刚";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} 分钟前";
        if (time.Date == now.Date) return time.ToString("今天 HH:mm");
        if (time.Date == now.Date.AddDays(-1)) return time.ToString("昨天 HH:mm");
        if (delta.TotalDays < 7) return $"{(int)delta.TotalDays} 天前";
        return time.ToString("MM-dd");
    }

    /// <summary>多会话模式：显示侧栏；单会话模式隐藏侧栏并在头部显示「新建会话」。</summary>
    private void UpdateSidebarVisibility()
    {
        var multi = _settings.IsMultiSessionEnabled;
        if (SessionSidebarBorder != null)
        {
            SessionSidebarBorder.Visibility = multi && _sidebarExpanded ? Visibility.Visible : Visibility.Collapsed;
        }
        // 展开按钮只在侧栏收起时出现，位置固定在内容区左缘
        if (SidebarToggleButton != null)
        {
            SidebarToggleButton.Visibility = multi && !_sidebarExpanded ? Visibility.Visible : Visibility.Collapsed;
        }
        if (NewSessionButton != null)
        {
            NewSessionButton.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void OnSidebarExpandClicked(object? sender, RoutedEventArgs e)
    {
        _sidebarExpanded = true;
        UpdateSidebarVisibility();
    }

    private void OnSidebarCollapseClicked(object? sender, RoutedEventArgs e)
    {
        _sidebarExpanded = false;
        UpdateSidebarVisibility();
    }
}
