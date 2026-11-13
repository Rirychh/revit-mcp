// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 視圖樣板與品類可見性。
    ///  GET  /views/templates                       列出所有視圖樣板
    ///  POST /views/template   { viewIds, templateName|templateId, apply }   套用樣板（"" = 移除樣板）
    ///  POST /views/categories { viewIds, categories:[OST_名或顯示名], hidden:bool, apply }
    ///       視圖有套樣板且樣板控制 V/G 時，改動會寫在樣板上並回報。
    ///  POST /views/grids3d   { viewIds, levels, mode:set|add|remove|clear, apply }  3D 視圖顯示柱線的樓層
    /// </summary>
    public class ViewSettingsHandler
    {
        private readonly Document _doc;
        public ViewSettingsHandler(Document doc) => _doc = doc;

        private static string Ok(object data) => JsonConvert.SerializeObject(new { success = true, data, error = (string?)null });
        private static string Fail(string msg) => JsonConvert.SerializeObject(new { success = false, data = (object?)null, error = msg });
        private static JObject Parse(string b) => string.IsNullOrWhiteSpace(b) ? new JObject() : JObject.Parse(b);

        private List<View> Templates() => new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => v.IsTemplate).OrderBy(v => v.Name).ToList();

        public string ListTemplates()
        {
            var list = Templates().Select(t => new
            {
                id = t.Id.Value, name = t.Name, viewType = t.ViewType.ToString(),
                usedBy = new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>()
                         .Count(v => !v.IsTemplate && v.ViewTemplateId == t.Id)
            }).ToList();
            return Ok(new { count = list.Count, templates = list });
        }

        private List<View> ResolveViews(JObject r, List<string> warn)
        {
            var res = new List<View>();
            foreach (var t in (r["viewIds"] as JArray) ?? new JArray())
            {
                long id = (long)t;
                if (_doc.GetElement(ElementIdCompat.NewId(id)) is View v) res.Add(v); else warn.Add($"找不到視圖 {id}");
            }
            return res;
        }

        public string ApplyTemplate(string body)
        {
            var r = Parse(body); bool apply = (bool?)r["apply"] ?? false;
            var warn = new List<string>();
            var views = ResolveViews(r, warn);
            if (views.Count == 0) return Fail("需要 viewIds。");

            View? tpl = null;
            string? name = (string?)r["templateName"];
            long tid = (long?)r["templateId"] ?? 0;
            bool remove = name == "" && tid == 0;
            if (!remove)
            {
                var all = Templates();
                tpl = tid > 0 ? all.FirstOrDefault(t => t.Id.Value == tid)
                              : all.FirstOrDefault(t => t.Name == name) ?? all.FirstOrDefault(t => t.Name.Contains(name ?? "\u0000"));
                if (tpl == null) return Fail($"找不到視圖樣板「{name}{(tid > 0 ? tid.ToString() : "")}」。可用：{string.Join("、", all.Select(t => t.Name))}");
            }

            var plan = views.Select(v => new
            {
                id = v.Id.Value, name = v.Name,
                before = v.ViewTemplateId == ElementId.InvalidElementId ? "(無)" : _doc.GetElement(v.ViewTemplateId)?.Name,
                after = tpl?.Name ?? "(無)",
                ok = tpl == null || v.IsValidViewTemplate(tpl.Id)
            }).ToList();
            if (!apply) return Ok(new { applied = false, views = plan, warnings = warn });

            using (var tx = new Transaction(_doc, "MCP 套用視圖樣板"))
            {
                tx.Start();
                foreach (var v in views)
                {
                    if (tpl != null && !v.IsValidViewTemplate(tpl.Id)) { warn.Add($"{v.Name}：樣板類型不符，略過"); continue; }
                    v.ViewTemplateId = tpl?.Id ?? ElementId.InvalidElementId;
                }
                tx.Commit();
            }
            return Ok(new { applied = true, views = plan, warnings = warn });
        }

        private Category? FindCategory(string key)
        {
            if (Enum.TryParse<BuiltInCategory>(key.StartsWith("OST_") ? key : "OST_" + key, true, out var bic))
            {
                try { var c = Category.GetCategory(_doc, bic); if (c != null) return c; } catch { }
            }
            foreach (Category c in _doc.Settings.Categories)
                if (string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        public string SetCategories(string body)
        {
            var r = Parse(body); bool apply = (bool?)r["apply"] ?? false;
            bool hidden = (bool?)r["hidden"] ?? true;
            var warn = new List<string>();
            var views = ResolveViews(r, warn);
            if (views.Count == 0) return Fail("需要 viewIds。");
            var cats = new List<Category>();
            foreach (var t in (r["categories"] as JArray) ?? new JArray())
            {
                var c = FindCategory((string)t!);
                if (c == null) warn.Add($"找不到品類 {t}"); else cats.Add(c);
            }
            if (cats.Count == 0) return Fail("需要 categories。" + string.Join("；", warn));

            // 視圖若被樣板控制 V/G（模型品類），改在樣板上
            var targets = new Dictionary<long, (View target, List<string> from)>();
            foreach (var v in views)
            {
                View target = v;
                if (v.ViewTemplateId != ElementId.InvalidElementId && _doc.GetElement(v.ViewTemplateId) is View tpl)
                {
                    var ctrl = tpl.GetNonControlledTemplateParameterIds();
                    bool vgFree = ctrl.Contains(new ElementId(BuiltInParameter.VIS_GRAPHICS_MODEL));
                    if (!vgFree) target = tpl;
                }
                if (!targets.TryGetValue(target.Id.Value, out var e)) { e = (target, new List<string>()); targets[target.Id.Value] = e; }
                e.from.Add(v.Name);
            }
            var plan = targets.Values.Select(t => new
            {
                target = t.target.Name, isTemplate = t.target.IsTemplate, forViews = t.from,
                categories = cats.Select(c => new { name = c.Name, canHide = t.target.CanCategoryBeHidden(c.Id), hiddenNow = t.target.GetCategoryHidden(c.Id) })
            }).ToList();
            if (!apply) return Ok(new { applied = false, hidden, plan, warnings = warn });

            using (var tx = new Transaction(_doc, "MCP 品類可見性"))
            {
                tx.Start();
                foreach (var t in targets.Values)
                    foreach (var c in cats)
                        if (t.target.CanCategoryBeHidden(c.Id)) t.target.SetCategoryHidden(c.Id, hidden);
                        else warn.Add($"{t.target.Name}：{c.Name} 不能隱藏");
                tx.Commit();
            }
            return Ok(new { applied = true, hidden, plan, warnings = warn });
        }

        /// <summary>
        /// POST /views/grids3d { viewIds, levels:[名稱或id], mode:"set"|"add"|"remove"|"clear", apply }
        /// 3D 視圖的「顯示柱線」（Show Grids）：指定柱線要畫在哪些樓層平面上。
        /// 只有與該樓層平面相交的柱線會顯示。此設定不屬於視圖樣板，逐一視圖設定。
        /// </summary>
        public string SetGrids3d(string body)
        {
            var r = Parse(body); bool apply = (bool?)r["apply"] ?? false;
            string mode = ((string?)r["mode"] ?? "set").ToLowerInvariant();
            if (mode != "set" && mode != "add" && mode != "remove" && mode != "clear")
                return Fail($"mode 只能是 set／add／remove／clear，收到「{mode}」。");

            var warn = new List<string>();
            var views = ResolveViews(r, warn);
            var v3 = new List<View3D>();
            foreach (var v in views)
                if (v is View3D d && !d.IsTemplate) v3.Add(d); else warn.Add($"{v.Name} 不是 3D 視圖，略過");
            if (v3.Count == 0) return Fail("需要至少一個 3D 視圖的 viewIds。" + string.Join("；", warn));

            var allLevels = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).ToList();
            var picked = new List<Level>();
            foreach (var t in (r["levels"] as JArray) ?? new JArray())
            {
                Level? lv = null;
                if (t.Type == JTokenType.Integer) lv = allLevels.FirstOrDefault(l => l.Id.Value == (long)t);
                else
                {
                    var key = (string?)t ?? "";
                    lv = allLevels.FirstOrDefault(l => l.Name == key)
                         ?? allLevels.FirstOrDefault(l => string.Equals(l.Name, key, StringComparison.OrdinalIgnoreCase));
                    if (lv == null && long.TryParse(key, out var lid)) lv = allLevels.FirstOrDefault(l => l.Id.Value == lid);
                }
                if (lv == null) warn.Add($"找不到樓層 {t}"); else if (!picked.Contains(lv)) picked.Add(lv);
            }
            if (mode != "clear" && picked.Count == 0)
                return Fail("需要 levels（樓層名稱或 id）。可用：" + string.Join("、", allLevels.Select(l => l.Name)) +
                            (warn.Count > 0 ? "。" + string.Join("；", warn) : ""));

            string LvName(ElementId id) => _doc.GetElement(id)?.Name ?? id.Value.ToString();

            var plan = new List<object>();
            var next = new Dictionary<long, HashSet<ElementId>>();
            foreach (var v in v3)
            {
                var cur = new HashSet<ElementId>(v.GetLevelsThatShowGrids());
                var nxt = mode switch
                {
                    "set"    => new HashSet<ElementId>(picked.Select(l => l.Id)),
                    "add"    => new HashSet<ElementId>(cur.Concat(picked.Select(l => l.Id))),
                    "remove" => new HashSet<ElementId>(cur.Where(id => !picked.Any(l => l.Id == id))),
                    _        => new HashSet<ElementId>()
                };
                next[v.Id.Value] = nxt;
                plan.Add(new
                {
                    id = v.Id.Value, name = v.Name,
                    perspective = v.IsPerspective,
                    sectionBox = v.IsSectionBoxActive,
                    before = cur.Select(LvName).ToList(),
                    after = nxt.Select(LvName).ToList()
                });
                if (v.IsPerspective) warn.Add($"{v.Name} 是透視圖：柱線標頭不會顯示，線也不會是虛線");
            }
            if (!apply) return Ok(new { applied = false, mode, views = plan, warnings = warn });

            using (var tx = new Transaction(_doc, "MCP 3D 視圖顯示柱線"))
            {
                tx.Start();
                foreach (var v in v3) v.ShowGridsOnLevels(next[v.Id.Value]);
                tx.Commit();
            }
            return Ok(new { applied = true, mode, views = plan, warnings = warn });
        }
    }
}
