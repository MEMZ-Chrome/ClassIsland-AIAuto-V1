# IslandAgent (ClassIsland 1.x)

ClassIsland **1.x**（WPF）专用的 AI 插件。接入任意 OpenAI 兼容接口，用对话的方式改课表、调设置。

> ClassIsland 2.x 请用另一个仓库：[ClassIsland-AIAuto](https://github.com/MEMZ-Chrome/ClassIsland-AIAuto)。
> 两个仓库各自独立维护，代码不共用 —— 因为 1.x 和 2.x 的档案模型不一样（字典键一个是 string、一个是 Guid，时间项一个是 DateTime、一个是 TimeSpan），强行共用只会两边都编译不过。

## 能做什么

**改课表**：问它「今天第 3 节是什么」，或者说「明天第 2 节换化学」「周三下午全调成自习」，它会直接写进当前档案。

**改设置**：主题、透明度、缩放、通知开关、上课隐藏、时间偏移，都能用一句话改掉，改完立即生效。

**发图片**：把课表照片丢进对话框，让它读出科目和时间段再排课。需要选一个支持视觉的模型。

**记东西**：跟它说「记住化学老师是王老师」，以后会一直记得。记忆内容在设置页可以直接编辑。

## 安装

在 [Releases](https://github.com/MEMZ-Chrome/ClassIsland-AIAuto-V1/releases) 下载 `ClassIsland.Automation.V1.cipx`。

`.cipx` 可以直接在 ClassIsland 里安装，或者解压到 `Plugins/` 目录。

本插件面向 ClassIsland 1.x，已在 1.7.0.1 上验证加载。

## 配置

装好后打开「应用设置 → IslandAgent设置」。

**第一步**，在「模型服务」里点一个预置模板，或者自己填：

| 预置 | 地址 |
| --- | --- |
| DeepSeek | `https://api.deepseek.com` |
| OpenRouter | `https://openrouter.ai/api/v1` |
| 小米 MiMo | `https://api.xiaomimimo.com/v1` |
| OpenAI | `https://api.openai.com/v1` |
| 自建 Ollama | `http://localhost:11434/v1` |

点预置只会填别名和地址，**密钥要自己填**。任何 OpenAI 兼容接口都能用，不限于上面这些。

**第二步**，填 API Key，点「自动获取」把模型列表拉下来，勾选要用的。

模型列表由接口实时拉取，也可以手动输入，插件不预置任何模型名。

**第三步**，托盘图标右键 → 「AI助手」，开始对话。

## 对话里能用的工具

模型通过下面这些工具操作 ClassIsland，一次对话可以连续调用多个（轮数上限可在「高级偏好」里调整，默认 8，范围 1–50）：

| 工具 | 作用 |
| --- | --- |
| `get_current_time` | 取系统时间、日期、星期，避免模型靠猜 |
| `get_profile_summary` | 档案概览：科目、时间表、课表列表 |
| `get_schedule_details` | 具体课表内容，含节次、科目、老师 |
| `upsert_subjects` | 增改科目，含简称、任课老师、是否室外课 |
| `create_time_layout` | 建或替换作息时间表 |
| `set_class_plan` | 配某天的固定课表 |
| `setup_temp_class_plan` | 临时调课，支持单节替换和整天替换 |
| `get_app_settings` / `update_app_settings` | 读写 ClassIsland 应用设置 |
| `get_plugin_settings` / `update_plugin_settings` | 读写插件自身设置（供应商、模型、记忆、提示词） |
| `navigate_app_page` | 跳转到 ClassIsland 的某个页面 |
| `update_memory` | 写入长期记忆 |
| `execute_command` | 执行命令行，**默认关闭** |

`execute_command` 需要手动开启（高级偏好 → 允许 AI 执行命令行命令）。

## 安全

设置页可以设固定密码或 TOTP 动态口令，防止别人在大屏上翻你的密钥。

关于 TOTP 有个取舍：**没完成首次验证前，页面不会被锁定**。在学校可能一时连不上手机同步验证器，如果一生成密钥就锁上，人就彻底进不去了。所以只有验证通过、TOTP 正式激活之后，保护才生效。

API 密钥和 TOTP 密钥在磁盘上加密保存。

## 作者

MEMZ-Chrome · [Bilibili](https://space.bilibili.com/1353783215)

MIT License
