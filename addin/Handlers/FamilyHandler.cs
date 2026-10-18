// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMcpAddin.Models;
using WM = System.Windows.Media;
using SW = System.Windows;

namespace RevitMcpAddin.Handlers
{
    // ── 請求格式（單位一律 mm）────────────────────────────────────────────
    public class FamilyBuildRequest
    {
        [JsonProperty("template")]        public string? Template { get; set; }
        [JsonProperty("savePath")]        public string SavePath { get; set; } = "";
        [JsonProperty("overwrite")]       public bool Overwrite { get; set; } = true;
        [JsonProperty("typeName")]        public string? TypeName { get; set; }
        [JsonProperty("parameters")]      public List<FamilyParamSpec> Parameters { get; set; } = new();
        [JsonProperty("nestedFamilies")]  public List<string> NestedFamilies { get; set; } = new();
        [JsonProperty("elements")]        public List<FamilyElementSpec> Elements { get; set; } = new();
        [JsonProperty("loadIntoProject")] public bool LoadIntoProject { get; set; } = false;
        /// <summary>刪除樣板自帶的說明文字</summary>
        [JsonProperty("cleanTemplate")]   public bool CleanTemplate { get; set; } = true;
        /// <summary>以既有 .rfa 為基礎（而非樣板）建立</summary>
        [JsonProperty("cloneFrom")]       public string? CloneFrom { get; set; }
        /// <summary>以目前專案中的族為基礎建立（EditFamily）</summary>
        [JsonProperty("cloneFromProjectFamily")] public string? CloneFromProjectFamily { get; set; }
        /// <summary>只保留文字相符的那個標籤，其餘標籤／線／填滿區域／巢狀實例一律刪除</summary>
        [JsonProperty("keepOnlyLabelText")] public string? KeepOnlyLabelText { get; set; }
        /// <summary>族參數改名 {舊:新}</summary>
        [JsonProperty("renameParameters")] public Dictionary<string, string>? RenameParameters { get; set; }
        /// <summary>刪除這些族參數</summary>
        [JsonProperty("removeParameters")] public List<string>? RemoveParameters { get; set; }
        [JsonProperty("labelTextSize")]   public double? LabelTextSize { get; set; }
        [JsonProperty("labelFont")]       public string? LabelFont { get; set; }
        [JsonProperty("labelAt")]         public double[]? LabelAt { get; set; }
        [JsonProperty("labelValue")]      public string? LabelValue { get; set; }
    }

    public class FamilyParamSpec
    {
        [JsonProperty("name")]     public string Name { get; set; } = "";
        /// <summary>yesno | length | text | integer | number | familyType</summary>
        [JsonProperty("kind")]     public string Kind { get; set; } = "text";
        [JsonProperty("instance")] public bool Instance { get; set; } = true;
        [JsonProperty("value")]    public string? Value { get; set; }
        [JsonProperty("formula")]  public string? Formula { get; set; }
        /// <summary>familyType 用：品類，例如 DetailComponents、GenericAnnotation</summary>
        [JsonProperty("category")] public string? Category { get; set; }
    }

    public class FamilyElementSpec
    {
        /// <summary>filledRegion | maskingRegion | glyph | line | nested</summary>
        [JsonProperty("kind")]         public string Kind { get; set; } = "";
        [JsonProperty("loops")]        public List<List<double[]>>? Loops { get; set; }
        /// <summary>[cx, cy, w, h]，每個矩形一個迴圈</summary>
        [JsonProperty("rects")]        public List<double[]>? Rects { get; set; }
        [JsonProperty("color")]        public int[]? Color { get; set; }
        [JsonProperty("text")]         public string? Text { get; set; }
        [JsonProperty("font")]         public string? Font { get; set; }
        [JsonProperty("capHeight")]    public double CapHeight { get; set; } = 350;
        [JsonProperty("center")]       public double[]? Center { get; set; }
        [JsonProperty("points")]       public List<double[]>? Points { get; set; }
        [JsonProperty("closed")]       public bool Closed { get; set; }
        [JsonProperty("family")]       public string? Family { get; set; }
        [JsonProperty("type")]         public string? Type { get; set; }
        [JsonProperty("at")]           public double[]? At { get; set; }
        [JsonProperty("typeParam")]    public string? TypeParam { get; set; }
        [JsonProperty("visibleParam")] public string? VisibleParam { get; set; }
        [JsonProperty("showEdges")]    public bool ShowEdges { get; set; } = false;
        /// <summary>nested：巢狀族實例參數 → 主族參數，例如 {"代號":"代號1"}</summary>
        [JsonProperty("paramMap")]     public Dictionary<string, string>? ParamMap { get; set; }
        /// <summary>label：來源 .rfa 路徑（與 sourceProjectFamily 擇一）</summary>
        [JsonProperty("source")]       public string? Source { get; set; }
        /// <summary>label：目前專案中已載入的族名稱，會以 EditFamily 開啟當來源</summary>
        [JsonProperty("sourceProjectFamily")] public string? SourceProjectFamily { get; set; }
        /// <summary>label：來源標籤的文字（多個標籤時用來挑選），省略取第一個</summary>
        [JsonProperty("sourceLabelText")] public string? SourceLabelText { get; set; }
        /// <summary>label：複製後標籤參數改成的名稱</summary>
        [JsonProperty("param")]        public string? Param { get; set; }
        [JsonProperty("paramInstance")] public bool ParamInstance { get; set; } = true;
        [JsonProperty("paramValue")]   public string? ParamValue { get; set; }
        /// <summary>label：字高（出圖 mm），省略沿用來源</summary>
        [JsonProperty("textSize")]     public double? TextSize { get; set; }
        [JsonProperty("textFont")]     public string? TextFont { get; set; }
    }

