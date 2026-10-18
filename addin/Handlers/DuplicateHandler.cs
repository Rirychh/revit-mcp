// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpAddin.Handlers
{
    /// <summary>複製視圖（含詳圖／不含詳圖／從屬）。</summary>
    public class DuplicateHandler
    {
        private readonly Document _doc;
        public DuplicateHandler(Document doc) => _doc = doc;

        private static string Ok(object data) => JsonConvert.SerializeObject(new { success = true, data, error = (string?)null });
        private static string Fail(string msg) => JsonConvert.SerializeObject(new { success = false, data = (object?)null, error = msg });

        // POST /views/duplicate
        // { views:[{viewId, name?}], option?: "withDetailing"|"plain"|"dependent", apply:bool }
        public string DuplicateViews(string body)
        {
            var r = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            bool apply  = (bool?)r["apply"] ?? false;
            string opt  = ((string?)r["option"] ?? "withDetailing").ToLowerInvariant();
            var dupOpt  = opt == "plain"     ? ViewDuplicateOption.Duplicate
                        : opt == "dependent" ? ViewDuplicateOption.AsDependent
                                             : ViewDuplicateOption.WithDetailing;

            var items = new List<(View v, string? name)>();
            foreach (var jv in (r["views"] as JArray) ?? new JArray())
            {
                long vid = (long?)jv["viewId"] ?? 0;
                if (_doc.GetElement(ElementIdCompat.NewId(vid)) is not View v) return Fail($"找不到視圖 {vid}。");
                if (!v.CanViewBeDuplicated(dupOpt)) return Fail($"視圖「{v.Name}」不支援 {dupOpt} 複製。");
                items.Add((v, (string?)jv["name"]));
            }
            if (items.Count == 0) return Fail("需要 views:[{viewId}]。");

            if (!apply)
                return Ok(new
                {
                    applied = false,
                    option  = dupOpt.ToString(),
                    views   = items.Select(t => new
                    {
                        sourceId = t.v.Id.Value,
                        source   = t.v.Name,
                        newName  = t.name ?? (t.v.Name + " 複製")
                    }).ToList()
                });

            var made = new List<object>();
            using (var tx = new Transaction(_doc, "MCP 複製視圖"))
            {
                tx.Start();
                var fo = tx.GetFailureHandlingOptions();
                fo.SetFailuresPreprocessor(new SwallowWarnings());
                tx.SetFailureHandlingOptions(fo);

                foreach (var (v, name) in items)
                {
                    var nid = v.Duplicate(dupOpt);
                    var nv  = _doc.GetElement(nid) as View;
                    if (nv != null) SafeName(nv, name ?? (v.Name + " 複製"));
                    made.Add(new { sourceId = v.Id.Value, source = v.Name, newId = nid.Value, name = nv?.Name ?? "" });
                }
                tx.Commit();
            }
            return Ok(new { applied = true, option = dupOpt.ToString(), count = made.Count, views = made });
        }

        private static void SafeName(View v, string wanted)
        {
            var name = wanted;
            for (int i = 2; i < 100; i++)
            {
                try { v.Name = name; return; }
                catch { name = wanted + "_" + i; }
            }
        }
    }
}