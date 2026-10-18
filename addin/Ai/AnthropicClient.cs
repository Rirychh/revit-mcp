// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.Net.Http;
using System.Text;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMcpAddin.Ai
{
    /// <summary>
    /// Anthropic Messages API 客戶端 + tool-use 迴圈。
    /// 對話在背景緒進行；工具呼叫透過 RevitApiExecutor 排到 Revit 主緒執行。
    /// </summary>
    public class AnthropicClient
    {
        // 模型 ID（可用 ~/revit-mcp-model.txt 覆蓋，免重編）
        public string Model = "claude-sonnet-5";
        public int MaxTokens = 4096;

        private const string Endpoint = "https://api.anthropic.com/v1/messages";
        private const string SystemPrompt =
            "你是嵌在 Autodesk Revit 裡的助理。使用者用中文或英文下指令，你透過提供的工具讀取與修改目前開啟的 Revit 模型。" +
            "動作前先確認有開啟文件；破壞性操作（建立/修改/匯出）先簡述你要做什麼再執行。回覆用繁體中文。";

        private readonly string _apiKey;
        private readonly UIApplication _uiApp;
        private readonly RevitApiExecutor _executor;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
        private readonly JArray _messages = new();

        public AnthropicClient(string apiKey, UIApplication uiApp, RevitApiExecutor executor)
        {
            _apiKey = apiKey;
            _uiApp = uiApp;
            _executor = executor;
        }

        /// <summary>送一則使用者訊息，跑完整 tool-use 迴圈，onText 回傳每段助理文字。</summary>
        public async Task SendAsync(string userText, Action<string> onText, Action<string> onToolNote)
        {
            _messages.Add(new JObject { ["role"] = "user", ["content"] = userText });

            for (int turn = 0; turn < 12; turn++)
            {
                var reqBody = new JObject
                {
                    ["model"] = Model,
                    ["max_tokens"] = MaxTokens,
                    ["system"] = SystemPrompt,
                    ["tools"] = RevitTools.Definitions(),
                    ["messages"] = _messages
                };

                var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                req.Headers.Add("x-api-key", _apiKey);
                req.Headers.Add("anthropic-version", "2023-06-01");
                req.Content = new StringContent(reqBody.ToString(), Encoding.UTF8, "application/json");

                var resp = await _http.SendAsync(req);
                var respText = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    onText("[API 錯誤 " + (int)resp.StatusCode + "] " + respText);
                    return;
                }

                var obj = JObject.Parse(respText);
                var content = (JArray)(obj["content"] ?? new JArray());

                // 助理這一輪的完整 content 要原樣加回對話（含 tool_use blocks）
                _messages.Add(new JObject { ["role"] = "assistant", ["content"] = content });

                var toolResults = new JArray();
                bool hasToolUse = false;

                foreach (var block in content)
                {
                    var btype = block["type"]?.ToString();
                    if (btype == "text")
                    {
                        var t = block["text"]?.ToString();
                        if (!string.IsNullOrEmpty(t)) onText(t);
                    }
                    else if (btype == "tool_use")
                    {
                        hasToolUse = true;
                        var name = block["name"]?.ToString() ?? "";
                        var id = block["id"]?.ToString() ?? "";
                        var input = block["input"] as JObject ?? new JObject();
                        onToolNote("↳ 執行 " + name);

                        string result;
                        try
                        {
                            result = _executor.Execute(app => RevitTools.Execute(app, name, input));
                        }
                        catch (Exception ex)
                        {
                            result = JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                        }

                        toolResults.Add(new JObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = id,
                            ["content"] = result
                        });
                    }
                }

                if (!hasToolUse) return;  // 純文字回覆，結束

                // 把工具結果當 user 訊息送回，續跑迴圈
                _messages.Add(new JObject { ["role"] = "user", ["content"] = toolResults });
            }

            onText("（已達工具呼叫上限，先停在這）");
        }
    }
}
