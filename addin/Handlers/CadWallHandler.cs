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
    /// POST /cad/wall-diff：把視圖中連結（或匯入）的 DWG 牆線跟 Revit 牆比對。
    ///   1) 讀 ImportInstance 幾何（已是模型座標，免配準），依圖層名稱關鍵字篩選牆線
    ///   2) 平行雙線配對成中心線＋牆厚
    ///   3) 中心線附近沒有可見的 Revit 牆 → newWalls（CAD 有、Revit 沒有）
    ///      Revit 可見牆沿線找不到 CAD 牆線 → obsoleteWalls／partialWalls（Revit 有、CAD 沒有）
    ///   4) apply=true 才依 newWalls 建牆（牆型依厚度找最接近的基本牆，或指定 wallTypeName）
    /// 只看「這張視圖裡看得到」的牆；被隱藏的牆不列入，避免誤刪別人刻意留著的元素。
    /// 不會刪除任何東西；過時清單交給使用者確認後再用 revit_delete_elements。
    /// </summary>
    public class CadWallHandler
    {
        private const double FT = 304.8;
        private readonly UIApplication _uiApp;
        public CadWallHandler(UIApplication uiApp) => _uiApp = uiApp;
        private static string Ok(object o) => JsonConvert.SerializeObject(ApiResponse<object>.Ok(o));

        private struct Seg { public double X1, Y1, X2, Y2; public string Layer; }
        private class Center { public double X1, Y1, X2, Y2, Th, Len; public string Layer = ""; }

        public string Diff(string body)
        {
            var req = JsonConvert.DeserializeObject<Req>(body ?? "{}") ?? new Req();
            var uidoc = _uiApp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document;
            if (!req.ViewId.HasValue) throw new ArgumentException("viewId 必填：放有 DWG 連結的平面視圖。");
            var view = doc.GetElement(ElementIdCompat.NewId(req.ViewId.Value)) as View
                       ?? throw new ArgumentException("找不到視圖 " + req.ViewId);
            var level = (view as ViewPlan)?.GenLevel ?? throw new ArgumentException("viewId 必須是平面視圖。");
            var layers = (req.Layers == null || req.Layers.Count == 0) ? new List<string> { "WALL" } : req.Layers;

            // ── 1) CAD 線段 ──────────────────────────────
            var imports = new FilteredElementCollector(doc, view.Id).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                .Where(ii =>
                {
                    if (string.IsNullOrWhiteSpace(req.CadName)) return true;
                    var tn = doc.GetElement(ii.GetTypeId())?.Name ?? "";
                    return tn.IndexOf(req.CadName!, StringComparison.OrdinalIgnoreCase) >= 0
                        || (ii.Name ?? "").IndexOf(req.CadName!, StringComparison.OrdinalIgnoreCase) >= 0;
                }).ToList();
            if (imports.Count == 0) throw new ArgumentException("這張視圖裡找不到符合的 DWG 連結" + (string.IsNullOrWhiteSpace(req.CadName) ? "" : "：" + req.CadName));

            var segs = new List<Seg>();
            var layerCount = new Dictionary<string, int>();
            int arcsSkipped = 0;
            var opts = new Options { View = view, ComputeReferences = false };
            foreach (var ii in imports)
            {
                var ge = ii.get_Geometry(opts);
                if (ge != null) Walk(doc, ge, layers, req, segs, layerCount, ref arcsSkipped, 0);
            }
            if (segs.Count == 0)
                throw new InvalidOperationException("DWG 裡沒有符合圖層關鍵字的線段。圖層統計：" + string.Join(", ", layerCount.OrderByDescending(k => k.Value).Take(30).Select(k => k.Key + "=" + k.Value)));

            // ── 2) 雙線配對 ──────────────────────────────
            var centers = Pair(segs, req);

            // ── 3) Revit 可見牆 ──────────────────────────
            var walls = new List<(Wall w, List<(double x1, double y1, double x2, double y2)> pl, double width)>();
            foreach (var w in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Wall)).Cast<Wall>())
            {
                if (w.Location is not LocationCurve lc || lc.Curve == null) continue;
                var pts = lc.Curve is Line ? new List<XYZ> { lc.Curve.GetEndPoint(0), lc.Curve.GetEndPoint(1) } : lc.Curve.Tessellate().ToList();
                var pl = new List<(double, double, double, double)>();
                for (int i = 0; i + 1 < pts.Count; i++)
                    pl.Add((pts[i].X * FT, pts[i].Y * FT, pts[i + 1].X * FT, pts[i + 1].Y * FT));
                if (!InRegion(pl, req)) continue;
                walls.Add((w, pl, w.Width * FT));
            }
            var wallIdx = new Grid(2000);
            for (int k = 0; k < walls.Count; k++) foreach (var s in walls[k].pl) wallIdx.Add(k, s.x1, s.y1, s.x2, s.y2);
            var segIdx = new Grid(2000);
            for (int k = 0; k < segs.Count; k++) segIdx.Add(k, segs[k].X1, segs[k].Y1, segs[k].X2, segs[k].Y2);

            // CAD 中心線 → 找不到牆 = 新牆
            var newWalls = new List<Center>();
            int matched = 0;
            foreach (var c in centers)
            {
                int n = Math.Max(2, (int)(c.Len / 400)); int far = 0;
                for (int k = 0; k <= n; k++)
                {
                    double t = (double)k / n, x = c.X1 + (c.X2 - c.X1) * t, y = c.Y1 + (c.Y2 - c.Y1) * t;
                    double best = double.MaxValue;
                    foreach (var wi in wallIdx.Near(x, y, req.MatchTolMm + 1000))
                        foreach (var s in walls[wi].pl) best = Math.Min(best, DSeg(x, y, s.x1, s.y1, s.x2, s.y2));
                    if (best > req.MatchTolMm) far++;
                }
                if ((double)far / (n + 1) >= 0.7) newWalls.Add(c); else matched++;
            }

            // Revit 牆 → 找不到 CAD 線 = 過時／部分
            var obsolete = new List<object>(); var partial = new List<object>();
            foreach (var (w, pl, width) in walls)
            {
                double tol = req.MatchTolMm + width / 2;
                var flags = new List<(double x, double y, double along, bool ok)>();
                double acc = 0;
                foreach (var s in pl)
                {
                    double L = Math.Sqrt((s.x2 - s.x1) * (s.x2 - s.x1) + (s.y2 - s.y1) * (s.y2 - s.y1));
                    int n = Math.Max(2, (int)(L / 400));
                    for (int k = 0; k <= n; k++)
                    {
                        double t = (double)k / n, x = s.x1 + (s.x2 - s.x1) * t, y = s.y1 + (s.y2 - s.y1) * t;
                        double best = double.MaxValue;
                        foreach (var si in segIdx.Near(x, y, tol + 1000))
                        { var g = segs[si]; best = Math.Min(best, DSeg(x, y, g.X1, g.Y1, g.X2, g.Y2)); }
                        flags.Add((x, y, acc + L * t, best <= tol));
                    }
                    acc += L;
                }
                if (flags.Count == 0) continue;
                double cov = flags.Count(f => f.ok) / (double)flags.Count;
                var typeName = doc.GetElement(w.GetTypeId())?.Name ?? "";
                if (cov < 0.3)
                    obsolete.Add(new { id = w.Id.Value, type = typeName, lengthMm = Math.Round(acc), coverage = Math.Round(cov, 2), start = R(pl[0].x1, pl[0].y1), end = R(pl[pl.Count - 1].x2, pl[pl.Count - 1].y2) });
                else if (cov < 0.85)
                {
                    var gaps = new List<object>(); int i0 = -1;
                    for (int i = 0; i <= flags.Count; i++)
                    {
                        bool bad = i < flags.Count && !flags[i].ok;
                        if (bad && i0 < 0) i0 = i;
                        if (!bad && i0 >= 0)
                        {
                            var a = flags[i0]; var b = flags[i - 1];
                            if (b.along - a.along >= 500) gaps.Add(new { fromMm = Math.Round(a.along), toMm = Math.Round(b.along), from = R(a.x, a.y), to = R(b.x, b.y) });
                            i0 = -1;
                        }
                    }
                    if (gaps.Count > 0)
                        partial.Add(new { id = w.Id.Value, type = typeName, lengthMm = Math.Round(acc), coverage = Math.Round(cov, 2), uncovered = gaps });
                }
            }

            // ── 4) 建牆（apply=true）────────────────────
            var created = new List<long>(); var swallowed = new List<string>();
            string? typeUsedNote = null;
            if (req.Apply && newWalls.Count > 0)
            {
                var types = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().Where(t => t.Kind == WallKind.Basic).ToList();
                WallType? fixedType = null;
                if (!string.IsNullOrWhiteSpace(req.WallTypeName))
                    fixedType = types.FirstOrDefault(t => t.Name == req.WallTypeName) ?? types.FirstOrDefault(t => t.Name.IndexOf(req.WallTypeName!, StringComparison.OrdinalIgnoreCase) >= 0)
                                ?? throw new ArgumentException("找不到牆類型：" + req.WallTypeName);
                double heightMm = req.HeightMm ?? 0;
                if (heightMm <= 0)
                {
                    // 預設：同一層可見牆的牆高中位數（相鄰樓層可能只差 1m，不能拿來當牆高）
                    var hs = walls.Where(x => x.w.LevelId == level.Id)
                        .Select(x => (x.w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0) * FT)
                        .Where(v => v > 100).OrderBy(v => v).ToList();
                    heightMm = hs.Count > 0 ? hs[hs.Count / 2] : 3000;
                }
                using var tx = new Transaction(doc, "CAD 牆比對建牆");
                var fo = tx.GetFailureHandlingOptions(); var sw = new SwallowWarnings(); fo.SetFailuresPreprocessor(sw); tx.SetFailureHandlingOptions(fo);
                tx.Start();
                foreach (var c in newWalls)
                {
                    var wt = fixedType ?? types.OrderBy(t => Math.Abs(t.Width * FT - c.Th)).FirstOrDefault();
                    if (wt == null) continue;
                    var line = Line.CreateBound(new XYZ(c.X1 / FT, c.Y1 / FT, level.Elevation), new XYZ(c.X2 / FT, c.Y2 / FT, level.Elevation));
                    var w = Wall.Create(doc, line, wt.Id, level.Id, heightMm / FT, 0, false, false);
                    created.Add(w.Id.Value);
                }
                tx.Commit();
                swallowed = sw.Swallowed;
                typeUsedNote = fixedType != null ? fixedType.Name : "依厚度自動配最接近的基本牆類型";
            }

            int lim = Math.Max(1, req.MaxResults);
            return Ok(new
            {
                document = doc.Title,
                view = view.Name,
                level = level.Name,
                cad = imports.Select(i => doc.GetElement(i.GetTypeId())?.Name ?? i.Name).Distinct().ToList(),
                cadSegments = segs.Count,
                arcsSkipped,
                layersUsed = layerCount.Where(k => layers.Any(l => k.Key.IndexOf(l, StringComparison.OrdinalIgnoreCase) >= 0)).ToDictionary(k => k.Key, k => k.Value),
                centerlines = centers.Count,
                matched,
                visibleWalls = walls.Count,
                newWallsCount = newWalls.Count,
                newWalls = newWalls.Take(lim).Select(c => new { from = R(c.X1, c.Y1), to = R(c.X2, c.Y2), thicknessMm = Math.Round(c.Th), lengthMm = Math.Round(c.Len), layer = c.Layer }),
                obsoleteWallsCount = obsolete.Count,
                obsoleteWalls = obsolete.Take(lim),
                partialWallsCount = partial.Count,
                partialWalls = partial.Take(lim),
                applied = req.Apply,
                createdWallIds = created,
                wallType = typeUsedNote,
                swallowedWarnings = swallowed.Distinct().Take(10),
                note = req.Apply ? "已建牆。過時／部分吻合的牆沒有刪，確認後再用 revit_delete_elements。"
                                 : "預檢，模型沒有改動。apply=true 才建 newWalls；過時清單請人工確認再刪。"
            });
        }

        private static object R(double x, double y) => new[] { Math.Round(x), Math.Round(y) };

        private static bool InRegion(IEnumerable<(double x1, double y1, double x2, double y2)> pl, Req q)
        {
            if (q.Region == null) return true;
            foreach (var s in pl)
                if (Math.Max(s.x1, s.x2) >= q.Region.MinX && Math.Min(s.x1, s.x2) <= q.Region.MaxX &&
                    Math.Max(s.y1, s.y2) >= q.Region.MinY && Math.Min(s.y1, s.y2) <= q.Region.MaxY) return true;
            return false;
        }

        private static void Walk(Document doc, GeometryElement ge, List<string> layers, Req req, List<Seg> segs,
                                 Dictionary<string, int> layerCount, ref int arcs, int depth)
        {
            if (depth > 8) return;
            foreach (var obj in ge)
            {
                if (obj is GeometryInstance gi)
                {
                    var inner = gi.GetInstanceGeometry();
                    if (inner != null) Walk(doc, inner, layers, req, segs, layerCount, ref arcs, depth + 1);
                    continue;
                }
                string layer = "";
                if (obj.GraphicsStyleId != ElementId.InvalidElementId && doc.GetElement(obj.GraphicsStyleId) is GraphicsStyle gs)
                    layer = gs.GraphicsStyleCategory?.Name ?? gs.Name ?? "";
                layerCount[layer] = layerCount.TryGetValue(layer, out var c) ? c + 1 : 1;
                if (!layers.Any(l => layer.IndexOf(l, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                if (obj is Line ln)
                    AddSeg(segs, ln.GetEndPoint(0), ln.GetEndPoint(1), layer, req);
                else if (obj is PolyLine pl)
                {
                    var p = pl.GetCoordinates();
                    for (int i = 0; i + 1 < p.Count; i++) AddSeg(segs, p[i], p[i + 1], layer, req);
                }
                else if (obj is Arc) arcs++;
            }
        }

        private static void AddSeg(List<Seg> segs, XYZ a, XYZ b, string layer, Req req)
        {
            double x1 = a.X * FT, y1 = a.Y * FT, x2 = b.X * FT, y2 = b.Y * FT;
            if (Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)) < 50) return;
            if (req.Region != null && (Math.Max(x1, x2) < req.Region.MinX || Math.Min(x1, x2) > req.Region.MaxX ||
                                       Math.Max(y1, y2) < req.Region.MinY || Math.Min(y1, y2) > req.Region.MaxY)) return;
            segs.Add(new Seg { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Layer = layer });
        }

        private static List<Center> Pair(List<Seg> segs, Req req)
        {
            double sinTol = Math.Sin(req.AngleTolDeg * Math.PI / 180);
            var idx = new Grid(2000);
            for (int k = 0; k < segs.Count; k++) idx.Add(k, segs[k].X1, segs[k].Y1, segs[k].X2, segs[k].Y2);
            var raw = new List<Center>();
            for (int i = 0; i < segs.Count; i++)
            {
                var a = segs[i];
                double la = Math.Sqrt((a.X2 - a.X1) * (a.X2 - a.X1) + (a.Y2 - a.Y1) * (a.Y2 - a.Y1));
                double ux = (a.X2 - a.X1) / la, uy = (a.Y2 - a.Y1) / la, nx = -uy, ny = ux;
                var cand = new HashSet<int>();
                int steps = Math.Max(1, (int)(la / 1500));
                for (int s = 0; s <= steps; s++)
                {
                    double t = (double)s / steps;
                    foreach (var j in idx.Near(a.X1 + (a.X2 - a.X1) * t, a.Y1 + (a.Y2 - a.Y1) * t, req.MaxThicknessMm + 100)) if (j > i) cand.Add(j);
                }
                foreach (var j in cand)
                {
                    var b = segs[j];
                    if (b.Layer != a.Layer) continue;
                    double lb = Math.Sqrt((b.X2 - b.X1) * (b.X2 - b.X1) + (b.Y2 - b.Y1) * (b.Y2 - b.Y1));
                    double vx = (b.X2 - b.X1) / lb, vy = (b.Y2 - b.Y1) / lb;
                    if (Math.Abs(ux * vy - uy * vx) > sinTol) continue;
                    double d1 = (b.X1 - a.X1) * nx + (b.Y1 - a.Y1) * ny, d2 = (b.X2 - a.X1) * nx + (b.Y2 - a.Y1) * ny;
                    if (Math.Abs(d1 - d2) > 30) continue;
                    double d = (d1 + d2) / 2;
                    if (Math.Abs(d) < req.MinThicknessMm || Math.Abs(d) > req.MaxThicknessMm) continue;
                    double t1 = (b.X1 - a.X1) * ux + (b.Y1 - a.Y1) * uy, t2 = (b.X2 - a.X1) * ux + (b.Y2 - a.Y1) * uy;
                    double s0 = Math.Max(0, Math.Min(t1, t2)), s1 = Math.Min(la, Math.Max(t1, t2));
                    if (s1 - s0 < req.MinLengthMm) continue;
                    double h = d / 2;
                    raw.Add(new Center
                    {
                        X1 = a.X1 + ux * s0 + nx * h, Y1 = a.Y1 + uy * s0 + ny * h,
                        X2 = a.X1 + ux * s1 + nx * h, Y2 = a.Y1 + uy * s1 + ny * h,
                        Th = Math.Abs(d), Len = s1 - s0, Layer = a.Layer
                    });
                }
            }
            // 去重：同方向、相距 < 120mm、落在既有中心線範圍內的只留最長的
            raw.Sort((p, q) => q.Len.CompareTo(p.Len));
            var keep = new List<Center>();
            var kIdx = new Grid(2000);
            foreach (var c in raw)
            {
                double mx = (c.X1 + c.X2) / 2, my = (c.Y1 + c.Y2) / 2;
                double vx = (c.X2 - c.X1) / c.Len, vy = (c.Y2 - c.Y1) / c.Len;
                bool dup = false;
                foreach (var ki in kIdx.Near(mx, my, 200))
                {
                    var k = keep[ki];
                    double ux = (k.X2 - k.X1) / k.Len, uy = (k.Y2 - k.Y1) / k.Len;
                    if (Math.Abs(ux * vy - uy * vx) > 0.02) continue;
                    double off = Math.Abs((mx - k.X1) * -uy + (my - k.Y1) * ux);
                    double s = (mx - k.X1) * ux + (my - k.Y1) * uy;
                    if (off <= 120 && s >= -100 && s <= k.Len + 100) { dup = true; break; }
                }
                if (!dup) { kIdx.Add(keep.Count, c.X1, c.Y1, c.X2, c.Y2); keep.Add(c); }
            }
            return keep;
        }

        private static double DSeg(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
            double t = l2 == 0 ? 0 : Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / l2));
            double qx = ax + t * dx - px, qy = ay + t * dy - py;
            return Math.Sqrt(qx * qx + qy * qy);
        }

        /// <summary>簡單的均勻格網索引：線段登記到它經過的每一格。</summary>
        private class Grid
        {
            private readonly double _c;
            private readonly Dictionary<(long, long), List<int>> _m = new();
            public Grid(double cell) => _c = cell;
            public void Add(int id, double x1, double y1, double x2, double y2)
            {
                double L = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
                int n = Math.Max(1, (int)(L / (_c / 2)));
                var seen = new HashSet<(long, long)>();
                for (int k = 0; k <= n; k++)
                {
                    double t = (double)k / n;
                    var key = ((long)Math.Floor((x1 + (x2 - x1) * t) / _c), (long)Math.Floor((y1 + (y2 - y1) * t) / _c));
                    if (!seen.Add(key)) continue;
                    if (!_m.TryGetValue(key, out var l)) _m[key] = l = new List<int>();
                    l.Add(id);
                }
            }
            public IEnumerable<int> Near(double x, double y, double r)
            {
                long x0 = (long)Math.Floor((x - r) / _c), x1 = (long)Math.Floor((x + r) / _c);
                long y0 = (long)Math.Floor((y - r) / _c), y1 = (long)Math.Floor((y + r) / _c);
                var seen = new HashSet<int>();
                for (long i = x0; i <= x1; i++)
                    for (long j = y0; j <= y1; j++)
                        if (_m.TryGetValue((i, j), out var l))
                            foreach (var id in l) if (seen.Add(id)) yield return id;
            }
        }

        public class Region
        {
            [JsonProperty("minX")] public double MinX { get; set; }
            [JsonProperty("minY")] public double MinY { get; set; }
            [JsonProperty("maxX")] public double MaxX { get; set; }
            [JsonProperty("maxY")] public double MaxY { get; set; }
        }

        public class Req
        {
            [JsonProperty("viewId")] public long? ViewId { get; set; }
            [JsonProperty("cadName")] public string? CadName { get; set; }
            [JsonProperty("layers")] public List<string>? Layers { get; set; }
            [JsonProperty("region")] public Region? Region { get; set; }
            [JsonProperty("minThicknessMm")] public double MinThicknessMm { get; set; } = 70;
            [JsonProperty("maxThicknessMm")] public double MaxThicknessMm { get; set; } = 650;
            [JsonProperty("angleTolDeg")] public double AngleTolDeg { get; set; } = 1.0;
            [JsonProperty("matchTolMm")] public double MatchTolMm { get; set; } = 300;
            [JsonProperty("minLengthMm")] public double MinLengthMm { get; set; } = 300;
            [JsonProperty("maxResults")] public int MaxResults { get; set; } = 200;
            [JsonProperty("apply")] public bool Apply { get; set; } = false;
            [JsonProperty("wallTypeName")] public string? WallTypeName { get; set; }
            [JsonProperty("heightMm")] public double? HeightMm { get; set; }
        }
    }
}
