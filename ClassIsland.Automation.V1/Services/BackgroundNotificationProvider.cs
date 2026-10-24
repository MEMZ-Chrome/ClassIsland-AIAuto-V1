using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Models.Notification;

namespace ClassIsland.Automation.V1.Services;

/// <summary>
/// 通过 ClassIsland 提醒系统发送后台任务完成通知。
/// 走宿主管线可自动尊重用户的通知总开关与免打扰设置。
/// </summary>
public class BackgroundNotificationProvider : NotificationProviderBase
{
    public BackgroundNotificationProvider() : base(autoRegister: true)
    {
        ProviderGuid = new Guid("3C1D5B87-2A44-4F6E-8C90-5D2B7E4A1F03");
        Name = "IslandAgent后台任务";
        Description = "Agent 对话任务在后台完成时提醒";
    }

    public void Notify(string title, string message)
    {
        ShowNotification(new NotificationRequest
        {
            MaskContent = new NotificationContent($"{title}\n{message}")
            {
                Duration = TimeSpan.FromSeconds(4)
            }
        });
    }
}
