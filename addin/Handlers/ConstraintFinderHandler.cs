// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpAddin.Handlers
{
    /// <summary>唯讀：找出哪些尺寸標註／對齊約束參考到指定的元件。</summary>
    public class ConstraintFinderHandler
    {
        private readonly Document _doc;
        public ConstraintFinderHandler(Document doc) => _doc = doc;

        private static string Ok(object data) => JsonConvert.SerializeObject(new { success = true, data, error = (string)null });
        private static string Fail(string msg) => JsonConvert.SerializeObject(new { success = false, data = (object)null, error = msg });

        // POST /elements/constraints  { ids:[...] }
        public string Find(string body)
        {
            var r = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var arr = r["ids"] as JArray;
            if (arr == null || arr.Count == 0) return Fail("需要 ids。");
            var targets = new HashSet<int>(arr.Select(x => (int)x));

            var hits = new List<object>();
            int scanned = 0;

            foreach (var d in new FilteredElementCollector(_doc).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                scanned++;
                ReferenceArray refs;
                try { refs = d.References; } catch { continue; }
                if (refs == null) continue;

                var touched = new List<int>();
                foreach (Reference rf in refs)
                {
                    try
                    {
                        var eid = rf.ElementId;
                        if (eid != null && eid != ElementId.InvalidElementId && targets.Contains(eid.IntegerValue))
                            touched.Add(eid.IntegerValue);
                    }
                    catch { }
                }
                if (touched.Count == 0) continue;

                string viewName = "";
                try
                {
                    if (d.OwnerViewId != null && d.OwnerViewId != ElementId.InvalidElementId)
                        viewName = (_doc.GetElement(d.OwnerViewId) as View)?.Name ?? "";
                }
                catch { }

                string catName = "";
                try { catName = d.Category?.Name ?? ""; } catch { }

                string val = "";
                try { val = d.ValueString ?? ""; } catch { }

                hits.Add(new
                {
                    constraintId = d.Id.IntegerValue,
                    category = catName,
                    typeName = d.DimensionType?.Name,
                    view = viewName,
                    workset = d.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM)?.AsValueString(),
                    value = val,
                    refCount = refs.Size,
                    touches = touched.Distinct().ToList()
                });
            }

            var byTarget = targets.ToDictionary(
                t => t.ToString(),
                t => hits.Where(h => ((List<int>)h.GetType().GetProperty("touches").GetValue(h)).Contains(t))
                         .Select(h => (int)h.GetType().GetProperty("constraintId").GetValue(h)).ToList());

            return Ok(new { scannedDimensions = scanned, hitCount = hits.Count, hits, byTarget });
        }
    }
}