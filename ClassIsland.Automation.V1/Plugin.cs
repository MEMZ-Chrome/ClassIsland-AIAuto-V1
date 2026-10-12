using System.IO;
using System.Linq;
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
            // 直接 Add 会掉到「退出」「调试」下面。所以先追加，再整体挪到
            // 「换课…」之后、「重启」之前。
            var menu = taskBarIconService.MainTaskBarIcon.ContextMenu;
            if (menu == null) return;

            var separator = new Separator();
            menu.Items.Add(separator);
            menu.Items.Add(menuItem);

            // 目标：插在「重启」这一项的前面（它前面本来就是一个 Separator）。
            var restart = menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header?.ToString() == "重启");
            if (restart != null)
            {
                menu.Items.Remove(separator);
                menu.Items.Remove(menuItem);

                // 移除后索引会前移，重新定位「重启」
                var target = menu.Items.IndexOf(restart);
                if (target >= 0)
                {
                    menu.Items.Insert(target, separator);
                    menu.Items.Insert(target + 1, menuItem);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CI自动化 1.x] 托盘菜单注册异常: {ex.Message}");
        }
    }
}
