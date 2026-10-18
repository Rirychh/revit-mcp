// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMcpAddin.Ai
{
    /// <summary>
    /// 把 RevitTools.Definitions()（Anthropic 格式）轉成 OpenAI、Gemini 的工具格式。
    /// 三家共用同一份 tools-manifest.json，工具清單不會分岔。
    /// </summary>
    public static class ToolSchemas
    {
        private static JArray? _openAi, _gemini;
        private static readonly object _lock = new();

        /// <summary>OpenAI Chat Completions 的 tools 陣列。JSON Schema 原樣沿用（非 strict 模式）。</summary>
        public static JArray OpenAI()
        {
            lock (_lock)
            {
                if (_openAi != null) return _openAi;
                var arr = new JArray();
                foreach (JObject t in RevitTools.Definitions())
                {
                    arr.Add(new JObject
                    {
                        ["type"] = "function",
                        ["function"] = new JObject
                        {
                            ["name"] = t["name"],
                            ["description"] = t["description"],
                            ["parameters"] = t["input_schema"]!.DeepClone()
                        }
                    });
                }
                return _openAi = arr;
            }
        }

        /// <summary>
        /// Gemini 的 tools（一個 functionDeclarations 陣列）。Gemini 只接受 OpenAPI 子集，
        /// 所以把不支援的關鍵字拿掉；預設值、下限等資訊改寫進 description，模型仍看得到。
        /// </summary>
        public static JArray Gemini()
        {
            lock (_lock)
            {
                if (_gemini != null) return _gemini;
                var decls = new JArray();
                foreach (JObject t in RevitTools.Definitions())
                {
                    var d = new JObject { ["name"] = t["name"], ["description"] = t["description"] };
                    var p = GeminiSchema(t["input_schema"] as JObject ?? new JObject());
                    // 沒有參數的工具不送 parameters（Gemini 不接受空 properties 的 OBJECT）
                    if (p["properties"] is JObject props && props.Count > 0) d["parameters"] = p;
                    decls.Add(d);
                }
                return _gemini = new JArray { new JObject { ["functionDeclarations"] = decls } };
            }
        }

        private static readonly HashSet<string> GeminiKeep = new(StringComparer.Ordinal)
        {
            "type", "description", "enum", "properties", "required", "items",
            "minimum", "maximum", "minItems", "maxItems", "nullable", "format"
        };

        internal static JObject GeminiSchema(JObject s)
        {
            var o = new JObject();
            var extra = new List<string>();

            // type 可能是 ["string","null"] 這種陣列寫法
            var type = s["type"];
            if (type is JArray ta)
            {
                var nonNull = ta.Select(x => x.ToString()).Where(x => x != "null").ToList();
                o["type"] = nonNull.FirstOrDefault() ?? "string";
                if (nonNull.Count < ta.Count) o["nullable"] = true;
            }
            else if (type != null) o["type"] = type.DeepClone();

            foreach (var prop in s.Properties())
            {
                switch (prop.Name)
                {
                    case "type": break;
                    case "properties":
                        var ps = new JObject();
                        foreach (var pp in ((JObject)prop.Value).Properties())
                            ps[pp.Name] = pp.Value is JObject po ? GeminiSchema(po) : pp.Value.DeepClone();
                        o["properties"] = ps;
                        break;
                    case "items":
                        o["items"] = prop.Value is JObject io ? GeminiSchema(io) : new JObject { ["type"] = "string" };
                        break;
                    case "enum":
                        // Gemini 的 enum 只能用在字串；數字 enum 改寫進說明
                        if ((string?)o["type"] == "string" || o["type"] == null)
                            o["enum"] = new JArray(((JArray)prop.Value).Select(v => v.ToString()));
                        else
                            extra.Add("可用值：" + string.Join(", ", ((JArray)prop.Value).Select(v => v.ToString())));
                        break;
                    case "default":
                        extra.Add("預設 " + prop.Value.ToString(Formatting.None));
                        break;
                    case "exclusiveMinimum":
                        extra.Add("須大於 " + prop.Value);
                        break;
                    case "minLength":
                        extra.Add("最少 " + prop.Value + " 字");
                        break;
                    default:
                        if (GeminiKeep.Contains(prop.Name)) o[prop.Name] = prop.Value.DeepClone();
                        break;   // additionalProperties、$schema 等直接略過
                }
            }

            if (extra.Count > 0)
            {
                var desc = (string?)o["description"] ?? "";
                o["description"] = (desc == "" ? "" : desc + "（") + string.Join("；", extra) + (desc == "" ? "" : "）");
            }

            // 自由鍵值的物件（沒有 properties，例如參數對照表）Gemini 不接受，
            // 改成 JSON 字串；執行前由 FixGeminiArgs 轉回物件。
            if ((string?)o["type"] == "object" && (o["properties"] is not JObject op || op.Count == 0))
            {
                var desc = (string?)o["description"] ?? "";
                return new JObject
                {
                    ["type"] = "string",
                    ["description"] = (desc == "" ? "" : desc + "。") + "請填 JSON 物件字串，例如 {\"名稱\":\"值\"}"
                };
            }

            // required 只留真的存在的欄位，否則 Gemini 會回 400
            if (o["required"] is JArray req && o["properties"] is JObject pr)
                o["required"] = new JArray(req.Select(r => r.ToString()).Where(r => pr[r] != null));
            else if (o["properties"] == null) o.Remove("required");

            return o;
        }

        /// <summary>
        /// 把 Gemini 傳回的參數依原始 schema 還原：GeminiSchema 改成字串的自由物件，在這裡解析回物件。
        /// </summary>
        internal static JObject FixGeminiArgs(string toolName, JObject args)
        {
            var schema = RevitTools.Definitions()
                .Cast<JObject>()
                .FirstOrDefault(t => (string?)t["name"] == toolName)?["input_schema"] as JObject;
            if (schema == null) return args;
            return (Fix(schema, args) as JObject) ?? args;
        }

        private static JToken Fix(JObject schema, JToken value)
        {
            var type = schema["type"]?.ToString();
            var props = schema["properties"] as JObject;

            if (type == "object" && (props == null || props.Count == 0) && value.Type == JTokenType.String)
            {
                try { return JToken.Parse(value.ToString()); } catch { return value; }
            }
            if (value is JObject obj && props != null)
            {
                foreach (var p in obj.Properties().ToList())
                    if (props[p.Name] is JObject ps) p.Value = Fix(ps, p.Value);
                return obj;
            }
            if (value is JArray arr && schema["items"] is JObject its)
            {
                for (int i = 0; i < arr.Count; i++) arr[i] = Fix(its, arr[i]);
                return arr;
            }
            return value;
        }

        /// <summary>在 Revit 主緒執行工具，例外轉成結構化錯誤字串。</summary>
        internal static string Run(RevitApiExecutor executor, string name, JObject input)
        {
            try { return executor.Execute(app => RevitTools.Execute(app, name, input)); }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { success = false, error = ex.Message }); }
        }
    }
}
