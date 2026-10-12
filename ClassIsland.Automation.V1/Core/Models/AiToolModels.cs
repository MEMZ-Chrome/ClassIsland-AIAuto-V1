using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClassIsland.Automation.Core.Models;

public class ToolCallInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public JsonObject Arguments { get; set; } = new();
}

public class ToolExecutionResult
{
    public string ToolCallId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Success { get; set; }
    public string Result { get; set; } = "";
}

public class AgentChatResponse
{
    public string ReplyText { get; set; } = "";
    public List<ToolCallInfo> ToolCalls { get; set; } = new();
    public List<ToolExecutionResult> ExecutedResults { get; set; } = new();
}
