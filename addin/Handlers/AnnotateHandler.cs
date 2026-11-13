// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using static RevitMcpAddin.Handlers.PortUtil;
using static RevitMcpAddin.Handlers.ViewPlane;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 一般註解：文字、高程點、填滿區域、詳圖元件、房間標籤、修訂雲形線。位置一律用視圖平面座標 uv（mm），見 ViewPlane。
    /// 全部 apply=false 預檢；apply=true 單一交易，任何錯誤整批回滾。
    /// </summary>
    public class AnnotateHandler
    {
        private readonly Document _doc;
        public AnnotateHandler(Document doc) => _doc = doc;

        private View? V(JObject r) => Get(_doc, (long?)r["viewId"] ?? 0) as View;
        private static XYZ Depth(View v) => v is ViewPlan vp && vp.GenLevel != null ? new XYZ(0, 0, vp.GenLevel.Elevation) : v.Origin;
        private static (double u, double v)? UvOf(JToken? t) => t is JArray a && a.Count >= 2 ? ((double)a[0], (double)a[1]) : null;

        private static List<XYZ> LoopModel(View view, JToken? t)
        {
            var o = new List<XYZ>(); var d = Depth(view);
            foreach (var p in (t as JArray) ?? new JArray()) if (p is JArray a && a.Count >= 2) o.Add(ToModel(view, (double)a[0], (double)a[1], d));
            if (o.Count > 1 && o[0].DistanceTo(o[o.Count - 1]) < 1 / FT) o.RemoveAt(o.Count - 1);
            return o;
        }

        private static string TypeNameOf(Document doc, ElementId id) => doc.GetElement(id) is ElementType t ? $"{t.FamilyName}: {t.Name}" : "?";

        // POST /annotations/text { viewId, notes:[{text, pointUv:[u,v], widthMm?}], textType?, apply }   widthMm＝圖紙上的寬度
        public string Text(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var v = V(r); if (v == null || v.IsTemplate) return Fail("viewId 要是一般視圖或圖紙。");
            if (v is View3D) return Fail("3D 視圖不能放文字註記。");
            var types = new FilteredElementCollector(_doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();
            var tt = FindType(types, (string?)r["textType"]); if (tt == null) return Fail("模型裡沒有文字類型。");
            var items = new List<(string text, XYZ at, double? w)>(); var bad = new List<object>(); int i = -1;
            foreach (var n in (r["notes"] as JArray) ?? new JArray())
            {
                i++; var uv = UvOf(n["pointUv"]); string? text = (string?)n["text"];
                if (uv == null || string.IsNullOrEmpty(text)) { bad.Add(new { index = i, reason = "要有 text 與 pointUv" }); continue; }
                items.Add((text!, ToModel(v, uv.Value.u, uv.Value.v, Depth(v)), (double?)n["widthMm"]));
            }
            if (items.Count == 0) return Fail("沒有可放的文字：" + Newtonsoft.Json.JsonConvert.SerializeObject(bad));
            var plan = new { view = v.Name, textType = TypeNameOf(_doc, tt.Id), willCreate = items.Count, skipped = bad, availableTypes = TypeNames(types).Take(30) };
            if (!apply) return Ok(new { preflight = true, plan });
            var ids = new List<long>();
            var res = RunTx(_doc, "MCP Text Notes", () =>
            {
                foreach (var (text, at, w) in items)
                {
                    var tn = w.HasValue
                        ? TextNote.Create(_doc, v.Id, at, w.Value / FT, text, new TextNoteOptions(tt.Id))
                        : TextNote.Create(_doc, v.Id, at, text, tt.Id);
                    ids.Add(tn.Id.Value);
                }
            });
            return res.committed ? Ok(new { applied = true, ids, plan }) : Fail("放文字失敗，已回滾：" + string.Join("；", res.errors));
        }

        // POST /annotations/spot-elevations { viewId, items:[{elementId, pointMm?:[x,y]}], hasLeader?:true, apply }
        // 標在樓板／屋頂／天花等主體的頂面；pointMm 省略時取元件外框中心。
        public string SpotElevations(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var v = V(r); if (v == null || v.IsTemplate || v is ViewSheet) return Fail("viewId 要是平面、剖面或立面視圖。");
            bool leader = (bool?)r["hasLeader"] ?? true;
            var items = new List<(Element e, Reference rf, XYZ p)>(); var bad = new List<object>();
            foreach (var it in (r["items"] as JArray) ?? new JArray())
            {
                long id = (long?)it["elementId"] ?? 0; var e = Get(_doc, id);
                if (e is not HostObject ho) { bad.Add(new { elementId = id, reason = "要是樓板、屋頂、天花等主體元件" }); continue; }
                IList<Reference> tops; try { tops = HostObjectUtils.GetTopFaces(ho); } catch { tops = new List<Reference>(); }
                if (tops.Count == 0) { bad.Add(new { elementId = id, reason = "找不到頂面" }); continue; }
                var bb = e.get_BoundingBox(null)!;
                XYZ guess = it["pointMm"] is JArray pa && pa.Count >= 2 ? new XYZ((double)pa[0] / FT, (double)pa[1] / FT, bb.Max.Z) : (bb.Min + bb.Max) / 2 + new XYZ(0, 0, (bb.Max.Z - bb.Min.Z) / 2);
                Reference? best = null; XYZ? bp = null; double bd = double.MaxValue;
                foreach (var rf in tops)
                {
                    if (e.GetGeometryObjectFromReference(rf) is not Face f) continue;
                    var ir = f.Project(guess); if (ir == null) continue;
                    if (ir.Distance < bd) { bd = ir.Distance; best = rf; bp = ir.XYZPoint; }
                }
                if (best == null || bp == null) { bad.Add(new { elementId = id, reason = "這個點投影不到頂面上" }); continue; }
                items.Add((e, best, bp));
            }
            if (items.Count == 0) return Fail("沒有可標的位置：" + Newtonsoft.Json.JsonConvert.SerializeObject(bad));
            var plan = new { view = v.Name, willCreate = items.Count, points = items.Select(x => new { elementId = x.e.Id.Value, atMm = Mm(x.p) }), hasLeader = leader, skipped = bad };
            if (!apply) return Ok(new { preflight = true, plan });
            var ids = new List<long>();
            var res = RunTx(_doc, "MCP Spot Elevations", () =>
            {
                var off = (v.RightDirection + v.UpDirection) * (800.0 / FT);
                foreach (var (e, rf, p) in items)
                {
                    var sd = _doc.Create.NewSpotElevation(v, rf, p, p + off, p + off + v.RightDirection * (600.0 / FT), p, leader);
                    if (sd != null) ids.Add(sd.Id.Value);
                }
            });
            return res.committed ? Ok(new { applied = true, ids, plan }) : Fail("放高程點失敗，已回滾：" + string.Join("；", res.errors));
        }

        // POST /annotations/filled-regions { viewId, regions:[ [[u,v],...] ], regionType?, apply }
        public string FilledRegions(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var v = V(r); if (v == null || v.IsTemplate || v is View3D) return Fail("viewId 要是平面、剖面、立面、詳圖或製圖視圖。");
            var types = new FilteredElementCollector(_doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().ToList();
            var ft = string.IsNullOrWhiteSpace((string?)r["regionType"]) ? types.FirstOrDefault() : types.FirstOrDefault(t => t.Name == (string?)r["regionType"]) ?? types.FirstOrDefault(t => t.Name.IndexOf((string)r["regionType"]!, StringComparison.OrdinalIgnoreCase) >= 0);
            if (ft == null) return Fail("找不到填滿區域類型。可用：" + string.Join("、", types.Select(t => t.Name)));
            var loops = new List<List<XYZ>>(); var bad = new List<object>(); int i = -1;
            foreach (var reg in (r["regions"] as JArray) ?? new JArray())
            { i++; var pts = LoopModel(v, reg); if (pts.Count < 3) bad.Add(new { index = i, reason = "至少 3 點" }); else loops.Add(pts); }
            if (loops.Count == 0) return Fail("沒有可建立的區域。");
            var plan = new { view = v.Name, regionType = ft.Name, willCreate = loops.Count, skipped = bad, availableTypes = types.Select(t => t.Name).Take(40) };
            if (!apply) return Ok(new { preflight = true, plan });
            var ids = new List<long>();
            var res = RunTx(_doc, "MCP Filled Regions", () =>
            {
                foreach (var pts in loops)
                {
                    var cl = new CurveLoop(); for (int k = 0; k < pts.Count; k++) cl.Append(Line.CreateBound(pts[k], pts[(k + 1) % pts.Count]));
                    ids.Add(FilledRegion.Create(_doc, ft.Id, v.Id, new List<CurveLoop> { cl }).Id.Value);
                }
            });
            return res.committed ? Ok(new { applied = true, ids, plan }) : Fail("建立填滿區域失敗，已回滾：" + string.Join("；", res.errors));
        }

        // POST /annotations/detail-components { viewId, familyType, items:[{pointUv, rotationDeg?}], apply }
        public string DetailComponents(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var v = V(r); if (v == null || v.IsTemplate || v is View3D || v is ViewSheet) return Fail("viewId 要是平面、剖面、立面、詳圖或製圖視圖。");
            var syms = new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category?.Id.Value == (long)BuiltInCategory.OST_DetailComponents).ToList();
            string? fn = (string?)r["familyType"];
            var sym = syms.FirstOrDefault(s => $"{s.FamilyName}: {s.Name}" == fn) ?? (syms.Where(s => fn != null && $"{s.FamilyName}: {s.Name}".IndexOf(fn, StringComparison.OrdinalIgnoreCase) >= 0).ToList() is var m && m.Count == 1 ? m[0] : null);   // 模糊比對只在唯一時採用
            if (sym == null) return Fail("找不到詳圖元件「" + fn + "」。可用：" + string.Join("、", syms.Select(s => $"{s.FamilyName}: {s.Name}").Take(60)) + "（不在清單的請先用 revit_load_families 載入）");
            var items = new List<(XYZ p, double rot)>();
            foreach (var it in (r["items"] as JArray) ?? new JArray()) { var uv = UvOf(it["pointUv"]); if (uv != null) items.Add((ToModel(v, uv.Value.u, uv.Value.v, Depth(v)), ((double?)it["rotationDeg"] ?? 0) * Math.PI / 180)); }
            if (items.Count == 0) return Fail("items 要有 pointUv。");
            var plan = new { view = v.Name, familyType = $"{sym.FamilyName}: {sym.Name}", willPlace = items.Count };
            if (!apply) return Ok(new { preflight = true, plan });
            var ids = new List<long>();
            var res = RunTx(_doc, "MCP Detail Components", () =>
            {
                if (!sym.IsActive) { sym.Activate(); _doc.Regenerate(); }
                foreach (var (p, rot) in items)
                {
                    var fi = _doc.Create.NewFamilyInstance(p, sym, v);
                    if (Math.Abs(rot) > 1e-9) ElementTransformUtils.RotateElement(_doc, fi.Id, Line.CreateBound(p, p + v.ViewDirection), rot);
                    ids.Add(fi.Id.Value);
                }
            });
            return res.committed ? Ok(new { applied = true, ids, plan }) : Fail("放詳圖元件失敗，已回滾：" + string.Join("；", res.errors));
        }

        // POST /annotations/room-tags { viewId, roomIds?, tagType?, apply }  省略 roomIds＝這張平面所在樓層、還沒標的房間
        public string RoomTags(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            if (V(r) is not ViewPlan v || v.IsTemplate) return Fail("viewId 要是平面視圖。");
            var tagged = new HashSet<long>(new FilteredElementCollector(_doc, v.Id).OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsNotElementType()
                .OfType<RoomTag>().Where(t => t.Room != null).Select(t => t.Room.Id.Value));
            IEnumerable<Room> rooms = r["roomIds"] is JArray
                ? Ids(r["roomIds"]).Select(i => Get(_doc, i)).OfType<Room>()
                : new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>().Where(x => v.GenLevel != null && x.LevelId == v.GenLevel.Id);
            var list = rooms.Where(x => x.Area > 0 && x.Location is LocationPoint).ToList();
            var skipped = list.Where(x => tagged.Contains(x.Id.Value)).Select(x => new { roomId = x.Id.Value, reason = "這張視圖已經有房間標籤" }).ToList<object>();
            list = list.Where(x => !tagged.Contains(x.Id.Value)).ToList();
            if (list.Count == 0) return Fail("沒有需要標的房間。" + Newtonsoft.Json.JsonConvert.SerializeObject(skipped.Take(20)));
            var types = new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().Where(s => s.Category?.Id.Value == (long)BuiltInCategory.OST_RoomTags).ToList();
            string? tn = (string?)r["tagType"];
            var tt = string.IsNullOrWhiteSpace(tn) ? null : types.FirstOrDefault(s => $"{s.FamilyName}: {s.Name}" == tn) ?? types.FirstOrDefault(s => $"{s.FamilyName}: {s.Name}".IndexOf(tn!, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrWhiteSpace(tn) && tt == null) return Fail("找不到房間標籤類型「" + tn + "」。可用：" + string.Join("、", types.Select(s => $"{s.FamilyName}: {s.Name}")));
            var plan = new { view = v.Name, willTag = list.Count, rooms = list.Take(100).Select(x => new { roomId = x.Id.Value, number = x.Number, name = x.Name }), tagType = tt != null ? $"{tt.FamilyName}: {tt.Name}" : "（預設）", skipped };
            if (!apply) return Ok(new { preflight = true, plan });
            var ids = new List<long>();
            var res = RunTx(_doc, "MCP Room Tags", () =>
            {
                foreach (var room in list)
                {
                    var p = ((LocationPoint)room.Location).Point;
                    var tag = _doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(p.X, p.Y), v.Id);
                    if (tag == null) continue;
                    if (tt != null) tag.ChangeTypeId(tt.Id);
                    ids.Add(tag.Id.Value);
                }
            });
            return res.committed ? Ok(new { applied = true, ids, plan }) : Fail("放房間標籤失敗，已回滾：" + string.Join("；", res.errors));
        }

        // POST /revisions/clouds { viewId, rectsUv:[[u0,v0,u1,v1]], revisionId?, newRevision?:{description, date?, issuedBy?, issuedTo?}, apply }
        public string RevisionClouds(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var v = V(r); if (v == null || v.IsTemplate || v is View3D) return Fail("viewId 要是圖紙或 2D 視圖。");
            var rects = ((r["rectsUv"] as JArray) ?? new JArray()).OfType<JArray>().Where(a => a.Count == 4).Select(a => a.Select(x => (double)x).ToArray()).ToList();
            if (rects.Count == 0) return Fail("rectsUv 要是 [[u0,v0,u1,v1], ...]（mm；圖紙上是圖紙 mm）。");
            Revision? rev = null; var nr = r["newRevision"] as JObject;
            if (nr == null)
            {
                rev = Get(_doc, (long?)r["revisionId"] ?? 0) as Revision;
                if (rev == null) return Fail("要給 revisionId（既有修訂，用 revit_list_revisions 查），或 newRevision 建新的。");
                if (rev.Issued) return Fail("修訂「" + rev.Description + "」已發佈，不能再加雲形線。");
            }
            var plan = new { view = v.Name, clouds = rects.Count, revision = rev != null ? rev.Description : "新修訂：" + (string?)nr!["description"] };
            if (!apply) return Ok(new { preflight = true, plan });
            var ids = new List<long>(); long revId = rev?.Id.Value ?? 0;
            var res = RunTx(_doc, "MCP Revision Clouds", () =>
            {
                if (rev == null)
                {
                    rev = Revision.Create(_doc);
                    rev.Description = (string?)nr!["description"] ?? "";
                    if (nr["date"] != null) rev.RevisionDate = (string)nr["date"]!;
                    if (nr["issuedBy"] != null) rev.IssuedBy = (string)nr["issuedBy"]!;
                    if (nr["issuedTo"] != null) rev.IssuedTo = (string)nr["issuedTo"]!;
                    revId = rev.Id.Value;
                }
                var d = Depth(v);
                foreach (var a in rects)
                {
                    double u0 = Math.Min(a[0], a[2]), u1 = Math.Max(a[0], a[2]), v0 = Math.Min(a[1], a[3]), v1 = Math.Max(a[1], a[3]);
                    var p = new[] { ToModel(v, u0, v0, d), ToModel(v, u1, v0, d), ToModel(v, u1, v1, d), ToModel(v, u0, v1, d) };
                    var curves = new List<Curve>(); for (int k = 0; k < 4; k++) curves.Add(Line.CreateBound(p[k], p[(k + 1) % 4]));
                    ids.Add(RevisionCloud.Create(_doc, v, rev.Id, curves).Id.Value);
                }
            });
            return res.committed ? Ok(new { applied = true, revisionId = revId, ids, plan }) : Fail("建立雲形線失敗，已回滾：" + string.Join("；", res.errors));
        }
    }
}
