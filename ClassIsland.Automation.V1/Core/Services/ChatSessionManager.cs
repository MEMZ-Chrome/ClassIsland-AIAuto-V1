using System.IO;
using System.Text.Json.Nodes;
using ClassIsland.Automation.Core.Models;
using ClassIsland.Shared.IPC.Abstractions.Services;

namespace ClassIsland.Automation.Core.Services;

/// <summary>
/// 全局会话与对话回合管理器（单例）。
/// 会话列表、活跃上下文与正在进行的 ReAct 回合都存活于本实例而非聊天窗口，
/// 因此关闭窗口后回合仍在后台继续执行并落盘，重新打开窗口直接附着到同一份状态，
/// 不会出现新旧窗口各持一份会话互相覆盖的问题。
/// </summary>
public sealed class ChatSessionManager
{
    public static ChatSessionManager Instance { get; } = new();

    private ChatSessionManager() { }

    /// <summary>系统消息之外保留的最大历史条数，超过即从最旧回合截断，防止上下文无限膨胀。</summary>
    private const int MaxHistoryMessages = 60;

    private readonly AiClientService _aiClient = new();
    private string _filePath = "";
    private bool _initialized;
    private IPublicProfileService? _profileService;
    private IPublicUriNavigationService? _uriService;

    /// <summary>当前活跃会话的完整上下文（含 system 消息），窗口只读渲染。</summary>
    public JsonArray Context { get; } = new();

    public List<ChatSession> Sessions { get; } = new();
    public string ActiveSessionId { get; private set; } = "";
    public bool IsTurnInFlight { get; private set; }

    /// <summary>是否有可见的聊天窗口。由窗口在打开/关闭时维护，用于决定回合结束是否发后台通知。</summary>
    public bool IsWindowVisible { get; set; }

    /// <summary>最近一次失败回合的展示信息（上下文已回滚，气泡由窗口单独重放）。下一回合或切换会话时清空。</summary>
    public (string UserText, string Error)? LastFailedTurn { get; private set; }

    // ---- UI 事件（在 UI 线程触发；窗口订阅，窗口关闭即自动失联，回合继续） ----
    public event Action? SessionsChanged;      // 会话列表/标题/激活会话变化
    public event Action? TurnStarted;          // 回合开始：UI 追加思考气泡
    public event Action<string>? TurnProgress; // 工具徽标
    public event Action? TurnEnded;            // 回合结束：UI 从上下文重建并恢复发送

    /// <summary>后台任务完成通知请求（标题, 正文）。由外壳层订阅并转发到宿主通知系统。</summary>
    public event Action<string, string>? BackgroundNotificationRequested;

    public ChatSession? ActiveSession => Sessions.FirstOrDefault(s => s.Id == ActiveSessionId);

    /// <summary>首次使用时加载会话；之后窗口重建调用是空操作，状态跨窗口存活。</summary>
    public void Initialize(string filePath, IPublicProfileService? profileService, IPublicUriNavigationService? uriService)
    {
        if (_initialized) return;
        _initialized = true;
        _filePath = filePath;
        _profileService = profileService;
        _uriService = uriService;

        Sessions.AddRange(ChatSessionStore.Load(filePath));

        var settings = SettingsManager.Current;
        if (Sessions.Count == 0 || settings.IsNewSessionOnOpenEnabled)
        {
            CreateNewSessionCore(persist: true);
        }
        else
        {
            LoadSessionIntoContext(Sessions.OrderByDescending(s => s.LastActiveAt).First());
        }
    }

    public void CreateNewSession()
    {
        if (IsTurnInFlight) return;
        CreateNewSessionCore(persist: true);
        RaiseChanged();
    }

    public void ActivateSession(string sessionId)
    {
        if (IsTurnInFlight) return;
        var session = Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session == null || sessionId == ActiveSessionId) return;

