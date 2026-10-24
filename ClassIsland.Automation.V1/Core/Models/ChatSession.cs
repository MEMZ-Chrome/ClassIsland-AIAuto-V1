using System.Text.Json.Nodes;

namespace ClassIsland.Automation.Core.Models;

/// <summary>
/// 一次独立的对话会话。Messages 保存 OpenAI 兼容格式的上下文消息
/// （不含 system 提示词——它每次对话轮次都会按最新配置重建，无需持久化）。
/// </summary>
public class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新会话";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime LastActiveAt { get; set; } = DateTime.Now;
    public JsonArray Messages { get; set; } = new();
}
