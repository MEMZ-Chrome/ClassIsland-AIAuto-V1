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

            // ClassIsland 的托盘菜单在主程序里已经构建好，插件只能在末尾追加，
            // 直接 Add 会掉到「退出」「调试」下面。所以先追加再挪位置。
            //
            // 目标：跟在「换课…」之后、原有的那一条分割线之前，与「重启/退出」分开。
            // 注意不要把 AI助手 插到「重启」前面自己再加一条分隔线 ——
            // 宿主在换课与重启之间本来就有一条 Specator，那样会出现连续两条。
            var menu = taskBarIconService.MainTaskBarIcon.ContextMenu;
            if (menu == null) return;

            menu.Items.Add(menuItem);

            var restartIndex = -1;
            for (int i = 0; i < menu.Items.Count; i++)
            {
                if (menu.Items[i] is MenuItem mi && mi.Header?.ToString() == "重启")
                {
                    restartIndex = i;
                    break;
                }
            }

            if (restartIndex > 0)
            {
                menu.Items.Remove(menuItem);

                // 重新定位「重启」，若它前面已是分割线，就插到那条线之前
                var target = -1;
                for (int i = 0; i < menu.Items.Count; i++)
                {
                    if (menu.Items[i] is MenuItem mi && mi.Header?.ToString() == "重启")
                    {
                        target = i;
                        break;
                    }
                }

                if (target > 0 && menu.Items[target - 1] is Separator)
                {
                    target -= 1;
                }

                if (target >= 0)
                {
                    menu.Items.Insert(target, menuItem);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CI自动化 1.x] 托盘菜单注册异常: {ex.Message}");
        }
    }
}
