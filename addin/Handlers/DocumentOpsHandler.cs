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
    /// 文件層級操作：存檔、切換使用中文件、連結 Revit 檔＋取得座標、清除模型元件。
    /// 都可用 "document"（標題或完整路徑的一部分）指定目標文件，不必是使用中文件。
    /// 不碰中心檔同步；工作共用檔只做本機 Save。
    /// </summary>
    public class DocumentOpsHandler
    {
        private readonly UIApplication _uiApp;
        public DocumentOpsHandler(UIApplication uiApp) => _uiApp = uiApp;

        // ── 共用 ───────────────────────────────────────────────
        private Document Resolve(string? key)
        {
            var docs = _uiApp.Application.Documents.Cast<Document>().Where(d => !d.IsLinked).ToList();
            if (string.IsNullOrWhiteSpace(key))
                return _uiApp.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No active Revit document.");
            var hit = docs.FirstOrDefault(d => string.Equals(d.Title, key, StringComparison.OrdinalIgnoreCase))
                   ?? docs.FirstOrDefault(d => (d.Title ?? "").IndexOf(key!, StringComparison.OrdinalIgnoreCase) >= 0)
                   ?? docs.FirstOrDefault(d => (d.PathName ?? "").IndexOf(key!, StringComparison.OrdinalIgnoreCase) >= 0);
            return hit ?? throw new ArgumentException($"找不到開啟中的文件：{key}（開啟中：{string.Join(", ", docs.Select(d => d.Title))}）");
        }

        private static string Ok(object o) => JsonConvert.SerializeObject(ApiResponse<object>.Ok(o));
        private static string Fail(string e) => JsonConvert.SerializeObject(ApiResponse<object>.Fail(e));

        private class Preproc : IFailuresPreprocessor
        {
            public List<string> Notes = new();
            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                bool resolved = false;
                foreach (var f in fa.GetFailureMessages())
                {
                    if (f.GetSeverity() == FailureSeverity.Warning) { fa.DeleteWarning(f); continue; }
                    if (f.HasResolutions()) { fa.ResolveFailure(f); resolved = true; Notes.Add("resolved: " + f.GetDescriptionText()); }
                    else Notes.Add("error: " + f.GetDescriptionText());
                }
                return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
            }
        }

        private static Transaction Begin(Document doc, string name, Preproc pp)
        {
            var t = new Transaction(doc, name);
            var o = t.GetFailureHandlingOptions();
            o.SetFailuresPreprocessor(pp);
            o.SetClearAfterRollback(true);
            o.SetForcedModalHandling(false);
            t.SetFailureHandlingOptions(o);
            t.Start();
            return t;
        }

        // ── GET /documents/info?document= ──────────────────────
        public string Info(System.Collections.Specialized.NameValueCollection qs)
        {
            var doc = Resolve(qs["document"]);
            var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .Select(l => new { id = l.Id.IntegerValue, name = l.Name, loaded = RevitLinkType.IsLoaded(doc, l.GetTypeId()) }).ToList();
            var pp = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
            return Ok(new
            {
                title = doc.Title, path = doc.PathName, modified = doc.IsModified, workshared = doc.IsWorkshared,
                active = _uiApp.ActiveUIDocument?.Document?.Equals(doc) ?? false,
                links,
                projectPosition = new { eastWestM = pp.EastWest * 0.3048, northSouthM = pp.NorthSouth * 0.3048, elevationM = pp.Elevation * 0.3048, angleDeg = pp.Angle * 180 / Math.PI },
                openDocuments = _uiApp.Application.Documents.Cast<Document>().Where(d => !d.IsLinked).Select(d => new { d.Title, d.IsModified }).ToList()
            });
        }

        // ── POST /documents/save {document} ────────────────────
        public string Save(string body)
        {
            var req = JsonConvert.DeserializeObject<DocReq>(body) ?? new DocReq();
            var doc = Resolve(req.Document);
            if (string.IsNullOrEmpty(doc.PathName)) return Fail("文件尚未存過檔（沒有路徑），請用另存。");
            if (doc.IsWorkshared && doc.GetWorksharingCentralModelPath() != null &&
                ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath()).Equals(doc.PathName, StringComparison.OrdinalIgnoreCase))
                return Fail("這是中心檔本身，不能直接 Save，請用同步。");
            bool wasModified = doc.IsModified;
            doc.Save();
            return Ok(new { title = doc.Title, path = doc.PathName, saved = true, wasModified });
        }

        // ── POST /documents/activate {document} ────────────────
        public string Activate(string body)
        {
            var req = JsonConvert.DeserializeObject<DocReq>(body) ?? new DocReq();
            var doc = Resolve(req.Document);
            if (string.IsNullOrEmpty(doc.PathName)) return Fail("文件沒有路徑，無法切換。");
            _uiApp.OpenAndActivateDocument(doc.PathName);
            return Ok(new { active = doc.Title });
        }

        // ── POST /links/revit {document, path, placement, acquireCoordinates, closeIfOpen} ──
        public string LinkRevit(string body)
        {
            var req = JsonConvert.DeserializeObject<LinkReq>(body) ?? throw new ArgumentException("Invalid body.");
            if (string.IsNullOrWhiteSpace(req.Path)) return Fail("請提供 path（要連結的 .rvt 完整路徑）。");
            var doc = Resolve(req.Document);
            var notes = new List<string>();

            // 被連結的檔案若在同一個 Revit 開著，Revit 會拒絕連結
            var openSame = _uiApp.Application.Documents.Cast<Document>()
                .FirstOrDefault(d => !d.IsLinked && string.Equals(d.PathName, req.Path, StringComparison.OrdinalIgnoreCase));
            if (openSame != null)
            {
                if (!req.CloseIfOpen) return Fail($"{openSame.Title} 正開在這個 Revit 裡，不能連結。加 closeIfOpen=true 會在它沒有未存變更時先關閉。");
                if (openSame.IsModified) return Fail($"{openSame.Title} 有未存變更，未關閉。請先存檔。");
                openSame.Close(false);
                notes.Add($"已關閉 {openSame.Title}（無未存變更）");
            }

            string fname = System.IO.Path.GetFileName(req.Path);
            var existing = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                .FirstOrDefault(l => (l.Name ?? "").IndexOf(System.IO.Path.GetFileNameWithoutExtension(req.Path), StringComparison.OrdinalIgnoreCase) >= 0);

            RevitLinkInstance? li = existing;
            if (li == null)
            {
                var pp = new Preproc();
                using var t = Begin(doc, "MCP 連結 " + fname, pp);
                var lt = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>()
                    .FirstOrDefault(x => x.Name.IndexOf(System.IO.Path.GetFileNameWithoutExtension(req.Path), StringComparison.OrdinalIgnoreCase) >= 0);
                ElementId typeId;
                if (lt != null) typeId = lt.Id;
                else
                {
                    var r = RevitLinkType.Create(doc, ModelPathUtils.ConvertUserVisiblePathToModelPath(req.Path), new RevitLinkOptions(false));
                    notes.Add("載入結果：" + r.LoadResult);
                    if (r.ElementId == ElementId.InvalidElementId) { t.RollBack(); return Fail("連結類型建立失敗：" + r.LoadResult); }
                    typeId = r.ElementId;
                }
                var placement = string.Equals(req.Placement, "shared", StringComparison.OrdinalIgnoreCase) ? ImportPlacement.Shared : ImportPlacement.Origin;
                li = RevitLinkInstance.Create(doc, typeId, placement);
                var st = t.Commit();
                notes.AddRange(pp.Notes);
                if (st != TransactionStatus.Committed) return Fail("連結交易未提交：" + st + " " + string.Join("; ", notes));
                notes.Add("建立連結實體（" + placement + "）");
            }
            else notes.Add("已有連結實體，沿用");

            if (req.AcquireCoordinates)
            {
                var ppA = new Preproc();
                using (var ta = Begin(doc, "MCP 取得座標", ppA))
                {
                    doc.AcquireCoordinates(li.Id);
                    var sta = ta.Commit();
                    notes.AddRange(ppA.Notes);
                    if (sta != TransactionStatus.Committed) return Fail("取得座標未提交：" + sta + " " + string.Join("; ", notes));
                }
                notes.Add("已取得共用座標");
            }
            var pos = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
            return Ok(new
            {
                document = doc.Title, linkId = li.Id.IntegerValue, linkName = li.Name, notes,
                projectPosition = new { eastWestM = pos.EastWest * 0.3048, northSouthM = pos.NorthSouth * 0.3048, angleDeg = pos.Angle * 180 / Math.PI }
            });
        }

        // ── POST /documents/clear-model {document, categories?, apply} ──
        private static readonly string[] DefaultCats =
        {
            "OST_IOSModelGroups","OST_Rooms","OST_Areas","OST_Walls","OST_Floors","OST_Roofs","OST_Ceilings","OST_StructuralColumns","OST_Columns",
            "OST_StructuralFraming","OST_StructuralFoundation","OST_Stairs","OST_StairsRailing","OST_Railings","OST_Ramps","OST_Doors","OST_Windows",
            "OST_Furniture","OST_FurnitureSystems","OST_PlumbingFixtures","OST_SpecialityEquipment","OST_MechanicalEquipment","OST_LightingFixtures",
            "OST_ElectricalFixtures","OST_ElectricalEquipment","OST_GenericModel","OST_Casework","OST_Parking","OST_Planting","OST_Site","OST_Entourage",
            "OST_Mass","OST_Topography","OST_Toposolid","OST_ShaftOpening","OST_CurtainWallPanels","OST_CurtainWallMullions","OST_Cornices","OST_EdgeSlab",
            "OST_Fascia","OST_Gutter","OST_RoofSoffit"
        };

        public string ClearModel(string body)
        {
            var req = JsonConvert.DeserializeObject<ClearReq>(body) ?? new ClearReq();
            var doc = Resolve(req.Document);
            var cats = (req.Categories != null && req.Categories.Count > 0 ? req.Categories : DefaultCats.ToList());
            var counts = new Dictionary<string, int>();
            var ids = new List<ElementId>();
            foreach (var c in cats)
            {
                if (!Enum.TryParse<BuiltInCategory>(c.StartsWith("OST_") ? c : "OST_" + c, out var bic)) continue;
                var found = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType()
                    .Where(e => e.GroupId == ElementId.InvalidElementId || bic == BuiltInCategory.OST_IOSModelGroups)
                    .Select(e => e.Id).ToList();
                if (found.Count > 0) { counts[c] = found.Count; ids.AddRange(found); }
            }
            if (!req.Apply) return Ok(new { document = doc.Title, applied = false, total = ids.Count, counts });

            var pp = new Preproc();
            int deleted = 0, failed = 0;
            using (var t = Begin(doc, "MCP 清除模型元件", pp))
            {
                foreach (var id in ids)
                {
                    if (doc.GetElement(id) == null) continue;   // 已隨宿主刪除
                    try { doc.Delete(id); deleted++; } catch { failed++; }
                }
                var st = t.Commit();
                if (st != TransactionStatus.Committed) return Fail("清除交易未提交：" + st + " " + string.Join("; ", pp.Notes.Take(10)));
            }
            return Ok(new { document = doc.Title, applied = true, requested = ids.Count, deleted, failed, counts, notes = pp.Notes.Take(20) });
        }

        public class DocReq   { [JsonProperty("document")] public string? Document { get; set; } }
        public class LinkReq  : DocReq
        {
            [JsonProperty("path")] public string? Path { get; set; }
            [JsonProperty("placement")] public string? Placement { get; set; } = "origin";
            [JsonProperty("acquireCoordinates")] public bool AcquireCoordinates { get; set; }
            [JsonProperty("closeIfOpen")] public bool CloseIfOpen { get; set; }
        }
        public class ClearReq : DocReq
        {
            [JsonProperty("categories")] public List<string>? Categories { get; set; }
            [JsonProperty("apply")] public bool Apply { get; set; }
        }
    }
}
