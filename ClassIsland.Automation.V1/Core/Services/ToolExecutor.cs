using System.Text.Json;
using System.Text.Json.Nodes;
using ClassIsland.Automation.Core.Models;
using ClassIsland.Shared;
using ClassIsland.Shared.IPC.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;

namespace ClassIsland.Automation.Core.Services;

public class ToolExecutor
{
    /// <summary>
    /// 可供 AI 读取与修改的 ClassIsland 宿主设置项白名单。
    /// 仅包含标量类型，避免误改集合类设置导致应用异常。
    /// </summary>
    private static readonly string[] AppSettingPropertyNames =
    [
        // 时间与计时
        "TimeOffsetSeconds", "IsTimeAutoAdjustEnabled", "TimeAutoAdjustInterval",
        // 通知
        "IsNotificationEnabled", "IsClassPrepareNotificationEnabled",
        "IsClassOffNotificationEnabled", "IsClassChangingNotificationEnabled",
        "IsWeatherNotificationEnabled", "IsAfterClassNotificationEnabled",
        // 外观
        "Theme", "Opacity", "Scale", "ShowDate", "ShowWeekDay",
        "ShowSecond", "IsClockSecondsVisible", "IsMainWindowVisible",
        // 窗口行为
        "HideOnClass", "HideOnFullscreen", "HideOnMaxWindow",
        "IsMainWindowTopmost", "IsMousePenetrationEnabled",
        // 启动与关闭
        "IsAutoStartupEnabled", "IsSentryEnabled", "IsDebugEnabled",
        "IsStartupUpdateCheckEnabled",
        // 档案
        "SelectedProfile", "IsSleepModeEnabled",
    ];

    private readonly IPublicProfileService? _profileService;
    private readonly IPublicUriNavigationService? _uriService;
    private readonly PluginSettings? _pluginSettings;
    private readonly Action? _onSavePluginSettings;

    public ToolExecutor(
        IPublicProfileService? profileService,
        IPublicUriNavigationService? uriService = null,
        PluginSettings? pluginSettings = null,
        Action? onSavePluginSettings = null)
    {
        _profileService = profileService;
        _uriService = uriService;
        _pluginSettings = pluginSettings;
        _onSavePluginSettings = onSavePluginSettings;
    }

    public ToolExecutionResult Execute(ToolCallInfo toolCall)
    {
        try
        {
            var args = toolCall.Arguments;
            switch (toolCall.Name)
            {
                case "get_profile_summary":
                    return ExecuteGetProfileSummary(toolCall.Id);

                case "get_schedule_details":
                    return ExecuteGetScheduleDetails(toolCall.Id, args);

                case "upsert_subjects":
                    return ExecuteUpsertSubjects(toolCall.Id, args);

                case "create_time_layout":
                    return ExecuteCreateTimeLayout(toolCall.Id, args);

                case "set_class_plan":
                    return ExecuteSetClassPlan(toolCall.Id, args);

                case "setup_temp_class_plan":
                    return ExecuteSetupTempClassPlan(toolCall.Id, args);

                case "get_app_settings":
                    return ExecuteGetAppSettings(toolCall.Id);

                case "update_app_settings":
                    return ExecuteUpdateAppSettings(toolCall.Id, args);

                case "navigate_app_page":
                    return ExecuteNavigateAppPage(toolCall.Id, args);

                case "get_current_time":
                    return ExecuteGetCurrentTime(toolCall.Id);

                case "update_memory":
                    return ExecuteUpdateMemory(toolCall.Id, args);

                case "execute_command":
                    return ExecuteCommand(toolCall.Id, args);

                case "get_plugin_settings":
                    return ExecuteGetPluginSettings(toolCall.Id);

                case "update_plugin_settings":
                    return ExecuteUpdatePluginSettings(toolCall.Id, args);

                default:
                    return new ToolExecutionResult
                    {
                        ToolCallId = toolCall.Id,
                        Name = toolCall.Name,
                        Success = false,
                        Result = $"未知的工具名称: {toolCall.Name}"
                    };
            }
        }
        catch (Exception ex)
        {
            return new ToolExecutionResult
            {
                ToolCallId = toolCall.Id,
                Name = toolCall.Name,
                Success = false,
                Result = $"执行失败: {ex.Message}"
            };
        }
    }

    private ToolExecutionResult ExecuteGetProfileSummary(string toolId)
    {
        if (_profileService?.Profile == null)
        {
            return new ToolExecutionResult { ToolCallId = toolId, Name = "get_profile_summary", Success = false, Result = "当前 Profile 服务未就绪" };
        }

        var p = _profileService.Profile;
        var now = DateTime.Now;
        var summary = new JsonObject
        {
            ["currentTime"] = now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["currentDayOfWeek"] = GetChineseWeekDay(now.DayOfWeek),
            ["profileName"] = p.Name,
            ["subjectCount"] = p.Subjects.Count,
            ["subjects"] = new JsonArray(p.Subjects.Values.Select(s => (JsonNode)s.Name).ToArray()),
            ["timeLayouts"] = new JsonArray(p.TimeLayouts.Values.Select(t => (JsonNode)t.Name).ToArray()),
            ["classPlans"] = new JsonArray(p.ClassPlans.Values.Select(c => (JsonNode)c.Name).ToArray()),
            ["isOverlayEnabled"] = p.IsOverlayClassPlanEnabled,
            ["activeOverlayId"] = string.IsNullOrEmpty(ProfileCompat.GetOverlayClassPlanId(p)) ? "无" : ProfileCompat.GetOverlayClassPlanId(p)
        };

        return new ToolExecutionResult
        {
            ToolCallId = toolId,
            Name = "get_profile_summary",
            Success = true,
            Result = summary.ToJsonString()
        };
    }

    private ToolExecutionResult ExecuteGetScheduleDetails(string toolId, JsonObject args)
    {
        if (_profileService?.Profile == null)
        {
            return new ToolExecutionResult { ToolCallId = toolId, Name = "get_schedule_details", Success = false, Result = "Profile 服务未就绪" };
        }

        var profile = _profileService.Profile;
        var planNameQuery = args["planName"]?.GetValue<string>()?.Trim();
        var targetDateQuery = args["targetDate"]?.GetValue<string>()?.Trim();

        var subjects = profile.Subjects;
        var resultObj = new JsonObject();
        var plansArray = new JsonArray();

        var queryPlans = profile.ClassPlans.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(planNameQuery))
        {
            queryPlans = queryPlans.Where(kv =>
                kv.Value.Name.Contains(planNameQuery, StringComparison.OrdinalIgnoreCase) ||
                NormalizeWeekDayName(kv.Value.Name).Contains(NormalizeWeekDayName(planNameQuery)));
        }

