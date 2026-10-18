// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 警告管理。
    ///   GET  /warnings          — 依描述分組；可篩選、分頁、輸出 CSV
    ///   POST /warnings/resolve  — 只處理可安全自動修正的類型，預設預檢
    /// 自動修正一律不解除群組：群組內元素一律跳過並回報。
    /// </summary>
    public class WarningsHandler
    {
        private readonly Document _doc;
        public WarningsHandler(Document doc) { _doc = doc; }

        private static string Ok(object d) => JsonConvert.SerializeObject(new { success = true, data = d });
        private const double MM = 304.8;

        // 可自動修正的警告：以 FailureDefinitionId 判斷，不依賴介面語言
        private static readonly Dictionary<Guid, string> Kinds = new()
        {
            { BuiltInFailures.OverlapFailures.DuplicateInstances.Guid,        "duplicate-instances" },
            { BuiltInFailures.OverlapFailures.WallRoomSeparationOverlap.Guid, "room-separation-on-wall" },
            { BuiltInFailures.RoomFailures.RoomTagNotInRoom.Guid,             "room-tag-outside" },
        };

        public static string? KindOf(FailureMessage f)
            => Kinds.TryGetValue(f.GetFailureDefinitionId().Guid, out var k) ? k : null;

        private bool Grouped(Element e) => e.GroupId != null && e.GroupId != ElementId.InvalidElementId;

        private object Info(ElementId id)
        {
            var e = _doc.GetElement(id);
            if (e == null) return new { id = id.Value };
            string? fam = null, typ = null, name = null, level = null;
            try
            {
                if (e is FamilyInstance fi) { fam = fi.Symbol?.Family?.Name; typ = fi.Symbol?.Name; }
                else typ = _doc.GetElement(e.GetTypeId())?.Name;
                name = e.Name;
                if (e.LevelId != null && e.LevelId != ElementId.InvalidElementId) level = _doc.GetElement(e.LevelId)?.Name;
            }
            catch { }
            return new { id = id.Value, category = e.Category?.Name, family = fam, type = typ, name, level, grouped = Grouped(e) };
        }

        // GET /warnings?contains=&kind=&limit=50&offset=0&csvPath=
        public string List(NameValueCollection qs)
        {
            var contains = qs["contains"] ?? "";
            var kind = qs["kind"] ?? "";
            int limit = int.TryParse(qs["limit"], out var l) ? l : 50;
            int offset = int.TryParse(qs["offset"], out var o) ? o : 0;
            var csvPath = qs["csvPath"];

            var all = _doc.GetWarnings().ToList();
            var groups = all.GroupBy(w => w.GetDescriptionText())
                .Select(g => new
                {
                    description = g.Key,
                    count = g.Count(),
                    severity = g.First().GetSeverity().ToString(),
                    autoFix = KindOf(g.First()),
                    failureId = g.First().GetFailureDefinitionId().Guid,
                })
                .OrderByDescending(x => x.count).ToList();

            var filtered = all.Where(w =>
                    (contains == "" || w.GetDescriptionText().IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0) &&
                    (kind == "" || KindOf(w) == kind))
                .ToList();

            var items = filtered.Skip(offset).Take(limit).Select((w, i) => new
            {
                index = offset + i,
                description = w.GetDescriptionText(),
                autoFix = KindOf(w),
                elements = w.GetFailingElements().Concat(w.GetAdditionalElements()).Distinct().Select(Info).ToList(),
            }).ToList();

            string? csvWritten = null;
            if (!string.IsNullOrWhiteSpace(csvPath))
            {
                var sb = new StringBuilder();
                sb.AppendLine("序號,警告描述,可自動修正,元件ID,品類,族,類型,樓層,群組內");
                int n = 0;
                foreach (var w in filtered)
                {
                    n++;
                    foreach (var id in w.GetFailingElements().Concat(w.GetAdditionalElements()).Distinct())
                    {
                        var e = _doc.GetElement(id);
                        string fam = "", typ = "", lev = "";
                        try
                        {
                            if (e is FamilyInstance fi) { fam = fi.Symbol?.Family?.Name ?? ""; typ = fi.Symbol?.Name ?? ""; }
                            else if (e != null) typ = _doc.GetElement(e.GetTypeId())?.Name ?? "";
                            if (e != null && e.LevelId != ElementId.InvalidElementId) lev = _doc.GetElement(e.LevelId)?.Name ?? "";
                        }
                        catch { }
                        sb.AppendLine(string.Join(",", new[] { n.ToString(), Csv(w.GetDescriptionText()), KindOf(w) ?? "",
                            id.Value.ToString(), Csv(e?.Category?.Name ?? ""), Csv(fam), Csv(typ), Csv(lev),
                            e != null && Grouped(e) ? "Y" : "" }));
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
                File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(true));   // BOM：Excel 直接開中文不亂碼
                csvWritten = csvPath;
            }

            return Ok(new { total = all.Count, groups, matched = filtered.Count, offset, limit, items, csv = csvWritten });
        }

        private static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

        // POST /warnings/resolve { kinds:["duplicate-instances",...], apply:false, maxFixes:500 }
        public string Resolve(string body)
        {
            var r = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            var kinds = (r["kinds"] as JArray)?.Select(x => (string)x!).ToHashSet() ?? new HashSet<string>(Kinds.Values);
            bool apply = (bool?)r["apply"] ?? false;
            int maxFixes = (int?)r["maxFixes"] ?? 500;

            int before = _doc.GetWarnings().Count;
            var plans = new List<Plan>();
            var skipped = new List<object>();
            var seen = new HashSet<long>();

            foreach (var w in _doc.GetWarnings())
            {
                var k = KindOf(w);
                if (k == null || !kinds.Contains(k)) continue;
                var ids = w.GetFailingElements().Concat(w.GetAdditionalElements()).Distinct().ToList();
                switch (k)
                {
                    case "duplicate-instances": PlanDuplicate(ids, plans, skipped, seen); break;
                    case "room-separation-on-wall": PlanRoomSep(ids, plans, skipped, seen); break;
                    case "room-tag-outside": PlanRoomTag(ids, plans, skipped, seen); break;
                }
            }

            var planned = plans.Take(maxFixes).ToList();
            int done = 0;
            var failed = new List<object>();
            if (apply)
            {
                foreach (var p in planned)
                {
                    var pre = new RollbackOnError();
                    try
                    {
                        using var tx = new Transaction(_doc, "MCP 修正警告 " + p.Kind);
                        tx.Start();
                        var fo = tx.GetFailureHandlingOptions();
                        fo.SetFailuresPreprocessor(pre);
                        fo.SetClearAfterRollback(true);
                        fo.SetForcedModalHandling(false);
                        tx.SetFailureHandlingOptions(fo);
                        p.Do();
                        var st = tx.Commit();
                        if (st == TransactionStatus.Committed && !pre.HadError) done++;
                        else failed.Add(new { p.Kind, p.ElementId, status = st.ToString(), reasons = pre.Errors.Distinct().Take(3) });
                    }
                    catch (Exception ex) { failed.Add(new { p.Kind, p.ElementId, status = "exception", reasons = new[] { ex.Message } }); }
                }
            }

            return Ok(new
            {
                applied = apply,
                warningsBefore = before,
                warningsAfter = apply ? _doc.GetWarnings().Count : (int?)null,
                plannedCount = plans.Count,
                executedLimit = maxFixes,
                fixedCount = done,
                summary = plans.GroupBy(p => p.Kind).Select(g => new { kind = g.Key, count = g.Count() }),
                actions = planned.Select(p => new { p.Kind, p.ElementId, p.Action, p.Detail }),
                skippedCount = skipped.Count,
                skipped = skipped.Take(200),
                failed,
            });
        }

        private class Plan
        {
            public string Kind = "";
            public long ElementId;
            public string Action = "";
            public string Detail = "";
            public Action Do = () => { };
        }

        private bool HasHosted(Element e)
        {
            try
            {
                return e.GetDependentElements(new ElementClassFilter(typeof(FamilyInstance)))
                        .Any(id => id != e.Id);
            }
            catch { return false; }
        }

        // 重疊的相同實例：保留 id 最小者，刪除其餘；有寄宿元件（門窗等）的不刪
        private void PlanDuplicate(List<ElementId> ids, List<Plan> plans, List<object> skipped, HashSet<long> seen)
        {
            var els = ids.Select(i => _doc.GetElement(i)).Where(e => e != null).OrderBy(e => e.Id.Value).ToList();
            if (els.Count < 2) return;
            if (els.Any(Grouped)) { skipped.Add(new { kind = "duplicate-instances", ids = els.Select(e => e.Id.Value), reason = "群組內元素（不解除群組）" }); return; }

            // 優先保留有寄宿元件的那個
            var keep = els.FirstOrDefault(HasHosted) ?? els[0];
            foreach (var e in els.Where(x => x.Id != keep.Id))
            {
                if (!seen.Add(e.Id.Value)) continue;
                if (e.Pinned) { skipped.Add(new { kind = "duplicate-instances", id = e.Id.Value, reason = "已釘選" }); continue; }
                if (HasHosted(e)) { skipped.Add(new { kind = "duplicate-instances", id = e.Id.Value, reason = "有寄宿元件，刪除會連帶刪除" }); continue; }
                var id = e.Id;
                plans.Add(new Plan
                {
                    Kind = "duplicate-instances", ElementId = id.Value, Action = "delete",
                    Detail = $"{e.Category?.Name}｜{e.Name}，保留 {keep.Id.Value}",
                    Do = () => _doc.Delete(id),
                });
            }
        }

        // 與房間邊界牆重疊的房間分隔線：牆為房間邊界、且線完全落在牆厚範圍內才刪
        private void PlanRoomSep(List<ElementId> ids, List<Plan> plans, List<object> skipped, HashSet<long> seen)
        {
            var els = ids.Select(i => _doc.GetElement(i)).Where(e => e != null).ToList();
            var line = els.OfType<CurveElement>().FirstOrDefault(c => c.Category?.Id.Value == (long)BuiltInCategory.OST_RoomSeparationLines);
            var wall = els.OfType<Wall>().FirstOrDefault();
            if (line == null || wall == null) return;
            if (!seen.Add(line.Id.Value)) return;
            if (Grouped(line)) { skipped.Add(new { kind = "room-separation-on-wall", id = line.Id.Value, reason = "群組內元素（不解除群組）" }); return; }

            var rb = wall.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING)?.AsInteger() ?? 0;
            if (rb != 1) { skipped.Add(new { kind = "room-separation-on-wall", id = line.Id.Value, reason = "牆不是房間邊界，分隔線仍需要" }); return; }

            var wc = (wall.Location as LocationCurve)?.Curve;
            var lc = line.GeometryCurve;
            if (wc == null || lc == null) return;
            double tol = wall.Width / 2 + 5 / MM;
            double z = wc.GetEndPoint(0).Z;
            bool inside = true;
            foreach (var t in new[] { 0.0, 0.5, 1.0 })
            {
                var p = lc.Evaluate(t, true);
                var q = new XYZ(p.X, p.Y, z);
                var pr = wc.Project(q);
                if (pr == null || pr.Distance > tol) { inside = false; break; }
            }
            if (!inside) { skipped.Add(new { kind = "room-separation-on-wall", id = line.Id.Value, reason = "分隔線只部分與牆重疊" }); return; }

            var id = line.Id;
            plans.Add(new Plan
            {
                Kind = "room-separation-on-wall", ElementId = id.Value, Action = "delete",
                Detail = $"分隔線長 {Math.Round(lc.Length * MM)} mm，牆 {wall.Id.Value}（{wall.Name}）",
                Do = () => _doc.Delete(id),
            });
        }

        // 房間標籤在房間外：移到房間點（保留標籤高度）
        private void PlanRoomTag(List<ElementId> ids, List<Plan> plans, List<object> skipped, HashSet<long> seen)
        {
            foreach (var tag in ids.Select(i => _doc.GetElement(i)).OfType<RoomTag>())
            {
                if (!seen.Add(tag.Id.Value)) continue;
                if (Grouped(tag)) { skipped.Add(new { kind = "room-tag-outside", id = tag.Id.Value, reason = "群組內元素（不解除群組）" }); continue; }
                var room = tag.Room;
                if (room == null) { skipped.Add(new { kind = "room-tag-outside", id = tag.Id.Value, reason = "標籤對應連結模型房間或房間已刪" }); continue; }
                var rp = (room.Location as LocationPoint)?.Point;
                var tp = (tag.Location as LocationPoint)?.Point;
                if (rp == null || tp == null) { skipped.Add(new { kind = "room-tag-outside", id = tag.Id.Value, reason = "房間未放置" }); continue; }
                if (!room.IsPointInRoom(new XYZ(rp.X, rp.Y, rp.Z + 1)))
                { skipped.Add(new { kind = "room-tag-outside", id = tag.Id.Value, reason = "房間點本身不在房間內，需人工處理" }); continue; }

                var delta = new XYZ(rp.X - tp.X, rp.Y - tp.Y, 0);
                var id = tag.Id;
                plans.Add(new Plan
                {
                    Kind = "room-tag-outside", ElementId = id.Value, Action = "move",
                    Detail = $"{room.Number} {room.Name}，位移 {Math.Round(delta.GetLength() * MM)} mm",
                    Do = () => ElementTransformUtils.MoveElement(_doc, id, delta),
                });
            }
        }
    }
}
