using ClassIsland.Automation.Core.Services;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;
using MaterialDesignThemes.Wpf;

namespace ClassIsland.Automation.V1.Services;

/// <summary>
/// 通过 ClassIsland 提醒系统发送后台任务完成通知。
/// 走宿主管线可自动尊重用户的通知总开关与免打扰设置。
/// </summary>
[NotificationProviderInfo("3C1D5B87-2A44-4F6E-8C90-5D2B7E4A1F03", "IslandAgent 后台任务", PackIconKind.RobotOutline, "Agent 对话任务在后台完成时提醒")]
public class BackgroundNotificationProvider : NotificationProviderBase, IDisposable
{
    public BackgroundNotificationProvider()
    {
        ChatSessionManager.Instance.BackgroundNotificationRequested += OnBackgroundNotificationRequested;
    }

    private void OnBackgroundNotificationRequested(string title, string message)
    {
        var dispatcher = AppBase.Current?.Dispatcher;
        if (dispatcher == null) return;

        dispatcher.InvokeAsync(() => Notify(title, message));
    }

    public void Notify(string title, string message)
    {
        try
        {
            ShowNotification(new NotificationRequest
            {
                MaskContent = NotificationContent.CreateTwoIconsMask(
                    title,
                    leftIcon: PackIconKind.RobotOutline,
                    hasRightIcon: false,
                    factory: x =>
                    {
                        x.Duration = TimeSpan.FromSeconds(2);
                    }),
                OverlayContent = string.IsNullOrWhiteSpace(message)
                    ? null
                    : NotificationContent.CreateSimpleTextContent(
                        message,
                        factory: x =>
                        {
                            x.Duration = TimeSpan.FromSeconds(5);
                        })
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CI自动化 1.x] 后台通知发送失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        ChatSessionManager.Instance.BackgroundNotificationRequested -= OnBackgroundNotificationRequested;
    }
}
