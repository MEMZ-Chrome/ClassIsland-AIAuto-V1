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

public partial class ChatWindow : Window
{
    private byte[]? _selectedImageBytes;
    private bool _isSending = false;
    private string _imageMimeType = "image/png";
    private readonly AiClientService _aiClient = new();
    private readonly IProfileService? _profileService;
    private readonly IUriNavigationService? _uriService;
    private readonly JsonArray _conversationMessages = new();
    private readonly ObservableCollection<ChatMessageItem> _displayMessages = new();
    private PluginSettings _settings = new();
    private bool _isUpdatingModelCombo = false;

    /// <summary>系统消息之外保留的最大历史条数，超过即从最旧回合截断，防止上下文无限膨胀。</summary>
    private const int MaxHistoryMessages = 60;

    // ---- 多会话状态 ----
    private List<ChatSession> _sessions = new();
    private string _activeSessionId = "";
    private bool _sidebarExpanded = true;

    private string SessionsFilePath => Path.Combine(
        Plugin.Instance?.PluginConfigFolder ?? AppContext.BaseDirectory, "sessions.json");

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
        InitializeSessions();

        // 设置页或 AI 工具改了供应商/模型后，下拉框要跟着刷新，
        // 否则会继续持有已被删除的 ActiveModelOption，发送时又把它写回配置。
        SettingsManager.SettingsChanged += OnSettingsChangedExternally;
        Closed += (_, _) =>
        {
            SettingsManager.SettingsChanged -= OnSettingsChangedExternally;
            // 关窗前把当前会话状态落盘
            CaptureCurrentSession();
            PersistSessions();
        };
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

    private static string BuildSystemPrompt(PluginSettings settings)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("你是一个专门为 ClassIsland 课表信息显示软件服务的智能助手 IslandAgent。");
        sb.AppendLine("你可以根据用户的需求、上传的课表图片或指令，通过调用提供的工具来灵活管理科目 (upsert_subjects)、时间表 (create_time_layout)、" +
                      "每日课表 (set_class_plan)、查询课表详情 (get_schedule_details)、临时调课 (setup_temp_class_plan)、读取修改软件设置 (get_app_settings, update_app_settings)、获取系统时间 (get_current_time) 以及记录更新长期记忆 (update_memory)。");
        if (settings.IsCommandExecutionEnabled)
        {
            sb.AppendLine("你已被授权在必要时调用 execute_command 在系统终端执行命令行指令。");
        }
        sb.AppendLine();
        sb.AppendLine("【重要执行原则】：");
        sb.AppendLine("1. 你具备全自主连续工具调用能力（ReAct），在必要时可自主连续调用工具（例如：先获取当前时间或课表详情，再执行调课），无需让用户确认继续。");
        sb.AppendLine("2. 当用户涉及“今天”、“明天”、“周几”等相对时间概念时，请调用 get_current_time 工具获取当前精确基准时间与星期几，严禁盲目猜测日期。");
        sb.AppendLine("3. 当用户询问课表内容或调课前需要确认现有课程时，请主动调用 get_profile_summary 或 get_schedule_details 查看确切课程安排。");
        sb.AppendLine("4. 当用户要求修改设置或开关通知提醒时，请优先调用 update_app_settings 直接修改设置，无需让用户手动点击页面。");
        if (settings.IsMemoryEnabled)
        {
            sb.AppendLine("5. 【长期记忆与自动更新】：当用户在对话中透露或你分析得出班级信息、作息习惯、科目/老师偏好、调课规律等具有持久价值的信息时，请自主调用 update_memory 工具自动记录并更新长期记忆库（无需用户每次明确提醒“请记住”）；用户明确吩咐“记住...”或要求修改记忆时也必须调用。在回答关于用户偏好、班级、老师或之前记录过的内容时，必须严格查阅并遵循【AI 长期记忆库 / 偏好事实】中的记录！");
        }
        sb.AppendLine("6. 操作完成后，请用清晰明了的中文 Markdown 富文本给出回复。");

        if (!string.IsNullOrWhiteSpace(settings.CustomPrompt))
        {
            sb.AppendLine();
            sb.AppendLine("【用户自定义要求】：");
            sb.AppendLine(settings.CustomPrompt.Trim());
        }

