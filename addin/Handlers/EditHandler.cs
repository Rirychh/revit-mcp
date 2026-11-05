// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using static RevitMcpAddin.Handlers.PortUtil;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 修改：鏡射、依參數值在視圖中上色、清除視圖中的元件覆寫。
    /// 全部 apply=false 預檢；群組內元件一律跳過、不解除群組。
    /// </summary>
    public class EditHandler
    {
        private readonly UIApplication _uiApp;
        private readonly Document _doc;
        public EditHandler(UIApplication uiApp, Document doc) { _uiApp = uiApp; _doc = doc; }

        // POST /elements/mirror
        // { ids:[...], axis:"x"|"y", origin?:[x,y], plane?:{origin:[x,y,z], normal:[x,y,z]}, copy:true, apply }
        // axis="x"：以通過 origin、平行 X 軸的鉛直面鏡射（上下翻）；"y"：平行 Y 軸（左右翻）。
        // origin 省略 = 所選元件邊界框中心。
        public string Mirror(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false; bool copy = (bool?)r["copy"] ?? true;
            var ids = Ids(r["ids"]);
            if (ids.Count == 0) return Fail("ids 是空的。");

            var ok = new List<ElementId>(); var skipped = new List<object>();
            BoundingBoxXYZ? box = null;
            foreach (var id in ids)
            {
                var el = Get(_doc, id);
                if (el == null) { skipped.Add(new { id, reason = "找不到元件" }); continue; }
                if (InGroup(el)) { skipped.Add(new { id, reason = "在群組內（不解除群組）" }); continue; }
                if (el.Pinned) { skipped.Add(new { id, reason = "已釘選" }); continue; }
                if (!ElementTransformUtils.CanMirrorElement(_doc, el.Id)) { skipped.Add(new { id, reason = "Revit 不允許鏡射這個元件" }); continue; }
                ok.Add(el.Id);
                var bb = el.get_BoundingBox(null);
                if (bb != null)
                {
                    if (box == null) box = new BoundingBoxXYZ { Min = bb.Min, Max = bb.Max };
                    else
                    {
                        box.Min = new XYZ(Math.Min(box.Min.X, bb.Min.X), Math.Min(box.Min.Y, bb.Min.Y), Math.Min(box.Min.Z, bb.Min.Z));
                        box.Max = new XYZ(Math.Max(box.Max.X, bb.Max.X), Math.Max(box.Max.Y, bb.Max.Y), Math.Max(box.Max.Z, bb.Max.Z));
                    }
                }
            }
            if (ok.Count == 0) return Fail("沒有可鏡射的元件。" + Newtonsoft.Json.JsonConvert.SerializeObject(skipped));

            Plane plane;
            if (r["plane"] is JObject pl)
            {
                var o = Pt(pl["origin"]); var nRaw = pl["normal"] as JArray;
                if (o == null || nRaw == null || nRaw.Count < 3) return Fail("plane 需要 origin [x,y,z] 與 normal [x,y,z]。");
                var n = new XYZ((double)nRaw[0], (double)nRaw[1], (double)nRaw[2]);
                if (n.IsZeroLength()) return Fail("normal 不能是零向量。");
                plane = Plane.CreateByNormalAndOrigin(n.Normalize(), o);
            }
            else
            {
                string axis = ((string?)r["axis"] ?? "y").ToLowerInvariant();
                var o = Pt(r["origin"]) ?? (box == null ? XYZ.Zero : (box.Min + box.Max) / 2.0);
                plane = Plane.CreateByNormalAndOrigin(axis == "x" ? XYZ.BasisY : XYZ.BasisX, o);
            }

            var plan = new { willMirror = ok.Count, copy, planeOriginMm = Mm(plane.Origin), planeNormal = new[] { plane.Normal.X, plane.Normal.Y, plane.Normal.Z }, skipped };
            if (!apply) return Ok(new { preflight = true, plan });

            var newIds = new List<long>();
            var res = RunTx(_doc, "MCP Mirror Elements", () =>
            {
                var created = ElementTransformUtils.MirrorElements(_doc, ok, plane, copy);
                if (created != null) newIds.AddRange(created.Select(x => x.Value));
            });
            if (!res.committed) return Fail("鏡射失敗，已回滾、模型沒有變更：" + string.Join("；", res.errors));
            return Ok(new { applied = true, mirrored = ok.Count, copy, newIds, plan, warnings = res.warnings });
        }

        // POST /views/color-by-parameter
        // { viewId?, category, parameter, apply }
        // 依參數值分組，每組一個顏色（線色＋實心填滿前景）。只改這個視圖的元件覆寫。
        public string ColorByParameter(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var view = ResolveView((long?)r["viewId"]);
            if (view == null) return Fail("找不到視圖（viewId 省略時用作用中視圖）。");
            if (view.IsTemplate) return Fail("不能對視圖樣板上色。");
            var cat = Category(_doc, (string?)r["category"] ?? "");
            if (cat == null) return Fail("不認得 category。");
            string pname = (string?)r["parameter"] ?? "";
            if (string.IsNullOrWhiteSpace(pname)) return Fail("需要 parameter。");

            var els = new FilteredElementCollector(_doc, view.Id).OfCategory(cat.Value).WhereElementIsNotElementType().ToElements();
            var groups = new Dictionary<string, List<ElementId>>();
            int noParam = 0;
            foreach (var e in els)
            {
                var p = e.LookupParameter(pname) ?? (_doc.GetElement(e.GetTypeId())?.LookupParameter(pname));
                if (p == null) { noParam++; continue; }
                string v = (p.StorageType == StorageType.String ? p.AsString() : p.AsValueString()) ?? "";
                if (v.Length == 0) v = "(空白)";
                if (!groups.TryGetValue(v, out var list)) groups[v] = list = new List<ElementId>();
                list.Add(e.Id);
            }
            if (groups.Count == 0) return Fail($"這個視圖中的 {cat} 都沒有參數「{pname}」。");

            var keys = groups.Keys.OrderBy(k => k).ToList();
            var legend = keys.Select((k, i) => { var c = Palette(i, keys.Count); return new { value = k, count = groups[k].Count, rgb = new[] { (int)c.Red, (int)c.Green, (int)c.Blue } }; }).ToList();
            var plan = new { view = view.Name, viewId = view.Id.Value, category = cat.Value.ToString(), parameter = pname, values = keys.Count, elements = els.Count, missingParameter = noParam, legend };
            if (!apply) return Ok(new { preflight = true, plan });

            var solid = new FilteredElementCollector(_doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
            var res = RunTx(_doc, "MCP Color By Parameter", () =>
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    var c = Palette(i, keys.Count);
                    var ogs = new OverrideGraphicSettings();
                    ogs.SetProjectionLineColor(c);
                    if (solid != null) { ogs.SetSurfaceForegroundPatternId(solid.Id); ogs.SetSurfaceForegroundPatternColor(c);
                                         ogs.SetCutForegroundPatternId(solid.Id); ogs.SetCutForegroundPatternColor(c); }
                    foreach (var id in groups[keys[i]]) view.SetElementOverrides(id, ogs);
                }
            });
            return res.committed
                ? Ok(new { applied = true, colored = groups.Values.Sum(g => g.Count), plan, warnings = res.warnings })
                : Fail("上色失敗，已回滾：" + string.Join("；", res.errors));
        }

        // POST /views/clear-overrides  { viewId?, ids?:[...], category?, apply }
        // ids 與 category 都省略 = 清除這個視圖中所有可見元件的元件覆寫。
        public string ClearOverrides(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var view = ResolveView((long?)r["viewId"]);
            if (view == null) return Fail("找不到視圖（viewId 省略時用作用中視圖）。");
            var ids = Ids(r["ids"]);
            List<ElementId> targets;
            if (ids.Count > 0) targets = ids.Select(ElementIdCompat.NewId).ToList();
            else
            {
                var col = new FilteredElementCollector(_doc, view.Id).WhereElementIsNotElementType();
                var cn = (string?)r["category"];
                if (!string.IsNullOrWhiteSpace(cn))
                {
                    var cat = Category(_doc, cn!);
                    if (cat == null) return Fail("不認得 category。");
                    col = col.OfCategory(cat.Value);
                }
                targets = col.ToElementIds().ToList();
            }
            var blank = new OverrideGraphicSettings();
            // 只處理目前真的有覆寫的元件
            var overridden = targets.Where(id => { try { return !IsDefault(view.GetElementOverrides(id)); } catch { return false; } }).ToList();
            var plan = new { view = view.Name, viewId = view.Id.Value, checkedElements = targets.Count, withOverrides = overridden.Count };
            if (!apply) return Ok(new { preflight = true, plan });

            var res = RunTx(_doc, "MCP Clear Overrides", () => { foreach (var id in overridden) view.SetElementOverrides(id, blank); });
            return res.committed
                ? Ok(new { applied = true, cleared = overridden.Count, plan })
                : Fail("清除失敗，已回滾：" + string.Join("；", res.errors));
        }

        private View? ResolveView(long? id) =>
            id.HasValue ? Get(_doc, id.Value) as View : _uiApp.ActiveUIDocument?.ActiveView;

        private static bool IsDefault(OverrideGraphicSettings o) =>
            !o.ProjectionLineColor.IsValid && o.SurfaceForegroundPatternId == ElementId.InvalidElementId
            && !o.SurfaceForegroundPatternColor.IsValid && o.CutForegroundPatternId == ElementId.InvalidElementId
            && o.ProjectionLinePatternId == ElementId.InvalidElementId && o.ProjectionLineWeight == OverrideGraphicSettings.InvalidPenNumber
            && o.Transparency == 0 && !o.Halftone;

        /// <summary>均勻分布的色相（HSV，S=0.65，V=0.9），相鄰值顏色差異明顯。</summary>
        private static Color Palette(int i, int n)
        {
            double h = (i * 0.618033988749895) % 1.0, s = 0.65, v = 0.9;
            int hi = (int)(h * 6) % 6; double f = h * 6 - Math.Floor(h * 6);
            double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
            double R, G, B;
            switch (hi)
            {
                case 0: R = v; G = t; B = p; break;
                case 1: R = q; G = v; B = p; break;
                case 2: R = p; G = v; B = t; break;
                case 3: R = p; G = q; B = v; break;
                case 4: R = t; G = p; B = v; break;
                default: R = v; G = p; B = q; break;
            }
            return new Color((byte)(R * 255), (byte)(G * 255), (byte)(B * 255));
        }
    }
}
