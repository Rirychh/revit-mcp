// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 模型健檢與效能分析（唯讀，不開交易）。
    ///   GET  /qc/health       — 一頁總覽：檔案、警告、族、CAD、群組、視圖、房間、可清除元素，附 flags
    ///   POST /qc/performance  — 依品類/族統計實例數與幾何量，找出拖慢模型的主因
    /// </summary>
    public class ModelQcHandler
    {
        private readonly Document _doc;
        public ModelQcHandler(Document doc) { _doc = doc; }

        private static string Ok(object d) => JsonConvert.SerializeObject(new { success = true, data = d });

        // ───────────────────────── 健檢 ─────────────────────────
        public string Health(NameValueCollection qs)
        {
            bool withPurgeable = !string.Equals(qs["purgeable"], "false", StringComparison.OrdinalIgnoreCase);
            var flags = new List<object>();
            void Flag(string level, string item, string detail, string suggestion)
                => flags.Add(new { level, item, detail, suggestion });

            // 檔案
            double sizeMb = 0;
            try
            {
                if (!string.IsNullOrEmpty(_doc.PathName) && File.Exists(_doc.PathName))
                    sizeMb = Math.Round(new FileInfo(_doc.PathName).Length / 1048576.0, 1);
            }
            catch { }
            if (sizeMb > 300) Flag("high", "檔案大小", $"{sizeMb} MB", "跑 revit_performance_report 找主要佔用來源");
            else if (sizeMb > 150) Flag("medium", "檔案大小", $"{sizeMb} MB", "定期清除未使用元素並壓縮存檔");

            // 元素數
            int instCount = new FilteredElementCollector(_doc).WhereElementIsNotElementType().GetElementCount();
            int typeCount = new FilteredElementCollector(_doc).WhereElementIsElementType().GetElementCount();

            // 警告
            var warnings = _doc.GetWarnings();
            var topWarnings = warnings.GroupBy(w => w.GetDescriptionText())
                .Select(g => new { description = g.Key, count = g.Count(), autoFix = WarningsHandler.KindOf(g.First()) })
                .OrderByDescending(x => x.count).Take(15).ToList();
            if (warnings.Count > 500) Flag("high", "警告", $"{warnings.Count} 筆", "用 revit_list_warnings 看分類，revit_resolve_warnings 處理可自動修的");
            else if (warnings.Count > 100) Flag("medium", "警告", $"{warnings.Count} 筆", "用 revit_list_warnings 看分類");

            // 族
            var families = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>().ToList();
            var inPlace = new FilteredElementCollector(_doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(fi => fi.Symbol?.Family?.IsInPlace == true).ToList();
            var inPlaceByCat = inPlace.GroupBy(fi => fi.Category?.Name ?? "?")
                .Select(g => new { category = g.Key, count = g.Count() }).OrderByDescending(x => x.count).ToList();
            if (inPlace.Count > 20) Flag("medium", "內建族", $"{inPlace.Count} 個實例", "重複出現的內建族改成可載入族");

            // CAD / 影像
            var imports = new FilteredElementCollector(_doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
            var cadImported = imports.Where(i => !i.IsLinked).ToList();
            var cadLinked = imports.Where(i => i.IsLinked).ToList();
            var importedList = cadImported.Take(30).Select(i => new
            {
                id = i.Id.Value,
                name = i.Category?.Name,
                viewSpecific = i.ViewSpecific,
                view = i.ViewSpecific ? _doc.GetElement(i.OwnerViewId)?.Name : null,
            }).ToList();
            if (cadImported.Count > 10) Flag("high", "匯入 CAD", $"{cadImported.Count} 個", "改用連結 CAD，刪除匯入的 DWG");
            else if (cadImported.Count > 0) Flag("medium", "匯入 CAD", $"{cadImported.Count} 個", "確認是否需要；可改用連結");
            int images = new FilteredElementCollector(_doc).OfClass(typeof(ImageType)).GetElementCount();

            // Revit 連結
            var linkTypes = new FilteredElementCollector(_doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().ToList();
            var links = linkTypes.Select(t => new { id = t.Id.Value, name = t.Name, loaded = RevitLinkType.IsLoaded(_doc, t.Id) }).ToList();
            int unloaded = links.Count(l => !l.loaded);
            if (unloaded > 0) Flag("low", "Revit 連結", $"{unloaded} 個未載入", "不用的連結可移除");

            // 群組
            int modelGroups = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_IOSModelGroups).WhereElementIsNotElementType().GetElementCount();
            int detailGroups = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_IOSDetailGroups).WhereElementIsNotElementType().GetElementCount();
            var unusedGroupTypes = new FilteredElementCollector(_doc).OfClass(typeof(GroupType)).Cast<GroupType>()
                .Where(gt => gt.Groups.Size == 0).Select(gt => gt.Name).ToList();
            if (unusedGroupTypes.Count > 0) Flag("low", "未使用群組類型", $"{unusedGroupTypes.Count} 個", "可清除");

            // 視圖
            var allViews = new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>().ToList();
            var templates = allViews.Where(v => v.IsTemplate).ToList();
            var views = allViews.Where(v => !v.IsTemplate).ToList();
            var placed = new HashSet<long>();
            foreach (var vp in new FilteredElementCollector(_doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                placed.Add(vp.ViewId.Value);
            foreach (var ss in new FilteredElementCollector(_doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
                placed.Add(ss.ScheduleId.Value);
            var skipTypes = new HashSet<ViewType> { ViewType.ProjectBrowser, ViewType.SystemBrowser, ViewType.Internal,
                                                    ViewType.Undefined, ViewType.DrawingSheet };
            var graphical = views.Where(v => !skipTypes.Contains(v.ViewType) && v.ViewType != ViewType.Schedule).ToList();
            var notOnSheet = graphical.Where(v => !placed.Contains(v.Id.Value)).ToList();
            var schedules = views.OfType<ViewSchedule>()
                .Where(s => !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule).ToList();
            int schedulesNotOnSheet = schedules.Count(s => !placed.Contains(s.Id.Value));
            var usedTemplateIds = new HashSet<long>(views.Select(v => v.ViewTemplateId.Value));
            var unusedTemplates = templates.Where(t => !usedTemplateIds.Contains(t.Id.Value)).Select(t => t.Name).ToList();
            if (notOnSheet.Count > 100 && notOnSheet.Count > graphical.Count * 0.3)
                Flag("medium", "未上圖紙視圖", $"{notOnSheet.Count} / {graphical.Count}", "清掉不用的工作視圖可減少開檔與重生時間");
            if (unusedTemplates.Count > 0) Flag("low", "未使用視圖樣板", $"{unusedTemplates.Count} 個", "可清除");

            // 房間
            var rooms = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Room>().ToList();
            int unplaced = rooms.Count(r => r.Location == null);
            int notEnclosed = rooms.Count(r => r.Location != null && r.Area <= 0);
            if (unplaced > 0) Flag("low", "未放置房間", $"{unplaced} 個", "確認後刪除");
            if (notEnclosed > 0) Flag("medium", "未封閉房間", $"{notEnclosed} 個", "檢查邊界或刪除");

            // 其他會膨脹的元素
            int designOptions = new FilteredElementCollector(_doc).OfClass(typeof(DesignOption)).GetElementCount();
            int worksets = _doc.IsWorkshared ? new FilteredWorksetCollector(_doc).OfKind(WorksetKind.UserWorkset).ToWorksets().Count : 0;
            int materials = new FilteredElementCollector(_doc).OfClass(typeof(Material)).GetElementCount();
            int fillPatterns = new FilteredElementCollector(_doc).OfClass(typeof(FillPatternElement)).GetElementCount();
            int linePatterns = new FilteredElementCollector(_doc).OfClass(typeof(LinePatternElement)).GetElementCount();

            // 可清除元素（Revit 2024 API）
            int? purgeable = null;
            if (withPurgeable)
            {
#if REVIT2024_OR_GREATER
                try
                {
                    purgeable = _doc.GetUnusedElements(new HashSet<ElementId>()).Count;
                    if (purgeable > 200) Flag("medium", "可清除元素", $"{purgeable} 個", "清除未使用元素（第2批 Purge 工具）");
                }
                catch { }
#endif
            }

            var order = new Dictionary<string, int> { { "high", 0 }, { "medium", 1 }, { "low", 2 } };
            flags = flags.OrderBy(f => order[(string)f.GetType().GetProperty("level")!.GetValue(f)!]).ToList();

            return Ok(new
            {
                document = _doc.Title,
                path = _doc.PathName,
                fileSizeMb = sizeMb,
                workshared = _doc.IsWorkshared,
                flags,
                elements = new { instances = instCount, types = typeCount },
                warnings = new { total = warnings.Count, top = topWarnings },
                families = new { loadable = families.Count(f => !f.IsInPlace), inPlaceFamilies = families.Count(f => f.IsInPlace),
                                 inPlaceInstances = inPlace.Count, inPlaceByCategory = inPlaceByCat },
                cad = new { imported = cadImported.Count, linked = cadLinked.Count, importedList, imageTypes = images },
                revitLinks = links,
                groups = new { modelGroupInstances = modelGroups, detailGroupInstances = detailGroups,
                               unusedGroupTypes = unusedGroupTypes.Count, unusedGroupTypeNames = unusedGroupTypes.Take(30) },
                views = new
                {
                    total = graphical.Count,
                    notOnSheet = notOnSheet.Count,
                    notOnSheetByType = notOnSheet.GroupBy(v => v.ViewType.ToString())
                        .Select(g => new { viewType = g.Key, count = g.Count() }).OrderByDescending(x => x.count),
                    sheets = views.Count(v => v.ViewType == ViewType.DrawingSheet),
                    schedules = schedules.Count,
                    schedulesNotOnSheet,
                    templates = templates.Count,
                    unusedTemplates = unusedTemplates.Count,
                    unusedTemplateNames = unusedTemplates.Take(30),
                },
                rooms = new { total = rooms.Count, unplaced, notEnclosed },
                misc = new { designOptions, worksets, materials, fillPatterns, linePatterns },
                purgeableElements = purgeable,
            });
        }

        // ───────────────────────── 效能 ─────────────────────────
        private class GeoCount { public int Solids, Faces, Edges, Curves, MeshTris, Nested; }

        private static void Count(GeometryElement? ge, GeoCount c, int depth)
        {
            if (ge == null) return;
            foreach (var o in ge)
            {
                switch (o)
                {
                    case Solid s:
                        if (s.Faces.Size == 0) break;
                        c.Solids++; c.Faces += s.Faces.Size; c.Edges += s.Edges.Size; break;
                    case Mesh m: c.MeshTris += m.NumTriangles; break;
                    case Curve _: c.Curves++; break;
                    case PolyLine _: c.Curves++; break;
                    case GeometryInstance gi:
                        c.Nested++;
                        if (depth < 6) Count(gi.GetSymbolGeometry(), c, depth + 1);
                        break;
                }
            }
        }

        // POST /qc/performance { topN:20, maxFamilies:400, sampleGeometry:true }
        public string Performance(string body)
        {
            var r = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            int topN = (int?)r["topN"] ?? 20;
            int maxFamilies = (int?)r["maxFamilies"] ?? 400;
            bool sample = (bool?)r["sampleGeometry"] ?? true;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var opt = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = false };

            // 品類實例數
            var inst = new FilteredElementCollector(_doc).WhereElementIsNotElementType().ToElements();
            var byCategory = inst.Where(e => e.Category != null)
                .GroupBy(e => e.Category.Name)
                .Select(g => new { category = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count).Take(topN).ToList();

            // 族：實例數 × 幾何量
            var fis = inst.OfType<FamilyInstance>().Where(fi => fi.Symbol != null).ToList();
            var famRows = new List<(string fam, string cat, bool inPlace, int instances, int types, GeoCount g, long sampleId)>();
            foreach (var grp in fis.GroupBy(fi => fi.Symbol.Family.Id.Value)
                                   .OrderByDescending(g => g.Count()).Take(maxFamilies))
            {
                var first = grp.First();
                var fam = first.Symbol.Family;
                var gc = new GeoCount();
                if (sample)
                {
                    try
                    {
                        if (fam.IsInPlace)
                            foreach (var fi in grp) Count(fi.get_Geometry(opt), gc, 0);   // 內建族每個都不同
                        else
                            Count(first.Symbol.get_Geometry(opt) ?? first.get_Geometry(opt), gc, 0);
                    }
                    catch { }
                }
                famRows.Add((fam.Name, fam.FamilyCategory?.Name ?? "?", fam.IsInPlace, grp.Count(),
                             grp.Select(x => x.Symbol.Id.Value).Distinct().Count(), gc, first.Id.Value));
            }

            object Row((string fam, string cat, bool inPlace, int instances, int types, GeoCount g, long sampleId) x)
            {
                long load = x.inPlace ? (long)x.g.Faces + x.g.MeshTris / 2 : ((long)x.g.Faces + x.g.MeshTris / 2) * x.instances;
                return new
                {
                    family = x.fam, category = x.cat, inPlace = x.inPlace, instances = x.instances, typesUsed = x.types,
                    facesPerInstance = x.inPlace ? (int?)null : x.g.Faces,
                    meshTriangles = x.g.MeshTris, nestedInstances = x.g.Nested, curves = x.g.Curves,
                    geometryLoad = load, sampleElementId = x.sampleId,
                };
            }

            long LoadOf((string fam, string cat, bool inPlace, int instances, int types, GeoCount g, long sampleId) x)
                => x.inPlace ? (long)x.g.Faces + x.g.MeshTris / 2 : ((long)x.g.Faces + x.g.MeshTris / 2) * x.instances;

            var heaviest = famRows.OrderByDescending(LoadOf).Take(topN).Select(Row).ToList();
            var mostPlaced = famRows.OrderByDescending(x => x.instances).Take(topN).Select(Row).ToList();
            var complexSingle = famRows.Where(x => !x.inPlace)
                .OrderByDescending(x => x.g.Faces + x.g.MeshTris / 2).Take(topN).Select(Row).ToList();

            // CAD：每個 DWG 的幾何量
            var cad = new List<object>();
            foreach (var ii in new FilteredElementCollector(_doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            {
                var gc = new GeoCount();
                try { Count(ii.get_Geometry(new Options()), gc, 0); } catch { }
                cad.Add(new
                {
                    id = ii.Id.Value, name = ii.Category?.Name, linked = ii.IsLinked, viewSpecific = ii.ViewSpecific,
                    view = ii.ViewSpecific ? _doc.GetElement(ii.OwnerViewId)?.Name : null,
                    curves = gc.Curves, solids = gc.Solids, meshTriangles = gc.MeshTris, blocks = gc.Nested,
                });
            }
            cad = cad.OrderByDescending(c => (int)c.GetType().GetProperty("curves")!.GetValue(c)!).Take(topN).ToList();

            // 2D 雜物
            int Cnt(BuiltInCategory c) => new FilteredElementCollector(_doc).OfCategory(c).WhereElementIsNotElementType().GetElementCount();
            var annotations = new
            {
                detailLines = new FilteredElementCollector(_doc).OfClass(typeof(CurveElement)).Cast<CurveElement>().Count(c => c.CurveElementType == CurveElementType.DetailCurve),
                modelLines = new FilteredElementCollector(_doc).OfClass(typeof(CurveElement)).Cast<CurveElement>().Count(c => c.CurveElementType == CurveElementType.ModelCurve),
                filledRegions = new FilteredElementCollector(_doc).OfClass(typeof(FilledRegion)).GetElementCount(),
                textNotes = new FilteredElementCollector(_doc).OfClass(typeof(TextNote)).GetElementCount(),
                dimensions = new FilteredElementCollector(_doc).OfClass(typeof(Dimension)).GetElementCount(),
                directShapes = new FilteredElementCollector(_doc).OfClass(typeof(DirectShape)).GetElementCount(),
                revisionClouds = Cnt(BuiltInCategory.OST_RevisionClouds),
            };

            // 視圖細緻度
            var views = new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
            int fineViews = 0;
            foreach (var v in views) { try { if (v.DetailLevel == ViewDetailLevel.Fine) fineViews++; } catch { } }

            return Ok(new
            {
                elapsedSec = Math.Round(sw.Elapsed.TotalSeconds, 1),
                note = "geometryLoad = (面數 + 網格三角形/2) × 實例數；內建族為各實例幾何總和。數字是相對指標，用來排序找主因。",
                familiesExamined = famRows.Count,
                byCategory,
                heaviestFamilies = heaviest,
                mostPlacedFamilies = mostPlaced,
                mostComplexFamilyTypes = complexSingle,
                cad,
                annotations,
                views = new { total = views.Count, fineDetail = fineViews },
            });
        }
    }
}
