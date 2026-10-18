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
    /// Google Gemini API（generateContent）客戶端 + function calling 迴圈。
    /// 工具清單由 ToolSchemas.Gemini 從同一份 manifest 轉出。
    /// </summary>
    public class GeminiClient : IChatClient
    {
        public string Model { get; set; } = AiProviders.DefaultModel(AiProvider.Gemini);

        private const string EndpointFmt = "https://generativelanguage.googleapis.com/v1beta/models/{0}:generateContent";

        private readonly string _apiKey;
        private readonly RevitApiExecutor _executor;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(180) };
        private readonly JArray _contents = new();

        public GeminiClient(string apiKey, UIApplication uiApp, RevitApiExecutor executor)
        {
            _apiKey = apiKey;
            _executor = executor;
        }

        public async Task SendAsync(string userText, Action<string> onText, Action<string> onToolNote)
        {
            _contents.Add(new JObject
            {
                ["role"] = "user",
                ["parts"] = new JArray { new JObject { ["text"] = userText } }
            });

            for (int turn = 0; turn < AiProviders.MaxToolTurns; turn++)
            {
                var reqBody = new JObject
                {
                    ["systemInstruction"] = new JObject
                    {
                        ["parts"] = new JArray { new JObject { ["text"] = AiProviders.SystemPrompt } }
                    },
                    ["contents"] = _contents,
                    ["tools"] = ToolSchemas.Gemini()
                };

                var url = string.Format(EndpointFmt, Uri.EscapeDataString(Model));
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("x-goog-api-key", _apiKey);
                req.Content = new StringContent(reqBody.ToString(), Encoding.UTF8, "application/json");

                var resp = await _http.SendAsync(req);
                var respText = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    onText("[API 錯誤 " + (int)resp.StatusCode + "] " + respText);
                    return;
                }

                var obj = JObject.Parse(respText);
                var content = obj["candidates"]?[0]?["content"] as JObject;
                if (content == null)
                {
                    var reason = obj["promptFeedback"]?["blockReason"]?.ToString()
                                 ?? obj["candidates"]?[0]?["finishReason"]?.ToString() ?? "無內容";
                    onText("[Gemini 沒有回覆：" + reason + "]");
                    return;
                }

                // 模型回覆原樣加回（含 functionCall 與 thoughtSignature），多輪工具呼叫才接得上
                content["role"] = "model";
                _contents.Add(content);

                var parts = content["parts"] as JArray ?? new JArray();
                var responses = new JArray();

                foreach (var part in parts)
                {
                    if (part["text"] is JToken t && part["thought"]?.Value<bool>() != true)
                    {
                        var s = t.ToString();
                        if (!string.IsNullOrWhiteSpace(s)) onText(s);
                    }
                    else if (part["functionCall"] is JObject fc)
                    {
                        var name = fc["name"]?.ToString() ?? "";
                        var input = ToolSchemas.FixGeminiArgs(name, (fc["args"] as JObject)?.DeepClone() as JObject ?? new JObject());
                        onToolNote("↳ 執行 " + name);
                        var result = ToolSchemas.Run(_executor, name, input);

                        // functionResponse.response 必須是物件
                        JToken parsed;
                        try { parsed = JToken.Parse(result); } catch { parsed = result; }
                        var fr = new JObject
                        {
                            ["name"] = name,
                            ["response"] = parsed is JObject po ? po : new JObject { ["result"] = parsed }
                        };
                        if (fc["id"] != null) fr["id"] = fc["id"];
                        responses.Add(new JObject { ["functionResponse"] = fr });
                    }
                }

                if (responses.Count == 0) return;
                _contents.Add(new JObject { ["role"] = "user", ["parts"] = responses });
            }

            onText("（已達工具呼叫上限，先停在這）");
        }
    }
}