    public class LabelScanRequest
    {
        [JsonProperty("paths")]       public List<string> Paths { get; set; } = new();
        [JsonProperty("fromProject")] public bool FromProject { get; set; } = false;
        [JsonProperty("nameContains")] public string? NameContains { get; set; }
        [JsonProperty("max")]         public int Max { get; set; } = 40;
    }

    public class FamilyBuildResult
    {
        [JsonProperty("familyPath")]      public string FamilyPath { get; set; } = "";
        [JsonProperty("familyName")]      public string FamilyName { get; set; } = "";
        [JsonProperty("template")]        public string Template { get; set; } = "";
        [JsonProperty("elementsCreated")] public int ElementsCreated { get; set; }
        [JsonProperty("parameters")]      public List<string> Parameters { get; set; } = new();
        [JsonProperty("loadedInto")]      public string? LoadedInto { get; set; }
        [JsonProperty("warnings")]        public List<string> Warnings { get; set; } = new();
    }

    /// <summary>
    /// 依 JSON 規格建立族檔（.rfa）。
    /// 族文件一律在 finally 關閉；交易用 using，例外時自動回滾，
    /// 避免殘留交易物件讓 Revit 發生嚴重錯誤。
    /// </summary>
    public class FamilyHandler
    {
        private readonly UIApplication _uiApp;
        private readonly List<string> _warn = new();

        public FamilyHandler(UIApplication uiApp) { _uiApp = uiApp; }

        private static double Ft(double mm) => mm / 304.8;
        private static XYZ P(double x, double y) => new XYZ(Ft(x), Ft(y), 0);

