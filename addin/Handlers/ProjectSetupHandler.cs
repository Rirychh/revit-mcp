// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static RevitMcpAddin.Handlers.PortUtil;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 專案設定與輸出：專案資訊、專案單位、專案參數、階段、工作集、DWG／NWC 匯出、全面清除未使用項目。
    /// 寫入工具 apply=false 預檢；apply=true 單一交易，任何錯誤整批回滾。
    /// </summary>
    public class ProjectSetupHandler
    {
        private const double FT = 304.8;
        private readonly Document _doc;
        public ProjectSetupHandler(Document doc) => _doc = doc;

        private static string Fmt(Exception ex) => ex.Message.Split('\n')[0];

        // POST /project/info { set?:{參數名稱: 值}, apply }   不給 set＝只讀
        public string Info(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var pi = _doc.ProjectInformation;
            var current = new SortedDictionary<string, string?>();
            foreach (Parameter p in pi.Parameters) if (p.Definition != null && !current.ContainsKey(p.Definition.Name)) current[p.Definition.Name] = p.AsValueString() ?? p.AsString();
            var set = (r["set"] as JObject) ?? new JObject();
            if (set.Count == 0) return Ok(new { projectInformation = current });
            var bad = new List<object>(); var plan = new List<object>();
            foreach (var kv in set)
            {
                var p = pi.LookupParameter(kv.Key);
                if (p == null) bad.Add(new { parameter = kv.Key, reason = "找不到參數" });
                else if (p.IsReadOnly) bad.Add(new { parameter = kv.Key, reason = "唯讀參數" });
                else plan.Add(new { parameter = kv.Key, current = current.TryGetValue(kv.Key, out var c) ? c : null, newValue = kv.Value?.ToString() });
            }
            if (!apply) return Ok(new { preflight = true, changes = plan, errors = bad });
            if (bad.Count > 0) return Fail("有參數無法設定，整批不執行：" + Newtonsoft.Json.JsonConvert.SerializeObject(bad));
            var res = RunTx(_doc, "MCP Project Information", () =>
            {
                foreach (var kv in set) { var err = ParamSet.Set(pi.LookupParameter(kv.Key)!, kv.Value!); if (err != null) throw new InvalidOperationException($"「{kv.Key}」：{err}"); }
            });
            return res.committed ? Ok(new { applied = true, changes = plan }) : Fail("設定專案資訊失敗，已回滾：" + string.Join("；", res.errors));
        }

        // POST /project/units { length?:"mm"|"cm"|"m", lengthDecimals?, area?:"m2"|"cm2", areaDecimals?, volume?:"m3", angleDecimals?, apply }
        public string Units(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var u = _doc.GetUnits();
            object Cur(ForgeTypeId spec) { var fo = u.GetFormatOptions(spec); return new { unit = fo.UseDefault ? "（預設）" : LabelUtils.GetLabelForUnit(fo.GetUnitTypeId()), accuracy = fo.UseDefault ? (double?)null : fo.Accuracy }; }
            var current = new { length = Cur(SpecTypeId.Length), area = Cur(SpecTypeId.Area), volume = Cur(SpecTypeId.Volume), angle = Cur(SpecTypeId.Angle) };
            var lenMap = new Dictionary<string, ForgeTypeId> { ["mm"] = UnitTypeId.Millimeters, ["cm"] = UnitTypeId.Centimeters, ["m"] = UnitTypeId.Meters };
            var areaMap = new Dictionary<string, ForgeTypeId> { ["m2"] = UnitTypeId.SquareMeters, ["cm2"] = UnitTypeId.SquareCentimeters, ["mm2"] = UnitTypeId.SquareMillimeters };
            var volMap = new Dictionary<string, ForgeTypeId> { ["m3"] = UnitTypeId.CubicMeters, ["cm3"] = UnitTypeId.CubicCentimeters };
            string? len = (string?)r["length"], area = (string?)r["area"], vol = (string?)r["volume"];
            int? ld = (int?)r["lengthDecimals"], ad = (int?)r["areaDecimals"], vd = (int?)r["volumeDecimals"], angd = (int?)r["angleDecimals"];
            if (len != null && !lenMap.ContainsKey(len)) return Fail("length 要是 mm／cm／m。");
            if (area != null && !areaMap.ContainsKey(area)) return Fail("area 要是 m2／cm2／mm2。");
            if (vol != null && !volMap.ContainsKey(vol)) return Fail("volume 要是 m3／cm3。");
            bool any = len != null || area != null || vol != null || ld.HasValue || ad.HasValue || vd.HasValue || angd.HasValue;
            if (!any) return Ok(new { units = current });
            if (!apply) return Ok(new { preflight = true, current, willSet = new { length = len, lengthDecimals = ld, area, areaDecimals = ad, volume = vol, volumeDecimals = vd, angleDecimals = angd } });
            var res = RunTx(_doc, "MCP Project Units", () =>
            {
                void Set(ForgeTypeId spec, ForgeTypeId? unit, int? dec)
                {
                    if (unit == null && dec == null) return;
                    var old = u.GetFormatOptions(spec);
                    var fo = new FormatOptions(unit ?? (old.UseDefault ? UnitTypeId.Millimeters : old.GetUnitTypeId()));
                    if (dec.HasValue) fo.Accuracy = Math.Pow(10, -Math.Max(0, Math.Min(8, dec.Value)));
                    u.SetFormatOptions(spec, fo);
                }
                Set(SpecTypeId.Length, len != null ? lenMap[len] : null, ld);
                Set(SpecTypeId.Area, area != null ? areaMap[area] : null, ad);
                Set(SpecTypeId.Volume, vol != null ? volMap[vol] : null, vd);
                if (angd.HasValue) Set(SpecTypeId.Angle, UnitTypeId.Degrees, angd);
                _doc.SetUnits(u);
            });
            return res.committed ? Ok(new { applied = true, before = current }) : Fail("設定單位失敗，已回滾：" + string.Join("；", res.errors));
        }

        // POST /project/parameters { name, dataType:"text"|"number"|"integer"|"length"|"area"|"yesNo", categories, binding:"instance"|"type", apply }
        public string AddParameter(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            string name = ((string?)r["name"] ?? "").Trim(); if (name.Length == 0) return Fail("name 必填。");
            var dtMap = new Dictionary<string, ForgeTypeId> { ["text"] = SpecTypeId.String.Text, ["number"] = SpecTypeId.Number, ["integer"] = SpecTypeId.Int.Integer,
                ["length"] = SpecTypeId.Length, ["area"] = SpecTypeId.Area, ["yesNo"] = SpecTypeId.Boolean.YesNo };
            string dt = (string?)r["dataType"] ?? "text"; if (!dtMap.ContainsKey(dt)) return Fail("dataType 要是 " + string.Join("／", dtMap.Keys) + "。");
            var (cats, unknown) = Categories(_doc, Strings(r["categories"]));
            if (cats.Count == 0) return Fail("categories 必填。" + (unknown.Count > 0 ? "無法辨識：" + string.Join("、", unknown) : ""));
            bool isType = ((string?)r["binding"] ?? "instance") == "type";
            var it = _doc.ParameterBindings.ForwardIterator(); bool dup = false;
            while (it.MoveNext()) if (it.Key?.Name == name) { dup = true; break; }
            if (dup) return Fail("專案裡已經有參數「" + name + "」。");
            var plan = new { name, dataType = dt, binding = isType ? "type" : "instance", categories = cats.Select(c => c.ToString()), note = "以暫存共用參數檔建立後綁定到專案（等同共用參數），不會改動你原本的共用參數檔設定。" };
            if (!apply) return Ok(new { preflight = true, plan });

            var app = _doc.Application; string? oldFile = app.SharedParametersFilename;
            string tmp = Path.Combine(Path.GetTempPath(), "mcp_shared_params_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(tmp, "");
            string? guid = null;
            try
            {
                app.SharedParametersFilename = tmp;
                var file = app.OpenSharedParameterFile();
                var grp = file.Groups.Create("MCP");
                var def = (ExternalDefinition)grp.Definitions.Create(new ExternalDefinitionCreationOptions(name, dtMap[dt]));
                guid = def.GUID.ToString();
                var cs = new CategorySet(); foreach (var c in cats) { var cat = Autodesk.Revit.DB.Category.GetCategory(_doc, c); if (cat != null && cat.AllowsBoundParameters) cs.Insert(cat); }
                if (cs.IsEmpty) return Fail("這些品類都不能加參數。");
                Binding b = isType ? _doc.Application.Create.NewTypeBinding(cs) : _doc.Application.Create.NewInstanceBinding(cs);
                var res = RunTx(_doc, "MCP Add Parameter", () =>
                {
#if REVIT2024_OR_GREATER
                    if (!_doc.ParameterBindings.Insert(def, b, GroupTypeId.Data)) throw new InvalidOperationException("綁定失敗");
#else
                    if (!_doc.ParameterBindings.Insert(def, b, BuiltInParameterGroup.PG_DATA)) throw new InvalidOperationException("綁定失敗");
#endif
                });
                return res.committed ? Ok(new { applied = true, guid, plan }) : Fail("新增參數失敗，已回滾：" + string.Join("；", res.errors));
            }
            finally
            {
                try { app.SharedParametersFilename = oldFile ?? ""; } catch { }
                try { File.Delete(tmp); } catch { }
            }
        }

        // POST /project/phases { elementIds?, created?, demolished?("none" 清除), viewIds?, viewPhase?, phaseFilter?, apply }
        public string Phases(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var phases = _doc.Phases.Cast<Phase>().ToList();
            var filters = new FilteredElementCollector(_doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>().ToList();
            Phase? P(string? n) => n == null ? null : phases.FirstOrDefault(p => p.Name == n);
            string? created = (string?)r["created"], demolished = (string?)r["demolished"], vph = (string?)r["viewPhase"], pf = (string?)r["phaseFilter"];
            var listing = new { phases = phases.Select(p => new { id = p.Id.Value, name = p.Name }), phaseFilters = filters.Select(f => f.Name) };
            var els = Ids(r["elementIds"]).Select(i => Get(_doc, i)).Where(e => e != null).Cast<Element>().ToList();
            var views = Ids(r["viewIds"]).Select(i => Get(_doc, i)).OfType<View>().ToList();
            if (els.Count == 0 && views.Count == 0) return Ok(listing);
            if (created != null && P(created) == null) return Fail("找不到階段「" + created + "」。");
            if (demolished != null && demolished != "none" && P(demolished) == null) return Fail("找不到階段「" + demolished + "」。");
            if (vph != null && P(vph) == null) return Fail("找不到階段「" + vph + "」。");
            var pfe = pf == null ? null : filters.FirstOrDefault(f => f.Name == pf);
            if (pf != null && pfe == null) return Fail("找不到階段篩選「" + pf + "」。可用：" + string.Join("、", filters.Select(f => f.Name)));
            var plan = new { elements = els.Count, created, demolished, views = views.Count, viewPhase = vph, phaseFilter = pf };
            if (!apply) return Ok(new { preflight = true, plan, listing });
            var failed = new List<object>();
            var res = RunTx(_doc, "MCP Phases", () =>
            {
                foreach (var e in els)
                {
                    try
                    {
                        if (created != null) e.get_Parameter(BuiltInParameter.PHASE_CREATED)?.Set(P(created)!.Id);
                        if (demolished != null) e.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.Set(demolished == "none" ? ElementId.InvalidElementId : P(demolished)!.Id);
                    }
                    catch (Exception ex) { failed.Add(new { elementId = e.Id.Value, reason = Fmt(ex) }); }
                }
                foreach (var v in views)
                {
                    try
                    {
                        if (vph != null) v.get_Parameter(BuiltInParameter.VIEW_PHASE)?.Set(P(vph)!.Id);
                        if (pfe != null) v.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.Set(pfe.Id);
                    }
                    catch (Exception ex) { failed.Add(new { viewId = v.Id.Value, reason = Fmt(ex) }); }
                }
            });
            return res.committed ? Ok(new { applied = true, plan, notApplied = failed }) : Fail("設定階段失敗，已回滾：" + string.Join("；", res.errors));
        }

        // POST /project/worksets { action:"list"|"create"|"move", name?, elementIds?, worksetName?, apply }
        public string Worksets(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            if (!_doc.IsWorkshared) return Fail("這份文件沒有啟用工作共用。");
            var all = new FilteredWorksetCollector(_doc).OfKind(WorksetKind.UserWorkset).ToWorksets().ToList();
            var table = _doc.GetWorksetTable();
            string action = (string?)r["action"] ?? "list";
            object L() => all.Select(w => new { id = w.Id.IntegerValue, name = w.Name, owner = w.Owner, isOpen = w.IsOpen, isEditable = w.IsEditable, isDefault = w.IsDefaultWorkset });
            if (action == "list") return Ok(new { active = table.GetWorkset(table.GetActiveWorksetId())?.Name, worksets = L() });
            if (action == "create")
            {
                string name = ((string?)r["name"] ?? "").Trim(); if (name.Length == 0) return Fail("name 必填。");
                if (!WorksetTable.IsWorksetNameUnique(_doc, name)) return Fail("已經有工作集「" + name + "」。");
                if (!apply) return Ok(new { preflight = true, create = name });
                int id = 0; var res = RunTx(_doc, "MCP Create Workset", () => id = Workset.Create(_doc, name).Id.IntegerValue);
                return res.committed ? Ok(new { applied = true, worksetId = id, name, note = "記得與中央同步。" }) : Fail("建立工作集失敗：" + string.Join("；", res.errors));
            }
            if (action == "move")
            {
                var target = all.FirstOrDefault(w => w.Name == (string?)r["worksetName"]);
                if (target == null) return Fail("找不到工作集「" + r["worksetName"] + "」。可用：" + string.Join("、", all.Select(w => w.Name)));
                var els = Ids(r["elementIds"]).Select(i => Get(_doc, i)).Where(e => e != null).Cast<Element>().ToList();
                var bad = new List<object>(); var ok = new List<Element>();
                foreach (var e in els)
                {
                    var p = e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                    if (p == null || p.IsReadOnly) bad.Add(new { elementId = e.Id.Value, reason = "這個元件不能換工作集" });
                    else if (WorksharingUtils.GetCheckoutStatus(_doc, e.Id) == CheckoutStatus.OwnedByOtherUser) bad.Add(new { elementId = e.Id.Value, reason = "被其他人借用中" });
                    else ok.Add(e);
                }
                if (!apply) return Ok(new { preflight = true, to = target.Name, willMove = ok.Count, skipped = bad });
                var res = RunTx(_doc, "MCP Move To Workset", () => { foreach (var e in ok) e.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM).Set(target.Id.IntegerValue); });
                return res.committed ? Ok(new { applied = true, to = target.Name, moved = ok.Count, skipped = bad, note = "記得與中央同步。" }) : Fail("移動工作集失敗，已回滾：" + string.Join("；", res.errors));
            }
            return Fail("action 要是 list／create／move。");
        }

        private List<View> ResolveViews(JObject r, List<object> bad)
        {
            var o = new List<View>();
            foreach (var id in Ids(r["viewIds"])) { if (Get(_doc, id) is View v && !v.IsTemplate) o.Add(v); else bad.Add(new { viewId = id, reason = "不是視圖" }); }
            var nums = Strings(r["sheetNumbers"]);
            if (nums.Count > 0)
            {
                var sheets = new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
                foreach (var n in nums) { var s = sheets.FirstOrDefault(x => x.SheetNumber == n); if (s != null) o.Add(s); else bad.Add(new { sheetNumber = n, reason = "找不到圖紙" }); }
            }
            return o;
        }

        // POST /export/dwg { viewIds?, sheetNumbers?, folder, setupName?, mergedViews?:false, apply }
        public string ExportDwg(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var bad = new List<object>(); var views = ResolveViews(r, bad);
            if (views.Count == 0) return Fail("要給 viewIds 或 sheetNumbers。" + Newtonsoft.Json.JsonConvert.SerializeObject(bad));
            string folder = (string?)r["folder"] ?? ""; if (folder.Length == 0) return Fail("folder 必填（匯出資料夾）。");
            var setups = BaseExportOptions.GetPredefinedSetupNames(_doc).ToList();
            string? setup = (string?)r["setupName"];
            if (setup != null && !setups.Contains(setup)) return Fail("找不到匯出設定「" + setup + "」。可用：" + string.Join("、", setups));
            var plan = new { folder, setup = setup ?? "（預設）", views = views.Select(v => v is ViewSheet s ? s.SheetNumber + " " + s.Name : v.Name), availableSetups = setups, skipped = bad };
            if (!apply) return Ok(new { preflight = true, plan });
            Directory.CreateDirectory(folder);
            var opt = setup != null ? DWGExportOptions.GetPredefinedOptions(_doc, setup) : new DWGExportOptions();
            opt.MergedViews = (bool?)r["mergedViews"] ?? false;
            var before = new HashSet<string>(Directory.GetFiles(folder));
            bool ok = _doc.Export(folder, "", views.Select(v => v.Id).ToList(), opt);
            var files = Directory.GetFiles(folder).Where(f => !before.Contains(f)).Select(Path.GetFileName).ToList();
            return ok ? Ok(new { exported = true, folder, files }) : Fail("Revit 回報匯出失敗。");
        }

        // POST /export/nwc { folder, fileName?, viewId?(3D 視圖；省略＝整個模型), apply }
        public string ExportNwc(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            if (!OptionalFunctionalityUtils.IsNavisworksExporterAvailable()) return Fail("這台電腦沒有安裝 Navisworks NWC 匯出器（Navisworks Exporters for Revit）。");
            string folder = (string?)r["folder"] ?? ""; if (folder.Length == 0) return Fail("folder 必填。");
            string name = (string?)r["fileName"] ?? Path.GetFileNameWithoutExtension(_doc.PathName ?? _doc.Title);
            var v = Get(_doc, (long?)r["viewId"] ?? 0) as View3D;
            var plan = new { folder, fileName = name + ".nwc", scope = v != null ? "視圖：" + v.Name : "整個模型" };
            if (!apply) return Ok(new { preflight = true, plan });
            Directory.CreateDirectory(folder);
            var opt = new NavisworksExportOptions();
            if (v != null) { opt.ExportScope = NavisworksExportScope.View; opt.ViewId = v.Id; } else opt.ExportScope = NavisworksExportScope.Model;
            _doc.Export(folder, name, opt);
            return Ok(new { exported = true, file = Path.Combine(folder, name + ".nwc"), exists = File.Exists(Path.Combine(folder, name + ".nwc")) });
        }

        // POST /purge/all { dryRun?:true, passes?:3 }   Revit 2024 起才有 GetUnusedElements
        public string PurgeAll(string body)
        {
            var r = Body(body); bool dry = (bool?)r["dryRun"] ?? true;
#if REVIT2024_OR_GREATER
            int passes = Math.Max(1, Math.Min(5, (int?)r["passes"] ?? 3));
            var first = _doc.GetUnusedElements(new HashSet<ElementId>());
            var summary = first.Select(id => _doc.GetElement(id)).Where(e => e != null)
                .GroupBy(e => e!.Category?.Name ?? e!.GetType().Name).OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count());
            if (dry) return Ok(new { dryRun = true, unused = first.Count, byCategory = summary, note = "這是第一輪；刪掉之後可能會多出新的未使用項目（例如族群刪了，材質才變成未使用）。dryRun:false 會重複清除 passes 輪。" });
            int total = 0; var perPass = new List<int>();
            var res = RunTx(_doc, "MCP Purge All", () =>
            {
                for (int i = 0; i < passes; i++)
                {
                    var ids = _doc.GetUnusedElements(new HashSet<ElementId>()).Where(id => _doc.GetElement(id) != null).ToList();
                    if (ids.Count == 0) break;
                    int n = 0;
                    foreach (var id in ids) { try { if (_doc.GetElement(id) != null) { _doc.Delete(id); n++; } } catch { } }
                    perPass.Add(n); total += n;
                }
            });
            return res.committed ? Ok(new { purged = total, perPass, firstPassByCategory = summary }) : Fail("清除失敗，已回滾：" + string.Join("；", res.errors));
#else
            return Fail("全面清除需要 Revit 2024 以上（API 從 2024 才提供）。Revit 2022／2023 請用 revit_purge_scan／revit_purge_delete 清除未使用的族群。");
#endif
        }
    }
}
