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

        /// <summary>
        /// POST /selection/category：把某品類的元素全部選起來（整個專案或某視圖內，可限定樓層），
        /// 給「管理 → 設計選項 → 新增至集合」這類只能在 UI 做的指令接手。
        /// </summary>
        public string SelectCategory(string body)
        {
            var req = JsonConvert.DeserializeObject<SelCatReq>(body ?? "{}") ?? new SelCatReq();
            var uidoc = _uiApp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document;
            if (req.Categories == null || req.Categories.Count == 0)
                throw new ArgumentException("categories 必填，例如 [\"OST_StructuralColumns\"] 或 [\"結構柱\"]。");

            var catIds = new List<ElementId>(); var unknown = new List<string>();
            foreach (var c in req.Categories)
            {
                ElementId? id = null;
                if (Enum.TryParse<BuiltInCategory>(c, true, out var bic))
                {
                    try { var cat = Category.GetCategory(doc, bic); if (cat != null) id = cat.Id; } catch { }
                }
                if (id == null)
                    foreach (Category cat in doc.Settings.Categories)
                        if (string.Equals(cat.Name, c, StringComparison.OrdinalIgnoreCase)) { id = cat.Id; break; }
                if (id == null) unknown.Add(c); else catIds.Add(id);
            }
            if (catIds.Count == 0) throw new ArgumentException("找不到品類：" + string.Join(", ", unknown));

            FilteredElementCollector col = req.ViewId.HasValue
                ? new FilteredElementCollector(doc, ElementIdCompat.NewId(req.ViewId.Value))
                : new FilteredElementCollector(doc);
            var els = col.WherePasses(new ElementMulticategoryFilter(catIds)).WhereElementIsNotElementType().ToElements();

            var ids = new List<ElementId>();
            var byLevel = new Dictionary<string, int>();
            foreach (var e in els)
            {
                string lvName = "";
                try
                {
                    var lid = e.LevelId;
                    if ((lid == null || lid == ElementId.InvalidElementId))
                    {
                        var p = e.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM) ?? e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
                        lid = p?.AsElementId();
                    }
                    if (lid != null && lid != ElementId.InvalidElementId) lvName = doc.GetElement(lid)?.Name ?? "";
                }
                catch { }
                if (!string.IsNullOrWhiteSpace(req.LevelName) && !string.Equals(lvName, req.LevelName, StringComparison.OrdinalIgnoreCase)) continue;
                if (req.ExcludeInOptions)
                {
                    DesignOption? d = null; try { d = e.DesignOption; } catch { }
                    if (d != null) continue;
                }
                ids.Add(e.Id);
                var k = string.IsNullOrEmpty(lvName) ? "（無樓層）" : lvName;
                byLevel[k] = byLevel.TryGetValue(k, out var n) ? n + 1 : 1;
            }

            if (req.Apply)
            {
                uidoc.Selection.SetElementIds(ids);
                if (req.Zoom && ids.Count > 0) { try { uidoc.ShowElements(ids); } catch { } }
            }
            return Ok(new
            {
                document = doc.Title,
                applied = req.Apply,
                count = ids.Count,
                byLevel,
                unknownCategories = unknown,
                next = req.Apply ? "已選取。接著在 Revit：管理 → 設計選項 → 新增至集合，勾選目標選項。" : "預檢；apply=true 才會真的選取。"
            });
        }

        /// <summary>
        /// POST /design-options/move：把元素移進「目前作用中的設計選項」。
        /// Revit API 沒有直接改元素所屬選項的方法；做法與 UI「新增至集合」相同概念：
        /// 原地複製（複本會落在作用中選項）→ 刪除原件，單一交易，任何錯誤整批回滾。
        /// 作用中選項只能在 UI 切換（狀態列「設計選項」下拉）。
        /// 限制：元件 id 會改變；附在原件上的標籤／尺寸會隨原件刪除；網格、樓層、參考平面等基準元素不能進選項。
        /// </summary>
        public string Move(string body)
        {
            var req = JsonConvert.DeserializeObject<MoveReq>(body ?? "{}") ?? new MoveReq();
            var doc = Resolve(req.Document);
            var activeId = DesignOption.GetActiveDesignOptionId(doc);
            if (activeId == ElementId.InvalidElementId)
                throw new InvalidOperationException("目前作用中的是主模型。請先在 Revit 狀態列的「設計選項」下拉切換到目標選項，再執行。");
            var active = doc.GetElement(activeId) as DesignOption;
            if (req.Ids == null || req.Ids.Count == 0) throw new ArgumentException("elementIds 必填。");

            var movable = new List<ElementId>();
            var skipped = new List<object>();
            var dependents = 0;
            foreach (var raw in req.Ids.Distinct())
            {
                var e = doc.GetElement(ElementIdCompat.NewId(raw));
                string? why = null;
                if (e == null) why = "找不到元件";
                else if (e is Grid || e is Level || e is ReferencePlane || e is DatumPlane) why = "基準元素（網格／樓層／參考平面）不能放進設計選項";
                else if (e is View || e is DesignOption || e is ElementType) why = "不是模型元素";
                else if (e.ViewSpecific) why = "視圖專屬元素（詳圖線、標註等）不能放進設計選項";
                else if (e.Pinned) why = "已釘選，請先解除釘選";
                else if (e.GroupId != null && e.GroupId != ElementId.InvalidElementId) why = "在群組內，請先解散群組";
                else
                {
                    DesignOption? cur = null; try { cur = e.DesignOption; } catch { }
                    if (cur != null && cur.Id == activeId) why = "已在目標選項";
                }
                if (why != null) { skipped.Add(new { id = raw, reason = why }); continue; }
                movable.Add(e!.Id);
                try
                {
                    dependents += e!.GetDependentElements(new ElementClassFilter(typeof(IndependentTag))).Count
                                + e.GetDependentElements(new ElementClassFilter(typeof(Dimension))).Count;
                }
                catch { }
            }

            if (!req.Apply)
            {
                return Ok(new
                {
                    document = doc.Title, preflight = true,
                    targetOption = active?.Name ?? "", targetOptionId = activeId.IntegerValue,
                    willMove = movable.Count, skipped,
                    tagsAndDimensionsLost = dependents,
                    note = "apply=true 才會執行。原件會刪除、改成作用中選項裡的複本，元件 id 會改變。"
                });
            }
            if (movable.Count == 0) return Ok(new { document = doc.Title, applied = false, moved = 0, skipped });

            var map = new List<object>();
            var wrongOption = new List<long>();
            using (var t = new Transaction(doc, "MCP: 移至設計選項 " + (active?.Name ?? "")))
            {
                var fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new SwallowWarnings());
                t.SetFailureHandlingOptions(fho);
                t.Start();
                var copies = ElementTransformUtils.CopyElements(doc, movable, XYZ.Zero).ToList();
                foreach (var c in copies)
                {
                    DesignOption? d = null; try { d = doc.GetElement(c)?.DesignOption; } catch { }
                    if (d == null || d.Id != activeId) wrongOption.Add(c.IntegerValue);
                }
                if (wrongOption.Count > 0)
                {
                    t.RollBack();
                    throw new InvalidOperationException($"複本沒有落在作用中選項（{wrongOption.Count} 個），已整批回滾、模型未變更。");
                }
                doc.Delete(movable);
                t.Commit();
                // CopyElements 回傳順序不保證與輸入一一對應，只在數量相同時列出對照
                if (copies.Count == movable.Count)
                    for (int i = 0; i < copies.Count; i++) map.Add(new { oldId = movable[i].IntegerValue, newId = copies[i].IntegerValue });
                return Ok(new
                {
                    document = doc.Title, applied = true,
                    targetOption = active?.Name ?? "",
                    moved = movable.Count, newIds = copies.Select(c => c.IntegerValue).ToList(),
                    idMap = map, idMapNote = "對照依回傳順序，僅供參考",
                    skipped, tagsAndDimensionsLost = dependents
                });
            }
        }

        /// <summary>
        /// POST /design-options/add-to-set：選取指定元素，觸發 Revit 原生指令「管理 → 設計選項 → 新增至集合」（PostableCommand.AddToSet）。
        /// 不需要先切換作用中選項；元件 id 與附著的標註依 Revit 原生行為處理。
        /// 指令在本次請求結束後才執行並跳出勾選對話框，對話框由 MCP server 端以 UI Automation 勾選後按確定，或由使用者手動完成。
        /// </summary>
        public string AddToSet(string body)
        {
            var req = JsonConvert.DeserializeObject<MoveReq>(body ?? "{}") ?? new MoveReq();
            var uidoc = _uiApp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc = uidoc.Document;
            if (!string.IsNullOrWhiteSpace(req.Document))
            {
                var t = Resolve(req.Document);
                if (t.Title != doc.Title)
                    throw new InvalidOperationException($"新增至集合只能作用在畫面上的作用中文件（目前是 {doc.Title}），請先 revit_activate_document。");
            }
            if (req.Ids == null || req.Ids.Count == 0) throw new ArgumentException("elementIds 必填。");

            var ok = new List<ElementId>(); var skipped = new List<object>();
            foreach (var raw in req.Ids.Distinct())
            {
                var e = doc.GetElement(ElementIdCompat.NewId(raw));
                string? why = null;
                if (e == null) why = "找不到元件";
                else if (e is Grid || e is Level || e is ReferencePlane || e is DatumPlane) why = "基準元素（網格／樓層／參考平面）不能放進設計選項";
                else if (e.ViewSpecific) why = "視圖專屬元素不能放進設計選項";
                else if (e.GroupId != null && e.GroupId != ElementId.InvalidElementId) why = "在群組內";
                if (why != null) skipped.Add(new { id = raw, reason = why }); else ok.Add(e!.Id);
            }
            if (!req.Apply)
                return Ok(new { document = doc.Title, preflight = true, willAdd = ok.Count, skipped,
                                note = "apply=true 會選取這些元素並開啟「新增至集合」對話框。" });
            if (ok.Count == 0) return Ok(new { document = doc.Title, applied = false, added = 0, skipped });

            uidoc.Selection.SetElementIds(ok);
            var cmd = RevitCommandId.LookupPostableCommandId(PostableCommand.AddToSet);
            if (cmd == null || !_uiApp.CanPostCommand(cmd))
                throw new InvalidOperationException("目前無法觸發「新增至集合」（可能有其他指令進行中）。元素已選取，可在 UI 手動執行。");
            _uiApp.PostCommand(cmd);
            return Ok(new
            {
                document = doc.Title, applied = true, selected = ok.Count, skipped,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                next = "已觸發「新增至集合」，對話框即將出現：勾選目標選項、取消其他，按確定。"
            });
        }

        private class SwallowWarnings : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                foreach (var f in fa.GetFailureMessages())
                    if (f.GetSeverity() == FailureSeverity.Warning) fa.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }

        public class MoveReq
        {
            [JsonProperty("document")] public string? Document { get; set; }
            [JsonProperty("elementIds")] public List<long>? Ids { get; set; }
            [JsonProperty("apply")] public bool Apply { get; set; } = false;
        }

        public class SelCatReq
        {
            [JsonProperty("categories")] public List<string>? Categories { get; set; }
            [JsonProperty("viewId")] public long? ViewId { get; set; }
            [JsonProperty("levelName")] public string? LevelName { get; set; }
            [JsonProperty("excludeInOptions")] public bool ExcludeInOptions { get; set; } = true;
            [JsonProperty("zoom")] public bool Zoom { get; set; } = false;
            [JsonProperty("apply")] public bool Apply { get; set; } = false;
        }

        public class SelReq
        {
            [JsonProperty("document")] public string? Document { get; set; }
            [JsonProperty("elementIds")] public List<long>? Ids { get; set; }
            [JsonProperty("zoom")] public bool Zoom { get; set; } = false;
        }
    }
}