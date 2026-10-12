using System.Text.Json.Serialization;
using ClassIsland.Automation.Core.Services;

namespace ClassIsland.Automation.Core.Models;

public enum AiProviderType
{
    MiMo,
    DeepSeek,
    Custom
}

public class AiModelItem
{
    public string Name { get; set; } = "";
    public bool IsEnabled { get; set; } = true;

    public AiModelItem() { }
    public AiModelItem(string name, bool isEnabled = true)
    {
        Name = name;
        IsEnabled = isEnabled;
    }
}

public class AiProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = ""; // 供应商别名
    public string BaseUrl { get; set; } = "";

    [JsonIgnore]
    public string ApiKey { get; set; } = "";

    [JsonPropertyName("ApiKey")]
    public string ApiKeyEncrypted
    {
        get => SecretProtectionService.Encrypt(ApiKey);
        set => ApiKey = SecretProtectionService.Decrypt(value);
    }

    public List<AiModelItem> Models { get; set; } = new();
}

public class ActiveModelOption
{
    public string ProviderId { get; set; } = "";
    public string ProviderName { get; set; } = "";
    public string ModelName { get; set; } = "";

    public string DisplayText => string.IsNullOrWhiteSpace(ProviderName)
        ? ModelName
        : $"[{ProviderName}] {ModelName}";

    public override string ToString() => DisplayText;
}

public class PluginSettings
{
    // 多供应商支持
    public List<AiProviderConfig> Providers { get; set; } = new();

    // 当前选中的活跃供应商与模型
    public string ActiveProviderId { get; set; } = "";
    public string ActiveModelName { get; set; } = "";

    // 兼容旧字段 (用于向下兼容及迁移)
    public AiProviderType Provider { get; set; } = AiProviderType.MiMo;
    public string MiMoBaseUrl { get; set; } = "https://api.xiaomimimo.com/v1";
    [JsonIgnore] public string MiMoApiKey { get; set; } = "";
    [JsonPropertyName("MiMoApiKey")]
    public string MiMoApiKeyEncrypted
    {
        get => SecretProtectionService.Encrypt(MiMoApiKey);
        set => MiMoApiKey = SecretProtectionService.Decrypt(value);
    }
    public string MiMoModel { get; set; } = "";

    public string DeepSeekBaseUrl { get; set; } = "https://api.deepseek.com";
    [JsonIgnore] public string DeepSeekApiKey { get; set; } = "";
    [JsonPropertyName("DeepSeekApiKey")]
    public string DeepSeekApiKeyEncrypted
    {
        get => SecretProtectionService.Encrypt(DeepSeekApiKey);
        set => DeepSeekApiKey = SecretProtectionService.Decrypt(value);
    }
    public string DeepSeekModel { get; set; } = "";

    public string CustomBaseUrl { get; set; } = "https://api.openai.com/v1";
    [JsonIgnore] public string CustomApiKey { get; set; } = "";
    [JsonPropertyName("CustomApiKey")]
    public string CustomApiKeyEncrypted
    {
        get => SecretProtectionService.Encrypt(CustomApiKey);
        set => CustomApiKey = SecretProtectionService.Decrypt(value);
    }
    public string CustomModel { get; set; } = "";

    // 常用提示词/自定义系统提示词
    public string CustomPrompt { get; set; } = "";

    // 单轮对话中允许的最大工具调用轮数
    public int MaxToolIterations { get; set; } = 8;

    /// <summary>
    /// 实际生效的工具调用轮数上限。夹在 1–50 之间：
    /// 过大意味着一次提问可能产生几十次 API 调用与费用，过小则复杂任务做不完。
    /// </summary>
    [JsonIgnore]
    public int EffectiveMaxToolIterations => Math.Clamp(MaxToolIterations, 1, 50);

    // 长期记忆系统
    public string CustomMemory { get; set; } = "";
    public bool IsMemoryEnabled { get; set; } = true;
    public bool IsCommandExecutionEnabled { get; set; } = false;

    // 固定密码
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";

    // TOTP
    [JsonIgnore] public string TotpSecret { get; set; } = "";
    [JsonPropertyName("TotpSecret")]
    public string TotpSecretEncrypted
    {
        get => SecretProtectionService.Encrypt(TotpSecret);
        set => TotpSecret = SecretProtectionService.Decrypt(value);
    }

    [JsonIgnore] public string PendingTotpSecret { get; set; } = "";
    [JsonPropertyName("PendingTotpSecret")]
    public string PendingTotpSecretEncrypted
    {
        get => SecretProtectionService.Encrypt(PendingTotpSecret);
        set => PendingTotpSecret = SecretProtectionService.Decrypt(value);
    }

    [JsonIgnore]
    public AiProviderConfig? CurrentProvider
    {
        get
        {
            EnsureProvidersInitialized();
            if (!string.IsNullOrWhiteSpace(ActiveProviderId))
            {
                var p = Providers.FirstOrDefault(x => x.Id == ActiveProviderId);
                if (p != null) return p;
            }
            return Providers.FirstOrDefault(p => p.Models.Any(m => m.IsEnabled)) ?? Providers.FirstOrDefault();
        }
    }

