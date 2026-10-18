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
    public class ViewHandler
    {
        private readonly UIApplication _uiApp;
        private readonly Document      _doc;

        public ViewHandler(UIApplication uiApp, Document doc)
        {
            _uiApp = uiApp;
            _doc   = doc;
        }

        // ─── 列出視圖 ─────────────────────────────────────────────────────────
        public string ListViews(NameValueCollection qs)
        {
            var typeFilter = qs["viewType"];  // 例如 "FloorPlan" / "Section" / "3D"

            var views = new FilteredElementCollector(_doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .Where(v => string.IsNullOrEmpty(typeFilter) ||
                            v.ViewType.ToString().Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
                .Select(v => new ViewDto
                {
                    Id        = v.Id.IntegerValue,
                    Name      = v.Name,
                    ViewType  = v.ViewType.ToString(),
                    LevelName = GetViewLevelName(v),
                    Scale     = v.Scale,
                    SheetNumber = GetSheetNumber(v)
                })
                .ToList();

            return JsonConvert.SerializeObject(ApiResponse<List<ViewDto>>.Ok(views));
        }

        /// <summary>
        /// 圖紙號碼。先讀 ViewSheet.SheetNumber；本案那個參數是空的，
        /// 真正的圖號寫在工作集名稱裡（視圖 "圖紙: A9-17 - 廁所AB2詳圖"），所以再從那裡撈。
        /// </summary>
        private static string GetSheetNumber(View v)
        {
            if (v is ViewSheet vs && !string.IsNullOrWhiteSpace(vs.SheetNumber))
                return vs.SheetNumber;
            try
            {
                var w = v.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM)?.AsValueString() ?? "";
                var m = System.Text.RegularExpressions.Regex.Match(w, @"圖紙:\s*([^\s]+)\s*-");
                if (m.Success) return m.Groups[1].Value;
            }
            catch { }
            return "";
        }

        // ─── 建立視圖 ─────────────────────────────────────────────────────────
        public string CreateView(string body)
        {
            var req = JsonConvert.DeserializeObject<CreateViewRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");

            using var tx = new Transaction(_doc, "MCP Create View");
            tx.Start();

            View newView = req.ViewType.ToUpperInvariant() switch
            {
                "FLOORPLAN" or "FLOOR_PLAN" => CreateFloorPlan(req),
                "SECTION"                   => CreateSection(req),
                "3D" or "THREED"            => Create3DView(req),
                _                           => throw new ArgumentException($"Unsupported viewType: {req.ViewType}")
            };

            if (!string.IsNullOrEmpty(req.Name))
                newView.Name = req.Name;

            tx.Commit();

            var dto = new ViewDto
            {
                Id       = newView.Id.IntegerValue,
                Name     = newView.Name,
                ViewType = newView.ViewType.ToString(),
                Scale    = newView.Scale
            };
            return JsonConvert.SerializeObject(ApiResponse<ViewDto>.Ok(dto));
        }

        // ─── 截圖 ─────────────────────────────────────────────────────────────
        public string TakeScreenshot(long viewId)
        {
            var view = _doc.GetElement(new ElementId((int)viewId)) as View
                       ?? throw new InvalidOperationException($"View {viewId} not found.");

            var outPath = Path.Combine(
                Path.GetTempPath(),
                $"revit_view_{viewId}_{DateTime.Now:yyyyMMddHHmmss}.png");

            var opts = new ImageExportOptions
            {
                ExportRange          = ExportRange.SetOfViews,
                HLRandWFViewsFileType = ImageFileType.PNG,
                FilePath             = outPath,
                ImageResolution      = ImageResolution.DPI_150,
                ShadowViewsFileType  = ImageFileType.PNG,
                ZoomType             = ZoomFitType.FitToPage,
                PixelSize            = 2048
            };
            opts.SetViewsAndSheets(new List<ElementId> { view.Id });

            using var tx = new Transaction(_doc, "MCP Screenshot");
            tx.Start();
            _doc.ExportImage(opts);
            tx.Commit();

            // Revit 會自動在檔名後加後綴，找實際輸出的檔案
            var dir  = Path.GetDirectoryName(outPath)!;
            var stem = Path.GetFileNameWithoutExtension(outPath);
            var file = Directory.EnumerateFiles(dir, $"{stem}*.png").FirstOrDefault() ?? outPath;

            var result = new ScreenshotResult
            {
                ViewName = view.Name,
                FilePath = file,
                Width    = 2048,
                Height   = 2048
            };
            return JsonConvert.SerializeObject(ApiResponse<ScreenshotResult>.Ok(result));
        }

        // ─── 輔助：建立平面圖 ─────────────────────────────────────────────────
        private ViewPlan CreateFloorPlan(CreateViewRequest req)
        {
            var level = FindLevel(req.LevelName)
                        ?? _doc.ActiveView.GenLevel
                        ?? throw new InvalidOperationException("No level specified and no active level found.");

            var vft = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .First(vt => vt.ViewFamily == ViewFamily.FloorPlan);

            return ViewPlan.Create(_doc, vft.Id, level.Id);
        }

        // ─── 輔助：建立剖面圖 ─────────────────────────────────────────────────
        private ViewSection CreateSection(CreateViewRequest req)
        {
            var vft = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .First(vt => vt.ViewFamily == ViewFamily.Section);

            // 預設剖面框（X 方向）
            var bb = new BoundingBoxXYZ
            {
                Min = new XYZ(-100, -50, 0),
                Max = new XYZ(100, 50, 20)
            };
            return ViewSection.CreateSection(_doc, vft.Id, bb);
        }

        // ─── 輔助：建立 3D 視圖 ───────────────────────────────────────────────
        private View3D Create3DView(CreateViewRequest req)
        {
            var vft = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .First(vt => vt.ViewFamily == ViewFamily.ThreeDimensional);

            return View3D.CreateIsometric(_doc, vft.Id);
        }

        private Level? FindLevel(string? name) =>
            string.IsNullOrEmpty(name) ? null :
            new FilteredElementCollector(_doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => l.Name == name);

        private static string GetViewLevelName(View v)
        {
            if (v is ViewPlan vp)
                return v.Document.GetElement(vp.GenLevel?.Id ?? ElementId.InvalidElementId)?.Name ?? "";
            return "";
        }
    }
}
