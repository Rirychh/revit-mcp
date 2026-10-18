// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMcpAddin.Models;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 批次建模：新專案（含長度單位與中央檔）、標高、牆（含帷幕牆）、樓板、圓柱。
    /// 所有幾何輸入一律 mm、專案座標；內部一律換算成英呎。
    /// </summary>
    public class BuildHandler
    {
        private readonly UIApplication _uiApp;
        private readonly Document? _doc;

        public BuildHandler(UIApplication uiApp) { _uiApp = uiApp; _doc = null; }
        public BuildHandler(UIApplication uiApp, Document doc) { _uiApp = uiApp; _doc = doc; }

        private Document Doc => _doc ?? throw new InvalidOperationException("No active Revit document.");

        private static double Mm(double mm) => mm / 304.8;
        private static double ToMm(double ft) => ft * 304.8;

        private static ForgeTypeId ResolveLengthUnit(string u)
        {
            switch (u.Trim().ToLowerInvariant())
            {
                case "cm": case "centimeter": case "centimeters": case "公分":
                    return UnitTypeId.Centimeters;
                case "mm": case "millimeter": case "millimeters": case "公釐":
                    return UnitTypeId.Millimeters;
                case "m": case "meter": case "meters": case "公尺":
                    return UnitTypeId.Meters;
                default:
                    throw new InvalidOperationException($"不支援的長度單位「{u}」。可用 cm / mm / m。");
            }
        }

        // ─── 新專案：由樣板建立、設單位、存成中央檔並開啟 ─────────────────────
        public string NewProject(string body)
        {
            var req = JsonConvert.DeserializeObject<NewProjectRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");

            if (string.IsNullOrWhiteSpace(req.TemplatePath) || !File.Exists(req.TemplatePath))
                throw new InvalidOperationException($"找不到專案樣板：{req.TemplatePath}");
            if (string.IsNullOrWhiteSpace(req.SavePath))
                throw new InvalidOperationException("savePath 必填（要存到哪個 .rvt）。");

            var dir = Path.GetDirectoryName(req.SavePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            if (File.Exists(req.SavePath) && !req.Overwrite)
                throw new InvalidOperationException($"檔案已存在且 overwrite=false：{req.SavePath}");

            var app = _uiApp.Application;
            var nd = app.NewProjectDocument(req.TemplatePath)
                     ?? throw new InvalidOperationException("無法由該樣板建立新專案。");

            var unitUsed = "（沿用樣板）";
            try
            {
                if (!string.IsNullOrWhiteSpace(req.LengthUnit))
                {
                    var fid = ResolveLengthUnit(req.LengthUnit!);
                    using var tu = new Transaction(nd, "MCP 設定長度單位");
                    tu.Start();
                    var units = nd.GetUnits();
                    units.SetFormatOptions(SpecTypeId.Length, new FormatOptions(fid));
                    nd.SetUnits(units);
                    tu.Commit();
                    unitUsed = req.LengthUnit!;
                }

                var note = "";
                if (req.Central)
                {
                    try
                    {
                        nd.EnableWorksharing(
                            string.IsNullOrWhiteSpace(req.LevelsGridsWorkset) ? "共用標高及軸網" : req.LevelsGridsWorkset!,
                            string.IsNullOrWhiteSpace(req.DefaultWorkset)     ? "工作集1"        : req.DefaultWorkset!);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"無法啟用工作共用（中央檔）：{ex.Message}。" +
                            "這個 Revit 版本或授權可能不支援；改用 central=false 存成一般檔案。");
                    }
                }

                var sao = new SaveAsOptions { OverwriteExistingFile = req.Overwrite };
                if (req.Central)
                    sao.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
                nd.SaveAs(req.SavePath, sao);
                nd.Close(false);

                var opened = false;
                var title = Path.GetFileName(req.SavePath);
                if (req.OpenAfterSave)
                {
                    var uidoc = _uiApp.OpenAndActivateDocument(req.SavePath);
                    opened = uidoc != null;
                    if (uidoc != null) title = uidoc.Document.Title;
                    note = "已開啟並設為使用中文件，後續 MCP 呼叫都會作用在這個檔案上。";
                }
                else
                {
                    note = "未開啟。後續 MCP 呼叫作用在目前使用中的文件，不是這個新檔。";
                }

                return JsonConvert.SerializeObject(ApiResponse<NewProjectResult>.Ok(new NewProjectResult
                {
                    SavedPath = req.SavePath, Central = req.Central,
                    LengthUnit = unitUsed, Opened = opened, Title = title, Note = note
                }));
            }
            catch
            {
                try { nd.Close(false); } catch { }
                throw;
            }
        }

        // ─── 批次建立標高 ─────────────────────────────────────────────────────
        public string CreateLevels(string body)
        {
            var req = JsonConvert.DeserializeObject<CreateLevelsRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");
            if (req.Levels == null || req.Levels.Count == 0)
                throw new ArgumentException("levels 不可為空。");

            var doc = Doc;
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>().ToList();

            var result = new CreateLevelsResult();

            using var tx = new Transaction(doc, "MCP 建立標高");
            tx.Start();
            var fo = tx.GetFailureHandlingOptions();
            fo.SetFailuresPreprocessor(new SwallowWarnings());
            fo.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(fo);

            foreach (var spec in req.Levels)
            {
                var label = string.IsNullOrWhiteSpace(spec.Name)
                    ? $"{spec.ElevationMm:0.#} mm" : spec.Name!;

                var dup = existing.FirstOrDefault(e => Math.Abs(ToMm(e.Elevation) - spec.ElevationMm) < 0.5);
                if (dup != null && req.SkipExisting)
                {
                    result.Skipped.Add($"{label}：同高度已有標高「{dup.Name}」(id {dup.Id.IntegerValue})");
                    continue;
                }

                var lv = Level.Create(doc, Mm(spec.ElevationMm));
                if (lv == null)
                {
                    result.Skipped.Add($"{label}：建立失敗");
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(spec.Name))
                {
                    try { lv.Name = spec.Name; }
                    catch (Exception ex) { result.Skipped.Add($"{label}：標高已建但改名失敗（{ex.Message}）"); }
                }
                existing.Add(lv);
                result.Levels.Add(new LevelDto
                {
                    Id = lv.Id.IntegerValue, Name = lv.Name, Elevation = ToMm(lv.Elevation)
                });
            }

            result.Created = result.Levels.Count;
            tx.Commit();

            return JsonConvert.SerializeObject(ApiResponse<CreateLevelsResult>.Ok(result));
        }

        // ─── 幾何輔助 ─────────────────────────────────────────────────────────
        private static List<XYZ> ToXyz(List<List<double>> pts, double zFt)
        {
            var list = new List<XYZ>();
            foreach (var p in pts)
            {
                if (p == null || p.Count < 2) continue;
                list.Add(new XYZ(Mm(p[0]), Mm(p[1]), zFt));
            }
            return list;
        }

        /// <summary>去掉過短的相鄰點，保證後續每段都長於 Revit 的最短曲線容差。</summary>
        private static List<XYZ> Dedup(List<XYZ> pts, double minSegMm)
        {
            var outp = new List<XYZ>();
            foreach (var p in pts)
            {
                if (outp.Count == 0 || outp[outp.Count - 1].DistanceTo(p) >= Mm(minSegMm))
                    outp.Add(p);
            }
            while (outp.Count > 2 && outp[0].DistanceTo(outp[outp.Count - 1]) < Mm(minSegMm))
                outp.RemoveAt(outp.Count - 1);
            return outp;
        }

        private static CurveLoop? BuildLoop(List<List<double>> pts, double zFt, double minSegMm, out int segs)
        {
            segs = 0;
            var xyz = Dedup(ToXyz(pts, zFt), Math.Max(minSegMm, 1.0));
            if (xyz.Count < 3) return null;
            var loop = new CurveLoop();
            for (int i = 0; i < xyz.Count; i++)
            {
                loop.Append(Line.CreateBound(xyz[i], xyz[(i + 1) % xyz.Count]));
                segs++;
            }
            return loop;
        }

        // ─── 列出牆／樓板型別 ─────────────────────────────────────────────────
        public string ListBuildTypes(NameValueCollection qs)
        {
            var doc = Doc;
            var cat = (qs["category"] ?? "walls").Trim().ToLowerInvariant();
            var list = new List<BuildTypeDto>();

            if (cat.StartsWith("wall"))
            {
                foreach (var wt in new FilteredElementCollector(doc).OfClass(typeof(WallType))
                                       .Cast<WallType>().OrderBy(w => w.Name))
                {
                    double? w = null;
                    try { if (wt.Kind == WallKind.Basic) w = Math.Round(ToMm(wt.Width), 1); } catch { }
                    list.Add(new BuildTypeDto
                    {
                        Id = wt.Id.IntegerValue, Family = wt.FamilyName,
                        Name = wt.Name, Kind = wt.Kind.ToString(), WidthMm = w
                    });
                }
            }
            else if (cat.StartsWith("floor") || cat.StartsWith("slab"))
            {
                foreach (var ft in new FilteredElementCollector(doc).OfClass(typeof(FloorType))
                                       .Cast<FloorType>().OrderBy(f => f.Name))
                {
                    if (ft.Category == null ||
                        ft.Category.Id.IntegerValue != (int)BuiltInCategory.OST_Floors) continue;
                    double? w = null;
                    try { var cs = ft.GetCompoundStructure(); if (cs != null) w = Math.Round(ToMm(cs.GetWidth()), 1); }
                    catch { }
                    list.Add(new BuildTypeDto
                    {
                        Id = ft.Id.IntegerValue, Family = ft.FamilyName,
                        Name = ft.Name, Kind = "Floor", WidthMm = w
                    });
                }
            }
            else throw new InvalidOperationException($"category 只支援 walls 或 floors，收到「{cat}」。");

            return JsonConvert.SerializeObject(ApiResponse<List<BuildTypeDto>>.Ok(list));
        }

        // ─── 批次建立牆（帷幕牆或一般牆）──────────────────────────────────────
        public string CreateWalls(string body)
        {
            var req = JsonConvert.DeserializeObject<CreateWallsRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");
            if (req.Loops == null || req.Loops.Count == 0)
                throw new ArgumentException("loops 不可為空。");
            if (req.HeightMm <= 0)
                throw new ArgumentException("heightMm 必須大於 0。");

            var doc = Doc;
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .FirstOrDefault(l => l.Name == req.LevelName)
                ?? throw new InvalidOperationException(
                    $"找不到標高「{req.LevelName}」。先用 revit_list_levels 看目前有哪些標高。");

            var wallTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType))
                                .Cast<WallType>().ToList();
            WallType? wt = null;
            if (!string.IsNullOrWhiteSpace(req.WallTypeName))
            {
                wt = wallTypes.FirstOrDefault(t => t.Name == req.WallTypeName)
                  ?? wallTypes.FirstOrDefault(t => t.Name.IndexOf(
                         req.WallTypeName!, StringComparison.OrdinalIgnoreCase) >= 0);
                if (wt == null)
                    throw new InvalidOperationException(
                        $"找不到牆型別「{req.WallTypeName}」。目前可用：" +
                        string.Join("、", wallTypes.Select(t => t.Name).Take(40)));
            }

            var result = new CreateWallsResult
            {
                LevelName = level.Name,
                WallTypeUsed = wt?.Name ?? "（模型預設牆型別）"
            };

            using var tx = new Transaction(doc, "MCP 建立牆");
            tx.Start();
            var fo = tx.GetFailureHandlingOptions();
            fo.SetFailuresPreprocessor(new SwallowWarnings());
            fo.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(fo);

            int li = 0;
            foreach (var loopSpec in req.Loops)
            {
                li++;
                if (loopSpec.Points == null || loopSpec.Points.Count < 2)
                { result.Skipped.Add($"loop {li}：點數不足"); continue; }

                // 牆的定位線放在 Z=0，實際高度由 level + baseOffset 決定
                var xyz = Dedup(ToXyz(loopSpec.Points, 0.0), Math.Max(req.MinSegmentMm, 1.0));
                if (xyz.Count < 2)
                { result.Skipped.Add($"loop {li}：去重後點數不足"); continue; }

                int n = loopSpec.Closed ? xyz.Count : xyz.Count - 1;
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        var line = Line.CreateBound(xyz[i], xyz[(i + 1) % xyz.Count]);
                        // 未指定型別時用模型預設牆型別，但仍套用 heightMm / baseOffsetMm
                        // （舊版走 Wall.Create(doc,line,level,structural) 會忽略高度與偏移）
                        var wtId = wt?.Id ?? doc.GetDefaultElementTypeId(ElementTypeGroup.WallType);
                        var w = Wall.Create(doc, line, wtId, level.Id,
                                            Mm(req.HeightMm), Mm(req.BaseOffsetMm), false, req.Structural);
                        result.Ids.Add(w.Id.IntegerValue);
                    }
                    catch (Exception ex)
                    { result.Skipped.Add($"loop {li} 第 {i + 1} 段：{ex.Message}"); }
                }
            }

            result.Created = result.Ids.Count;
            tx.Commit();
            return JsonConvert.SerializeObject(ApiResponse<CreateWallsResult>.Ok(result));
        }

        // ─── 批次建立樓板（含挑空開口）────────────────────────────────────────
        public string CreateFloors(string body)
        {
            var req = JsonConvert.DeserializeObject<CreateFloorsRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");
            if (req.Floors == null || req.Floors.Count == 0)
                throw new ArgumentException("floors 不可為空。");

            var doc = Doc;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            var floorTypes = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>()
                .Where(f => f.Category != null &&
                            f.Category.Id.IntegerValue == (int)BuiltInCategory.OST_Floors).ToList();
            if (floorTypes.Count == 0)
                throw new InvalidOperationException("這個模型裡沒有可用的樓板型別。");

            FloorType baseFt;
            if (!string.IsNullOrWhiteSpace(req.FloorTypeName))
            {
                baseFt = floorTypes.FirstOrDefault(t => t.Name == req.FloorTypeName)
                      ?? floorTypes.FirstOrDefault(t => t.Name.IndexOf(
                             req.FloorTypeName!, StringComparison.OrdinalIgnoreCase) >= 0)
                      ?? throw new InvalidOperationException(
                             $"找不到樓板型別「{req.FloorTypeName}」。目前可用：" +
                             string.Join("、", floorTypes.Select(t => t.Name).Take(40)));
            }
            else baseFt = floorTypes.First();

            var result = new CreateFloorsResult();

            using var tx = new Transaction(doc, "MCP 建立樓板");
            tx.Start();
            var fo = tx.GetFailureHandlingOptions();
            fo.SetFailuresPreprocessor(new SwallowWarnings());
            fo.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(fo);

            var useFt = baseFt;
            if (req.ThicknessMm.HasValue && req.ThicknessMm.Value > 0)
            {
                var wantName = string.IsNullOrWhiteSpace(req.NewTypeName)
                    ? $"{baseFt.Name}_{req.ThicknessMm.Value:0}mm" : req.NewTypeName!;
                var exist = floorTypes.FirstOrDefault(t => t.Name == wantName);
                if (exist != null) useFt = exist;
                else
                {
                    try
                    {
                        var nt = (FloorType)baseFt.Duplicate(wantName);
                        var cs = nt.GetCompoundStructure();
                        if (cs != null)
                        {
                            int idx = cs.GetFirstCoreLayerIndex();
                            cs.SetLayerWidth(idx, Mm(req.ThicknessMm.Value));
                            nt.SetCompoundStructure(cs);
                        }
                        useFt = nt;
                    }
                    catch (Exception ex)
                    {
                        result.Skipped.Add($"複製樓板型別失敗，改用「{baseFt.Name}」：{ex.Message}");
                    }
                }
            }
            result.FloorTypeUsed = useFt.Name;

            foreach (var f in req.Floors)
            {
                var lv = levels.FirstOrDefault(l => l.Name == f.LevelName);
                if (lv == null) { result.Skipped.Add($"{f.LevelName}：找不到這個標高"); continue; }
                if (f.Outer == null || f.Outer.Count < 3)
                { result.Skipped.Add($"{f.LevelName}：outer 點數不足"); continue; }

                var outer = BuildLoop(f.Outer, lv.Elevation, req.MinSegmentMm, out _);
                if (outer == null)
                { result.Skipped.Add($"{f.LevelName}：外框無法組成封閉輪廓"); continue; }

                var loops = new List<CurveLoop> { outer };
                int nopen = 0;
                if (f.Openings != null)
                {
                    foreach (var op in f.Openings)
                    {
                        if (op == null || op.Count < 3) continue;
                        var il = BuildLoop(op, lv.Elevation, req.MinSegmentMm, out _);
                        if (il != null) { loops.Add(il); nopen++; }
                    }
                }

                try
                {
                    var fl = Floor.Create(doc, loops, useFt.Id, lv.Id);
                    if (Math.Abs(f.HeightOffsetMm) > 0.01)
                    {
                        var p = fl.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                        if (p != null && !p.IsReadOnly) p.Set(Mm(f.HeightOffsetMm));
                    }
                    result.Ids.Add(fl.Id.IntegerValue);
                    result.Openings += nopen;
                }
                catch (Exception ex) { result.Skipped.Add($"{f.LevelName}：{ex.Message}"); }
            }

            result.Created = result.Ids.Count;
            tx.Commit();
            return JsonConvert.SerializeObject(ApiResponse<CreateFloorsResult>.Ok(result));
        }

        // ─── 批次建立圓柱（DirectShape，貫通 baseMm → topMm）──────────────────
        public string CreateColumns(string body)
        {
            var req = JsonConvert.DeserializeObject<CreateColumnsRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");
            if (req.Columns == null || req.Columns.Count == 0)
                throw new ArgumentException("columns 不可為空。");
            var hMm = req.TopMm - req.BaseMm;
            if (hMm <= 0) throw new ArgumentException("topMm 必須大於 baseMm。");

            var doc = Doc;
            var result = new CreateColumnsResult();
            var catId = new ElementId((int)BuiltInCategory.OST_StructuralColumns);

            using var tx = new Transaction(doc, "MCP 建立圓柱");
            tx.Start();
            var fo = tx.GetFailureHandlingOptions();
            fo.SetFailuresPreprocessor(new SwallowWarnings());
            fo.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(fo);

            int i = 0;
            foreach (var c in req.Columns)
            {
                i++;
                var rMm = c.RadiusMm ?? (req.DiameterMm.HasValue ? req.DiameterMm.Value / 2.0 : 0.0);
                if (rMm <= 0) { result.Skipped.Add($"第 {i} 支：半徑未給或不合法"); continue; }
                try
                {
                    var ctr = new XYZ(Mm(c.X), Mm(c.Y), Mm(req.BaseMm));
                    var cl = new CurveLoop();
                    cl.Append(Arc.Create(ctr, Mm(rMm), 0.0, Math.PI, XYZ.BasisX, XYZ.BasisY));
                    cl.Append(Arc.Create(ctr, Mm(rMm), Math.PI, 2.0 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                    var solid = GeometryCreationUtilities.CreateExtrusionGeometry(
                        new List<CurveLoop> { cl }, XYZ.BasisZ, Mm(hMm));
                    var ds = DirectShape.CreateElement(doc, catId);
                    ds.ApplicationId     = "RevitMCP";
                    ds.ApplicationDataId = "column";
                    ds.SetShape(new List<GeometryObject> { solid });
                    result.Ids.Add(ds.Id.IntegerValue);
                }
                catch (Exception ex) { result.Skipped.Add($"第 {i} 支：{ex.Message}"); }
            }

            result.Created = result.Ids.Count;
            tx.Commit();
            return JsonConvert.SerializeObject(ApiResponse<CreateColumnsResult>.Ok(result));
        }
    }
}
