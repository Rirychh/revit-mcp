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
    /// <summary>元件幾何變換：目前提供繞任意軸旋轉。</summary>
    public class TransformHandler
    {
        private readonly Document _doc;
        public TransformHandler(Document doc) => _doc = doc;

        private static string Ok(object data) => JsonConvert.SerializeObject(new { success = true, data, error = (string?)null });
        private static string Fail(string msg) => JsonConvert.SerializeObject(new { success = false, data = (object?)null, error = msg });

        // POST /elements/rotate
        // { ids:[...], angleDeg:90, axis:"auto"|"x"|"y"|"z", origin:"location"|"bboxCenter", apply:false }
        // axis="auto"：取 bbox 最薄的方向當旋轉軸（面置族群的主體法線），適合把貼牆族群在牆面內轉正。
        public string Rotate(string body)
        {
            var r = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            bool apply = (bool?)r["apply"] ?? false;
            double angleDeg = (double?)r["angleDeg"] ?? 90.0;
            string axisMode = ((string?)r["axis"] ?? "auto").ToLowerInvariant();
            string originMode = ((string?)r["origin"] ?? "location").ToLowerInvariant();

            var ids = (r["ids"] as JArray)?.Select(t => (long)t).ToList() ?? new List<long>();
            if (ids.Count == 0) return Fail("ids 是空的。");

            var plan = new List<object>();
            var targets = new List<(Element el, Line axis)>();

            foreach (var id in ids)
            {
                var el = _doc.GetElement(new ElementId((int)id));
                if (el == null) { plan.Add(new { id, ok = false, reason = "找不到元件" }); continue; }
                if (el.GroupId != null && el.GroupId != ElementId.InvalidElementId)
                { plan.Add(new { id, ok = false, reason = "在群組內，Revit 不允許從外部修改" }); continue; }

                var bb = el.get_BoundingBox(null);
                if (bb == null) { plan.Add(new { id, ok = false, reason = "沒有邊界框" }); continue; }

                XYZ dir;
                if (axisMode == "x") dir = XYZ.BasisX;
                else if (axisMode == "y") dir = XYZ.BasisY;
                else if (axisMode == "z") dir = XYZ.BasisZ;
                else
                {
                    double dx = bb.Max.X - bb.Min.X, dy = bb.Max.Y - bb.Min.Y, dz = bb.Max.Z - bb.Min.Z;
                    if (dx <= dy && dx <= dz) dir = XYZ.BasisX;
                    else if (dy <= dx && dy <= dz) dir = XYZ.BasisY;
                    else dir = XYZ.BasisZ;
                }

                XYZ pt;
                if (originMode == "bboxcenter" || !(el.Location is LocationPoint lp))
                    pt = (bb.Min + bb.Max) / 2.0;
                else
                    pt = lp.Point;

                var axis = Line.CreateBound(pt, pt + dir);
                targets.Add((el, axis));
                plan.Add(new
                {
                    id,
                    ok = true,
                    name = el.Name,
                    axis = dir.IsAlmostEqualTo(XYZ.BasisX) ? "X" : dir.IsAlmostEqualTo(XYZ.BasisY) ? "Y" : "Z",
                    originMm = new { x = Math.Round(pt.X * 304.8, 1), y = Math.Round(pt.Y * 304.8, 1), z = Math.Round(pt.Z * 304.8, 1) }
                });
            }

            if (!apply)
                return Ok(new { preflight = true, angleDeg, willRotate = targets.Count, total = ids.Count, plan });

            int done = 0;
            var failed = new List<object>();
            using (var tx = new Transaction(_doc, "MCP Rotate Elements"))
            {
                tx.Start();
                var fo = tx.GetFailureHandlingOptions();
                fo.SetFailuresPreprocessor(new SwallowWarnings());
                fo.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(fo);

                foreach (var (el, axis) in targets)
                {
                    try
                    {
                        ElementTransformUtils.RotateElement(_doc, el.Id, axis, angleDeg * Math.PI / 180.0);
                        done++;
                    }
                    catch (Exception ex) { failed.Add(new { id = el.Id.IntegerValue, reason = ex.Message }); }
                }
                tx.Commit();
            }
            return Ok(new { applied = true, angleDeg, rotated = done, failed, total = ids.Count });
        }
    }
}