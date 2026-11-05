// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using static RevitMcpAddin.Handlers.PortUtil;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 建模與出圖小工具：網格、結構梁、房間、房間分隔線、詳圖線、明細表。
    /// 全部 apply=false 預檢；apply=true 單一交易，任何錯誤整批回滾。
    /// </summary>
    public class ModelingHandler
    {
        private readonly Document _doc;
        public ModelingHandler(Document doc) => _doc = doc;

        private static string Done(string what, (bool committed, List<string> errors, List<string> warnings) res, object payload) =>
            res.committed ? Ok(payload) : Fail($"{what}失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));

        // POST /grids  { grids:[{ name?, start:[x,y], end:[x,y] }], apply }
        public string Grids(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var existing = new HashSet<string>(new FilteredElementCollector(_doc).OfClass(typeof(Grid)).Select(g => g.Name));
            var items = new List<(Line line, string? name)>(); var plan = new List<object>(); int i = 0;
            foreach (var g in (r["grids"] as JArray) ?? new JArray())
            {
                var a = Pt(g["start"]); var b = Pt(g["end"]); string? name = (string?)g["name"];
                if (a == null || b == null) { plan.Add(new { index = i++, ok = false, reason = "start/end 要是 [x,y]（mm）" }); continue; }
                a = new XYZ(a.X, a.Y, 0); b = new XYZ(b.X, b.Y, 0);
                if (a.DistanceTo(b) < 100 * FtPerMm) { plan.Add(new { index = i++, ok = false, reason = "長度小於 100 mm" }); continue; }
                if (name != null && existing.Contains(name)) { plan.Add(new { index = i++, ok = false, reason = $"網格名稱「{name}」已存在" }); continue; }
                if (name != null) existing.Add(name);
                items.Add((Line.CreateBound(a, b), name));
                plan.Add(new { index = i++, ok = true, name });
            }
            if (items.Count == 0) return Fail("沒有可建立的網格。" + Newtonsoft.Json.JsonConvert.SerializeObject(plan));
            if (!apply) return Ok(new { preflight = true, willCreate = items.Count, plan });

            var ids = new List<long>();
            var res = RunTx(_doc, "MCP Create Grids", () =>
            {
                foreach (var (line, name) in items)
                {
                    var g = Grid.Create(_doc, line);
                    if (!string.IsNullOrWhiteSpace(name)) g.Name = name;
                    ids.Add(g.Id.Value);
                }
            });
            return Done("建立網格", res, new { applied = true, created = ids.Count, ids, warnings = res.warnings });
        }

        // POST /structural-framing
        // { beams:[{ start:[x,y,z], end:[x,y,z] }], familyType?:"族群: 類型", level?, apply }
        public string Framing(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var syms = new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_StructuralFraming).Cast<FamilySymbol>().ToList();
            string? want = (string?)r["familyType"];
            var sym = FindType(syms, want);
            if (sym == null) return Fail("模型裡沒有結構框架族群，請先用 revit_load_families 載入。");
            if (!string.IsNullOrWhiteSpace(want) && !($"{sym.FamilyName}: {sym.Name}".Contains(want!) || sym.Name == want))
                return Fail($"找不到結構框架類型「{want}」。可用：" + string.Join("、", TypeNames(syms)));

            var beams = new List<(Line line, Level lv)>(); var plan = new List<object>(); int i = 0;
            foreach (var b in (r["beams"] as JArray) ?? new JArray())
            {
                var p0 = Pt(b["start"]); var p1 = Pt(b["end"]);
                if (p0 == null || p1 == null || p0.DistanceTo(p1) < 100 * FtPerMm)
                { plan.Add(new { index = i++, ok = false, reason = "start/end 要是 [x,y,z]（mm），長度至少 100 mm" }); continue; }
                var lv = FindLevel(_doc, (string?)b["level"] ?? (string?)r["level"], Math.Min(p0.Z, p1.Z));
                if (lv == null) { plan.Add(new { index = i++, ok = false, reason = "找不到樓層" }); continue; }
                beams.Add((Line.CreateBound(p0, p1), lv));
                plan.Add(new { index = i++, ok = true, level = lv.Name, lengthMm = ToMm(p0.DistanceTo(p1)) });
            }
            if (beams.Count == 0) return Fail("沒有可建立的梁。" + Newtonsoft.Json.JsonConvert.SerializeObject(plan));
            var head = new { familyType = $"{sym.FamilyName}: {sym.Name}", willCreate = beams.Count };
            if (!apply) return Ok(new { preflight = true, head.familyType, head.willCreate, plan });

            var ids = new List<long>();
            var res = RunTx(_doc, "MCP Create Structural Framing", () =>
            {
                if (!sym.IsActive) { sym.Activate(); _doc.Regenerate(); }
                foreach (var (line, lv) in beams)
                    ids.Add(_doc.Create.NewFamilyInstance(line, sym, lv, StructuralType.Beam).Id.Value);
            });
            return Done("建立結構梁", res, new { applied = true, created = ids.Count, ids, head.familyType, warnings = res.warnings });
        }

        // POST /rooms/create  { rooms:[{ level, point:[x,y], name?, number? }], apply }
        public string Rooms(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var items = new List<(Level lv, UV uv, string? name, string? number)>(); var plan = new List<object>(); int i = 0;
            foreach (var x in (r["rooms"] as JArray) ?? new JArray())
            {
                var p = Pt(x["point"]);
                var lv = FindLevel(_doc, (string?)x["level"] ?? (string?)r["level"], p?.Z ?? 0);
                if (p == null || lv == null) { plan.Add(new { index = i++, ok = false, reason = "point 要是 [x,y]（mm），level 要存在" }); continue; }
                items.Add((lv, new UV(p.X, p.Y), (string?)x["name"], (string?)x["number"]));
                plan.Add(new { index = i++, ok = true, level = lv.Name, name = (string?)x["name"], number = (string?)x["number"] });
            }
            if (items.Count == 0) return Fail("沒有可建立的房間。");
            if (!apply) return Ok(new { preflight = true, willCreate = items.Count, plan,
                note = "點不在封閉邊界內時會建出「未封閉」房間（面積 0）；建立後可用 revit_export_room_data 檢查。" });

            var created = new List<long>();
            var res = RunTx(_doc, "MCP Create Rooms", () =>
            {
                foreach (var (lv, uv, name, number) in items)
                {
                    var rm = _doc.Create.NewRoom(lv, uv);
                    if (!string.IsNullOrWhiteSpace(name)) rm.Name = name;
                    if (!string.IsNullOrWhiteSpace(number)) rm.Number = number;
                    created.Add(rm.Id.Value);
                }
            });
            if (res.committed)
            {
                // 交易提交後面積才算得出來
                var withArea = created.Select(id =>
                {
                    var rm = Get(_doc, id) as Room;
                    return (object)new { id, number = rm?.Number, level = rm?.Level?.Name, areaM2 = Math.Round((rm?.Area ?? 0) * M2PerFt2, 2), enclosed = (rm?.Area ?? 0) > 0 };
                }).ToList();
                return Ok(new { applied = true, created = withArea.Count, rooms = withArea, warnings = res.warnings });
            }
            return Done("建立房間", res, new { });
        }

        // POST /rooms/separation  { level?, viewId?, lines:[{ start:[x,y], end:[x,y] }], apply }
        public string RoomSeparation(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            View? view = null; Level? lv;
            long? vid = (long?)r["viewId"];
            if (vid.HasValue)
            {
                view = Get(_doc, vid.Value) as ViewPlan;
                if (view == null) return Fail("viewId 不是平面視圖。");
                lv = view.GenLevel;
            }
            else
            {
                lv = FindLevel(_doc, (string?)r["level"], 0);
                if (lv == null) return Fail("找不到樓層。");
                view = new FilteredElementCollector(_doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel?.Id == lv.Id);
                if (view == null) return Fail($"樓層「{lv.Name}」沒有平面視圖，請給 viewId。");
            }
            if (lv == null) return Fail("這個視圖沒有對應樓層。");

            var curves = new CurveArray(); int bad = 0;
            foreach (var l in (r["lines"] as JArray) ?? new JArray())
            {
                var a = Pt(l["start"], lv.Elevation); var b = Pt(l["end"], lv.Elevation);
                if (a == null || b == null) { bad++; continue; }
                a = new XYZ(a.X, a.Y, lv.Elevation); b = new XYZ(b.X, b.Y, lv.Elevation);
                if (a.DistanceTo(b) < 10 * FtPerMm) { bad++; continue; }
                curves.Append(Line.CreateBound(a, b));
            }
            if (curves.Size == 0) return Fail("lines 沒有可用的線段（[x,y] mm，長度至少 10 mm）。");
            var plan = new { level = lv.Name, view = view.Name, viewId = view.Id.Value, willCreate = curves.Size, skipped = bad };
            if (!apply) return Ok(new { preflight = true, plan });

            var ids = new List<long>();
            var res = RunTx(_doc, "MCP Room Separation Lines", () =>
            {
                var sp = SketchPlane.Create(_doc, lv.Id);
                foreach (ModelCurve mc in _doc.Create.NewRoomBoundaryLines(sp, curves, view)) ids.Add(mc.Id.Value);
            });
            return Done("建立房間分隔線", res, new { applied = true, created = ids.Count, ids, plan, warnings = res.warnings });
        }

        // POST /detail-lines  { viewId, lines:[{ start:[x,y], end:[x,y] }], lineStyle?, apply }
        public string DetailLines(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            long? vid = (long?)r["viewId"];
            if (!vid.HasValue) return Fail("需要 viewId。");
            var view = Get(_doc, vid.Value) as View;
            if (view == null || view.IsTemplate) return Fail("viewId 不是可繪製的視圖。");
            if (view.ViewType == ViewType.ThreeD) return Fail("3D 視圖不能放詳圖線。");

            // 視圖平面：平面類用 z=樓層高程（或 0），剖立面類把 [x,y] 解讀為視圖平面上的 (右, 上) 座標
            XYZ origin = view.Origin, right = view.RightDirection, up = view.UpDirection;
            bool planLike = view is ViewPlan || view.ViewType == ViewType.DraftingView || view.ViewType == ViewType.Legend;
            double zPlan = (view as ViewPlan)?.GenLevel?.Elevation ?? 0;
            XYZ Map(XYZ p) => planLike ? new XYZ(p.X, p.Y, view.ViewType == ViewType.DraftingView ? 0 : zPlan)
                                       : origin + right * p.X + up * p.Y;

            GraphicsStyle? style = null; string? styleName = (string?)r["lineStyle"];
            var lineCat = _doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            var styles = new List<string>();
            foreach (Category sub in lineCat.SubCategories)
            {
                styles.Add(sub.Name);
                if (styleName != null && sub.Name == styleName) style = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
            }
            if (styleName != null && style == null) return Fail($"找不到線型「{styleName}」。可用：" + string.Join("、", styles));

            var lines = new List<Line>(); int bad = 0;
            foreach (var l in (r["lines"] as JArray) ?? new JArray())
            {
                var a = Pt(l["start"]); var b = Pt(l["end"]);
                if (a == null || b == null) { bad++; continue; }
                var A = Map(a); var B = Map(b);
                if (A.DistanceTo(B) < 1 * FtPerMm) { bad++; continue; }
                lines.Add(Line.CreateBound(A, B));
            }
            if (lines.Count == 0) return Fail("lines 沒有可用的線段。");
            var plan = new { view = view.Name, viewType = view.ViewType.ToString(), lineStyle = styleName, willCreate = lines.Count, skipped = bad,
                coordinateNote = planLike ? "[x,y] = 專案平面座標（mm）" : "[x,y] = 視圖平面上的 (右, 上) 座標（mm），原點為視圖原點" };
            if (!apply) return Ok(new { preflight = true, plan });

            var ids = new List<long>();
            var res = RunTx(_doc, "MCP Detail Lines", () =>
            {
                foreach (var ln in lines)
                {
                    var dc = _doc.Create.NewDetailCurve(view, ln);
                    if (style != null) dc.LineStyle = style;
                    ids.Add(dc.Id.Value);
                }
            });
            return Done("建立詳圖線", res, new { applied = true, created = ids.Count, ids, plan, warnings = res.warnings });
        }

        // POST /schedules  { category, name, fields:["族群與類型","數量"], apply }
        public string Schedule(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var cat = Category(_doc, (string?)r["category"] ?? "");
            if (cat == null) return Fail("不認得 category。可用 OST_ 名稱、英文別名或品類顯示名。");
            string name = (string?)r["name"] ?? "";
            if (string.IsNullOrWhiteSpace(name)) return Fail("需要 name。");
            if (new FilteredElementCollector(_doc).OfClass(typeof(ViewSchedule)).Any(v => v.Name == name))
                return Fail($"明細表名稱「{name}」已存在。");
            var want = Strings(r["fields"]);

            // 預檢要知道有哪些欄位可用 → 開一個一定回滾的交易建暫時明細表來讀
            List<string> available = new List<string>();
            using (var tg = new Transaction(_doc, "MCP schedule probe"))
            {
                tg.Start();
                try
                {
                    var probe = ViewSchedule.CreateSchedule(_doc, new ElementId(cat.Value));
                    available = probe.Definition.GetSchedulableFields().Select(f => f.GetName(_doc)).ToList();
                }
                catch (Exception ex) { tg.RollBack(); return Fail("這個品類不能建明細表：" + ex.Message); }
                tg.RollBack();
            }
            var missing = want.Where(f => !available.Contains(f)).ToList();
            var plan = new { category = cat.Value.ToString(), name, fields = want.Where(available.Contains), missingFields = missing };
            if (!apply) return Ok(new { preflight = true, plan, availableFields = available });
            if (missing.Count > 0) return Fail("有欄位不存在，沒有建立：" + string.Join("、", missing));

            long id = 0;
            var res = RunTx(_doc, "MCP Create Schedule", () =>
            {
                var vs = ViewSchedule.CreateSchedule(_doc, new ElementId(cat.Value));
                vs.Name = name;
                var def = vs.Definition;
                var sf = def.GetSchedulableFields();
                foreach (var f in want)
                {
                    var hit = sf.FirstOrDefault(x => x.GetName(_doc) == f);
                    if (hit != null) def.AddField(hit);
                }
                id = vs.Id.Value;
            });
            return Done("建立明細表", res, new { applied = true, scheduleId = id, plan, warnings = res.warnings });
        }
    }
}
