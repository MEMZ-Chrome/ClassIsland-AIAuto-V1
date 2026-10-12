using ClassIsland.Shared.IPC.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;

namespace ClassIsland.Automation.Core.Services;

/// <summary>
/// ClassIsland 1.x 与 2.x 的档案模型差异适配层。
///
/// 两代差异（已对照双方源码确认）：
///   字典键：1.x 为 <c>ObservableDictionary&lt;string, T&gt;</c>，2.x 为 <c>ObservableDictionary&lt;Guid, T&gt;</c>
///   时间项：1.x 用 <c>StartSecond</c>/<c>EndSecond</c>（DateTime），2.x 用 <c>StartTime</c>/<c>EndTime</c>（TimeSpan）
///
/// 这里只做最小适配，不改变档案本身的语义。
/// </summary>
internal static class ProfileCompat
{
    /// <summary>
    /// 把节次的开始时间统一读成 TimeSpan，供工具层格式化输出。
    /// </summary>
    public static TimeSpan GetStart(TimeLayoutItem item)
    {
#if CLASSISLAND_1X
        return item.StartSecond.TimeOfDay;
#else
        return item.StartTime;
#endif
    }

    /// <summary>
    /// 把节次的结束时间统一读成 TimeSpan。
    /// </summary>
    public static TimeSpan GetEnd(TimeLayoutItem item)
    {
#if CLASSISLAND_1X
        return item.EndSecond.TimeOfDay;
#else
        return item.EndTime;
#endif
    }

    /// <summary>
    /// 写入节次的起止时间。传入的是"当天内的时刻"，1.x 侧需要转回 DateTime。
    /// </summary>
    public static void SetTimeRange(TimeLayoutItem item, TimeSpan start, TimeSpan end)
    {
#if CLASSISLAND_1X
        item.StartSecond = DateTime.Today.Add(start);
        item.EndSecond = DateTime.Today.Add(end);
#else
        item.StartTime = start;
        item.EndTime = end;
#endif
    }

    /// <summary>
    /// 按字符串键写入档案字典。1.x 直接用该键，2.x 解析为 Guid。
    /// </summary>
    public static void SetByKey<TValue>(System.Collections.Generic.IDictionary<string, TValue> dictionary, string key, TValue value)
    {
        dictionary[key] = value;
    }

    /// <summary>
    /// 按字符串键写入档案字典（2.x 的 Guid 键版本）。
    /// </summary>
    public static void SetByKey<TValue>(System.Collections.Generic.IDictionary<Guid, TValue> dictionary, string key, TValue value)
    {
        dictionary[Guid.TryParse(key, out var guid) ? guid : Guid.NewGuid()] = value;
    }

    /// <summary>
    /// 读取课表绑定的时间表 Id，统一成字符串。
    /// 1.x 的 <c>TimeLayoutId</c> 本身是 string，2.x 是 Guid。
    /// </summary>
    public static string GetTimeLayoutId(ClassPlan plan)
    {
#if CLASSISLAND_1X
        return plan.TimeLayoutId;
#else
        return plan.TimeLayoutId.ToString();
#endif
    }

    /// <summary>
    /// 写入课表绑定的时间表 Id。
    /// </summary>
    public static void SetTimeLayoutId(ClassPlan plan, string timeLayoutId)
    {
#if CLASSISLAND_1X
        plan.TimeLayoutId = timeLayoutId;
#else
        if (Guid.TryParse(timeLayoutId, out var guid)) plan.TimeLayoutId = guid;
#endif
    }

    /// <summary>
    /// 按时间表 Id 查找时间表内容，键类型在 1.x/2.x 间不同。
    /// </summary>
    public static bool TryGetTimeLayout(Profile profile, string timeLayoutId, out TimeLayout? layout)
    {
#if CLASSISLAND_1X
        return profile.TimeLayouts.TryGetValue(timeLayoutId, out layout);
#else
        layout = null;
        if (!Guid.TryParse(timeLayoutId, out var guid)) return false;
        return profile.TimeLayouts.TryGetValue(guid, out layout);
#endif
    }

    /// <summary>
    /// 返回档案中第一套时间表的 Id（字符串形式），没有则返回空串。
    /// </summary>
    public static string FirstTimeLayoutId(Profile profile)
    {
        // 键在 1.x 是 string、2.x 是 Guid，Convert.ToString 对两者都成立，
        // 且不会因值类型不可空而在 ?. 上编译失败。
        var first = profile.TimeLayouts.Keys.FirstOrDefault();
        return Convert.ToString(first) ?? "";
    }

