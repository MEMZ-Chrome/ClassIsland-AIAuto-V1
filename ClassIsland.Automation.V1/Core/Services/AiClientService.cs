using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClassIsland.Automation.Core.Models;

namespace ClassIsland.Automation.Core.Services;

public class AiClientService
{
    private readonly HttpClient _httpClient;

    public AiClientService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(120);
    }

    /// <summary>
    /// 获取提供商支持的模型列表（兼容标准 OpenAI /models、/v1/models 以及 Ollama /api/tags）
    /// </summary>
    public async Task<List<string>> FetchModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken = default)
    {
        var models = new List<string>();
        if (string.IsNullOrWhiteSpace(baseUrl))
            return models;

        var cleanBase = baseUrl.TrimEnd('/');
        var candidateUrls = new List<string>();

        if (cleanBase.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            candidateUrls.Add(cleanBase);
        }
        else
        {
            candidateUrls.Add($"{cleanBase}/models");
            if (!cleanBase.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                candidateUrls.Add($"{cleanBase}/v1/models");
            }
            candidateUrls.Add($"{cleanBase}/api/tags");
        }

        foreach (var url in candidateUrls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/MEMZ-Chrome/ClassIsland-AIAuto");
                request.Headers.TryAddWithoutValidation("X-Title", "ClassIsland IslandAgent");
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                }

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    continue;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);

                // 兼容标准 OpenAI 格式: { "data": [ { "id": "..." } ] }
                if (doc.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("id", out var idElement))
                        {
                            var id = idElement.GetString();
                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                models.Add(id);
                            }
                        }
                    }
                }
                // 兼容 Ollama /api/tags 格式: { "models": [ { "name": "..." } ] }
                else if (doc.RootElement.TryGetProperty("models", out var modelsElement) && modelsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in modelsElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("name", out var nameElement))
                        {
                            var name = nameElement.GetString();
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                models.Add(name);
                            }
                        }
                    }
                }

                if (models.Count > 0)
                {
                    break;
                }
            }
            catch
            {
                // 忽略当前候选地址异常，尝试下一个
            }
        }

        models.Sort();
        return models;
    }

    /// <summary>
    /// 基于 Tools Call 的全自主 ReAct Agent 对话交互循环（支持链式多步连续调用与 DSML 兜底）
    /// </summary>
    public async Task<AgentChatResponse> ExecuteAgentTurnAsync(
        PluginSettings settings,
        JsonArray conversationMessages,
        ToolExecutor? executor,
        Action<string, ToolExecutionResult?>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = settings.CurrentBaseUrl.TrimEnd('/');
        var chatUrl = baseUrl.EndsWith("/chat/completions") ? baseUrl : $"{baseUrl}/chat/completions";

        var agentResponse = new AgentChatResponse();

        int iterations = 0;
        var maxIterations = settings.EffectiveMaxToolIterations;

        while (iterations < maxIterations)
        {
            iterations++;
            cancellationToken.ThrowIfCancellationRequested();

            var requestPayload = new JsonObject
            {
                ["model"] = settings.CurrentModel,
                ["messages"] = conversationMessages.DeepClone(),
                ["tools"] = AiToolDefinitions.GetAvailableTools(settings),
                ["tool_choice"] = "auto",
                ["temperature"] = 0.2
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, chatUrl)
            {
                Content = new StringContent(requestPayload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/MEMZ-Chrome/ClassIsland-AIAuto");
            request.Headers.TryAddWithoutValidation("X-Title", "ClassIsland IslandAgent");

            if (!string.IsNullOrWhiteSpace(settings.CurrentApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.CurrentApiKey.Trim());
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"AI 接口请求失败 ({response.StatusCode}): {rawResponse}");
            }

            using var doc = JsonDocument.Parse(rawResponse);

            if (!doc.RootElement.TryGetProperty("choices", out var choicesElem) ||
                choicesElem.ValueKind != JsonValueKind.Array ||
                choicesElem.GetArrayLength() == 0)
            {
                throw new HttpRequestException(
                    $"AI 接口返回内容中缺少 choices 字段，无法解析回复。原始响应：{Truncate(rawResponse, 400)}");
            }

            var choice = choicesElem[0];
            if (!choice.TryGetProperty("message", out var message))
            {
                throw new HttpRequestException(
                    $"AI 接口返回的 choices 中缺少 message 字段。原始响应：{Truncate(rawResponse, 400)}");
            }

            var textContent = message.TryGetProperty("content", out var cElem) && cElem.ValueKind == JsonValueKind.String
                ? cElem.GetString() ?? ""
                : "";

            // 1. 优先检查原生 tool_calls
            if (message.TryGetProperty("tool_calls", out var toolCallsElem) &&
                toolCallsElem.ValueKind == JsonValueKind.Array &&
                toolCallsElem.GetArrayLength() > 0)
            {
                var assistantMsg = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = textContent,
                    ["tool_calls"] = JsonNode.Parse(toolCallsElem.GetRawText())
                };
                conversationMessages.Add(assistantMsg);

                foreach (var tc in toolCallsElem.EnumerateArray())
                {
                    // 部分模型/网关会返回结构不完整的 tool_call，跳过而不是让整轮对话崩溃
                    if (tc.ValueKind != JsonValueKind.Object ||
                        !tc.TryGetProperty("function", out var fn) ||
                        fn.ValueKind != JsonValueKind.Object ||
                        !fn.TryGetProperty("name", out var nameElem))
                    {
                        continue;
                    }

                    var id = tc.TryGetProperty("id", out var idElem) && idElem.ValueKind == JsonValueKind.String
                        ? idElem.GetString() ?? Guid.NewGuid().ToString()
                        : Guid.NewGuid().ToString();
                    var name = nameElem.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var argsStr = fn.TryGetProperty("arguments", out var argsElem)
                        ? (argsElem.ValueKind == JsonValueKind.String
                            ? argsElem.GetString() ?? "{}"
                            : argsElem.GetRawText())
                        : "{}";

                    JsonObject argsObj;
                    try
                    {
                        argsObj = JsonNode.Parse(argsStr) as JsonObject ?? new JsonObject();
                    }
                    catch
                    {
                        argsObj = new JsonObject();
                    }

                    var callInfo = new ToolCallInfo
                    {
                        Id = id,
                        Name = name,
                        Arguments = argsObj
                    };
                    agentResponse.ToolCalls.Add(callInfo);

                    ToolExecutionResult execResult;
                    if (executor != null)
                    {
                        execResult = executor.Execute(callInfo);
                    }
                    else
                    {
                        execResult = new ToolExecutionResult
                        {
                            ToolCallId = id,
                            Name = name,
                            Success = false,
                            Result = "本地工具执行器未注入"
                        };
                    }
                    agentResponse.ExecutedResults.Add(execResult);

                    onProgress?.Invoke($"[工具调用: {name}] {(execResult.Success ? "成功" : "失败")}", execResult);

                    // 送回历史供下一轮推理
                    conversationMessages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = id,
                        ["content"] = execResult.Result
                    });
                }

                // 继续自动执行下一轮推理
                continue;
            }

            // 2. 兜底检查：模型是否在 content 中直接输出了 DSML / XML 格式的调用
            var dsmlCalls = ParseDsmlToolCalls(textContent);
            if (dsmlCalls.Count > 0)
            {
                conversationMessages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = textContent
                });

                foreach (var (toolName, toolArgs) in dsmlCalls)
                {
                    var id = "call_" + Guid.NewGuid().ToString("N")[..8];
                    var callInfo = new ToolCallInfo
                    {
                        Id = id,
                        Name = toolName,
                        Arguments = toolArgs
                    };
                    agentResponse.ToolCalls.Add(callInfo);

                    ToolExecutionResult execResult;
                    if (executor != null)
                    {
                        execResult = executor.Execute(callInfo);
                    }
                    else
                    {
                        execResult = new ToolExecutionResult
                        {
                            ToolCallId = id,
                            Name = toolName,
                            Success = false,
                            Result = "本地工具执行器未注入"
                        };
                    }
                    agentResponse.ExecutedResults.Add(execResult);

                    onProgress?.Invoke($"[DSML 工具调用: {toolName}] {(execResult.Success ? "成功" : "失败")}", execResult);

                    conversationMessages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = $"[系统提示 - 工具 {toolName} 执行结果]: {execResult.Result}"
                    });
                }

                // 继续自动执行下一轮推理
                continue;
            }

            // 3. 模型没有发起工具调用，说明已得到最终回复
            var cleanedFinal = CleanDsmlTags(textContent);

            // 极少数模型会在正文里复述调用过程却没有任何可解析的调用，
            // 若清洗后为空则保留原文，避免用户看到空气泡。
            agentResponse.ReplyText = string.IsNullOrWhiteSpace(cleanedFinal) ? textContent.Trim() : cleanedFinal;
            conversationMessages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = cleanedFinal
            });
            break;
        }

        // 达到最大迭代次数仍未产出最终回复：必须明确告知用户，
        // 否则界面会显示一条空白消息，看起来像插件卡死。
        if (string.IsNullOrWhiteSpace(agentResponse.ReplyText))
        {
            agentResponse.ReplyText =
                $"已连续调用工具 {maxIterations} 次仍未得出最终结论，本轮对话已停止。请补充更明确的信息后重试。";
        }

        return agentResponse;
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    private static List<(string Name, JsonObject Arguments)> ParseDsmlToolCalls(string text)
    {
        var list = new List<(string Name, JsonObject Arguments)>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        // 匹配 DSML invoke: <｜｜DSML｜｜ invoke name="...">...</｜｜DSML｜｜ invoke>
        var invokePattern = new Regex(@"<[|｜]{2}DSML[|｜]{2}\s*invoke\s+name=""(?<name>[^""]+)""[^>]*>(?<body>.*?)(</[|｜]{2}DSML[|｜]{2}\s*invoke>|$)", RegexOptions.Singleline);
        var matches = invokePattern.Matches(text);

        foreach (Match match in matches)
        {
            var toolName = match.Groups["name"].Value.Trim();
            var body = match.Groups["body"].Value;
            var argsObj = new JsonObject();

            // 匹配各个参数: <｜｜DSML｜｜ parameter name="...">value</｜｜DSML｜｜ parameter>
            var paramPattern = new Regex(@"<[|｜]{2}DSML[|｜]{2}\s*parameter\s+name=""(?<pname>[^""]+)""[^>]*>(?<pval>.*?)(</[|｜]{2}DSML[|｜]{2}\s*parameter>|$)", RegexOptions.Singleline);
            var paramMatches = paramPattern.Matches(body);

            foreach (Match pMatch in paramMatches)
            {
                var pname = pMatch.Groups["pname"].Value.Trim();
                var pval = pMatch.Groups["pval"].Value.Trim();

                // 清理可能由于模型格式混乱引起的开标签残留
                pval = Regex.Replace(pval, @"^[a-zA-Z0-9_""=]+\s*>\s*", "");

                try
                {
                    var parsedNode = JsonNode.Parse(pval);
                    argsObj[pname] = parsedNode;
                }
                catch
                {
                    // 作为普通字符串
                    argsObj[pname] = pval;
                }
            }

            list.Add((toolName, argsObj));
        }

        return list;
    }

    private static string CleanDsmlTags(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var cleaned = Regex.Replace(text, @"<[|｜]{2}DSML[|｜]{2}[\s\S]*?(</[|｜]{2}DSML[|｜]{2}\s*calls>|/?>|$)", "");
        return cleaned.Trim();
    }
}
