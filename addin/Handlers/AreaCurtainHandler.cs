// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using static RevitMcpAddin.Handlers.PortUtil;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// v1.10 B：面積平面（面積配置、邊界、面積）、帷幕牆格線／豎框／嵌板、牆頂／牆底附著、牆／樓板／屋頂／天花構造層。
    /// 全部 apply=false 預檢；apply=true 單一交易，任何錯誤整批回滾。
    /// </summary>
    public class AreaCurtainHandler
    {
        private const double FT = 304.8;
        private readonly Document _doc;
        public AreaCurtainHandler(Document doc) => _doc = doc;

        private Level? LevelExact(string? name) =>
            string.IsNullOrWhiteSpace(name) ? null : Levels(_doc).FirstOrDefault(l => l.Name == name)
                ?? (Levels(_doc).Where(l => l.Name.IndexOf(name!, StringComparison.OrdinalIgnoreCase) >= 0).ToList() is var m && m.Count == 1 ? m[0] : null);

        private static string Done(string what, (bool committed, List<string> errors, List<string> warnings) res, object ok) =>
            res.committed ? Ok(ok) : Fail(what + "失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));

        // ── 面積平面 ─────────────────────────────────────────
        // POST /areas
        //  action:"list"        → 面積配置、面積平面、各平面的面積
        //  action:"createPlan"  { scheme?, levels:[...] }
        //  action:"boundaries"  { viewId, loopsMm?:[[[x,y],...]], roomIds?:[...] }   面積邊界線（迴圈或沿房間邊界）
        //  action:"areas"       { viewId, items:[{ pointMm:[x,y], name?, number? }] }
        public string Areas(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            string act = ((string?)r["action"] ?? "list").ToLowerInvariant();
            var schemes = new FilteredElementCollector(_doc).OfClass(typeof(AreaScheme)).Cast<AreaScheme>().ToList();
            if (act == "list")
            {
                var plans = new FilteredElementCollector(_doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate && v.ViewType == ViewType.AreaPlan).ToList();
                var areas = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Areas).WhereElementIsNotElementType().Cast<Area>().ToList();
                return Ok(new
                {
                    schemes = schemes.Select(s => new { id = s.Id.Value, name = s.Name, isGross = s.IsGrossBuildingArea }),
                    plans = plans.Select(v => new
                    {
                        id = v.Id.Value, name = v.Name, level = v.GenLevel?.Name, scheme = v.AreaScheme?.Name,
                        areas = areas.Where(a => a.AreaScheme?.Id == v.AreaScheme?.Id && a.LevelId == v.GenLevel?.Id)
                            .Select(a => new { id = a.Id.Value, number = a.Number, name = AreaName(a), areaM2 = Math.Round(a.Area * M2PerFt2, 2), placed = a.Location != null }),
                    }),
                });
            }
            if (act == "createplan")
            {
                var sch = string.IsNullOrWhiteSpace((string?)r["scheme"]) ? schemes.FirstOrDefault()
                    : schemes.FirstOrDefault(s => s.Name == (string?)r["scheme"]) ?? schemes.FirstOrDefault(s => s.Name.IndexOf((string?)r["scheme"]!, StringComparison.OrdinalIgnoreCase) >= 0);
                if (sch == null) return Fail("找不到面積配置。可用：" + string.Join("、", schemes.Select(s => s.Name)));
                var lvs = Strings(r["levels"]).Select(n => (n, lv: LevelExact(n))).ToList();
                var bad = lvs.Where(x => x.lv == null).Select(x => x.n).ToList();
                if (lvs.Count == 0 || bad.Count > 0) return Fail("levels 要是樓層名稱清單。" + (bad.Count > 0 ? "找不到：" + string.Join("、", bad) : "") + " 可用：" + string.Join("、", Levels(_doc).Select(l => l.Name)));
                var plan = new { scheme = sch.Name, levels = lvs.Select(x => x.n) };
                if (!apply) return Ok(new { preflight = true, plan, note = "建立後是空的面積平面；再用 action boundaries 畫邊界（可沿房間邊界），用 action areas 放面積。" });
                var ids = new List<object>();
                var res = RunTx(_doc, "MCP Area Plans", () =>
                {
                    foreach (var (_, lv) in lvs) { var v = ViewPlan.CreateAreaPlan(_doc, sch.Id, lv!.Id); ids.Add(new { viewId = v.Id.Value, name = v.Name, level = lv.Name }); }
                });
                return Done("建立面積平面", res, new { applied = true, views = ids, plan, warnings = res.warnings });
            }
            var view = Get(_doc, (long?)r["viewId"] ?? 0) as ViewPlan;
            if (view == null || view.ViewType != ViewType.AreaPlan) return Fail("viewId 要是面積平面（先用 action list 或 createPlan）。");
            var level = view.GenLevel;
            if (act == "boundaries")
            {
                var curves = new List<Curve>(); var src = new List<object>(); var bad = new List<object>();
                foreach (var lp in (r["loopsMm"] as JArray) ?? new JArray())
                {
                    var pts = new List<XYZ>();
                    foreach (var p in (lp as JArray) ?? new JArray()) if (p is JArray a && a.Count >= 2) pts.Add(new XYZ((double)a[0] / FT, (double)a[1] / FT, level.Elevation));
                    if (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) < 1 / FT) pts.RemoveAt(pts.Count - 1);
                    if (pts.Count < 3) { bad.Add(new { reason = "迴圈至少 3 點" }); continue; }
                    for (int i = 0; i < pts.Count; i++) curves.Add(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
                    src.Add(new { loop = pts.Count });
                }
                var opt = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
                foreach (var rid in Ids(r["roomIds"]))
                {
                    if (Get(_doc, rid) is not Room room || room.Area <= 0) { bad.Add(new { roomId = rid, reason = "不是已放置的房間" }); continue; }
                    int n = 0;
                    foreach (var loop in room.GetBoundarySegments(opt) ?? new List<IList<BoundarySegment>>())
                        foreach (var s in loop)
                        {
                            var c = s.GetCurve(); var a = c.GetEndPoint(0); var b = c.GetEndPoint(1);
                            curves.Add(c is Line ? Line.CreateBound(new XYZ(a.X, a.Y, level.Elevation), new XYZ(b.X, b.Y, level.Elevation)) : c.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, level.Elevation - a.Z)))); n++;
                        }
                    src.Add(new { roomId = rid, room = room.Number + " " + room.Name, segments = n });
                }
                if (curves.Count == 0) return Fail("沒有可畫的邊界：給 loopsMm 或 roomIds。" + Newtonsoft.Json.JsonConvert.SerializeObject(bad));
                var plan = new { viewId = view.Id.Value, view = view.Name, lines = curves.Count, sources = src, skipped = bad };
                if (!apply) return Ok(new { preflight = true, plan });
                var ids = new List<long>();
                var res = RunTx(_doc, "MCP Area Boundaries", () =>
                {
                    var sp = SketchPlane.Create(_doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, level.Elevation)));
                    foreach (var c in curves) ids.Add(_doc.Create.NewAreaBoundaryLine(sp, c, view).Id.Value);
                });
                return Done("畫面積邊界", res, new { applied = true, ids, plan, warnings = res.warnings });
            }
            if (act == "areas")
            {
                var items = new List<(UV uv, string? name, string? number)>();
                foreach (var it in (r["items"] as JArray) ?? new JArray())
                {
                    var pa = it["pointMm"] as JArray; if (pa == null || pa.Count < 2) return Fail("items[].pointMm 要是 [x, y]（mm）。");
                    items.Add((new UV((double)pa[0] / FT, (double)pa[1] / FT), (string?)it["name"], (string?)it["number"]));
                }
                if (items.Count == 0) return Fail("items 必填：[{ pointMm:[x,y], name?, number? }]，點要落在面積邊界圍起來的範圍內。");
                var plan = new { viewId = view.Id.Value, view = view.Name, willPlace = items.Count };
                if (!apply) return Ok(new { preflight = true, plan });
                var made = new List<Area>();
                var res = RunTx(_doc, "MCP Place Areas", () =>
                {
                    foreach (var (uv, name, number) in items)
                    {
                        var a = _doc.Create.NewArea(view, uv);
                        if (!string.IsNullOrWhiteSpace(name)) a.Name = name;
                        if (!string.IsNullOrWhiteSpace(number)) a.Number = number;
                        made.Add(a);
                    }
                });
                if (!res.committed) return Done("放置面積", res, new { });
                return Ok(new
                {
                    applied = true, plan, warnings = res.warnings,
                    areas = made.Select(a => new { id = a.Id.Value, number = a.Number, name = AreaName(a), areaM2 = Math.Round(a.Area * M2PerFt2, 2), enclosed = a.Area > 0 }),
                });
            }
            return Fail("action 要是 list、createPlan、boundaries 或 areas。");
        }

        private static string AreaName(Area a) => a.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";

        private List<MullionType> MullionTypes() =>
            new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_CurtainWallMullions).WhereElementIsElementType().OfType<MullionType>().ToList();

        // ── 帷幕牆 ───────────────────────────────────────────
        // POST /curtain
        //  action:"info"     { wallId }  格線位置、嵌板、豎框
        //  action:"grid"     { wallId, verticalMm?:[離牆起點距離], horizontalMm?:[離牆底高度] }
        //  action:"mullions" { wallId, mullionType, gridLineIds?（省略＝全部格線）}
        //  action:"panels"   { wallId, panelType, panelIds?（省略＝全部嵌板）}
        public string Curtain(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            string act = ((string?)r["action"] ?? "info").ToLowerInvariant();
            if (Get(_doc, (long?)r["wallId"] ?? 0) is not Wall w || w.CurtainGrid == null) return Fail("wallId 要是帷幕牆。");
            var cg = w.CurtainGrid;
            var lc = w.Location as LocationCurve; var crv = lc?.Curve;
            if (crv == null) return Fail("讀不到牆的定位線。");
            var p0 = crv.GetEndPoint(0); var lv = _doc.GetElement(w.LevelId) as Level;
            double baseZ = (lv?.Elevation ?? 0) + (w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0);
            double height = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0;
            double len = crv.Length;

            object Line_(CurtainGridLine g)
            {
                var fc = g.FullCurve; var a = fc.GetEndPoint(0); var b = fc.GetEndPoint(1);
                bool vert = Math.Abs(a.Z - b.Z) > Math.Abs(new XYZ(a.X - b.X, a.Y - b.Y, 0).GetLength());
                double pos;
                if (vert) { var pr = crv.Project(new XYZ(a.X, a.Y, p0.Z)); pos = pr == null ? 0 : crv.ComputeNormalizedParameter(pr.Parameter) * len * FT; }
                else pos = (a.Z - baseZ) * FT;
                return new { id = g.Id.Value, orientation = vert ? "vertical" : "horizontal", positionMm = Math.Round(pos) };
            }
            if (act == "info")
            {
                var lines = cg.GetUGridLineIds().Concat(cg.GetVGridLineIds()).Select(id => _doc.GetElement(id) as CurtainGridLine).Where(g => g != null).Select(g => Line_(g!)).ToList();
                var panels = cg.GetPanelIds().Select(id => _doc.GetElement(id)).Where(e => e != null)
                    .GroupBy(e => $"{FamilyName(_doc, e!)}: {TypeName(_doc, e!)}").Select(g => new { type = g.Key, count = g.Count(), ids = g.Select(e => e!.Id.Value).Take(200) });
                var mull = cg.GetMullionIds().Select(id => _doc.GetElement(id)).Where(e => e != null)
                    .GroupBy(e => TypeName(_doc, e!)).Select(g => new { type = g.Key, count = g.Count() });
                return Ok(new
                {
                    wallId = w.Id.Value, wallType = w.WallType.Name, lengthMm = Math.Round(len * FT), heightMm = Math.Round(height * FT), gridLines = lines, panels, mullions = mull,
                    availableMullionTypes = MullionTypes().Select(t => $"{t.FamilyName}: {t.Name}").Take(40),
                    availablePanelTypes = PanelTypes().Select(t => $"{t.FamilyName}: {t.Name}").Take(40),
                });
            }
            if (act == "grid")
            {
                var vs = ((r["verticalMm"] as JArray) ?? new JArray()).Select(x => (double)x).ToList();
                var hs = ((r["horizontalMm"] as JArray) ?? new JArray()).Select(x => (double)x).ToList();
                if (vs.Count + hs.Count == 0) return Fail("給 verticalMm（離牆起點的距離）或 horizontalMm（離牆底的高度）。");
                var badV = vs.Where(v => v <= 0 || v >= len * FT).ToList(); var badH = hs.Where(h => h <= 0 || h >= height * FT).ToList();
                if (badV.Count + badH.Count > 0) return Fail($"位置超出牆面：牆長 {Math.Round(len * FT)} mm、牆高 {Math.Round(height * FT)} mm。超出：" + string.Join("、", badV.Concat(badH)));
                var plan = new { wallId = w.Id.Value, vertical = vs, horizontal = hs, lengthMm = Math.Round(len * FT), heightMm = Math.Round(height * FT) };
                if (!apply) return Ok(new { preflight = true, plan });
                var made = new List<object>();
                var res = RunTx(_doc, "MCP Curtain Grid", () =>
                {
                    double midZ = baseZ + height / 2;
                    foreach (var v in vs)
                    {
                        var q = crv.Evaluate(v / FT / len, true);
                        made.Add(Line_(AddGrid(cg, new XYZ(q.X, q.Y, midZ), true)));
                    }
                    var mid = crv.Evaluate(0.5, true);
                    foreach (var h in hs) made.Add(Line_(AddGrid(cg, new XYZ(mid.X, mid.Y, baseZ + h / FT), false)));
                });
                return Done("加帷幕格線", res, new { applied = true, gridLines = made, plan, warnings = res.warnings });
            }
            if (act == "mullions")
            {
                var mts = MullionTypes();
                var mt = FindType(mts, (string?)r["mullionType"]);
                if (mt == null) return Fail("找不到豎框類型。可用：" + string.Join("、", TypeNames(mts)));
                var want = Ids(r["gridLineIds"]);
                var lines = cg.GetUGridLineIds().Concat(cg.GetVGridLineIds()).Where(id => want.Count == 0 || want.Contains(id.Value))
                    .Select(id => _doc.GetElement(id) as CurtainGridLine).Where(g => g != null).ToList();
                if (lines.Count == 0) return Fail("這面帷幕牆沒有格線（先用 action grid）。");
                var plan = new { wallId = w.Id.Value, mullionType = $"{mt.FamilyName}: {mt.Name}", gridLines = lines.Count, note = "邊框豎框跟著牆類型設定，這裡只加在格線上。" };
                if (!apply) return Ok(new { preflight = true, plan });
                int n = 0;
                var res = RunTx(_doc, "MCP Mullions", () =>
                {
                    foreach (var g in lines)
                        foreach (Curve seg in g!.AllSegmentCurves) { var set = g.AddMullions(seg, mt, true); n += set?.Size ?? 0; }
                });
                return Done("加豎框", res, new { applied = true, mullionsAdded = n, plan, warnings = res.warnings });
            }
            if (act == "panels")
            {
                var pts = PanelTypes();
                var pt = FindType(pts, (string?)r["panelType"]);
                if (pt == null) return Fail("找不到嵌板類型。可用：" + string.Join("、", TypeNames(pts)));
                var want = Ids(r["panelIds"]);
                var panels = cg.GetPanelIds().Where(id => want.Count == 0 || want.Contains(id.Value)).Select(id => _doc.GetElement(id)).Where(e => e != null).ToList();
                if (panels.Count == 0) return Fail("找不到嵌板（用 action info 看嵌板 id）。");
                var plan = new { wallId = w.Id.Value, panelType = $"{pt.FamilyName}: {pt.Name}", panels = panels.Count };
                if (!apply) return Ok(new { preflight = true, plan });
                var res = RunTx(_doc, "MCP Curtain Panels", () =>
                {
                    foreach (var e in panels) { if (e!.Pinned) e.Pinned = false; e.ChangeTypeId(pt.Id); }
                });
                return Done("換嵌板", res, new { applied = true, plan, warnings = res.warnings });
            }
            return Fail("action 要是 info、grid、mullions 或 panels。");
        }

        /// <summary>加一條格線；依實際方向判斷 U／V，方向不對就刪掉改用另一個旗標。</summary>
        private CurtainGridLine AddGrid(CurtainGrid cg, XYZ at, bool vertical)
        {
            foreach (var isU in new[] { !vertical, vertical })
            {
                var g = cg.AddGridLine(isU, at, false);
                var fc = g.FullCurve; var a = fc.GetEndPoint(0); var b = fc.GetEndPoint(1);
                bool isVert = Math.Abs(a.Z - b.Z) > new XYZ(a.X - b.X, a.Y - b.Y, 0).GetLength();
                if (isVert == vertical) return g;
                _doc.Delete(g.Id);
            }
            throw new InvalidOperationException("無法在這個位置加格線。");
        }

        private List<ElementType> PanelTypes() =>
            new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_CurtainWallPanels).WhereElementIsElementType().Cast<ElementType>().ToList();

        // ── 牆附著 ───────────────────────────────────────────
        // POST /walls/attach { wallIds, targetId（屋頂／樓板／天花）, location?:"top"|"base", detach?:false, apply }
        public string AttachWalls(string body)
        {
#if REVIT2025_OR_GREATER
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var target = Get(_doc, (long?)r["targetId"] ?? 0);
            if (target == null) return Fail("targetId 必填（屋頂、樓板、天花或參考平面）。");
            bool top = !string.Equals((string?)r["location"], "base", StringComparison.OrdinalIgnoreCase);
            bool detach = (bool?)r["detach"] ?? false;
            var loc = top ? AttachmentLocation.Top : AttachmentLocation.Base;
            var walls = new List<Wall>(); var bad = new List<object>();
            foreach (var id in Ids(r["wallIds"]))
            {
                if (Get(_doc, id) is not Wall w) { bad.Add(new { id, reason = "不是牆" }); continue; }
                if (!detach && !Wall.IsValidTargetAttachment(_doc, target.Id)) { bad.Add(new { id, reason = "這個目標不能附著" }); continue; }
                walls.Add(w);
            }
            if (walls.Count == 0) return Fail("沒有可處理的牆：" + Newtonsoft.Json.JsonConvert.SerializeObject(bad));
            var plan = new { action = detach ? "detach" : "attach", location = top ? "top" : "base", target = new { id = target.Id.Value, category = target.Category?.Name }, walls = walls.Count, skipped = bad };
            if (!apply) return Ok(new { preflight = true, plan });
            var res = RunTx(_doc, "MCP Attach Walls", () =>
            {
                foreach (var w in walls) { if (detach) w.RemoveAttachment(target.Id); else w.AddAttachment(target.Id, loc); }
            });
            return Done(detach ? "取消附著" : "牆附著", res, new { applied = true, plan, warnings = res.warnings });
#else
            return Fail("牆附著需要 Revit 2025 以上（API 從 2025 才提供）。");
#endif
        }

        // ── 構造層 ───────────────────────────────────────────
        // POST /types/layers { typeName, category?:"walls"|"floors"|"roofs"|"ceilings", layers?:[{ function, material?, thicknessMm, core? }], apply }
        //  不給 layers＝讀目前構造層
        public string Layers(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            string cat = ((string?)r["category"] ?? "").ToLowerInvariant();
            Type cls = cat switch { "floors" => typeof(FloorType), "roofs" => typeof(RoofType), "ceilings" => typeof(CeilingType), "walls" => typeof(WallType), _ => typeof(HostObjAttributes) };
            var all = new FilteredElementCollector(_doc).OfClass(cls).Cast<HostObjAttributes>().Where(t => t.GetCompoundStructure() != null).ToList();
            var ht = FindType(all, (string?)r["typeName"]);
            if (ht == null || string.IsNullOrWhiteSpace((string?)r["typeName"])) return Fail("找不到類型（只限有構造層的牆、樓板、屋頂、天花類型）。可用：" + string.Join("、", TypeNames(all).Take(60)));
            var cs = ht.GetCompoundStructure();
            object Read(CompoundStructure c)
            {
                int ext = c.GetNumberOfShellLayers(ShellLayerType.Exterior), inn = c.GetNumberOfShellLayers(ShellLayerType.Interior);
                var ls = c.GetLayers();
                return new
                {
                    totalMm = Math.Round(c.GetWidth() * FT, 1),
                    layers = ls.Select((l, i) => new
                    {
                        index = i, function = l.Function.ToString(), material = l.MaterialId == ElementId.InvalidElementId ? "（依品類）" : _doc.GetElement(l.MaterialId)?.Name,
                        thicknessMm = Math.Round(l.Width * FT, 1), core = i >= ext && i < ls.Count - inn,
                    }),
                };
            }
            var specs = r["layers"] as JArray;
            if (specs == null || specs.Count == 0) return Ok(new { type = $"{ht.FamilyName}: {ht.Name}", current = Read(cs), note = "第 0 層＝外側（牆）或頂面（樓板／屋頂）。" });

            var mats = new FilteredElementCollector(_doc).OfClass(typeof(Material)).Cast<Material>().ToList();
            var layers = new List<CompoundStructureLayer>(); var coreFlags = new List<bool>(); var bad = new List<string>();
            int idx = 0;
            foreach (var s in specs)
            {
                string fn = ((string?)s["function"] ?? "structure").ToLowerInvariant();
                MaterialFunctionAssignment f = fn switch
                {
                    "structure" => MaterialFunctionAssignment.Structure, "substrate" => MaterialFunctionAssignment.Substrate,
                    "insulation" => MaterialFunctionAssignment.Insulation, "finish1" => MaterialFunctionAssignment.Finish1,
                    "finish2" => MaterialFunctionAssignment.Finish2, "membrane" => MaterialFunctionAssignment.Membrane,
                    _ => MaterialFunctionAssignment.None,
                };
                if (f == MaterialFunctionAssignment.None) bad.Add($"第 {idx} 層 function「{fn}」不認得（structure／substrate／insulation／finish1／finish2／membrane）");
                double th = (double?)s["thicknessMm"] ?? 0;
                if (f != MaterialFunctionAssignment.Membrane && th <= 0) bad.Add($"第 {idx} 層 thicknessMm 要大於 0");
                var mid = ElementId.InvalidElementId; var mn = (string?)s["material"];
                if (!string.IsNullOrWhiteSpace(mn))
                {
                    var m = mats.FirstOrDefault(x => x.Name == mn) ?? (mats.Where(x => x.Name.IndexOf(mn!, StringComparison.OrdinalIgnoreCase) >= 0).ToList() is var mm && mm.Count == 1 ? mm[0] : null);
                    if (m == null) bad.Add($"第 {idx} 層找不到材料「{mn}」（可用 revit_materials 先建立）"); else mid = m.Id;
                }
                layers.Add(new CompoundStructureLayer(f == MaterialFunctionAssignment.Membrane ? 0 : th / FT, f, mid));
                coreFlags.Add((bool?)s["core"] ?? (f == MaterialFunctionAssignment.Structure));
                idx++;
            }
            int first = coreFlags.IndexOf(true), last = coreFlags.LastIndexOf(true);
            if (first < 0) bad.Add("至少要有一層 core（或 function structure）");
            else if (coreFlags.Skip(first).Take(last - first + 1).Any(x => !x)) bad.Add("core 層要連續");
            if (bad.Count > 0) return Fail(string.Join("；", bad));
            var plan = new { type = $"{ht.FamilyName}: {ht.Name}", totalMm = Math.Round(layers.Sum(l => l.Width) * FT, 1), layers = layers.Count, coreFrom = first, coreTo = last, current = Read(cs), affectsAllInstances = true };
            if (!apply) return Ok(new { preflight = true, plan, note = "會改這個類型，所有用到它的元件都會跟著變；只想改部分元件請先用 revit_duplicate_type 複製類型。" });
            var res = RunTx(_doc, "MCP Type Layers", () =>
            {
                var c = ht.GetCompoundStructure();
                c.SetLayers(layers);
                c.SetNumberOfShellLayers(ShellLayerType.Exterior, first);
                c.SetNumberOfShellLayers(ShellLayerType.Interior, layers.Count - 1 - last);
                int si = layers.FindIndex(l => l.Function == MaterialFunctionAssignment.Structure);
                if (si >= 0) c.StructuralMaterialIndex = si;
                if (ht is WallType && !c.IsValid(_doc, out IDictionary<int, CompoundStructureError> errs, out IDictionary<int, int> _))
                    throw new InvalidOperationException("構造層不合法：" + string.Join("、", errs.Select(e => $"第 {e.Key} 層 {e.Value}")));
                ht.SetCompoundStructure(c);
            });
            return Done("設定構造層", res, new { applied = true, type = plan.type, now = Read(ht.GetCompoundStructure()), warnings = res.warnings });
        }
    }
}
