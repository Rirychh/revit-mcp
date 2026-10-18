// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMcpAddin.Models;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// GET  /design-options : 列出設計選項集、選項、作用中選項、各選項模型元素數（唯讀）。
    /// POST /selection      : 依 Element ID 在 Revit 畫面上選取元素（可縮放到該處），
    ///                        供使用者或畫面操作接著執行「加入集合」等只能在 UI 做的指令。
    /// Revit API 沒有建立選項集／選項、切換作用中選項、指定元素所屬選項的方法，這些只能在 UI 做。
    /// API 新建的元素會落在「目前作用中的選項」。
    /// </summary>
    public class DesignOptionHandler
    {
        private readonly UIApplication _uiApp;
        public DesignOptionHandler(UIApplication uiApp) => _uiApp = uiApp;
        private static string Ok(object o) => JsonConvert.SerializeObject(ApiResponse<object>.Ok(o));

        private Document Resolve(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return _uiApp.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No active Revit document.");
            return _uiApp.Application.Documents.Cast<Document>()
                .Where(d => !d.IsLinked && !d.IsFamilyDocument)
                .FirstOrDefault(d => (d.Title ?? "").IndexOf(key!, StringComparison.OrdinalIgnoreCase) >= 0
                                  || (d.PathName ?? "").IndexOf(key!, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? throw new ArgumentException("找不到開啟中的文件：" + key);
        }

        public string List(NameValueCollection qs)
        {
            var doc = Resolve(qs["document"]);
            var activeId = DesignOption.GetActiveDesignOptionId(doc);
            var opts = new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).Cast<DesignOption>().ToList();

            var counts = new Dictionary<int, int>();
            int mainCount = 0;
            foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                var cat = e.Category;
                if (cat == null || cat.CategoryType != CategoryType.Model) continue;
                if (e is DesignOption) continue;
                DesignOption? d = null;
                try { d = e.DesignOption; } catch { }
                if (d == null) { mainCount++; continue; }
                int k = d.Id.IntegerValue;
                counts[k] = counts.TryGetValue(k, out var c) ? c + 1 : 1;
            }

            var sets = opts
                .GroupBy(o => o.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId()?.IntegerValue ?? -1)
                .Select(g => new
                {
                    setId = g.Key,
                    setName = g.Key > 0 ? (doc.GetElement(new ElementId(g.Key))?.Name ?? "") : "",
                    options = g.Select(o => new
                    {
                        id = o.Id.IntegerValue,
                        name = o.Name,
                        isPrimary = o.IsPrimary,
                        isActive = o.Id == activeId,
                        modelElements = counts.TryGetValue(o.Id.IntegerValue, out var n) ? n : 0
                    }).ToList()
                }).ToList();

            return Ok(new
            {
                document = doc.Title,
                activeOptionId = activeId == ElementId.InvalidElementId ? -1 : activeId.IntegerValue,
                activeOptionName = activeId == ElementId.InvalidElementId ? "（主模型）" : (doc.GetElement(activeId)?.Name ?? ""),
                mainModelElements = mainCount,
                sets,
                note = "Revit API 無法建立選項或切換作用中選項；API 新建元素會落在作用中選項。"
            });
        }

        public string Select(string body)
        {
            var req = JsonConvert.DeserializeObject<SelReq>(body ?? "{}") ?? new SelReq();
            var uidoc = _uiApp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            if (!string.IsNullOrWhiteSpace(req.Document))
            {
                var t = Resolve(req.Document);
                if (!ReferenceEquals(t, uidoc.Document) && t.Title != uidoc.Document.Title)
                    throw new InvalidOperationException($"選取只能作用在畫面上的作用中文件（目前是 {uidoc.Document.Title}），請先 revit_activate_document。");
            }
            var doc = uidoc.Document;
            var ok = new List<ElementId>(); var missing = new List<long>();
            foreach (var i in req.Ids ?? new List<long>())
            {
                var id = new ElementId((int)i);
                if (doc.GetElement(id) != null) ok.Add(id); else missing.Add(i);
            }
            uidoc.Selection.SetElementIds(ok);
            if (req.Zoom && ok.Count > 0) { try { uidoc.ShowElements(ok); } catch { } }
            return Ok(new { document = doc.Title, selected = ok.Count, missing });
        }

        public class SelReq
        {
            [JsonProperty("document")] public string? Document { get; set; }
            [JsonProperty("elementIds")] public List<long>? Ids { get; set; }
            [JsonProperty("zoom")] public bool Zoom { get; set; } = false;
        }
    }
}