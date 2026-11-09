// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using static RevitMcpAddin.Handlers.PortUtil;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 一般修改操作：移動、照向量複製／線性陣列、換類型、複製類型（可改厚度與參數）、接合幾何。
    /// 全部 apply=false 預檢；apply=true 單一交易，任何錯誤整批回滾。
    /// </summary>
    public class EditOpsHandler
    {
        private const double FT = 304.8;
        private readonly Document _doc;
        public EditOpsHandler(Document doc) => _doc = doc;

        private static XYZ? Vec(JToken? t) =>
            t is JArray a && a.Count >= 2 ? new XYZ((double)a[0] / FT, (double)a[1] / FT, a.Count >= 3 ? (double)a[2] / FT : 0) : null;

        private (List<Element> ok, List<object> bad) Resolve(JToken? ids)
        {
            var ok = new List<Element>(); var bad = new List<object>();
            foreach (var id in Ids(ids))
            {
                var e = Get(_doc, id);
                if (e == null) bad.Add(new { elementId = id, reason = "找不到元件" });
                else if (e is ElementType) bad.Add(new { elementId = id, reason = "這是類型，不是放在模型裡的元件" });
                else if (e.Pinned) bad.Add(new { elementId = id, reason = "已釘選（pinned），請先解除釘選" });
                else if (e.GroupId != ElementId.InvalidElementId) bad.Add(new { elementId = id, reason = "在群組內，請先解除群組或改動群組" });
                else ok.Add(e);
            }
            return (ok, bad);
        }

        // POST /elements/move  { elementIds, deltaMm:[dx,dy,dz?], apply }
        public string Move(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var v = Vec(r["deltaMm"]); if (v == null || v.IsZeroLength()) return Fail("deltaMm 要是 [dx, dy, dz]（mm），而且不能是 0。");
            var (els, bad) = Resolve(r["elementIds"]);
            if (els.Count == 0) return Fail("沒有可移動的元件：" + Newtonsoft.Json.JsonConvert.SerializeObject(bad));
            var plan = new { willMove = els.Count, deltaMm = Mm(v), skipped = bad, sample = els.Take(20).Select(e => new { id = e.Id.Value, category = e.Category?.Name, name = e.Name }) };
            if (!apply) return Ok(new { preflight = true, plan });
            var res = RunTx(_doc, "MCP Move Elements", () => ElementTransformUtils.MoveElements(_doc, els.Select(e => e.Id).ToList(), v));
            return res.committed ? Ok(new { applied = true, moved = els.Count, plan, warnings = res.warnings })
                : Fail("移動失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }

        // POST /elements/array  { elementIds, deltaMm:[dx,dy,dz?], count?:1, apply }  每份依序位移 delta×i
        public string ArrayCopy(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var v = Vec(r["deltaMm"]); if (v == null || v.IsZeroLength()) return Fail("deltaMm 要是 [dx, dy, dz]（mm），而且不能是 0。");
            int count = Math.Max(1, Math.Min(500, (int?)r["count"] ?? 1));
            var (els, bad) = Resolve(r["elementIds"]);
            if (els.Count == 0) return Fail("沒有可複製的元件：" + Newtonsoft.Json.JsonConvert.SerializeObject(bad));
            if (els.Count * count > 5000) return Fail("一次最多產生 5000 個元件。");
            var plan = new { sourceCount = els.Count, copies = count, willCreate = els.Count * count, stepMm = Mm(v), lastOffsetMm = Mm(v * count), skipped = bad };
            if (!apply) return Ok(new { preflight = true, plan });
            var created = new List<long>();
            var res = RunTx(_doc, "MCP Array Elements", () =>
            {
                var src = els.Select(e => e.Id).ToList();
                for (int i = 1; i <= count; i++) created.AddRange(ElementTransformUtils.CopyElements(_doc, src, v * i).Select(x => x.Value));
            });
            return res.committed ? Ok(new { applied = true, created = created.Count, ids = created.Take(2000), plan, warnings = res.warnings })
                : Fail("複製失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }

        private ElementType? FindTypeByName(string? name, IEnumerable<ElementId>? within = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            IEnumerable<ElementType> pool = within != null ? within.Select(i => _doc.GetElement(i)).OfType<ElementType>()
                : new FilteredElementCollector(_doc).WhereElementIsElementType().Cast<ElementType>();
            var list = pool.ToList();
            if (long.TryParse(name, out var idNum)) { var byId = list.FirstOrDefault(t => t.Id.Value == idNum); if (byId != null) return byId; }
            return list.FirstOrDefault(t => $"{t.FamilyName}: {t.Name}" == name) ?? list.FirstOrDefault(t => t.Name == name)
                ?? list.FirstOrDefault(t => $"{t.FamilyName}: {t.Name}".IndexOf(name!, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // POST /elements/change-type  { elementIds, typeName:"族群: 類型", apply }
        public string ChangeType(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            string? tn = (string?)r["typeName"];
            if (string.IsNullOrWhiteSpace(tn)) return Fail("typeName 必填（「族群: 類型」或類型 id）。");
            var (els, bad) = Resolve(r["elementIds"]);
            var plan = new List<(Element e, ElementType t)>(); var rows = new List<object>();
            foreach (var e in els)
            {
                var valid = e.GetValidTypes();
                var t = FindTypeByName(tn, valid);
                if (t == null)
                {
                    var names = valid.Select(i => _doc.GetElement(i)).OfType<ElementType>().Select(x => $"{x.FamilyName}: {x.Name}").Take(40);
                    bad.Add(new { elementId = e.Id.Value, reason = "這個元件不能換成「" + tn + "」。可換的類型：" + string.Join("、", names) });
                    continue;
                }
                if (t.Id == e.GetTypeId()) { bad.Add(new { elementId = e.Id.Value, reason = "已經是這個類型" }); continue; }
                plan.Add((e, t));
                rows.Add(new { elementId = e.Id.Value, from = _doc.GetElement(e.GetTypeId()) is ElementType ft ? $"{ft.FamilyName}: {ft.Name}" : "?", to = $"{t.FamilyName}: {t.Name}" });
            }
            if (plan.Count == 0) return Fail("沒有可換類型的元件：" + Newtonsoft.Json.JsonConvert.SerializeObject(bad.Take(20)));
            if (!apply) return Ok(new { preflight = true, willChange = plan.Count, changes = rows.Take(200), skipped = bad });
            var res = RunTx(_doc, "MCP Change Type", () => { foreach (var (e, t) in plan) e.ChangeTypeId(t.Id); });
            return res.committed ? Ok(new { applied = true, changed = plan.Count, skipped = bad, warnings = res.warnings })
                : Fail("換類型失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }

        // POST /types/duplicate  { sourceType, newName, thicknessMm?, parameters?:{名稱:值}, apply }
        // thicknessMm：牆／樓板／屋頂／天花類型，調整最厚的那一層讓總厚度等於這個值。
        public string DuplicateType(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var src = FindTypeByName((string?)r["sourceType"]);
            if (src == null) return Fail("找不到 sourceType「" + r["sourceType"] + "」。可以用 revit_list_build_types 或 revit_list_elements 查類型名稱。");
            string newName = ((string?)r["newName"] ?? "").Trim();
            if (newName.Length == 0) return Fail("newName 必填。");
            bool exists = new FilteredElementCollector(_doc).OfClass(src.GetType()).Cast<ElementType>().Any(t => t.FamilyName == src.FamilyName && t.Name == newName);
            if (exists) return Fail($"「{src.FamilyName}: {newName}」已經存在。");

            double? thk = (double?)r["thicknessMm"];
            CompoundStructure? cs = (src as HostObjAttributes)?.GetCompoundStructure();
            object? thkPlan = null;
            if (thk.HasValue)
            {
                if (cs == null) return Fail("這個類型沒有複合結構（只有牆、樓板、屋頂、天花類型可以設定厚度）。");
                double total = cs.GetWidth() * FT;
                var layers = cs.GetLayers(); int idx = 0; double maxW = -1;
                for (int i = 0; i < layers.Count; i++) if (layers[i].Width > maxW) { maxW = layers[i].Width; idx = i; }
                double newLayer = maxW * FT + (thk.Value - total);
                if (newLayer <= 1) return Fail($"厚度 {thk} mm 太薄：其他層合計 {Math.Round(total - maxW * FT)} mm。");
                thkPlan = new { fromMm = Math.Round(total, 1), toMm = thk.Value, adjustedLayerIndex = idx, layerFromMm = Math.Round(maxW * FT, 1), layerToMm = Math.Round(newLayer, 1) };
            }
            var pars = (r["parameters"] as JObject) ?? new JObject();
            var parPlan = new List<object>(); var parBad = new List<object>();
            foreach (var kv in pars)
            {
                var p = src.LookupParameter(kv.Key);
                if (p == null) parBad.Add(new { parameter = kv.Key, reason = "找不到參數" });
                else if (p.IsReadOnly) parBad.Add(new { parameter = kv.Key, reason = "唯讀參數" });
                else parPlan.Add(new { parameter = kv.Key, current = p.AsValueString() ?? p.AsString(), newValue = kv.Value?.ToString() });
            }
            var plan = new { source = $"{src.FamilyName}: {src.Name}", newType = $"{src.FamilyName}: {newName}", thickness = thkPlan, parameters = parPlan, parameterErrors = parBad };
            if (!apply) return Ok(new { preflight = true, plan });
            if (parBad.Count > 0) return Fail("有參數無法設定，整批不執行：" + Newtonsoft.Json.JsonConvert.SerializeObject(parBad));

            long newId = 0;
            var res = RunTx(_doc, "MCP Duplicate Type", () =>
            {
                var nt = src.Duplicate(newName);
                if (thk.HasValue && nt is HostObjAttributes ho)
                {
                    var c2 = ho.GetCompoundStructure(); var ls = c2.GetLayers(); int idx = 0; double maxW = -1;
                    for (int i = 0; i < ls.Count; i++) if (ls[i].Width > maxW) { maxW = ls[i].Width; idx = i; }
                    c2.SetLayerWidth(idx, maxW + (thk.Value / FT - c2.GetWidth()));
                    ho.SetCompoundStructure(c2);
                }
                foreach (var kv in pars)
                {
                    var err = ParamSet.Set(nt.LookupParameter(kv.Key)!, kv.Value!);
                    if (err != null) throw new InvalidOperationException($"參數「{kv.Key}」：{err}");
                }
                newId = nt.Id.Value;
            });
            return res.committed ? Ok(new { applied = true, typeId = newId, plan, warnings = res.warnings })
                : Fail("複製類型失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }

        // POST /geometry/join  { pairs?:[[a,b]], elementIds?:[...], action?:"join"|"unjoin"|"switch", apply }
        // elementIds：自動找出外框相交、且還沒接合的兩兩組合（只做 join）。
        public string Join(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            string action = ((string?)r["action"] ?? "join").ToLowerInvariant();
            var pairs = new List<(Element a, Element b)>(); var bad = new List<object>();
            foreach (var p in (r["pairs"] as JArray) ?? new JArray())
            {
                if (p is not JArray pa || pa.Count != 2) continue;
                var a = Get(_doc, (long)pa[0]); var b = Get(_doc, (long)pa[1]);
                if (a == null || b == null) bad.Add(new { pair = pa, reason = "找不到元件" }); else pairs.Add((a, b));
            }
            if (r["elementIds"] is JArray && action == "join")
            {
                var els = Ids(r["elementIds"]).Select(i => Get(_doc, i)).Where(e => e != null).Cast<Element>().ToList();
                for (int i = 0; i < els.Count; i++)
                {
                    var bb = els[i].get_BoundingBox(null); if (bb == null) continue;
                    var f = new BoundingBoxIntersectsFilter(new Outline(bb.Min, bb.Max));
                    for (int j = i + 1; j < els.Count; j++)
                        if (!(els[i] is Wall && els[j] is Wall) && f.PassesFilter(els[j]) && !JoinGeometryUtils.AreElementsJoined(_doc, els[i], els[j])) pairs.Add((els[i], els[j]));
                }
            }
            if (pairs.Count == 0) return Fail("沒有可處理的元件組合。" + Newtonsoft.Json.JsonConvert.SerializeObject(bad));
            var rows = pairs.Take(300).Select(p => new { a = p.a.Id.Value, b = p.b.Id.Value, joined = JoinGeometryUtils.AreElementsJoined(_doc, p.a, p.b) }).ToList();
            if (!apply) return Ok(new { preflight = true, action, pairs = pairs.Count, detail = rows, skipped = bad });

            var done = 0; var failed = new List<object>();
            var res = RunTx(_doc, "MCP Join Geometry", () =>
            {
                foreach (var (a, b) in pairs)
                {
                    try
                    {
                        bool joined = JoinGeometryUtils.AreElementsJoined(_doc, a, b);
                        if (action == "join" && !joined) JoinGeometryUtils.JoinGeometry(_doc, a, b);
                        else if (action == "unjoin" && joined) JoinGeometryUtils.UnjoinGeometry(_doc, a, b);
                        else if (action == "switch" && joined) JoinGeometryUtils.SwitchJoinOrder(_doc, a, b);
                        else { failed.Add(new { a = a.Id.Value, b = b.Id.Value, reason = joined ? "已接合" : "沒有接合" }); continue; }
                        done++;
                    }
                    catch (Exception ex) { failed.Add(new { a = a.Id.Value, b = b.Id.Value, reason = ex.Message.Contains("cannot be joined") ? "這兩個元件不能接合（牆與牆在端點會自動接合，不用這個工具）" : ex.Message.Split('\n')[0] }); }
                }
            });
            return res.committed ? Ok(new { applied = true, action, done, notDone = failed, warnings = res.warnings })
                : Fail("接合失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }
    }
}