        CaptureCurrentSession();
        ActiveSessionId = session.Id;
        LoadSessionIntoContext(session);
        PersistSessions();
        RaiseChanged();
    }

    public void DeleteSession(string sessionId)
    {
        if (IsTurnInFlight) return;
        var session = Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session == null) return;
        Sessions.Remove(session);

        if (sessionId == ActiveSessionId)
        {
            ActiveSessionId = "";
            if (Sessions.Count > 0)
            {
                var latest = Sessions.OrderByDescending(s => s.LastActiveAt).First();
                ActiveSessionId = latest.Id;
                LoadSessionIntoContext(latest);
            }
            else
            {
                CreateNewSessionCore(persist: true);
            }
        }

        PersistSessions();
        RaiseChanged();
    }

    /// <summary>
    /// 发起一轮对话。窗口关闭不影响执行；返回时结果已写入会话并落盘。
    /// 失败回合回滚上下文并保留 LastFailedTurn 供界面重放。
    /// </summary>
    public async Task SendUserTurnAsync(string textContent, byte[]? imageBytes, string imageMime)
    {
        if (IsTurnInFlight) return;
        IsTurnInFlight = true;
        LastFailedTurn = null;

        var settings = SettingsManager.Current;
        var replyText = "";
        var success = false;

        try
        {
            EnsureSystemMessage();
            TrimConversationHistory();
            var historyBaseline = Context.Count;

            var userMsgContent = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = textContent }
            };
            if (imageBytes is { Length: > 0 })
            {
                userMsgContent.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = $"data:{imageMime};base64,{Convert.ToBase64String(imageBytes)}"
                    }
                });
            }

            var userMessageIndex = Context.Count;
            Context.Add(new JsonObject { ["role"] = "user", ["content"] = userMsgContent });
            var hadImage = imageBytes is { Length: > 0 };

            TurnStarted?.Invoke();

            try
            {
                var executor = new ToolExecutor(_profileService, _uriService, settings, () =>
                {
                    SettingsManager.Save(settings);
                    EnsureSystemMessage();
                })
                {
                    // 聊天窗口不提供解锁入口，因此只有「未设置任何访问保护」时才视为已授权。
                    // 设置了密码/TOTP 的用户必须去设置页改高危项，AI 不能替他开。
                    IsAuthorizedSession = !SecurityService.IsActiveProtectionEnabled(settings)
                };

                var response = await _aiClient.ExecuteAgentTurnAsync(
                    settings,
                    Context,
                    executor,
                    onProgress: (statusText, _) => TurnProgress?.Invoke(statusText),
                    onFirstResponse: () => StripImageFromHistory(userMessageIndex, textContent));

                replyText = string.IsNullOrWhiteSpace(response.ReplyText) ? "操作已执行完毕。" : response.ReplyText;
                success = true;
            }
            catch (Exception ex)
            {
                // 回滚本回合写入的全部消息：失败时用户消息留在历史里，重试后会出现
                // 连续两条 user 消息，严格交替校验的端点会直接返回 400。
                while (Context.Count > historyBaseline)
                {
                    Context.RemoveAt(Context.Count - 1);
                }
                replyText = $"请求处理失败: {ex.Message}";
                LastFailedTurn = (textContent, replyText);
            }
        }
        finally
        {
            CaptureCurrentSession();
            PersistSessions();
            IsTurnInFlight = false;

            if (success && SettingsManager.Current.IsAiSessionTitleEnabled)
            {
                var titleSession = ActiveSession;
                if (titleSession != null && titleSession.Title == "新会话")
                {
                    _ = UpdateSessionTitleWithAiAsync(titleSession);
                }
            }

            TurnEnded?.Invoke();
            RaiseChanged();

            // 回合结束时窗口已关闭（任务转入后台）且用户开启了后台通知，提醒任务结果
            if (!IsWindowVisible && SettingsManager.Current.IsBackgroundTaskNotificationEnabled)
            {
                var title = success ? "IslandAgent 后台任务已完成" : "IslandAgent 后台任务失败";
                BackgroundNotificationRequested?.Invoke(title, TruncateText(replyText, 120));
            }
        }
    }

    private void RaiseChanged()
    {
        SessionsChanged?.Invoke();
    }

    private void CreateNewSessionCore(bool persist)
    {
        CaptureCurrentSession();

        var session = new ChatSession { Title = "新会话" };
        Sessions.Add(session);
        ActiveSessionId = session.Id;
        LoadSessionIntoContext(session);

        if (persist) PersistSessions();
    }

    private void LoadSessionIntoContext(ChatSession session)
    {
        Context.Clear();
        foreach (var m in session.Messages)
        {
            if (m != null) Context.Add(m.DeepClone());
        }
        EnsureSystemMessage();
        LastFailedTurn = null;
    }

    /// <summary>把当前上下文写回活跃会话（剥离 system、把多段内容归一为纯文本）。</summary>
    private void CaptureCurrentSession()
    {
        var current = ActiveSession;
        if (current == null) return;

        current.Messages.Clear();
        foreach (var node in Context)
        {
            if (node is not JsonObject o) continue;
            if (o["role"]?.GetValue<string>() == "system") continue;

            if (node.DeepClone() is JsonObject clone)
            {
                clone["content"] = NormalizeContentToString(clone["content"]);
                current.Messages.Add(clone);
            }
        }
        current.LastActiveAt = DateTime.Now;

        // 已有标题（含 AI 生成的）不覆盖；AI 命名模式下也不用首条消息截取兜底，
        // 否则会抢在 AI 返回前把标题定死，导致 AI 命名永远不触发
        if (current.Title == "新会话" && !SettingsManager.Current.IsAiSessionTitleEnabled)
        {
            var title = DeriveSessionTitle(current);
            if (!string.IsNullOrWhiteSpace(title)) current.Title = title;
        }
    }

    private void PersistSessions()
    {
        ChatSessionStore.Save(_filePath, Sessions);
    }

    private void EnsureSystemMessage()
    {
        var prompt = BuildSystemPrompt(SettingsManager.Current);
        if (Context.Count > 0 && Context[0]?["role"]?.GetValue<string>() == "system")
        {
            Context[0] = new JsonObject
            {
                ["role"] = "system",
                ["content"] = prompt
            };
        }
        else
        {
            Context.Insert(0, new JsonObject
            {
                ["role"] = "system",
                ["content"] = prompt
            });
        }
    }

    private void StripImageFromHistory(int userMessageIndex, string textContent)
    {
        if (userMessageIndex < 0 || userMessageIndex >= Context.Count) return;
        if (Context[userMessageIndex]?["content"] is JsonArray)
        {
            Context[userMessageIndex] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = $"{textContent}（用户上传了一张课表图片）"
            };
        }
    }

    /// <summary>按整回合截断历史，保证截断后第一条仍是 user 消息，不产生悬挂的 tool 应答。</summary>
    private void TrimConversationHistory()
    {
        while (Context.Count > MaxHistoryMessages)
        {
            int keep = 2;
            while (keep < Context.Count && Context[keep]?["role"]?.GetValue<string>() != "user")
            {
                keep++;
            }

            int removeCount = keep - 1;
            if (removeCount <= 0) break;
            for (int i = 0; i < removeCount; i++)
            {
                Context.RemoveAt(1);
            }
        }
    }

    private static string BuildSystemPrompt(PluginSettings settings)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("你是一个专门为 ClassIsland 课表信息显示软件服务的智能助手 IslandAgent。");
        sb.AppendLine("你可以根据用户的需求、上传的课表图片或指令，通过调用提供的工具来灵活管理科目 (upsert_subjects)、时间表 (create_time_layout)、" +
                      "每日课表 (set_class_plan)、查询课表详情 (get_schedule_details)、临时调课 (setup_temp_class_plan)、读取修改软件设置 (get_app_settings, update_app_settings)、获取系统时间 (get_current_time) 以及记录更新长期记忆 (update_memory)。");
        if (settings.IsCommandExecutionEnabled)
        {
            sb.AppendLine("你已被授权在必要时调用 execute_command 在系统终端执行命令行指令。");
        }
        sb.AppendLine();
        sb.AppendLine("【重要执行原则】：");
        sb.AppendLine("1. 你具备全自主连续工具调用能力（ReAct），在必要时可自主连续调用工具（例如：先获取当前时间或课表详情，再执行调课），无需让用户确认继续。");
        sb.AppendLine("2. 当用户涉及“今天”、“明天”、“周几”等相对时间概念时，请调用 get_current_time 工具获取当前精确基准时间与星期几，严禁盲目猜测日期。");
        sb.AppendLine("3. 当用户询问课表内容或调课前需要确认现有课程时，请主动调用 get_profile_summary 或 get_schedule_details 查看确切课程安排。");
        sb.AppendLine("4. 当用户要求修改设置或开关通知提醒时，请优先调用 update_app_settings 直接修改设置，无需让用户手动点击页面。");
        if (settings.IsMemoryEnabled)
        {
            sb.AppendLine("5. 【长期记忆与自动更新】：当用户在对话中透露或你分析得出班级信息、作息习惯、科目/老师偏好、调课规律等具有持久价值的信息时，请自主调用 update_memory 工具自动记录并更新长期记忆库（无需用户每次明确提醒“请记住”）；用户明确吩咐“记住...”或要求修改记忆时也必须调用。在回答关于用户偏好、班级、老师或之前记录过的内容时，必须严格查阅并遵循【AI 长期记忆库 / 偏好事实】中的记录！");
        }
        sb.AppendLine("6. 操作完成后，请用清晰明了的中文 Markdown 富文本给出回复。");

        if (!string.IsNullOrWhiteSpace(settings.CustomPrompt))
        {
            sb.AppendLine();
            sb.AppendLine("【用户自定义要求】：");
            sb.AppendLine(settings.CustomPrompt.Trim());
        }

        if (settings.IsMemoryEnabled && !string.IsNullOrWhiteSpace(settings.CustomMemory))
        {
            sb.AppendLine();
            sb.AppendLine("【AI 长期记忆库 / 偏好事实】：");
            sb.AppendLine(settings.CustomMemory.Trim());
        }

        return sb.ToString();
    }

    /// <summary>调用当前模型为本会话生成总结标题；期间会话被删除则放弃写入。</summary>
    private async Task UpdateSessionTitleWithAiAsync(ChatSession session)
    {
        try
        {
            var excerpt = BuildTitleExcerpt();
            if (string.IsNullOrWhiteSpace(excerpt)) return;

            var title = await _aiClient.GenerateSessionTitleAsync(SettingsManager.Current, excerpt);
            if (string.IsNullOrWhiteSpace(title)) return;
            if (!Sessions.Contains(session)) return;

            session.Title = title;
            PersistSessions();
            RaiseChanged();
        }
        catch
        {
            // 标题生成失败不影响对话
        }
    }

    /// <summary>取首条用户消息与最终回复各 200 字作为命名素材。</summary>
    private string BuildTitleExcerpt()
    {
        string? firstUser = null;
        string? lastAssistant = null;
        foreach (var node in Context)
        {
            if (node is not JsonObject o) continue;
            switch (o["role"]?.GetValue<string>())
            {
                case "user" when firstUser == null:
                    firstUser = NormalizeContentToString(o["content"]);
                    break;
                case "assistant":
                    var a = NormalizeContentToString(o["content"]);
                    if (!string.IsNullOrWhiteSpace(a)) lastAssistant = a;
                    break;
            }
        }

        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(firstUser)) sb.AppendLine("用户：" + TruncateText(firstUser, 200));
        if (!string.IsNullOrWhiteSpace(lastAssistant)) sb.Append("助手：" + TruncateText(lastAssistant, 200));
        return sb.ToString().Trim();
    }

    private static string DeriveSessionTitle(ChatSession session)
    {
        foreach (var node in session.Messages)
        {
            if (node is not JsonObject o) continue;
            if (o["role"]?.GetValue<string>() != "user") continue;
            var text = NormalizeContentToString(o["content"]);
            if (string.IsNullOrWhiteSpace(text)) continue;
            text = text.Replace("\n", " ").Trim();
            return text.Length <= 16 ? text : text[..16] + "…";
        }
        return "新会话";
    }

    /// <summary>把消息 content（字符串或多段数组）归一为纯文本，多段数组里的图片转为占位说明。</summary>
    public static string NormalizeContentToString(JsonNode? content)
    {
        if (content is JsonArray arr)
        {
            var parts = new List<string>();
            var hasImage = false;
            foreach (var p in arr)
            {
                if (p is not JsonObject po) continue;
                switch (po["type"]?.GetValue<string>())
                {
                    case "text":
                        var t = po["text"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(t)) parts.Add(t.Trim());
                        break;
                    case "image_url":
                        hasImage = true;
                        break;
                }
            }
            var text = string.Join("\n", parts);
            if (hasImage)
            {
                text = string.IsNullOrWhiteSpace(text) ? "（用户上传了一张课表图片）" : text + "（用户上传了一张课表图片）";
            }
            return text;
        }

        try { return content?.GetValue<string>() ?? ""; }
        catch { return ""; }
    }

    private static string TruncateText(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";
}
