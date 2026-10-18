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
    /// <summary>把工作平面基礎的牆面元件統一轉正：讓族群某個軸對齊世界上方。</summary>
    public class AlignUprightHandler
    {
        private readonly Document _doc;
        public AlignUprightHandler(Document doc) => _doc = doc;

        private static string Ok(object data) => JsonConvert.SerializeObject(new { success = true, data, error = (string)null });
        private static string Fail(string msg) => JsonConvert.SerializeObject(new { success = false, data = (object)null, error = msg });

        private const double F = 304.8;
        private static object V(XYZ p) => p == null ? null : (object)new { x = Math.Round(p.X * F, 1), y = Math.Round(p.Y * F, 1), z = Math.Round(p.Z * F, 1) };
        private static object D(XYZ p) => p == null ? null : (object)new { x = Math.Round(p.X, 4), y = Math.Round(p.Y, 4), z = Math.Round(p.Z, 4) };

        // POST /elements/align-upright
        // { familyName:"BAF_衛生紙架", ids:[..], upAxis:"Y"|"X", apply:false, includeGrouped:false, tolDeg:1.0 }
        public string Align(string body)
        {
            var r = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            string famName = (string)r["familyName"] ?? "";
            string upAxis = ((string)r["upAxis"] ?? "Y").ToUpperInvariant();
            bool apply = (bool?)r["apply"] ?? false;
            bool includeGrouped = (bool?)r["includeGrouped"] ?? false;
            double tolDeg = (double?)r["tolDeg"] ?? 1.0;

            var idList = (r["ids"] as JArray)?.Select(x => new ElementId((int)x)).ToList();

            List<FamilyInstance> targets;
            if (idList != null && idList.Count > 0)
                targets = idList.Select(i => _doc.GetElement(i)).OfType<FamilyInstance>().ToList();
            else if (famName != "")
                targets = new FilteredElementCollector(_doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                          .Where(f => f.Symbol?.Family?.Name == famName).ToList();
            else return Fail("需要 familyName 或 ids。");

            if (targets.Count == 0) return Fail("沒有找到任何實例。");

            var rows = new List<object>();
            var toRotate = new List<Tuple<ElementId, XYZ, XYZ, double>>();

            foreach (var fi in targets)
            {
                int gid = (fi.GroupId != null && fi.GroupId != ElementId.InvalidElementId) ? fi.GroupId.IntegerValue : 0;
                string gname = gid != 0 ? _doc.GetElement(fi.GroupId)?.Name : null;
                int gtid = 0; string gtname = null;
                if (gid != 0)
                {
                    try
                    {
                        var grp = _doc.GetElement(fi.GroupId) as Group;
                        if (grp?.GroupType != null) { gtid = grp.GroupType.Id.IntegerValue; gtname = grp.GroupType.Name; }
                    }
                    catch { }
                }

                Transform t;
                try { t = fi.GetTransform(); }
                catch (Exception ex) { rows.Add(new { id = fi.Id.IntegerValue, groupId = gid, groupName = gname, status = "no-transform", note = ex.Message }); continue; }

                XYZ bx = t.BasisX, by = t.BasisY, bz = t.BasisZ;
                XYZ origin = (fi.Location as LocationPoint)?.Point ?? t.Origin;
                XYZ cur = upAxis == "X" ? bx : by;
                XYZ up = XYZ.BasisZ;

                XYZ tgt = up - bz.Multiply(up.DotProduct(bz));
                double tl = tgt.GetLength();
                if (tl < 1e-6)
                {
                    rows.Add(new { id = fi.Id.IntegerValue, groupId = gid, groupName = gname, status = "horizontal-face",
                                   basisX = D(bx), basisY = D(by), basisZ = D(bz), origin = V(origin) });
                    continue;
                }
                tgt = tgt.Divide(tl);

                double ang = Math.Atan2(bz.DotProduct(cur.CrossProduct(tgt)), cur.DotProduct(tgt));
                double angDeg = ang * 180.0 / Math.PI;

                string status;
                if (Math.Abs(angDeg) <= tolDeg) status = "already-upright";
                else if (gid != 0 && !includeGrouped) status = "skipped-grouped";
                else status = apply ? "rotate" : "would-rotate";

                if (status == "rotate") toRotate.Add(Tuple.Create(fi.Id, origin, bz, ang));

                rows.Add(new
                {
                    id = fi.Id.IntegerValue,
                    groupId = gid,
                    groupName = gname,
                    groupTypeId = gtid,
                    groupTypeName = gtname,
                    type = fi.Symbol?.Name,
                    hostId = fi.Host?.Id.IntegerValue ?? 0,
                    basisX = D(bx), basisY = D(by), basisZ = D(bz),
                    origin = V(origin),
                    deviationDeg = Math.Round(angDeg, 2),
                    status
                });
            }

            int done = 0; var errs = new List<string>(); var blocked = new List<object>();
            if (apply && toRotate.Count > 0)
            {
                // 一個元件一筆交易：有錯誤就只回復那一筆，不影響其他元件，也不會彈出對話框。
                foreach (var job in toRotate)
                {
                    var pre = new RollbackOnError();
                    try
                    {
                        using (var tx = new Transaction(_doc, "轉正 " + job.Item1.IntegerValue))
                        {
                            tx.Start();
                            var fo = tx.GetFailureHandlingOptions();
                            fo.SetFailuresPreprocessor(pre);
                            fo.SetClearAfterRollback(true);
                            fo.SetForcedModalHandling(false);
                            tx.SetFailureHandlingOptions(fo);

                            var axis = Line.CreateUnbound(job.Item2, job.Item3);
                            ElementTransformUtils.RotateElement(_doc, job.Item1, axis, job.Item4);

                            var st = tx.Commit();
                            if (st == TransactionStatus.Committed && !pre.HadError) done++;
                            else blocked.Add(new { id = job.Item1.IntegerValue, status = st.ToString(),
                                                   reasons = pre.Errors.Distinct().Take(5).ToList() });
                        }
                    }
                    catch (Exception ex) { errs.Add(job.Item1.IntegerValue + ": " + ex.Message); }
                }
            }

            var summary = rows.GroupBy(x => (string)x.GetType().GetProperty("status").GetValue(x))
                              .Select(g => new { status = g.Key, count = g.Count() }).ToList();

            return Ok(new { applied = apply, total = targets.Count, rotated = done, blockedCount = blocked.Count, blocked, upAxis, summary, errors = errs, elements = rows });
        }
    }
}