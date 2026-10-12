using System.Text.Json.Nodes;
using ClassIsland.Automation.Core.Models;

namespace ClassIsland.Automation.Core.Services;

public static class AiToolDefinitions
{
    public static JsonArray GetAvailableTools(PluginSettings? settings = null)
    {
        var tools = new JsonArray
        {
            // 1. 获取当前档案概述
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "get_profile_summary",
                    ["description"] = "获取当前 ClassIsland 档案中的科目列表、时间表和课表概况（仅名称概要）",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject(),
                        ["required"] = new JsonArray()
                    }
                }
            },

            // 2. 获取指定课表或时间表的详细信息
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "get_schedule_details",
                    ["description"] = "获取课表与作息时间表的具体详细内容（包含各节次的序号、时间段、科目全称、任课老师及临时层预定状态，当用户询问具体某天课程安排或调课前需核实课表时使用）",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["planName"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "课表名称或对应星期（例如：周三、周六、星期五），留空则返回所有课表或当天生效课表的详细信息"
                            },
                            ["targetDate"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "可选，指定查询日期（例如：today、tomorrow、今天、明天、2026-10-10等）"
                            }
                        },
                        ["required"] = new JsonArray()
                    }
                }
            },

            // 3. 添加或更新科目
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "upsert_subjects",
                    ["description"] = "批量添加或更新科目信息（如科目全称、简称、任课老师姓名、是否为室外课等）",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["subjects"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["description"] = "科目列表",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["name"] = new JsonObject { ["type"] = "string", ["description"] = "科目名称，如 语文、数学、体育" },
                                        ["initial"] = new JsonObject { ["type"] = "string", ["description"] = "科目简称或单字简称，如 文、数、体" },
                                        ["teacherName"] = new JsonObject { ["type"] = "string", ["description"] = "任课老师姓名，可留空" },
                                        ["isOutDoor"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否为室外课，如体育课设为 true" }
                                    },
                                    ["required"] = new JsonArray { "name" }
                                }
                            }
                        },
                        ["required"] = new JsonArray { "subjects" }
                    }
                }
            },

            // 4. 创建或更新时间表
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "create_time_layout",
                    ["description"] = "创建或替换作息时间表（配置各节课及课间起止时间）",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["layoutName"] = new JsonObject { ["type"] = "string", ["description"] = "时间表名称，例如：夏季作息时间表、标准时间表" },
                            ["items"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["description"] = "按时间顺序排列的节次与课间",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["startTime"] = new JsonObject { ["type"] = "string", ["description"] = "开始时间 HH:mm:ss 或 HH:mm" },
                                        ["endTime"] = new JsonObject { ["type"] = "string", ["description"] = "结束时间 HH:mm:ss 或 HH:mm" },
                                        ["timeType"] = new JsonObject { ["type"] = "integer", ["description"] = "0: 上课, 1: 课间休息, 2: 分割线, 3: 行动" },
                                        ["breakName"] = new JsonObject { ["type"] = "string", ["description"] = "课间名称或广播操/大课间名称，可留空" }
                                    },
                                    ["required"] = new JsonArray { "startTime", "endTime", "timeType" }
                                }
                            }
                        },
                        ["required"] = new JsonArray { "layoutName", "items" }
                    }
                }
            },

            // 5. 配置每日课表
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "set_class_plan",
                    ["description"] = "创建或更新某天（如周一到周日）的固定课表课程安排",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["planName"] = new JsonObject { ["type"] = "string", ["description"] = "课表名称，如 星期一、星期二、周三、周六" },
                            ["timeLayoutName"] = new JsonObject { ["type"] = "string", ["description"] = "绑定的时间表名称，留空则默认绑定第一套时间表" },
                            ["classes"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["description"] = "课程科目名称列表，严格按上课节次顺序排列",
                                ["items"] = new JsonObject { ["type"] = "string" }
                            }
                        },
                        ["required"] = new JsonArray { "planName", "classes" }
                    }
                }
            },

            // 6. 设置临时调课/临时课表
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "setup_temp_class_plan",
                    ["description"] = "设置或调整指定日期的临时调课/临时课表（支持指定节次单科调课或整天课程替换，调课后立即激活为当天生效的临时课表）",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["sourcePlanName"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "基础课表名称或星期，例如：周三、周六、星期五"
                            },
                            ["dayOfWeek"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "星期几（与 sourcePlanName 等效，例如：周三、周六）"
                            },
                            ["targetDate"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "调课生效的目标日期（例如：today、tomorrow、今天、明天、周六、2026-10-10等，留空默认今天）"
                            },
                            ["classIndex"] = new JsonObject
                            {
                                ["type"] = "integer",
                                ["description"] = "要调整的节次序号（1 表示第 1 节课，2 表示第 2 节课，依此类推；传 -1 可直接表示最后一节课）"
                            },
                            ["classIndices"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["description"] = "需要同时调整为该科目的多个节次序号（例如：[1, -1] 表示第一节和最后一节课，[1, 2] 等）",
                                ["items"] = new JsonObject { ["type"] = "integer" }
                            },
                            ["subject"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "调整后的新科目名称（如：化学、数学、自习）"
                            },
                            ["modifiedClasses"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["description"] = "修改后的完整节次科目列表（若已指定 classIndex 和 subject 则优先使用单科替换）",
                                ["items"] = new JsonObject { ["type"] = "string" }
                            }
                        },
                        ["required"] = new JsonArray()
                    }
                }
            },

            // 7. 读取应用全局设置（直接调接口）
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "get_app_settings",
                    ["description"] = "直接通过服务接口获取 ClassIsland 的应用设置（如通知开关、深色/浅色主题模式、窗口透明度、界面缩放、全屏/上课隐藏状态等）",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject(),
                        ["required"] = new JsonArray()
                    }
                }
            },

            // 8. 修改应用全局设置（直接调接口即时生效，无需用户点击）
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "update_app_settings",
                    ["description"] = "直接通过底层接口修改 ClassIsland 的应用设置并即时保存生效，无需用户手动在设置页面点击（例如开启/关闭通知、切换深色/浅色主题、调整透明度与缩放、上课/全屏隐藏等）",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["timeOffsetSeconds"] = new JsonObject
                            {
                                ["type"] = "number",
                                ["description"] = "时间偏移量（单位：秒，支持正数或负数，例如 10、-5、0）。设定课程时间与实际时间的偏移值。增大偏移以抵消铃声提前，减小偏移以抵消铃声滞后。"
                            },
                            ["isNotificationEnabled"] = new JsonObject
                            {
                                ["type"] = "boolean",
                                ["description"] = "通知总开关（true 为开启提醒，false 为静音/关闭提醒）"
                            },
                            ["theme"] = new JsonObject
                            {
                                ["type"] = "integer",
                                ["description"] = "主题外观模式：0 为跟随系统，1 为浅色模式，2 为深色模式"
                            },
                            ["opacity"] = new JsonObject
                            {
                                ["type"] = "number",
                                ["description"] = "窗口不透明度（0.1 到 1.0）"
                            },
                            ["scale"] = new JsonObject
                            {
                                ["type"] = "number",
                                ["description"] = "界面缩放比例（0.5 到 2.0）"
                            },
                            ["hideOnClass"] = new JsonObject
                            {
                                ["type"] = "boolean",
                                ["description"] = "上课时是否自动隐藏悬浮窗"
                            },
                            ["hideOnFullscreen"] = new JsonObject
                            {
                                ["type"] = "boolean",
                                ["description"] = "全屏应用运行时是否自动隐藏悬浮窗"
                            },
                            ["isClassPrepareNotificationEnabled"] = new JsonObject
                            {
                                ["type"] = "boolean",
                                ["description"] = "是否开启上课预备提醒"
                            },
                            ["isClassOffNotificationEnabled"] = new JsonObject
                            {
                                ["type"] = "boolean",
                                ["description"] = "是否开启下课提醒"
                            },
                            ["customSettings"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["description"] = "其他支持的设置项键值对（键名需在白名单内，如 showWeekDay、isMainWindowTopmost、isAutoStartupEnabled）"
                            },
                            ["timeOffset"] = new JsonObject
                            {
                                ["type"] = "number",
                                ["description"] = "timeOffsetSeconds 的别名"
                            },
                            ["showDate"] = new JsonObject { ["type"] = "boolean", ["description"] = "主界面是否显示日期" },
                            ["showWeekDay"] = new JsonObject { ["type"] = "boolean", ["description"] = "主界面是否显示星期" },
                            ["showSecond"] = new JsonObject { ["type"] = "boolean", ["description"] = "时钟是否显示秒" },
                            ["isMainWindowVisible"] = new JsonObject { ["type"] = "boolean", ["description"] = "主窗口是否显示" },
                            ["isMainWindowTopmost"] = new JsonObject { ["type"] = "boolean", ["description"] = "主窗口是否置顶" },
                            ["isMousePenetrationEnabled"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否开启鼠标穿透" },
                            ["hideOnMaxWindow"] = new JsonObject { ["type"] = "boolean", ["description"] = "窗口最大化时是否自动隐藏" },
                            ["isTimeAutoAdjustEnabled"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否开启网络时间自动校准" },
                            ["isClassChangingNotificationEnabled"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否开启下课/换课提醒" },
                            ["isWeatherNotificationEnabled"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否开启天气提醒" },
                            ["isAfterClassNotificationEnabled"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否开启放学提醒" },
                            ["isAutoStartupEnabled"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否开机自启动" },
                            ["isStartupUpdateCheckEnabled"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否在启动时检查更新" },
                            ["isSleepModeEnabled"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否开启休眠模式（关闭全部提醒）" },
                            ["isDebugEnabled"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否开启调试模式" },
                            ["selectedProfile"] = new JsonObject { ["type"] = "string", ["description"] = "当前启用的档案名称" }
                        },
                        ["required"] = new JsonArray()
                    }
                }
            },

            // 9b. 读取本插件（IslandAgent）自身的设置
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "get_plugin_settings",
                    ["description"] = "读取 IslandAgent 插件自身的设置：全部 AI 供应商（别名、Base URL、是否已配置密钥、模型及启用状态）、当前生效模型、长期记忆开关、命令行权限、自定义提示词与长期记忆内容、密码与 TOTP 保护状态。当用户询问“我配了哪些模型/供应商”“现在用的是哪个模型”“记忆里有什么”时调用。出于安全考虑不会返回 API 密钥明文。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject(),
                        ["required"] = new JsonArray()
                    }
                }
            },

            // 9c. 修改本插件（IslandAgent）自身的设置
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "update_plugin_settings",
                    ["description"] = "修改 IslandAgent 插件自身的设置并立即保存生效。可新增/修改/删除 AI 供应商，增删与启停模型，切换当前使用的模型，开关长期记忆与命令行权限，改写自定义提示词与长期记忆内容。当用户说“添加一个 xxx 供应商”“把模型换成 xxx”“关掉记忆”“记住我的提示词”时调用。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["action"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray
                                {
                                    "add_provider", "set_provider", "delete_provider",
                                    "add_model", "remove_model", "enable_model", "disable_model",
                                    "set_active_model"
                                },
                                ["description"] = "操作类型。仅修改开关/提示词/记忆时可省略此参数"
                            },
                            ["providerName"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "目标供应商的别名或 Id（支持模糊匹配），用于 set_provider / delete_provider / 模型相关操作"
                            },
                            ["newName"] = new JsonObject { ["type"] = "string", ["description"] = "set_provider 时的新别名" },
                            ["baseUrl"] = new JsonObject { ["type"] = "string", ["description"] = "供应商的 API 地址，例如 https://api.deepseek.com" },
                            ["apiKey"] = new JsonObject { ["type"] = "string", ["description"] = "供应商的 API 密钥（会加密存储）" },
                            ["models"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["description"] = "add_provider 时一并写入并启用的模型名称列表",
                                ["items"] = new JsonObject { ["type"] = "string" }
                            },
                            ["modelName"] = new JsonObject { ["type"] = "string", ["description"] = "模型名称，用于模型增删启停与切换当前模型" },
                            ["isMemoryEnabled"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否启用长期记忆系统" },
                            ["isCommandExecutionEnabled"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否允许 AI 执行命令行命令" },
                            ["customPrompt"] = new JsonObject { ["type"] = "string", ["description"] = "自定义系统提示词全文" },
                            ["maxToolIterations"] = new JsonObject
                            {
                                ["type"] = "integer",
                                ["description"] = "单次提问内允许的最大工具调用轮数（1–50），数值越大越能完成复杂多步任务，API 调用与费用也越多"
                            },
                            ["customMemory"] = new JsonObject { ["type"] = "string", ["description"] = "长期记忆全文（会覆盖原有内容）" }
                        },
                        ["required"] = new JsonArray()
                    }
                }
            },

            // 9. 导航应用页面或打开功能页
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "navigate_app_page",
                    ["description"] = "当用户明确要求打开设置窗口界面或插件页面时，通过 classisland:// 内部协议跳转打开页面",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["pageUri"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "目标 URI，例如 classisland://app/settings/general, classisland://app/settings/classisland.plugins, classisland://app/settings/update"
                            }
                        },
                        ["required"] = new JsonArray { "pageUri" }
                    }
                }
            },

            // 10. 获取当前系统真实时间与星期
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "get_current_time",
                    ["description"] = "获取 ClassIsland 当前系统的精确时间、日期、星期几（例如：2026-10-10 星期六）以及时间偏移量。当用户提到“今天”、“明天”、“周几”等相对时间概念时调用，确保调课或查询准确无误。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject(),
                        ["required"] = new JsonArray()
                    }
                }
            }
        };

        // 11. 记录与更新 AI 长期记忆库（仅当启用记忆系统时可用）
        if (settings == null || settings.IsMemoryEnabled)
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "update_memory",
                    ["description"] = "记录、更新、追加、覆盖或清空关于用户、班级、老师信息或调课习惯的长期记忆。当用户在对话中透露或你分析得出重要的班级信息、作息习惯、科目老师偏好、调课规律等具有长远价值的事实时，应主动调用此工具自动更新记忆；当用户吩咐“记住...”（如“记住化学老师是王老师”、“记住我们是高三2班”）时也必须调用。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["action"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JsonArray { "add", "set", "clear" },
                                ["description"] = "操作类型：add（追加一条新记忆）、set（覆盖全部记忆内容）、clear（清空所有记忆）"
                            },
                            ["content"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "记忆内容文本（例如：'班级：高三2班'、'化学老师是王老师'。action 为 clear 时可留空）"
                            }
                        },
                        ["required"] = new JsonArray { "action" }
                    }
                }
            });
        }

        // 12. 系统命令行执行权限（仅当用户在高级设置中显式启用时提供）
        if (settings?.IsCommandExecutionEnabled == true)
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "execute_command",
                    ["description"] = "在系统终端执行命令行指令（仅在高级设置中启用了命令行执行权限时可用）。可用于系统查询、运行脚本、网络测试等操作。",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["command"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "要执行的命令行指令，例如 'ping baidu.com'、'shutdown -s -t 3600' 或系统批处理命令"
                            },
                            ["timeoutSeconds"] = new JsonObject
                            {
                                ["type"] = "integer",
                                ["description"] = "可选，超时时间（秒），默认 15 秒"
                            }
                        },
                        ["required"] = new JsonArray { "command" }
                    }
                }
            });
        }

        return tools;
    }
}
