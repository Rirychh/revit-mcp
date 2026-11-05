// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static RevitMcpAddin.Handlers.PortUtil;
using static RevitMcpAddin.Handlers.ViewPlane;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 標號截圖：暫時在元件上放編號文字（可選擇隔離），輸出視圖圖片後整個交易回滾，模型不留任何變更。
    /// 讓 AI 看圖時能把畫面上的位置對回元件 id。（功能構想參考 MLabs Revit MCP 公開的工具清單，程式獨立實作）
    /// </summary>
    public class VisualLabelHandler
    {
        private readonly Document _doc;
        public VisualLabelHandler(Document doc) => _doc = doc;

        // POST /views/labeled-snapshot
        // { viewId, elementIds?, categories?, maxLabels?:80, isolate?:false, labelBy?:"index"|"id", pixelSize?:2048 }
        public string Snapshot(string body)
        {
            var r = Body(body);
            long? vid = (long?)r["viewId"];
            if (!vid.HasValue) return Fail("viewId 必填。");
            if (Get(_doc, vid.Value) is not View view) return Fail("找不到視圖 " + vid);
            if (view is View3D) return Fail("3D 視圖不能放文字註記，請改用平面、立面、剖面或詳圖視圖。");
            if (view.IsTemplate) return Fail("不能對視圖樣板截圖。");
            int max = Math.Min(300, Math.Max(1, (int?)r["maxLabels"] ?? 80));
            bool isolate = (bool?)r["isolate"] ?? false;
            string labelBy = ((string?)r["labelBy"] ?? "index").ToLowerInvariant();
            int px = Math.Min(8000, Math.Max(512, (int?)r["pixelSize"] ?? 2048));
            bool cropTo = (bool?)r["cropToElements"] ?? true;
            double cropMargin = ((double?)r["cropMarginMm"] ?? 1500) / ViewPlane.FT;

            List<Element> els;
            if (r["elementIds"] is JArray)
                els = Ids(r["elementIds"]).Select(i => Get(_doc, i)).Where(e => e != null).Cast<Element>().ToList();
            else
            {
                var col = new FilteredElementCollector(_doc, view.Id).WhereElementIsNotElementType();
                var cats = Strings(r["categories"]);
                if (cats.Count > 0)
                {
                    var (ok, unknown) = Categories(_doc, cats);
                    if (ok.Count == 0) return Fail("無法辨識的品類：" + string.Join("、", unknown));
                    col = col.WherePasses(CategoryFilter(ok));
                }
                els = col.Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model && !(e is ElementType)).ToList();
            }

            var items = els.Select(e => (e, rect: ElementRect(view, e))).Where(x => x.rect.HasValue)
                .OrderByDescending(x => Math.Round(x.rect!.Value.CV / 500)).ThenBy(x => x.rect!.Value.CU)   // 由上而下、由左而右編號
                .Take(max).ToList();
            if (items.Count == 0) return Fail("這張視圖裡沒有可標號的元件。");
            int skipped = Math.Max(0, els.Count - items.Count);

            var noteType = new FilteredElementCollector(_doc).OfClass(typeof(TextNoteType)).FirstElementId();
            if (noteType == null || noteType == ElementId.InvalidElementId) return Fail("模型裡沒有文字類型（TextNoteType）。");

            var labels = new List<object>();
            string outPath = Path.Combine(Path.GetTempPath(), $"revit_labeled_{view.Id.Value}_{DateTime.Now:yyyyMMddHHmmss}.png");
            string? file = null; string? error = null;

            using (var tx = new Transaction(_doc, "MCP Labeled Snapshot (rolled back)"))
            {
                tx.Start();
                var fo = tx.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(new SwallowWarnings()); tx.SetFailureHandlingOptions(fo);
                try
                {
                    var noteIds = new List<ElementId>();
                    int n = 0;
                    var depthRef = items[0].e.get_BoundingBox(view)?.Min ?? view.Origin;
                    foreach (var (e, rect) in items)
                    {
                        n++;
                        string text = labelBy == "id" ? e.Id.Value.ToString() : n.ToString();
                        var at = ToModel(view, rect!.Value.CU, rect.Value.CV, depthRef);
                        var note = TextNote.Create(_doc, view.Id, at, text, noteType);
                        noteIds.Add(note.Id);
                        labels.Add(new { label = text, elementId = e.Id.Value, category = e.Category?.Name, name = e.Name, centerUv = R2(rect.Value.CU, rect.Value.CV), boxUv = rect.Value.Dto() });
                    }
                    // 暫時把裁剪框縮到被標元件的範圍，截圖不被立面標記等遠處的東西縮小（同樣會回滾）
                    if (cropTo && view.ViewType != ViewType.DraftingView && view.ViewType != ViewType.Legend)
                    {
                        try
                        {
                            var cb = view.CropBox; var tr = cb.Transform ?? Transform.Identity; var inv = tr.Inverse;
                            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                            foreach (var (e, _) in items)
                            {
                                var bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null); if (bb == null) continue;
                                foreach (var x in new[] { bb.Min.X, bb.Max.X }) foreach (var y in new[] { bb.Min.Y, bb.Max.Y }) foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                                { var q = inv.OfPoint(new XYZ(x, y, z)); x0 = Math.Min(x0, q.X); y0 = Math.Min(y0, q.Y); x1 = Math.Max(x1, q.X); y1 = Math.Max(y1, q.Y); }
                            }
                            if (x1 > x0 && y1 > y0)
                            {
                                var nb = new BoundingBoxXYZ { Transform = tr, Min = new XYZ(x0 - cropMargin, y0 - cropMargin, cb.Min.Z), Max = new XYZ(x1 + cropMargin, y1 + cropMargin, cb.Max.Z) };
                                view.CropBoxActive = true; view.CropBoxVisible = false; view.CropBox = nb;
                            }
                        }
                        catch { /* 有些視圖不能改裁剪框，照原範圍截圖 */ }
                    }
                    if (isolate)
                        view.IsolateElementsTemporary(items.Select(x => x.e.Id).Concat(noteIds).ToList());
                    _doc.Regenerate();

                    var opts = new ImageExportOptions
                    {
                        ExportRange = ExportRange.SetOfViews, HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG,
                        FilePath = outPath, ImageResolution = ImageResolution.DPI_150, ZoomType = ZoomFitType.FitToPage, PixelSize = px,
                    };
                    opts.SetViewsAndSheets(new List<ElementId> { view.Id });
                    _doc.ExportImage(opts);
                    var dir = Path.GetDirectoryName(outPath)!; var stem = Path.GetFileNameWithoutExtension(outPath);
                    file = Directory.EnumerateFiles(dir, stem + "*.png").FirstOrDefault() ?? outPath;
                }
                catch (Exception ex) { error = ex.Message; }
                finally { if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack(); }
            }
            if (error != null) return Fail("標號截圖失敗（暫時的標號已回滾，模型沒有變更）：" + error);
            return Ok(new
            {
                view = view.Name, filePath = file, pixelSize = px, isolated = isolate, croppedToElements = cropTo, labeled = labels.Count, notLabeled = skipped,
                note = "圖上的數字對應 labels 裡的 label。標號、隔離與裁剪框都已回滾，模型沒有任何變更。",
                labels,
            });
        }
    }
}
