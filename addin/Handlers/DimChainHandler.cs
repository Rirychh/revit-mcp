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
using static RevitMcpAddin.Handlers.ViewPlane;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 牆的連續尺寸線、尺寸檢查與移動（功能構想參考 MLabs Revit MCP 公開的工具清單，程式獨立實作）。
    /// </summary>
    public class DimChainHandler
    {
        private readonly Document _doc;
        public DimChainHandler(Document doc) => _doc = doc;

        private View GetView(long? id)
        {
            if (!id.HasValue) throw new ArgumentException("viewId 必填。");
            return _doc.GetElement(ElementIdCompat.NewId(id.Value)) as View ?? throw new ArgumentException("找不到視圖 " + id);
        }

        private class RefPos { public Reference Ref = null!; public double Pos; public string Kind = ""; public long Owner; }

        private DimensionType? FindDimType(string? name)
        {
            var all = new FilteredElementCollector(_doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .Where(t => t.StyleType == DimensionStyleType.Linear).ToList();
            if (string.IsNullOrWhiteSpace(name)) return null;
            return all.FirstOrDefault(t => t.Name == name)
                ?? all.FirstOrDefault(t => t.Name.IndexOf(name!, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? throw new ArgumentException("找不到線性尺寸類型「" + name + "」。可用：" + string.Join("、", all.Select(t => t.Name).Distinct().Take(40)));
        }

        // POST /annotations/dimensions/wall-chains
        // { viewId, wallIds?, direction?:"horizontal"|"vertical"|"both", faces?:"both"|"exterior"|"interior",
        //   includeGrids?:false, side?:"auto"|"top"|"bottom"|"left"|"right", offsetMm?:1000, dimensionTypeName?, apply }
        public string WallChains(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var view = GetView((long?)r["viewId"]);
            if (view is not ViewPlan) return Fail("連續尺寸線目前只支援平面視圖（樓層平面、天花平面、結構平面）。");
            string dir = ((string?)r["direction"] ?? "both").ToLowerInvariant();
            string faces = ((string?)r["faces"] ?? "both").ToLowerInvariant();
            bool grids = (bool?)r["includeGrids"] ?? false;
            string side = ((string?)r["side"] ?? "auto").ToLowerInvariant();
            double offset = (double?)r["offsetMm"] ?? 1000;
            var dimType = FindDimType((string?)r["dimensionTypeName"]);

            List<Wall> walls;
            if (r["wallIds"] is JArray)
                walls = Ids(r["wallIds"]).Select(i => Get(_doc, i) as Wall).Where(w => w != null).Cast<Wall>().ToList();
            else
                walls = new FilteredElementCollector(_doc, view.Id).OfClass(typeof(Wall)).Cast<Wall>().ToList();
            walls = walls.Where(w => w.Location is LocationCurve lc && lc.Curve is Line).ToList();
            if (walls.Count == 0) return Fail("沒有可標的直牆（弧牆不支援）。");

            var chains = new List<(string name, List<RefPos> refs, Line line, double vAt)>();
            var notes = new List<string>();
            foreach (var d in dir == "both" ? new[] { "horizontal", "vertical" } : new[] { dir })
            {
                bool horiz = d == "horizontal";
                var axis = horiz ? view.RightDirection : view.UpDirection;   // 尺寸量測方向
                var across = horiz ? view.UpDirection : view.RightDirection;
                var refs = new List<RefPos>();
                double aMin = double.MaxValue, aMax = double.MinValue, cMin = double.MaxValue, cMax = double.MinValue;
                XYZ? depth = null;

                foreach (var w in walls)
                {
                    var ln = (Line)((LocationCurve)w.Location).Curve;
                    if (Math.Abs(ln.Direction.DotProduct(across)) < 0.999) continue;   // 牆要垂直於量測方向
                    depth ??= ln.GetEndPoint(0);
                    foreach (var p in new[] { ln.GetEndPoint(0), ln.GetEndPoint(1) })
                    {
                        double a = (p - view.Origin).DotProduct(axis) * FT, c = (p - view.Origin).DotProduct(across) * FT;
                        aMin = Math.Min(aMin, a); aMax = Math.Max(aMax, a); cMin = Math.Min(cMin, c); cMax = Math.Max(cMax, c);
                    }
                    var shells = faces == "exterior" ? new[] { ShellLayerType.Exterior }
                               : faces == "interior" ? new[] { ShellLayerType.Interior }
                               : new[] { ShellLayerType.Exterior, ShellLayerType.Interior };
                    foreach (var sh in shells)
                    {
                        IList<Reference> fr;
                        try { fr = HostObjectUtils.GetSideFaces(w, sh); } catch { continue; }
                        foreach (var fref in fr)
                        {
                            if (w.GetGeometryObjectFromReference(fref) is not PlanarFace pf) continue;
                            if (Math.Abs(pf.FaceNormal.DotProduct(axis)) < 0.999) continue;
                            refs.Add(new RefPos { Ref = fref, Pos = (pf.Origin - view.Origin).DotProduct(axis) * FT, Kind = "wallFace", Owner = w.Id.Value });
                        }
                    }
                }
                if (grids)
                {
                    foreach (var g in new FilteredElementCollector(_doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>())
                    {
                        if (g.Curve is not Line gl || Math.Abs(gl.Direction.DotProduct(across)) < 0.999) continue;
                        refs.Add(new RefPos { Ref = new Reference(g), Pos = (gl.Origin - view.Origin).DotProduct(axis) * FT, Kind = "grid", Owner = g.Id.Value });
                    }
                }
                // 同一位置只留一個參考（相接或對齊的牆面會重疊）
                refs = refs.OrderBy(x => x.Pos).Aggregate(new List<RefPos>(), (acc, x) => { if (acc.Count == 0 || x.Pos - acc[acc.Count - 1].Pos > 1) acc.Add(x); return acc; });
                if (refs.Count < 2 || depth == null) { notes.Add(d + "：可用的參考面少於 2 個，略過"); continue; }

                string s = side == "auto" ? (horiz ? "top" : "right") : side;
                bool high = s == "top" || s == "right";
                double cAt = high ? cMax + offset : cMin - offset;
                double a0 = Math.Min(aMin, refs[0].Pos) - 500, a1 = Math.Max(aMax, refs[refs.Count - 1].Pos) + 500;
                double w0 = (depth - view.Origin).DotProduct(view.ViewDirection);
                XYZ P(double a) => view.Origin + axis * (a / FT) + across * (cAt / FT) + view.ViewDirection * w0;
                chains.Add((d, refs, Line.CreateBound(P(a0), P(a1)), cAt));
            }
            if (chains.Count == 0) return Fail("沒有產生任何尺寸鏈。" + string.Join("；", notes));

            object Summ((string name, List<RefPos> refs, Line line, double vAt) c)
            {
                var segs = new List<double>();
                for (int i = 1; i < c.refs.Count; i++) segs.Add(Math.Round(c.refs[i].Pos - c.refs[i - 1].Pos));
                return new
                {
                    direction = c.name, references = c.refs.Count, segmentsMm = segs,
                    totalMm = Math.Round(c.refs[c.refs.Count - 1].Pos - c.refs[0].Pos),
                    tinySegments = segs.Count(x => x < 100),
                    kinds = c.refs.GroupBy(x => x.Kind).ToDictionary(g => g.Key, g => g.Count()),
                };
            }
            var plan = new { view = view.Name, walls = walls.Count, faces, includeGrids = grids, side, offsetMm = offset, dimensionType = dimType?.Name ?? "（預設）", chains = chains.Select(Summ).ToList(), notes };
            if (!apply) return Ok(new { preflight = true, plan });

            var ids = new List<long>();
            var res = RunTx(_doc, "MCP Wall Dimension Chains", () =>
            {
                foreach (var c in chains)
                {
                    var ra = new ReferenceArray();
                    foreach (var x in c.refs) ra.Append(x.Ref);
                    var dim = dimType != null ? _doc.Create.NewDimension(view, c.line, ra, dimType) : _doc.Create.NewDimension(view, c.line, ra);
                    if (dim == null) throw new InvalidOperationException(c.name + " 尺寸鏈建立失敗");
                    ids.Add(dim.Id.Value);
                }
            });
            return res.committed
                ? Ok(new { applied = true, dimensionIds = ids, plan, warnings = res.warnings })
                : Fail("建立尺寸鏈失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }

        // GET /annotations/dimensions/inspect?viewId=&minSegmentMm=100
        public string Inspect(NameValueCollection qs)
        {
            var view = GetView(long.TryParse(qs["viewId"], out var v) ? v : (long?)null);
            double minSeg = double.TryParse(qs["minSegmentMm"], out var m) ? m : 100;
            var dims = new FilteredElementCollector(_doc, view.Id).OfClass(typeof(Dimension)).Cast<Dimension>()
                .Where(d => d.DimensionShape == DimensionShape.Linear).ToList();
            var rows = new List<(Dimension d, Line? line, object dto)>();
            foreach (var d in dims)
            {
                Line? ln = null; try { ln = d.Curve as Line; } catch { }
                var segs = new List<double>();
                if (d.NumberOfSegments > 0) foreach (DimensionSegment s in d.Segments) segs.Add(Math.Round((s.Value ?? 0) * FT));
                else segs.Add(Math.Round((d.Value ?? 0) * FT));
                object? lineUv = null;
                if (ln != null)
                {
                    var (u, vv) = UV(view, ln.Origin);
                    lineUv = new { originUv = R2(u, vv), along = Math.Abs(ln.Direction.DotProduct(view.RightDirection)) > 0.999 ? "u" : Math.Abs(ln.Direction.DotProduct(view.UpDirection)) > 0.999 ? "v" : "斜向" };
                }
                rows.Add((d, ln, new
                {
                    dimensionId = d.Id.Value, typeName = d.DimensionType?.Name, references = d.References.Size,
                    segmentsMm = segs, tinySegments = segs.Count(x => x < minSeg), line = lineUv,
                }));
            }
            // 平行且距離很近（< 300 mm）的尺寸線視為疊在一起
            var close = new List<object>();
            for (int i = 0; i < rows.Count; i++)
                for (int j = i + 1; j < rows.Count; j++)
                {
                    var a = rows[i].line; var b = rows[j].line;
                    if (a == null || b == null || Math.Abs(a.Direction.DotProduct(b.Direction)) < 0.999) continue;
                    var n = view.ViewDirection.CrossProduct(a.Direction).Normalize();
                    double dist = Math.Abs((b.Origin - a.Origin).DotProduct(n)) * FT;
                    if (dist < 300) close.Add(new { ids = new[] { rows[i].d.Id.Value, rows[j].d.Id.Value }, distanceMm = Math.Round(dist) });
                }
            return Ok(new { view = view.Name, total = rows.Count, minSegmentMm = minSeg, crowdedPairs = close.Take(200), dimensions = rows.Select(x => x.dto).Take(500) });
        }

        // POST /annotations/dimensions/adjust
        // { viewId, moves?:[{dimensionId, offsetMm?, deltaMm?:[du,dv]}], deleteIds?:[], apply }
        public string Adjust(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var view = GetView((long?)r["viewId"]);
            var plan = new List<(ElementId id, XYZ vec, object dto)>(); var bad = new List<object>();
            foreach (var m in (r["moves"] as JArray) ?? new JArray())
            {
                long id = (long?)m["dimensionId"] ?? 0;
                if (Get(_doc, id) is not Dimension d) { bad.Add(new { dimensionId = id, reason = "不是尺寸" }); continue; }
                XYZ vec = XYZ.Zero;
                if (m["offsetMm"] != null)
                {
                    Line? ln = null; try { ln = d.Curve as Line; } catch { }
                    if (ln == null) { bad.Add(new { dimensionId = id, reason = "讀不到尺寸線方向" }); continue; }
                    vec += view.ViewDirection.CrossProduct(ln.Direction).Normalize() * ((double)m["offsetMm"]! / FT);
                }
                if (m["deltaMm"] is JArray dd && dd.Count >= 2)
                    vec += view.RightDirection * ((double)dd[0] / FT) + view.UpDirection * ((double)dd[1] / FT);
                if (vec.IsZeroLength()) { bad.Add(new { dimensionId = id, reason = "沒有位移量" }); continue; }
                var (du, dv) = (vec.DotProduct(view.RightDirection) * FT, vec.DotProduct(view.UpDirection) * FT);
                plan.Add((d.Id, vec, new { dimensionId = id, moveUv = R2(du, dv) }));
            }
            var del = Ids(r["deleteIds"]).Where(i => Get(_doc, i) is Dimension).ToList();
            if (plan.Count == 0 && del.Count == 0) return Fail("沒有可執行的移動或刪除。" + Newtonsoft.Json.JsonConvert.SerializeObject(bad));
            if (!apply) return Ok(new { preflight = true, moves = plan.Select(p => p.dto), deleteIds = del, skipped = bad });

            var res = RunTx(_doc, "MCP Adjust Dimensions", () =>
            {
                foreach (var p in plan) ElementTransformUtils.MoveElement(_doc, p.id, p.vec);
                if (del.Count > 0) _doc.Delete(del.Select(ElementIdCompat.NewId).ToList());
            });
            return res.committed
                ? Ok(new { applied = true, moved = plan.Count, deleted = del.Count, skipped = bad, warnings = res.warnings })
                : Fail("調整尺寸失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }
    }
}
