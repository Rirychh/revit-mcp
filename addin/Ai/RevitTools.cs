// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMcpAddin.Ai
{
    /// <summary>
    /// 把 Revit handler（經 RevitHttpServer.Route）包成 Claude 可呼叫的工具。
    ///
    /// 工具清單「不」寫在這個檔案裡：唯一事實源是 tools-manifest.json，
    /// 以 embedded resource 的形式跟 DLL 一起編進組件（見 RevitMcpAddin.csproj）。
    /// 之所以選 embedded 而不是「跟 DLL 放同資料夾的檔案」：
    ///   1) 部署只需要 .addin + .dll 兩個檔，不會出現「dll 更新了但 json 還是舊的」
    ///      或「安裝包漏帶 json」這種半壞狀態 —— manifest 和它所描述的 Route()
    ///      永遠是同一次編譯的產物。
    ///   2) 既有的安裝包（HTA 安裝器、share-package）都只處理那兩個檔，
    ///      多一個外部檔就多一條安裝失敗路徑。
    /// 代價是改 manifest 必須重新編譯；這是刻意的，因為 manifest 描述的是
    /// 編譯進去的那份 Route()。
    ///
    /// 面板呼叫路徑（刻意不走 HTTP）：
    ///   ChatPanel → RevitTools.Execute → RevitHttpServer.Route(uiApp, ...) (static)
    ///   而 Execute 本身是由 RevitApiExecutor 的 ExternalEvent 帶到 Revit 主執行緒上跑。
    /// 因為沒有經過 HttpListener，所以不需要（也拿不到）X-Revit-Mcp-Token，
    /// 也完全不受 RevitHttpServer 的認證閘門影響。
    /// </summary>
    public static class RevitTools
    {
        /// <summary>manifest 的 embedded resource 名稱（csproj 的 LogicalName）。</summary>
        private const string ManifestResourceName = "RevitMcpAddin.tools-manifest.json";

        /// <summary>只讀取並解析一次；失敗時不快取，讓下次呼叫重新丟出同樣的錯。</summary>
        private static JObject? _manifest;
        private static readonly object _lock = new();

        /// <summary>
        /// 載入 manifest。任何失敗（資源不存在、JSON 壞掉、結構不對）都直接丟例外，
        /// 絕不「靜默退回內建的一小份清單」—— 那樣會讓面板看起來正常運作，
        /// 實際上少了一大半工具，而且沒人會發現。
        /// </summary>
        private static JObject Manifest()
        {
            lock (_lock)
            {
                if (_manifest != null) return _manifest;

                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(ManifestResourceName);
                if (stream == null)
                {
                    var available = string.Join(", ", asm.GetManifestResourceNames());
                    throw new InvalidOperationException(
                        $"[RevitMCP] 找不到內嵌的工具清單 '{ManifestResourceName}'。" +
                        $"RevitMcpAddin.csproj 裡的 <EmbeddedResource Include=\"tools-manifest.json\" " +
                        $"LogicalName=\"{ManifestResourceName}\" /> 可能被移除了，或 tools-manifest.json 不在專案根目錄。" +
                        $"目前組件內的資源：[{available}]");
                }

                string text;
                using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
                    text = reader.ReadToEnd();

                JObject root;
                try
                {
                    root = JObject.Parse(text);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException(
                        $"[RevitMCP] 工具清單 tools-manifest.json 不是合法的 JSON：{ex.Message}", ex);
                }

                if (root["tools"] is not JArray tools || tools.Count == 0)
                    throw new InvalidOperationException(
                        "[RevitMCP] 工具清單 tools-manifest.json 缺少 tools 陣列，或陣列是空的。");

                // 結構健檢：缺欄位的話現在就炸，不要等到 Claude 真的去呼叫才出錯。
                for (int i = 0; i < tools.Count; i++)
                {
                    var t = tools[i] as JObject
                            ?? throw new InvalidOperationException(
                                $"[RevitMCP] tools[{i}] 不是物件。");
                    foreach (var key in new[] { "name", "method", "route", "summary", "exposedTo", "input_schema" })
                        if (t[key] == null)
                            throw new InvalidOperationException(
                                $"[RevitMCP] tools[{i}]（{t["name"]?.ToString() ?? "未命名"}）缺少必要欄位 '{key}'。");
                }

                _manifest = root;
                return root;
            }
        }

        /// <summary>manifest 裡所有曝光給面板的工具。</summary>
        private static IEnumerable<JObject> PanelTools() =>
            ((JArray)Manifest()["tools"]!)
                .Cast<JObject>()
                .Where(t => t["exposedTo"] is JArray ex &&
                            ex.Any(v => string.Equals(v.ToString(), "panel", StringComparison.Ordinal)));

        /// <summary>
        /// 回傳 Anthropic tools 陣列（JSON），由 manifest 產生。
        /// 工具說明會把破壞性旗標寫進去，讓模型自己知道哪些要先問人。
        /// </summary>
        public static JArray Definitions()
        {
            var arr = new JArray();

            foreach (var t in PanelTools())
            {
                var desc = t["summary"]!.ToString();

                var notes = t["notes"]?.ToString();
                if (!string.IsNullOrWhiteSpace(notes))
                    desc += "\n備註：" + notes;

                bool destructive = t["destructive"]?.Value<bool>() ?? false;
                bool alwaysWrites = t["alwaysWrites"]?.Value<bool>() ?? false;

                if (destructive && alwaysWrites)
                    desc += "\n⚠ 這支工具會改模型／覆蓋檔案，而且沒有預檢開關 —— 呼叫前先向使用者確認。";
                else if (destructive)
                    desc += "\n⚠ 這支工具可能刪除元件或覆蓋檔案 —— 先用預檢（apply=false／dryRun=true）看結果，確認後再執行。";
                else if (alwaysWrites)
                    desc += "\n注意：沒有預檢開關，呼叫就會寫入模型。";

                arr.Add(new JObject
                {
                    ["name"]         = t["name"]!.ToString(),
                    ["description"]  = desc,
                    // manifest 的 input_schema 已經是 JSON Schema，直接深拷貝給 API。
                    ["input_schema"] = t["input_schema"]!.DeepClone()
                });
            }

            return arr;
        }

        /// <summary>
        /// 執行一個工具呼叫（在 Revit 主緒內，由 executor 帶入 uiApp）。
        /// 依 manifest 把 tool input 轉成 (method, path, body, query) 再走 Route。
        /// </summary>
        public static string Execute(UIApplication uiApp, string toolName, JObject input)
        {
            JObject tool;
            try
            {
                tool = PanelTools().FirstOrDefault(t =>
                           string.Equals(t["name"]!.ToString(), toolName, StringComparison.Ordinal))
                       ?? throw new InvalidOperationException(
                           $"Unknown tool: {toolName}（不在 tools-manifest.json 的 panel 清單中）");
            }
            catch (Exception ex)
            {
                // 連 manifest 都載不起來時，也要回一個結構正確的錯誤給聊天面板，
                // 而不是讓例外穿出去變成一個看不懂的 Revit 崩潰對話框。
                return JsonConvert.SerializeObject(new { success = false, error = ex.Message });
            }

            var method = tool["method"]!.ToString();
            var route  = tool["route"]!.ToString();

            var pathParams = (tool["pathParams"] as JArray)?
                                 .Select(v => v.ToString()).ToHashSet(StringComparer.Ordinal)
                             ?? new HashSet<string>(StringComparer.Ordinal);

            // 1) 把路徑參數代進 route 的 {placeholder}
            foreach (var p in pathParams)
            {
                var token = input[p];
                if (token == null || token.Type == JTokenType.Null)
                    return JsonConvert.SerializeObject(new
                    {
                        success = false,
                        error   = $"工具 {toolName} 缺少必要的路徑參數 '{p}'。"
                    });

                route = route.Replace("{" + p + "}", Scalar(token));
            }

            // 2) 其餘參數：GET 進查詢字串，POST/PUT 進 body
            var qs   = new NameValueCollection();
            var body = new JObject();
            bool isGet = string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);

            foreach (var prop in input.Properties())
            {
                if (pathParams.Contains(prop.Name)) continue;
                if (prop.Value.Type == JTokenType.Null) continue;   // 省略＝不送，讓 handler 用自己的預設值

                if (isGet) qs[prop.Name] = Scalar(prop.Value);
                else       body[prop.Name] = prop.Value.DeepClone();
            }

            // POST/PUT 一律送物件（空也送 "{}"）：好幾個 handler 對空 body 會丟
            // "Invalid request body."，送 {} 才會走到它們自己的預設值。
            var bodyText = isGet ? "" : body.ToString(Formatting.None);

            return RevitHttpServer.Route(uiApp, method.ToUpperInvariant(), route, bodyText, qs);
        }

        /// <summary>
        /// 把 JToken 轉成查詢字串／路徑用的純量字串。
        /// 查詢字串的值在 Revit 端都是以字串讀取後再自行解析，所以布林要給小寫
        /// 的 "true"/"false"（handler 多半是不分大小寫比對，但不要賭）。
        /// 陣列／物件不該出現在查詢字串裡，真的出現就序列化成 JSON，至少不會靜默丟掉。
        /// </summary>
        private static string Scalar(JToken token) => token.Type switch
        {
            JTokenType.Boolean => token.Value<bool>() ? "true" : "false",
            JTokenType.Object or JTokenType.Array => token.ToString(Formatting.None),
            _ => token.ToString()
        };
    }
}
