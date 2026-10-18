// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;

namespace RevitMcpAddin.Handlers
{
    public class PurgeRequest
    {
        /// <summary>只看這些品類（BuiltInCategory 名稱或去掉 OST_ 的名稱）；空 = 全部標籤類</summary>
        [JsonProperty("categories")] public List<string> Categories { get; set; } = new();
        /// <summary>true = 只列出不刪除（預設）</summary>
        [JsonProperty("dryRun")]     public bool DryRun { get; set; } = true;
        /// <summary>實際刪除時要刪的族名稱（必填，避免誤刪）</summary>
        [JsonProperty("names")]      public List<string> Names { get; set; } = new();
        /// <summary>只看註解類品類（預設 true）</summary>
        [JsonProperty("annotationOnly")] public bool AnnotationOnly { get; set; } = true;
    }

    /// <summary>列出沒有被放置的族（預設限註解類），並可依名單刪除。</summary>
    public class PurgeHandler
    {
        private readonly Document _doc;
        public PurgeHandler(Document doc) { _doc = doc; }

        private Dictionary<ElementId, int> TypeUsage()
        {
            var counts = new Dictionary<ElementId, int>();
            foreach (var e in new FilteredElementCollector(_doc).WhereElementIsNotElementType())
            {
                var tid = e.GetTypeId();
                if (tid == null || tid == ElementId.InvalidElementId) continue;
                counts[tid] = counts.TryGetValue(tid, out var c) ? c + 1 : 1;
            }
            return counts;
        }

        private bool Match(Family f, PurgeRequest req)
        {
            var cat = f.FamilyCategory;
            if (cat == null) return false;
            if (req.Categories.Count > 0)
            {
                var ok = req.Categories.Any(c =>
                {
                    var n = c.StartsWith("OST_") ? c : "OST_" + c;
                    return string.Equals(cat.Name, c, StringComparison.OrdinalIgnoreCase) ||
                           (Enum.TryParse<BuiltInCategory>(n, true, out var bic) && cat.Id == new ElementId(bic));
                });
                if (!ok) return false;
            }
            else if (req.AnnotationOnly && cat.CategoryType != CategoryType.Annotation)
                return false;
            return true;
        }

        public string Scan(string body)
        {
            var req = string.IsNullOrWhiteSpace(body)
                      ? new PurgeRequest()
                      : JsonConvert.DeserializeObject<PurgeRequest>(body) ?? new PurgeRequest();
            var usage = TypeUsage();
            var unused = new List<object>();
            var used = 0;

            foreach (var fam in new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>())
            {
                if (!Match(fam, req)) continue;
                var symIds = fam.GetFamilySymbolIds();
                var total = symIds.Sum(id => usage.TryGetValue(id, out var c) ? c : 0);
                if (total > 0) { used++; continue; }
                unused.Add(new
                {
                    name = fam.Name,
                    category = fam.FamilyCategory?.Name,
                    types = symIds.Select(id => (_doc.GetElement(id) as FamilySymbol)?.Name).ToList(),
                    id = fam.Id.Value,
                });
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                data = new { unusedCount = unused.Count, usedCount = used, unused }
            });
        }

        public string Delete(string body)
        {
            var req = JsonConvert.DeserializeObject<PurgeRequest>(body)
                      ?? throw new ArgumentException("Invalid body.");
            if (req.Names.Count == 0) throw new ArgumentException("names is required (nothing deleted).");

            var usage = TypeUsage();
            var deleted = new List<string>();
            var skipped = new List<object>();

            // dryRun 預設 true，但先前完全沒被讀取 —— 要求預覽的呼叫者照樣刪了東西。
            // 預覽路徑不開 Transaction（唯讀分析不該開交易）。
            if (req.DryRun)
            {
                var wouldDelete = new List<string>();
                foreach (var name in req.Names)
                {
                    var f = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>()
                            .FirstOrDefault(x => x.Name == name);
                    if (f == null) { skipped.Add(new { name, reason = "not found" }); continue; }
                    var used = f.GetFamilySymbolIds().Sum(id => usage.TryGetValue(id, out var c) ? c : 0);
                    if (used > 0) { skipped.Add(new { name, reason = $"still used ({used})" }); continue; }
                    wouldDelete.Add(name);
                }
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    data = new
                    {
                        dryRun = true,
                        deletedCount = 0,
                        wouldDeleteCount = wouldDelete.Count,
                        wouldDelete,
                        skipped,
                        note = "預覽模式，未刪除任何東西。確認後帶 dryRun=false 才會真正刪除。"
                    }
                });
            }

            using var tx = new Transaction(_doc, "MCP 刪除未使用的族");
            var fo = tx.GetFailureHandlingOptions();
            fo.SetFailuresPreprocessor(new SwallowWarnings());
            tx.SetFailureHandlingOptions(fo);
            tx.Start();

            foreach (var name in req.Names)
            {
                var fam = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>()
                          .FirstOrDefault(f => f.Name == name);
                if (fam == null) { skipped.Add(new { name, reason = "not found" }); continue; }
                var total = fam.GetFamilySymbolIds().Sum(id => usage.TryGetValue(id, out var c) ? c : 0);
                if (total > 0) { skipped.Add(new { name, reason = $"still used ({total})" }); continue; }
                try { _doc.Delete(fam.Id); deleted.Add(name); }
                catch (Exception ex) { skipped.Add(new { name, reason = ex.Message }); }
            }
            tx.Commit();

            return JsonConvert.SerializeObject(new
            {
                success = true,
                data = new { dryRun = false, deletedCount = deleted.Count, deleted, skipped }
            });
        }
    }
}
