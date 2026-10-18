// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMcpAddin.Models;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// POST /mass/boxes：以「公制量體」族群建立矩形量體（每個 box 一個族群），放在專案原點，
    /// 建立／沿用指定樓層並加上量體樓板，回傳各量體樓板面積。座標 mm（專案內部座標）。
    /// replace=true 會先刪除同名族群（含實體）。
    /// </summary>
    public class MassBoxHandler
    {
        private readonly UIApplication _uiApp;
        public MassBoxHandler(UIApplication uiApp) => _uiApp = uiApp;
        private static string Ok(object o) => JsonConvert.SerializeObject(ApiResponse<object>.Ok(o));
        const double MM = 1.0 / 304.8;

        private Document Resolve(string? key)
        {
            var docs = _uiApp.Application.Documents.Cast<Document>().Where(d => !d.IsLinked && !d.IsFamilyDocument).ToList();
            if (string.IsNullOrWhiteSpace(key)) return _uiApp.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No active Revit document.");
            return docs.FirstOrDefault(d => (d.Title ?? "").IndexOf(key!, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? throw new ArgumentException("找不到開啟中的文件：" + key);
        }

        private string FindTemplate()
        {
            var root = _uiApp.Application.FamilyTemplatePath;
            var cands = new List<string>();
            foreach (var dir in new[] { root, System.IO.Path.GetDirectoryName(root) ?? root })
            {
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) continue;
                cands.AddRange(System.IO.Directory.GetFiles(dir, "公制量體.rft", System.IO.SearchOption.AllDirectories));
                cands.AddRange(System.IO.Directory.GetFiles(dir, "Metric Mass.rft", System.IO.SearchOption.AllDirectories));
            }
            var p = @"C:\ProgramData\Autodesk\RVT " + _uiApp.Application.VersionNumber + @"\Family Templates\Traditional Chinese\概念量體\公制量體.rft";
            if (System.IO.File.Exists(p)) cands.Insert(0, p);
            return cands.FirstOrDefault() ?? throw new InvalidOperationException("找不到公制量體.rft");
        }

        public string Boxes(string body)
        {
            var req = JsonConvert.DeserializeObject<Req>(body ?? "{}") ?? new Req();
            var doc = Resolve(req.Document);
            if (req.Boxes == null || req.Boxes.Count == 0) throw new ArgumentException("boxes 必填");
            string tpl = FindTemplate();
            string outDir = string.IsNullOrWhiteSpace(req.FamilyFolder) ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp_mass") : req.FamilyFolder!;
            System.IO.Directory.CreateDirectory(outDir);
            var notes = new List<string>();

            // 1) 族群檔
            var famPaths = new List<(Box box, string path)>();
            foreach (var b in req.Boxes)
            {
                var fdoc = _uiApp.Application.NewFamilyDocument(tpl);
                using (var t = new Transaction(fdoc, "量體"))
                {
                    t.Start();
                    var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
                    var sp = SketchPlane.Create(fdoc, plane);
                    var pts = (b.Loop != null && b.Loop.Count >= 3)
                        ? b.Loop.Select(p => new XYZ(p[0] * MM, p[1] * MM, 0)).ToList()
                        : new List<XYZ> { new XYZ(b.XMin * MM, b.YMin * MM, 0), new XYZ(b.XMax * MM, b.YMin * MM, 0), new XYZ(b.XMax * MM, b.YMax * MM, 0), new XYZ(b.XMin * MM, b.YMax * MM, 0) };
                    var ra = new ReferenceArray();
                    for (int i = 0; i < pts.Count; i++)
                    {
                        var mc = fdoc.FamilyCreate.NewModelCurve(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]), sp);
                        ra.Append(mc.GeometryCurve.Reference);
                    }
                    fdoc.FamilyCreate.NewExtrusionForm(true, ra, new XYZ(0, 0, (b.ZMax - b.ZMin) * MM));
                    t.Commit();
                }
                string path = System.IO.Path.Combine(outDir, b.Name + ".rfa");
                fdoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
                fdoc.Close(false);
                famPaths.Add((b, path));
            }

            // 2) 專案：刪舊、建樓層、載入、放置、量體樓板
            var results = new List<object>();
            var made = new List<(string name, ElementId id)>();
            using (var t = new Transaction(doc, "MCP 量體"))
            {
                var o = t.GetFailureHandlingOptions(); o.SetForcedModalHandling(false); o.SetFailuresPreprocessor(new Swallow()); t.SetFailureHandlingOptions(o);
                t.Start();
                if (req.Replace)
                {
                    var names = new HashSet<string>(req.Boxes.Select(x => x.Name));
                    var old = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Where(f => names.Contains(f.Name)).Select(f => f.Id).ToList();
                    if (old.Count > 0) { doc.Delete(old); notes.Add($"刪除舊量體族群 {old.Count}"); }
                }
                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                var lvMap = new Dictionary<string, Level>();
                foreach (var lv in req.Levels ?? new List<Lv>())
                {
                    var ex = levels.FirstOrDefault(l => l.Name == lv.Name);
                    if (ex == null)
                    {
                        ex = Level.Create(doc, lv.ElevationMm * MM);
                        try { ex.Name = lv.Name; } catch { }
                        levels.Add(ex);
                    }
                    else if (Math.Abs(ex.Elevation - lv.ElevationMm * MM) > 1e-4) ex.Elevation = lv.ElevationMm * MM;
                    lvMap[lv.Name] = ex;
                }
                doc.Regenerate();
                foreach (var (b, path) in famPaths)
                {
                    if (!doc.LoadFamily(path, new LoadOpts(), out Family fam)) throw new InvalidOperationException("載入失敗：" + path);
                    var sym = doc.GetElement(fam.GetFamilySymbolIds().First()) as FamilySymbol ?? throw new InvalidOperationException("無類型");
                    if (!sym.IsActive) sym.Activate();
                    var inst = doc.Create.NewFamilyInstance(new XYZ(0, 0, b.ZMin * MM), sym, StructuralType.NonStructural);
                    inst.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(b.Comment ?? b.Name);
                    doc.Regenerate();
                    int nf = 0;
                    foreach (var ln in b.MassFloors ?? new List<string>())
                    {
                        if (!lvMap.TryGetValue(ln, out var lv)) lv = levels.FirstOrDefault(l => l.Name == ln);
                        if (lv == null) continue;
                        try { MassInstanceUtils.AddMassLevelDataToMassInstance(doc, inst.Id, lv.Id); nf++; } catch (Exception ex) { notes.Add($"{b.Name}@{ln}: {ex.Message}"); }
                    }
                    results.Add(new { name = b.Name, instanceId = inst.Id.IntegerValue, massFloors = nf }); made.Add((b.Name, inst.Id));
                }
                doc.Regenerate();
                t.Commit();
            }

            // 3) 面積
            var floors = new List<object>();
            double total = 0;
            foreach (var r in made)
            {
                var ids = MassInstanceUtils.GetMassLevelDataIds(doc, r.id);
                foreach (var id in ids)
                {
                    var md = doc.GetElement(id);
                    var lv = doc.GetElement(md.LevelId) as Level;
                    var ap = md.get_Parameter(BuiltInParameter.LEVEL_DATA_FLOOR_AREA);
                    double a = ap != null ? ap.AsDouble() * 0.09290304 : 0;
                    total += a;
                    floors.Add(new { mass = r.name, level = lv?.Name, areaM2 = Math.Round(a, 2) });
                }
            }
            return Ok(new { document = doc.Title, template = tpl, familyFolder = outDir, masses = results, floors, totalFloorAreaM2 = Math.Round(total, 2), notes });
        }

        class LoadOpts : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = true; return true; }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues) { source = FamilySource.Family; overwriteParameterValues = true; return true; }
        }
        class Swallow : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                foreach (var f in fa.GetFailureMessages()) if (f.GetSeverity() == FailureSeverity.Warning) fa.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }

        public class Box
        {
            [JsonProperty("name")] public string Name { get; set; } = "量體";
            [JsonProperty("comment")] public string? Comment { get; set; }
            [JsonProperty("xMin")] public double XMin { get; set; }
            [JsonProperty("yMin")] public double YMin { get; set; }
            [JsonProperty("xMax")] public double XMax { get; set; }
            [JsonProperty("yMax")] public double YMax { get; set; }
            [JsonProperty("loop")] public List<double[]>? Loop { get; set; }
            [JsonProperty("zMin")] public double ZMin { get; set; }
            [JsonProperty("zMax")] public double ZMax { get; set; }
            [JsonProperty("massFloors")] public List<string>? MassFloors { get; set; }
        }
        public class Lv { [JsonProperty("name")] public string Name { get; set; } = ""; [JsonProperty("elevationMm")] public double ElevationMm { get; set; } }
        public class Req
        {
            [JsonProperty("document")] public string? Document { get; set; }
            [JsonProperty("boxes")] public List<Box>? Boxes { get; set; }
            [JsonProperty("levels")] public List<Lv>? Levels { get; set; }
            [JsonProperty("familyFolder")] public string? FamilyFolder { get; set; }
            [JsonProperty("replace")] public bool Replace { get; set; } = true;
        }
    }
}
