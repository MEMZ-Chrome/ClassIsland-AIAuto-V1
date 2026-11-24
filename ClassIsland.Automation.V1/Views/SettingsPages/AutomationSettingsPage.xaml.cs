using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using ClassIsland.Automation.Core.Models;
using ClassIsland.Automation.Core.Services;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls;
using ClassIsland.Core.Enums.SettingsWindow;
using MaterialDesignThemes.Wpf;

namespace ClassIsland.Automation.V1.Views.SettingsPages;

[SettingsPageInfo("memzchrome.islandagent.settings", "IslandAgent设置",
    PackIconKind.RobotOutline, PackIconKind.Robot, SettingsPageCategory.External)]
public partial class AutomationSettingsPage : SettingsPageBase
{
    private PluginSettings _settings;
    private readonly string _settingsFilePath;
    private readonly AiClientService _aiClient = new();
    private bool _isUpdatingUi = false;
    private bool _isUnlocked = false;

    // 模态弹窗编辑状态
    private AiProviderConfig? _dialogEditingProvider = null;
    private List<AiModelItem> _dialogModels = new();

    // MaterialDesign 原生 Snackbar 消息队列
    private readonly SnackbarMessageQueue _snackbarQueue = new(TimeSpan.FromSeconds(2.5));

    public AutomationSettingsPage()
    {
        InitializeComponent();

        if (PageSnackbar != null)
        {
            PageSnackbar.MessageQueue = _snackbarQueue;
        }

        var configDir = Plugin.Instance?.PluginConfigFolder ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(configDir);
        _settingsFilePath = Path.Combine(configDir, "settings.json");
        // 复用共享实例而不是重新从磁盘加载，避免与对话窗口持有的配置对象分叉。
        SettingsManager.SetSettingsFilePath(_settingsFilePath);
        _settings = SettingsManager.Current;
        _settings.EnsureProvidersInitialized();

        InitSecurityState();
        InitControls();
        InitSecurityControls();

        SettingsManager.SettingsChanged += OnExternalSettingsChanged;
        Unloaded += (_, _) =>
        {
            SettingsManager.SettingsChanged -= OnExternalSettingsChanged;
            // 卸载时不再回写：所有改动在变更当场就已 AutoSave，
            // 这里再写一次只会用可能已过时的控件值覆盖外部（AI 工具）的修改。
        };
    }

    public void ShowToast(string message, bool isError = false)
    {
        Dispatcher.Invoke(() =>
        {
            _snackbarQueue.Enqueue(message);
        });
    }