        public string Build(string body)
        {
            var req = JsonConvert.DeserializeObject<FamilyBuildRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");
            if (string.IsNullOrWhiteSpace(req.SavePath))
                throw new ArgumentException("savePath is required.");
            if (!req.SavePath.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("savePath must end with .rfa");
            if (File.Exists(req.SavePath) && !req.Overwrite)
                throw new InvalidOperationException($"File exists: {req.SavePath}");

            var app  = _uiApp.Application;
            var tpl  = ResolveTemplate(app, req.Template);
            var proj = _uiApp.ActiveUIDocument?.Document;
            if (proj != null && proj.IsFamilyDocument) proj = null;

            Directory.CreateDirectory(Path.GetDirectoryName(req.SavePath)!);

            var result = new FamilyBuildResult
            {
                FamilyPath = req.SavePath,
                FamilyName = Path.GetFileNameWithoutExtension(req.SavePath),
                Template   = tpl,
            };

            Document? fdoc = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(req.CloneFromProjectFamily))
                {
                    var baseProj = proj ?? throw new InvalidOperationException("cloneFromProjectFamily needs an active project.");
                    var srcFam = new FilteredElementCollector(baseProj).OfClass(typeof(Family)).Cast<Family>()
                                 .FirstOrDefault(x => x.Name == req.CloneFromProjectFamily)
                                 ?? throw new ArgumentException("Family not in project: " + req.CloneFromProjectFamily);
                    fdoc = baseProj.EditFamily(srcFam);
                    result.Template = "clone:" + req.CloneFromProjectFamily;
                }
                else if (!string.IsNullOrWhiteSpace(req.CloneFrom))
                {
                    if (!File.Exists(req.CloneFrom)) throw new FileNotFoundException("cloneFrom not found", req.CloneFrom);
                    fdoc = app.OpenDocumentFile(req.CloneFrom);
                    result.Template = "clone:" + req.CloneFrom;
                }
                else
                {
                    fdoc = app.NewFamilyDocument(tpl)
                           ?? throw new InvalidOperationException("NewFamilyDocument returned null.");
                }

                // 1) 巢狀族（要在交易外載入）
                foreach (var nf in req.NestedFamilies)
                {
                    if (!File.Exists(nf)) throw new FileNotFoundException("Nested family not found", nf);
                    using var tl = new Transaction(fdoc, "MCP Load Nested");
                    tl.Start();
                    if (!fdoc.LoadFamily(nf, new OverwriteLoadOptions(), out _))
                        _warn.Add($"LoadFamily returned false (already loaded?): {nf}");
                    tl.Commit();
                }

                // 2) 主要內容
                using (var tx = new Transaction(fdoc, "MCP Build Family"))
                {
                    var fo = tx.GetFailureHandlingOptions();
                    fo.SetFailuresPreprocessor(new SwallowWarnings());
                    tx.SetFailureHandlingOptions(fo);
                    tx.Start();

                    if (req.CleanTemplate)
                    {
                        var notes = new FilteredElementCollector(fdoc).OfClass(typeof(TextNote)).ToElementIds();
                        if (notes.Count > 0) fdoc.Delete(notes);
                    }
                    var fm = fdoc.FamilyManager;
                    var typeName = req.TypeName ?? "標準";
                    if (fm.CurrentType == null) fm.NewType(typeName);
                    else if (req.TypeName != null && fm.CurrentType.Name != typeName)
                        fm.RenameCurrentType(typeName);

                    // ── 複製來源族的改造：留一個標籤、改參數名、調字高 ──
                    if (req.KeepOnlyLabelText != null || req.RenameParameters != null ||
                        req.RemoveParameters != null || req.LabelTextSize != null ||
                        req.LabelAt != null || req.LabelValue != null)
                    {
                        var labels = new FilteredElementCollector(fdoc).WhereElementIsNotElementType()
                                     .Where(IsLabel).Cast<TextElement>().ToList();
                        TextElement? keep = labels.FirstOrDefault();
                        if (req.KeepOnlyLabelText != null && labels.Count > 0)
                        {
                            var want = req.KeepOnlyLabelText.Trim();
                            keep = labels.FirstOrDefault(l => (l.Text ?? "").Trim() == want) ?? labels[0];
                            var kill = new List<ElementId>();
                            foreach (var e in new FilteredElementCollector(fdoc).WhereElementIsNotElementType())
                            {
                                if (e.Id == keep.Id) continue;
                                if (e is TextElement || e is TextNote || e is FilledRegion ||
                                    e is CurveElement || e is FamilyInstance) kill.Add(e.Id);
                            }
                            if (kill.Count > 0)
                                try { fdoc.Delete(kill); } catch (Exception ex) { _warn.Add("clean clone: " + ex.Message); }
                        }

                        if (req.RemoveParameters != null)
                            foreach (var n in req.RemoveParameters)
                            {
                                var rp = fm.get_Parameter(n);
                                if (rp == null) { _warn.Add("removeParameters: not found " + n); continue; }
                                try { fm.RemoveParameter(rp); } catch (Exception ex) { _warn.Add("removeParameter " + n + ": " + ex.Message); }
                            }

                        if (req.RenameParameters != null)
                            foreach (var kv in req.RenameParameters)
                            {
                                var rp = fm.get_Parameter(kv.Key);
                                if (rp == null) { _warn.Add("renameParameters: not found " + kv.Key); continue; }
                                try { fm.RenameParameter(rp, kv.Value); } catch (Exception ex) { _warn.Add("rename " + kv.Key + ": " + ex.Message); }
                            }

                        if (keep != null)
                        {
                            try { keep.HorizontalAlignment = HorizontalTextAlignment.Center; } catch { }
                            try { keep.VerticalAlignment = VerticalTextAlignment.Middle; } catch { }
                            if (req.LabelAt != null)
                                try { keep.Coord = P(req.LabelAt[0], req.LabelAt[1]); } catch (Exception ex) { _warn.Add("labelAt: " + ex.Message); }
                            if (req.LabelTextSize != null || req.LabelFont != null)
                            {
                                var tt = keep.Symbol;
                                var nm = "MCP_標籤_" + (req.LabelTextSize ?? 0).ToString("0.##") + "mm";
                                var ex0 = new FilteredElementCollector(fdoc).OfClass(typeof(TextElementType))
                                          .Cast<TextElementType>().FirstOrDefault(x => x.Name == nm);
                                var nt = ex0 ?? (TextElementType)tt.Duplicate(nm);
                                if (req.LabelTextSize != null) nt.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(Ft(req.LabelTextSize.Value));
                                if (req.LabelFont != null) nt.get_Parameter(BuiltInParameter.TEXT_FONT)?.Set(req.LabelFont);
                                keep.ChangeTypeId(nt.Id);
                            }
                        }

                        if (req.LabelValue != null)
                        {
                            var sp = fm.GetParameters().FirstOrDefault(p => p.StorageType == StorageType.String);
                            if (sp != null)
                                try { fm.Set(sp, req.LabelValue); } catch (Exception ex) { _warn.Add("labelValue: " + ex.Message); }
                        }
                    }

                    var fparams = new Dictionary<string, FamilyParameter>();
                    foreach (var ps in req.Parameters)
                        fparams[ps.Name] = AddParam(fdoc, fm, ps);

                    // 一般參數的值
                    foreach (var ps in req.Parameters)
                        if (ps.Value != null && !IsFamilyType(ps) && ps.Formula == null)
                            SetValue(fm, fparams[ps.Name], ps);

                    var view = WorkView(fdoc);
                    int count = 0;
                    foreach (var es in req.Elements)
                        count += CreateElement(fdoc, proj, fm, view, es, fparams);
                    result.ElementsCreated = count;

                    // 族類型參數的預設值（巢狀族已載入後才能設）
                    foreach (var ps in req.Parameters)
                        if (ps.Value != null && IsFamilyType(ps))
                            fm.Set(fparams[ps.Name], FindSymbol(fdoc, ps.Value, null).Id);

                    // 公式最後設
                    foreach (var ps in req.Parameters)
                        if (!string.IsNullOrEmpty(ps.Formula))
                            fm.SetFormula(fparams[ps.Name], ps.Formula);

                    result.Parameters = fparams.Keys.ToList();
                    tx.Commit();
                }

                var so = new SaveAsOptions { OverwriteExistingFile = true };
                fdoc.SaveAs(req.SavePath, so);

                if (req.LoadIntoProject)
                {
                    if (proj == null) _warn.Add("No active project document; family saved but not loaded.");
                    else
                    {
                        fdoc.LoadFamily(proj, new OverwriteLoadOptions());
                        result.LoadedInto = proj.Title;
                    }
                }
            }
            finally
            {
                if (fdoc != null)
                {
                    try { if (fdoc.IsValidObject) fdoc.Close(false); }
                    catch (Exception ex) { _warn.Add("Close failed: " + ex.Message); }
                }
            }

