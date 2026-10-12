using System.IO;
using System.Windows.Controls;
using ClassIsland.Automation.Core.Services;
using ClassIsland.Automation.V1.Views;
using ClassIsland.Automation.V1.Views.SettingsPages;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.Automation.V1;

[PluginEntrance]
public class Plugin : PluginBase
{
    public static Plugin? Instance { get; private set; }
    private ChatWindow? _chatWindow;

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        Instance = this;

        // 注册配置路径，使 SettingsManager.Current 可在任意窗口按需加载，
        // 避免只打开对话窗口时拿到一份空配置。
        var configDir = PluginConfigFolder ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(configDir);
        SettingsManager.SetSettingsFilePath(Path.Combine(configDir, "settings.json"));

        // 1. 注册设置页面
        services.AddSettingsPage<AutomationSettingsPage>();

        // 2. 注册应用启动完成事件，配置托盘右键菜单
        AppBase.Current.AppStarted += (_, _) =>
        {
            RegisterTrayMenu();
        };
    }

    private void RegisterTrayMenu()
    {
        try
        {
            var taskBarIconService = IAppHost.TryGetService<ITaskBarIconService>();
            var profileService = IAppHost.TryGetService<IProfileService>();
            var uriService = IAppHost.TryGetService<IUriNavigationService>();

            if (taskBarIconService == null) return;

            var menuItem = new MenuItem
            {
                Header = "AI助手"
            };
            menuItem.Click += (_, _) =>
            {
                if (_chatWindow == null || !_chatWindow.IsVisible)
                {
                    _chatWindow = new ChatWindow(profileService, uriService);
                    _chatWindow.Show();
                }
                else
                {
                    _chatWindow.Activate();
                }
            };

            // 1.x WPF 托盘菜单扩展支持
            taskBarIconService.MainTaskBarIcon.ContextMenu?.Items.Add(menuItem);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CI自动化 1.x] 托盘菜单注册异常: {ex.Message}");
        }
    }
}