    /// <summary>
    /// 按 Id 查找课表，键类型在 1.x/2.x 间不同。
    /// </summary>
    public static bool TryGetClassPlan(Profile profile, string classPlanId, out ClassPlan? plan)
    {
#if CLASSISLAND_1X
        return profile.ClassPlans.TryGetValue(classPlanId, out plan);
#else
        plan = null;
        if (!Guid.TryParse(classPlanId, out var guid)) return false;
        return profile.ClassPlans.TryGetValue(guid, out plan);
#endif
    }

    /// <summary>
    /// 读取 <see cref="OrderedSchedule"/> 指向的课表 Id，统一成字符串。
    /// </summary>
    public static string GetClassPlanId(OrderedSchedule schedule)
    {
#if CLASSISLAND_1X
        return schedule.ClassPlanId;
#else
        return schedule.ClassPlanId.ToString();
#endif
    }

    /// <summary>
    /// 写入 <see cref="OrderedSchedule"/> 指向的课表 Id。
    /// </summary>
    public static void SetClassPlanId(OrderedSchedule schedule, string classPlanId)
    {
#if CLASSISLAND_1X
        schedule.ClassPlanId = classPlanId;
#else
        if (Guid.TryParse(classPlanId, out var guid)) schedule.ClassPlanId = guid;
#endif
    }

    /// <summary>
    /// 调用宿主的临时课表创建接口，并返回统一成字符串的课表 Id（失败返回空串）。
    /// 两代接口的参数与返回类型都不同（string 与 Guid）。
    /// </summary>
    public static string CreateTempClassPlan(IPublicProfileService service, string sourceId, DateTime date)
    {
#if CLASSISLAND_1X
        return service.CreateTempClassPlan(sourceId, null, date) ?? "";
#else
        if (!Guid.TryParse(sourceId, out var guid)) return "";
        var created = service.CreateTempClassPlan(guid, null, date);
        return created?.ToString() ?? "";
#endif
    }

    /// <summary>
    /// 读取当前生效的临时层课表 Id，统一成字符串（未启用返回空串）。
    /// </summary>
    public static string GetOverlayClassPlanId(Profile profile)
    {
        return Convert.ToString(profile.OverlayClassPlanId) ?? "";
    }

    /// <summary>
    /// 设置当前生效的临时层课表 Id。
    /// </summary>
    public static void SetOverlayClassPlanId(Profile profile, string classPlanId)
    {
#if CLASSISLAND_1X
        profile.OverlayClassPlanId = classPlanId;
#else
        if (Guid.TryParse(classPlanId, out var guid)) profile.OverlayClassPlanId = guid;
#endif
    }

    /// <summary>
    /// 读取课程项引用的科目 Id，统一成字符串。
    /// </summary>
    public static string GetSubjectId(ClassInfo classInfo)
    {
#if CLASSISLAND_1X
        return classInfo.SubjectId;
#else
        return classInfo.SubjectId.ToString();
#endif
    }

    /// <summary>
    /// 写入课程项引用的科目 Id。
    /// </summary>
    public static void SetSubjectId(ClassInfo classInfo, string subjectId)
    {
#if CLASSISLAND_1X
        classInfo.SubjectId = subjectId;
#else
        if (Guid.TryParse(subjectId, out var guid)) classInfo.SubjectId = guid;
#endif
    }

    /// <summary>
    /// 写入临时层课表的来源课表 Id。
    /// </summary>
    public static void SetOverlaySourceId(ClassPlan plan, string sourceId)
    {
#if CLASSISLAND_1X
        plan.OverlaySourceId = sourceId;
#else
        if (Guid.TryParse(sourceId, out var guid)) plan.OverlaySourceId = guid;
#endif
    }

    /// <summary>
    /// 写入当前临时层课表 Id（<see cref="Profile.TempClassPlanId"/>）。
    /// </summary>
    public static void SetTempClassPlanId(Profile profile, string classPlanId)
    {
#if CLASSISLAND_1X
        profile.TempClassPlanId = classPlanId;
#else
        if (Guid.TryParse(classPlanId, out var guid)) profile.TempClassPlanId = guid;
#endif
    }

    /// <summary>
    /// 按科目 Id 查找科目，键类型在 1.x/2.x 间不同。
    /// </summary>
    public static bool TryGetSubject(Profile profile, string subjectId, out Subject? subject)
    {
#if CLASSISLAND_1X
        return profile.Subjects.TryGetValue(subjectId, out subject);
#else
        subject = null;
        if (!Guid.TryParse(subjectId, out var guid)) return false;
        return profile.Subjects.TryGetValue(guid, out subject);
#endif
    }
}
