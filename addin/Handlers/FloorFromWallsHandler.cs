// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMcpAddin.Models;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// POST /floors/from-walls：用一組牆圍出的區域建樓板（例：連續壁內建筏基）。
    /// 做法：牆定位線投影到平面 → 互相求交點並切段 → 懸空端點在 gapToleranceMm 內接到最近的線（T 接、短缺口）
    /// → 反覆剪掉懸空段 → 取整個圖形的外輪廓（弧線保留）。
    /// boundary：outer（預設，最外圈）| largest（最大的單一封閉區）。
    /// face：location（預設，直接用牆定位線）| center | inner | outer（依牆厚再偏移）。
    /// apply=false 回報讀到的牆、未讀到的 id、閉合與否、面積與周長；不閉合時列出懸空端點。
    /// </summary>
    public class FloorFromWallsHandler
    {
        private readonly UIApplication _ui;
        public FloorFromWallsHandler(UIApplication ui) => _ui = ui;
        private static string Ok(object o) => JsonConvert.SerializeObject(ApiResponse<object>.Ok(o));
        private const double MM = 304.8;
        private const double EPS = 1e-4; // ft

        public class Req
        {
            [JsonProperty("elementIds")] public List<long>? Ids { get; set; }
            [JsonProperty("useSelection")] public bool UseSelection { get; set; }
            [JsonProperty("levelName")] public string? LevelName { get; set; }
            [JsonProperty("heightOffsetMm")] public double HeightOffsetMm { get; set; }
            [JsonProperty("floorTypeName")] public string? FloorTypeName { get; set; }
            [JsonProperty("face")] public string Face { get; set; } = "location";
            [JsonProperty("boundary")] public string Boundary { get; set; } = "outer";
            [JsonProperty("gapToleranceMm")] public double GapToleranceMm { get; set; } = 1500;
            [JsonProperty("structural")] public bool Structural { get; set; }
            [JsonProperty("apply")] public bool Apply { get; set; }
        }

        private sealed class Edge { public Curve C = null!; public int A, B; public double Half; public long Wall; public bool Dead; }

        public string Run(string body)
        {
            var req = JsonConvert.DeserializeObject<Req>(body ?? "{}") ?? new Req();
            var uidoc = _ui.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document;
            var ids = req.UseSelection
                ? uidoc.Selection.GetElementIds().ToList()
                : (req.Ids ?? new List<long>()).Select(ElementIdCompat.NewId).ToList();

            // 1) 讀牆
            var src = new List<(Curve c, double half, long id)>();
            var missing = new List<object>();
            foreach (var id in ids)
            {
                var e = doc.GetElement(id);
                if (e is Wall w && w.Location is LocationCurve lc && lc.Curve.IsBound)
                    src.Add((Flatten(lc.Curve), w.Width / 2, id.Value));
                else missing.Add(new { id = id.Value, reason = e == null ? "找不到元件" : (e is Wall ? "牆沒有定位線" : "不是牆：" + (e.Category?.Name ?? e.GetType().Name)) });
            }
            if (src.Count < 2) return Ok(new { closed = false, wallsRead = src.Count, missing, note = "讀到的牆少於 2 道。" });

            double tol = req.GapToleranceMm / MM;

            // 2) 懸空端點接到最近的線（T 接、短缺口）
            var curves = src.Select(s => (c: s.c, half: s.half, id: s.id)).ToList();
            var bridges = new List<object>();
            int n0 = curves.Count;
            for (int i = 0; i < n0; i++)
                for (int end = 0; end < 2; end++)
                {
                    var p = curves[i].c.GetEndPoint(end);
                    double best = double.MaxValue; XYZ? bp = null;
                    for (int j = 0; j < n0; j++)
                    {
                        if (j == i) continue;
                        var pr = curves[j].c.Project(p); if (pr == null) continue;
                        if (pr.Distance < best) { best = pr.Distance; bp = pr.XYZPoint; }
                    }
                    if (bp == null || best <= EPS) continue;          // 已相接
                    if (best <= tol)
                    {
                        curves.Add((Line.CreateBound(p, bp), 0, -1));
                        bridges.Add(new { fromWall = curves[i].id, gapMm = Math.Round(best * MM), at = Pt(p) });
                    }
                }

            // 3) 求交點並切段
            var cuts = curves.Select(_ => new List<double>()).ToList();
            for (int i = 0; i < curves.Count; i++)
                for (int j = i + 1; j < curves.Count; j++)
                {
                    var hits = new List<XYZ>();
#if REVIT2027_OR_GREATER
                    var cr = curves[i].c.Intersect(curves[j].c, CurveIntersectResultOption.Detailed);
                    if (cr.Result == SetComparisonResult.Overlap)
                        foreach (var ov in cr.GetOverlaps()) hits.Add(ov.Point);
#else
                    IntersectionResultArray? ra;
                    var r = curves[i].c.Intersect(curves[j].c, out ra);
                    if (r == SetComparisonResult.Overlap && ra != null)
                        foreach (IntersectionResult ir in ra) hits.Add(ir.XYZPoint);
#endif
                    foreach (var hp in hits)
                    {
                        var pi = curves[i].c.Project(hp); var pj = curves[j].c.Project(hp);
                        if (pi != null) cuts[i].Add(pi.Parameter);
                        if (pj != null) cuts[j].Add(pj.Parameter);
                    }
                }

            var nodes = new List<XYZ>();
            int Node(XYZ p)
            {
                for (int k = 0; k < nodes.Count; k++) if (nodes[k].DistanceTo(p) < 0.005) return k; // ~1.5 mm
                nodes.Add(p); return nodes.Count - 1;
            }
            var edges = new List<Edge>();
            for (int i = 0; i < curves.Count; i++)
            {
                var c = curves[i].c;
                double s = c.GetEndParameter(0), t = c.GetEndParameter(1);
                var ps = cuts[i].Where(p => p > s + EPS && p < t - EPS).Distinct().OrderBy(p => p).ToList();
                ps.Insert(0, s); ps.Add(t);
                for (int k = 0; k + 1 < ps.Count; k++)
                {
                    if (ps[k + 1] - ps[k] < EPS) continue;
                    var sub = c.Clone(); sub.MakeBound(ps[k], ps[k + 1]);
                    if (sub.Length < 0.01) continue;
                    int a = Node(sub.GetEndPoint(0)), b = Node(sub.GetEndPoint(1));
                    if (a == b) continue;
                    edges.Add(new Edge { C = sub, A = a, B = b, Half = curves[i].half, Wall = curves[i].id });
                }
            }

            // 4) 剪懸空段
            var dangling = new List<double[]>();
            bool changed = true;
            while (changed)
            {
                changed = false;
                var deg = new int[nodes.Count];
                foreach (var e in edges.Where(e => !e.Dead)) { deg[e.A]++; deg[e.B]++; }
                foreach (var e in edges.Where(e => !e.Dead))
                    if (deg[e.A] == 1 || deg[e.B] == 1)
                    {
                        e.Dead = true; changed = true;
                        dangling.Add(Pt(nodes[deg[e.A] == 1 ? e.A : e.B]));
                    }
            }
            var live = edges.Where(e => !e.Dead).ToList();
            if (live.Count == 0)
                return Ok(new { closed = false, wallsRead = src.Count, missing, bridges, danglingEnds = dangling.Take(30),
                    note = "牆沒有圍成封閉區域。看 danglingEnds（懸空端點，mm）補牆，或調大 gapToleranceMm。" });

            // 5) 平面面追蹤：每條邊兩個半邊；在每個節點依離開方向角度排序
            var half = new List<(Edge e, bool fwd)>();
            foreach (var e in live) { half.Add((e, true)); half.Add((e, false)); }
            int From(int h) => half[h].fwd ? half[h].e.A : half[h].e.B;
            int To(int h) => half[h].fwd ? half[h].e.B : half[h].e.A;
            double Ang(int h)
            {
                var c = half[h].e.C;
                var d = half[h].fwd ? c.ComputeDerivatives(c.GetEndParameter(0), false).BasisX
                                    : c.ComputeDerivatives(c.GetEndParameter(1), false).BasisX.Negate();
                return Math.Atan2(d.Y, d.X);
            }
            var outAt = new Dictionary<int, List<int>>();
            for (int h = 0; h < half.Count; h++)
            {
                if (!outAt.TryGetValue(From(h), out var l)) outAt[From(h)] = l = new List<int>();
                l.Add(h);
            }
            foreach (var k in outAt.Keys.ToList()) outAt[k] = outAt[k].OrderBy(Ang).ToList();
            int Twin(int h) => h ^ 1;
            int Next(int h)
            {
                var l = outAt[To(h)]; int i = l.IndexOf(Twin(h));
                return l[(i - 1 + l.Count) % l.Count];   // 從回頭方向順時針取下一條 → 面在左側
            }
            var used = new bool[half.Count];
            var faces = new List<(List<int> hs, double area)>();
            for (int h0 = 0; h0 < half.Count; h0++)
            {
                if (used[h0]) continue;
                var hs = new List<int>(); int h = h0, guard = 0;
                while (!used[h] && guard++ < 100000) { used[h] = true; hs.Add(h); h = Next(h); }
                if (h != h0) continue;
                faces.Add((hs, SignedArea(hs.Select(x => Oriented(half[x].e.C, half[x].fwd)))));
            }
            if (faces.Count == 0) return Ok(new { closed = false, wallsRead = src.Count, missing, note = "面追蹤失敗。" });

            bool outer = !string.Equals(req.Boundary, "largest", StringComparison.OrdinalIgnoreCase);
            var pick = outer ? faces.OrderBy(f => f.area).First() : faces.OrderByDescending(f => f.area).First();
            var seq = pick.hs.ToList();
            bool reverse = pick.area < 0;           // 外輪廓是順時針 → 反轉成逆時針
            if (reverse) seq.Reverse();

            // 6) 依節點座標重建曲線，確保首尾精確相接
            var loopCurves = new List<Curve>(); var halves = new List<double>();
            foreach (var h in seq)
            {
                bool fwd = half[h].fwd ^ reverse;
                var e = half[h].e;
                var a = nodes[fwd ? e.A : e.B]; var b = nodes[fwd ? e.B : e.A];
                foreach (var c in Snap(e.C, a, b)) { loopCurves.Add(c); halves.Add(e.Half); }
            }
            var loop = new CurveLoop(); foreach (var c in loopCurves) loop.Append(c);

            string face = (req.Face ?? "location").ToLowerInvariant();
            if (face == "inner" || face == "outer")
            {
                CurveLoop? a = null, b = null;
                try { a = CurveLoop.CreateViaOffset(loop, halves, XYZ.BasisZ); } catch { }
                try { b = CurveLoop.CreateViaOffset(loop, halves.Select(x => -x).ToList(), XYZ.BasisZ); } catch { }
                var cand = new[] { a, b }.Where(x => x != null).Select(x => (l: x!, ar: Math.Abs(Area(x!)))).ToList();
                if (cand.Count == 0) throw new InvalidOperationException("依牆厚偏移失敗，改用 face=location。");
                loop = (face == "inner" ? cand.OrderBy(x => x.ar) : cand.OrderByDescending(x => x.ar)).First().l;
            }
            double areaM2 = Math.Abs(Area(loop)) * 0.09290304, perimM = loop.GetExactLength() * MM / 1000;
            var usedWalls = seq.Select(h => half[h].e.Wall).Where(x => x > 0).Distinct().ToList();

            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => l.Name == req.LevelName) ?? throw new ArgumentException("找不到樓層：" + req.LevelName);
            var ftype = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>()
                .FirstOrDefault(t => string.IsNullOrWhiteSpace(req.FloorTypeName) || t.Name.IndexOf(req.FloorTypeName!, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? throw new ArgumentException("找不到樓板類型：" + req.FloorTypeName);

            var info = new
            {
                closed = true, wallsRead = src.Count, missing, bridges, prunedDangling = dangling.Count,
                boundaryWalls = usedWalls, unusedWalls = src.Select(s => s.id).Except(usedWalls),
                faces = faces.Count, boundary = outer ? "outer" : "largest", face,
                areaM2 = Math.Round(areaM2, 1), perimeterM = Math.Round(perimM, 1), segments = loopCurves.Count,
                level = level.Name, heightOffsetMm = req.HeightOffsetMm, floorType = ftype.Name
            };
            if (!req.Apply) return Ok(new { preflight = true, info });

            ElementId fid;
            using (var t = new Transaction(doc, "MCP: 依牆建樓板"))
            {
                t.Start();
                var f = Floor.Create(doc, new List<CurveLoop> { loop }, ftype.Id, level.Id, req.Structural, null, 0);
                f.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(req.HeightOffsetMm / MM);
                fid = f.Id;
                t.Commit();
            }
            return Ok(new { applied = true, floorId = fid.Value, info });
        }

        private static IEnumerable<Curve> Snap(Curve c, XYZ a, XYZ b)
        {
            if (c is Line) { yield return Line.CreateBound(a, b); yield break; }
            if (c is Arc)
            {
                var mid = c.Evaluate(0.5, true);
                Curve? arc = null; try { arc = Arc.Create(a, b, mid); } catch { }
                if (arc != null) { yield return arc; yield break; }
            }
            var pts = c.Tessellate().ToList();
            if (pts[0].DistanceTo(a) > pts[pts.Count - 1].DistanceTo(a)) pts.Reverse();
            pts[0] = a; pts[pts.Count - 1] = b;
            for (int i = 0; i + 1 < pts.Count; i++)
                if (pts[i].DistanceTo(pts[i + 1]) > 0.005) yield return Line.CreateBound(pts[i], pts[i + 1]);
        }
        private static Curve Oriented(Curve c, bool fwd) => fwd ? c : c.CreateReversed();
        private static Curve Flatten(Curve c)
        {
            var tr = Transform.CreateTranslation(new XYZ(0, 0, -c.GetEndPoint(0).Z));
            return c.CreateTransformed(tr);
        }
        private static double[] Pt(XYZ p) => new[] { Math.Round(p.X * MM), Math.Round(p.Y * MM) };
        private static double SignedArea(IEnumerable<Curve> cs)
        {
            var pts = new List<XYZ>();
            foreach (var c in cs) { var tp = c.Tessellate(); pts.AddRange(tp.Take(tp.Count - 1)); }
            double s = 0; for (int i = 0; i < pts.Count; i++) { var p = pts[i]; var q = pts[(i + 1) % pts.Count]; s += p.X * q.Y - q.X * p.Y; }
            return s / 2;
        }
        private static double Area(CurveLoop l) => SignedArea(l);
    }
}