        foreach (var kv in queryPlans)
        {
            var plan = kv.Value;
            var planObj = new JsonObject
            {
                ["id"] = kv.Key.ToString(),
                ["name"] = plan.Name,
                ["isEnabled"] = plan.IsEnabled,
                ["isOverlay"] = plan.IsOverlay,
                ["timeRuleWeekDay"] = GetChineseWeekDay((DayOfWeek)plan.TimeRule.WeekDay)
            };

            TimeLayout? layout = null;
            if (profile.TimeLayouts.TryGetValue(plan.TimeLayoutId, out var tl))
            {
                layout = tl;
                planObj["timeLayoutName"] = tl.Name;
            }

            var classesArray = new JsonArray();
            var validLessons = layout?.Layouts.Where(l => l.TimeType == 0).ToList() ?? new List<TimeLayoutItem>();

            for (int i = 0; i < plan.Classes.Count; i++)
            {
                var c = plan.Classes[i];
                var subName = ProfileCompat.TryGetSubject(profile, ProfileCompat.GetSubjectId(c), out var s) && s != null ? s.Name : "未设置科目";
                var teacherName = ProfileCompat.TryGetSubject(profile, ProfileCompat.GetSubjectId(c), out var s2) && s2 != null ? s2.TeacherName : "";
                var timeSlot = i < validLessons.Count
                    ? $"{ProfileCompat.GetStart(validLessons[i]):hh\\:mm} - {ProfileCompat.GetEnd(validLessons[i]):hh\\:mm}"
                    : "";

                var itemObj = new JsonObject
                {
                    ["index"] = i + 1, // 1 表示第 1 节课
                    ["subject"] = subName,
                    ["teacher"] = teacherName,
                    ["time"] = timeSlot,
                    ["isChanged"] = c.IsChangedClass
                };
                classesArray.Add(itemObj);
            }
            planObj["classes"] = classesArray;
            plansArray.Add(planObj);
        }

        resultObj["classPlans"] = plansArray;

        var ordersArray = new JsonArray();
        foreach (var ord in profile.OrderedSchedules)
        {
            var targetPlanName = profile.ClassPlans.TryGetValue(ord.Value.ClassPlanId, out var cp) ? cp.Name : "未知课表";
            ordersArray.Add(new JsonObject
            {
                ["date"] = ord.Key.ToString("yyyy-MM-dd"),
                ["planName"] = targetPlanName
            });
        }
        resultObj["orderedSchedules"] = ordersArray;

