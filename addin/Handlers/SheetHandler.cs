// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.IO;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 圖紙相關：建圖紙＋放視圖、設開啟畫面、依圖號批次出 PDF、
    /// 圖框修訂欄寫說明、修訂雲形線旁加文字＋引線。
    /// 所有會改模型的路由都支援 apply=false 預檢（預設 false）。
    /// </summary>
    public class SheetHandler
    {
        private const double FT = 304.8;
        private readonly Document _doc;
        public SheetHandler(Document doc) => _doc = doc;

        private static string Ok(object data) => JsonConvert.SerializeObject(new { success = true, data, error = (string?)null });
        private static string Fail(string msg) => JsonConvert.SerializeObject(new { success = false, data = (object?)null, error = msg });
        private static JObject Parse(string body) => string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);

        private Dictionary<string, ViewSheet> SheetsByNumber() =>
            new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .GroupBy(s => s.SheetNumber).ToDictionary(g => g.Key, g => g.First());

        private TextNoteType? FindTextType(string? name)
        {
            var all = new FilteredElementCollector(_doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();
            if (string.IsNullOrWhiteSpace(name)) return all.FirstOrDefault();
            return all.FirstOrDefault(t => t.Name == name);
        }

        private static string TypeName(Element? e) =>
            e?.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM)?.AsString() ?? e?.Name ?? "";

        // ── GET /sheets ─────────────────────────────────────────────
        public string ListSheets()
        {
            var list = new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder).OrderBy(s => s.SheetNumber)
                .Select(s => new { id = s.Id.IntegerValue, number = s.SheetNumber, name = s.Name }).ToList();
            var tbs = new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks).Cast<FamilySymbol>()
                .Select(t => new { id = t.Id.IntegerValue, family = t.FamilyName, type = TypeName(t) }).ToList();
            var start = StartingViewSettings.GetStartingViewSettings(_doc).ViewId;
            return Ok(new { count = list.Count, sheets = list, titleBlocks = tbs, startingViewId = start.IntegerValue });
        }

        // ── POST /sheets/create ─────────────────────────────────────
        // { number, name, titleBlock?: "族群:類型" 或類型名, views?: [{viewId, x?, y?}] (圖紙 mm),
        //   setStartingView?: bool, apply }
        public string CreateSheet(string body)
        {
            var r = Parse(body);
            string number = (string?)r["number"] ?? "";
            string name = (string?)r["name"] ?? "";
            bool apply = (bool?)r["apply"] ?? false;
            if (number == "" || name == "") return Fail("需要 number 與 name。");
            if (SheetsByNumber().ContainsKey(number)) return Fail($"圖號 {number} 已存在。");

            string tbName = (string?)r["titleBlock"] ?? "";
            var tbTypes = new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks).Cast<FamilySymbol>().ToList();
            FamilySymbol? tb = null;
            if (tbName != "")
            {
                tb = tbTypes.FirstOrDefault(t => $"{t.FamilyName}:{TypeName(t)}" == tbName)
                  ?? tbTypes.FirstOrDefault(t => TypeName(t) == tbName)
                  ?? tbTypes.FirstOrDefault(t => t.FamilyName == tbName);
                if (tb == null) return Fail($"找不到圖框「{tbName}」。用 GET /sheets 查 titleBlocks。");
            }

            var views = new List<(View v, double x, double y)>();
            foreach (var jv in (r["views"] as JArray) ?? new JArray())
            {
                long vid = (long?)jv["viewId"] ?? 0;
                if (_doc.GetElement(new ElementId((int)vid)) is not View v) return Fail($"找不到視圖 {vid}。");
                views.Add((v, (double?)jv["x"] ?? 420, (double?)jv["y"] ?? 297));
            }
            bool setStart = (bool?)r["setStartingView"] ?? false;

            if (!apply)
                return Ok(new { applied = false, number, name, titleBlock = tb == null ? "(無)" : $"{tb.FamilyName}:{TypeName(tb)}",
                                views = views.Select(t => t.v.Name).ToList(), setStartingView = setStart });

            ViewSheet sheet;
            var placed = new List<object>();
            using (var tx = new Transaction(_doc, "MCP 建立圖紙"))
            {
                tx.Start();
                if (tb != null && !tb.IsActive) tb.Activate();
                sheet = ViewSheet.Create(_doc, tb?.Id ?? ElementId.InvalidElementId);
                sheet.SheetNumber = number;
                sheet.Name = name;
                foreach (var (v, x, y) in views)
                {
                    if (!Viewport.CanAddViewToSheet(_doc, sheet.Id, v.Id))
                    { placed.Add(new { view = v.Name, ok = false, reason = "無法放置（可能已在其他圖紙）" }); continue; }
                    var vp = Viewport.Create(_doc, sheet.Id, v.Id, new XYZ(x / FT, y / FT, 0));
                    placed.Add(new { view = v.Name, ok = true, viewportId = vp.Id.IntegerValue });
                }
                if (setStart) StartingViewSettings.GetStartingViewSettings(_doc).ViewId = sheet.Id;
                tx.Commit();
            }
            return Ok(new { applied = true, sheetId = sheet.Id.IntegerValue, number, name, viewports = placed, setStartingView = setStart });
        }

        // ── POST /sheets/starting-view { viewId | sheetNumber, apply } ──
        public string SetStartingView(string body)
        {
            var r = Parse(body);
            bool apply = (bool?)r["apply"] ?? false;
            View? v = null;
            if (r["sheetNumber"] != null) { SheetsByNumber().TryGetValue((string)r["sheetNumber"]!, out var s); v = s; }
            else if (r["viewId"] != null) v = _doc.GetElement(new ElementId((int)(long)r["viewId"]!)) as View;
            if (v == null) return Fail("找不到指定的視圖或圖紙。");
            var svs = StartingViewSettings.GetStartingViewSettings(_doc);
            if (!svs.IsAcceptableStartingView(v.Id)) return Fail($"「{v.Name}」不能設為開啟畫面。");
            if (!apply) return Ok(new { applied = false, viewId = v.Id.IntegerValue, viewName = v.Name, currentId = svs.ViewId.IntegerValue });
            using (var tx = new Transaction(_doc, "MCP 設定開啟畫面"))
            { tx.Start(); svs.ViewId = v.Id; tx.Commit(); }
            return Ok(new { applied = true, viewId = v.Id.IntegerValue, viewName = v.Name });
        }

        // ── POST /sheets/export-pdf ─────────────────────────────────
        // { sheetNumbers: [...], folder, separator?: "_", overwrite?: false, apply }
        // 每張一個檔：{圖號}{sep}{圖名}.pdf
        public string ExportPdf(string body)
        {
            var r = Parse(body);
            bool apply = (bool?)r["apply"] ?? false;
            string folder = (string?)r["folder"] ?? "";
            string sep = (string?)r["separator"] ?? "_";
            bool overwrite = (bool?)r["overwrite"] ?? false;
            var nums = ((r["sheetNumbers"] as JArray) ?? new JArray()).Select(t => (string)t!).ToList();
            if (folder == "") return Fail("需要 folder。");
            if (nums.Count == 0) return Fail("需要 sheetNumbers。");

            var map = SheetsByNumber();
            var plan = new List<(ViewSheet s, string file)>();
            var skipped = new List<string>();
            var bad = Path.GetInvalidFileNameChars();
            foreach (var n in nums)
            {
                if (!map.TryGetValue(n, out var s)) { skipped.Add($"{n}：找不到圖紙"); continue; }
                var fname = new string($"{s.SheetNumber}{sep}{s.Name}".Select(c => bad.Contains(c) ? '_' : c).ToArray());
                var full = Path.Combine(folder, fname + ".pdf");
                if (File.Exists(full) && !overwrite) { skipped.Add($"{n}：檔案已存在（overwrite=false）"); continue; }
                plan.Add((s, fname));
            }
            if (!apply)
                return Ok(new { applied = false, folder, toExport = plan.Select(p => p.file + ".pdf").ToList(), skipped });

            Directory.CreateDirectory(folder);
            var done = new List<string>(); var failed = new List<string>();
            foreach (var (s, fname) in plan)
            {
                try
                {
                    var opt = new PDFExportOptions { Combine = true, FileName = fname };
                    var ok = _doc.Export(folder, new List<ElementId> { s.Id }, opt);
                    if (ok) done.Add(fname + ".pdf"); else failed.Add($"{s.SheetNumber}：Export 回傳 false");
                }
                catch (Exception ex) { failed.Add($"{s.SheetNumber}：{ex.Message}"); }
            }
            return Ok(new { applied = true, folder, exported = done, failed, skipped });
        }

        // ── POST /sheets/revision-notes ─────────────────────────────
        // { items: [{sheetNumber, text}], textTypeName?, titleBlocks?: {類型名:{x,y,dy,chars,maxLines}}, apply }
        public string RevisionNotes(string body)
        {
            var r = Parse(body);
            bool apply = (bool?)r["apply"] ?? false;
            var tt = FindTextType((string?)r["textTypeName"] ?? "BAF 3mm 內文");
            if (tt == null) return Fail("找不到文字類型。");

            var cfg = new Dictionary<string, (double x, double y, double dy, int chars, int maxLines)>
            {
                ["範例圖框(執照圖)"] = (119, 42.5, 9.8, 18, 3),
                ["範例圖框(顧問)"]   = (119, 42.5, 9.8, 18, 3),
                ["範例圖框(細設圖)"] = (471.5, 52, 10.5, 22, 4),
            };
            if (r["titleBlocks"] is JObject extra)
                foreach (var p in extra.Properties())
                    cfg[p.Name] = ((double?)p.Value["x"] ?? 119, (double?)p.Value["y"] ?? 42.5, (double?)p.Value["dy"] ?? 9.8,
                                   (int?)p.Value["chars"] ?? 18, (int?)p.Value["maxLines"] ?? 3);

            var map = SheetsByNumber();
            var plan = new List<(ViewSheet s, XYZ o, (double x, double y, double dy, int chars, int maxLines) c, List<string> lines)>();
            var skipped = new List<string>();
            foreach (var it in (r["items"] as JArray) ?? new JArray())
            {
                string n = (string?)it["sheetNumber"] ?? ""; string text = (string?)it["text"] ?? "";
                if (!map.TryGetValue(n, out var s)) { skipped.Add($"{n}：找不到圖紙"); continue; }
                var tb = new FilteredElementCollector(_doc, s.Id).OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsNotElementType().FirstOrDefault() as FamilyInstance;
                if (tb == null) { skipped.Add($"{n}：沒有圖框"); continue; }
                var tn = TypeName(_doc.GetElement(tb.GetTypeId()));
                if (!cfg.TryGetValue(tn, out var c)) { skipped.Add($"{n}：圖框「{tn}」未設定座標"); continue; }
                var o = (tb.Location as LocationPoint)?.Point ?? XYZ.Zero;
                double x0 = o.X + (c.x - 7) / FT, x1 = o.X + (c.x + c.chars * 4.5) / FT;
                double y1 = o.Y + (c.y + 3.5) / FT, y0 = o.Y + (c.y - c.dy * c.maxLines) / FT;
                var hit = new FilteredElementCollector(_doc, s.Id).OfClass(typeof(TextNote)).Cast<TextNote>()
                    .FirstOrDefault(t => t.Coord.X >= x0 && t.Coord.X <= x1 && t.Coord.Y >= y0 && t.Coord.Y <= y1);
                if (hit != null) { skipped.Add($"{n}：修訂欄已有文字「{hit.Text.Trim()}」"); continue; }
                var lines = Wrap(text, c.chars);
                if (lines.Count > c.maxLines) { skipped.Add($"{n}：超過 {c.maxLines} 行"); continue; }
                plan.Add((s, o, c, lines));
            }
            if (!apply)
                return Ok(new { applied = false, textType = tt.Name, toWrite = plan.Select(p => new { sheet = p.s.SheetNumber, lines = p.lines }).ToList(), skipped });

            int count = 0;
            using (var tx = new Transaction(_doc, "MCP 圖框修正說明"))
            {
                tx.Start();
                foreach (var (s, o, c, lines) in plan)
                    for (int i = 0; i < lines.Count; i++)
                    {
                        TextNote.Create(_doc, s.Id, new XYZ(o.X + c.x / FT, o.Y + (c.y - c.dy * i) / FT, 0), lines[i], tt.Id);
                        count++;
                    }
                tx.Commit();
            }
            return Ok(new { applied = true, sheets = plan.Count, textNotes = count, skipped });
        }

        private static List<string> Wrap(string note, int limit)
        {
            var lines = new List<string>(); var cur = ""; double w = 0; int lastBreak = -1;
            foreach (var ch in note)
            {
                cur += ch; w += ch < 128 ? 0.6 : 1.0;
                if ("、，,；;".IndexOf(ch) >= 0) lastBreak = cur.Length;
                if (w > limit)
                {
                    if (lastBreak > 0) { lines.Add(cur.Substring(0, lastBreak)); cur = cur.Substring(lastBreak); }
                    else { lines.Add(cur.Substring(0, cur.Length - 1)); cur = ch.ToString(); }
                    w = cur.Sum(c => c < 128 ? 0.6 : 1.0); lastBreak = -1;
                }
            }
            if (cur != "") lines.Add(cur);
            return lines.Select(l => l.Trim()).Where(l => l != "").ToList();
        }

        // ── GET /revisions ──────────────────────────────────────────
        public string ListRevisions()
        {
            var clouds = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_RevisionClouds)
                .WhereElementIsNotElementType().Cast<RevisionCloud>().GroupBy(c => c.RevisionId.IntegerValue)
                .ToDictionary(g => g.Key, g => g.Count());
            var list = new FilteredElementCollector(_doc).OfClass(typeof(Revision)).Cast<Revision>()
                .OrderBy(x => x.SequenceNumber)
                .Select(x => new { id = x.Id.IntegerValue, sequence = x.SequenceNumber, description = x.Description,
                                   date = x.RevisionDate, clouds = clouds.TryGetValue(x.Id.IntegerValue, out var n) ? n : 0 }).ToList();
            return Ok(new { count = list.Count, revisions = list });
        }

        // ── POST /revisions/cloud-notes ─────────────────────────────
        // { revisionId, notes: {圖號: 說明}, textTypeName?, offsetMm?: 4, apply }
        // 雲形線右上角外加文字＋直線引線，並把說明寫進雲形線「備註」作為已標註記號。
        public string CloudNotes(string body)
        {
            var r = Parse(body);
            bool apply = (bool?)r["apply"] ?? false;
            long revId = (long?)r["revisionId"] ?? 0;
            if (_doc.GetElement(new ElementId((int)revId)) is not Revision rev) return Fail($"找不到修訂 {revId}。用 GET /revisions 查。");
            var tt = FindTextType((string?)r["textTypeName"] ?? "BAF 3mm 內文(紅)");
            if (tt == null) return Fail("找不到文字類型。");
            double offMm = (double?)r["offsetMm"] ?? 4;
            var notes = new Dictionary<string, string>();
            if (r["notes"] is JObject jn) foreach (var p in jn.Properties()) notes[p.Name] = (string?)p.Value ?? "";

            var viewSheet = new Dictionary<int, string>();
            foreach (var vp in new FilteredElementCollector(_doc).OfClass(typeof(Viewport)).Cast<Viewport>())
                if (_doc.GetElement(vp.SheetId) is ViewSheet sh && !viewSheet.ContainsKey(vp.ViewId.IntegerValue))
                    viewSheet[vp.ViewId.IntegerValue] = sh.SheetNumber;

            var clouds = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_RevisionClouds)
                .WhereElementIsNotElementType().Cast<RevisionCloud>().Where(c => c.RevisionId == rev.Id).ToList();
            var plan = new List<(RevisionCloud c, View v, string note, BoundingBoxXYZ bb)>();
            var skipped = new List<string>();
            foreach (var c in clouds)
            {
                if (_doc.GetElement(c.OwnerViewId) is not View v) continue;
                var cp = c.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (!string.IsNullOrWhiteSpace(cp?.AsString())) { skipped.Add($"{v.Name}：已標註過"); continue; }
                string? num = v is ViewSheet vs ? vs.SheetNumber : (viewSheet.TryGetValue(v.Id.IntegerValue, out var nn) ? nn : null);
                if (num == null) { skipped.Add($"{v.Name}：視圖未放在圖紙上"); continue; }
                if (!notes.TryGetValue(num, out var note) || note == "") { skipped.Add($"{num}（{v.Name}）：圖號不在清單"); continue; }
                var bb = c.get_BoundingBox(v);
                if (bb == null) { skipped.Add($"{v.Name}：取不到外框"); continue; }
                plan.Add((c, v, note, bb));
            }
            if (!apply)
                return Ok(new { applied = false, revision = rev.Description, clouds = clouds.Count, toAnnotate = plan.Count,
                                items = plan.Select(p => new { cloudId = p.c.Id.IntegerValue, view = p.v.Name, note = p.note }).ToList(), skipped });

            var created = new List<object>();
            using (var tx = new Transaction(_doc, "MCP 雲形線變更標註"))
            {
                tx.Start();
                foreach (var (c, v, note, bb) in plan)
                {
                    int scale = v is ViewSheet ? 1 : Math.Max(1, v.Scale);
                    XYZ right = v.RightDirection, up = v.UpDirection, d = bb.Max - bb.Min;
                    var pts = new[] { bb.Min, bb.Max, bb.Min + right * d.DotProduct(right), bb.Min + up * d.DotProduct(up) };
                    var corner = pts.OrderByDescending(p => p.DotProduct(right) + p.DotProduct(up)).First();
                    double off = offMm * scale / FT;
                    var tn = TextNote.Create(_doc, v.Id, corner + right * off + up * off * 2, note, tt.Id);
                    var ld = tn.AddLeader(TextNoteLeaderTypes.TNLT_STRAIGHT_L);
                    ld.End = corner;
                    var cp = c.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (cp != null && !cp.IsReadOnly) cp.Set(note);
                    created.Add(new { cloudId = c.Id.IntegerValue, textNoteId = tn.Id.IntegerValue, view = v.Name });
                }
                tx.Commit();
            }
            return Ok(new { applied = true, annotated = created.Count, items = created, skipped });
        }
    }
}
