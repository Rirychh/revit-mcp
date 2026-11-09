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
    /// v1.10 C：材料（列出／建立／指定）、群組（建立／放置／列出）、明細表排序篩選格式、族類型參數與公式。
    /// 全部 apply=false 預檢；apply=true 單一交易，任何錯誤整批回滾。
    /// </summary>
    public class MaterialGroupHandler
    {
        private const double FT = 304.8;
        private readonly Document _doc;
        public MaterialGroupHandler(Document doc) => _doc = doc;

        private static string Done(string what, (bool committed, List<string> errors, List<string> warnings) res, object ok) =>
            res.committed ? Ok(ok) : Fail(what + "失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));

        private List<Material> Mats() => new FilteredElementCollector(_doc).OfClass(typeof(Material)).Cast<Material>().OrderBy(m => m.Name).ToList();

        private static Material? FindMat(List<Material> mats, string? n) =>
            string.IsNullOrWhiteSpace(n) ? null : mats.FirstOrDefault(m => m.Name == n)
                ?? (mats.Where(m => m.Name.IndexOf(n!, StringComparison.OrdinalIgnoreCase) >= 0).ToList() is var x && x.Count == 1 ? x[0] : null);

        private static bool IsMaterialParam(Parameter p)
        {
            if (p.StorageType != StorageType.ElementId) return false;
            try { return p.Definition.GetDataType() == SpecTypeId.Reference.Material; } catch { return false; }
        }

        // ── 材料 ─────────────────────────────────────────────
        // POST /materials
        //  action:"list"   { filter? }
        //  action:"create" { name, basedOn?, colorRgb?:[r,g,b], transparency?:0–100, materialClass? }
        //  action:"assign" { material, elementIds?, typeNames?, parameter? }   設到元件或類型的材料參數
        public string Materials(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            string act = ((string?)r["action"] ?? "list").ToLowerInvariant();
            var mats = Mats();
            if (act == "list")
            {
                var f = (string?)r["filter"];
                return Ok(new
                {
                    count = mats.Count,
                    materials = mats.Where(m => string.IsNullOrWhiteSpace(f) || m.Name.IndexOf(f!, StringComparison.OrdinalIgnoreCase) >= 0).Take(300)
                        .Select(m => new { id = m.Id.Value, name = m.Name, materialClass = m.MaterialClass, colorRgb = m.Color.IsValid ? new[] { (int)m.Color.Red, m.Color.Green, m.Color.Blue } : null, transparency = m.Transparency }),
                });
            }
            if (act == "create")
            {
                string name = ((string?)r["name"] ?? "").Trim();
                if (name.Length == 0) return Fail("name 必填。");
                if (mats.Any(m => m.Name == name)) return Fail($"材料「{name}」已存在。");
                Material? baseMat = null;
                if (!string.IsNullOrWhiteSpace((string?)r["basedOn"])) { baseMat = FindMat(mats, (string?)r["basedOn"]); if (baseMat == null) return Fail("找不到 basedOn 材料。"); }
                var rgb = (r["colorRgb"] as JArray)?.Select(x => (int)x).ToArray();
                if (rgb != null && (rgb.Length != 3 || rgb.Any(v => v < 0 || v > 255))) return Fail("colorRgb 要是 [r,g,b]（0–255）。");
                int? tr = (int?)r["transparency"]; if (tr.HasValue && (tr < 0 || tr > 100)) return Fail("transparency 要是 0–100。");
                var plan = new { name, basedOn = baseMat?.Name, colorRgb = rgb, transparency = tr, materialClass = (string?)r["materialClass"] };
                if (!apply) return Ok(new { preflight = true, plan });
                long id = 0;
                var res = RunTx(_doc, "MCP Create Material", () =>
                {
                    var m = baseMat != null ? baseMat.Duplicate(name) : (Material)_doc.GetElement(Material.Create(_doc, name));
                    if (rgb != null) m.Color = new Color((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]);
                    if (tr.HasValue) m.Transparency = tr.Value;
                    if (!string.IsNullOrWhiteSpace((string?)r["materialClass"])) m.MaterialClass = (string?)r["materialClass"];
                    id = m.Id.Value;
                });
                return Done("建立材料", res, new { applied = true, materialId = id, plan, note = "彩現外觀可用 revit_set_material_appearance 設定。", warnings = res.warnings });
            }
            if (act == "assign")
            {
                var mat = FindMat(mats, (string?)r["material"]);
                if (mat == null) return Fail("找不到材料「" + r["material"] + "」。");
                string? pname = (string?)r["parameter"];
                var targets = new List<Element>(); var bad = new List<object>();
                foreach (var id in Ids(r["elementIds"])) { var e = Get(_doc, id); if (e == null) bad.Add(new { id, reason = "找不到" }); else targets.Add(e); }
                var types = new FilteredElementCollector(_doc).WhereElementIsElementType().Cast<ElementType>().ToList();
                foreach (var tn in Strings(r["typeNames"]))
                {
                    var t = types.Where(x => $"{x.FamilyName}: {x.Name}" == tn).ToList();
                    if (t.Count != 1) bad.Add(new { typeName = tn, reason = t.Count == 0 ? "找不到（用「族群: 類型」）" : "名稱重複" }); else targets.Add(t[0]);
                }
                var plan = new List<(Element el, Parameter p)>(); var rows = new List<object>();
                foreach (var el in targets)
                {
                    var cands = el.Parameters.Cast<Parameter>().Where(p => IsMaterialParam(p) && !p.IsReadOnly).ToList();
                    var hit = pname != null ? cands.Where(p => p.Definition.Name == pname).ToList() : cands;
                    if (hit.Count == 1) { plan.Add((el, hit[0])); rows.Add(new { id = el.Id.Value, name = el.Name, parameter = hit[0].Definition.Name, from = _doc.GetElement(hit[0].AsElementId())?.Name }); }
                    else bad.Add(new { id = el.Id.Value, name = el.Name, reason = hit.Count == 0 ? "沒有可寫的材料參數（牆、樓板等系統族請用 revit_set_type_layers）" : "有多個材料參數，請用 parameter 指定", materialParameters = cands.Select(p => p.Definition.Name) });
                }
                if (plan.Count == 0) return Fail("沒有可指定的目標：" + Newtonsoft.Json.JsonConvert.SerializeObject(bad));
                var sum = new { material = mat.Name, targets = rows, skipped = bad };
                if (!apply) return Ok(new { preflight = true, plan = sum });
                var res = RunTx(_doc, "MCP Assign Material", () => { foreach (var (_, p) in plan) p.Set(mat.Id); });
                return Done("指定材料", res, new { applied = true, plan = sum, warnings = res.warnings });
            }
            return Fail("action 要是 list、create 或 assign。");
        }

        // ── 群組 ─────────────────────────────────────────────
        // POST /groups
        //  action:"list"
        //  action:"create" { elementIds, name? }
        //  action:"place"  { groupType, pointsMm:[[x,y,z?],...] }
        public string Groups(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            string act = ((string?)r["action"] ?? "list").ToLowerInvariant();
            var gts = new FilteredElementCollector(_doc).OfClass(typeof(GroupType)).Cast<GroupType>().ToList();
            if (act == "list")
                return Ok(new
                {
                    groupTypes = gts.Select(g => new { id = g.Id.Value, name = g.Name, category = g.Category?.Name, instances = g.Groups.Size }),
                });
            if (act == "create")
            {
                var ids = Ids(r["elementIds"]);
                var els = ids.Select(id => Get(_doc, id)).ToList();
                var bad = ids.Where((id, i) => els[i] == null || InGroup(els[i]!)).ToList();
                if (ids.Count == 0) return Fail("elementIds 必填。");
                if (bad.Count > 0) return Fail("這些元件找不到或已在群組內：" + string.Join("、", bad));
                string? name = (string?)r["name"];
                if (!string.IsNullOrWhiteSpace(name) && gts.Any(g => g.Name == name)) return Fail($"群組類型「{name}」已存在。");
                var plan = new { elements = ids.Count, name, categories = els.GroupBy(e => e!.Category?.Name ?? "").Select(g => new { category = g.Key, count = g.Count() }) };
                if (!apply) return Ok(new { preflight = true, plan });
                long gid = 0, tid = 0; string tname = "";
                var res = RunTx(_doc, "MCP Create Group", () =>
                {
                    var g = _doc.Create.NewGroup(ids.Select(ElementIdCompat.NewId).ToList());
                    if (!string.IsNullOrWhiteSpace(name)) g.GroupType.Name = name;
                    gid = g.Id.Value; tid = g.GroupType.Id.Value; tname = g.GroupType.Name;
                });
                return Done("建立群組", res, new { applied = true, groupId = gid, groupTypeId = tid, groupType = tname, plan, warnings = res.warnings });
            }
            if (act == "place")
            {
                var gn = (string?)r["groupType"];
                var gt = gts.FirstOrDefault(g => g.Name == gn) ?? (gts.Where(g => gn != null && g.Name.IndexOf(gn, StringComparison.OrdinalIgnoreCase) >= 0).ToList() is var m && m.Count == 1 ? m[0] : null);
                if (gt == null) return Fail("找不到群組類型。可用：" + string.Join("、", gts.Select(g => g.Name)));
                var pts = ((r["pointsMm"] as JArray) ?? new JArray()).Select(t => Pt(t)).ToList();
                if (pts.Count == 0 || pts.Any(p => p == null)) return Fail("pointsMm 要是 [[x,y] 或 [x,y,z],...]（mm），群組原點放到這些位置。");
                var plan = new { groupType = gt.Name, willPlace = pts.Count };
                if (!apply) return Ok(new { preflight = true, plan });
                var made = new List<long>();
                var res = RunTx(_doc, "MCP Place Groups", () => { foreach (var p in pts) made.Add(_doc.Create.PlaceGroup(p!, gt).Id.Value); });
                return Done("放置群組", res, new { applied = true, ids = made, plan, warnings = res.warnings });
            }
            return Fail("action 要是 list、create 或 place。");
        }

        // ── 明細表設定 ───────────────────────────────────────
        // POST /schedules/settings { scheduleId, sort?:[{field, descending?, header?, footer?, blankLine?}], filters?:[{field, op, value?}],
        //                            itemize?, grandTotal?, hide?:[field], show?:[field], apply }
        public string ScheduleSettings(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            if (Get(_doc, (long?)r["scheduleId"] ?? 0) is not ViewSchedule vs || vs.IsTitleblockRevisionSchedule) return Fail("scheduleId 要是明細表（用 revit_list_schedules 查）。");
            var def = vs.Definition;
            var fields = def.GetFieldOrder().Select(id => def.GetField(id)).ToList();
            object Read() => new
            {
                scheduleId = vs.Id.Value, name = vs.Name, itemize = def.IsItemized, grandTotal = def.ShowGrandTotal,
                fields = def.GetFieldOrder().Select(id => def.GetField(id)).Select(f => new { name = f.GetName(), heading = f.ColumnHeading, hidden = f.IsHidden }),
                sort = def.GetSortGroupFields().Select(s => new { field = def.GetField(s.FieldId).GetName(), descending = s.SortOrder == ScheduleSortOrder.Descending, header = s.ShowHeader, footer = s.ShowFooter }),
                filters = def.GetFilters().Select(f => new { field = def.GetField(f.FieldId).GetName(), op = f.FilterType.ToString(), value = f.IsStringValue ? f.GetStringValue() : f.IsDoubleValue ? (object)FromInternal(def.GetField(f.FieldId), f.GetDoubleValue()) : f.IsIntegerValue ? f.GetIntegerValue() : null }),
            };
            bool any = r["sort"] != null || r["filters"] != null || r["itemize"] != null || r["grandTotal"] != null || r["hide"] != null || r["show"] != null;
            if (!any) return Ok(new { current = Read(), filterOps = Enum.GetNames(typeof(ScheduleFilterType)) });

            ScheduleField? F(string? n) => n == null ? null : fields.FirstOrDefault(f => f.GetName() == n) ?? fields.FirstOrDefault(f => f.ColumnHeading == n);
            var bad = new List<string>();
            var sorts = new List<ScheduleSortGroupField>();
            foreach (var s in (r["sort"] as JArray) ?? new JArray())
            {
                var f = F((string?)s["field"]); if (f == null) { bad.Add($"排序欄位「{s["field"]}」不在明細表裡"); continue; }
                sorts.Add(new ScheduleSortGroupField(f.FieldId, ((bool?)s["descending"] ?? false) ? ScheduleSortOrder.Descending : ScheduleSortOrder.Ascending)
                { ShowHeader = (bool?)s["header"] ?? false, ShowFooter = (bool?)s["footer"] ?? false, ShowBlankLine = (bool?)s["blankLine"] ?? false });
            }
            var filters = new List<ScheduleFilter>();
            foreach (var s in (r["filters"] as JArray) ?? new JArray())
            {
                var f = F((string?)s["field"]); if (f == null) { bad.Add($"篩選欄位「{s["field"]}」不在明細表裡"); continue; }
                if (!Enum.TryParse<ScheduleFilterType>((string?)s["op"] ?? "", true, out var op)) { bad.Add($"op「{s["op"]}」不認得"); continue; }
                var v = s["value"];
                if (v == null || v.Type == JTokenType.Null) filters.Add(new ScheduleFilter(f.FieldId, op));
                else if (v.Type == JTokenType.String) filters.Add(new ScheduleFilter(f.FieldId, op, (string)v!));
                else if (v.Type == JTokenType.Integer && f.GetSpecTypeId() == SpecTypeId.Int.Integer) filters.Add(new ScheduleFilter(f.FieldId, op, (int)v));
                else
                {
                    double d = (double)v; var spec = f.GetSpecTypeId();
                    if (spec == SpecTypeId.Length) d /= FT; else if (spec == SpecTypeId.Area) d /= M2PerFt2; else if (spec == SpecTypeId.Volume) d /= M3PerFt3;
                    filters.Add(new ScheduleFilter(f.FieldId, op, d));
                }
            }
            if (filters.Count > 8) bad.Add("明細表最多 8 個篩選條件");
            var hide = Strings(r["hide"]).Select(n => (n, f: F(n))).ToList(); var show = Strings(r["show"]).Select(n => (n, f: F(n))).ToList();
            bad.AddRange(hide.Concat(show).Where(x => x.f == null).Select(x => $"欄位「{x.n}」不在明細表裡"));
            if (bad.Count > 0) return Fail(string.Join("；", bad) + "。欄位：" + string.Join("、", fields.Select(f => f.GetName())));
            var plan = new { schedule = vs.Name, sort = r["sort"], filters = r["filters"], itemize = (bool?)r["itemize"], grandTotal = (bool?)r["grandTotal"], hide = hide.Select(x => x.n), show = show.Select(x => x.n), note = "給 sort 或 filters 會取代原本的排序或篩選。" };
            if (!apply) return Ok(new { preflight = true, plan, current = Read() });
            var res = RunTx(_doc, "MCP Schedule Settings", () =>
            {
                if (r["sort"] != null) { def.ClearSortGroupFields(); foreach (var s in sorts) def.AddSortGroupField(s); }
                if (r["filters"] != null) { def.ClearFilters(); foreach (var f in filters) def.AddFilter(f); }
                if (r["itemize"] != null) def.IsItemized = (bool)r["itemize"]!;
                if (r["grandTotal"] != null) def.ShowGrandTotal = (bool)r["grandTotal"]!;
                foreach (var (_, f) in hide) f!.IsHidden = true;
                foreach (var (_, f) in show) f!.IsHidden = false;
            });
            return Done("設定明細表", res, new { applied = true, now = Read(), warnings = res.warnings });
        }

        private static double FromInternal(ScheduleField f, double d)
        {
            var spec = f.GetSpecTypeId();
            if (spec == SpecTypeId.Length) return Math.Round(d * FT, 2);
            if (spec == SpecTypeId.Area) return Math.Round(d * M2PerFt2, 4);
            if (spec == SpecTypeId.Volume) return Math.Round(d * M3PerFt3, 4);
            return d;
        }

        // ── 族類型參數 ───────────────────────────────────────
        // POST /families/type-params { family, type?, set?:{參數: 值}, formulas?:{參數: 公式}, apply }
        //  不給 set／formulas＝列出類型與類型參數。set 寫在專案中的類型；formulas 會開族檔改公式再重新載入。
        public string FamilyTypeParams(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var fn = (string?)r["family"];
            var fams = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>().ToList();
            var fam = fams.FirstOrDefault(f => f.Name == fn) ?? (fams.Where(f => fn != null && f.Name.IndexOf(fn, StringComparison.OrdinalIgnoreCase) >= 0).ToList() is var m && m.Count == 1 ? m[0] : null);
            if (fam == null)
            {
                var cand = fams.Where(f => !string.IsNullOrWhiteSpace(fn) && f.Name.IndexOf(fn!.Length > 2 ? fn.Substring(0, 2) : fn, StringComparison.OrdinalIgnoreCase) >= 0).Select(f => f.Name).Take(40).ToList();
                return Fail("找不到可載入族「" + fn + "」（系統族請用 revit_duplicate_type／revit_modify_element）。" + (cand.Count > 0 ? "相近的族：" + string.Join("、", cand) : "族名稱可用 revit_list_elements 的 familyName 查。"));
            }
            string famName = fam.Name; var famId = fam.Id;
            var syms = fam.GetFamilySymbolIds().Select(id => (FamilySymbol)_doc.GetElement(id)).ToList();
            var tn = (string?)r["type"];
            var targets = string.IsNullOrWhiteSpace(tn) ? syms : syms.Where(s => s.Name == tn).ToList();
            if (targets.Count == 0) return Fail("找不到類型。可用：" + string.Join("、", syms.Select(s => s.Name)));
            var targetIds = targets.Select(s => s.Id).ToList();   // 族重新載入後舊物件會失效，之後一律用 id 重新取

            object Val(Parameter p)
            {
                switch (p.StorageType)
                {
                    case StorageType.Double:
                        var spec = p.Definition.GetDataType(); double d = p.AsDouble();
                        if (spec == SpecTypeId.Length) return Math.Round(d * FT, 2);
                        if (spec == SpecTypeId.Angle) return Math.Round(d * 180 / Math.PI, 3);
                        if (spec == SpecTypeId.Area) return Math.Round(d * M2PerFt2, 4);
                        return Math.Round(d, 6);
                    case StorageType.Integer: return p.AsInteger();
                    case StorageType.String: return p.AsString() ?? "";
                    case StorageType.ElementId: return _doc.GetElement(p.AsElementId())?.Name ?? "";
                }
                return "";
            }
            var set = r["set"] as JObject; var formulas = r["formulas"] as JObject;
            if ((set == null || !set.Properties().Any()) && (formulas == null || !formulas.Properties().Any()))
                return Ok(new
                {
                    family = fam.Name, category = fam.FamilyCategory?.Name,
                    types = targets.Select(s => new
                    {
                        id = s.Id.Value, name = s.Name,
                        parameters = s.Parameters.Cast<Parameter>().Where(p => p.Definition != null && p.HasValue).OrderBy(p => p.Definition.Name)
                            .Select(p => new { name = p.Definition.Name, value = Val(p), readOnly = p.IsReadOnly }).Take(120),
                    }),
                    note = "長度 mm、角度度、面積 m²。有公式的參數在專案裡是唯讀，要改用 formulas。",
                });

            var bad = new List<string>(); var writes = new List<(Parameter p, object v)>();
            foreach (var s in targets)
                foreach (var kv in set?.Properties() ?? Enumerable.Empty<JProperty>())
                {
                    var p = s.LookupParameter(kv.Name);
                    if (p == null) { bad.Add($"{s.Name}：沒有參數「{kv.Name}」"); continue; }
                    if (p.IsReadOnly) { bad.Add($"{s.Name}：「{kv.Name}」唯讀（可能由公式驅動）"); continue; }
                    try { writes.Add((p, Conv(p, kv.Value))); } catch (Exception ex) { bad.Add($"{s.Name}：「{kv.Name}」{ex.Message}"); }
                }
            var fl = formulas?.Properties().Select(x => (name: x.Name, f: (string?)x.Value ?? "")).ToList() ?? new List<(string, string)>();
            if (bad.Count > 0) return Fail(string.Join("；", bad));
            var plan = new { family = fam.Name, types = targets.Select(s => s.Name), set = set, formulas = formulas, editsFamilyFile = fl.Count > 0 };
            if (!apply) return Ok(new { preflight = true, plan, note = fl.Count > 0 ? "formulas 會開啟族檔、設定公式後重新載入；專案中其他參數值保留。" : null });

            var warns = new List<string>();
            if (fl.Count > 0)
            {
                if (fam.IsInPlace || !fam.IsEditable) return Fail("這個族不能編輯（內建族或不可編輯）。");
                Document? fdoc = null;
                try
                {
                    fdoc = _doc.EditFamily(fam);
                    var fm = fdoc.FamilyManager; var fbad = new List<string>();
                    var pre = new RollbackOnError();
                    using (var t = new Transaction(fdoc, "MCP Formulas"))
                    {
                        t.Start();
                        var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(pre); t.SetFailureHandlingOptions(fo);
                        foreach (var (name, f) in fl)
                        {
                            var fp = fm.get_Parameter(name);
                            if (fp == null) { fbad.Add($"族裡沒有參數「{name}」"); continue; }
                            try { fm.SetFormula(fp, string.IsNullOrWhiteSpace(f) ? null : f); } catch (Exception ex) { fbad.Add($"「{name}」公式無效：{ex.Message}"); }
                        }
                        if (fbad.Count > 0) { t.RollBack(); return Fail("設定公式失敗、模型沒有變更：" + string.Join("；", fbad)); }
                        t.Commit();
                        if (pre.HadError) return Fail("設定公式失敗、模型沒有變更：" + string.Join("；", pre.Errors));
                    }
                    fdoc.LoadFamily(_doc, new LoadOpts());
                }
                catch (Exception ex) { return Fail("編輯族檔失敗、模型沒有變更：" + ex.Message); }
                finally { try { fdoc?.Close(false); } catch { } }
                warns.Add("公式已寫入族檔並重新載入。");
            }
            if (writes.Count > 0)
            {
                // 族重新載入後參數物件可能失效，重新取
                var res = RunTx(_doc, "MCP Family Type Params", () =>
                {
                    foreach (var sid in targetIds)
                        foreach (var kv in set!.Properties())
                        {
                            var sym = (FamilySymbol)_doc.GetElement(sid); var p = sym.LookupParameter(kv.Name);
                            if (p == null || p.IsReadOnly) throw new InvalidOperationException($"{sym.Name}：「{kv.Name}」無法寫入");
                            var v = Conv(p, kv.Value);
                            switch (v) { case double d: p.Set(d); break; case int i: p.Set(i); break; case ElementId e: p.Set(e); break; default: p.Set((string)v); break; }
                        }
                });
                if (!res.committed) return Fail((fl.Count > 0 ? "公式已更新，但" : "") + "設定類型參數失敗、參數值沒有變更：" + string.Join("；", res.errors));
                warns.AddRange(res.warnings);
            }
            var now = targetIds.Select(id => (FamilySymbol)_doc.GetElement(id)).Select(s => new
            {
                name = s.Name,
                values = (set?.Properties().Select(x => x.Name) ?? Enumerable.Empty<string>()).Concat(fl.Select(x => x.name)).Distinct()
                    .Select(n => new { parameter = n, value = s.LookupParameter(n) is Parameter p ? Val(p) : null }),
            });
            return Ok(new { applied = true, family = famName, now, warnings = warns });
        }

        private object Conv(Parameter p, JToken v)
        {
            switch (p.StorageType)
            {
                case StorageType.Double:
                    double d = v.Type == JTokenType.String ? double.Parse((string)v!, System.Globalization.CultureInfo.InvariantCulture) : (double)v;
                    var spec = p.Definition.GetDataType();
                    if (spec == SpecTypeId.Length) return d / FT;
                    if (spec == SpecTypeId.Angle) return d * Math.PI / 180;
                    if (spec == SpecTypeId.Area) return d / M2PerFt2;
                    return d;
                case StorageType.Integer:
                    if (v.Type == JTokenType.Boolean) return (bool)v ? 1 : 0;
                    if (v.Type == JTokenType.String && ((string)v! == "是" || (string)v! == "否")) return (string)v! == "是" ? 1 : 0;
                    return (int)v;
                case StorageType.String: return (string?)v ?? "";
                case StorageType.ElementId:
                    var n = (string?)v ?? "";
                    var hit = new FilteredElementCollector(_doc).WhereElementIsElementType().Cast<Element>().Concat(Mats())
                        .Where(e => e.Name == n || (e is ElementType et && $"{et.FamilyName}: {et.Name}" == n)).ToList();
                    if (hit.Count != 1) throw new ArgumentException(hit.Count == 0 ? $"找不到「{n}」" : $"「{n}」名稱重複");
                    return hit[0].Id;
            }
            throw new ArgumentException("不支援的參數型別");
        }

        /// <summary>重新載入族時保留專案中既有的參數值（只帶入族檔的公式與定義）。</summary>
        private class LoadOpts : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = false; return true; }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            { source = FamilySource.Project; overwriteParameterValues = false; return true; }
        }
    }
}
