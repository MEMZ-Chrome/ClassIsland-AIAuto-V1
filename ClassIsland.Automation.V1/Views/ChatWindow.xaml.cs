using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ClassIsland.Automation.Core.Models;
using ClassIsland.Automation.Core.Services;
using ClassIsland.Core.Abstractions.Services;
using Microsoft.Win32;

namespace ClassIsland.Automation.V1.Views;

public partial class ChatWindow : Window
{
    private byte[]? _selectedImageBytes;
    private string _imageMimeType = "image/png";
    private readonly AiClientService _aiClient = new();
    private readonly IProfileService? _profileService;
    private readonly IUriNavigationService? _uriService;
    private readonly JsonArray _conversationMessages = new();
    private readonly ObservableCollection<ChatMessageItem> _displayMessages = new();
    private PluginSettings _settings = new();
    private bool _isUpdatingModelCombo = false;

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
        InitConversation();
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

    private void InitConversation()
    {
        _conversationMessages.Clear();
        _displayMessages.Clear();

        var settings = LoadPluginSettings();
        _conversationMessages.Add(new JsonObject
        {
            ["role"] = "system",
            ["content"] = BuildSystemPrompt(settings)
        });

        var now = DateTime.Now;
        var todayStr = ToolExecutor.GetChineseWeekDay(now.DayOfWeek);

        _displayMessages.Add(new ChatMessageItem
        {
            Role = ChatRole.Assistant,
            TimeString = now.ToString("HH:mm"),
            Content = "你好，我是 ClassIsland AI 助手。"
        });

        ScrollToBottom();
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
            _selectedImageBytes = File.ReadAllBytes(filePath);

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

        _conversationMessages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = userMsgContent
        });

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
            });
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
                });

            assistantMsg.IsThinking = false;
            assistantMsg.Content = string.IsNullOrWhiteSpace(response.ReplyText) ? "操作已执行完毕。" : response.ReplyText;
            ScrollToBottom();
        }
        catch (Exception ex)
        {
            assistantMsg.IsThinking = false;
            assistantMsg.Content = $"请求处理失败: {ex.Message}";
            ScrollToBottom();
        }
        finally
        {
            if (SendButton != null) SendButton.IsEnabled = true;
        }
    }
}