            result.Warnings = _warn;
            return JsonConvert.SerializeObject(ApiResponse<FamilyBuildResult>.Ok(result));
        }

        // ── 樣板 ─────────────────────────────────────────────────────────
        private static string ResolveTemplate(Autodesk.Revit.ApplicationServices.Application app, string? tpl)
        {
            if (!string.IsNullOrWhiteSpace(tpl) && File.Exists(tpl)) return tpl!;
            var root = app.FamilyTemplatePath;
            var names = new List<string>();
            if (!string.IsNullOrWhiteSpace(tpl)) names.Add(tpl!);
            names.AddRange(new[] { "公制詳圖項目.rft", "Metric Detail Item.rft" });
            var dirs = new List<string>();
            if (Directory.Exists(root)) dirs.Add(root);
            var parent = Directory.Exists(root) ? Directory.GetParent(root)?.FullName : null;
            if (parent != null) dirs.Add(parent);
            foreach (var n in names)
                foreach (var d in dirs)
                {
                    var hit = Directory.GetFiles(d, n, SearchOption.AllDirectories).FirstOrDefault();
                    if (hit != null) return hit;
                }
            throw new FileNotFoundException($"Family template not found: {tpl ?? "(detail item)"} under {root}");
        }

        private static View WorkView(Document fdoc)
        {
            var views = new FilteredElementCollector(fdoc).OfClass(typeof(View)).Cast<View>()
                .Where(x => !x.IsTemplate && x.ViewType != ViewType.Schedule &&
                            x.ViewType != ViewType.ThreeD &&
                            x.ViewType != ViewType.ProjectBrowser && x.ViewType != ViewType.SystemBrowser)
                .ToList();
            var v = views.FirstOrDefault(x => x.ViewType == ViewType.FloorPlan || x.ViewType == ViewType.DraftingView ||
                                              x.ViewType == ViewType.Detail || x.ViewType == ViewType.EngineeringPlan)
                    ?? views.FirstOrDefault();
            if (v == null)
                throw new InvalidOperationException("No usable view in family template. (views: " +
                    string.Join(", ", new FilteredElementCollector(fdoc).OfClass(typeof(View)).Cast<View>()
                        .Select(x => x.ViewType + (x.IsTemplate ? "*" : ""))) + ")");
            return v;
        }

        // ── 參數 ─────────────────────────────────────────────────────────
        private static bool IsFamilyType(FamilyParamSpec ps) =>
            ps.Kind.Equals("familyType", StringComparison.OrdinalIgnoreCase);

        private static FamilyParameter AddParam(Document fdoc, FamilyManager fm, FamilyParamSpec ps)
        {
            var existing = fm.get_Parameter(ps.Name);
            if (existing != null) return existing;

            var group = GroupTypeId.Graphics;
            if (IsFamilyType(ps))
            {
                var bic = ParseCategory(ps.Category ?? "DetailComponents");
                var cat = Category.GetCategory(fdoc, bic)
                          ?? throw new ArgumentException($"Category not found: {ps.Category}");
                return fm.AddParameter(ps.Name, group, cat, ps.Instance);
            }
            ForgeTypeId spec = ps.Kind.ToLowerInvariant() switch
            {
                "yesno"   => SpecTypeId.Boolean.YesNo,
                "length"  => SpecTypeId.Length,
                "integer" => SpecTypeId.Int.Integer,
                "number"  => SpecTypeId.Number,
                "text"    => SpecTypeId.String.Text,
                _ => throw new ArgumentException($"Unknown parameter kind: {ps.Kind}")
            };
            return fm.AddParameter(ps.Name, group, spec, ps.Instance);
        }

        private static BuiltInCategory ParseCategory(string s)
        {
            var name = s.StartsWith("OST_") ? s : "OST_" + s;
            if (Enum.TryParse<BuiltInCategory>(name, true, out var bic)) return bic;
            throw new ArgumentException($"Unknown category: {s}");
        }

        private static void SetValue(FamilyManager fm, FamilyParameter p, FamilyParamSpec ps)
        {
            var v = ps.Value!;
            switch (ps.Kind.ToLowerInvariant())
            {
                case "yesno":
                    var on = v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "是";
                    fm.Set(p, on ? 1 : 0); break;
                case "integer":
                    fm.Set(p, int.Parse(v, CultureInfo.InvariantCulture)); break;
                case "length":
                    fm.Set(p, Ft(double.Parse(v, CultureInfo.InvariantCulture))); break;
                case "number":
                    fm.Set(p, double.Parse(v, CultureInfo.InvariantCulture)); break;
                default:
                    fm.Set(p, v); break;
            }
        }