        return new ToolExecutionResult
        {
            ToolCallId = toolId,
            Name = "get_schedule_details",
            Success = true,
            Result = resultObj.ToJsonString()
        };
    }

    private ToolExecutionResult ExecuteUpsertSubjects(string toolId, JsonObject args)
    {
        if (_profileService?.Profile == null)
        {
            return new ToolExecutionResult { ToolCallId = toolId, Name = "upsert_subjects", Success = false, Result = "Profile 服务未就绪" };
        }

        var profile = _profileService.Profile;
        if (!args.TryGetPropertyValue("subjects", out var subsNode) || subsNode is not JsonArray subsArray)
        {
            return new ToolExecutionResult { ToolCallId = toolId, Name = "upsert_subjects", Success = false, Result = "缺少 subjects 列表参数" };
        }

        int addedCount = 0;
        int updatedCount = 0;

        foreach (var subNode in subsArray)
        {
            if (subNode is not JsonObject sObj) continue;
            var name = sObj["name"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var initial = sObj["initial"]?.GetValue<string>()?.Trim();
            var teacher = sObj["teacherName"]?.GetValue<string>()?.Trim() ?? "";
            var isOutDoor = sObj["isOutDoor"]?.GetValue<bool>() ?? false;

            var existingEntry = profile.Subjects.FirstOrDefault(kv => string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existingEntry.Value != null)
            {
                existingEntry.Value.TeacherName = teacher;
                existingEntry.Value.IsOutDoor = isOutDoor;
                if (!string.IsNullOrWhiteSpace(initial)) existingEntry.Value.Initial = initial;
                updatedCount++;
            }
            else
            {
                var newSub = new Subject
                {
                    Name = name,
                    Initial = string.IsNullOrWhiteSpace(initial) ? name.Substring(0, 1) : initial,
                    TeacherName = teacher,
                    IsOutDoor = isOutDoor
                };
                ProfileCompat.SetByKey(profile.Subjects, Guid.NewGuid().ToString(), newSub);
                addedCount++;
            }
        }

        _profileService.SaveProfile();
        return new ToolExecutionResult
        {
            ToolCallId = toolId,
            Name = "upsert_subjects",
            Success = true,
            Result = $"成功添加 {addedCount} 个科目，更新 {updatedCount} 个科目。"
        };
    }

    private ToolExecutionResult ExecuteCreateTimeLayout(string toolId, JsonObject args)
    {
        if (_profileService?.Profile == null)
        {
            return new ToolExecutionResult { ToolCallId = toolId, Name = "create_time_layout", Success = false, Result = "Profile 服务未就绪" };
        }

        var profile = _profileService.Profile;
        var layoutName = args["layoutName"]?.GetValue<string>()?.Trim() ?? "时间表";
        if (!args.TryGetPropertyValue("items", out var itemsNode) || itemsNode is not JsonArray itemsArray)
        {
            return new ToolExecutionResult { ToolCallId = toolId, Name = "create_time_layout", Success = false, Result = "缺少 items 节点列表" };
        }

        var existingLayout = profile.TimeLayouts.FirstOrDefault(kv => string.Equals(kv.Value.Name, layoutName, StringComparison.OrdinalIgnoreCase));
        TimeLayout layout;
        if (existingLayout.Value != null)
        {
            layout = existingLayout.Value;
            layout.Layouts.Clear();
        }
        else
        {
            layout = new TimeLayout { Name = layoutName };
            ProfileCompat.SetByKey(profile.TimeLayouts, Guid.NewGuid().ToString(), layout);
        }

        foreach (var itemNode in itemsArray)
        {
            if (itemNode is not JsonObject itObj) continue;
            var startStr = itObj["startTime"]?.GetValue<string>() ?? "00:00:00";
            var endStr = itObj["endTime"]?.GetValue<string>() ?? "00:00:00";
            var timeType = itObj["timeType"]?.GetValue<int>() ?? 0;
            var breakName = itObj["breakName"]?.GetValue<string>() ?? "";

            TimeSpan.TryParse(startStr, out var startTime);
            TimeSpan.TryParse(endStr, out var endTime);

            var timeItem = new TimeLayoutItem
            {
                TimeType = timeType,
                BreakName = breakName
            };
            ProfileCompat.SetTimeRange(timeItem, startTime, endTime);
            layout.Layouts.Add(timeItem);
        }

        _profileService.SaveProfile();
        return new ToolExecutionResult
        {
            ToolCallId = toolId,
            Name = "create_time_layout",
            Success = true,
            Result = $"时间表【{layoutName}】已创建并保存，包含 {layout.Layouts.Count} 个节点。"
        };
    }

    private ToolExecutionResult ExecuteSetClassPlan(string toolId, JsonObject args)
    {
        if (_profileService?.Profile == null)
        {
            return new ToolExecutionResult { ToolCallId = toolId, Name = "set_class_plan", Success = false, Result = "Profile 服务未就绪" };
        }

        var profile = _profileService.Profile;
        var planName = args["planName"]?.GetValue<string>()?.Trim() ?? "新课表";
        var timeLayoutName = args["timeLayoutName"]?.GetValue<string>()?.Trim() ?? "";

        if (!args.TryGetPropertyValue("classes", out var clsNode) || clsNode is not JsonArray clsArray)
        {
            return new ToolExecutionResult { ToolCallId = toolId, Name = "set_class_plan", Success = false, Result = "缺少 classes 课程列表" };
        }

        var timeLayoutId = "";
        if (!string.IsNullOrWhiteSpace(timeLayoutName))
        {
            var match = profile.TimeLayouts.FirstOrDefault(kv => string.Equals(kv.Value.Name, timeLayoutName, StringComparison.OrdinalIgnoreCase));
            if (match.Value != null) timeLayoutId = Convert.ToString(match.Key) ?? "";
        }
        if (string.IsNullOrEmpty(timeLayoutId) && profile.TimeLayouts.Count > 0)
        {
            timeLayoutId = ProfileCompat.FirstTimeLayoutId(profile);
        }

        var subMap = profile.Subjects.ToDictionary(kv => kv.Value.Name, kv => Convert.ToString(kv.Key) ?? "", StringComparer.OrdinalIgnoreCase);

        var existingPlan = profile.ClassPlans.FirstOrDefault(kv => string.Equals(kv.Value.Name, planName, StringComparison.OrdinalIgnoreCase));
        ClassPlan plan;
        if (existingPlan.Value != null)
        {
            plan = existingPlan.Value;
            plan.Classes.Clear();
            if (!string.IsNullOrEmpty(timeLayoutId)) ProfileCompat.SetTimeLayoutId(plan, timeLayoutId);
        }
        else
        {
            plan = new ClassPlan
            {
                Name = planName,
                IsEnabled = true
            };
            ProfileCompat.SetTimeLayoutId(plan, timeLayoutId);
            ProfileCompat.SetByKey(profile.ClassPlans, Guid.NewGuid().ToString(), plan);
        }

        int detectedWeekDay = ParseWeekDay(planName);
        if (detectedWeekDay >= 0)
        {
            plan.TimeRule.WeekDay = detectedWeekDay;
        }

        for (int i = 0; i < clsArray.Count; i++)
        {
            var subName = clsArray[i]?.GetValue<string>()?.Trim() ?? "";
            var subId = "";
            if (!string.IsNullOrWhiteSpace(subName))
            {
                if (!subMap.TryGetValue(subName, out subId))
                {
                    subId = Guid.NewGuid().ToString();
                    var newSub = new Subject { Name = subName, Initial = subName.Substring(0, 1) };
                    ProfileCompat.SetByKey(profile.Subjects, subId, newSub);
                    subMap[subName] = subId;
                }
            }

            var classInfo = new ClassInfo
            {
                Index = i,
                IsEnabled = true
            };
            ProfileCompat.SetSubjectId(classInfo, subId);
            plan.Classes.Add(classInfo);
        }

        _profileService.SaveProfile();
        return new ToolExecutionResult
        {
            ToolCallId = toolId,
            Name = "set_class_plan",
            Success = true,
            Result = $"课表【{planName}】已配置完毕，共安排 {plan.Classes.Count} 节课（对应：{(detectedWeekDay >= 0 ? GetChineseWeekDay((DayOfWeek)detectedWeekDay) : "未指定固定星期")}）。"
        };
    }

    private ToolExecutionResult ExecuteSetupTempClassPlan(string toolId, JsonObject args)
    {
        if (_profileService == null || _profileService.Profile == null)
        {
            return new ToolExecutionResult { ToolCallId = toolId, Name = "setup_temp_class_plan", Success = false, Result = "Profile 服务未就绪" };
        }

        var profile = _profileService.Profile;

        var planName = args["sourcePlanName"]?.GetValue<string>()?.Trim()
                       ?? args["dayOfWeek"]?.GetValue<string>()?.Trim()
                       ?? args["plan"]?.GetValue<string>()?.Trim()
                       ?? "";

        var dateQuery = args["targetDate"]?.GetValue<string>()?.Trim()
                        ?? args["date"]?.GetValue<string>()?.Trim();

        int? classIndex = null;
        if (args.TryGetPropertyValue("classIndex", out var ciNode))
        {
            if (ciNode != null && int.TryParse(ciNode.ToString(), out var parsedCi))
            {
                classIndex = parsedCi;
            }
        }

        var subjectName = args["subject"]?.GetValue<string>()?.Trim()
                          ?? args["targetSubject"]?.GetValue<string>()?.Trim();

        var targetDate = ResolveTargetDate(dateQuery, planName);

        var sourcePlanEntry = FindSourcePlan(profile, planName, targetDate);
        if (sourcePlanEntry.Value == null)
        {
            return new ToolExecutionResult
            {
                ToolCallId = toolId,
                Name = "setup_temp_class_plan",
                Success = false,
                Result = $"未能匹配到适合的基础课表【{planName}】。当前已有课表：{string.Join(", ", profile.ClassPlans.Values.Select(c => c.Name))}"
            };
        }

        var sourceKey = sourcePlanEntry.Key;
        var sourcePlan = sourcePlanEntry.Value;

        ClassPlan? tempPlan = null;
        var tempId = "";

        if (profile.OrderedSchedules.TryGetValue(targetDate.Date, out var existingOrdered)
            && ProfileCompat.TryGetClassPlan(profile, ProfileCompat.GetClassPlanId(existingOrdered), out var existingOverlay)
            && existingOverlay != null
            && existingOverlay.IsOverlay)
        {
            tempId = ProfileCompat.GetClassPlanId(existingOrdered);
            tempPlan = existingOverlay;
        }
        else
        {
            var createdId = ProfileCompat.CreateTempClassPlan(_profileService, sourceKey, targetDate.Date);
            if (!string.IsNullOrEmpty(createdId)
                && ProfileCompat.TryGetClassPlan(profile, createdId, out var cp)
                && cp != null)
            {
                tempId = createdId;
                tempPlan = cp;
            }
            else
            {
                try
                {
                    var serialized = JsonSerializer.Serialize(sourcePlan);
                    var newPlan = JsonSerializer.Deserialize<ClassPlan>(serialized) ?? new ClassPlan();
                    newPlan.IsOverlay = true;
                    newPlan.Name = sourcePlan.Name + "（临时层）";
                    ProfileCompat.SetOverlaySourceId(newPlan, sourceKey);
                    newPlan.OverlaySetupTime = targetDate.Date;
                    tempId = Guid.NewGuid().ToString();
                    ProfileCompat.SetByKey(profile.ClassPlans, tempId, newPlan);
                    var schedule = new OrderedSchedule();
                    ProfileCompat.SetClassPlanId(schedule, tempId);
                    profile.OrderedSchedules[targetDate.Date] = schedule;
                    tempPlan = newPlan;
                }
                catch (Exception ex)
                {
                    return new ToolExecutionResult { ToolCallId = toolId, Name = "setup_temp_class_plan", Success = false, Result = $"复制创建临时课表异常: {ex.Message}" };
                }
            }
        }

        if (tempPlan == null)
        {
            return new ToolExecutionResult { ToolCallId = toolId, Name = "setup_temp_class_plan", Success = false, Result = "创建或获取临时课表对象失败" };
        }

        string changeDescription = "";
        var subMap = profile.Subjects.ToDictionary(kv => kv.Value.Name, kv => Convert.ToString(kv.Key) ?? "", StringComparer.OrdinalIgnoreCase);

        var targetIndices = new List<int>();
        if (args.TryGetPropertyValue("classIndices", out var cisNode) && cisNode is JsonArray cisArr)
        {
            foreach (var item in cisArr)
            {
                if (item != null && int.TryParse(item.ToString(), out var ci))
                {
                    targetIndices.Add(ci);
                }
            }
        }
        else if (classIndex.HasValue)
        {
            targetIndices.Add(classIndex.Value);
        }

        if (targetIndices.Count > 0 && !string.IsNullOrWhiteSpace(subjectName))
        {
            if (!subMap.TryGetValue(subjectName, out var targetSubGuid))
            {
                targetSubGuid = Guid.NewGuid().ToString();
                var newSub = new Subject { Name = subjectName, Initial = subjectName.Substring(0, 1) };
                ProfileCompat.SetByKey(profile.Subjects, targetSubGuid, newSub);
                subMap[subjectName] = targetSubGuid;
            }

            var changedSlots = new List<string>();
            foreach (var ci in targetIndices)
            {
                int targetIdx = ci == -1 ? tempPlan.Classes.Count - 1 : (ci > 0 ? ci - 1 : 0);
                if (targetIdx >= 0 && targetIdx < tempPlan.Classes.Count)
                {
                    var oldSubName = ProfileCompat.TryGetSubject(profile, ProfileCompat.GetSubjectId(tempPlan.Classes[targetIdx]), out var oldSub) && oldSub != null ? oldSub.Name : "未定";
                    ProfileCompat.SetSubjectId(tempPlan.Classes[targetIdx], targetSubGuid);
                    tempPlan.Classes[targetIdx].IsChangedClass = true;
                    changedSlots.Add($"第 {targetIdx + 1} 节（原【{oldSubName}】）");
                }
            }

            changeDescription = $"已将 {string.Join(" 与 ", changedSlots)} 变更为【{subjectName}】";
        }
        else if (args.TryGetPropertyValue("modifiedClasses", out var modNode) && modNode is JsonArray modArr)
        {
            int changedCount = 0;
            for (int i = 0; i < Math.Min(modArr.Count, tempPlan.Classes.Count); i++)
            {
                var sName = modArr[i]?.GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(sName)) continue;

                if (!subMap.TryGetValue(sName, out var sGuid))
                {
                    sGuid = Guid.NewGuid().ToString();
                    var newSub = new Subject { Name = sName, Initial = sName.Substring(0, 1) };
                    ProfileCompat.SetByKey(profile.Subjects, sGuid, newSub);
                    subMap[sName] = sGuid;
                }

                ProfileCompat.SetSubjectId(tempPlan.Classes[i], sGuid);
                tempPlan.Classes[i].IsChangedClass = true;
                changedCount++;
            }
            changeDescription = $"已更新 {changedCount} 节课程安排";
        }
        else
        {
            changeDescription = "已同步生成临时层";
        }

        profile.IsOverlayClassPlanEnabled = true;
        ProfileCompat.SetOverlayClassPlanId(profile, tempId);
        ProfileCompat.SetTempClassPlanId(profile, tempId);
        profile.TempClassPlanSetupTime = targetDate.Date;
        var orderedSchedule = new OrderedSchedule();
        ProfileCompat.SetClassPlanId(orderedSchedule, tempId);
        profile.OrderedSchedules[targetDate.Date] = orderedSchedule;

        _profileService.SaveProfile();

        return new ToolExecutionResult
        {
            ToolCallId = toolId,
            Name = "setup_temp_class_plan",
            Success = true,
            Result = $"临时调课已生效！\n" +
                     $"• 生效日期：{targetDate:yyyy-MM-dd}（{GetChineseWeekDay(targetDate.DayOfWeek)}）\n" +
                     $"• 基础课表：【{sourcePlan.Name}】\n" +
                     $"• 调整内容：{changeDescription}\n" +
                     $"• 状态：已即时激活并在 ClassIsland 中生效显示。"
        };
    }

    private ToolExecutionResult ExecuteGetAppSettings(string toolId)
    {
        var settingsObj = GetAppHostSettingsObject();
        if (settingsObj == null)
        {
            return new ToolExecutionResult
            {
                ToolCallId = toolId,
                Name = "get_app_settings",
                Success = false,
                Result = "当前宿主设置服务未就绪或未找到"
            };
        }

        var resultObj = new JsonObject();
        var type = settingsObj.GetType();

        foreach (var propName in AppSettingPropertyNames)
        {
            var p = type.GetProperty(propName);
            if (p == null) continue;

            object? val;
            try { val = p.GetValue(settingsObj); }
            catch { continue; }

            switch (val)
            {
                case null:
                    break;
                case bool b:
                    resultObj[propName] = b;
                    break;
                case int i:
                    resultObj[propName] = i;
                    break;
                case double d:
                    resultObj[propName] = d;
                    break;
                case string s:
                    resultObj[propName] = s;
                    break;
                case Enum e:
                    resultObj[propName] = Convert.ToInt32(e);
                    break;
                case System.Collections.IEnumerable:
                    break;
                default:
                    resultObj[propName] = val.ToString();
                    break;
            }
        }

        return new ToolExecutionResult
        {
            ToolCallId = toolId,
            Name = "get_app_settings",
            Success = true,
            Result = resultObj.ToJsonString()
        };
    }

    private ToolExecutionResult ExecuteUpdateAppSettings(string toolId, JsonObject args)
    {
        var settingsObj = GetAppHostSettingsObject();
        if (settingsObj == null)
        {
            return new ToolExecutionResult
            {
                ToolCallId = toolId,
                Name = "update_app_settings",
                Success = false,
                Result = "当前宿主设置服务未就绪，无法通过接口修改设置"
            };
        }

        var type = settingsObj.GetType();
        var updatedList = new List<string>();

        bool TrySetProp(string propName, JsonNode? node)
        {
            if (node == null) return false;
            var p = type.GetProperty(propName);
            if (p == null || !p.CanWrite) return false;

            try
            {
                var raw = node.ToString().Trim().Trim('"');
                object converted;
                if (p.PropertyType == typeof(bool))
                    converted = bool.TryParse(raw, out var b)
                        ? b
                        : Convert.ToBoolean(double.TryParse(raw, out var bn) && bn != 0);
                else if (p.PropertyType == typeof(int))
                    converted = int.TryParse(raw, out var i) ? i : Convert.ToInt32(double.Parse(raw));
                else if (p.PropertyType == typeof(double))
                    converted = double.Parse(raw);
                else if (p.PropertyType == typeof(float))
                    converted = float.Parse(raw);
                else if (p.PropertyType == typeof(string))
                    converted = raw;
                else if (p.PropertyType.IsEnum)
                    converted = Enum.ToObject(p.PropertyType, int.Parse(raw));
                else
                    converted = Convert.ChangeType(raw, p.PropertyType);

                p.SetValue(settingsObj, converted);
                updatedList.Add($"{propName} = {converted}");
                return true;
            }
            catch
            {
                return false;
            }
        }

        // 1) 按白名单匹配具名参数（支持 camelCase / PascalCase 两种写法）
        foreach (var propName in AppSettingPropertyNames)
        {
            var camel = char.ToLowerInvariant(propName[0]) + propName[1..];
            if (args.TryGetPropertyValue(camel, out var node) || args.TryGetPropertyValue(propName, out node))
            {
                TrySetProp(propName, node);
            }
        }

        // 2) 常见别名
        if (args.TryGetPropertyValue("timeOffset", out var toAlias) || args.TryGetPropertyValue("offset", out toAlias))
        {
            TrySetProp("TimeOffsetSeconds", toAlias);
        }

        // 3) 主题切换需要通知主题服务刷新，仅改属性不会立即重绘
        if (updatedList.Any(s => s.StartsWith("Theme = ")))
        {
            try
            {
                var themeServiceType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(x => x.FullName == "ClassIsland.Core.Abstractions.Services.IThemeService");
                if (themeServiceType != null)
                {
                    var ts = IAppHost.Host?.Services.GetService(themeServiceType);
                    var setMethod = themeServiceType.GetMethod("SetTheme");
                    setMethod?.Invoke(ts, new object?[] { type.GetProperty("Theme")?.GetValue(settingsObj), null });
                }
            }
            catch { }
        }

        // 4) 任意键值对兜底（仍受白名单约束，防止误改集合类设置）
        if (args.TryGetPropertyValue("customSettings", out var customNode) && customNode is JsonObject customObj)
        {
            foreach (var kv in customObj)
            {
                var matched = AppSettingPropertyNames.FirstOrDefault(
                    n => n.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                if (matched != null)
                {
                    TrySetProp(matched, kv.Value);
                }
            }
        }

        if (updatedList.Count == 0)
        {
            return new ToolExecutionResult
            {
                ToolCallId = toolId,
                Name = "update_app_settings",
                Success = false,
                Result = "未提供或未匹配到需要修改的有效设置项"
            };
        }

        return new ToolExecutionResult
        {
            ToolCallId = toolId,
            Name = "update_app_settings",
            Success = true,
            Result = $"设置修改成功且已即时生效（无需用户手动点击）：\n• " + string.Join("\n• ", updatedList)
        };
    }

    private static object? GetAppHostSettingsObject()
    {
        try
        {
            var host = IAppHost.Host;
            if (host == null) return null;

            var serviceType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.FullName == "ClassIsland.Services.SettingsService");

            if (serviceType == null) return null;

            var serviceInstance = host.Services.GetService(serviceType);
            if (serviceInstance == null) return null;

            var settingsProp = serviceType.GetProperty("Settings");
            return settingsProp?.GetValue(serviceInstance);
        }
        catch
        {
            return null;
        }
    }

    private ToolExecutionResult ExecuteGetPluginSettings(string toolId)
    {
        if (_pluginSettings == null)
        {
            return new ToolExecutionResult
            {
                ToolCallId = toolId,
                Name = "get_plugin_settings",
                Success = false,
                Result = "插件设置未就绪"
            };
        }

        _pluginSettings.EnsureProvidersInitialized();

        var providers = new JsonArray();
        foreach (var p in _pluginSettings.Providers)
        {
            providers.Add(new JsonObject
            {
                ["id"] = p.Id,
                ["name"] = p.Name,
                ["baseUrl"] = p.BaseUrl,
                ["hasApiKey"] = !string.IsNullOrWhiteSpace(p.ApiKey),
                ["models"] = new JsonArray(p.Models.Select(m => (JsonNode)new JsonObject
                {
                    ["name"] = m.Name,
                    ["isEnabled"] = m.IsEnabled
                }).ToArray())
            });
        }

        var result = new JsonObject
        {
            ["providers"] = providers,
            ["activeProviderId"] = _pluginSettings.ActiveProviderId,
            ["activeModelName"] = _pluginSettings.ActiveModelName,
            ["isMemoryEnabled"] = _pluginSettings.IsMemoryEnabled,
            ["isCommandExecutionEnabled"] = _pluginSettings.IsCommandExecutionEnabled,
            ["customPrompt"] = _pluginSettings.CustomPrompt,
            ["customMemory"] = _pluginSettings.CustomMemory,
            ["hasPassword"] = !string.IsNullOrEmpty(_pluginSettings.PasswordHash),
            ["isTotpEnabled"] = !string.IsNullOrWhiteSpace(_pluginSettings.TotpSecret),
            ["isTotpPending"] = string.IsNullOrWhiteSpace(_pluginSettings.TotpSecret)
                                 && !string.IsNullOrWhiteSpace(_pluginSettings.PendingTotpSecret)
        };

        return new ToolExecutionResult
        {
            ToolCallId = toolId,
            Name = "get_plugin_settings",
            Success = true,
            Result = result.ToJsonString()
        };
    }

    private ToolExecutionResult ExecuteUpdatePluginSettings(string toolId, JsonObject args)
    {
        if (_pluginSettings == null)
        {
            return new ToolExecutionResult
            {
                ToolCallId = toolId,
                Name = "update_plugin_settings",
                Success = false,
                Result = "插件设置未就绪"
            };
        }

        _pluginSettings.EnsureProvidersInitialized();
        var changes = new List<string>();
        var action = args["action"]?.ToString().Trim().ToLowerInvariant() ?? "";

        // ---------- 供应商增删改 ----------
        switch (action)
        {
            case "add_provider":
            {
                var name = args["providerName"]?.ToString().Trim() ?? "";
                var baseUrl = args["baseUrl"]?.ToString().Trim() ?? "";
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(baseUrl))
                {
                    return new ToolExecutionResult
                    {
                        ToolCallId = toolId, Name = "update_plugin_settings", Success = false,
                        Result = "add_provider 需要 providerName 与 baseUrl"
                    };
                }

                if (_pluginSettings.Providers.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    return new ToolExecutionResult
                    {
                        ToolCallId = toolId, Name = "update_plugin_settings", Success = false,
                        Result = $"供应商「{name}」已存在，请改用 set_provider 修改"
                    };
                }

                var provider = new AiProviderConfig
                {
                    Name = name,
                    BaseUrl = baseUrl,
                    ApiKey = args["apiKey"]?.ToString().Trim() ?? "",
                    Models = new List<AiModelItem>()
                };
                if (args["models"] is JsonArray modelArr)
                {
                    foreach (var m in modelArr)
                    {
                        var mn = m?.ToString().Trim() ?? "";
                        if (!string.IsNullOrWhiteSpace(mn)) provider.Models.Add(new AiModelItem(mn, true));
                    }
                }

                _pluginSettings.Providers.Add(provider);
                if (string.IsNullOrWhiteSpace(_pluginSettings.ActiveProviderId))
                {
                    _pluginSettings.ActiveProviderId = provider.Id;
                }
                changes.Add($"已新增供应商「{name}」（{baseUrl}），模型 {provider.Models.Count} 个");
                break;
            }

            case "set_provider":
            {
                var target = ResolveProvider(args["providerName"]?.ToString().Trim() ?? "");
                if (target == null)
                {
                    return new ToolExecutionResult
                    {
                        ToolCallId = toolId, Name = "update_plugin_settings", Success = false,
                        Result = "未找到指定供应商，可先用 get_plugin_settings 查看现有供应商"
                    };
                }

                var oldName = target.Name;
                if (args["newName"] != null) target.Name = args["newName"]!.ToString().Trim();
                if (args["baseUrl"] != null) target.BaseUrl = args["baseUrl"]!.ToString().Trim();
                if (args["apiKey"] != null) target.ApiKey = args["apiKey"]!.ToString().Trim();
                changes.Add($"已更新供应商「{oldName}」");
                break;
            }

            case "delete_provider":
            {
                var target = ResolveProvider(args["providerName"]?.ToString().Trim() ?? "");
                if (target == null)
                {
                    return new ToolExecutionResult
                    {
                        ToolCallId = toolId, Name = "update_plugin_settings", Success = false,
                        Result = "未找到指定供应商"
                    };
                }
                if (_pluginSettings.Providers.Count <= 1)
                {
                    return new ToolExecutionResult
                    {
                        ToolCallId = toolId, Name = "update_plugin_settings", Success = false,
                        Result = "至少需保留一个供应商，无法删除"
                    };
                }

                _pluginSettings.Providers.Remove(target);
                if (_pluginSettings.ActiveProviderId == target.Id)
                {
                    _pluginSettings.ActiveProviderId = _pluginSettings.Providers.FirstOrDefault()?.Id ?? "";
                }
                changes.Add($"已删除供应商「{target.Name}」");
                break;
            }

            case "add_model":
            case "remove_model":
            case "enable_model":
            case "disable_model":
            {
                var target = ResolveProvider(args["providerName"]?.ToString().Trim() ?? "");
                if (target == null)
                {
                    return new ToolExecutionResult
                    {
                        ToolCallId = toolId, Name = "update_plugin_settings", Success = false,
                        Result = "未找到指定供应商"
                    };
                }

                var modelName = args["modelName"]?.ToString().Trim() ?? "";
                if (string.IsNullOrWhiteSpace(modelName))
                {
                    return new ToolExecutionResult
                    {
                        ToolCallId = toolId, Name = "update_plugin_settings", Success = false,
                        Result = "缺少 modelName"
                    };
                }

                var existing = target.Models.FirstOrDefault(m => m.Name == modelName);
                if (action == "add_model")
                {
                    if (existing == null)
                    {
                        target.Models.Add(new AiModelItem(modelName, true));
                        changes.Add($"已为「{target.Name}」添加模型 {modelName}（已启用）");
                    }
                    else
                    {
                        existing.IsEnabled = true;
                        changes.Add($"模型 {modelName} 已存在，已置为启用");
                    }
                }
                else if (action == "remove_model")
                {
                    if (existing == null)
                    {
                        return new ToolExecutionResult
                        {
                            ToolCallId = toolId, Name = "update_plugin_settings", Success = false,
                            Result = $"供应商「{target.Name}」下没有模型 {modelName}"
                        };
                    }
                    target.Models.Remove(existing);
                    changes.Add($"已从「{target.Name}」移除模型 {modelName}");
                }
                else
                {
                    if (existing == null)
                    {
                        return new ToolExecutionResult
                        {
                            ToolCallId = toolId, Name = "update_plugin_settings", Success = false,
                            Result = $"供应商「{target.Name}」下没有模型 {modelName}"
                        };
                    }
                    existing.IsEnabled = action == "enable_model";
                    changes.Add($"模型 {modelName} 已{(existing.IsEnabled ? "启用" : "停用")}");
                }
                break;
            }

            case "set_active_model":
            {
                var target = ResolveProvider(args["providerName"]?.ToString().Trim() ?? "");
                var modelName = args["modelName"]?.ToString().Trim() ?? "";
                if (target == null || string.IsNullOrWhiteSpace(modelName))
                {
                    return new ToolExecutionResult
                    {
                        ToolCallId = toolId, Name = "update_plugin_settings", Success = false,
                        Result = "set_active_model 需要 providerName 与 modelName"
                    };
                }

                var model = target.Models.FirstOrDefault(m => m.Name == modelName);
                if (model == null)
                {
                    target.Models.Add(new AiModelItem(modelName, true));
                }
                else
                {
                    model.IsEnabled = true;
                }

                _pluginSettings.SelectActiveModel(target.Id, modelName);
                changes.Add($"当前模型已切换为 [{target.Name}] {modelName}");
                break;
            }

            case "":
                break; // 纯字段更新，无需 action

            default:
                return new ToolExecutionResult
                {
                    ToolCallId = toolId, Name = "update_plugin_settings", Success = false,
                    Result = $"未知操作类型: {action}"
                };
        }

        // ---------- 纯字段更新 ----------
        if (args["isMemoryEnabled"] is JsonNode memNode)
        {
            _pluginSettings.IsMemoryEnabled = memNode.GetValue<bool>();
            changes.Add($"长期记忆已{(_pluginSettings.IsMemoryEnabled ? "启用" : "停用")}");
        }
        if (args["isCommandExecutionEnabled"] is JsonNode cmdNode)
        {
            var enabled = cmdNode.GetValue<bool>();
            _pluginSettings.IsCommandExecutionEnabled = enabled;
            changes.Add($"命令行执行权限已{(enabled ? "开启" : "关闭")}");
        }
        if (args["customPrompt"] is JsonNode promptNode)
        {
            _pluginSettings.CustomPrompt = promptNode.ToString();
            changes.Add("自定义系统提示词已更新");
        }
        if (args["maxToolIterations"] is JsonNode iterNode)
        {
            var raw = iterNode.ToString().Trim().Trim('"');
            if (int.TryParse(raw, out var parsedIter))
            {
                _pluginSettings.MaxToolIterations = Math.Clamp(parsedIter, 1, 50);
                changes.Add($"工具调用轮数上限已设为 {_pluginSettings.MaxToolIterations}");
            }
            else
            {
                changes.Add("工具调用轮数上限未修改（需要 1–50 的整数）");
            }
        }
        if (args["customMemory"] is JsonNode memoryNode)
        {
            _pluginSettings.CustomMemory = memoryNode.ToString();
            changes.Add("长期记忆内容已更新");
        }

        if (changes.Count == 0)
        {
            return new ToolExecutionResult
            {
                ToolCallId = toolId, Name = "update_plugin_settings", Success = false,
                Result = "未提供任何有效的插件设置修改项"
            };
        }

        try { _onSavePluginSettings?.Invoke(); }
        catch { }

        return new ToolExecutionResult
        {
            ToolCallId = toolId,
            Name = "update_plugin_settings",
            Success = true,
            Result = "插件设置已更新并保存：\n• " + string.Join("\n• ", changes)
        };
    }

    private AiProviderConfig? ResolveProvider(string nameOrId)
    {
        if (_pluginSettings == null || string.IsNullOrWhiteSpace(nameOrId)) return null;

        return _pluginSettings.Providers.FirstOrDefault(p => p.Id == nameOrId)
               ?? _pluginSettings.Providers.FirstOrDefault(p => p.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase))
               ?? _pluginSettings.Providers.FirstOrDefault(p => p.Name.Contains(nameOrId, StringComparison.OrdinalIgnoreCase));
    }

    private ToolExecutionResult ExecuteNavigateAppPage(string toolId, JsonObject args)    {
        var uriStr = args["pageUri"]?.GetValue<string>()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(uriStr))
        {
            return new ToolExecutionResult { ToolCallId = toolId, Name = "navigate_app_page", Success = false, Result = "URI 不能为空" };
        }

        if (_uriService != null)
        {
            _uriService.NavigateWrapped(new Uri(uriStr));
            return new ToolExecutionResult { ToolCallId = toolId, Name = "navigate_app_page", Success = true, Result = $"已成功跳转到页面: {uriStr}" };
        }

        return new ToolExecutionResult { ToolCallId = toolId, Name = "navigate_app_page", Success = false, Result = "导航服务不可用" };
    }

    private ToolExecutionResult ExecuteGetCurrentTime(string toolId)
    {
        var now = DateTime.Now;
        var offsetSeconds = 0.0;

        try
        {
            var settingsObj = GetAppHostSettingsObject();
            if (settingsObj != null)
            {
                var prop = settingsObj.GetType().GetProperty("TimeOffsetSeconds");
                if (prop != null && prop.GetValue(settingsObj) is double d)
                {
                    offsetSeconds = d;
                }
            }
        }
        catch { }

        var exactNow = now.AddSeconds(offsetSeconds);
        var todayChinese = GetChineseWeekDay(exactNow.DayOfWeek);
        var tomorrow = exactNow.AddDays(1);
        var tomorrowChinese = GetChineseWeekDay(tomorrow.DayOfWeek);

        var result = new JsonObject
        {
            ["currentTime"] = exactNow.ToString("yyyy-MM-dd HH:mm:ss"),
            ["currentDate"] = exactNow.ToString("yyyy-MM-dd"),
            ["currentDayOfWeek"] = todayChinese,
            ["dayOfWeekNumber"] = (int)exactNow.DayOfWeek,
            ["tomorrowDate"] = tomorrow.ToString("yyyy-MM-dd"),
            ["tomorrowDayOfWeek"] = tomorrowChinese,
            ["isWeekend"] = exactNow.DayOfWeek == DayOfWeek.Saturday || exactNow.DayOfWeek == DayOfWeek.Sunday,
            ["timeOffsetSeconds"] = offsetSeconds
        };

        return new ToolExecutionResult
        {
            ToolCallId = toolId,
            Name = "get_current_time",
            Success = true,
            Result = result.ToJsonString()
        };
    }

    private ToolExecutionResult ExecuteUpdateMemory(string toolId, JsonObject args)
    {
        if (_pluginSettings == null)
        {
            return new ToolExecutionResult
            {
                ToolCallId = toolId,
                Name = "update_memory",
                Success = false,
                Result = "记忆系统存储配置未就绪"
            };
        }

        if (!_pluginSettings.IsMemoryEnabled)
        {
            return new ToolExecutionResult
            {
                ToolCallId = toolId,
                Name = "update_memory",
                Success = false,
                Result = "用户已在设置中关闭了 AI 记忆系统，无法写入或更新新记忆。"
            };
        }

        var action = args["action"]?.GetValue<string>()?.ToLowerInvariant() ?? "add";
        var content = args["content"]?.GetValue<string>()?.Trim() ?? "";

        switch (action)
        {
            case "add":
                if (string.IsNullOrWhiteSpace(content))
                {
                    return new ToolExecutionResult { ToolCallId = toolId, Name = "update_memory", Success = false, Result = "记忆内容不能为空" };
                }
                var currentMemory = _pluginSettings.CustomMemory?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(currentMemory))
                {
                    _pluginSettings.CustomMemory = $"- {content}";
                }
                else
                {
                    _pluginSettings.CustomMemory = $"{currentMemory}\n- {content}";
                }
                _onSavePluginSettings?.Invoke();
                SettingsManager.Save(_pluginSettings);
                SettingsManager.NotifySettingsChanged(_pluginSettings);
                return new ToolExecutionResult
                {
                    ToolCallId = toolId,
                    Name = "update_memory",
                    Success = true,
                    Result = $"已成功添加记忆：“{content}”。当前记忆已更新并已持久化保存。"
                };

            case "set":
                _pluginSettings.CustomMemory = content;
                _onSavePluginSettings?.Invoke();
                SettingsManager.Save(_pluginSettings);
                SettingsManager.NotifySettingsChanged(_pluginSettings);
                return new ToolExecutionResult
                {
                    ToolCallId = toolId,
                    Name = "update_memory",
                    Success = true,
                    Result = "已成功覆盖并更新全部记忆库内容。"
                };

            case "clear":
                _pluginSettings.CustomMemory = "";
                _onSavePluginSettings?.Invoke();
                SettingsManager.Save(_pluginSettings);
                SettingsManager.NotifySettingsChanged(_pluginSettings);
                return new ToolExecutionResult
                {
                    ToolCallId = toolId,
                    Name = "update_memory",
                    Success = true,
                    Result = "已清空所有长期记忆。"
                };

            default:
                return new ToolExecutionResult
                {
                    ToolCallId = toolId,
                    Name = "update_memory",
                    Success = false,
                    Result = $"不支持的操作类型: {action}，支持的操作为 add, set, clear。"
                };
        }
    }

    private ToolExecutionResult ExecuteCommand(string toolId, JsonObject args)
    {
        if (_pluginSettings?.IsCommandExecutionEnabled != true)
        {
            return new ToolExecutionResult
            {
                ToolCallId = toolId,
                Name = "execute_command",
                Success = false,
                Result = "用户未在【设置 -> IslandAgent设置 -> 高级偏好】中开启“允许执行命令行命令”权限，命令已被系统拒绝执行。"
            };
        }

        var cmd = args["command"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(cmd))
        {
            return new ToolExecutionResult { ToolCallId = toolId, Name = "execute_command", Success = false, Result = "命令不能为空" };
        }

        var timeoutSec = 15;
        if (args.TryGetPropertyValue("timeoutSeconds", out var toNode) && toNode != null)
        {
            if (int.TryParse(toNode.ToString(), out var parsedSec) && parsedSec > 0)
            {
                timeoutSec = Math.Min(parsedSec, 60);
            }
        }

        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {cmd}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.Default,
                StandardErrorEncoding = System.Text.Encoding.Default
            };

            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutSec * 1000))
            {
                try { process.Kill(); } catch { }
                return new ToolExecutionResult
                {
                    ToolCallId = toolId,
                    Name = "execute_command",
                    Success = false,
                    Result = $"命令执行超时（超过 {timeoutSec} 秒），进程已终止。"
                };
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            var exitCode = process.ExitCode;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"退出代码: {exitCode}");
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                sb.AppendLine("【标准输出】:");
                sb.AppendLine(stdout.Trim());
            }
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                sb.AppendLine("【错误输出】:");
                sb.AppendLine(stderr.Trim());
            }

            return new ToolExecutionResult
            {
                ToolCallId = toolId,
                Name = "execute_command",
                Success = exitCode == 0,
                Result = sb.ToString().Trim()
            };
        }
        catch (Exception ex)
        {
            return new ToolExecutionResult
            {
                ToolCallId = toolId,
                Name = "execute_command",
                Success = false,
                Result = $"执行异常: {ex.Message}"
            };
        }
    }

    private static DateTime ResolveTargetDate(string? dateQuery, string? planName)
    {
        var today = DateTime.Today;
        if (!string.IsNullOrWhiteSpace(dateQuery))
        {
            var q = dateQuery.Trim().ToLowerInvariant();
            if (q.Contains("明") || q.Contains("tomorrow"))
            {
                return today.AddDays(1);
            }
            if (q.Contains("后天"))
            {
                return today.AddDays(2);
            }
            if (q.Contains("今") || q.Contains("today"))
            {
                return today;
            }
            if (DateTime.TryParse(dateQuery, out var parsedDate))
            {
                return parsedDate.Date;
            }
            int targetWd = ParseWeekDay(dateQuery);
            if (targetWd >= 0)
            {
                int diff = (targetWd - (int)today.DayOfWeek + 7) % 7;
                return today.AddDays(diff);
            }
        }

        if (!string.IsNullOrWhiteSpace(planName))
        {
            int planWd = ParseWeekDay(planName);
            if (planWd >= 0)
            {
                int diff = (planWd - (int)today.DayOfWeek + 7) % 7;
                return today.AddDays(diff);
            }
        }

        return today;
    }

    /// <summary>
    /// 按名称/星期查找基础课表（排除临时层）。返回的键统一为字符串形式，
    /// 以屏蔽 1.x(string) 与 2.x(Guid) 的字典键类型差异。
    /// </summary>
    private static KeyValuePair<string, ClassPlan> FindSourcePlan(Profile profile, string planName, DateTime targetDate)
    {
        if (!string.IsNullOrWhiteSpace(planName))
        {
            var exactMatch = profile.ClassPlans.FirstOrDefault(kv => !kv.Value.IsOverlay && string.Equals(kv.Value.Name, planName, StringComparison.OrdinalIgnoreCase));
            if (exactMatch.Value != null) return ToStringKeyed(exactMatch);

            var normPlan = NormalizeWeekDayName(planName);
            var fuzzyMatch = profile.ClassPlans.FirstOrDefault(kv => !kv.Value.IsOverlay && NormalizeWeekDayName(kv.Value.Name).Contains(normPlan));
            if (fuzzyMatch.Value != null) return ToStringKeyed(fuzzyMatch);
        }

        int targetDayInt = (int)targetDate.DayOfWeek;
        var ruleMatch = profile.ClassPlans.FirstOrDefault(kv => !kv.Value.IsOverlay && kv.Value.TimeRule.WeekDay == targetDayInt);
        if (ruleMatch.Value != null) return ToStringKeyed(ruleMatch);

        var dayChar = GetChineseWeekDayChar(targetDate.DayOfWeek);
        var nameMatch = profile.ClassPlans.FirstOrDefault(kv => !kv.Value.IsOverlay && kv.Value.Name.Contains(dayChar));
        if (nameMatch.Value != null) return ToStringKeyed(nameMatch);

        return ToStringKeyed(profile.ClassPlans.FirstOrDefault(kv => !kv.Value.IsOverlay));
    }

    /// <summary>
    /// 统一课表字典键为字符串，屏蔽 1.x(string) 与 2.x(Guid) 的差异。
    /// </summary>
    private static KeyValuePair<string, ClassPlan> ToStringKeyed<TKey>(KeyValuePair<TKey, ClassPlan> pair)
        => new(Convert.ToString(pair.Key) ?? string.Empty, pair.Value);

    public static int ParseWeekDay(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        var s = name.Replace("礼拜", "周").Replace("星期", "周");
        if (s.Contains("周一") || s.Contains("星期一") || s.Contains("一")) return (int)DayOfWeek.Monday;
        if (s.Contains("周二") || s.Contains("星期二") || s.Contains("二")) return (int)DayOfWeek.Tuesday;
        if (s.Contains("周三") || s.Contains("星期三") || s.Contains("三")) return (int)DayOfWeek.Wednesday;
        if (s.Contains("周四") || s.Contains("星期四") || s.Contains("四")) return (int)DayOfWeek.Thursday;
        if (s.Contains("周五") || s.Contains("星期五") || s.Contains("五")) return (int)DayOfWeek.Friday;
        if (s.Contains("周六") || s.Contains("星期六") || s.Contains("六")) return (int)DayOfWeek.Saturday;
        if (s.Contains("周日") || s.Contains("周天") || s.Contains("星期日") || s.Contains("星期天") || s.Contains("日") || s.Contains("天")) return (int)DayOfWeek.Sunday;
        return -1;
    }

    public static string NormalizeWeekDayName(string name)
    {
        return name.Replace("星期", "周").Replace("礼拜", "周").Trim();
    }

    public static string GetChineseWeekDay(DayOfWeek dow)
    {
        return dow switch
        {
            DayOfWeek.Monday => "周一",
            DayOfWeek.Tuesday => "周二",
            DayOfWeek.Wednesday => "周三",
            DayOfWeek.Thursday => "周四",
            DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六",
            DayOfWeek.Sunday => "周日",
            _ => "未知"
        };
    }

    public static string GetChineseWeekDayChar(DayOfWeek dow)
    {
        return dow switch
        {
            DayOfWeek.Monday => "一",
            DayOfWeek.Tuesday => "二",
            DayOfWeek.Wednesday => "三",
            DayOfWeek.Thursday => "四",
            DayOfWeek.Friday => "五",
            DayOfWeek.Saturday => "六",
            DayOfWeek.Sunday => "日",
            _ => ""
        };
    }
}