    private void UpdateDependentControlStates(bool syncMultiSessionExpander = false, bool syncNotificationExpander = false)
    {
        if (CommandWarningBanner != null)
        {
            CommandWarningBanner.Visibility = _settings.IsCommandExecutionEnabled
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (MultiSessionSubOptionsPanel != null)
        {
            MultiSessionSubOptionsPanel.IsEnabled = _settings.IsMultiSessionEnabled;
        }

        if (syncMultiSessionExpander && MultiSessionExpander != null)
        {
            MultiSessionExpander.IsExpanded = _settings.IsMultiSessionEnabled;
        }

        if (BackgroundNotificationSubOptionsPanel != null)
        {
            BackgroundNotificationSubOptionsPanel.IsEnabled = _settings.IsBackgroundTaskNotificationEnabled;
        }

        if (syncNotificationExpander && BackgroundNotificationExpander != null)
        {
            BackgroundNotificationExpander.IsExpanded = _settings.IsBackgroundTaskNotificationEnabled;
        }
    }

    private void OnExternalSettingsChanged(PluginSettings updated)
    {
        Dispatcher.Invoke(() =>
        {
            if (_isUpdatingUi) return;
            _isUpdatingUi = true;
            try
            {
                // 同步所有可能在设置页之外被修改的字段。
                // 只同步 CustomMemory 的话，其他控件会保留旧值并可能被回写覆盖。
                _settings.CustomMemory = updated.CustomMemory;
                if (CustomMemoryTextBox != null) CustomMemoryTextBox.Text = updated.CustomMemory;

                _settings.CustomPrompt = updated.CustomPrompt;
                if (CustomPromptTextBox != null) CustomPromptTextBox.Text = updated.CustomPrompt;

                _settings.IsMemoryEnabled = updated.IsMemoryEnabled;
                if (IsMemoryEnabledCheckBox != null) IsMemoryEnabledCheckBox.IsChecked = updated.IsMemoryEnabled;

                _settings.IsCommandExecutionEnabled = updated.IsCommandExecutionEnabled;
                if (IsCommandExecutionEnabledCheckBox != null) IsCommandExecutionEnabledCheckBox.IsChecked = updated.IsCommandExecutionEnabled;

                _settings.IsMultiSessionEnabled = updated.IsMultiSessionEnabled;
                if (IsMultiSessionEnabledCheckBox != null) IsMultiSessionEnabledCheckBox.IsChecked = updated.IsMultiSessionEnabled;

                _settings.IsSessionSidebarExpandedByDefault = updated.IsSessionSidebarExpandedByDefault;
                if (SessionSidebarExpandedCheckBox != null) SessionSidebarExpandedCheckBox.IsChecked = updated.IsSessionSidebarExpandedByDefault;

                _settings.IsNewSessionOnOpenEnabled = updated.IsNewSessionOnOpenEnabled;
                if (NewSessionOnOpenCheckBox != null) NewSessionOnOpenCheckBox.IsChecked = updated.IsNewSessionOnOpenEnabled;

                _settings.IsAiSessionTitleEnabled = updated.IsAiSessionTitleEnabled;
                if (AiSessionTitleCheckBox != null) AiSessionTitleCheckBox.IsChecked = updated.IsAiSessionTitleEnabled;

                _settings.IsBackgroundTaskNotificationEnabled = updated.IsBackgroundTaskNotificationEnabled;
                if (BackgroundTaskNotificationCheckBox != null) BackgroundTaskNotificationCheckBox.IsChecked = updated.IsBackgroundTaskNotificationEnabled;

                _settings.IsAiNotificationSummaryEnabled = updated.IsAiNotificationSummaryEnabled;
                if (AiNotificationSummaryCheckBox != null) AiNotificationSummaryCheckBox.IsChecked = updated.IsAiNotificationSummaryEnabled;

                _settings.MaxToolIterations = updated.MaxToolIterations;
                if (MaxToolIterationsTextBox != null) MaxToolIterationsTextBox.Text = updated.EffectiveMaxToolIterations.ToString();

                UpdateDependentControlStates(syncMultiSessionExpander: true, syncNotificationExpander: true);

                // 供应商列表可能被 AI 在聊天窗口里增删改过，必须重建卡片，
                // 否则页面停留在过时快照上，用户在旧卡片上编辑/删除极易造成脏写冲突。
                RenderProvidersList();
            }
            finally
            {
                _isUpdatingUi = false;
            }
        });
    }

    private void InitSecurityState()
    {
        // 关键安全设计：待验证的暂存 TOTP 绝不能使页面进入锁定状态，防止用户因手环未同步而卡死！
        if (SecurityService.IsActiveProtectionEnabled(_settings) && !_isUnlocked)
        {
            if (LockHintTextBlock != null)
            {
                bool hasPwd = SecurityService.HasPassword(_settings);
                bool hasTotp = SecurityService.HasActiveTotp(_settings);
                LockHintTextBlock.Text = (hasPwd, hasTotp) switch
                {
                    (true, true) => "当前已开启固定密码与 TOTP 动态口令保护，输入任意一种即可解锁设置页面。",
                    (true, false) => "当前已开启固定密码保护，请输入访问密码解锁设置页面。",
                    (false, true) => "当前已开启 TOTP 动态口令保护，请输入验证器中的 6 位动态验证码解锁设置页面。",
                    _ => "当前设置页面已开启访问保护，请验证身份后继续。"
                };
            }
            if (LockOverlayBorder != null) LockOverlayBorder.Visibility = Visibility.Visible;
            if (MainSettingsScrollViewer != null) MainSettingsScrollViewer.Visibility = Visibility.Collapsed;
        }
        else
        {
            _isUnlocked = true;
            if (LockOverlayBorder != null) LockOverlayBorder.Visibility = Visibility.Collapsed;
            if (MainSettingsScrollViewer != null) MainSettingsScrollViewer.Visibility = Visibility.Visible;
        }

        if (UnlockButton != null)
        {
            UnlockButton.Click += (_, _) => AttemptUnlock();
        }

        if (UnlockPasswordBox != null)
        {
            UnlockPasswordBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    AttemptUnlock();
                }
            };
        }
    }

    private void AttemptUnlock()
    {
        var input = UnlockPasswordBox?.Password?.Trim() ?? "";
        var result = SecurityService.VerifyAccess(_settings, input);

        if (result != AccessAuthResult.Failed)
        {
            _isUnlocked = true;
            if (UnlockErrorTextBlock != null) UnlockErrorTextBlock.Visibility = Visibility.Collapsed;
            if (LockOverlayBorder != null) LockOverlayBorder.Visibility = Visibility.Collapsed;
            if (MainSettingsScrollViewer != null) MainSettingsScrollViewer.Visibility = Visibility.Visible;
            if (UnlockPasswordBox != null) UnlockPasswordBox.Password = "";
            ShowToast("安全验证通过，已解锁设置页面！");
        }
        else
        {
            if (UnlockErrorTextBlock != null)
            {
                bool hasPwd = SecurityService.HasPassword(_settings);
                bool hasTotp = SecurityService.HasActiveTotp(_settings);
                UnlockErrorTextBlock.Text = (hasPwd, hasTotp) switch
                {
                    (true, false) => "访问密码错误，请重新输入！",
                    (false, true) => "TOTP 动态验证码错误或已过期，请确认设备时间后重试！",
                    _ => "密码或 TOTP 动态验证码错误，请重新输入！"
                };
                UnlockErrorTextBlock.Visibility = Visibility.Visible;
            }
            ShowToast("验证失败，请重试！", isError: true);
        }
    }

    private void SaveSettingsToFile()
    {
        try
        {
            // Save 现在返回是否真的落盘；以前异常被吞掉，界面却照样提示「已保存」。
            bool ok = SettingsManager.Save(_settings);
            if (StatusTextBlock != null)
            {
                StatusTextBlock.Text = ok
                    ? "设置已自动保存"
                    : $"保存失败：{SettingsManager.LastSaveError ?? "未知原因"}";
            }
        }
        catch (Exception ex)
        {
            if (StatusTextBlock != null) StatusTextBlock.Text = $"保存失败: {ex.Message}";
        }
    }

    private void AutoSave()
    {
        if (_isUpdatingUi) return;
        SyncFromInputs();
        UpdateDependentControlStates();
        SaveSettingsToFile();
    }

    private void InitControls()
    {
        InitPresetButtons();
        RenderProvidersList();

        if (AddProviderButton != null)
        {
            AddProviderButton.Click += (_, _) => OpenAddProviderDialog();
        }

        if (DialogCloseButton != null)
        {
            DialogCloseButton.Click += (_, _) => CloseProviderDialog();
        }

        if (DialogCancelButton != null)
        {
            DialogCancelButton.Click += (_, _) => CloseProviderDialog();
        }

        if (DialogSaveButton != null)
        {
            DialogSaveButton.Click += (_, _) => SaveProviderDialog();
        }

        if (DialogAddModelButton != null)
        {
            DialogAddModelButton.Click += OnDialogAddModelClicked;
        }

        if (DialogNewModelTextBox != null)
        {
            DialogNewModelTextBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    OnDialogAddModelClicked(null, null);
                }
            };
        }

        if (DialogFetchModelsButton != null)
        {
            DialogFetchModelsButton.Click += OnDialogFetchModelsClicked;
        }

        if (SaveButton != null)
        {
            SaveButton.Click += (_, _) =>
            {
                SyncFromInputs();
                UpdateDependentControlStates();
                bool ok = SettingsManager.Save(_settings);
                if (StatusTextBlock != null)
                {
                    StatusTextBlock.Text = ok
                        ? "设置已保存"
                        : $"保存失败：{SettingsManager.LastSaveError ?? "未知原因"}";
                }
                ShowToast(
                    ok ? "所有设置已保存成功！" : $"保存失败：{SettingsManager.LastSaveError ?? "未知原因"}",
                    isError: !ok);
            };
        }

        _isUpdatingUi = true;
        try
        {
            if (IsMemoryEnabledCheckBox != null)
            {
                IsMemoryEnabledCheckBox.IsChecked = _settings.IsMemoryEnabled;
            }

            if (IsCommandExecutionEnabledCheckBox != null)
            {
                IsCommandExecutionEnabledCheckBox.IsChecked = _settings.IsCommandExecutionEnabled;
            }

            if (IsMultiSessionEnabledCheckBox != null)
            {
                IsMultiSessionEnabledCheckBox.IsChecked = _settings.IsMultiSessionEnabled;
            }

            if (SessionSidebarExpandedCheckBox != null)
            {
                SessionSidebarExpandedCheckBox.IsChecked = _settings.IsSessionSidebarExpandedByDefault;
            }

            if (NewSessionOnOpenCheckBox != null)
            {
                NewSessionOnOpenCheckBox.IsChecked = _settings.IsNewSessionOnOpenEnabled;
            }

            if (AiSessionTitleCheckBox != null)
            {
                AiSessionTitleCheckBox.IsChecked = _settings.IsAiSessionTitleEnabled;
            }

            if (BackgroundTaskNotificationCheckBox != null)
            {
                BackgroundTaskNotificationCheckBox.IsChecked = _settings.IsBackgroundTaskNotificationEnabled;
            }

            if (AiNotificationSummaryCheckBox != null)
            {
                AiNotificationSummaryCheckBox.IsChecked = _settings.IsAiNotificationSummaryEnabled;
            }

            if (MaxToolIterationsTextBox != null)
            {
                MaxToolIterationsTextBox.Text = _settings.EffectiveMaxToolIterations.ToString();
            }

            if (CustomPromptTextBox != null)
            {
                CustomPromptTextBox.Text = _settings.CustomPrompt;
            }

            if (CustomMemoryTextBox != null)
            {
                CustomMemoryTextBox.Text = _settings.CustomMemory;
            }

            UpdateDependentControlStates(syncMultiSessionExpander: true, syncNotificationExpander: true);
        }
        finally
        {
            _isUpdatingUi = false;
        }

        if (IsMemoryEnabledCheckBox != null) IsMemoryEnabledCheckBox.Click += (_, _) => AutoSave();
        if (IsCommandExecutionEnabledCheckBox != null) IsCommandExecutionEnabledCheckBox.Click += (_, _) => AutoSave();
        if (IsMultiSessionEnabledCheckBox != null)
        {
            IsMultiSessionEnabledCheckBox.Click += (_, _) =>
            {
                AutoSave();
                UpdateDependentControlStates(syncMultiSessionExpander: true);
            };
        }
        if (SessionSidebarExpandedCheckBox != null) SessionSidebarExpandedCheckBox.Click += (_, _) => AutoSave();
        if (NewSessionOnOpenCheckBox != null) NewSessionOnOpenCheckBox.Click += (_, _) => AutoSave();
        if (AiSessionTitleCheckBox != null) AiSessionTitleCheckBox.Click += (_, _) => AutoSave();
        if (BackgroundTaskNotificationCheckBox != null)
        {
            BackgroundTaskNotificationCheckBox.Click += (_, _) =>
            {
                AutoSave();
                UpdateDependentControlStates(syncNotificationExpander: true);
            };
        }
        if (AiNotificationSummaryCheckBox != null) AiNotificationSummaryCheckBox.Click += (_, _) => AutoSave();
        if (MaxToolIterationsTextBox != null) MaxToolIterationsTextBox.LostFocus += (_, _) => AutoSave();
        if (CustomPromptTextBox != null) CustomPromptTextBox.LostFocus += (_, _) => AutoSave();
        if (CustomMemoryTextBox != null) CustomMemoryTextBox.LostFocus += (_, _) => AutoSave();

        if (ClearMemoryButton != null)
        {
            ClearMemoryButton.Click += (_, _) =>
            {
                if (CustomMemoryTextBox != null) CustomMemoryTextBox.Text = "";
                _settings.CustomMemory = "";
                AutoSave();
                ShowToast("长期记忆库已清空并保存！");
            };
        }

        if (AuthorHomepageHyperlink != null)
        {
            AuthorHomepageHyperlink.RequestNavigate += OnAuthorHomepageRequestNavigate;
        }
    }

    private void OnAuthorHomepageRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast($"无法打开链接：{ex.Message}", isError: true);
        }
    }

    private void InitPresetButtons()
    {
        if (PresetButtonsWrapPanel == null) return;
        PresetButtonsWrapPanel.Children.Clear();

        var presets = new (string Name, string Url)[]
        {
            ("DeepSeek", "https://api.deepseek.com"),
            ("OpenRouter", "https://openrouter.ai/api/v1"),
            ("小米 MiMo", "https://api.xiaomimimo.com/v1"),
            ("OpenAI", "https://api.openai.com/v1"),
            ("自建 Ollama", "http://localhost:11434/v1")
        };

        foreach (var p in presets)
        {
            var btn = new Button
            {
                Content = p.Name,
                Style = (Style)FindResource("PresetChipButton")
            };
            btn.Click += (_, _) => ApplyPreset(p.Name, p.Url);
            PresetButtonsWrapPanel.Children.Add(btn);
        }
    }

    /// <summary>弹窗内的行内提示：校验不过时贴在底部左侧，不再弹浮层 Toast。</summary>
    private void ShowProviderDialogError(string message)
    {
        if (DialogErrorTextBlock == null) return;
        DialogErrorTextBlock.Text = message;
        DialogErrorTextBlock.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyPreset(string name, string baseUrl)
    {
        if (DialogNameTextBox != null) DialogNameTextBox.Text = name;
        if (DialogBaseUrlTextBox != null) DialogBaseUrlTextBox.Text = baseUrl;

        ShowToast($"已填入「{name}」预置地址，请填写密钥后点击从接口获取。");
    }

    /// <summary>
    /// 给运行时创建的控件挂上 DynamicResource 引用，而不是塞一个当时取到的画刷实例。
    /// 用 SetResourceReference 注册成动态引用才能在用户切换深浅色主题时自动更新。
    /// </summary>
    private void BindThemeBrush(FrameworkElement target, DependencyProperty property, string key)
    {
        if (target == null || property == null) return;

        if (TryFindResource(key) != null)
        {
            target.SetResourceReference(property, key);
        }
        else
        {
            target.SetValue(property, Brushes.Gray);
        }
    }

    private void ApplyDynamicStyle(FrameworkElement target, string styleKey)
    {
        if (TryFindResource(styleKey) != null)
        {
            target.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        }
    }

    private static string BuildProviderDescription(AiProviderConfig provider)
    {
        var url = string.IsNullOrWhiteSpace(provider.BaseUrl) ? "(未设置 API 地址)" : provider.BaseUrl;
        int enabledCount = provider.Models.Count(m => m.IsEnabled);
        return $"{url} · 已启用 {enabledCount}/{provider.Models.Count} 个模型";
    }

    private void RenderProvidersList()
    {
        if (ProvidersListContainer == null) return;
        ProvidersListContainer.Children.Clear();

        if (_settings.Providers.Count == 0)
        {
            var emptyCard = new Card
            {
                Padding = new Thickness(24),
                Margin = new Thickness(0, 0, 0, 6)
            };
            BindThemeBrush(emptyCard, Control.ForegroundProperty, "MaterialDesignBody");

            var emptyStack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var emptyIcon = new PackIcon
            {
                Kind = PackIconKind.CloudOutline,
                Width = 36,
                Height = 36,
                Opacity = 0.45,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };
            var emptyText = new TextBlock
            {
                Text = "暂无模型供应商，点击右上角「添加供应商」开始配置。",
                Opacity = 0.7,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            BindThemeBrush(emptyText, TextBlock.ForegroundProperty, "MaterialDesignBody");
            emptyStack.Children.Add(emptyIcon);
            emptyStack.Children.Add(emptyText);
            emptyCard.Content = emptyStack;
            ProvidersListContainer.Children.Add(emptyCard);
            return;
        }

        foreach (var provider in _settings.Providers)
        {
            var card = new Card
            {
                Margin = new Thickness(0, 0, 0, 6)
            };
            BindThemeBrush(card, Control.ForegroundProperty, "MaterialDesignBody");

            var expander = new Expander
            {
                Background = Brushes.Transparent,
                IsExpanded = true
            };
            BindThemeBrush(expander, Control.ForegroundProperty, "MaterialDesignBody");

            var actionsStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var editBtn = new Button
            {
                Content = new IconText { Kind = PackIconKind.EditOutline, Text = "编辑" },
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "编辑供应商"
            };
            ApplyDynamicStyle(editBtn, "MaterialDesignFlatButton");
            editBtn.Click += (_, _) => OpenEditProviderDialog(provider);
            actionsStack.Children.Add(editBtn);

            var delBtn = new Button
            {
                Content = new IconText { Kind = PackIconKind.Delete, Text = "删除" },
                Foreground = Brushes.IndianRed,
                ToolTip = "删除供应商"
            };
            ApplyDynamicStyle(delBtn, "MaterialDesignFlatButton");
            delBtn.Click += (_, _) =>
            {
                if (_settings.Providers.Count <= 1)
                {
                    ShowToast("至少需保留一个供应商！", isError: true);
                    return;
                }
                _settings.Providers.Remove(provider);
                if (_settings.ActiveProviderId == provider.Id)
                {
                    _settings.ActiveProviderId = _settings.Providers.FirstOrDefault()?.Id ?? "";
                }
                SaveSettingsToFile();
                RenderProvidersList();
                ShowToast($"已删除供应商「{provider.Name}」！");
            };
            actionsStack.Children.Add(delBtn);

            var headerControl = new SettingsControl
            {
                IconGlyph = PackIconKind.CloudOutline,
                Header = string.IsNullOrWhiteSpace(provider.Name) ? "(未命名供应商)" : provider.Name,
                Description = BuildProviderDescription(provider),
                Switcher = actionsStack,
                Margin = new Thickness(-12, 0, -12, 0)
            };
            BindThemeBrush(headerControl, Control.ForegroundProperty, "MaterialDesignBody");
            expander.Header = headerControl;

            var modelsStack = new StackPanel
            {
                Margin = new Thickness(36, 0, 24, 12)
            };

            if (provider.Models.Count == 0)
            {
                var noModelsText = new TextBlock
                {
                    Text = "暂无模型，点击「编辑」从接口获取或手动添加",
                    FontSize = 12,
                    Opacity = 0.65,
                    FontStyle = FontStyles.Italic
                };
                BindThemeBrush(noModelsText, TextBlock.ForegroundProperty, "MaterialDesignBody");
                modelsStack.Children.Add(noModelsText);
            }
            else
            {
                var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var model in provider.Models)
                {
                    var cb = new CheckBox
                    {
                        Content = model.Name,
                        IsChecked = model.IsEnabled,
                        MinWidth = 190,
                        Margin = new Thickness(0, 3, 12, 3)
                    };
                    cb.Click += (_, _) =>
                    {
                        model.IsEnabled = cb.IsChecked == true;
                        headerControl.Description = BuildProviderDescription(provider);
                        SaveSettingsToFile();
                        ShowToast($"已{(model.IsEnabled ? "启用" : "禁用")}模型「{model.Name}」");
                    };
                    wrap.Children.Add(cb);
                }
                modelsStack.Children.Add(wrap);
            }

            expander.Content = modelsStack;
            card.Content = expander;
            ProvidersListContainer.Children.Add(card);
        }
    }

    private void OpenAddProviderDialog()
    {
        _dialogEditingProvider = null;
        _dialogModels = new List<AiModelItem>();

        if (DialogTitleTextBlock != null) DialogTitleTextBlock.Text = "添加供应商";
        if (DialogSubtitleTextBlock != null)
        {
            DialogSubtitleTextBlock.Text = "填好别名、地址和密钥就能用。预置模板只填地址，密钥要自己填。";
        }
        ShowProviderDialogError("");
        if (DialogNameTextBox != null) DialogNameTextBox.Text = "";
        if (DialogBaseUrlTextBox != null) DialogBaseUrlTextBox.Text = "";
        if (DialogApiKeyPasswordBox != null) DialogApiKeyPasswordBox.Password = "";
        if (DialogNewModelTextBox != null) DialogNewModelTextBox.Text = "";

        RenderDialogModels();
        if (ProviderDialogOverlay != null) ProviderDialogOverlay.Visibility = Visibility.Visible;
    }

    private void OpenEditProviderDialog(AiProviderConfig provider)
    {
        _dialogEditingProvider = provider;
        _dialogModels = provider.Models.Select(m => new AiModelItem(m.Name, m.IsEnabled)).ToList();

        if (DialogTitleTextBlock != null) DialogTitleTextBlock.Text = "编辑供应商";
        if (DialogSubtitleTextBlock != null)
        {
            DialogSubtitleTextBlock.Text = $"正在修改「{provider.Name}」，保存后立即生效。";
        }
        ShowProviderDialogError("");
        if (DialogNameTextBox != null) DialogNameTextBox.Text = provider.Name;
        if (DialogBaseUrlTextBox != null) DialogBaseUrlTextBox.Text = provider.BaseUrl;
        if (DialogApiKeyPasswordBox != null) DialogApiKeyPasswordBox.Password = provider.ApiKey;
        if (DialogNewModelTextBox != null) DialogNewModelTextBox.Text = "";

        RenderDialogModels();
        if (ProviderDialogOverlay != null) ProviderDialogOverlay.Visibility = Visibility.Visible;
    }

    private void RenderDialogModels()
    {
        if (DialogModelsContainer == null) return;
        DialogModelsContainer.Children.Clear();

        if (_dialogModels.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = "还没有模型，点「从接口获取」或手动输入",
                Opacity = 0.5,
                FontSize = 12,
                Margin = new Thickness(4, 8, 4, 8)
            };
            BindThemeBrush(hint, TextBlock.ForegroundProperty, "MaterialDesignBody");
            DialogModelsContainer.Children.Add(hint);
            return;
        }

        foreach (var model in _dialogModels)
        {
            var rowGrid = new Grid
            {
                Margin = new Thickness(0, 1, 0, 1),
                MinHeight = 28
            };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var checkBox = new CheckBox
            {
                Content = model.Name,
                IsChecked = model.IsEnabled,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            checkBox.Click += (_, _) =>
            {
                model.IsEnabled = checkBox.IsChecked == true;
            };

            var delBtn = new Button
            {
                Content = new PackIcon { Kind = PackIconKind.Close, Width = 14, Height = 14 },
                Padding = new Thickness(4),
                Height = 24,
                Width = 24,
                Opacity = 0.65,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "移除这个模型"
            };
            ApplyDynamicStyle(delBtn, "MaterialDesignToolForegroundButton");
            delBtn.Click += (_, _) =>
            {
                _dialogModels.Remove(model);
                RenderDialogModels();
                ShowToast($"已移除模型「{model.Name}」");
            };
            Grid.SetColumn(delBtn, 1);

            rowGrid.Children.Add(checkBox);
            rowGrid.Children.Add(delBtn);
            DialogModelsContainer.Children.Add(rowGrid);
        }
    }

    private void OnDialogAddModelClicked(object? sender, RoutedEventArgs? e)
    {
        var modelName = DialogNewModelTextBox?.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(modelName)) return;

        if (!_dialogModels.Any(m => m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase)))
        {
            _dialogModels.Add(new AiModelItem(modelName, true));
            if (DialogNewModelTextBox != null) DialogNewModelTextBox.Text = "";
            RenderDialogModels();
            ShowToast($"已添加模型「{modelName}」");
        }
        else
        {
            ShowToast("该模型已在列表中！", isError: true);
        }
    }

    private async void OnDialogFetchModelsClicked(object? sender, RoutedEventArgs e)
    {
        var baseUrl = DialogBaseUrlTextBox?.Text?.Trim() ?? "";
        var apiKey = DialogApiKeyPasswordBox?.Password?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            ShowProviderDialogError("先填 API 地址，才能从接口拉模型。");
            return;
        }

        ShowToast("正在从 API 获取模型列表...");

        try
        {
            var models = await _aiClient.FetchModelsAsync(baseUrl, apiKey);
            if (models.Count > 0)
            {
                int addedCount = 0;
                foreach (var m in models)
                {
                    if (!_dialogModels.Any(existing => existing.Name.Equals(m, StringComparison.OrdinalIgnoreCase)))
                    {
                        _dialogModels.Add(new AiModelItem(m, true));
                        addedCount++;
                    }
                }
                RenderDialogModels();
                ShowToast($"成功获取 {models.Count} 个模型（新增 {addedCount} 个）！");
            }
            else
            {
                ShowToast("未能获取到任何模型，请检查地址与密钥。", isError: true);
            }
        }
        catch (Exception ex)
        {
            ShowProviderDialogError($"获取模型失败：{ex.Message}");
        }
    }

    private void SaveProviderDialog()
    {
        var name = DialogNameTextBox?.Text?.Trim() ?? "";
        var baseUrl = DialogBaseUrlTextBox?.Text?.Trim() ?? "";
        var apiKey = DialogApiKeyPasswordBox?.Password?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowProviderDialogError("请填写供应商别名。");
            return;
        }
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            ShowProviderDialogError("请填写 API 地址，例如 https://api.deepseek.com。");
            return;
        }

        // 同名供应商会让按名解析永远只命中第一个，第二个在 AI 工具视角下无法访问
        bool duplicateName = _dialogEditingProvider == null
            ? _settings.Providers.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            : _settings.Providers.Any(p => p != _dialogEditingProvider
                                           && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (duplicateName)
        {
            ShowProviderDialogError($"已存在名为「{name}」的供应商，换个别名。");
            return;
        }

        if (_dialogEditingProvider == null)
        {
            var newP = new AiProviderConfig
            {
                Name = name,
                BaseUrl = baseUrl,
                ApiKey = apiKey,
                Models = _dialogModels
            };
            _settings.Providers.Add(newP);
            if (string.IsNullOrWhiteSpace(_settings.ActiveProviderId))
            {
                _settings.ActiveProviderId = newP.Id;
            }
            SaveSettingsToFile();
            CloseProviderDialog();
            RenderProvidersList();
            ShowToast($"已成功添加供应商「{name}」！");
        }
        else
        {
            _dialogEditingProvider.Name = name;
            _dialogEditingProvider.BaseUrl = baseUrl;
            _dialogEditingProvider.ApiKey = apiKey;
            _dialogEditingProvider.Models = _dialogModels;

            SaveSettingsToFile();
            CloseProviderDialog();
            RenderProvidersList();
            ShowToast($"供应商「{name}」修改已保存！");
        }
    }

    private void CloseProviderDialog()
    {
        ShowProviderDialogError("");
        if (ProviderDialogOverlay != null)
        {
            ProviderDialogOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void SyncFromInputs()
    {
        _settings.IsMemoryEnabled = IsMemoryEnabledCheckBox?.IsChecked ?? true;
        _settings.IsCommandExecutionEnabled = IsCommandExecutionEnabledCheckBox?.IsChecked ?? false;
        _settings.IsMultiSessionEnabled = IsMultiSessionEnabledCheckBox?.IsChecked ?? true;
        _settings.IsSessionSidebarExpandedByDefault = SessionSidebarExpandedCheckBox?.IsChecked ?? true;
        _settings.IsNewSessionOnOpenEnabled = NewSessionOnOpenCheckBox?.IsChecked ?? false;
        _settings.IsAiSessionTitleEnabled = AiSessionTitleCheckBox?.IsChecked ?? false;
        _settings.IsBackgroundTaskNotificationEnabled = BackgroundTaskNotificationCheckBox?.IsChecked ?? false;
        _settings.IsAiNotificationSummaryEnabled = AiNotificationSummaryCheckBox?.IsChecked ?? true;

        // 填了非法内容就退回默认值，不把 0 或负数带进对话循环。
        _settings.MaxToolIterations = int.TryParse(MaxToolIterationsTextBox?.Text?.Trim(), out var parsedIterations)
            ? Math.Clamp(parsedIterations, 1, 50)
            : 8;

        _settings.CustomPrompt = CustomPromptTextBox?.Text?.Trim() ?? "";
        _settings.CustomMemory = CustomMemoryTextBox?.Text?.Trim() ?? "";
    }

    private void InitSecurityControls()
    {
        UpdateSecurityStateUi();

        if (SavePasswordButton != null)
        {
            SavePasswordButton.Click += (_, _) =>
            {
                var newPwd = NewPasswordBox?.Password?.Trim() ?? "";
                SecurityService.SetPassword(_settings, newPwd);
                SaveSettingsToFile();
                UpdateSecurityStateUi();
                if (NewPasswordBox != null) NewPasswordBox.Password = "";
                ShowToast(string.IsNullOrEmpty(newPwd) ? "固定密码已清除！" : "固定密码已更新！");
            };
        }

        if (ClearPasswordButton != null)
        {
            ClearPasswordButton.Click += (_, _) =>
            {
                SecurityService.SetPassword(_settings, "");
                SaveSettingsToFile();
                UpdateSecurityStateUi();
                if (NewPasswordBox != null) NewPasswordBox.Password = "";
                ShowToast("固定密码已清除（恢复为无密码）！");
            };
        }

        if (SetupTotpButton != null)
        {
            SetupTotpButton.Click += (_, _) =>
            {
                var secret = SecurityService.StartSetupTotp(_settings);
                SaveSettingsToFile();
                DisplayTotpSetupCard(secret);
                UpdateSecurityStateUi();
                ShowToast("已生成新的 TOTP 密钥！");
            };
        }

        if (CopyTotpSecretButton != null)
        {
            CopyTotpSecretButton.Click += (_, _) =>
            {
                var rawSecret = TotpService.CleanSecret(_settings.PendingTotpSecret.Length > 0 ? _settings.PendingTotpSecret : _settings.TotpSecret);
                if (!string.IsNullOrEmpty(rawSecret))
                {
                    Clipboard.SetText(rawSecret);
                    ShowToast("TOTP 密钥字符串已复制到剪贴板！");
                }
            };
        }

        if (VerifyAndActivateTotpButton != null)
        {
            VerifyAndActivateTotpButton.Click += (_, _) =>
            {
                var code = TotpVerifyCodeTextBox?.Text?.Trim() ?? "";
                if (SecurityService.VerifyAndActivatePendingTotp(_settings, code))
                {
                    SaveSettingsToFile();
                    UpdateSecurityStateUi();
                    if (TotpVerifyResultTextBlock != null)
                    {
                        TotpVerifyResultTextBlock.Text = "验证通过！TOTP 动态口令已正式生效保护。";
                        // 用动态引用而不是写死的 Green/Red，才能跟随运行时切换深浅色
                        BindThemeBrush(TotpVerifyResultTextBlock, TextBlock.ForegroundProperty, "PrimaryHueMidBrush");
                    }
                    ShowToast("TOTP 动态口令已正式激活！");
                }
                else
                {
                    if (TotpVerifyResultTextBlock != null)
                    {
                        TotpVerifyResultTextBlock.Text = "验证码错误或已失效，请确认手环/手机当前时间并重试。";
                        BindThemeBrush(TotpVerifyResultTextBlock, TextBlock.ForegroundProperty, "MaterialDesignValidationErrorBrush");
                    }
                    ShowToast("TOTP 验证失败，请重试！", isError: true);
                }
            };
        }

        if (CancelPendingTotpButton != null)
        {
            CancelPendingTotpButton.Click += (_, _) =>
            {
                SecurityService.CancelPendingTotp(_settings);
                SaveSettingsToFile();
                UpdateSecurityStateUi();
                ShowToast("已放弃暂存的 TOTP 配置！");
            };
        }

        if (DisableTotpButton != null)
        {
            DisableTotpButton.Click += (_, _) =>
            {
                SecurityService.DisableTotp(_settings);
                SaveSettingsToFile();
                UpdateSecurityStateUi();
                ShowToast("TOTP 动态口令已关闭！");
            };
        }
    }

    private void UpdateSecurityStateUi()
    {
        if (PasswordStatusTextBlock != null)
        {
            bool hasPwd = SecurityService.HasPassword(_settings);
            PasswordStatusTextBlock.Text = hasPwd ? "当前状态：已设置固定密码" : "当前状态：未设置固定密码（默认无密码）";
            if (ClearPasswordButton != null) ClearPasswordButton.Visibility = hasPwd ? Visibility.Visible : Visibility.Collapsed;
        }

        if (TotpStatusTextBlock != null)
        {
            // 暂存优先于已激活：重新配置 TOTP 时两者会同时存在，
            // 若先判 HasActiveTotp 会把刚显示的验证卡立刻隐藏，导致无法完成替换。
            if (SecurityService.HasPendingTotp(_settings))
            {
                bool alsoActive = SecurityService.HasActiveTotp(_settings);
                TotpStatusTextBlock.Text = alsoActive
                    ? "当前状态：正在重新配置 TOTP（旧口令仍然有效，验证通过后才会替换）"
                    : "当前状态：发现暂存的待验证 TOTP（在输入正确动态码前绝不生效）";
                if (SetupTotpButton != null) SetupTotpButton.Content = "重新生成 TOTP";
                if (DisableTotpButton != null) DisableTotpButton.Visibility = alsoActive ? Visibility.Visible : Visibility.Collapsed;
                DisplayTotpSetupCard(_settings.PendingTotpSecret);
            }
            else if (SecurityService.HasActiveTotp(_settings))
            {
                TotpStatusTextBlock.Text = "当前状态：TOTP 动态口令已正式激活生效保护";
                if (SetupTotpButton != null) SetupTotpButton.Content = "重新配置 TOTP";
                if (DisableTotpButton != null) DisableTotpButton.Visibility = Visibility.Visible;
                if (TotpSetupCard != null) TotpSetupCard.Visibility = Visibility.Collapsed;
            }
            else
            {
                TotpStatusTextBlock.Text = "当前状态：未启用 TOTP 动态口令";
                if (SetupTotpButton != null) SetupTotpButton.Content = "生成并配置 TOTP";
                if (DisableTotpButton != null) DisableTotpButton.Visibility = Visibility.Collapsed;
                if (TotpSetupCard != null) TotpSetupCard.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void DisplayTotpSetupCard(string secret)
    {
        if (TotpSetupCard == null) return;
        if (TotpExpander != null) TotpExpander.IsExpanded = true;
        TotpSetupCard.Visibility = Visibility.Visible;

        if (TotpSecretStringTextBox != null)
        {
            TotpSecretStringTextBox.Text = TotpService.FormatSecretForDisplay(secret);
        }

        if (TotpVerifyResultTextBlock != null)
        {
            TotpVerifyResultTextBlock.Text = "";
        }

        if (TotpVerifyCodeTextBox != null)
        {
            TotpVerifyCodeTextBox.Text = "";
        }

        try
        {
            string uri = TotpService.GenerateOtpAuthUri(secret);
            byte[] pngBytes = QrCodeService.GeneratePngBytes(uri);
            if (pngBytes.Length > 0 && TotpQrCodeImage != null)
            {
                using var ms = new MemoryStream(pngBytes);
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.StreamSource = ms;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                TotpQrCodeImage.Source = bi;
            }
        }
        catch { }
    }
}