        if (settings.IsMemoryEnabled && !string.IsNullOrWhiteSpace(settings.CustomMemory))
        {
            sb.AppendLine();
            sb.AppendLine("【AI 长期记忆库 / 偏好事实】：");
            sb.AppendLine(settings.CustomMemory.Trim());
        }

        return sb.ToString();
    }

    // ==================== 多会话管理 ====================

    /// <summary>
    /// 启动时恢复会话：无历史会话或设置了「每次打开时新建会话」则新建，
    /// 否则继续最近活跃的会话。多会话与单会话模式共用同一套存储
    /// （单会话只是不显示侧栏，始终操作最近的一个会话）。
    /// </summary>
    private void InitializeSessions()
    {
        _sessions = ChatSessionStore.Load(SessionsFilePath);

        if (_sessions.Count == 0 || _settings.IsNewSessionOnOpenEnabled)
        {
            CreateNewSession();
        }
        else
        {
            var latest = _sessions.OrderByDescending(s => s.LastActiveAt).First();
            _activeSessionId = latest.Id;
            _conversationMessages.Clear();
            foreach (var m in latest.Messages)
            {
                if (m != null) _conversationMessages.Add(m.DeepClone());
            }
            EnsureSystemMessage();
            RebuildDisplayFromContext();
        }

        UpdateSidebarVisibility();
        RenderSessionList();
        ScrollToBottom();
    }

    private void CreateNewSession()
    {
        CaptureCurrentSession();

        var session = new ChatSession { Title = "新会话" };
        _sessions.Add(session);
        _activeSessionId = session.Id;

        _conversationMessages.Clear();
        _displayMessages.Clear();
        EnsureSystemMessage();
        _displayMessages.Add(new ChatMessageItem
        {
            Role = ChatRole.Assistant,
            TimeString = DateTime.Now.ToString("HH:mm"),
            Content = "你好，我是 ClassIsland AI 助手。"
        });

        PersistSessions();
        RenderSessionList();
        ScrollToBottom();
    }

    private void ActivateSession(ChatSession session)
    {
        if (_isSending || session.Id == _activeSessionId) return;
        CaptureCurrentSession();

        _activeSessionId = session.Id;
        _conversationMessages.Clear();
        foreach (var m in session.Messages)
        {
            if (m != null) _conversationMessages.Add(m.DeepClone());
        }
        EnsureSystemMessage();
        RebuildDisplayFromContext();

        PersistSessions();
        RenderSessionList();
        ScrollToBottom();
    }

    private void DeleteSession(ChatSession session)
    {
        if (_isSending) return;
        _sessions.Remove(session);

        if (session.Id == _activeSessionId)
        {
            _activeSessionId = "";
            _conversationMessages.Clear();
            _displayMessages.Clear();

            if (_sessions.Count > 0)
            {
                var latest = _sessions.OrderByDescending(s => s.LastActiveAt).First();
                _activeSessionId = latest.Id;
                foreach (var m in latest.Messages)
                {
                    if (m != null) _conversationMessages.Add(m.DeepClone());
                }
                EnsureSystemMessage();
                RebuildDisplayFromContext();
            }
            else
            {
                CreateNewSession();
                return;
            }
        }

        PersistSessions();
        RenderSessionList();
        ScrollToBottom();
    }

    /// <summary>把当前对话上下文写回活跃会话（剥离 system、把多段内容归一为纯文本）。</summary>
    private void CaptureCurrentSession()
    {
        var current = _sessions.FirstOrDefault(s => s.Id == _activeSessionId);
        if (current == null) return;

        current.Messages.Clear();
        foreach (var node in _conversationMessages)
        {
            if (node is not JsonObject o) continue;
            if (o["role"]?.GetValue<string>() == "system") continue;

            if (node.DeepClone() is JsonObject clone)
            {
                clone["content"] = NormalizeContentToString(clone["content"]);
                current.Messages.Add(clone);
            }
        }
        current.LastActiveAt = DateTime.Now;
        // 已有标题（含 AI 生成的）不覆盖；AI 命名模式下也不用首条消息截取兜底，
        // 否则会抢在 AI 返回前把标题定死，导致 AI 命名永远不触发
        if (current.Title == "新会话" && !_settings.IsAiSessionTitleEnabled)
        {
            var title = DeriveSessionTitle(current);
            if (!string.IsNullOrWhiteSpace(title)) current.Title = title;
        }
    }

    private void PersistSessions()
    {
        ChatSessionStore.Save(SessionsFilePath, _sessions);
    }

    private void EnsureSystemMessage()
    {
        var prompt = BuildSystemPrompt(_settings);
        if (_conversationMessages.Count > 0 && _conversationMessages[0]?["role"]?.GetValue<string>() == "system")
        {
            _conversationMessages[0] = new JsonObject
            {
                ["role"] = "system",
                ["content"] = prompt
            };
        }
        else
        {
            _conversationMessages.Insert(0, new JsonObject
            {
                ["role"] = "system",
                ["content"] = prompt
            });
        }
    }

    /// <summary>从会话上下文重建聊天气泡（工具消息只服务于模型上下文，不在界面重放）。</summary>
    private void RebuildDisplayFromContext()
    {
        _displayMessages.Clear();
        foreach (var node in _conversationMessages)
        {
            if (node is not JsonObject o) continue;
            var role = o["role"]?.GetValue<string>();
            var content = NormalizeContentToString(o["content"]);
            if (string.IsNullOrWhiteSpace(content)) continue;

            if (role == "user")
            {
                _displayMessages.Add(new ChatMessageItem { Role = ChatRole.User, TimeString = "", Content = content });
            }
            else if (role == "assistant")
            {
                _displayMessages.Add(new ChatMessageItem { Role = ChatRole.Assistant, TimeString = "", Content = content });
            }
        }
    }

    /// <summary>把消息 content（字符串或多段数组）归一为纯文本，多段数组里的图片转为占位说明。</summary>
    private static string NormalizeContentToString(JsonNode? content)
    {
        if (content is JsonArray arr)
        {
            var parts = new List<string>();
            var hasImage = false;
            foreach (var p in arr)
            {
                if (p is not JsonObject po) continue;
                switch (po["type"]?.GetValue<string>())
                {
                    case "text":
                        var t = po["text"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(t)) parts.Add(t.Trim());
                        break;
                    case "image_url":
                        hasImage = true;
                        break;
                }
            }
            var text = string.Join("\n", parts);
            if (hasImage)
            {
                text = string.IsNullOrWhiteSpace(text) ? "（用户上传了一张课表图片）" : text + "（用户上传了一张课表图片）";
            }
            return text;
        }

        try { return content?.GetValue<string>() ?? ""; }
        catch { return ""; }
    }

    private static string DeriveSessionTitle(ChatSession session)
    {
        foreach (var node in session.Messages)
        {
            if (node is not JsonObject o) continue;
            if (o["role"]?.GetValue<string>() != "user") continue;
            var text = NormalizeContentToString(o["content"]);
            if (string.IsNullOrWhiteSpace(text)) continue;
            text = text.Replace("\n", " ").Trim();
            return text.Length <= 16 ? text : text[..16] + "…";
        }
        return "新会话";
    }

    /// <summary>调用当前模型为本会话生成总结标题；期间会话被删除则放弃写入。</summary>
    private async Task UpdateSessionTitleWithAiAsync(ChatSession session)
    {
        try
        {
            var excerpt = BuildTitleExcerpt();
            if (string.IsNullOrWhiteSpace(excerpt)) return;

            var title = await _aiClient.GenerateSessionTitleAsync(_settings, excerpt);
            if (string.IsNullOrWhiteSpace(title)) return;
            if (!_sessions.Contains(session)) return;

            session.Title = title;
            PersistSessions();
            RenderSessionList();
        }
        catch
        {
            // 标题生成失败不影响对话
        }
    }

    /// <summary>取首条用户消息与最终回复各 200 字作为命名素材。</summary>
    private string BuildTitleExcerpt()
    {
        string? firstUser = null;
        string? lastAssistant = null;
        foreach (var node in _conversationMessages)
        {
            if (node is not JsonObject o) continue;
            switch (o["role"]?.GetValue<string>())
            {
                case "user" when firstUser == null:
                    firstUser = NormalizeContentToString(o["content"]);
                    break;
                case "assistant":
                    var a = NormalizeContentToString(o["content"]);
                    if (!string.IsNullOrWhiteSpace(a)) lastAssistant = a;
                    break;
            }
        }

        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(firstUser)) sb.AppendLine("用户：" + TruncateText(firstUser, 200));
        if (!string.IsNullOrWhiteSpace(lastAssistant)) sb.Append("助手：" + TruncateText(lastAssistant, 200));
        return sb.ToString().Trim();
    }

    private static string TruncateText(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";

    private void RenderSessionList()
    {
        if (SessionListStackPanel == null) return;
        SessionListStackPanel.Children.Clear();

        foreach (var session in _sessions.OrderByDescending(s => s.LastActiveAt))
        {
            var isActive = session.Id == _activeSessionId;
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
            title.MouseLeftButtonUp += (_, _) => ActivateSession(captured);

            var deleteButton = new Button
            {
                Content = "✕",
                FontSize = 11,
                Padding = new Thickness(6, 2, 6, 2)
            };
            deleteButton.Click += (_, _) => DeleteSession(captured);

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

    private void ScrollToBottom()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            ChatScrollViewer?.ScrollToEnd();
        }));
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

    private async void SendCurrentMessage()
    {
        var prompt = InputTextBox?.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(prompt) && (_selectedImageBytes == null || _selectedImageBytes.Length == 0))
        {
            return;
        }

        // 防重入：Enter 处理器不受 SendButton.IsEnabled 约束，连按回车会并发发起多次请求，
        // 同时读写 _conversationMessages / _displayMessages 与共享配置。
        if (_isSending)
        {
            return;
        }
        _isSending = true;
        try
        {
            await SendCurrentMessageCoreAsync(prompt);
        }
        finally
        {
            _isSending = false;
        }
    }

    private async Task SendCurrentMessageCoreAsync(string prompt)
    {
        // 使用 SettingsManager 的共享实例，而不是每次都从磁盘重新反序列化。
        // 重新 Load 会替换静态实例，导致设置页正在编辑的对象与对话窗口持有的对象
        // 变成两份，设置页的修改会在下次发送时被静默丢弃。
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

        var now = DateTime.Now;
        var textContent = string.IsNullOrWhiteSpace(prompt) ? "请分析这张课表图片，并调用相应工具完成课表配置。" : prompt;
        var userMsgContent = new JsonArray();
        userMsgContent.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = textContent
        });

        if (_selectedImageBytes != null && _selectedImageBytes.Length > 0)
        {
            var base64 = Convert.ToBase64String(_selectedImageBytes);
            userMsgContent.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = $"data:{_imageMimeType};base64,{base64}"
                }
            });
        }

        // 每次对话轮次都刷新最新的系统提示词（确保长期记忆库更新后下一轮直接生效）
        var systemPromptContent = BuildSystemPrompt(settings);
        if (_conversationMessages.Count > 0 && _conversationMessages[0]?["role"]?.GetValue<string>() == "system")
        {
            _conversationMessages[0] = new JsonObject
            {
                ["role"] = "system",
                ["content"] = systemPromptContent
            };
        }
        else
        {
            _conversationMessages.Insert(0, new JsonObject
            {
                ["role"] = "system",
                ["content"] = systemPromptContent
            });
        }

        // 先截断过长的历史再发请求，否则对话轮数一多必然撞上下文长度限制
        TrimConversationHistory();
        var historyBaseline = _conversationMessages.Count;

        var userMessage = new JsonObject
        {
            ["role"] = "user",
            ["content"] = userMsgContent
        };
        _conversationMessages.Add(userMessage);
        var userMessageIndex = _conversationMessages.Count - 1;
        var hadImage = _selectedImageBytes != null && _selectedImageBytes.Length > 0;

        _displayMessages.Add(new ChatMessageItem
        {
            Role = ChatRole.User,
            TimeString = now.ToString("HH:mm"),
            Content = textContent
        });

        if (InputTextBox != null) InputTextBox.Text = "";
        OnClearImageClicked(null, null);

        var assistantMsg = new ChatMessageItem
        {
            Role = ChatRole.Assistant,
            TimeString = now.ToString("HH:mm"),
            Content = "正在思考中...",
            IsThinking = true
        };
        _displayMessages.Add(assistantMsg);
        ScrollToBottom();

        if (SendButton != null) SendButton.IsEnabled = false;

        try
        {
            var executor = new ToolExecutor(_profileService, _uriService, settings, () =>
            {
                SettingsManager.Save(settings);
                if (_conversationMessages.Count > 0 && _conversationMessages[0]?["role"]?.GetValue<string>() == "system")
                {
                    _conversationMessages[0]["content"] = BuildSystemPrompt(settings);
                }
            })
            {
                // 聊天窗口不提供解锁入口，因此只有「未设置任何访问保护」时才视为已授权。
                // 设置了密码/TOTP 的用户必须去设置页改高危项，AI 不能替他开。
                IsAuthorizedSession = !SecurityService.IsActiveProtectionEnabled(settings)
            };
            var response = await _aiClient.ExecuteAgentTurnAsync(
                settings,
                _conversationMessages,
                executor,
                onProgress: (statusText, execResult) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        assistantMsg.AddToolBadge(statusText);
                        ScrollToBottom();
                    });
                },
                onFirstResponse: () =>
                {
                    // 模型已在首轮拿到图片，立即把历史里的 Base64 换成占位文本。
                    // 否则同一回合内每轮 ReAct 请求都会重新上传约 11MB 的编码数据。
                    StripImageFromHistory(userMessageIndex, textContent);
                });

            assistantMsg.IsThinking = false;
            assistantMsg.Content = string.IsNullOrWhiteSpace(response.ReplyText) ? "操作已执行完毕。" : response.ReplyText;
            ScrollToBottom();

            // AI 总结标题：仅在会话还没有标题（新会话首轮完成）时触发一次，
            // 失败静默保留默认标题，下一轮还会再试
            if (_settings.IsAiSessionTitleEnabled)
            {
                var titleSession = _sessions.FirstOrDefault(s => s.Id == _activeSessionId);
                if (titleSession != null && titleSession.Title == "新会话")
                {
                    _ = UpdateSessionTitleWithAiAsync(titleSession);
                }
            }
        }
        catch (Exception ex)
        {
            assistantMsg.IsThinking = false;
            assistantMsg.Content = $"请求处理失败: {ex.Message}";
            ScrollToBottom();

            // 回滚本次失败回合写入的全部消息。失败时用户消息留在历史里，
            // 重试后会出现连续两条 user 消息，严格交替校验的端点会直接返回 400。
            while (_conversationMessages.Count > historyBaseline)
            {
                _conversationMessages.RemoveAt(_conversationMessages.Count - 1);
            }
        }
        finally
        {
            // 兜底：首轮响应回调未覆盖到的路径（如模型直出异常前）也不把大图留在历史里。
            if (hadImage)
            {
                StripImageFromHistory(userMessageIndex, textContent);
            }

            // 回合结束把会话状态落盘（含标题、最后活跃时间）
            CaptureCurrentSession();
            PersistSessions();

            if (SendButton != null) SendButton.IsEnabled = true;
        }
    }

    /// <summary>把历史中指定下标的带图 user 消息替换为纯文本占位，避免图片被反复上传。</summary>
    private void StripImageFromHistory(int userMessageIndex, string textContent)
    {
        if (userMessageIndex < 0 || userMessageIndex >= _conversationMessages.Count) return;
        if (_conversationMessages[userMessageIndex]?["content"] is JsonArray)
        {
            _conversationMessages[userMessageIndex] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = $"{textContent}（用户上传了一张课表图片）"
            };
        }
    }

    /// <summary>按整回合截断历史，保证截断后第一条仍是 user 消息，不产生悬挂的 tool 应答。</summary>
    private void TrimConversationHistory()
    {
        while (_conversationMessages.Count > MaxHistoryMessages)
        {
            // 保留起点 = 第二个 user 消息（索引 0 是 system），
            // 删除它之前的所有内容即最旧的一个完整回合（含其 assistant/tool 链）。
            int keep = 2;
            while (keep < _conversationMessages.Count
                   && _conversationMessages[keep]?["role"]?.GetValue<string>() != "user")
            {
                keep++;
            }

            int removeCount = keep - 1;
            if (removeCount <= 0) break;
            for (int i = 0; i < removeCount; i++)
            {
                _conversationMessages.RemoveAt(1);
            }
        }
    }

    private void OnNewSessionClicked(object? sender, RoutedEventArgs e)
    {
        // 发送过程中切换/新建会与进行中的 ReAct 循环竞争 _conversationMessages
        if (_isSending) return;
        CreateNewSession();
    }
}
