// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using static RevitMcpAddin.Handlers.PortUtil;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 讀明細表內容、批次設定參數（全有或全無）。
    /// （功能構想參考 MLabs Revit MCP 公開的工具清單，程式獨立實作）
    /// </summary>
    public class ScheduleParamHandler
    {
        private readonly Document _doc;
        public ScheduleParamHandler(Document doc) => _doc = doc;

        // GET /schedules/list?nameContains=
        public string List(NameValueCollection qs)
        {
            string? q = qs["nameContains"];
            var list = new FilteredElementCollector(_doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule)
                .Where(s => string.IsNullOrWhiteSpace(q) || s.Name.IndexOf(q!, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(s => s.Name)
                .Select(s =>
                {
                    string? cat = null; try { cat = Autodesk.Revit.DB.Category.GetCategory(_doc, s.Definition.CategoryId)?.Name; } catch { }
                    return new { scheduleId = s.Id.Value, name = s.Name, category = cat, fields = s.Definition.GetFieldCount() };
                }).ToList();
            return Ok(new { count = list.Count, schedules = list });
        }

        // GET /schedules/data?scheduleId=|name=&offset=0&maxRows=300
        public string Data(NameValueCollection qs)
        {
            ViewSchedule? vs = null;
            if (long.TryParse(qs["scheduleId"], out var id)) vs = Get(_doc, id) as ViewSchedule;
            else if (!string.IsNullOrWhiteSpace(qs["name"]))
                vs = new FilteredElementCollector(_doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                    .FirstOrDefault(s => s.Name == qs["name"]) ?? new FilteredElementCollector(_doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                    .FirstOrDefault(s => s.Name.IndexOf(qs["name"]!, StringComparison.OrdinalIgnoreCase) >= 0);
            if (vs == null) return Fail("找不到明細表（scheduleId 或 name）。");
            int offset = int.TryParse(qs["offset"], out var o) ? Math.Max(0, o) : 0;
            int max = int.TryParse(qs["maxRows"], out var m) ? Math.Min(2000, Math.Max(1, m)) : 300;

            var table = vs.GetTableData();
            List<List<string>> Read(SectionType st, int from, int count)
            {
                var sec = table.GetSectionData(st); var rows = new List<List<string>>();
                if (sec == null) return rows;
                int nr = sec.NumberOfRows, nc = sec.NumberOfColumns;
                for (int i = from; i < Math.Min(nr, from + count); i++)
                {
                    var row = new List<string>();
                    for (int j = 0; j < nc; j++) { string t; try { t = vs.GetCellText(st, i, j); } catch { t = ""; } row.Add(t); }
                    rows.Add(row);
                }
                return rows;
            }
            var body = table.GetSectionData(SectionType.Body);
            int total = body?.NumberOfRows ?? 0;
            var headers = vs.Definition.GetFieldOrder().Select(fid => vs.Definition.GetField(fid)).Where(f => !f.IsHidden).Select(f => f.GetName()).ToList();
            return Ok(new
            {
                scheduleId = vs.Id.Value, name = vs.Name, fields = headers,
                header = Read(SectionType.Header, 0, 20),
                bodyRows = total, offset, returned = Math.Max(0, Math.Min(max, total - offset)),
                note = "body 前一兩列通常是欄位標題；分組小計、空白列照明細表原樣呈現。",
                rows = Read(SectionType.Body, offset, max),
            });
        }

        private static string Spec(Parameter p)
        {
            try
            {
                var t = p.Definition.GetDataType();
                if (t == SpecTypeId.Length) return "length";
                if (t == SpecTypeId.Area) return "area";
                if (t == SpecTypeId.Volume) return "volume";
                if (t == SpecTypeId.Angle) return "angle";
            }
            catch { }
            return "";
        }

        // POST /parameters/batch
        // { updates:[{elementId, parameter, value}], allowTypeParameters?:false, apply }
        // 數值：長度 mm、面積 m²、體積 m³、角度 度；字串值交給 Revit 依專案單位解析（SetValueString）。
        public string Batch(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            bool allowType = (bool?)r["allowTypeParameters"] ?? false;
            var ups = (r["updates"] as JArray) ?? new JArray();
            if (ups.Count == 0) return Fail("updates 是空的。");
            if (ups.Count > 5000) return Fail("一次最多 5000 筆。");

            var plan = new List<(Parameter p, JToken v, string spec)>();
            var rows = new List<object>(); var errors = new List<object>();
            int i = -1;
            foreach (var u in ups)
            {
                i++;
                long id = (long?)u["elementId"] ?? 0; string name = (string?)u["parameter"] ?? ""; var val = u["value"];
                var el = Get(_doc, id);
                if (el == null) { errors.Add(new { index = i, elementId = id, reason = "找不到元件" }); continue; }
                var p = el.LookupParameter(name); bool isType = el is ElementType;
                if (p == null && el.GetTypeId() != ElementId.InvalidElementId && _doc.GetElement(el.GetTypeId()) is ElementType et)
                { p = et.LookupParameter(name); isType = p != null; }
                if (p == null) { errors.Add(new { index = i, elementId = id, parameter = name, reason = "找不到參數" }); continue; }
                if (isType && !allowType) { errors.Add(new { index = i, elementId = id, parameter = name, reason = "這是類型參數，會影響同類型的所有元件；確定要改請加 allowTypeParameters:true" }); continue; }
                if (p.IsReadOnly) { errors.Add(new { index = i, elementId = id, parameter = name, reason = "唯讀參數" }); continue; }
                if (val == null || val.Type == JTokenType.Null) { errors.Add(new { index = i, elementId = id, parameter = name, reason = "value 是空的" }); continue; }
                string spec = Spec(p);
                if (p.StorageType == StorageType.ElementId && val.Type != JTokenType.Integer) { errors.Add(new { index = i, elementId = id, parameter = name, reason = "這個參數要填元件 id（整數）" }); continue; }
                if (p.StorageType == StorageType.Integer && val.Type != JTokenType.Integer && val.Type != JTokenType.Boolean) { errors.Add(new { index = i, elementId = id, parameter = name, reason = "這個參數要填整數或 true/false" }); continue; }
                plan.Add((p, val, spec));
                rows.Add(new { index = i, elementId = id, parameter = name, isTypeParameter = isType, storage = p.StorageType.ToString(), unit = spec == "length" ? "mm" : spec == "area" ? "m²" : spec == "volume" ? "m³" : spec == "angle" ? "度" : null, current = p.AsValueString() ?? p.AsString(), newValue = val.ToString() });
            }
            var summary = new { total = ups.Count, ok = plan.Count, errorCount = errors.Count };
            if (!apply) return Ok(new { preflight = true, summary, errors, changes = rows.Take(300) });
            if (errors.Count > 0) return Fail("有 " + errors.Count + " 筆無法設定，整批不執行（全有或全無）：" + Newtonsoft.Json.JsonConvert.SerializeObject(errors.Take(30)));

            var res = RunTx(_doc, "MCP Batch Set Parameters", () =>
            {
                foreach (var (p, v, spec) in plan)
                {
                    bool ok;
                    switch (p.StorageType)
                    {
                        case StorageType.String: ok = p.Set(v.ToString()); break;
                        case StorageType.Integer: ok = p.Set(v.Type == JTokenType.Boolean ? ((bool)v ? 1 : 0) : (int)v); break;
                        case StorageType.ElementId: ok = p.Set(ElementIdCompat.NewId((long)v)); break;
                        case StorageType.Double:
                            if (v.Type == JTokenType.String) ok = p.SetValueString((string)v!);
                            else
                            {
                                double d = (double)v;
                                d = spec == "length" ? d / 304.8 : spec == "area" ? d / 0.09290304 : spec == "volume" ? d / 0.028316846592 : spec == "angle" ? d * Math.PI / 180 : d;
                                ok = p.Set(d);
                            }
                            break;
                        default: ok = false; break;
                    }
                    if (!ok) throw new InvalidOperationException($"參數「{p.Definition.Name}」設定失敗（元件 {p.Element?.Id.Value}，值 {v}）");
                }
            });
            return res.committed
                ? Ok(new { applied = true, summary, warnings = res.warnings })
                : Fail("批次設定失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }
    }
}
