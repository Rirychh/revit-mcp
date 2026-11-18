// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 規則設定：%LOCALAPPDATA%\revit-mcp\rules.json（沒有這個檔就用預設值）。
    /// {
    ///   "disabledTools": ["revit_ungroup_groups"],   停用的工具（MCP 與 Revit 內對話面板都擋）
    ///   "legacyPreview": true,                       早期建模工具沒帶 apply=true 時只預檢
    ///   "checkOwnership": true                       工作共用模型：元件被別人借用時先擋下
    /// }
    /// 檔案修改後下次呼叫即生效（依修改時間重讀）。MCP server 端只在啟動時讀 disabledTools
    /// 來決定工具清單，所以改了 disabledTools 之後要重開 MCP 客戶端才會從清單消失；
    /// add-in 這邊則是立刻擋。
    /// </summary>
    internal static class Rules
    {
        public static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "revit-mcp", "rules.json");

        private static JObject _cfg = new JObject();
        private static DateTime _stamp = DateTime.MinValue;
        private static readonly object _lock = new object();

        private static JObject Cfg
        {
            get
            {
                lock (_lock)
                {
                    try
                    {
                        if (!File.Exists(FilePath)) { _cfg = new JObject(); _stamp = DateTime.MinValue; return _cfg; }
                        var t = File.GetLastWriteTimeUtc(FilePath);
                        if (t != _stamp) { _cfg = JObject.Parse(File.ReadAllText(FilePath)); _stamp = t; }
                    }
                    catch { _cfg = new JObject(); }
                    return _cfg;
                }
            }
        }

        private static bool Flag(string key, bool dflt)
        {
            var v = Cfg[key];
            return v != null && v.Type == JTokenType.Boolean ? (bool)v : dflt;
        }

        public static bool IsDisabled(string tool) =>
            Cfg["disabledTools"] is JArray a && a.Any(v => string.Equals(v.ToString(), tool, StringComparison.Ordinal));

        public static bool LegacyPreview => Flag("legacyPreview", true);
        public static bool CheckOwnership => Flag("checkOwnership", true);
    }

    /// <summary>
    /// 進到各 Handler 之前的共用檢查。HTTP（MCP server）與 Revit 內對話面板都走
    /// RevitHttpServer.Route，所以放在這裡兩邊都涵蓋。
    /// Check 回 null＝放行；回字串＝直接當作回應（不進 Handler、不開交易）。
    /// </summary>
    internal static class RouteGuards
    {
        private sealed class ToolRef
        {
            public string Name = "", Method = "";
            public Regex Pattern = new Regex("^$");
        }

        private static List<ToolRef>? _index;
        private static readonly object _lock = new object();

        /// <summary>由內嵌的 tools-manifest.json 建立「方法＋路徑 → 工具名稱」對照。</summary>
        private static List<ToolRef> Index()
        {
            lock (_lock)
            {
                if (_index != null) return _index;
                var list = new List<ToolRef>();
                foreach (var t in ((JArray)Ai.RevitTools.Manifest()["tools"]!).Cast<JObject>())
                {
                    var route = t["route"]!.ToString();
                    var rx = "^" + Regex.Replace(Regex.Escape(route), @"\\\{[^}]+\}", "[^/]+") + "$";
                    list.Add(new ToolRef
                    {
                        Name = t["name"]!.ToString(),
                        Method = t["method"]!.ToString().ToUpperInvariant(),
                        Pattern = new Regex(rx, RegexOptions.CultureInvariant),
                    });
                }
                // 沒有 {placeholder} 的路由優先（/elements/rotate 要先於 /elements/{id}）。
                _index = list.OrderBy(r => r.Pattern.ToString().Contains("[^/]+") ? 1 : 0).ToList();
                return _index;
            }
        }

        private static string? ToolFor(string method, string path)
        {
            try { return Index().FirstOrDefault(r => r.Method == method && r.Pattern.IsMatch(path))?.Name; }
            catch { return null; }   // manifest 讀不到時不擋路，維持原本行為
        }

        /// <summary>
        /// 早期的建模工具：Handler 本身沒有預檢開關，呼叫就寫入。
        /// 這裡統一補上：沒帶 apply=true 就只回報請求內容，不進 Handler。
        /// </summary>
        private static readonly HashSet<string> LegacyWriters = new HashSet<string>(StringComparer.Ordinal)
        {
            "revit_create_element", "revit_modify_element", "revit_create_view",
            "revit_create_levels", "revit_create_walls", "revit_create_floors", "revit_create_columns",
            "revit_mass_boxes", "revit_link_cad", "revit_move_link",
        };

        /// <summary>會「就地修改指定元件」的工具，以及請求裡放元件 id 的欄位。</summary>
        private static readonly Dictionary<string, string[]> OwnedIdFields = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["revit_move_elements"] = new[] { "elementIds" },
            ["revit_rotate_elements"] = new[] { "ids" },
            ["revit_mirror_elements"] = new[] { "ids" },
            ["revit_change_type"] = new[] { "elementIds" },
            ["revit_batch_set_parameters"] = new[] { "updates" },
            ["revit_delete_elements"] = new[] { "elementIds" },
            ["revit_ungroup_groups"] = new[] { "groupIds" },
            ["revit_join_geometry"] = new[] { "elementIds", "pairs" },
            ["revit_snap_elements_to_grid"] = new[] { "elementIds" },
            ["revit_align_elements_upright"] = new[] { "ids" },
            ["revit_set_phases"] = new[] { "elementIds" },
            ["revit_attach_walls"] = new[] { "wallIds" },
            ["revit_modify_element"] = new string[0],   // id 在路徑上
        };

        public static string? Check(UIApplication uiApp, string method, string path, string body)
        {
            var tool = ToolFor(method, path);
            if (tool == null) return null;

            if (Rules.IsDisabled(tool))
                return PortUtil.Fail($"工具 {tool} 已在規則設定停用（{Rules.FilePath} 的 disabledTools）。");

            JObject req;
            try { req = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body); }
            catch { return null; }   // 內容不是 JSON 物件：交給 Handler 自己回報格式錯誤

            var doc = uiApp.ActiveUIDocument?.Document;

            if (doc != null && Rules.CheckOwnership && OwnedIdFields.TryGetValue(tool, out var fields))
            {
                var blocked = OwnedByOthers(doc, tool, path, req, fields);
                if (blocked != null) return blocked;
            }

            if (Rules.LegacyPreview && LegacyWriters.Contains(tool) && !((bool?)req["apply"] ?? false))
                return LegacyPreflight(doc, tool, path, req);

            return null;
        }

        // ── 工作共用：被別人借用的元件 ─────────────────────────────

        private static string? OwnedByOthers(Document doc, string tool, string path, JObject req, string[] fields)
        {
            if (!doc.IsWorkshared) return null;
            // 鏡射預設保留原件（copy=true）：原件不變，不需要擁有權。
            if (tool == "revit_mirror_elements" && ((bool?)req["copy"] ?? true)) return null;

            var ids = new HashSet<long>();
            if (tool == "revit_modify_element" &&
                long.TryParse(path.Substring(path.LastIndexOf('/') + 1), out var pid)) ids.Add(pid);
            foreach (var f in fields) Collect(req[f], ids);
            if (ids.Count == 0) return null;

            var owned = new List<object>();
            var owners = new List<string>();
            foreach (var id in ids)
            {
                try
                {
                    var eid = ElementIdCompat.NewId(id);
                    if (doc.GetElement(eid) == null) continue;   // 不存在的 id 交給 Handler 回報
                    if (WorksharingUtils.GetCheckoutStatus(doc, eid, out string owner) == CheckoutStatus.OwnedByOtherUser)
                    {
                        owned.Add(new { elementId = id, owner });
                        if (!string.IsNullOrEmpty(owner) && !owners.Contains(owner)) owners.Add(owner);
                    }
                }
                catch { }
            }
            if (owned.Count == 0) return null;

            var who = string.Join("、", owners);
            return Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                success = false,
                data = new { ownedByOthers = owned.Take(200), total = owned.Count },
                error = $"有 {owned.Count} 個元件被其他使用者借用中（{who}），沒有執行，模型沒有變更。" +
                        "請對方與中央同步並放棄借用後再試，或把這些元件從清單拿掉。" +
                        "（借用狀態取自本機快取；先「重新載入最新」會比較準。）",
            });
        }

        private static void Collect(JToken? t, HashSet<long> ids)
        {
            if (t == null) return;
            if (t.Type == JTokenType.Integer) { ids.Add((long)t); return; }
            if (t is JObject o) { Collect(o["elementId"], ids); return; }
            if (t is JArray a) foreach (var x in a) Collect(x, ids);
        }

        // ── 早期建模工具的預檢 ───────────────────────────────────

        private static string LegacyPreflight(Document? doc, string tool, string path, JObject req)
        {
            var echo = (JObject)req.DeepClone();
            echo.Remove("apply");

            // 陣列欄位的筆數（牆的迴圈數、樓板數、標高數…）
            var counts = new JObject();
            foreach (var p in echo.Properties())
                if (p.Value is JArray arr) counts[p.Name] = arr.Count;

            var checks = new List<string>();
            if (doc == null) checks.Add("目前沒有作用中的文件。");
            else if (!string.IsNullOrWhiteSpace((string?)echo["document"]))
                checks.Add("請求指定了 document；預檢不對該文件做樓層檢查。");
            else
            {
                foreach (var key in new[] { "levelName", "level" })
                {
                    var name = (string?)echo[key];
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    bool found = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Any(l => l.Name == name);
                    checks.Add(found ? $"樓層「{name}」存在。" : $"找不到樓層「{name}」（用 revit_list_levels 查正確名稱）。");
                }

                if (tool == "revit_modify_element")
                {
                    long.TryParse(path.Substring(path.LastIndexOf('/') + 1), out var id);
                    var el = doc.GetElement(ElementIdCompat.NewId(id));
                    if (el == null) checks.Add($"找不到元件 {id}。");
                    else if (echo["parameters"] is JObject ps)
                    {
                        var now = new JObject();
                        foreach (var p in ps.Properties())
                        {
                            var prm = el.LookupParameter(p.Name);
                            now[p.Name] = prm == null ? "（找不到這個參數）"
                                        : prm.IsReadOnly ? "（唯讀）" + (prm.AsValueString() ?? prm.AsString())
                                        : (prm.AsValueString() ?? prm.AsString());
                        }
                        echo["currentValues"] = now;
                    }
                }
            }

            return PortUtil.Ok(new
            {
                preflight = true,
                applied = false,
                tool,
                request = echo,
                counts,
                checks,
                note = "預檢：模型沒有變更。這支工具的預檢只回報請求內容、筆數與上列檢查，不做幾何或類型驗證；" +
                       "確認後加 apply=true 執行（單一交易，失敗整批回滾）。",
            });
        }
    }
}
