using System.Text.Json.Serialization;

namespace ClassIsland.Automation.Core.Models;

public class AiExtractedSchedule
{
    [JsonPropertyName("subjects")]
    public List<AiSubjectItem> Subjects { get; set; } = [];

    [JsonPropertyName("timeLayout")]
    public AiTimeLayout TimeLayout { get; set; } = new();

    [JsonPropertyName("classPlans")]
    public List<AiClassPlanItem> ClassPlans { get; set; } = [];

    [JsonPropertyName("appSettings")]
    public Dictionary<string, object>? AppSettings { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public class AiSubjectItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("initial")]
    public string Initial { get; set; } = "";

    [JsonPropertyName("teacherName")]
    public string TeacherName { get; set; } = "";

    [JsonPropertyName("isOutDoor")]
    public bool IsOutDoor { get; set; } = false;
}

public class AiTimeLayout
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "AI生成作息时间表";

    [JsonPropertyName("items")]
    public List<AiTimePointItem> Items { get; set; } = [];
}

public class AiTimePointItem
{
    [JsonPropertyName("startTime")]
    public string StartTime { get; set; } = "08:00:00";

    [JsonPropertyName("endTime")]
    public string EndTime { get; set; } = "08:45:00";

    /// <summary>
    /// 0 - 上课, 1 - 课间, 2 - 分割线, 3 - 行动
    /// </summary>
    [JsonPropertyName("timeType")]
    public int TimeType { get; set; } = 0;

    [JsonPropertyName("breakName")]
    public string BreakName { get; set; } = "";

    [JsonPropertyName("defaultSubject")]
    public string DefaultSubject { get; set; } = "";
}

public class AiClassPlanItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "星期一课表";

    /// <summary>
    /// 按顺序排列的每节课科目名称（对应 timeLayout 中 timeType==0 的节点）
    /// </summary>
    [JsonPropertyName("classes")]
    public List<string> Classes { get; set; } = [];
}
