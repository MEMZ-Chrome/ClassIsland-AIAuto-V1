using System.IO;
using System.Text.Json;
using ClassIsland.Automation.Core.Models;

namespace ClassIsland.Automation.Core.Services;

/// <summary>
/// 集中式配置管理器，确保所有窗口和工具执行器共享同一配置实例并实现配置变更全应用实时同步。
/// </summary>
public static class SettingsManager
{
    private static readonly object _lock = new();
    private static PluginSettings _settings = new();
    private static string? _settingsFilePath;
    private static bool _loaded;

    public static event Action<PluginSettings>? SettingsChanged;

    /// <summary>
    /// 共享配置实例。若尚未从磁盘加载过，则自动完成一次加载，
    /// 避免只打开对话窗口（未进设置页）时拿到一份空配置。
    /// </summary>
    public static PluginSettings Current
    {
        get
        {
            lock (_lock)
            {
                if (!_loaded && !string.IsNullOrEmpty(_settingsFilePath))
                {
                    LoadCore(_settingsFilePath);
                }
                return _settings;
            }
        }
    }

    /// <summary>
    /// 供插件宿主在启动时注册配置路径，使 <see cref="Current"/> 可在任意窗口按需加载。
    /// </summary>
    public static void SetSettingsFilePath(string filePath)
    {
        lock (_lock)
        {
            if (_settingsFilePath != filePath)
            {
                _loaded = false;
            }
            _settingsFilePath = filePath;
        }
    }

    public static PluginSettings Load(string filePath)
    {
        lock (_lock)
        {
            LoadCore(filePath);
            return _settings;
        }
    }

    private static void LoadCore(string filePath)
    {
        _settingsFilePath = filePath;
        if (File.Exists(filePath))
        {
            try
            {
                string json = File.ReadAllText(filePath);
                _settings = JsonSerializer.Deserialize<PluginSettings>(json) ?? new PluginSettings();
                _settings.EnsureProvidersInitialized();
                _loaded = true;
                return;
            }
            catch (Exception ex)
            {
                // 解析失败绝不静默覆盖：先把原文件另存为 .corrupt，
                // 否则后续任何一次保存都会用默认配置永久覆盖掉用户的供应商/密钥/记忆。
                try
                {
                    File.Copy(filePath, filePath + ".corrupt", overwrite: true);
                }
                catch { }

                LastLoadError = $"配置文件解析失败，已备份为 {Path.GetFileName(filePath)}.corrupt：{ex.Message}";
                _settings = new PluginSettings();
                _settings.EnsureProvidersInitialized();
                _loaded = true;
                return;
            }
        }

        _settings = new PluginSettings();
        _settings.EnsureProvidersInitialized();
        _loaded = true;
    }

    /// <summary>
    /// 最近一次加载失败的原因；为 null 表示加载正常。
    /// </summary>
    public static string? LastLoadError { get; private set; }

    /// <summary>
    /// 最近一次保存失败的原因；为 null 表示上次保存成功。
    /// </summary>
    public static string? LastSaveError { get; private set; }

    /// <summary>
    /// 保存配置。<returns>true 表示确实写入了磁盘。</returns>
    /// 调用方必须检查返回值，不能无条件提示「保存成功」。
    /// </summary>
    public static bool Save(PluginSettings? settings = null)
    {
        lock (_lock)
        {
            // 仅在托管实例尚未建立时接受外部实例，避免后续窗口各持一份配置互相覆盖。
            if (settings != null && !_loaded)
            {
                _settings = settings;
                _loaded = true;
            }

            var ok = false;
            if (!string.IsNullOrEmpty(_settingsFilePath))
            {
                try
                {
                    var dir = Path.GetDirectoryName(_settingsFilePath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
                    File.WriteAllText(_settingsFilePath, json);
                    ok = true;
                    LastSaveError = null;
                }
                catch (Exception ex)
                {
                    LastSaveError = ex.Message;
                }
            }
            else
            {
                LastSaveError = "未指定配置文件路径";
            }

            NotifySettingsChanged(_settings);
            return ok;
        }
    }

    public static void NotifySettingsChanged(PluginSettings settings)
    {
        SettingsChanged?.Invoke(settings);
    }
}