        private void Associate(FamilyManager fm, Element e, string? paramName,
                               Dictionary<string, FamilyParameter> fparams, BuiltInParameter[] bips, string label)
        {
            if (string.IsNullOrEmpty(paramName)) return;
            if (!fparams.TryGetValue(paramName!, out var fp))
            {
                var found = fm.get_Parameter(paramName);
                if (found == null) { _warn.Add($"{label}: parameter '{paramName}' not defined"); return; }
                fp = found;
            }
            foreach (var bip in bips)
            {
                var p = e.get_Parameter(bip);
                if (p != null && fm.CanElementParameterBeAssociated(p))
                {
                    fm.AssociateElementParameterToFamilyParameter(p, fp);
                    return;
                }
            }
            _warn.Add($"{label}: cannot associate to '{paramName}'");
        }

        private static readonly BuiltInParameter[] VisBips = { BuiltInParameter.IS_VISIBLE_PARAM };
        private static readonly BuiltInParameter[] TypeBips =
        {
            BuiltInParameter.ELEM_TYPE_PARAM,
            BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM,
            BuiltInParameter.SYMBOL_ID_PARAM,
        };

        // ── 元件 ─────────────────────────────────────────────────────────
        private int CreateElement(Document fdoc, Document? proj, FamilyManager fm, View view,
                                  FamilyElementSpec es, Dictionary<string, FamilyParameter> fparams)
        {
            var kind = es.Kind.ToLowerInvariant();
            switch (kind)
            {
                case "filledregion":
                case "maskingregion":
                {
                    var loops = RegionLoops(es);
                    if (loops.Count == 0) throw new ArgumentException($"{es.Kind}: no loops/rects");
#if REVIT2024_OR_GREATER
                    FilledRegion r = kind == "maskingregion"
                        ? FilledRegion.CreateMaskingRegion(fdoc, view.Id, loops)
                        : FilledRegion.Create(fdoc, RegionType(fdoc, proj, es.Color), view.Id, loops);
#else
                    if (kind == "maskingregion") throw new NotSupportedException("遮罩區域需要 Revit 2024 以上");
                    FilledRegion r = FilledRegion.Create(fdoc, RegionType(fdoc, proj, es.Color), view.Id, loops);
#endif
                    if (!es.ShowEdges) HideEdges(fdoc, r);
                    Associate(fm, r, es.VisibleParam, fparams, VisBips, es.Kind);
                    return 1;
                }
                case "glyph":
                {
                    if (string.IsNullOrEmpty(es.Text)) throw new ArgumentException("glyph: text required");
                    var c = es.Center ?? new[] { 0.0, 0.0 };
                    var loops = GlyphLoops(es.Text!, es.Font ?? "Arial", es.CapHeight, c[0], c[1]);
                    var r = FilledRegion.Create(fdoc, RegionType(fdoc, proj, es.Color), view.Id, loops);
                    if (!es.ShowEdges) HideEdges(fdoc, r);
                    Associate(fm, r, es.VisibleParam, fparams, VisBips, "glyph " + es.Text);
                    return 1;
                }
                case "line":
                {
                    var pts = es.Points ?? throw new ArgumentException("line: points required");
                    int n = 0;
                    var segs = pts.Count - 1 + (es.Closed ? 1 : 0);
                    for (int i = 0; i < segs; i++)
                    {
                        var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                        var dc = fdoc.FamilyCreate.NewDetailCurve(view, Line.CreateBound(P(a[0], a[1]), P(b[0], b[1])));
                        Associate(fm, dc, es.VisibleParam, fparams, VisBips, "line");
                        n++;
                    }
                    return n;
                }
                case "nested":
                {
                    var sym = FindSymbol(fdoc, es.Family ?? throw new ArgumentException("nested: family required"), es.Type);
                    if (!sym.IsActive) { sym.Activate(); fdoc.Regenerate(); }
                    var at = es.At ?? new[] { 0.0, 0.0 };
                    var inst = fdoc.FamilyCreate.NewFamilyInstance(P(at[0], at[1]), sym, view);
                    Associate(fm, inst, es.TypeParam, fparams, TypeBips, "nested " + es.Family + " type");
                    Associate(fm, inst, es.VisibleParam, fparams, VisBips, "nested " + es.Family + " visibility");
                    if (es.ParamMap != null)
                        foreach (var kv in es.ParamMap)
                        {
                            var ip = inst.LookupParameter(kv.Key);
                            if (ip == null) { _warn.Add($"nested {es.Family}: instance parameter '{kv.Key}' not found"); continue; }
                            var hp = fparams.TryGetValue(kv.Value, out var hv) ? hv : fm.get_Parameter(kv.Value);
                            if (hp == null) { _warn.Add($"nested {es.Family}: host parameter '{kv.Value}' not defined"); continue; }
                            if (fm.CanElementParameterBeAssociated(ip)) fm.AssociateElementParameterToFamilyParameter(ip, hp);
                            else _warn.Add($"nested {es.Family}: '{kv.Key}' cannot be associated (type parameter?)");
                        }
                    return 1;
                }
                case "label":
                    return CopyLabel(fdoc, fm, view, es, fparams);
                default:
                    throw new ArgumentException($"Unknown element kind: {es.Kind}");
            }
        }

