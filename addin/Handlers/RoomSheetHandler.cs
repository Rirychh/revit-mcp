// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;

namespace RevitMcpAddin.Handlers
{
    public class RoomViewSetRequest
    {
        [JsonProperty("roomNumbers")]      public List<string> RoomNumbers { get; set; } = new();
        [JsonProperty("nameContains")]     public string? NameContains { get; set; }
        [JsonProperty("scale")]            public int Scale { get; set; } = 50;
        [JsonProperty("marginMm")]         public double MarginMm { get; set; } = 500;
        [JsonProperty("elevationCount")]   public int ElevationCount { get; set; } = 4;
        [JsonProperty("depthMm")]          public double DepthMm { get; set; } = 5000;
        [JsonProperty("topMm")]            public double TopMm { get; set; } = 3000;
        [JsonProperty("bottomMm")]         public double BottomMm { get; set; } = -300;
        [JsonProperty("planTemplate")]     public string? PlanTemplate { get; set; }
        [JsonProperty("elevTemplate")]     public string? ElevTemplate { get; set; }
        [JsonProperty("planName")]         public string PlanName { get; set; } = "{room}_plan";
        [JsonProperty("elevName")]         public string ElevName { get; set; } = "{room}_elev";
        [JsonProperty("dryRun")]           public bool DryRun { get; set; } = true;
        [JsonProperty("alignElevToRoom")] public bool AlignElevToRoom { get; set; } = false;
        [JsonProperty("elevAngleDeg")]    public double? ElevAngleDeg { get; set; }
    }

    public class SheetSpec
    {
        [JsonProperty("number")] public string Number { get; set; } = "";
        [JsonProperty("name")]   public string Name { get; set; } = "";
    }

    public class SheetCreateRequest
    {
        [JsonProperty("sheets")]         public List<SheetSpec> Sheets { get; set; } = new();
        [JsonProperty("titleblockFrom")] public string? TitleblockFrom { get; set; }
        [JsonProperty("dryRun")]         public bool DryRun { get; set; } = true;
    }

    public class PlaceItem
    {
        [JsonProperty("viewId")]   public long ViewId { get; set; }
        [JsonProperty("viewName")] public string? ViewName { get; set; }
        [JsonProperty("xMm")]      public double XMm { get; set; }
        [JsonProperty("yMm")]      public double YMm { get; set; }
    }

    public class PlaceViewportsRequest
    {
        [JsonProperty("sheetNumber")] public string SheetNumber { get; set; } = "";
        [JsonProperty("items")]       public List<PlaceItem> Items { get; set; } = new();
        [JsonProperty("dryRun")]      public bool DryRun { get; set; } = true;
    }

    public class CloseDocRequest
    {
        [JsonProperty("title")]  public string Title { get; set; } = "";
        [JsonProperty("save")]   public bool Save { get; set; } = false;
        [JsonProperty("force")]  public bool Force { get; set; } = false;
        [JsonProperty("dryRun")] public bool DryRun { get; set; } = true;
    }

    public class RoomSheetHandler
    {
        private readonly Document _doc;
        private readonly List<string> _warn = new();
        public RoomSheetHandler(Document doc) { _doc = doc; }

        private const double FTMM = 304.8;
        private static double Ft(double mm) => mm / FTMM;
        private static double Mm(double ft) => ft * FTMM;

        public string CreateRoomViews(string body)
        {
            var req = JsonConvert.DeserializeObject<RoomViewSetRequest>(body)
                      ?? throw new ArgumentException("Invalid body.");

            var rooms = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>()
                .Where(r => r.Area > 0)
                .ToList();

            if (req.RoomNumbers.Count > 0)
                rooms = rooms.Where(r => req.RoomNumbers.Contains(r.Number)).ToList();
            else if (!string.IsNullOrEmpty(req.NameContains))
                rooms = rooms.Where(r => (r.Name ?? "").Contains(req.NameContains!)).ToList();

            rooms = rooms.OrderBy(r => r.Level?.Elevation ?? 0).ThenBy(r => r.Number).ToList();

            var planned = rooms.Select(r => new
            {
                number = r.Number,
                name   = r.Name,
                level  = r.Level?.Name ?? "",
                planView  = Expand(req.PlanName, r),
                elevViews = Enumerable.Range(0, Math.Max(0, Math.Min(4, req.ElevationCount)))
                                      .Select(i => Expand(req.ElevName, r) + Suffix(i)).ToList()
            }).ToList();

            if (req.DryRun)
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    data = new { dryRun = true, roomCount = rooms.Count, planned }
                });

