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
    /// POST /lines/draw：把 CAD 的線（已在外部轉成折線座標）畫成 Revit 模型線。
    ///  body: {document, elevationMm, tag, replace, styles:[{name,rgb,weight,dash}], curves:[{style,pts:[[x,y],..],closed}],
    ///         removeCadLinks:[名稱片段], apply}
    ///  每條線註解「CAD:{tag}|{style}」；replace=true 先刪同 tag 的舊線。
    /// </summary>
    public class CadLinesHandler
    {
        private readonly UIApplication _uiApp;
        public CadLinesHandler(UIApplication uiApp) => _uiApp = uiApp;
        private static string Ok(object o) => JsonConvert.SerializeObject(ApiResponse<object>.Ok(o));
        private const double MM = 1 / 304.8;

        private Document Resolve(string? key)
        {
            var docs = _uiApp.Application.Documents.Cast<Document>().Where(d => !d.IsLinked).ToList();
            if (string.IsNullOrWhiteSpace(key)) return _uiApp.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No active Revit document.");
            return docs.FirstOrDefault(d => (d.Title ?? "").IndexOf(key!, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? throw new ArgumentException("找不到開啟中的文件：" + key);
        }

        private class Swallow : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                foreach (var f in fa.GetFailureMessages()) if (f.GetSeverity() == FailureSeverity.Warning) fa.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }

        public string Draw(string body)
        {
            var req = JsonConvert.DeserializeObject<Req>(body ?? "{}") ?? new Req();
            var doc = Resolve(req.Document);
            string tag = "CAD:" + (string.IsNullOrWhiteSpace(req.Tag) ? "cad" : req.Tag) + "|";
            double z = req.ElevationMm * MM;
            double minLen = doc.Application.ShortCurveTolerance * 1.01;

            int made = 0, skipped = 0, removed = 0, linksRemoved = 0;
            var perStyle = new Dictionary<string, int>();
            var errs = new List<string>();
            if (!req.Apply)
            {
                int segs = (req.Curves ?? new List<Crv>()).Sum(c => Math.Max(0, (c.Pts?.Count ?? 0) - 1 + (c.Closed ? 1 : 0)));
                return Ok(new { applied = false, curves = req.Curves?.Count ?? 0, segments = segs });
            }
            using var t = new Transaction(doc, "MCP CAD 線轉模型線");
            var o = t.GetFailureHandlingOptions(); o.SetFailuresPreprocessor(new Swallow()); o.SetForcedModalHandling(false); t.SetFailureHandlingOptions(o);
            t.Start();
            if (req.Replace)
            {
                var old = new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).WhereElementIsNotElementType()
                    .Where(e => (e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "").StartsWith(tag))
                    .Select(e => e.Id).ToList();
                if (old.Count > 0) { doc.Delete(old); removed = old.Count; }
            }
            var gsMap = new Dictionary<string, GraphicsStyle?>();
            foreach (var s in req.Styles ?? new List<Sty>()) gsMap[s.Name] = EnsureStyle(doc, s);
            var sp = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, z)));
            foreach (var c in req.Curves ?? new List<Crv>())
            {
                if (c.Pts == null || c.Pts.Count < 2) { skipped++; continue; }
                var pts = c.Pts.Select(p => new XYZ(p[0] * MM, p[1] * MM, z)).ToList();
                if (c.Closed && pts[0].DistanceTo(pts[pts.Count - 1]) > minLen) pts.Add(pts[0]);
                gsMap.TryGetValue(c.Style ?? "", out var gs);
                var a = pts[0];
                for (int i = 1; i < pts.Count; i++)
                {
                    var b = pts[i];
                    if (a.DistanceTo(b) < minLen) { skipped++; continue; }
                    try
                    {
                        var mc = doc.Create.NewModelCurve(Line.CreateBound(a, b), sp);
                        if (gs != null) mc.LineStyle = gs;
                        mc.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(tag + (c.Style ?? ""));
                        made++; perStyle[c.Style ?? ""] = perStyle.TryGetValue(c.Style ?? "", out var n) ? n + 1 : 1;
                    }
                    catch (Exception ex) { skipped++; if (errs.Count < 5) errs.Add(ex.Message); }
                    a = b;
                }
            }
            if (req.RemoveCadLinks != null && req.RemoveCadLinks.Count > 0)
            {
                var types = new FilteredElementCollector(doc).OfClass(typeof(CADLinkType)).Cast<CADLinkType>()
                    .Where(ct => req.RemoveCadLinks.Any(k => (ct.Name ?? "").IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                    .Select(ct => ct.Id).ToList();
                if (types.Count > 0) { doc.Delete(types); linksRemoved = types.Count; }
            }
            t.Commit();
            return Ok(new { applied = true, made, skipped, removedOld = removed, linksRemoved, perStyle, errors = errs });
        }

        // ── POST /links/move {document, nameContains, dxMm, dyMm, dzMm, acquireCoordinates} ──
        public string MoveLink(string body)
        {
            var req = JsonConvert.DeserializeObject<MoveReq>(body ?? "{}") ?? new MoveReq();
            var doc = Resolve(req.Document);
            var lis = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .Where(l => string.IsNullOrEmpty(req.NameContains) || (l.Name ?? "").IndexOf(req.NameContains, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (lis.Count != 1) throw new ArgumentException($"符合的連結數 {lis.Count}：" + string.Join(", ", lis.Select(l => l.Name)));
            var li = lis[0];
            var before = li.GetTotalTransform().Origin;
            using (var t = new Transaction(doc, "MCP 移動連結"))
            {
                t.Start();
                bool pinned = li.Pinned; if (pinned) li.Pinned = false;
                ElementTransformUtils.MoveElement(doc, li.Id, new XYZ(req.DxMm * MM, req.DyMm * MM, req.DzMm * MM));
                if (pinned) li.Pinned = true;
                t.Commit();
            }
            if (req.AcquireCoordinates || req.SetElevationM.HasValue)
            {
                using var t2 = new Transaction(doc, "MCP 共用座標");
                t2.Start();
                if (req.AcquireCoordinates) doc.AcquireCoordinates(li.Id);
                if (req.SetElevationM.HasValue)
                {
                    var loc = doc.ActiveProjectLocation; var pp0 = loc.GetProjectPosition(XYZ.Zero);
                    loc.SetProjectPosition(XYZ.Zero, new ProjectPosition(pp0.EastWest, pp0.NorthSouth, req.SetElevationM.Value / 0.3048, pp0.Angle));
                }
                t2.Commit();
            }
            var after = li.GetTotalTransform().Origin;
            var pl = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
            return Ok(new { link = li.Name, beforeMm = new[] { before.X / MM, before.Y / MM, before.Z / MM }, afterMm = new[] { after.X / MM, after.Y / MM, after.Z / MM },
                projectPosition = new { eastWestM = pl.EastWest * 0.3048, northSouthM = pl.NorthSouth * 0.3048, elevationM = pl.Elevation * 0.3048, angleDeg = pl.Angle * 180 / Math.PI } });
        }
        public class MoveReq
        {
            [JsonProperty("document")] public string? Document { get; set; }
            [JsonProperty("nameContains")] public string? NameContains { get; set; }
            [JsonProperty("dxMm")] public double DxMm { get; set; }
            [JsonProperty("dyMm")] public double DyMm { get; set; }
            [JsonProperty("dzMm")] public double DzMm { get; set; }
            [JsonProperty("acquireCoordinates")] public bool AcquireCoordinates { get; set; }
            [JsonProperty("setElevationM")] public double? SetElevationM { get; set; }
        }

        // ── POST /materials/appearance {document, materials:[{name,rgb,transparency,glossiness}]} ──
        // 為材質建立／更新「一般」彩現外觀（漫射色、透明度、光澤），Enscape 與彩現可見
        public string Appearance(string body)
        {
            var req = JsonConvert.DeserializeObject<AppReq>(body ?? "{}") ?? new AppReq();
            var doc = Resolve(req.Document);
            var lib = doc.Application.GetAssets(Autodesk.Revit.DB.Visual.AssetType.Appearance);
            Autodesk.Revit.DB.Visual.Asset? baseAsset =
                lib.FirstOrDefault(a => a.Name == "Generic" && a.FindByName("generic_diffuse") != null)
                ?? lib.FirstOrDefault(a => a.FindByName("generic_diffuse") != null && a.FindByName("generic_transparency") != null);
            if (baseAsset == null) throw new InvalidOperationException("找不到一般（Generic）外觀資產，資產庫數量 " + lib.Count);
            var res = new List<object>();
            using var t = new Transaction(doc, "MCP 材質彩現外觀");
            t.Start();
            foreach (var m in req.Materials ?? new List<AppMat>())
            {
                try
                {
                    var mat = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(x => x.Name == m.Name);
                    if (mat == null) mat = doc.GetElement(Material.Create(doc, m.Name)) as Material;
                    string an = m.Name + "_外觀";
                    var ae = AppearanceAssetElement.GetAppearanceAssetElementByName(doc, an) ?? AppearanceAssetElement.Create(doc, an, baseAsset);
                    var col = m.Rgb != null && m.Rgb.Length == 3 ? new Color((byte)m.Rgb[0], (byte)m.Rgb[1], (byte)m.Rgb[2]) : new Color(200, 200, 200);
                    using (var scope = new Autodesk.Revit.DB.Visual.AppearanceAssetEditScope(doc))
                    {
                        var ea = scope.Start(ae.Id);
                        if (ea.FindByName("generic_diffuse") is Autodesk.Revit.DB.Visual.AssetPropertyDoubleArray4d d)
                        {
                            if (d.NumberOfConnectedProperties > 0) d.RemoveConnectedAsset();
                            d.SetValueAsColor(col);
                        }
                        if (ea.FindByName("generic_transparency") is Autodesk.Revit.DB.Visual.AssetPropertyDouble tr && tr.IsValidValue(m.Transparency / 100.0)) tr.Value = m.Transparency / 100.0;
                        if (m.Glossiness.HasValue && ea.FindByName("generic_glossiness") is Autodesk.Revit.DB.Visual.AssetPropertyDouble g && g.IsValidValue(m.Glossiness.Value)) g.Value = m.Glossiness.Value;
                        scope.Commit(true);
                    }
                    mat!.AppearanceAssetId = ae.Id;
                    mat.Color = col; mat.Transparency = m.Transparency; mat.UseRenderAppearanceForShading = false;
                    res.Add(new { m.Name, ok = true });
                }
                catch (Exception ex) { res.Add(new { m.Name, ok = false, error = ex.Message }); }
            }
            t.Commit();
            return Ok(new { baseAsset = baseAsset.Name, results = res });
        }
        public class AppMat
        {
            [JsonProperty("name")] public string Name { get; set; } = "";
            [JsonProperty("rgb")] public int[]? Rgb { get; set; }
            [JsonProperty("transparency")] public int Transparency { get; set; }
            [JsonProperty("glossiness")] public double? Glossiness { get; set; }
        }
        public class AppReq
        {
            [JsonProperty("document")] public string? Document { get; set; }
            [JsonProperty("materials")] public List<AppMat>? Materials { get; set; }
        }

        private static GraphicsStyle? EnsureStyle(Document doc, Sty s)
        {
            var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            Category? sub = null;
            foreach (Category c in lines.SubCategories) if (c.Name == s.Name) { sub = c; break; }
            if (sub == null) sub = doc.Settings.Categories.NewSubcategory(lines, s.Name);
            var col = s.Rgb != null && s.Rgb.Length == 3 ? s.Rgb : new[] { 128, 128, 128 };
            sub.LineColor = new Color((byte)col[0], (byte)col[1], (byte)col[2]);
            sub.SetLineWeight(s.Weight ?? 1, GraphicsStyleType.Projection);
            if (!string.IsNullOrWhiteSpace(s.Dash))
            {
                var pat = new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>()
                    .FirstOrDefault(p => s.Dash!.Split('|').Any(k => p.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0));
                if (pat != null) sub.SetLinePatternId(pat.Id, GraphicsStyleType.Projection);
            }
            return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
        }

        public class Sty
        {
            [JsonProperty("name")] public string Name { get; set; } = "";
            [JsonProperty("rgb")] public int[]? Rgb { get; set; }
            [JsonProperty("weight")] public int? Weight { get; set; }
            [JsonProperty("dash")] public string? Dash { get; set; }
        }
        public class Crv
        {
            [JsonProperty("style")] public string? Style { get; set; }
            [JsonProperty("pts")] public List<double[]>? Pts { get; set; }
            [JsonProperty("closed")] public bool Closed { get; set; }
        }
        public class Req
        {
            [JsonProperty("document")] public string? Document { get; set; }
            [JsonProperty("elevationMm")] public double ElevationMm { get; set; }
            [JsonProperty("tag")] public string? Tag { get; set; }
            [JsonProperty("replace")] public bool Replace { get; set; } = true;
            [JsonProperty("styles")] public List<Sty>? Styles { get; set; }
            [JsonProperty("curves")] public List<Crv>? Curves { get; set; }
            [JsonProperty("removeCadLinks")] public List<string>? RemoveCadLinks { get; set; }
            [JsonProperty("apply")] public bool Apply { get; set; }
        }
    }
}