        private static FamilySymbol FindSymbol(Document fdoc, string family, string? type)
        {
            // 支援 "Family:Type" 寫法
            if (type == null && family.Contains(':'))
            {
                var i = family.IndexOf(':');
                type = family.Substring(i + 1);
                family = family.Substring(0, i);
            }
            var syms = new FilteredElementCollector(fdoc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Family != null && s.Family.Name == family).ToList();
            if (syms.Count == 0) throw new ArgumentException($"Nested family not loaded: {family}");
            if (type == null) return syms[0];
            return syms.FirstOrDefault(s => s.Name == type)
                   ?? throw new ArgumentException($"Type '{type}' not found in {family}");
        }

        private static IList<CurveLoop> RegionLoops(FamilyElementSpec es)
        {
            var list = new List<CurveLoop>();
            if (es.Loops != null)
                foreach (var l in es.Loops)
                    list.Add(MakeLoop(l.Select(p => (p[0], p[1])).ToList(), 0.5));
            if (es.Rects != null)
                foreach (var r in es.Rects)
                {
                    double cx = r[0], cy = r[1], hw = r[2] / 2, hh = r[3] / 2;
                    list.Add(MakeLoop(new List<(double, double)>
                    {
                        (cx - hw, cy - hh), (cx + hw, cy - hh), (cx + hw, cy + hh), (cx - hw, cy + hh)
                    }, 0.5));
                }
            return list;
        }

        private static CurveLoop MakeLoop(List<(double x, double y)> pts, double minLenMm)
        {
            var clean = new List<(double x, double y)>();
            foreach (var q in pts)
            {
                if (clean.Count > 0 && Dist(q, clean[clean.Count - 1]) < minLenMm) continue;
                clean.Add(q);
            }
            while (clean.Count > 2 && Dist(clean[0], clean[clean.Count - 1]) < minLenMm) clean.RemoveAt(clean.Count - 1);
            if (clean.Count < 3) throw new ArgumentException("Loop needs at least 3 distinct points.");
            var cl = new CurveLoop();
            for (int i = 0; i < clean.Count; i++)
            {
                var a = clean[i]; var b = clean[(i + 1) % clean.Count];
                cl.Append(Line.CreateBound(P(a.x, a.y), P(b.x, b.y)));
            }
            return cl;
        }

