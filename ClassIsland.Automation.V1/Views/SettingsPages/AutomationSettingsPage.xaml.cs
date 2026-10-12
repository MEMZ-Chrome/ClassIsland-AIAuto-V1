using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClassIsland.Automation.Core.Models;
using ClassIsland.Automation.Core.Services;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;

namespace ClassIsland.Automation.V1.Views.SettingsPages;

[SettingsPageInfo("memzchrome.islandagent.settings", "IslandAgent设置", SettingsPageCategory.External)]
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

    // 浮动 Toast 定时器
    private DispatcherTimer? _toastTimer;

    public AutomationSettingsPage()
    {
        InitializeComponent();

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
            AutoSave();
        };
    }

    public void ShowToast(string message, bool isError = false)
    {
        Dispatcher.Invoke(() =>
        {
            if (ToastBorder == null || ToastMessageTextBlock == null) return;
            ToastMessageTextBlock.Text = message;
            ToastBorder.Visibility = Visibility.Visible;

            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.5)
            };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer.Stop();
                if (ToastBorder != null) ToastBorder.Visibility = Visibility.Collapsed;
            };
            _toastTimer.Start();
        });
    }

    private void OnExternalSettingsChanged(PluginSettings updated)
    {
        Dispatcher.Invoke(() =>
        {
            if (_isUpdatingUi) return;
            _isUpdatingUi = true;
            try
            {
                _settings.CustomMemory = updated.CustomMemory;
                if (CustomMemoryTextBox != null)
                {
                    CustomMemoryTextBox.Text = updated.CustomMemory;
                }
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
                UnlockErrorTextBlock.Text = "密码或 TOTP 动态验证码错误，请重新输入！";
                UnlockErrorTextBlock.Visibility = Visibility.Visible;
            }
            ShowToast("验证失败，请重试！", isError: true);
        }
    }

    private void SaveSettingsToFile()
    {
        try
        {
            SettingsManager.Save(_settings);
            if (StatusTextBlock != null) StatusTextBlock.Text = "设置已自动保存！";
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
                SaveSettingsToFile();
                ShowToast("所有设置已保存成功！");
            };
        }

        if (IsMemoryEnabledCheckBox != null)
        {
            IsMemoryEnabledCheckBox.IsChecked = _settings.IsMemoryEnabled;
            IsMemoryEnabledCheckBox.Click += (_, _) => AutoSave();
        }

        if (IsCommandExecutionEnabledCheckBox != null)
        {
            IsCommandExecutionEnabledCheckBox.IsChecked = _settings.IsCommandExecutionEnabled;
            IsCommandExecutionEnabledCheckBox.Click += (_, _) => AutoSave();
        }

        if (MaxToolIterationsTextBox != null)
        {
            MaxToolIterationsTextBox.Text = _settings.EffectiveMaxToolIterations.ToString();
            MaxToolIterationsTextBox.LostFocus += (_, _) => AutoSave();
        }

        if (CustomPromptTextBox != null)
        {
            CustomPromptTextBox.Text = _settings.CustomPrompt;
            CustomPromptTextBox.LostFocus += (_, _) => AutoSave();
        }

        if (CustomMemoryTextBox != null)
        {
            CustomMemoryTextBox.Text = _settings.CustomMemory;
            CustomMemoryTextBox.LostFocus += (_, _) => AutoSave();
        }

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
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 6, 6),
                FontSize = 11
            };
            btn.Click += (_, _) => ApplyPreset(p.Name, p.Url);
            PresetButtonsWrapPanel.Children.Add(btn);
        }
    }

    private void ApplyPreset(string name, string baseUrl)
    {
        if (DialogNameTextBox != null) DialogNameTextBox.Text = name;
        if (DialogBaseUrlTextBox != null) DialogBaseUrlTextBox.Text = baseUrl;

        ShowToast($"已填入「{name}」预置地址，请填写密钥后点击自动获取模型。");
    }

    /// <summary>
    /// 读取主题画刷；资源缺失时回退到默认值，避免深色主题下出现写死的浅色背景。
    /// </summary>
    private Brush ThemeBrush(string key, Brush fallback)
        => TryFindResource(key) as Brush ?? fallback;

    /// <summary>
    /// 次要文字画刷。深色主题下写死 Gray 会与背景糊在一起，必须跟随主题。
    /// </summary>
    private Brush SubtleBrush => ThemeBrush("MaterialDesignBodyLight", Brushes.Gray);

    private void RenderProvidersList()
    {
        if (ProvidersListContainer == null) return;
        ProvidersListContainer.Children.Clear();

        if (_settings.Providers.Count == 0)
        {
            var emptyBorder = new Border
            {
                Background = ThemeBrush("MaterialDesignCardBackground", Brushes.White),
                BorderBrush = ThemeBrush("MaterialDesignDivider", Brushes.LightGray),
                BorderThickness = new Thickness(1, 1, 1, 1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(24, 24, 24, 24),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var emptyText = new TextBlock
            {
                Text = "暂无供应商",
                Foreground = SubtleBrush,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            emptyBorder.Child = emptyText;
            ProvidersListContainer.Children.Add(emptyBorder);
            return;
        }

        foreach (var provider in _settings.Providers)
        {
            var card = new Border
            {
                Background = ThemeBrush("MaterialDesignCardBackground", Brushes.White),
                BorderBrush = ThemeBrush("MaterialDesignDivider", Brushes.LightGray),
                BorderThickness = new Thickness(1, 1, 1, 1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 16, 16, 16),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var mainStack = new StackPanel();

            // 顶行：别名与操作按钮
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel();
            var nameText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(provider.Name) ? "(未命名供应商)" : provider.Name,
                FontSize = 15,
                FontWeight = FontWeights.Bold
            };
            var urlText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(provider.BaseUrl) ? "(未设置 API 地址)" : provider.BaseUrl,
                FontSize = 11,
                Foreground = SubtleBrush,
                Margin = new Thickness(0, 2, 0, 0)
            };
            titleStack.Children.Add(nameText);
            titleStack.Children.Add(urlText);
            Grid.SetColumn(titleStack, 0);

            var actionsStack = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };
            Grid.SetColumn(actionsStack, 1);

            var editBtn = new Button
            {
                Content = "编辑",
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 8, 0)
            };
            editBtn.Click += (_, _) => OpenEditProviderDialog(provider);
            actionsStack.Children.Add(editBtn);

            var delBtn = new Button
            {
                Content = "删除",
                Padding = new Thickness(10, 4, 10, 4)
            };
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

            topGrid.Children.Add(titleStack);
            topGrid.Children.Add(actionsStack);
            mainStack.Children.Add(topGrid);

            // 分割线
            var divider = new Border
            {
                Height = 1,
                Background = ThemeBrush("MaterialDesignDivider", Brushes.LightGray),
                Margin = new Thickness(0, 8, 0, 8)
            };
            mainStack.Children.Add(divider);

            // 模型勾选区域
            var modelsStack = new StackPanel();
            var modelsLabel = new TextBlock
            {
                Text = "模型列表",
                FontSize = 11,
                Foreground = SubtleBrush,
                Margin = new Thickness(0, 0, 0, 6)
            };
            modelsStack.Children.Add(modelsLabel);

            if (provider.Models.Count == 0)
            {
                var noModelsText = new TextBlock
                {
                    Text = "(暂无模型)",
                    FontSize = 11,
                    Foreground = SubtleBrush,
                    FontStyle = FontStyles.Italic
                };
                modelsStack.Children.Add(noModelsText);
            }
            else
            {
                // 固定单元格宽度让勾选项按列对齐，宽窗口下自动铺开，
                // 避免卡片右侧出现大片留白。
                var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
                foreach (var model in provider.Models)
                {
                    var cb = new CheckBox
                    {
                        Content = model.Name,
                        IsChecked = model.IsEnabled,
                        Width = 210,
                        Margin = new Thickness(0, 2, 8, 2)
                    };
                    cb.Click += (_, _) =>
                    {
                        model.IsEnabled = cb.IsChecked == true;
                        SaveSettingsToFile();
                        ShowToast($"已{(model.IsEnabled ? "启用" : "禁用")}模型「{model.Name}」");
                    };
                    wrap.Children.Add(cb);
                }
                modelsStack.Children.Add(wrap);
            }

            mainStack.Children.Add(modelsStack);
            card.Child = mainStack;
            ProvidersListContainer.Children.Add(card);
        }
    }

    private void OpenAddProviderDialog()
    {
        _dialogEditingProvider = null;
        _dialogModels = new List<AiModelItem>();

        if (DialogTitleTextBlock != null) DialogTitleTextBlock.Text = "添加供应商";
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

        if (DialogTitleTextBlock != null) DialogTitleTextBlock.Text = $"编辑供应商 - {provider.Name}";
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
                Text = "暂无模型",
                Foreground = SubtleBrush,
                FontSize = 12,
                Margin = new Thickness(4, 4, 4, 4)
            };
            DialogModelsContainer.Children.Add(hint);
            return;
        }

        foreach (var model in _dialogModels)
        {
            var rowGrid = new Grid
            {
                Margin = new Thickness(0, 2, 0, 2)
            };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var checkBox = new CheckBox
            {
                Content = model.Name,
                IsChecked = model.IsEnabled,
                VerticalAlignment = VerticalAlignment.Center
            };
            checkBox.Click += (_, _) =>
            {
                model.IsEnabled = checkBox.IsChecked == true;
            };

            var delBtn = new Button
            {
                Content = "删除",
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
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
            ShowToast("请先填写 API 地址 (Base URL)！", isError: true);
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
            ShowToast($"获取模型失败: {ex.Message}", isError: true);
        }
    }

    private void SaveProviderDialog()
    {
        var name = DialogNameTextBox?.Text?.Trim() ?? "";
        var baseUrl = DialogBaseUrlTextBox?.Text?.Trim() ?? "";
        var apiKey = DialogApiKeyPasswordBox?.Password?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowToast("请输入供应商别名！", isError: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            ShowToast("请输入 API 地址 (Base URL)！", isError: true);
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
        if (ProviderDialogOverlay != null)
        {
            ProviderDialogOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void SyncFromInputs()
    {
        _settings.IsMemoryEnabled = IsMemoryEnabledCheckBox?.IsChecked ?? true;
        _settings.IsCommandExecutionEnabled = IsCommandExecutionEnabledCheckBox?.IsChecked ?? false;

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
                        TotpVerifyResultTextBlock.Foreground = new SolidColorBrush(Colors.Green);
                    }
                    ShowToast("TOTP 动态口令已正式激活！");
                }
                else
                {
                    if (TotpVerifyResultTextBlock != null)
                    {
                        TotpVerifyResultTextBlock.Text = "验证码错误或已失效，请确认手环/手机当前时间并重试。";
                        TotpVerifyResultTextBlock.Foreground = new SolidColorBrush(Colors.Red);
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
            if (SecurityService.HasActiveTotp(_settings))
            {
                TotpStatusTextBlock.Text = "当前状态：TOTP 动态口令已正式激活生效保护";
                if (SetupTotpButton != null) SetupTotpButton.Content = "重新配置 TOTP";
                if (DisableTotpButton != null) DisableTotpButton.Visibility = Visibility.Visible;
                if (TotpSetupCard != null) TotpSetupCard.Visibility = Visibility.Collapsed;
            }
            else if (SecurityService.HasPendingTotp(_settings))
            {
                TotpStatusTextBlock.Text = "当前状态：发现暂存的待验证 TOTP（在输入正确动态码前绝不生效）";
                if (SetupTotpButton != null) SetupTotpButton.Content = "重新生成 TOTP";
                if (DisableTotpButton != null) DisableTotpButton.Visibility = Visibility.Collapsed;
                DisplayTotpSetupCard(_settings.PendingTotpSecret);
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
