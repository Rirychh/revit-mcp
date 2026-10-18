// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.IO;
using Newtonsoft.Json.Linq;

namespace RevitMcpAddin.Ai
{
    /// <summary>面板可選的 AI 供應商。</summary>
    public enum AiProvider { Claude, OpenAI, Gemini }

    /// <summary>三家共用的對話介面：送一則訊息、跑完整個工具呼叫迴圈。</summary>
    public interface IChatClient
    {
        string Model { get; set; }
        Task SendAsync(string userText, Action<string> onText, Action<string> onToolNote);
    }

    /// <summary>供應商的顯示名稱、預設模型、金鑰存放名稱。</summary>
    public static class AiProviders
    {
        public static readonly AiProvider[] All = { AiProvider.Claude, AiProvider.OpenAI, AiProvider.Gemini };

        public static string DisplayName(AiProvider p) => p switch
        {
            AiProvider.OpenAI => "GPT",
            AiProvider.Gemini => "Gemini",
            _ => "Claude"
        };

        public static string KeyLabel(AiProvider p) => p switch
        {
            AiProvider.OpenAI => "OpenAI API 金鑰",
            AiProvider.Gemini => "Gemini API 金鑰（Google AI Studio）",
            _ => "Anthropic API 金鑰"
        };

        /// <summary>預設模型。可在面板的模型欄位改寫，改寫值存在設定檔。</summary>
        public static string DefaultModel(AiProvider p) => p switch
        {
            AiProvider.OpenAI => "gpt-5",
            AiProvider.Gemini => "gemini-2.5-pro",
            _ => "claude-sonnet-5"
        };

        /// <summary>Windows 認證管理員的目標名稱。Claude 沿用舊名 revit-mcp，既有金鑰不用重設。</summary>
        public static string CredTarget(AiProvider p) => p switch
        {
            AiProvider.OpenAI => "revit-mcp-openai",
            AiProvider.Gemini => "revit-mcp-gemini",
            _ => "revit-mcp"
        };

        public static IChatClient Create(AiProvider p, string apiKey, Autodesk.Revit.UI.UIApplication uiApp, RevitApiExecutor executor) => p switch
        {
            AiProvider.OpenAI => new OpenAIClient(apiKey, uiApp, executor),
            AiProvider.Gemini => new GeminiClient(apiKey, uiApp, executor),
            _ => new AnthropicClient(apiKey, uiApp, executor)
        };

        public const string SystemPrompt =
            "你是嵌在 Autodesk Revit 裡的助理。使用者用中文或英文下指令，你透過提供的工具讀取與修改目前開啟的 Revit 模型。" +
            "動作前先確認有開啟文件；破壞性操作（建立/修改/匯出）先簡述你要做什麼再執行。回覆用繁體中文。";

        public const int MaxToolTurns = 12;
    }

    /// <summary>
    /// 面板設定（目前選的供應商、各家模型），存在 ~/revit-mcp-panel.json。
    /// 舊版的 ~/revit-mcp-model.txt 仍視為 Claude 模型的覆寫值。
    /// </summary>
    public static class PanelSettings
    {
        private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        private static readonly string File_ = Path.Combine(Home, "revit-mcp-panel.json");
        private static readonly string LegacyModelFile = Path.Combine(Home, "revit-mcp-model.txt");

        public static AiProvider Provider { get; set; } = AiProvider.Claude;
        private static readonly Dictionary<AiProvider, string> _models = new();

        public static string GetModel(AiProvider p) =>
            _models.TryGetValue(p, out var m) && !string.IsNullOrWhiteSpace(m) ? m : AiProviders.DefaultModel(p);

        public static void SetModel(AiProvider p, string model) => _models[p] = model.Trim();

        public static void Load()
        {
            try
            {
                if (File.Exists(File_))
                {
                    var o = JObject.Parse(File.ReadAllText(File_));
                    if (Enum.TryParse<AiProvider>((string?)o["provider"], out var pv)) Provider = pv;
                    if (o["models"] is JObject ms)
                        foreach (var p in AiProviders.All)
                            if (ms[p.ToString()] is JToken t && t.Type == JTokenType.String) _models[p] = t.ToString();
                }
                if (!_models.ContainsKey(AiProvider.Claude) && File.Exists(LegacyModelFile))
                {
                    var m = File.ReadAllText(LegacyModelFile).Trim();
                    if (m != "") _models[AiProvider.Claude] = m;
                }
            }
            catch { /* 設定檔壞掉就用預設值 */ }
        }

        public static void Save()
        {
            try
            {
                var ms = new JObject();
                foreach (var kv in _models) ms[kv.Key.ToString()] = kv.Value;
                var o = new JObject { ["provider"] = Provider.ToString(), ["models"] = ms };
                File.WriteAllText(File_, o.ToString());
            }
            catch { }
        }
    }
}