        private static double Dist((double x, double y) a, (double x, double y) b) =>
            Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));

        // ── 字形轉填滿區域（WPF 取字形外框）──────────────────────────────
        private IList<CurveLoop> GlyphLoops(string text, string font, double capHeight, double cx, double cy)
        {
            var tf = new WM.Typeface(new WM.FontFamily(font), SW.FontStyles.Normal,
                                     SW.FontWeights.Normal, SW.FontStretches.Normal);

            List<List<(double x, double y)>> Outline(string s)
            {
                var ftx = new WM.FormattedText(s, CultureInfo.InvariantCulture, SW.FlowDirection.LeftToRight,
                                               tf, 1000, WM.Brushes.Black, 1.0);
                var geo  = ftx.BuildGeometry(new SW.Point(0, 0));
                var flat = geo.GetFlattenedPathGeometry(0.25, WM.ToleranceType.Absolute);
                var res  = new List<List<(double, double)>>();
                foreach (var fig in flat.Figures)
                {
                    var pts = new List<(double, double)> { (fig.StartPoint.X, -fig.StartPoint.Y) };
                    foreach (var seg in fig.Segments)
                    {
                        if (seg is WM.PolyLineSegment pl) foreach (var p in pl.Points) pts.Add((p.X, -p.Y));
                        else if (seg is WM.LineSegment ls) pts.Add((ls.Point.X, -ls.Point.Y));
                    }
                    res.Add(pts);
                }
                return res;
            }

            static (double x0, double y0, double x1, double y1) Box(List<List<(double x, double y)>> ls)
            {
                var all = ls.SelectMany(l => l).ToList();
                return (all.Min(p => p.x), all.Min(p => p.y), all.Max(p => p.x), all.Max(p => p.y));
            }

            var hb = Box(Outline("H"));
            var scale = capHeight / (hb.y1 - hb.y0);
            var raw = Outline(text);
            if (raw.Count == 0) throw new ArgumentException($"No outline for text '{text}'");
            var b = Box(raw);
            double mx = (b.x0 + b.x1) / 2, my = (b.y0 + b.y1) / 2;

            var minLen = Math.Max(2.0, _uiApp.Application.ShortCurveTolerance * 304.8 * 2);
            var loops = new List<CurveLoop>();
            foreach (var l in raw)
            {
                var pts = l.Select(p => (cx + (p.x - mx) * scale, cy + (p.y - my) * scale)).ToList();
                try { loops.Add(MakeLoop(pts, minLen)); }
                catch (Exception ex) { _warn.Add($"glyph '{text}' loop skipped: {ex.Message}"); }
            }
            return loops;
        }

        // ── 標籤：API 不能新建，改由既有族複製 ─────────────────────────────
        private static bool IsLabel(Element e) => e.GetType() == typeof(TextElement);

        private Document OpenSource(FamilyElementSpec es, out bool close)
        {
            var app = _uiApp.Application;
            close = false;
            if (!string.IsNullOrEmpty(es.SourceProjectFamily))
            {
                var proj = _uiApp.ActiveUIDocument?.Document
                           ?? throw new InvalidOperationException("sourceProjectFamily needs an active project.");
                var fam = new FilteredElementCollector(proj).OfClass(typeof(Family)).Cast<Family>()
                          .FirstOrDefault(f => f.Name == es.SourceProjectFamily)
                          ?? throw new ArgumentException($"Family not in project: {es.SourceProjectFamily}");
                close = true;
                return proj.EditFamily(fam);
            }
            var path = es.Source ?? throw new ArgumentException("label: source or sourceProjectFamily required");
            foreach (Document d in app.Documents)
                if (string.Equals(d.PathName, path, StringComparison.OrdinalIgnoreCase)) return d;
            close = true;
            return app.OpenDocumentFile(path);
        }

        private int CopyLabel(Document fdoc, FamilyManager fm, View view, FamilyElementSpec es,
                              Dictionary<string, FamilyParameter> fparams)
        {
            Document? src = null; bool close = false;
            try
            {
                src = OpenSource(es, out close);
                var labels = new FilteredElementCollector(src).WhereElementIsNotElementType()
                             .Where(IsLabel).Cast<TextElement>().ToList();
                if (labels.Count == 0) throw new InvalidOperationException($"No label found in {src.Title}");
                var pick = es.SourceLabelText == null ? labels[0]
                         : labels.FirstOrDefault(l => l.Text == es.SourceLabelText) ?? labels[0];

                var before = new HashSet<string>(fm.GetParameters().Select(p => p.Definition.Name));
                var opts = new CopyPasteOptions();
                opts.SetDuplicateTypeNamesHandler(new UseDestinationTypes());
                var srcView = src.GetElement(pick.OwnerViewId) as View
                              ?? throw new InvalidOperationException("Label has no owner view.");
                var newIds = ElementTransformUtils.CopyElements(srcView, new List<ElementId> { pick.Id }, view,
                                                                Transform.Identity, opts);
                var lbl = newIds.Select(fdoc.GetElement).OfType<TextElement>().FirstOrDefault()
                          ?? throw new InvalidOperationException("Label copy returned nothing.");

                // 位置：置中
                try { lbl.HorizontalAlignment = HorizontalTextAlignment.Center; } catch { }
                try { lbl.VerticalAlignment = VerticalTextAlignment.Middle; } catch { }
                var at = es.At ?? new[] { 0.0, 0.0 };
                try { lbl.Coord = P(at[0], at[1]); } catch (Exception ex) { _warn.Add("label move: " + ex.Message); }

                // 字高 / 字型：複製一個專用文字類型
                if (es.TextSize != null || es.TextFont != null)
                {
                    var tt = lbl.Symbol;
                    var name = $"MCP_標籤_{(es.TextSize ?? 0):0.##}mm_{es.TextFont ?? "src"}";
                    var exist = new FilteredElementCollector(fdoc).OfClass(typeof(TextElementType))
                                .Cast<TextElementType>().FirstOrDefault(t => t.Name == name);
                    var nt = exist ?? (TextElementType)tt.Duplicate(name);
                    if (es.TextSize != null) nt.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(Ft(es.TextSize.Value));
                    if (es.TextFont != null) nt.get_Parameter(BuiltInParameter.TEXT_FONT)?.Set(es.TextFont);
                    lbl.ChangeTypeId(nt.Id);
                }

                // 標籤帶進來的參數 → 改名、改實例
                var added = fm.GetParameters().Where(p => !before.Contains(p.Definition.Name)).ToList();
                if (added.Count == 0)
                {
                    _warn.Add($"label from {src.Title}: no family parameter came with it (label bound to a built-in/category parameter?)");
                    return 1;
                }
                if (added.Count > 1) _warn.Add($"label brought {added.Count} parameters: {string.Join(", ", added.Select(p => p.Definition.Name))}; using the first");
                var fp = added[0];
                if (!string.IsNullOrEmpty(es.Param) && fp.Definition.Name != es.Param)
                {
                    if (fp.IsShared)
                        fp = fm.ReplaceParameter(fp, es.Param, GroupTypeId.Text, es.ParamInstance);
                    else
                        fm.RenameParameter(fp, es.Param);
                }
                if (es.ParamInstance && !fp.IsInstance) fm.MakeInstance(fp);
                if (!es.ParamInstance && fp.IsInstance) fm.MakeType(fp);
                if (es.ParamValue != null && fp.StorageType == StorageType.String) fm.Set(fp, es.ParamValue);
                fparams[fp.Definition.Name] = fp;
                Associate(fm, lbl, es.VisibleParam, fparams, VisBips, "label");
                return 1;
            }
            finally
            {
                if (src != null && close)
                {
                    try { src.Close(false); } catch (Exception ex) { _warn.Add("close source: " + ex.Message); }
                }
            }
        }

        private class UseDestinationTypes : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
                => DuplicateTypeAction.UseDestinationTypes;
        }

        /// <summary>掃描族檔（或目前專案中的族），列出含標籤的族與其參數，用來挑選標籤來源。</summary>
        public string ScanLabels(string body)
        {
            var req = JsonConvert.DeserializeObject<LabelScanRequest>(body) ?? new LabelScanRequest();
            var app = _uiApp.Application;
            var outList = new List<object>();
            var jobs = new List<(string name, Func<(Document doc, bool close)> open)>();

            foreach (var p in req.Paths)
            {
                if (Directory.Exists(p))
                    foreach (var f in Directory.GetFiles(p, "*.rfa", SearchOption.AllDirectories))
                        jobs.Add((f, () => (app.OpenDocumentFile(f), true)));
                else if (File.Exists(p))
                    jobs.Add((p, () => (app.OpenDocumentFile(p), true)));
            }
            if (req.FromProject)
            {
                var proj = _uiApp.ActiveUIDocument?.Document;
                if (proj != null && !proj.IsFamilyDocument)
                    foreach (var fam in new FilteredElementCollector(proj).OfClass(typeof(Family)).Cast<Family>())
                    {
                        if (!fam.IsEditable) continue;
                        var cat = fam.FamilyCategory;
                        if (cat == null || cat.CategoryType != CategoryType.Annotation) continue;
                        if (req.NameContains != null && !fam.Name.Contains(req.NameContains)) continue;
                        var f = fam;
                        jobs.Add(("project:" + f.Name, () => (proj.EditFamily(f), true)));
                    }
            }

            foreach (var job in jobs.Take(req.Max))
            {
                Document? d = null; bool close = false;
                try
                {
                    (d, close) = job.open();
                    if (!d.IsFamilyDocument) continue;
                    var labels = new FilteredElementCollector(d).WhereElementIsNotElementType()
                                 .Where(IsLabel).Cast<TextElement>().Select(l => l.Text).ToList();
                    if (labels.Count == 0) continue;
                    var fm = d.FamilyManager;
                    outList.Add(new
                    {
                        source = job.name,
                        category = d.OwnerFamily?.FamilyCategory?.Name,
                        labels,
                        parameters = fm.GetParameters().Select(p => new
                        {
                            name = p.Definition.Name, shared = p.IsShared, instance = p.IsInstance,
                            builtIn = p.Id.Value < 0, storage = p.StorageType.ToString()
                        }).ToList()
                    });
                }
                catch (Exception ex) { outList.Add(new { source = job.name, error = ex.Message }); }
                finally { if (d != null && close) { try { d.Close(false); } catch { } } }
            }
            return JsonConvert.SerializeObject(new { success = true, data = new { scanned = Math.Min(jobs.Count, req.Max), total = jobs.Count, results = outList } });
        }

        // ── 填滿區域類型 / 線型 ─────────────────────────────────────────
        private ElementId RegionType(Document fdoc, Document? proj, int[]? rgb)
        {
            var c = rgb is { Length: 3 } ? rgb : new[] { 0, 0, 0 };
            var name = $"MCP_實心_{c[0]:X2}{c[1]:X2}{c[2]:X2}";
            var types = new FilteredElementCollector(fdoc).OfClass(typeof(FilledRegionType))
                        .Cast<FilledRegionType>().ToList();
            var hit = types.FirstOrDefault(t => t.Name == name);
            if (hit != null) return hit.Id;

            if (types.Count == 0)
            {
                var src = proj == null ? null :
                    new FilteredElementCollector(proj).OfClass(typeof(FilledRegionType)).FirstElementId();
                if (src == null || src == ElementId.InvalidElementId)
                    throw new InvalidOperationException("No FilledRegionType in family template or active project to copy from.");
                ElementTransformUtils.CopyElements(proj, new List<ElementId> { src }, fdoc,
                                                   Transform.Identity, new CopyPasteOptions());
                types = new FilteredElementCollector(fdoc).OfClass(typeof(FilledRegionType))
                        .Cast<FilledRegionType>().ToList();
                _warn.Add("FilledRegionType copied from active project.");
            }

            var baseType = types.FirstOrDefault(t => !t.IsMasking) ?? types[0];
            var rt = (FilledRegionType)baseType.Duplicate(name);
            rt.ForegroundPatternId    = SolidFill(fdoc);
            rt.ForegroundPatternColor = new Color((byte)c[0], (byte)c[1], (byte)c[2]);
            try { rt.BackgroundPatternId = ElementId.InvalidElementId; } catch { }
            try { rt.LineWeight = 1; } catch { }
            return rt.Id;
        }

        private static ElementId SolidFill(Document fdoc)
        {
            var fp = new FilteredElementCollector(fdoc).OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(e => { var p = e.GetFillPattern(); return p.IsSolidFill && p.Target == FillPatternTarget.Drafting; });
            if (fp != null) return fp.Id;
            var created = FillPatternElement.Create(fdoc,
                new FillPattern("<Solid fill>", FillPatternTarget.Drafting, FillPatternHostOrientation.ToView));
            return created.Id;
        }

        private static void HideEdges(Document fdoc, FilledRegion r)
        {
            var invisible = new ElementId(BuiltInCategory.OST_InvisibleLines);
            foreach (var id in FilledRegion.GetValidLineStyleIdsForFilledRegion(fdoc))
            {
                if (fdoc.GetElement(id) is GraphicsStyle gs && gs.GraphicsStyleCategory?.Id == invisible)
                {
                    r.SetLineStyleId(id);
                    return;
                }
            }
        }

        private class OverwriteLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
                                            out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }
    }
}
