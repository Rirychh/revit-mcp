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
    /// 有主體的建模：門窗放進牆、天花板（點位或房間）、屋頂（跡線，可設坡度）、開口（牆、豎井、樓板）。
    /// 全部 apply=false 預檢；apply=true 單一交易，任何錯誤整批回滾。
    /// </summary>
    public class HostedBuildHandler
    {
        private const double FT = 304.8;
        private readonly Document _doc;
        public HostedBuildHandler(Document doc) => _doc = doc;

        private Level? LevelByName(string? name, double zFt) => FindLevel(_doc, name, zFt);

        private static List<XYZ> Pts(JToken? t, double zFt) => PolyMm(t, zFt, dropClosing: true);

        private static CurveLoop Loop(List<XYZ> pts)
        {
            var cl = new CurveLoop();
            for (int i = 0; i < pts.Count; i++) cl.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
            return cl;
        }

        private static CurveArray Arr(List<XYZ> pts)
        {
            var ca = new CurveArray();
            for (int i = 0; i < pts.Count; i++) ca.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
            return ca;
        }

        private FamilySymbol? Symbol(string? name, params BuiltInCategory[] cats)
        {
            var all = new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category != null && cats.Any(c => s.Category.Id.Value == (long)c)).ToList();
            if (string.IsNullOrWhiteSpace(name)) return null;
            return all.FirstOrDefault(s => $"{s.FamilyName}: {s.Name}" == name) ?? all.FirstOrDefault(s => s.Name == name)
                ?? (all.Where(s => $"{s.FamilyName}: {s.Name}".IndexOf(name!, StringComparison.OrdinalIgnoreCase) >= 0).ToList() is var m && m.Count == 1 ? m[0] : null);   // 模糊比對只在唯一時採用
        }

        private List<string> SymbolNames(params BuiltInCategory[] cats) =>
            new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category != null && cats.Any(c => s.Category.Id.Value == (long)c))
                .Select(s => $"{s.FamilyName}: {s.Name}").Take(60).ToList();

        // ── 門窗 ─────────────────────────────────────────────
        // POST /hosted/place { familyType, items:[{ pointMm:[x,y], wallId?, sillHeightMm?, flipFacing?, flipHand? }], level?, apply }
        public string PlaceHosted(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var cats = new[] { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows };
            var sym = Symbol((string?)r["familyType"], cats);
            if (sym == null) return Fail("找不到門窗類型「" + r["familyType"] + "」。可用：" + string.Join("、", SymbolNames(cats)));
            var walls = new FilteredElementCollector(_doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.Location is LocationCurve lc && lc.Curve is Line).ToList();

            var plan = new List<(XYZ p, Wall w, Level lv, double? sill, bool ff, bool fh)>(); var rows = new List<object>(); var bad = new List<object>();
            int i = -1;
            foreach (var it in (r["items"] as JArray) ?? new JArray())
            {
                i++;
                var pa = it["pointMm"] as JArray;
                if (pa == null || pa.Count < 2) { bad.Add(new { index = i, reason = "pointMm 要是 [x, y]（mm）" }); continue; }
                double x = (double)pa[0] / FT, y = (double)pa[1] / FT;
                Wall? host = null; double best = double.MaxValue; XYZ? proj = null;
                long? wid = (long?)it["wallId"];
                foreach (var w in wid.HasValue ? walls.Where(w => w.Id.Value == wid.Value) : walls)
                {
                    var ln = (Line)((LocationCurve)w.Location).Curve;
                    var a = ln.GetEndPoint(0); var b = ln.GetEndPoint(1);
                    var ab = new XYZ(b.X - a.X, b.Y - a.Y, 0); double len2 = ab.DotProduct(ab); if (len2 < 1e-9) continue;
                    double t = Math.Max(0, Math.Min(1, ((x - a.X) * ab.X + (y - a.Y) * ab.Y) / len2));
                    var q = new XYZ(a.X + ab.X * t, a.Y + ab.Y * t, a.Z);
                    double d = Math.Sqrt((q.X - x) * (q.X - x) + (q.Y - y) * (q.Y - y));
                    if (d < best) { best = d; host = w; proj = q; }
                }
                if (host == null || proj == null) { bad.Add(new { index = i, reason = wid.HasValue ? "wallId 不是直牆" : "找不到牆" }); continue; }
                if (!wid.HasValue && best * FT > host.Width * FT / 2 + 300) { bad.Add(new { index = i, reason = $"最近的牆（{host.Id.Value}）離這個點 {Math.Round(best * FT)} mm，太遠" }); continue; }
                var lv = _doc.GetElement(host.LevelId) as Level ?? LevelByName((string?)r["level"], proj.Z);
                if (lv == null) { bad.Add(new { index = i, reason = "找不到樓層" }); continue; }
                double? sill = (double?)it["sillHeightMm"];
                plan.Add((new XYZ(proj.X, proj.Y, lv.Elevation), host, lv, sill, (bool?)it["flipFacing"] ?? false, (bool?)it["flipHand"] ?? false));
                rows.Add(new { index = i, wallId = host.Id.Value, atMm = new[] { Math.Round(proj.X * FT), Math.Round(proj.Y * FT) }, offWallMm = Math.Round(best * FT), level = lv.Name, sillHeightMm = sill });
            }
            if (plan.Count == 0) return Fail("沒有可放的位置：" + Newtonsoft.Json.JsonConvert.SerializeObject(bad));
            var summary = new { familyType = $"{sym.FamilyName}: {sym.Name}", category = sym.Category.Name, willPlace = plan.Count };
            if (!apply) return Ok(new { preflight = true, summary, items = rows, skipped = bad });

            var ids = new List<long>();
            var res = RunTx(_doc, "MCP Place Doors/Windows", () =>
            {
                if (!sym.IsActive) { sym.Activate(); _doc.Regenerate(); }
                foreach (var (p, w, lv, sill, ff, fh) in plan)
                {
                    var fi = _doc.Create.NewFamilyInstance(p, sym, w, lv, StructuralType.NonStructural);
                    if (sill.HasValue) fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(sill.Value / FT);
                    if (ff && fi.CanFlipFacing) fi.flipFacing();
                    if (fh && fi.CanFlipHand) fi.flipHand();
                    ids.Add(fi.Id.Value);
                }
            });
            return res.committed ? Ok(new { applied = true, ids, summary, items = rows, skipped = bad, warnings = res.warnings })
                : Fail("放置門窗失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }

        // ── 天花板 ───────────────────────────────────────────
        // POST /ceilings { ceilingType?, level?, heightMm?, loops?:[[[x,y],...]], roomIds?:[...], apply }
        public string Ceilings(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var types = new FilteredElementCollector(_doc).OfClass(typeof(CeilingType)).Cast<CeilingType>().ToList();
            var ct = FindType(types, (string?)r["ceilingType"]);
            if (ct == null) return Fail("模型裡沒有天花板類型。");
            double? h = (double?)r["heightMm"];
            var items = new List<(List<CurveLoop> loops, Level lv, string src)>(); var bad = new List<object>();

            foreach (var lp in (r["loops"] as JArray) ?? new JArray())
            {
                var lv = LevelByName((string?)r["level"], 0);
                if (lv == null) { bad.Add(new { reason = "loops 需要 level" }); continue; }
                var pts = Pts(lp, lv.Elevation);
                if (pts.Count < 3) { bad.Add(new { reason = "迴圈至少 3 點" }); continue; }
                items.Add((new List<CurveLoop> { Loop(pts) }, lv, "loop"));
            }
            var opt = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
            foreach (var rid in Ids(r["roomIds"]))
            {
                if (Get(_doc, rid) is not Room room || room.Area <= 0) { bad.Add(new { roomId = rid, reason = "不是已放置的房間" }); continue; }
                var segs = room.GetBoundarySegments(opt);
                if (segs == null || segs.Count == 0) { bad.Add(new { roomId = rid, reason = "讀不到房間邊界" }); continue; }
                var cl = new CurveLoop();
                try { foreach (var s in segs[0]) cl.Append(s.GetCurve()); }
                catch (Exception ex) { bad.Add(new { roomId = rid, reason = "房間邊界不連續：" + ex.Message }); continue; }
                items.Add((new List<CurveLoop> { cl }, room.Level, "room " + room.Number + " " + room.Name));
            }
            if (items.Count == 0) return Fail("沒有可建立的天花板：" + Newtonsoft.Json.JsonConvert.SerializeObject(bad));
            var plan = new { ceilingType = $"{ct.FamilyName}: {ct.Name}", heightMm = h, willCreate = items.Count, items = items.Select(x => new { x.src, level = x.lv.Name }), skipped = bad, availableTypes = TypeNames(types).Take(40) };
            if (!apply) return Ok(new { preflight = true, plan });

            var ids = new List<long>();
            var res = RunTx(_doc, "MCP Create Ceilings", () =>
            {
                foreach (var (loops, lv, _) in items)
                {
                    var c = Ceiling.Create(_doc, loops, ct.Id, lv.Id);
                    if (h.HasValue) c.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.Set(h.Value / FT);
                    ids.Add(c.Id.Value);
                }
            });
            return res.committed ? Ok(new { applied = true, ids, plan, warnings = res.warnings })
                : Fail("建立天花板失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }

        // ── 屋頂 ─────────────────────────────────────────────
        // POST /roofs { pointsMm:[[x,y],...], level, roofType?, baseOffsetMm?, slopeDeg?:0, apply }
        public string Roofs(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var lv = LevelByName((string?)r["level"], 0);
            if (lv == null || string.IsNullOrWhiteSpace((string?)r["level"])) return Fail("level 必填（屋頂所在樓層）。");
            var pts = Pts(r["pointsMm"], lv.Elevation);
            if (pts.Count < 3) return Fail("pointsMm 至少 3 點（封閉的屋頂跡線）。");
            var types = new FilteredElementCollector(_doc).OfClass(typeof(RoofType)).Cast<RoofType>().ToList();
            var rt = FindType(types, (string?)r["roofType"]);
            if (rt == null) return Fail("模型裡沒有屋頂類型。");
            double slope = (double?)r["slopeDeg"] ?? 0; double? off = (double?)r["baseOffsetMm"];
            var plan = new { roofType = $"{rt.FamilyName}: {rt.Name}", level = lv.Name, edges = pts.Count, slopeDeg = slope, baseOffsetMm = off, availableTypes = TypeNames(types).Take(40) };
            if (!apply) return Ok(new { preflight = true, plan });

            long id = 0;
            var res = RunTx(_doc, "MCP Create Roof", () =>
            {
                ModelCurveArray map = new ModelCurveArray();   // Revit 要求傳入已建立的陣列，不能是 null
                var roof = _doc.Create.NewFootPrintRoof(Arr(pts), lv, rt, out map);
                if (slope > 0)
                    foreach (ModelCurve mc in map) { roof.set_DefinesSlope(mc, true); roof.set_SlopeAngle(mc, Math.Tan(slope * Math.PI / 180)); }
                if (off.HasValue) roof.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM)?.Set(off.Value / FT);
                id = roof.Id.Value;
            });
            return res.committed ? Ok(new { applied = true, roofId = id, plan, warnings = res.warnings })
                : Fail("建立屋頂失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }

        // ── 開口 ─────────────────────────────────────────────
        // POST /openings
        //  kind:"wall"  { wallId, alongMm（離牆起點）, widthMm, sillMm, heightMm }
        //  kind:"shaft" { pointsMm, bottomLevel, topLevel }
        //  kind:"floor" { hostId（樓板／屋頂／天花）, pointsMm }
        public string Openings(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            string kind = ((string?)r["kind"] ?? "").ToLowerInvariant();
            object plan; Func<Element> make;
            if (kind == "wall")
            {
                if (Get(_doc, (long?)r["wallId"] ?? 0) is not Wall w || w.Location is not LocationCurve lc || lc.Curve is not Line ln) return Fail("wallId 要是直牆。");
                double along = (double?)r["alongMm"] ?? -1, wd = (double?)r["widthMm"] ?? 0, sill = (double?)r["sillMm"] ?? 0, ht = (double?)r["heightMm"] ?? 0;
                double len = ln.Length * FT;
                if (along < 0 || wd <= 0 || ht <= 0) return Fail("牆開口需要 alongMm（≥0）、widthMm、heightMm。");
                if (along + wd > len + 1) return Fail($"開口超出牆長（牆長 {Math.Round(len)} mm）。");
                var lvw = _doc.GetElement(w.LevelId) as Level;
                double baseZ = (lvw?.Elevation ?? 0) + (w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0);
                var p0 = ln.GetEndPoint(0) + ln.Direction * (along / FT); var p1 = p0 + ln.Direction * (wd / FT);
                var a = new XYZ(p0.X, p0.Y, baseZ + sill / FT); var b = new XYZ(p1.X, p1.Y, baseZ + (sill + ht) / FT);
                plan = new { kind, wallId = w.Id.Value, wallLengthMm = Math.Round(len), fromMm = Mm(a), toMm = Mm(b) };
                make = () => _doc.Create.NewOpening(w, a, b);
            }
            else if (kind == "shaft")
            {
                var bl = Levels(_doc).FirstOrDefault(l => l.Name == (string?)r["bottomLevel"]);
                var tl = Levels(_doc).FirstOrDefault(l => l.Name == (string?)r["topLevel"]);
                if (bl == null || tl == null) return Fail("豎井需要 bottomLevel、topLevel（樓層名稱）。可用：" + string.Join("、", Levels(_doc).Select(l => l.Name)));
                if (tl.Elevation <= bl.Elevation) return Fail("topLevel 要比 bottomLevel 高。");
                var pts = Pts(r["pointsMm"], bl.Elevation);
                if (pts.Count < 3) return Fail("pointsMm 至少 3 點。");
                plan = new { kind, bottomLevel = bl.Name, topLevel = tl.Name, edges = pts.Count };
                make = () => _doc.Create.NewOpening(bl, tl, Arr(pts));
            }
            else if (kind == "floor")
            {
                var host = Get(_doc, (long?)r["hostId"] ?? 0);
                if (host == null || !(host is Floor || host is RoofBase || host is Ceiling)) return Fail("hostId 要是樓板、屋頂或天花板。");
                var bb = host.get_BoundingBox(null);
                var pts = Pts(r["pointsMm"], bb?.Max.Z ?? 0);
                if (pts.Count < 3) return Fail("pointsMm 至少 3 點。");
                plan = new { kind, hostId = host.Id.Value, host = host.Category?.Name, edges = pts.Count };
                make = () => _doc.Create.NewOpening(host, Arr(pts), true);
            }
            else return Fail("kind 要是 wall、shaft 或 floor。");

            if (!apply) return Ok(new { preflight = true, plan });
            long id = 0;
            var res = RunTx(_doc, "MCP Create Opening", () => { id = make().Id.Value; });
            return res.committed ? Ok(new { applied = true, openingId = id, plan, warnings = res.warnings })
                : Fail("建立開口失敗，已回滾、模型沒有變更：" + string.Join("；", res.errors));
        }
    }
}