    [JsonIgnore]
    public string CurrentBaseUrl => CurrentProvider?.BaseUrl ?? "";

    [JsonIgnore]
    public string CurrentApiKey => CurrentProvider?.ApiKey ?? "";

    [JsonIgnore]
    public string CurrentModel
    {
        get
        {
            var p = CurrentProvider;
            if (p == null) return "";
            if (!string.IsNullOrWhiteSpace(ActiveModelName) && p.Models.Any(m => m.Name == ActiveModelName && m.IsEnabled))
            {
                return ActiveModelName;
            }
            var firstEnabled = p.Models.FirstOrDefault(m => m.IsEnabled);
            if (firstEnabled != null) return firstEnabled.Name;
            var first = p.Models.FirstOrDefault();
            return first?.Name ?? ActiveModelName;
        }
    }

    public void EnsureProvidersInitialized()
    {
        if (Providers == null)
        {
            Providers = new List<AiProviderConfig>();
        }

        if (Providers.Count == 0)
        {
            // 自动从旧字段迁移
            var mimo = new AiProviderConfig
            {
                Name = "小米 MiMo",
                BaseUrl = string.IsNullOrWhiteSpace(MiMoBaseUrl) ? "https://api.xiaomimimo.com/v1" : MiMoBaseUrl,
                ApiKey = MiMoApiKey,
                Models = new List<AiModelItem>()
            };
            if (!string.IsNullOrWhiteSpace(MiMoModel))
            {
                mimo.Models.Add(new AiModelItem(MiMoModel, true));
            }

            var deepseek = new AiProviderConfig
            {
                Name = "DeepSeek",
                BaseUrl = string.IsNullOrWhiteSpace(DeepSeekBaseUrl) ? "https://api.deepseek.com" : DeepSeekBaseUrl,
                ApiKey = DeepSeekApiKey,
                Models = new List<AiModelItem>()
            };
            if (!string.IsNullOrWhiteSpace(DeepSeekModel))
            {
                deepseek.Models.Add(new AiModelItem(DeepSeekModel, true));
            }

            var openrouter = new AiProviderConfig
            {
                Name = "OpenRouter",
                BaseUrl = "https://openrouter.ai/api/v1",
                ApiKey = "",
                Models = new List<AiModelItem>()
            };

            Providers.Add(mimo);
            Providers.Add(deepseek);
            Providers.Add(openrouter);

            if (!string.IsNullOrWhiteSpace(CustomApiKey) || !string.IsNullOrWhiteSpace(CustomModel))
            {
                var custom = new AiProviderConfig
                {
                    Name = "自定义",
                    BaseUrl = string.IsNullOrWhiteSpace(CustomBaseUrl) ? "https://api.openai.com/v1" : CustomBaseUrl,
                    ApiKey = CustomApiKey,
                    Models = new List<AiModelItem>()
                };
                if (!string.IsNullOrWhiteSpace(CustomModel))
                {
                    custom.Models.Add(new AiModelItem(CustomModel, true));
                }
                Providers.Add(custom);
            }

            ActiveProviderId = Provider switch
            {
                AiProviderType.MiMo => mimo.Id,
                AiProviderType.DeepSeek => deepseek.Id,
                _ => Providers.First().Id
            };
            ActiveModelName = CurrentModel;
        }
        else
        {
            // 若现有配置中尚未包含 OpenRouter，自动补充此预置提供商
            if (!Providers.Any(p => p.Name.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase) || p.BaseUrl.Contains("openrouter.ai")))
            {
                Providers.Add(new AiProviderConfig
                {
                    Name = "OpenRouter",
                    BaseUrl = "https://openrouter.ai/api/v1",
                    ApiKey = "",
                    Models = new List<AiModelItem>()
                });
            }
        }
    }

    public List<ActiveModelOption> GetEnabledModelOptions()
    {
        EnsureProvidersInitialized();
        var list = new List<ActiveModelOption>();
        foreach (var p in Providers)
        {
            foreach (var m in p.Models)
            {
                if (m.IsEnabled)
                {
                    list.Add(new ActiveModelOption
                    {
                        ProviderId = p.Id,
                        ProviderName = p.Name,
                        ModelName = m.Name
                    });
                }
            }
        }
        return list;
    }

    public void SelectActiveModel(string providerId, string modelName)
    {
        ActiveProviderId = providerId;
        ActiveModelName = modelName;
    }

    public void SetModelForCurrentProvider(string model)
    {
        var p = CurrentProvider;
        if (p == null) return;
        var existing = p.Models.FirstOrDefault(m => m.Name == model);
        if (existing == null)
        {
            p.Models.Add(new AiModelItem(model, true));
        }
        else
        {
            existing.IsEnabled = true;
        }
        ActiveModelName = model;
    }
}