            var planVft = new FilteredElementCollector(_doc).OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.FloorPlan)
                ?? throw new InvalidOperationException("No FloorPlan ViewFamilyType.");
            var elevVft = new FilteredElementCollector(_doc).OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>().FirstOrDefault(v => v.ViewFamily == ViewFamily.Elevation)
                ?? throw new InvalidOperationException("No Elevation ViewFamilyType.");

            var planTpl = FindTemplate(req.PlanTemplate);
            var elevTpl = FindTemplate(req.ElevTemplate);

            var made = new List<object>();

            using var tx = new Transaction(_doc, "MCP RoomViews");
            var fo = tx.GetFailureHandlingOptions();
            fo.SetFailuresPreprocessor(new SwallowWarnings());
            tx.SetFailureHandlingOptions(fo);
            tx.Start();

            foreach (var room in rooms)
            {
                try
                {
                    var lvl = room.Level ?? throw new InvalidOperationException("room has no level");
                    var bb  = room.get_BoundingBox(null)
                              ?? throw new InvalidOperationException("room has no bounding box");
                    double m = Ft(req.MarginMm);

                    var vp = ViewPlan.Create(_doc, planVft.Id, lvl.Id);
                    SafeName(vp, Expand(req.PlanName, room));
                    if (planTpl != null) vp.ViewTemplateId = planTpl.Id;
                    vp.Scale = req.Scale;
                    vp.CropBoxActive = true;
                    vp.CropBoxVisible = false;
                    vp.CropBox = new BoundingBoxXYZ
                    {
                        Min = new XYZ(bb.Min.X - m, bb.Min.Y - m, bb.Min.Z),
                        Max = new XYZ(bb.Max.X + m, bb.Max.Y + m, bb.Max.Z)
                    };

                    var entry = new List<object> { new { kind = "plan", id = vp.Id.Value, name = vp.Name } };

                    int n = Math.Max(0, Math.Min(4, req.ElevationCount));
                    if (n > 0)
                    {
                        var cx = (bb.Min.X + bb.Max.X) / 2;
                        var cy = (bb.Min.Y + bb.Max.Y) / 2;
                        var cz = lvl.Elevation + Ft(1200);
                        var marker = ElevationMarker.CreateElevationMarker(
                            _doc, elevVft.Id, new XYZ(cx, cy, cz), req.Scale);

                        for (int i = 0; i < n; i++)
                        {
                            var ev = marker.CreateElevation(_doc, vp.Id, i);
                            SafeName(ev, Expand(req.ElevName, room) + Suffix(i));
                            if (elevTpl != null) ev.ViewTemplateId = elevTpl.Id;
                            ev.Scale = req.Scale;
                            CropElevation(ev, bb, lvl, req);
                            entry.Add(new { kind = "elevation", id = ev.Id.Value, name = ev.Name });
                        }

                        double? rotDeg = req.ElevAngleDeg;
                        if (rotDeg == null && req.AlignElevToRoom) rotDeg = RoomEdgeAngleDeg(room);
                        if (rotDeg.HasValue && Math.Abs(rotDeg.Value) > 1e-6)
                        {
                            var axis = Line.CreateBound(new XYZ(cx, cy, cz), new XYZ(cx, cy, cz + 1));
                            ElementTransformUtils.RotateElement(_doc, marker.Id, axis, rotDeg.Value * Math.PI / 180.0);
                            _warn.Add($"marker rotated {Math.Round(rotDeg.Value,3)} deg");
                        }
                    }

                    made.Add(new { room = room.Number, level = room.Level?.Name, views = entry });
                }
                catch (Exception ex)
                {
                    _warn.Add(room.Number + ": " + ex.Message);
                }
            }
            tx.Commit();

            return JsonConvert.SerializeObject(new
            {
                success = true,
                data = new { created = made.Count, rooms = made, warnings = _warn }
            });
        }

        private void CropElevation(ViewSection ev, BoundingBoxXYZ roomBb, Level lvl, RoomViewSetRequest req)
        {
            try
            {
                double m   = Ft(req.MarginMm);
                double zLo = lvl.Elevation + Ft(req.BottomMm);
                double zHi = lvl.Elevation + Ft(req.TopMm);

                var corners = new List<XYZ>();
                foreach (var x in new[] { roomBb.Min.X - m, roomBb.Max.X + m })
                    foreach (var y in new[] { roomBb.Min.Y - m, roomBb.Max.Y + m })
                        foreach (var z in new[] { zLo, zHi })
                            corners.Add(new XYZ(x, y, z));

                var cb  = ev.CropBox;
                var inv = cb.Transform.Inverse;
                var loc = corners.Select(p => inv.OfPoint(p)).ToList();

                double minX = loc.Min(p => p.X), maxX = loc.Max(p => p.X);
                double minY = loc.Min(p => p.Y), maxY = loc.Max(p => p.Y);

                cb.Min = new XYZ(minX, minY, cb.Min.Z);
                cb.Max = new XYZ(maxX, maxY, cb.Max.Z);

                ev.CropBoxActive  = true;
                ev.CropBoxVisible = false;
                ev.CropBox = cb;

                ev.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING)?.Set(1);
                ev.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR)?.Set(Ft(req.DepthMm));
            }
            catch (Exception ex) { _warn.Add("crop elevation: " + ex.Message); }
        }
        public string CreateSheets(string body)
        {
            var req = JsonConvert.DeserializeObject<SheetCreateRequest>(body)
                      ?? throw new ArgumentException("Invalid body.");
            if (req.Sheets.Count == 0) throw new ArgumentException("sheets is required.");

            var tbTypeId = ResolveTitleblock(req.TitleblockFrom);

            var existing = new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet))
                           .Cast<ViewSheet>().Select(s => s.SheetNumber).ToHashSet();

            var skipped = req.Sheets.Where(s => existing.Contains(s.Number)).Select(s => s.Number).ToList();
            var todo    = req.Sheets.Where(s => !existing.Contains(s.Number)).ToList();

            if (req.DryRun)
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    data = new { dryRun = true, willCreate = todo, alreadyExists = skipped,
                                 titleblockTypeId = tbTypeId.Value, warnings = _warn }
                });

            var made = new List<object>();
            using var tx = new Transaction(_doc, "MCP CreateSheets");
            var fo = tx.GetFailureHandlingOptions();
            fo.SetFailuresPreprocessor(new SwallowWarnings());
            tx.SetFailureHandlingOptions(fo);
            tx.Start();
            foreach (var s in todo)
            {
                try
                {
                    var sheet = ViewSheet.Create(_doc, tbTypeId);
                    sheet.SheetNumber = s.Number;
                    sheet.Name = s.Name;
                    made.Add(new { id = sheet.Id.Value, number = sheet.SheetNumber, name = sheet.Name });
                }
                catch (Exception ex) { _warn.Add(s.Number + ": " + ex.Message); }
            }
            tx.Commit();

            return JsonConvert.SerializeObject(new
            {
                success = true,
                data = new { created = made.Count, sheets = made, alreadyExists = skipped, warnings = _warn }
            });
        }

        private ElementId ResolveTitleblock(string? fromSheetNumber)
        {
            if (!string.IsNullOrEmpty(fromSheetNumber))
            {
                var src = new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                          .FirstOrDefault(s => s.SheetNumber == fromSheetNumber);
                if (src != null)
                {
                    var tb = new FilteredElementCollector(_doc, src.Id)
                             .OfCategory(BuiltInCategory.OST_TitleBlocks)
                             .WhereElementIsNotElementType().FirstElement();
                    if (tb != null) return tb.GetTypeId();
                    _warn.Add("sheet " + fromSheetNumber + " has no titleblock; falling back");
                }
                else _warn.Add("sheet " + fromSheetNumber + " not found; falling back");
            }
            var any = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                      .WhereElementIsElementType().FirstElementId();
            if (any == ElementId.InvalidElementId)
                throw new InvalidOperationException("No titleblock type in project.");
            return any;
        }

        public string ListSheets(string? numberContains)
        {
            var list = new List<object>();
            foreach (var s in new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            {
                if (!string.IsNullOrEmpty(numberContains) &&
                    !s.SheetNumber.Contains(numberContains!) && !s.Name.Contains(numberContains!)) continue;

                double w = 0, h = 0;
                string? tbName = null;
                var tb = new FilteredElementCollector(_doc, s.Id)
                         .OfCategory(BuiltInCategory.OST_TitleBlocks)
                         .WhereElementIsNotElementType().FirstElement();
                if (tb != null)
                {
                    w = Mm(tb.get_Parameter(BuiltInParameter.SHEET_WIDTH)?.AsDouble() ?? 0);
                    h = Mm(tb.get_Parameter(BuiltInParameter.SHEET_HEIGHT)?.AsDouble() ?? 0);
                    tbName = (_doc.GetElement(tb.GetTypeId()) as ElementType)?.Name;
                }

                var vps = new List<object>();
                foreach (var vid in s.GetAllViewports())
                {
                    if (_doc.GetElement(vid) is not Viewport v) continue;
                    var o = v.GetBoxOutline();
                    var c = v.GetBoxCenter();
                    vps.Add(new
                    {
                        viewportId = v.Id.Value,
                        viewId = v.ViewId.Value,
                        viewName = (_doc.GetElement(v.ViewId) as View)?.Name,
                        cxMm = Mm(c.X), cyMm = Mm(c.Y),
                        wMm = Mm(o.MaximumPoint.X - o.MinimumPoint.X),
                        hMm = Mm(o.MaximumPoint.Y - o.MinimumPoint.Y)
                    });
                }

                list.Add(new
                {
                    id = s.Id.Value, number = s.SheetNumber, name = s.Name,
                    titleblock = tbName, widthMm = w, heightMm = h,
                    viewportCount = vps.Count, viewports = vps
                });
            }
            return JsonConvert.SerializeObject(new { success = true, data = new { count = list.Count, sheets = list } });
        }

        public string PlaceViewports(string body)
        {
            var req = JsonConvert.DeserializeObject<PlaceViewportsRequest>(body)
                      ?? throw new ArgumentException("Invalid body.");
            var sheet = new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                        .FirstOrDefault(s => s.SheetNumber == req.SheetNumber)
                        ?? throw new ArgumentException("Sheet not found: " + req.SheetNumber);

            var resolved = new List<(View v, double x, double y)>();
            foreach (var it in req.Items)
            {
                View? v = null;
                if (it.ViewId > 0) v = _doc.GetElement(ElementIdCompat.NewId(it.ViewId)) as View;
                if (v == null && !string.IsNullOrEmpty(it.ViewName))
                    v = new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>()
                        .FirstOrDefault(x => !x.IsTemplate && x.Name == it.ViewName);
                if (v == null) { _warn.Add("view not found: " + it.ViewId + "/" + it.ViewName); continue; }
                if (!Viewport.CanAddViewToSheet(_doc, sheet.Id, v.Id))
                {
                    _warn.Add("cannot place (already on a sheet?): " + v.Name);
                    continue;
                }
                resolved.Add((v, it.XMm, it.YMm));
            }

            if (req.DryRun)
                return JsonConvert.SerializeObject(new
                {
                    success = true,
                    data = new { dryRun = true, sheet = sheet.SheetNumber,
                                 willPlace = resolved.Select(r => new { id = r.v.Id.Value, name = r.v.Name, r.x, r.y }),
                                 warnings = _warn }
                });

            var made = new List<object>();
            using var tx = new Transaction(_doc, "MCP PlaceViewports");
            var fo = tx.GetFailureHandlingOptions();
            fo.SetFailuresPreprocessor(new SwallowWarnings());
            tx.SetFailureHandlingOptions(fo);
            tx.Start();
            foreach (var t in resolved)
            {
                try
                {
                    var vp = Viewport.Create(_doc, sheet.Id, t.v.Id, new XYZ(Ft(t.x), Ft(t.y), 0));
                    made.Add(new { viewportId = vp.Id.Value, viewId = t.v.Id.Value, name = t.v.Name });
                }
                catch (Exception ex) { _warn.Add(t.v.Name + ": " + ex.Message); }
            }
            tx.Commit();

            return JsonConvert.SerializeObject(new
            {
                success = true,
                data = new { placed = made.Count, sheet = sheet.SheetNumber, viewports = made, warnings = _warn }
            });
        }

        // ── 文件管理 ──────────────────────────────────────────────────────
        public string ListDocuments()
        {
            var app = _doc.Application;
            var list = new List<object>();
            foreach (Document d in app.Documents)
            {
                if (d.IsLinked) continue;
                list.Add(new
                {
                    title = d.Title,
                    path = d.PathName,
                    isActive = (d.Equals(_doc)),
                    isModified = d.IsModified,
                    isFamily = d.IsFamilyDocument,
                    isWorkshared = d.IsWorkshared
                });
            }
            return JsonConvert.SerializeObject(new { success = true, data = new { count = list.Count, documents = list } });
        }

        public string CloseDocument(string body)
        {
            var req = JsonConvert.DeserializeObject<CloseDocRequest>(body)
                      ?? throw new ArgumentException("Invalid body.");
            if (string.IsNullOrWhiteSpace(req.Title))
                throw new ArgumentException("title is required.");

            var app = _doc.Application;
            Document? target = null;
            foreach (Document d in app.Documents)
            {
                if (d.IsLinked) continue;
                if (d.Title == req.Title || d.PathName == req.Title) { target = d; break; }
            }
            if (target == null) throw new ArgumentException("Document not found: " + req.Title);
            if (target.Equals(_doc))
                throw new InvalidOperationException("Cannot close the active document from the API; switch to another document first.");

            var info = new { title = target.Title, path = target.PathName, wasModified = target.IsModified };

            if (req.DryRun)
                return JsonConvert.SerializeObject(new { success = true, data = new { dryRun = true, target = info, save = req.Save } });

            if (target.IsModified && !req.Save && !req.Force)
                throw new InvalidOperationException("Document has unsaved changes. Pass save=true to save, or force=true to discard.");

            try
            {
                target.Close(req.Save);
                return JsonConvert.SerializeObject(new { success = true, data = new { closed = info, saved = req.Save } });
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Revit refused to close it: " + ex.Message);
            }
        }
        /// <summary>房間最長邊相對水平的夾角（已折到 -45~45 度），用來把立面標記轉成垂直牆面。</summary>
        private static double RoomEdgeAngleDeg(Room r)
        {
            var opt = new SpatialElementBoundaryOptions();
            double best = 0, bestLen = -1;
            var loops = r.GetBoundarySegments(opt);
            if (loops == null) return 0;
            foreach (var loop in loops)
                foreach (var seg in loop)
                {
                    if (seg.GetCurve() is not Line ln) continue;
                    var len = ln.Length;
                    if (len > bestLen)
                    {
                        bestLen = len;
                        var d = ln.Direction;
                        best = Math.Atan2(d.Y, d.X) * 180.0 / Math.PI;
                    }
                }
            var snapped = Math.Round(best / 90.0) * 90.0;
            return best - snapped;
        }
        private static string Suffix(int i) => i switch { 0 => "a", 1 => "b", 2 => "c", _ => "d" };

        private static string Expand(string pattern, Room r) => pattern
            .Replace("{room}",  r.Number ?? "")
            .Replace("{name}",  r.Name ?? "")
            .Replace("{level}", r.Level?.Name ?? "");

        private void SafeName(View v, string wanted)
        {
            var name = wanted;
            for (int i = 2; i < 100; i++)
            {
                try { v.Name = name; return; }
                catch { name = wanted + "_" + i; }
            }
            _warn.Add("could not name view: " + wanted);
        }

        private View? FindTemplate(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var t = new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>()
                    .FirstOrDefault(v => v.IsTemplate && v.Name == name);
            if (t == null) _warn.Add("view template not found: " + name);
            return t;
        }
    }
}