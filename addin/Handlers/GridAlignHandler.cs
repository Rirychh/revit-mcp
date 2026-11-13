// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMcpAddin.Models;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// POST /grids/align-to-cad：把直線網格對齊到平面視圖中 DWG（連結或匯入）的網格線。
    /// 每條網格找方向差 ≤ maxAngleDeg、垂直距離 ≤ maxOffsetMm、長度 ≥ minLengthMm 的最近 CAD 線，
    /// 以網格起點為軸旋轉後再平移到 CAD 線上（網格長度不變）。apply=false 只回報每條的旋轉角與偏移。
    /// </summary>
    public class GridAlignHandler
    {
        private readonly Document _doc;
        public GridAlignHandler(Document doc) => _doc = doc;
        private static string Ok(object o) => JsonConvert.SerializeObject(ApiResponse<object>.Ok(o));
        private const double MM = 304.8;

        public class Req
        {
            [JsonProperty("viewId")] public long ViewId { get; set; }
            [JsonProperty("cadName")] public string? CadName { get; set; }
            [JsonProperty("layers")] public List<string>? Layers { get; set; }
            [JsonProperty("gridNames")] public List<string>? GridNames { get; set; }
            [JsonProperty("maxAngleDeg")] public double MaxAngleDeg { get; set; } = 5;
            [JsonProperty("maxOffsetMm")] public double MaxOffsetMm { get; set; } = 5000;
            [JsonProperty("minLengthMm")] public double MinLengthMm { get; set; } = 3000;
            [JsonProperty("toleranceMm")] public double ToleranceMm { get; set; } = 1;
            [JsonProperty("apply")] public bool Apply { get; set; }
        }

        private sealed class CadLine
        {
            public XYZ A { get; } public XYZ B { get; } public string Layer { get; }
            public CadLine(XYZ a, XYZ b, string layer) { A = a; B = b; Layer = layer; }
        }

        public string Align(string body)
        {
            var req = JsonConvert.DeserializeObject<Req>(body ?? "{}") ?? new Req();
            var view = _doc.GetElement(ElementIdCompat.NewId(req.ViewId)) as View
                       ?? throw new ArgumentException("viewId 不是視圖。");

            // 1) 收集 CAD 線（模型座標）
            var cads = new FilteredElementCollector(_doc, view.Id).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                .Where(i => string.IsNullOrWhiteSpace(req.CadName)
                         || (_doc.GetElement(i.GetTypeId())?.Name ?? "").IndexOf(req.CadName!, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (cads.Count == 0) throw new InvalidOperationException("這個視圖裡找不到 DWG（可用 cadName 指定）。");
            var lines = new List<CadLine>();
            var opt = new Options { View = view };
            double minLen = req.MinLengthMm / MM;
            foreach (var ci in cads)
                foreach (var g in ci.get_Geometry(opt) ?? Enumerable.Empty<GeometryObject>())
                    if (g is GeometryInstance gi)
                        Walk(gi.GetInstanceGeometry(), lines, req.Layers, minLen);
            if (lines.Count == 0) throw new InvalidOperationException("DWG 裡沒有符合條件的直線（檢查 layers／minLengthMm）。");

            // 2) 網格
            var grids = new FilteredElementCollector(_doc).OfClass(typeof(Grid)).Cast<Grid>()
                .Where(g => req.GridNames == null || req.GridNames.Count == 0 || req.GridNames.Contains(g.Name)).ToList();

            var plans = new List<(Grid g, double ang, XYZ pivot, XYZ move, double offMm, double angDeg, string layer, bool ambiguous)>();
            var report = new List<object>();
            double maxAng = req.MaxAngleDeg * Math.PI / 180, maxOff = req.MaxOffsetMm / MM, tol = req.ToleranceMm / MM;
            foreach (var g in grids)
            {
                if (g.Curve is not Line gl) { report.Add(new { grid = g.Name, id = g.Id.IntegerValue, status = "非直線網格，略過" }); continue; }
                var p0 = Flat(gl.GetEndPoint(0)); var d = Flat(gl.Direction).Normalize();
                var cands = new List<(CadLine c, double ang, double off)>();
                foreach (var c in lines)
                {
                    var cd = Flat(c.B - c.A); if (cd.GetLength() < 1e-9) continue; cd = cd.Normalize();
                    if (cd.DotProduct(d) < 0) cd = cd.Negate();
                    double ang = Math.Atan2(d.CrossProduct(cd).Z, d.DotProduct(cd));
                    if (Math.Abs(ang) > maxAng) continue;
                    var mid = Flat((gl.GetEndPoint(0) + gl.GetEndPoint(1)) / 2);
                    double off = PerpDist(mid, Flat(c.A), cd);
                    if (off > maxOff) continue;
                    cands.Add((c, ang, off));
                }
                if (cands.Count == 0) { report.Add(new { grid = g.Name, id = g.Id.IntegerValue, status = "找不到對應 CAD 線" }); continue; }
                cands = cands.OrderBy(x => x.off).ToList();
                var best = cands[0];
                bool amb = cands.Count > 1 && cands[1].off - best.off < 500 / MM
                           && Math.Abs(cands[1].off - best.off) > tol;
                // 旋轉後網格方向 = CAD 方向；平移量 = 旋轉後網格線到 CAD 線的垂直向量
                var cdir = Flat(best.c.B - best.c.A).Normalize(); if (cdir.DotProduct(d) < 0) cdir = cdir.Negate();
                var v = Flat(best.c.A) - p0; var move = v - cdir * v.DotProduct(cdir);
                double offMm = move.GetLength() * MM, angDeg = best.ang * 180 / Math.PI;
                bool already = move.GetLength() <= tol && Math.Abs(best.ang) < 1e-6;
                report.Add(new
                {
                    grid = g.Name, id = g.Id.IntegerValue, pinned = g.Pinned,
                    rotateDeg = Math.Round(angDeg, 4), offsetMm = Math.Round(offMm, 1),
                    cadLayer = best.c.Layer, ambiguous = amb,
                    status = already ? "已對齊" : (amb ? "有兩條相近 CAD 線，請確認" : "將調整")
                });
                if (!already && !amb) plans.Add((g, best.ang, p0, move, offMm, angDeg, best.c.Layer, amb));
            }

            if (!req.Apply)
                return Ok(new { preflight = true, view = view.Name, cadLines = lines.Count, willAdjust = plans.Count, grids = report,
                                note = "apply=true 才會調整；ambiguous 的網格不會動。" });

            var done = new List<string>(); var skipped = new List<object>();
            using (var t = new Transaction(_doc, "MCP: 網格依 CAD 對齊"))
            {
                t.Start();
                foreach (var p in plans)
                {
                    bool wasPinned = p.g.Pinned;
                    if (wasPinned) p.g.Pinned = false;
                    if (Math.Abs(p.ang) > 1e-9)
                        ElementTransformUtils.RotateElement(_doc, p.g.Id, Line.CreateBound(p.pivot, p.pivot + XYZ.BasisZ), p.ang);
                    if (p.move.GetLength() > 1e-9) ElementTransformUtils.MoveElement(_doc, p.g.Id, p.move);
                    if (wasPinned) p.g.Pinned = true;
                    done.Add(p.g.Name);
                }
                t.Commit();
            }
            return Ok(new { applied = true, adjusted = done, grids = report });
        }

        private static void Walk(GeometryElement ge, List<CadLine> acc, List<string>? layers, double minLen)
        {
            foreach (var o in ge)
            {
                if (o is GeometryInstance gi) { Walk(gi.GetInstanceGeometry(), acc, layers, minLen); continue; }
                string layer = "";
                try
                {
                    if (o.GraphicsStyleId != ElementId.InvalidElementId)
                        layer = (gi_doc?.GetElement(o.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name ?? "";
                }
                catch { }
                if (layers != null && layers.Count > 0 && !layers.Any(k => layer.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                if (o is Line l && l.Length >= minLen) acc.Add(new CadLine(l.GetEndPoint(0), l.GetEndPoint(1), layer));
                else if (o is PolyLine pl)
                {
                    var pts = pl.GetCoordinates();
                    for (int i = 0; i + 1 < pts.Count; i++)
                        if (pts[i].DistanceTo(pts[i + 1]) >= minLen) acc.Add(new CadLine(pts[i], pts[i + 1], layer));
                }
            }
        }

        [ThreadStatic] private static Document? gi_doc;
        private static XYZ Flat(XYZ p) => new XYZ(p.X, p.Y, 0);
        private static double PerpDist(XYZ p, XYZ a, XYZ dirUnit)
        {
            var v = p - a; var along = dirUnit * v.DotProduct(dirUnit); return (v - along).GetLength();
        }

        public string Run(string body) { gi_doc = _doc; try { return Align(body); } finally { gi_doc = null; } }
    }
}
