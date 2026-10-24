using System.IO;
using System.Text.Json;
using ClassIsland.Automation.Core.Models;

namespace ClassIsland.Automation.Core.Services;

/// <summary>
/// 会话持久化存储（sessions.json）。文件路径由调用方传入，
/// Core 层不依赖具体宿主的配置目录结构。
/// </summary>
public static class ChatSessionStore
{
    /// <summary>最多保留的会话数，超出按最后活跃时间丢弃最旧的，防止文件无限增长。</summary>
    public const int MaxSessions = 30;

    private class StoreFile
    {
        public List<ChatSession> Sessions { get; set; } = new();
    }

    public static List<ChatSession> Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return new List<ChatSession>();
            var json = File.ReadAllText(filePath);
            var store = JsonSerializer.Deserialize<StoreFile>(json);
            return store?.Sessions ?? new List<ChatSession>();
        }
        catch
        {
            // 解析失败先备份现场再从空列表开始，绝不静默覆盖
            try { File.Copy(filePath, filePath + ".corrupt", overwrite: true); } catch { }
            return new List<ChatSession>();
        }
    }

    public static void Save(string filePath, List<ChatSession> sessions)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var capped = sessions
                .OrderByDescending(s => s.LastActiveAt)
                .Take(MaxSessions)
                .ToList();

            var json = JsonSerializer.Serialize(new StoreFile { Sessions = capped },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }
        catch
        {
            // 落盘失败不阻断对话，只是本次状态不持久化
        }
    }
}
