// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using static RevitMcpAddin.Handlers.PortUtil;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// DWG 幾何萃取（存成工作階段快取）與依快取產生 BIM 元件：柱、樓板。
    /// 座標換算全部在程式裡做，DWG 已是模型座標（ImportInstance 幾何），不需要再配準。
    /// （功能構想參考 MLabs Revit MCP 公開的工具清單，程式獨立實作）
    /// </summary>
    public class CadBimHandler
    {
        private const double FT = 304.8;
        private readonly Document _doc;
        public CadBimHandler(Document doc) => _doc = doc;

        private class Loop { public List<XYZ> Pts = new(); public string Layer = ""; }
        private class Circ { public XYZ C = XYZ.Zero; public double R; public string Layer = ""; }
        private class Cache
        {
            public string Id = ""; public string DocTitle = ""; public long ViewId; public long? LevelId;
            public DateTime At; public List<(XYZ a, XYZ b, string layer)> Lines = new();
            public List<Loop> Loops = new(); public List<Circ> Circles = new();
        }
        private static readonly Dictionary<string, Cache> _cache = new();

        private static string LayerOf(Document doc, GeometryObject o) =>
            o.GraphicsStyleId != ElementId.InvalidElementId && doc.GetElement(o.GraphicsStyleId) is GraphicsStyle gs
                ? gs.GraphicsStyleCategory?.Name ?? gs.Name ?? "" : "";

        private void Walk(GeometryElement ge, Cache c, List<string> layers, double[]? region, int depth)
        {
            if (depth > 8) return;
            foreach (var o in ge)
            {
                if (o is GeometryInstance gi) { var inner = gi.GetInstanceGeometry(); if (inner != null) Walk(inner, c, layers, region, depth + 1); continue; }
                string layer = LayerOf(_doc, o);
                if (layers.Count > 0 && !layers.Any(l => layer.IndexOf(l, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                if (o is Line ln) { if (In(region, ln.GetEndPoint(0), ln.GetEndPoint(1))) c.Lines.Add((ln.GetEndPoint(0), ln.GetEndPoint(1), layer)); }
                else if (o is PolyLine pl)
                {
                    var p = pl.GetCoordinates().ToList();
                    if (p.Count < 2 || !In(region, p.ToArray())) continue;
                    if (p.Count >= 4 && p[0].DistanceTo(p[p.Count - 1]) < 1 / FT) { p.RemoveAt(p.Count - 1); c.Loops.Add(new Loop { Pts = p, Layer = layer }); }
                    else for (int i = 0; i + 1 < p.Count; i++) c.Lines.Add((p[i], p[i + 1], layer));
                }
                else if (o is Arc arc && !arc.IsBound) { if (In(region, arc.Center)) c.Circles.Add(new Circ { C = arc.Center, R = arc.Radius, Layer = layer }); }
                else if (o is Ellipse el && !el.IsBound && Math.Abs(el.RadiusX - el.RadiusY) < 1 / FT) { if (In(region, el.Center)) c.Circles.Add(new Circ { C = el.Center, R = el.RadiusX, Layer = layer }); }
            }
        }

        private static bool In(double[]? reg, params XYZ[] pts) =>
            reg == null || pts.Any(p => p.X * FT >= reg[0] && p.X * FT <= reg[2] && p.Y * FT >= reg[1] && p.Y * FT <= reg[3]);

        // 把首尾相接的獨立線段串成封閉迴圈（DWG 常把矩形柱畫成四條線）
        private static List<Loop> ChainLines(List<(XYZ a, XYZ b, string layer)> lines, int maxLoops)
        {
            var loops = new List<Loop>();
            foreach (var g in lines.GroupBy(l => l.layer))
            {
                var segs = g.Select(l => (a: l.a, b: l.b)).ToList(); var used = new bool[segs.Count];
                string Key(XYZ p) => Math.Round(p.X * FT) + "," + Math.Round(p.Y * FT);
                var byPt = new Dictionary<string, List<int>>();
                for (int i = 0; i < segs.Count; i++)
                    foreach (var k in new[] { Key(segs[i].a), Key(segs[i].b) })
                    { if (!byPt.TryGetValue(k, out var l)) byPt[k] = l = new(); l.Add(i); }
                for (int s = 0; s < segs.Count && loops.Count < maxLoops; s++)
                {
                    if (used[s]) continue;
                    var pts = new List<XYZ> { segs[s].a }; var cur = segs[s].b; var taken = new List<int> { s }; bool closed = false;
                    for (int step = 0; step < 64; step++)
                    {
                        if (Key(cur) == Key(pts[0])) { closed = true; break; }
                        pts.Add(cur);
                        int next = byPt.TryGetValue(Key(cur), out var cand) ? cand.FirstOrDefault(i => !taken.Contains(i) && !used[i]) : 0;
                        if (cand == null || !cand.Any(i => !taken.Contains(i) && !used[i])) break;
                        taken.Add(next); cur = Key(segs[next].a) == Key(cur) ? segs[next].b : segs[next].a;
                    }
                    if (closed && pts.Count >= 3) { foreach (var i in taken) used[i] = true; loops.Add(new Loop { Pts = pts, Layer = g.Key }); }
                }
            }
            return loops;
        }

        // POST /cad/extract  { viewId, cadName?, layers?:[關鍵字], regionMm?:[minX,minY,maxX,maxY], chainLines?:true }
        public string Extract(string body)
        {
            var r = Body(body);
            long? vid = (long?)r["viewId"];
            if (!vid.HasValue || Get(_doc, vid.Value) is not View view) return Fail("viewId 必填：放有 DWG 的平面視圖。");
            string? cadName = (string?)r["cadName"];
            var layers = Strings(r["layers"]);
            double[]? region = r["regionMm"] is JArray ra && ra.Count == 4 ? ra.Select(x => (double)x).ToArray() : null;

            var imports = new FilteredElementCollector(_doc, view.Id).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                .Where(ii => string.IsNullOrWhiteSpace(cadName)
                    || (_doc.GetElement(ii.GetTypeId())?.Name ?? "").IndexOf(cadName!, StringComparison.OrdinalIgnoreCase) >= 0
                    || (ii.Name ?? "").IndexOf(cadName!, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (imports.Count == 0) return Fail("這張視圖裡找不到 DWG 連結或匯入" + (string.IsNullOrWhiteSpace(cadName) ? "" : "：" + cadName));

            var c = new Cache { Id = "cad" + DateTime.Now.ToString("HHmmssfff"), DocTitle = _doc.Title, ViewId = view.Id.Value, LevelId = (view as ViewPlan)?.GenLevel?.Id.Value, At = DateTime.Now };
            var opts = new Options { View = view, ComputeReferences = false };
            foreach (var ii in imports) { var ge = ii.get_Geometry(opts); if (ge != null) Walk(ge, c, layers, region, 0); }
            if ((bool?)r["chainLines"] ?? true) c.Loops.AddRange(ChainLines(c.Lines, 20000));

            lock (_cache)
            {
                foreach (var old in _cache.Values.OrderBy(x => x.At).Take(Math.Max(0, _cache.Count - 4)).Select(x => x.Id).ToList()) _cache.Remove(old);
                _cache[c.Id] = c;
            }

            var layerStats = c.Lines.Select(l => l.layer).Concat(c.Loops.Select(l => l.Layer)).Concat(c.Circles.Select(x => x.Layer)).Distinct()
                .Select(name => new
                {
                    layer = name,
                    lines = c.Lines.Count(l => l.layer == name),
                    closedLoops = c.Loops.Count(l => l.Layer == name),
                    rectangles = c.Loops.Count(l => l.Layer == name && Rect(l) != null),
                    circles = c.Circles.Count(x => x.Layer == name),
                }).OrderByDescending(x => x.closedLoops + x.circles).ThenByDescending(x => x.lines).Take(80).ToList();
            var all = c.Lines.SelectMany(l => new[] { l.a, l.b }).Concat(c.Loops.SelectMany(l => l.Pts)).Concat(c.Circles.Select(x => x.C)).ToList();
            object? ext = all.Count == 0 ? null : new[] { Math.Round(all.Min(p => p.X) * FT), Math.Round(all.Min(p => p.Y) * FT), Math.Round(all.Max(p => p.X) * FT), Math.Round(all.Max(p => p.Y) * FT) };
            return Ok(new
            {
                cacheId = c.Id, view = view.Name, cads = imports.Select(i => _doc.GetElement(i.GetTypeId())?.Name).Distinct(),
                totals = new { lines = c.Lines.Count, closedLoops = c.Loops.Count, circles = c.Circles.Count }, extentsMm = ext,
                layers = layerStats,
                next = "用 revit_cad_generate_bim 以 cacheId 產生柱（矩形／圓形迴圈）或樓板（封閉迴圈）。",
            });
        }

        // 矩形判斷：4 個頂點、四角接近 90°；回傳中心、寬、深、旋轉角
        private static (XYZ c, double w, double d, double ang)? Rect(Loop l)
        {
            if (l.Pts.Count != 4) return null;
            for (int i = 0; i < 4; i++)
            {
                var a = l.Pts[(i + 3) % 4]; var b = l.Pts[i]; var c2 = l.Pts[(i + 1) % 4];
                var u = (a - b); var v = (c2 - b); if (u.IsZeroLength() || v.IsZeroLength()) return null;
                if (Math.Abs(u.Normalize().DotProduct(v.Normalize())) > 0.02) return null;
            }
            var e0 = l.Pts[1] - l.Pts[0]; var e1 = l.Pts[2] - l.Pts[1];
            var center = (l.Pts[0] + l.Pts[2]) / 2;
            return (center, e0.GetLength() * FT, e1.GetLength() * FT, Math.Atan2(e0.Y, e0.X));
        }

        // POST /cad/generate
        // { cacheId, mode:"columns"|"floors", layer, familyType?, level?, topOffsetMm?, minSizeMm?:100, maxSizeMm?:3000, apply }
        public string Generate(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            string id = (string?)r["cacheId"] ?? "";
            Cache? c; lock (_cache) _cache.TryGetValue(id, out c);
            if (c == null) return Fail("找不到 cacheId「" + id + "」（快取只存在這次 Revit 工作階段，最多 5 份）。請先用 revit_cad_extract。");
            if (c.DocTitle != _doc.Title) return Fail("這份快取是從「" + c.DocTitle + "」萃取的，跟目前文件不同。");
            string mode = ((string?)r["mode"] ?? "").ToLowerInvariant();
            string layer = (string?)r["layer"] ?? "";
            if (string.IsNullOrWhiteSpace(layer)) return Fail("layer 必填（圖層名稱或關鍵字）。");
            bool LayerOk(string l) => l.IndexOf(layer, StringComparison.OrdinalIgnoreCase) >= 0;

            Level? lv = !string.IsNullOrWhiteSpace((string?)r["level"])
                ? Levels(_doc).FirstOrDefault(x => x.Name == (string?)r["level"])
                : (c.LevelId.HasValue ? Get(_doc, c.LevelId.Value) as Level : null);
            if (lv == null) return Fail("找不到樓層。請指定 level（樓層名稱）。");

            if (mode == "columns") return Columns(r, c, LayerOk, lv, apply);
            if (mode == "floors") return Floors(r, c, LayerOk, lv, apply);
            return Fail("mode 要是 columns 或 floors。");
        }

        private string Columns(JObject r, Cache c, Func<string, bool> layerOk, Level lv, bool apply)
        {
            double minS = (double?)r["minSizeMm"] ?? 100, maxS = (double?)r["maxSizeMm"] ?? 3000;
            var found = new List<(XYZ c, double w, double d, double ang, string shape)>();
            foreach (var l in c.Loops.Where(x => layerOk(x.Layer)))
            { var rc = Rect(l); if (rc != null && Math.Min(rc.Value.w, rc.Value.d) >= minS && Math.Max(rc.Value.w, rc.Value.d) <= maxS) found.Add((rc.Value.c, rc.Value.w, rc.Value.d, rc.Value.ang, "rect")); }
            foreach (var ci in c.Circles.Where(x => layerOk(x.Layer)))
            { double d = ci.R * 2 * FT; if (d >= minS && d <= maxS) found.Add((ci.C, d, d, 0, "round")); }
            // 同一位置重複畫的只留一個
            found = found.GroupBy(f => Math.Round(f.c.X * FT / 20) + "," + Math.Round(f.c.Y * FT / 20)).Select(g => g.First()).ToList();
            if (found.Count == 0) return Fail("這個圖層沒有符合尺寸範圍的矩形或圓形迴圈。");
            string SizeKey((XYZ c, double w, double d, double ang, string shape) f) =>
                f.shape == "round" ? "Ø" + Math.Round(f.w) : Math.Round(Math.Min(f.w, f.d)) + "x" + Math.Round(Math.Max(f.w, f.d));
            var allSizes = found.GroupBy(SizeKey).Select(g => new { size = g.Key, count = g.Count() }).OrderByDescending(x => x.count).ToList();
            string? onlySize = ((string?)r["onlySize"])?.Replace("*", "x").Replace("X", "x").Replace("ø", "Ø").Replace(" ", "");
            if (!string.IsNullOrWhiteSpace(onlySize))
            {
                found = found.Where(f => SizeKey(f) == onlySize).ToList();
                if (found.Count == 0) return Fail("沒有尺寸為「" + onlySize + "」的迴圈。這個圖層的尺寸：" + string.Join("、", allSizes.Select(x => x.size + "×" + x.count)));
            }

            var sizes = found.GroupBy(SizeKey)
                .Select(g => new { size = g.Key, count = g.Count() }).OrderByDescending(x => x.count).ToList();
            var symbols = new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category != null && (s.Category.Id.Value == (long)BuiltInCategory.OST_StructuralColumns || s.Category.Id.Value == (long)BuiltInCategory.OST_Columns)).ToList();
            string? ft = (string?)r["familyType"];
            var sym = string.IsNullOrWhiteSpace(ft) ? null
                : symbols.FirstOrDefault(s => $"{s.FamilyName}: {s.Name}" == ft) ?? symbols.FirstOrDefault(s => $"{s.FamilyName}: {s.Name}".IndexOf(ft!, StringComparison.OrdinalIgnoreCase) >= 0);
            double? topOff = (double?)r["topOffsetMm"];
            var plan = new
            {
                level = lv.Name, found = found.Count, onlySize, sizes, allSizesOnLayer = allSizes,
                familyType = sym != null ? $"{sym.FamilyName}: {sym.Name}" : null,
                availableColumnTypes = symbols.Select(s => $"{s.FamilyName}: {s.Name}").Take(80),
                sample = found.Take(20).Select(f => new { centerMm = new[] { Math.Round(f.c.X * FT), Math.Round(f.c.Y * FT) }, sizeMm = new[] { Math.Round(f.w), Math.Round(f.d) }, rotationDeg = Math.Round(f.ang * 180 / Math.PI, 1), f.shape }),
                note = "族群類型的斷面尺寸不會自動改；用 onlySize 一次只放一個尺寸，不同尺寸分批、各指定對應類型。",
            };
            if (!apply || sym == null)
            {
                if (apply && sym == null) return Fail("apply=true 需要 familyType（柱的「族群: 類型」）。可用：" + string.Join("、", plan.availableColumnTypes.Take(30)));
                return Ok(new { preflight = true, plan });
            }

            bool structural = sym.Category.Id.Value == (long)BuiltInCategory.OST_StructuralColumns;
            var ids = new List<long>();
            var res = RunTx(_doc, "MCP CAD Columns", () =>
            {
                if (!sym.IsActive) { sym.Activate(); _doc.Regenerate(); }
                foreach (var f in found)
                {
                    var p = new XYZ(f.c.X, f.c.Y, lv.Elevation);
                    var fi = _doc.Create.NewFamilyInstance(p, sym, lv, structural ? StructuralType.Column : StructuralType.NonStructural);
                    if (Math.Abs(f.ang) > 1e-4) ElementTransformUtils.RotateElement(_doc, fi.Id, Line.CreateBound(p, p + XYZ.BasisZ), f.ang);
                    if (topOff.HasValue)
                    {
                        fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(lv.Id);
                        fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.Set(topOff.Value / FT);
                    }
                    ids.Add(fi.Id.Value);
                }
            });
            return res.committed
                ? Ok(new { applied = true, created = ids.Count, ids, plan, warnings = res.warnings })
                : Fail("建立柱失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }

        private string Floors(JObject r, Cache c, Func<string, bool> layerOk, Level lv, bool apply)
        {
            var loops = c.Loops.Where(x => layerOk(x.Layer) && x.Pts.Count >= 3).ToList();
            var areas = loops.Select(l =>
            {
                double a = 0; for (int i = 0; i < l.Pts.Count; i++) { var p = l.Pts[i]; var q = l.Pts[(i + 1) % l.Pts.Count]; a += p.X * q.Y - q.X * p.Y; }
                return Math.Abs(a / 2) * 0.09290304;
            }).ToList();
            double minA = (double?)r["minAreaM2"] ?? 1.0;
            var pick = loops.Select((l, i) => (l, a: areas[i])).Where(x => x.a >= minA).ToList();
            if (pick.Count == 0) return Fail("這個圖層沒有面積大於 " + minA + " m² 的封閉迴圈。");

            var types = new FilteredElementCollector(_doc).OfClass(typeof(FloorType)).Cast<FloorType>().Where(t => !t.IsFoundationSlab).ToList();
            var ftp = FindType(types, (string?)r["familyType"]);
            if (ftp == null) return Fail("模型裡沒有樓板類型。");
            var plan = new { level = lv.Name, floorType = $"{ftp.FamilyName}: {ftp.Name}", willCreate = pick.Count, areasM2 = pick.Select(x => Math.Round(x.a, 2)).Take(50), availableFloorTypes = TypeNames(types).Take(60) };
            if (!apply) return Ok(new { preflight = true, plan });

            var ids = new List<long>(); var failed = new List<object>();
            var res = RunTx(_doc, "MCP CAD Floors", () =>
            {
                int k = 0;
                foreach (var (l, a) in pick)
                {
                    k++;
                    var pts = l.Pts.Select(p => new XYZ(p.X, p.Y, lv.Elevation)).ToList();
                    var cl = new CurveLoop();
                    for (int i = 0; i < pts.Count; i++) cl.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
                    var fl = Floor.Create(_doc, new List<CurveLoop> { cl }, ftp.Id, lv.Id);
                    ids.Add(fl.Id.Value);
                }
            });
            return res.committed
                ? Ok(new { applied = true, created = ids.Count, ids, plan, warnings = res.warnings })
                : Fail("建立樓板失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }
    }
}
