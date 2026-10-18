// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMcpAddin.Models;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 地界線（替代元素）。
    /// Revit 2024 API 的 PropertyLine 沒有公開的 Create 方法，無法用程式建立原生地界線。
    /// 這裡改用「地界線」線型的模型線畫出封閉邊界，並組成模型群組，
    /// 面積／周長寫入每條線與群組的「註解」。
    /// 邊界來源二擇一：
    ///   - points：頂點座標（mm，專案內部座標）
    ///   - cadLink + layer：從已連結/匯入的 CAD 取指定圖層上最長的封閉聚合線
    /// apply=false 只回報面積、周長、是否自相交；apply=true 才建立。
    /// replace=true 時先刪除同名舊地界線（依註解前綴「地界線:名稱|」比對）。
    /// </summary>
    public class PropertyLineAltHandler
    {
        private readonly Document _doc;
        public PropertyLineAltHandler(Document doc) => _doc = doc;

        private const double MmPerFt = 304.8;

        public string Create(string body)
        {
            var req = JsonConvert.DeserializeObject<PropertyLineAltRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");
            string name      = string.IsNullOrWhiteSpace(req.Name) ? "基地" : req.Name!.Trim();
            string styleName = string.IsNullOrWhiteSpace(req.LineStyleName) ? "地界線" : req.LineStyleName!.Trim();
            double elevFt    = req.ElevationMm / MmPerFt;
            string source;

            // 1) 取得頂點（feet，XY）
            List<XYZ> pts;
            if (req.Points != null && req.Points.Count >= 3)
            {
                pts = req.Points.Select(p =>
                {
                    if (p == null || p.Length < 2) throw new ArgumentException("每個頂點需為 [x, y]（mm）。");
                    return new XYZ(p[0] / MmPerFt, p[1] / MmPerFt, 0);
                }).ToList();
                source = "points";
            }
            else if (!string.IsNullOrWhiteSpace(req.Layer))
            {
                pts = PointsFromCad(req.CadLink, req.Layer!, out source);
            }
            else
                return Fail("請提供 points（≥3 個頂點，mm）或 layer（CAD 圖層名稱）。");

            // 去除與起點重複的收尾點、連續重複點
            double tol = _doc.Application.ShortCurveTolerance;
            var clean = new List<XYZ>();
            foreach (var p in pts)
                if (clean.Count == 0 || clean[clean.Count - 1].DistanceTo(p) > tol) clean.Add(p);
            if (clean.Count > 1 && clean[0].DistanceTo(clean[clean.Count - 1]) <= tol) clean.RemoveAt(clean.Count - 1);
            if (clean.Count < 3) return Fail("有效頂點不足 3 個。");

            // 2) 幾何檢查
            double areaFt2 = 0, perimFt = 0;
            for (int i = 0; i < clean.Count; i++)
            {
                var a = clean[i]; var b = clean[(i + 1) % clean.Count];
                areaFt2 += a.X * b.Y - b.X * a.Y;
                perimFt += a.DistanceTo(b);
            }
            double areaM2 = Math.Abs(areaFt2) / 2.0 * (0.3048 * 0.3048);
            double perimM = perimFt * 0.3048;
            var crossings = SelfCrossings(clean);

            var result = new PropertyLineAltResult
            {
                Applied          = false,
                Name             = name,
                Source           = source,
                LineStyle        = styleName,
                VertexCount      = clean.Count,
                AreaM2           = Math.Round(areaM2, 2),
                PerimeterM       = Math.Round(perimM, 2),
                SelfIntersecting = crossings.Count > 0,
                Crossings        = crossings,
                VerticesMm       = clean.Select(p => new[] { Math.Round(p.X * MmPerFt, 1), Math.Round(p.Y * MmPerFt, 1) }).ToList(),
            };
            if (!req.Apply) return JsonConvert.SerializeObject(ApiResponse<PropertyLineAltResult>.Ok(result));
            if (crossings.Count > 0) return Fail("邊界自相交，未建立。請先修正頂點順序。");

            string tag     = $"地界線:{name}|";
            string comment = $"{tag}面積={areaM2:0.00}㎡|周長={perimM:0.00}m";

            using var tx = new Transaction(_doc, $"MCP 地界線（替代）{name}");
            tx.Start();
            SwallowAll(tx);

            // 3) 刪除同名舊地界線
            if (req.Replace)
            {
                // 先找出舊群組：註解帶 tag，或群組類型名稱以「地界線_名稱」開頭
                string gprefix = $"地界線_{name}";
                var oldGroups = new FilteredElementCollector(_doc).OfClass(typeof(Group)).Cast<Group>()
                    .Where(g => (g.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "").StartsWith(tag)
                             || (g.GroupType?.Name ?? "").StartsWith(gprefix))
                    .ToList();
                var oldIds = new HashSet<ElementId>();
                foreach (var g in oldGroups)
                {
                    foreach (var mid in g.GetMemberIds()) oldIds.Add(mid);
                    try { g.UngroupMembers(); } catch { }
                }
                // 解組後再收集散落的舊線（含先前解組殘留）
                foreach (var e in new FilteredElementCollector(_doc).OfClass(typeof(CurveElement)))
                {
                    if ((e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "").StartsWith(tag)) { oldIds.Add(e.Id); continue; }
                    if (e is ModelCurve mc0 && mc0.GroupId == ElementId.InvalidElementId && (mc0.LineStyle?.Name ?? "") == styleName) oldIds.Add(e.Id);
                }
                var del = oldIds.Where(id => _doc.GetElement(id) != null).ToList();
                if (del.Count > 0) _doc.Delete(del);
                result.Replaced = del.Count;
                // 清掉已無實體的同名群組類型，讓新群組沿用「地界線_名稱」
                var usedTypes = new HashSet<ElementId>(new FilteredElementCollector(_doc).OfClass(typeof(Group)).Cast<Group>().Select(g => g.GroupType.Id));
                var deadTypes = new FilteredElementCollector(_doc).OfClass(typeof(GroupType)).Cast<GroupType>()
                    .Where(t => t.Name.StartsWith(gprefix) && !usedTypes.Contains(t.Id)).Select(t => t.Id).ToList();
                if (deadTypes.Count > 0) { try { _doc.Delete(deadTypes); } catch { } }
            }

            // 4) 線型
            var gs = EnsureLineStyle(styleName, req.ColorRgb, req.LineWeight);

            // 5) 模型線
            var sp  = SketchPlane.Create(_doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, elevFt)));
            var ids = new List<ElementId>();
            for (int i = 0; i < clean.Count; i++)
            {
                var a = clean[i]; var b = clean[(i + 1) % clean.Count];
                var ln = Line.CreateBound(new XYZ(a.X, a.Y, elevFt), new XYZ(b.X, b.Y, elevFt));
                var mc = _doc.Create.NewModelCurve(ln, sp);
                if (gs != null) mc.LineStyle = gs;
                mc.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(comment);
                ids.Add(mc.Id);
            }
            result.CurveIds = ids.Select(i => (long)i.IntegerValue).ToList();

            // 6) 模型群組
            if (req.Group)
            {
                try
                {
                    var grp = _doc.Create.NewGroup(ids);
                    grp.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(comment);
                    string gname = $"地界線_{name}";
                    var taken = new HashSet<string>(new FilteredElementCollector(_doc).OfClass(typeof(GroupType)).Select(t => t.Name));
                    string n2 = gname; int k = 2;
                    while (taken.Contains(n2)) n2 = $"{gname}_{k++}";
                    grp.GroupType.Name = n2;
                    result.GroupId   = grp.Id.IntegerValue;
                    result.GroupName = n2;
                }
                catch (Exception ex) { result.Warnings.Add("群組建立失敗：" + ex.Message); }
            }

            tx.Commit();
            result.Applied = true;
            return JsonConvert.SerializeObject(ApiResponse<PropertyLineAltResult>.Ok(result));
        }

        // ── CAD 圖層 → 頂點 ─────────────────────────────────────
        private List<XYZ> PointsFromCad(string? cadLink, string layer, out string source)
        {
            var imports = new FilteredElementCollector(_doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
            if (!string.IsNullOrWhiteSpace(cadLink))
                imports = imports.Where(i => (i.Name ?? "").IndexOf(cadLink!, StringComparison.OrdinalIgnoreCase) >= 0
                    || (_doc.GetElement(i.GetTypeId())?.Name ?? "").IndexOf(cadLink!, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (imports.Count == 0) throw new ArgumentException($"找不到 CAD 連結/匯入{(string.IsNullOrWhiteSpace(cadLink) ? "" : $"（{cadLink}）")}。");

            var opt = new Options { ComputeReferences = false, IncludeNonVisibleObjects = true };
            List<XYZ>? best = null; double bestLen = 0; string bestSrc = "";
            var loose = new List<Line>();
            foreach (var imp in imports)
            {
                var ge = imp.get_Geometry(opt);
                if (ge == null) continue;
                foreach (var go in ge)
                {
                    if (go is not GeometryInstance gi) continue;
                    foreach (var o in gi.GetInstanceGeometry())
                    {
                        var st = _doc.GetElement(o.GraphicsStyleId) as GraphicsStyle;
                        if (!string.Equals(st?.GraphicsStyleCategory?.Name, layer, StringComparison.OrdinalIgnoreCase)) continue;
                        if (o is PolyLine pl)
                        {
                            var c = pl.GetCoordinates().ToList();
                            double len = 0; for (int i = 1; i < c.Count; i++) len += c[i - 1].DistanceTo(c[i]);
                            bool closed = c.Count > 3 && c[0].DistanceTo(c[c.Count - 1]) < 1e-3;
                            if (closed && len > bestLen) { best = c; bestLen = len; bestSrc = $"cad:{_doc.GetElement(imp.GetTypeId())?.Name}/{layer}"; }
                        }
                        else if (o is Line l) loose.Add(l);
                    }
                }
            }
            if (best == null && loose.Count >= 3)
            {
                best = ChainLines(loose);
                bestSrc = $"cad:{layer}（以 {loose.Count} 條直線串接）";
            }
            if (best == null) throw new ArgumentException($"圖層「{layer}」上找不到封閉聚合線或可串接的直線。");
            source = bestSrc;
            return best.Select(p => new XYZ(p.X, p.Y, 0)).ToList();
        }

        private static List<XYZ>? ChainLines(List<Line> lines)
        {
            const double tol = 0.01; // ft
            var rest = new List<Line>(lines);
            var first = rest[0]; rest.RemoveAt(0);
            var chain = new List<XYZ> { first.GetEndPoint(0), first.GetEndPoint(1) };
            bool progress = true;
            while (rest.Count > 0 && progress)
            {
                progress = false;
                var tail = chain[chain.Count - 1];
                for (int i = 0; i < rest.Count; i++)
                {
                    var a = rest[i].GetEndPoint(0); var b = rest[i].GetEndPoint(1);
                    if (a.DistanceTo(tail) < tol) { chain.Add(b); rest.RemoveAt(i); progress = true; break; }
                    if (b.DistanceTo(tail) < tol) { chain.Add(a); rest.RemoveAt(i); progress = true; break; }
                }
            }
            return chain[0].DistanceTo(chain[chain.Count - 1]) < tol ? chain : null;
        }

        // ── 線型 ──────────────────────────────────────────────
        private GraphicsStyle? EnsureLineStyle(string styleName, int[]? rgb, int? weight)
        {
            var lines = _doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            Category? sub = null;
            foreach (Category c in lines.SubCategories) if (c.Name == styleName) { sub = c; break; }
            if (sub == null)
            {
                sub = _doc.Settings.Categories.NewSubcategory(lines, styleName);
                var col = rgb != null && rgb.Length == 3 ? rgb : new[] { 255, 0, 0 };
                sub.LineColor = new Color((byte)col[0], (byte)col[1], (byte)col[2]);
                sub.SetLineWeight(weight ?? 5, GraphicsStyleType.Projection);
                var pat = new FilteredElementCollector(_doc).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>()
                    .FirstOrDefault(p => p.Name.IndexOf("dash dot", StringComparison.OrdinalIgnoreCase) >= 0
                                      || p.Name.Contains("一點") || p.Name.Contains("中心"));
                if (pat != null) sub.SetLinePatternId(pat.Id, GraphicsStyleType.Projection);
            }
            return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
        }

        // ── 自相交（非相鄰邊 proper crossing）─────────────────
        private static List<int[]> SelfCrossings(List<XYZ> p)
        {
            static double Cr(XYZ a, XYZ b, XYZ c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            var res = new List<int[]>();
            int n = p.Count;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (j == i + 1 || (i == 0 && j == n - 1)) continue;
                    XYZ a = p[i], b = p[(i + 1) % n], c = p[j], d = p[(j + 1) % n];
                    double d1 = Cr(c, d, a), d2 = Cr(c, d, b), d3 = Cr(a, b, c), d4 = Cr(a, b, d);
                    if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
                        res.Add(new[] { i, j });
                }
            return res;
        }

        private static void SwallowAll(Transaction tx)
        {
            var o = tx.GetFailureHandlingOptions();
            o.SetFailuresPreprocessor(new WarningSwallower());
            tx.SetFailureHandlingOptions(o);
        }

        private class WarningSwallower : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                foreach (var f in fa.GetFailureMessages())
                    if (f.GetSeverity() == FailureSeverity.Warning) fa.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }

        private static string Fail(string msg) =>
            JsonConvert.SerializeObject(ApiResponse<PropertyLineAltResult>.Fail(msg));
    }

    public class PropertyLineAltRequest
    {
        [JsonProperty("points")]        public List<double[]>? Points { get; set; }
        [JsonProperty("cadLink")]       public string? CadLink { get; set; }
        [JsonProperty("layer")]         public string? Layer { get; set; }
        [JsonProperty("name")]          public string? Name { get; set; }
        [JsonProperty("lineStyleName")] public string? LineStyleName { get; set; }
        [JsonProperty("colorRgb")]      public int[]? ColorRgb { get; set; }
        [JsonProperty("lineWeight")]    public int? LineWeight { get; set; }
        [JsonProperty("elevationMm")]   public double ElevationMm { get; set; }
        [JsonProperty("group")]         public bool Group { get; set; } = true;
        [JsonProperty("replace")]       public bool Replace { get; set; } = true;
        [JsonProperty("apply")]         public bool Apply { get; set; }
    }

    public class PropertyLineAltResult
    {
        [JsonProperty("applied")]          public bool Applied { get; set; }
        [JsonProperty("name")]             public string Name { get; set; } = "";
        [JsonProperty("source")]           public string Source { get; set; } = "";
        [JsonProperty("lineStyle")]        public string LineStyle { get; set; } = "";
        [JsonProperty("vertexCount")]      public int VertexCount { get; set; }
        [JsonProperty("areaM2")]           public double AreaM2 { get; set; }
        [JsonProperty("perimeterM")]       public double PerimeterM { get; set; }
        [JsonProperty("selfIntersecting")] public bool SelfIntersecting { get; set; }
        [JsonProperty("crossings")]        public List<int[]> Crossings { get; set; } = new();
        [JsonProperty("verticesMm")]       public List<double[]> VerticesMm { get; set; } = new();
        [JsonProperty("replaced")]         public int Replaced { get; set; }
        [JsonProperty("curveIds")]         public List<long> CurveIds { get; set; } = new();
        [JsonProperty("groupId")]          public long GroupId { get; set; }
        [JsonProperty("groupName")]        public string? GroupName { get; set; }
        [JsonProperty("warnings")]         public List<string> Warnings { get; set; } = new();
    }
}
