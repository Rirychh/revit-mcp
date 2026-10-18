// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// POST /links/delete-elements
    /// { link: 連結檔名關鍵字, categories?: [OST_名], elementIds?: [連結檔內 id], apply }
    /// 刪除「被連結的 .rvt 檔」裡的元件：
    ///   1. 預檢：列出連結檔中符合的元件（id、品類、類型、範圍）
    ///   2. 執行：在本 Revit 所有開著的主檔卸載該連結 → 開啟連結檔 → 刪除 → 存檔 → 關閉 → 重新載入連結
    /// 連結檔是工作共用檔、或在本 Revit 以一般文件開著有未存變更時拒絕。
    /// 其他 Revit 程序若也連結這個檔，需在那邊手動「重新載入」。
    /// </summary>
    public class LinkEditHandler
    {
        private readonly UIApplication _ui;
        public LinkEditHandler(UIApplication ui) => _ui = ui;
        private static string Ok(object d) => JsonConvert.SerializeObject(new { success = true, data = d, error = (string?)null });
        private static string Fail(string m) => JsonConvert.SerializeObject(new { success = false, data = (object?)null, error = m });
        private const double FT = 304.8;

        public string DeleteElements(string body)
        {
            var r = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            bool apply = (bool?)r["apply"] ?? false;
            string key = (string?)r["link"] ?? "";
            if (key == "") return Fail("需要 link（連結檔名關鍵字）。");
            var host = _ui.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No active document.");

            var lt = new FilteredElementCollector(host).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>()
                .FirstOrDefault(t => t.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);
            if (lt == null) return Fail($"找不到連結「{key}」。");
            var li = new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .FirstOrDefault(i => i.GetTypeId() == lt.Id);
            var ldoc = li?.GetLinkDocument();
            if (ldoc == null) return Fail("連結未載入，無法預覽。");
            string path = ldoc.PathName;
            if (ldoc.IsWorkshared) return Fail("連結檔是工作共用檔，這個工具不處理。");

            var cats = ((r["categories"] as JArray) ?? new JArray()).Select(t => (string)t!).ToList();
            var ids = ((r["elementIds"] as JArray) ?? new JArray()).Select(t => (long)t).ToHashSet();
            if (cats.Count == 0 && ids.Count == 0) return Fail("需要 categories 或 elementIds。");

            List<Element> Collect(Document d)
            {
                var res = new List<Element>();
                foreach (var c in cats)
                {
                    if (!Enum.TryParse<BuiltInCategory>(c.StartsWith("OST_") ? c : "OST_" + c, true, out var bic)) continue;
                    res.AddRange(new FilteredElementCollector(d).OfCategory(bic).WhereElementIsNotElementType().ToElements());
                }
                foreach (var id in ids) { var e = d.GetElement(ElementIdCompat.NewId(id)); if (e != null) res.Add(e); }
                return res.GroupBy(e => e.Id.Value).Select(g => g.First()).ToList();
            }

            object Describe(Element e)
            {
                var bb = e.get_BoundingBox(null);
                return new
                {
                    id = e.Id.Value, category = e.Category?.Name, name = e.Name,
                    type = (e.Document.GetElement(e.GetTypeId()) as ElementType)?.Name,
                    bboxMm = bb == null ? null : new { minX = Math.Round(bb.Min.X * FT), minY = Math.Round(bb.Min.Y * FT), maxX = Math.Round(bb.Max.X * FT), maxY = Math.Round(bb.Max.Y * FT) }
                };
            }

            var preview = Collect(ldoc).Select(Describe).ToList();
            if (!apply) return Ok(new { applied = false, link = lt.Name, path, elements = preview,
                note = "座標為連結檔自身座標。執行時會卸載→開檔刪除→存檔→重新載入。" });
            if (preview.Count == 0) return Fail("連結檔裡沒有符合的元件。");

            // 同程序內開著同一檔（非連結）且有未存變更 → 拒絕
            var openSame = _ui.Application.Documents.Cast<Document>()
                .FirstOrDefault(d => !d.IsLinked && string.Equals(d.PathName, path, StringComparison.OrdinalIgnoreCase));
            if (openSame != null && openSame.IsModified) return Fail($"{openSame.Title} 在這個 Revit 開著且有未存變更，請先存檔。");

            var notes = new List<string>();
            // 卸載本程序所有主檔中的這個連結
            var hostTypes = new List<(Document doc, RevitLinkType type)>();
            foreach (Document d in _ui.Application.Documents)
            {
                if (d.IsLinked) continue;
                foreach (var t in new FilteredElementCollector(d).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>())
                {
                    var ext = t.GetExternalFileReference();
                    var p = ext == null ? "" : ModelPathUtils.ConvertModelPathToUserVisiblePath(ext.GetAbsolutePath());
                    if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase) && RevitLinkType.IsLoaded(d, t.Id))
                    { t.Unload(null); hostTypes.Add((d, t)); notes.Add($"已在 {d.Title} 卸載連結"); }
                }
            }

            int deleted = 0; string? error = null;
            Document? ed = openSame;
            bool openedHere = false;
            try
            {
                if (ed == null)
                {
                    var mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
                    ed = _ui.Application.OpenDocumentFile(mp, new OpenOptions());
                    openedHere = true;
                }
                var targets = Collect(ed).Select(e => e.Id).ToList();
                using (var tx = new Transaction(ed, "MCP 刪除連結檔元件"))
                {
                    tx.Start();
                    foreach (var id in targets) { try { ed.Delete(id); deleted++; } catch (Exception ex) { notes.Add($"{id.Value}: {ex.Message}"); } }
                    tx.Commit();
                }
                ed.Save();
                notes.Add($"已存檔 {ed.Title}");
            }
            catch (Exception ex) { error = ex.Message; }
            finally
            {
                if (openedHere && ed != null) { try { ed.Close(false); } catch (Exception ex) { notes.Add("關閉失敗：" + ex.Message); } }
                foreach (var (d, t) in hostTypes)
                {
                    try { t.Load(); notes.Add($"已在 {d.Title} 重新載入連結"); }
                    catch (Exception ex) { notes.Add($"{d.Title} 重新載入失敗：{ex.Message}"); }
                }
            }
            if (error != null) return Fail($"刪除失敗：{error}｜{string.Join("；", notes)}");
            return Ok(new { applied = true, link = lt.Name, path, deleted, elements = preview, notes,
                reminder = "其他 Revit 若也連結此檔，請在該 Revit 的「管理連結」按「重新載入」。" });
        }
    }
}
