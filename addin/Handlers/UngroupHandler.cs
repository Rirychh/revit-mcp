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
    /// <summary>解除指定的群組實例（只動被指名的 groupId，不碰同型式的其他實例）。</summary>
    public class UngroupHandler
    {
        private readonly Document _doc;
        public UngroupHandler(Document doc) => _doc = doc;

        private static string Ok(object data) => JsonConvert.SerializeObject(new { success = true, data, error = (string)null });
        private static string Fail(string msg) => JsonConvert.SerializeObject(new { success = false, data = (object)null, error = msg });

        // POST /groups/ungroup  { groupIds:[...], apply:false }
        public string Ungroup(string body)
        {
            var r = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var arr = r["groupIds"] as JArray;
            if (arr == null || arr.Count == 0) return Fail("需要 groupIds。");
            bool apply = (bool?)r["apply"] ?? false;

            var rows = new List<object>();
            var targets = new List<Group>();

            foreach (var x in arr.Select(v => (int)v).Distinct())
            {
                var el = _doc.GetElement(new ElementId(x));
                if (el == null) { rows.Add(new { groupId = x, status = "not-found" }); continue; }
                var g = el as Group;
                if (g == null) { rows.Add(new { groupId = x, status = "not-a-group", kind = el.GetType().Name }); continue; }

                int memberCount = 0;
                try { memberCount = g.GetMemberIds().Count; } catch { }

                int siblings = 0;
                try
                {
                    var gt = g.GroupType;
                    if (gt != null) siblings = new FilteredElementCollector(_doc).OfClass(typeof(Group)).Cast<Group>()
                        .Count(o => o.GroupType != null && o.GroupType.Id == gt.Id);
                }
                catch { }

                rows.Add(new
                {
                    groupId = x,
                    name = g.Name,
                    typeName = g.GroupType != null ? g.GroupType.Name : null,
                    typeId = g.GroupType != null ? g.GroupType.Id.IntegerValue : 0,
                    members = memberCount,
                    instancesOfSameType = siblings,
                    level = (_doc.GetElement(g.LevelId) as Level) != null ? (_doc.GetElement(g.LevelId) as Level).Name : null,
                    status = apply ? "ungroup" : "would-ungroup"
                });
                targets.Add(g);
            }

            int done = 0; var errs = new List<string>(); var freed = new List<int>();
            if (apply && targets.Count > 0)
            {
                using (var tx = new Transaction(_doc, "解除群組"))
                {
                    tx.Start();
                    var fo = tx.GetFailureHandlingOptions();
                    fo.SetFailuresPreprocessor(new SwallowWarnings());
                    fo.SetClearAfterRollback(true);
                    fo.SetForcedModalHandling(false);
                    tx.SetFailureHandlingOptions(fo);
                    foreach (var g in targets)
                    {
                        try
                        {
                            var ids = g.UngroupMembers();
                            if (ids != null) freed.AddRange(ids.Select(i => i.IntegerValue));
                            done++;
                        }
                        catch (Exception ex) { errs.Add(g.Id.IntegerValue + ": " + ex.Message); }
                    }
                    tx.Commit();
                }
            }

            return Ok(new { applied = apply, requested = arr.Count, ungrouped = done, freedCount = freed.Count, errors = errs, groups = rows });
        }
    }
}