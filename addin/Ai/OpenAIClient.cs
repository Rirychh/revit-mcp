// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.Net.Http;
using System.Text;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitMcpAddin.Ai
{
    /// <summary>
    /// OpenAI Chat Completions 客戶端 + function calling 迴圈。
    /// 工具清單與 Claude 相同（ToolSchemas.OpenAI），工具在 Revit 主緒執行。
    /// </summary>
    public class OpenAIClient : IChatClient
    {
        public string Model { get; set; } = AiProviders.DefaultModel(AiProvider.OpenAI);

        private const string Endpoint = "https://api.openai.com/v1/chat/completions";

        private readonly string _apiKey;
        private readonly RevitApiExecutor _executor;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(180) };
        private readonly JArray _messages = new()
        {
            new JObject { ["role"] = "system", ["content"] = AiProviders.SystemPrompt }
        };

        public OpenAIClient(string apiKey, UIApplication uiApp, RevitApiExecutor executor)
        {
            _apiKey = apiKey;
            _executor = executor;
        }

        public async Task SendAsync(string userText, Action<string> onText, Action<string> onToolNote)
        {
            _messages.Add(new JObject { ["role"] = "user", ["content"] = userText });

            for (int turn = 0; turn < AiProviders.MaxToolTurns; turn++)
            {
                var reqBody = new JObject
                {
                    ["model"] = Model,
                    ["messages"] = _messages,
                    ["tools"] = ToolSchemas.OpenAI()
                };

                var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                req.Headers.Add("Authorization", "Bearer " + _apiKey);
                req.Content = new StringContent(reqBody.ToString(), Encoding.UTF8, "application/json");

                var resp = await _http.SendAsync(req);
                var respText = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    onText("[API 錯誤 " + (int)resp.StatusCode + "] " + respText);
                    return;
                }

                var msg = JObject.Parse(respText)["choices"]?[0]?["message"] as JObject;
                if (msg == null) { onText("[API 回應格式不符] " + respText); return; }

                // 助理訊息原樣加回（含 tool_calls），下一輪才對得上 tool_call_id
                var keep = new JObject { ["role"] = "assistant", ["content"] = msg["content"] ?? JValue.CreateNull() };
                if (msg["tool_calls"] is JArray tcs && tcs.Count > 0) keep["tool_calls"] = tcs;
                _messages.Add(keep);

                var text = msg["content"]?.Type == JTokenType.String ? msg["content"]!.ToString() : "";
                if (!string.IsNullOrEmpty(text)) onText(text);

                if (msg["tool_calls"] is not JArray calls || calls.Count == 0) return;

                foreach (var call in calls)
                {
                    var id = call["id"]?.ToString() ?? "";
                    var name = call["function"]?["name"]?.ToString() ?? "";
                    JObject input;
                    try { input = JObject.Parse(call["function"]?["arguments"]?.ToString() ?? "{}"); }
                    catch { input = new JObject(); }

                    onToolNote("↳ 執行 " + name);
                    var result = ToolSchemas.Run(_executor, name, input);
                    _messages.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = result });
                }
            }

            onText("（已達工具呼叫上限，先停在這）");
        }
    }
}
