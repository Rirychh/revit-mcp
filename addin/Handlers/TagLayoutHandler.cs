// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using static RevitMcpAddin.Handlers.PortUtil;
using static RevitMcpAddin.Handlers.ViewPlane;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 標籤檢查與調整（功能構想參考 MLabs Revit MCP 公開的工具清單，程式獨立實作）。
    /// 位置一律用視圖平面座標 uv（mm），見 ViewPlane。
    /// </summary>
    public class TagLayoutHandler
    {
        private readonly Document _doc;
        public TagLayoutHandler(Document doc) => _doc = doc;

        private View GetView(long? id)
        {
            if (!id.HasValue) throw new ArgumentException("viewId 必填。");
            return _doc.GetElement(ElementIdCompat.NewId(id.Value)) as View ?? throw new ArgumentException("找不到視圖 " + id);
        }

        private class TagInfo
        {
            public IndependentTag Tag = null!;
            public Rect? Box;
            public double HU, HV;
            public double? AU, AV;           // 被標元件中心
            public Rect? Host;
            public List<long> Tagged = new();
        }

        private TagInfo Read(View view, IndependentTag t)
        {
            var info = new TagInfo { Tag = t, Box = ElementRect(view, t) };
            (info.HU, info.HV) = UV(view, t.TagHeadPosition);
            try { info.Tagged = t.GetTaggedLocalElementIds().Where(i => i != ElementId.InvalidElementId).Select(i => i.Value).ToList(); } catch { }
            Rect? host = null;
            foreach (var id in info.Tagged)
            {
                var el = Get(_doc, id); if (el == null) continue;
                var r = ElementRect(view, el); if (r == null) continue;
                host = host == null ? r : host.Value.Union(r.Value);
            }
            info.Host = host;
            if (host != null) { info.AU = host.Value.CU; info.AV = host.Value.CV; }
            return info;
        }

        private List<IndependentTag> TagsInView(View view, IEnumerable<long>? only = null)
        {
            var all = new FilteredElementCollector(_doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>().ToList();
            if (only == null) return all;
            var set = new HashSet<long>(only);
            return all.Where(t => set.Contains(t.Id.Value)).ToList();
        }

        // GET /annotations/tags/inspect?viewId=&limit=
        public string Inspect(NameValueCollection qs)
        {
            var view = GetView(long.TryParse(qs["viewId"], out var v) ? v : (long?)null);
            int limit = int.TryParse(qs["limit"], out var l) ? Math.Max(1, l) : 300;
            var infos = TagsInView(view).Select(t => Read(view, t)).ToList();

            var overlaps = new List<object>();
            for (int i = 0; i < infos.Count; i++)
                for (int j = i + 1; j < infos.Count; j++)
                    if (infos[i].Box.HasValue && infos[j].Box.HasValue && infos[i].Box!.Value.Overlaps(infos[j].Box!.Value, 1))
                        overlaps.Add(new[] { infos[i].Tag.Id.Value, infos[j].Tag.Id.Value });

            var crop = CropRect(view);
            var rows = infos.Take(limit).Select(x =>
            {
                string? text = null; try { text = x.Tag.TagText; } catch { }
                object? elbow = null;
                try
                {
                    if (x.Tag.HasLeader)
                    {
                        var refs = x.Tag.GetTaggedReferences();
                        if (refs.Count > 0 && x.Tag.HasLeaderElbow(refs[0]))
                        { var (eu, ev) = UV(view, x.Tag.GetLeaderElbow(refs[0])); elbow = R2(eu, ev); }
                    }
                }
                catch { }
                bool headOnHost = x.Host.HasValue && x.Host.Value.Contains(x.HU, x.HV);
                bool outsideCrop = crop.HasValue && !crop.Value.Contains(x.HU, x.HV);
                return new
                {
                    tagId = x.Tag.Id.Value,
                    typeName = _doc.GetElement(x.Tag.GetTypeId())?.Name,
                    text,
                    taggedIds = x.Tagged,
                    headUv = R2(x.HU, x.HV),
                    anchorUv = x.AU.HasValue ? R2(x.AU.Value, x.AV!.Value) : null,
                    hasLeader = x.Tag.HasLeader,
                    elbowUv = elbow,
                    orientation = x.Tag.TagOrientation.ToString(),
                    boxUv = x.Box?.Dto(),
                    leaderLengthMm = x.AU.HasValue ? Math.Round(Math.Sqrt(Math.Pow(x.HU - x.AU.Value, 2) + Math.Pow(x.HV - x.AV!.Value, 2))) : (double?)null,
                    headOnHost,
                    outsideCrop,
                };
            }).ToList();

            return Ok(new
            {
                view = view.Name, viewId = view.Id.Value,
                coordinateSystem = "uv：u＝視圖右方、v＝視圖上方，mm，原點為視圖 Origin。調整標籤時傳同一套座標。",
                cropUv = crop?.Dto(),
                total = infos.Count, returned = rows.Count,
                overlapCount = overlaps.Count, overlaps = overlaps.Take(200),
                tags = rows,
            });
        }

        private static (double u, double v) Elbow(string mode, double au, double av, double hu, double hv) =>
            mode == "straight" ? ((au + hu) / 2, (av + hv) / 2) : (au, hv);   // orthogonal：先垂直後水平

        private void ApplyOne(View view, TagInfo x, double hu, double hv, bool? hasLeader, string elbowMode, string? orientation)
        {
            var t = x.Tag;
            if (orientation != null)
                t.TagOrientation = orientation == "vertical" ? TagOrientation.Vertical : TagOrientation.Horizontal;
            bool wantLeader = hasLeader ?? (t.HasLeader || elbowMode != "keep");
            if (t.HasLeader != wantLeader) t.HasLeader = wantLeader;
            if (wantLeader && t.LeaderEndCondition != LeaderEndCondition.Attached)
                t.LeaderEndCondition = LeaderEndCondition.Attached;
            t.TagHeadPosition = ToModel(view, hu, hv, t.TagHeadPosition);
            if (wantLeader && elbowMode != "keep" && x.AU.HasValue)
            {
                var (eu, ev) = Elbow(elbowMode, x.AU.Value, x.AV!.Value, hu, hv);
                foreach (var r in t.GetTaggedReferences())
                    t.SetLeaderElbow(r, ToModel(view, eu, ev, t.TagHeadPosition));
            }
        }

        // POST /annotations/tags/adjust
        // { viewId, moves:[{tagId, headUv?:[u,v], deltaMm?:[du,dv], alignToAnchor?:"horizontal"|"vertical", hasLeader?, elbow?:"keep"|"orthogonal"|"straight", orientation?}], apply }
        public string Adjust(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var view = GetView((long?)r["viewId"]);
            var moves = (r["moves"] as JArray) ?? new JArray();
            if (moves.Count == 0) return Fail("moves 是空的。");
            var tags = TagsInView(view).ToDictionary(t => t.Id.Value);

            var plan = new List<(TagInfo x, double hu, double hv, bool? leader, string elbow, string? orient)>();
            var bad = new List<object>(); var preview = new List<object>();
            foreach (var m in moves)
            {
                long id = (long?)m["tagId"] ?? 0;
                if (!tags.TryGetValue(id, out var tag)) { bad.Add(new { tagId = id, reason = "這張視圖裡沒有這個標籤" }); continue; }
                var x = Read(view, tag);
                double hu = x.HU, hv = x.HV;
                if (m["headUv"] is JArray h && h.Count >= 2) { hu = (double)h[0]; hv = (double)h[1]; }
                if (m["deltaMm"] is JArray d && d.Count >= 2) { hu += (double)d[0]; hv += (double)d[1]; }
                string? align = (string?)m["alignToAnchor"];
                if (align != null && x.AU.HasValue)
                {
                    if (align == "horizontal") hv = x.AV!.Value; else if (align == "vertical") hu = x.AU.Value;
                }
                string elbow = ((string?)m["elbow"] ?? "keep").ToLowerInvariant();
                if (elbow != "keep" && !x.AU.HasValue) { bad.Add(new { tagId = id, reason = "讀不到被標元件的位置，無法計算引線轉折" }); continue; }
                plan.Add((x, hu, hv, (bool?)m["hasLeader"], elbow, (string?)m["orientation"]));
                preview.Add(new { tagId = id, fromUv = R2(x.HU, x.HV), toUv = R2(hu, hv), anchorUv = x.AU.HasValue ? R2(x.AU.Value, x.AV!.Value) : null, elbow });
            }
            if (plan.Count == 0) return Fail("沒有可調整的標籤：" + Newtonsoft.Json.JsonConvert.SerializeObject(bad));
            if (!apply) return Ok(new { preflight = true, willMove = plan.Count, moves = preview, skipped = bad });

            var res = RunTx(_doc, "MCP Adjust Tags", () => { foreach (var p in plan) ApplyOne(view, p.x, p.hu, p.hv, p.leader, p.elbow, p.orient); });
            return res.committed
                ? Ok(new { applied = true, moved = plan.Count, moves = preview, skipped = bad, warnings = res.warnings })
                : Fail("調整標籤失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }

        // POST /annotations/tags/arrange
        // { viewId, tagIds?, boundary?:"crop"|"tagged"|<elementId>|[u0,v0,u1,v1], side?:"auto"|"left"|"right"|"top"|"bottom",
        //   marginMm?:500, spacingMm?:400, elbow?:"orthogonal"|"straight", apply }
        public string Arrange(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var view = GetView((long?)r["viewId"]);
            var only = r["tagIds"] is JArray ? Ids(r["tagIds"]) : null;
            var infos = TagsInView(view, only).Select(t => Read(view, t)).Where(x => x.AU.HasValue).ToList();
            if (infos.Count == 0) return Fail("找不到可排列的標籤（需要讀得到被標元件位置）。");

            double margin = (double?)r["marginMm"] ?? 500, spacing = (double?)r["spacingMm"] ?? 400;
            string side = ((string?)r["side"] ?? "auto").ToLowerInvariant();
            string elbow = ((string?)r["elbow"] ?? "orthogonal").ToLowerInvariant();

            Rect? B = null; string boundaryDesc;
            var bt = r["boundary"];
            if (bt is JArray ba && ba.Count == 4)
            { B = new Rect { U0 = (double)ba[0], V0 = (double)ba[1], U1 = (double)ba[2], V1 = (double)ba[3] }; boundaryDesc = "指定矩形"; }
            else if (bt != null && bt.Type == JTokenType.Integer)
            {
                var el = Get(_doc, (long)bt) ?? throw new ArgumentException("找不到 boundary 元件 " + bt);
                B = ElementRect(view, el); boundaryDesc = "元件 " + el.Id.Value + "（" + el.Name + "）";
            }
            else if (((string?)bt ?? "tagged") == "crop")
            { B = CropRect(view); boundaryDesc = "視圖裁剪框"; if (B == null) return Fail("這張視圖沒有啟用裁剪框，請改用 boundary:\"tagged\"、元件 id 或矩形。"); }
            else
            {
                foreach (var x in infos) if (x.Host.HasValue) B = B == null ? x.Host : B.Value.Union(x.Host.Value);
                boundaryDesc = "所有被標元件的外框";
            }
            if (B == null) return Fail("算不出邊界。");
            var bx = B.Value;

            var groups = new Dictionary<string, List<TagInfo>> { ["left"] = new(), ["right"] = new(), ["top"] = new(), ["bottom"] = new() };
            foreach (var x in infos)
            {
                string s = side;
                if (s == "auto") s = x.AU!.Value <= bx.CU ? "left" : "right";
                groups[s].Add(x);
            }

            var plan = new List<(TagInfo x, double hu, double hv)>();
            foreach (var kv in groups)
            {
                var list = kv.Value; if (list.Count == 0) continue;
                bool vertStack = kv.Key == "left" || kv.Key == "right";
                list = vertStack ? list.OrderByDescending(x => x.AV).ToList() : list.OrderBy(x => x.AU).ToList();
                double? last = null;
                foreach (var x in list)
                {
                    double hu, hv;
                    if (vertStack)
                    {
                        hu = kv.Key == "left" ? bx.U0 - margin : bx.U1 + margin;
                        hv = x.AV!.Value;
                        if (last.HasValue && last.Value - hv < spacing) hv = last.Value - spacing;
                        last = hv;
                    }
                    else
                    {
                        hv = kv.Key == "top" ? bx.V1 + margin : bx.V0 - margin;
                        hu = x.AU!.Value;
                        if (last.HasValue && hu - last.Value < spacing) hu = last.Value + spacing;
                        last = hu;
                    }
                    plan.Add((x, hu, hv));
                }
            }

            var preview = plan.Select(p => new { tagId = p.x.Tag.Id.Value, fromUv = R2(p.x.HU, p.x.HV), toUv = R2(p.hu, p.hv), anchorUv = R2(p.x.AU!.Value, p.x.AV!.Value) }).ToList();
            var summary = new { boundary = boundaryDesc, boundaryUv = bx.Dto(), side, marginMm = margin, spacingMm = spacing, elbow, perSide = groups.ToDictionary(k => k.Key, k => k.Value.Count) };
            if (!apply) return Ok(new { preflight = true, summary, willMove = plan.Count, moves = preview });

            var res = RunTx(_doc, "MCP Arrange Tags", () => { foreach (var p in plan) ApplyOne(view, p.x, p.hu, p.hv, true, elbow, null); });
            return res.committed
                ? Ok(new { applied = true, moved = plan.Count, summary, moves = preview, warnings = res.warnings })
                : Fail("排列標籤失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }
    }
}
