// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMcpAddin.Models;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 依牆體重設視圖裁切範圍。牆通常在連結模型(RVT Link)裡，所以會一併搜尋
    /// 該視圖看得到的每個 RevitLinkInstance。
    ///
    /// 「結構」的判定規則：類型名稱含關鍵字（預設「輕隔間」）或厚度 ≥ minThicknessMm
    /// （預設 120，即 RC12 以上）。
    ///
    /// mode = bounding：從視圖中心往左右（或上下）找最近的一道結構牆當邊界。
    /// mode = extent  ：取所有符合條件牆體的總範圍。
    /// 預設 apply=false 乾跑，只回報選到哪幾道牆與前後尺寸。
    /// </summary>
    public class CropHandler
    {
        private const double FT = 304.8;
        private readonly Document _doc;

        public CropHandler(Document doc) => _doc = doc;

        /// <summary>一道候選牆在裁切框座標下的範圍。</summary>
        private class Cand
        {
            public CropCandidateDto Dto = null!;
            public double Min, Max;       // feet，裁切框座標
            public double PerpMin, PerpMax;
        }

        public string CropView(string body)
        {
            var req = JsonConvert.DeserializeObject<CropViewRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");

            if (_doc.GetElement(new ElementId((int)req.ViewId)) is not View view)
                return Fail($"View {req.ViewId} not found.");
            if (view.IsTemplate)
                return Fail("視圖樣板不能設裁切範圍。");

            var include = (req.IncludeTypeNames != null && req.IncludeTypeNames.Count > 0)
                ? req.IncludeTypeNames : new List<string> { "輕隔間" };
            var exclude = req.ExcludeTypeNames ?? new List<string>();

            var result = new CropViewResult
            {
                ViewId   = req.ViewId,
                ViewName = view.Name,
                Applied  = req.Apply,
                Mode     = req.Mode,
                Axis     = req.Axis,
                Rule     = $"類型名稱含 [{string.Join(" / ", include)}] 或 厚度 ≥ {req.MinThicknessMm}mm"
                           + (exclude.Count > 0 ? $"；排除 [{string.Join(" / ", exclude)}]" : "")
            };

            var cb  = view.CropBox;
            var inv = cb.Transform.Inverse;
            result.Before = Rect(cb.Min.X, cb.Max.X, cb.Min.Y, cb.Max.Y);

            bool horizontal = !req.Axis.Equals("vertical", StringComparison.OrdinalIgnoreCase);

            // ── manual：直接給數值，不做任何搜尋 ────────────────────────
            if (req.Mode.Equals("manual", StringComparison.OrdinalIgnoreCase))
            {
                double mMinX = req.LeftMm.HasValue   ? req.LeftMm.Value   / FT : cb.Min.X;
                double mMaxX = req.RightMm.HasValue  ? req.RightMm.Value  / FT : cb.Max.X;
                double mMinY = req.BottomMm.HasValue ? req.BottomMm.Value / FT : cb.Min.Y;
                double mMaxY = req.TopMm.HasValue    ? req.TopMm.Value    / FT : cb.Max.Y;

                if (mMaxX - mMinX < 1e-6 || mMaxY - mMinY < 1e-6)
                    return Fail("manual 模式的範圍為零或負值。");

                result.Seed  = "manual";
                result.After = Rect(mMinX, mMaxX, mMinY, mMaxY);
                if (!req.Apply) return Ok(result);
                var err = ApplyCrop(view, req, mMinX, mMaxX, mMinY, mMaxY);
                return err ?? Ok(result);
            }

            // ── room：直接裁到房間邊界。房間邊界就是牆內面，不必再找牆 ──
            if (req.Mode.Equals("room", StringComparison.OrdinalIgnoreCase))
            {
                var rh = ViewRoomFinder.Find(_doc, view, req.RoomSearchDepthMm);
                if (rh == null)
                    return Fail("在這個視圖的視線上取樣不到任何房間。" +
                                "可能是房間在未載入的連結裡，或現有裁切框已偏離房間；" +
                                "請改用 mode=\"bounding\" 並指定 seedElementId。");

                double rMinX = double.MaxValue, rMaxX = double.MinValue;
                double rMinY = double.MaxValue, rMaxY = double.MinValue;
                foreach (var p in rh.Corners)
                {
                    var v = inv.OfPoint(p);
                    if (v.X < rMinX) rMinX = v.X;
                    if (v.X > rMaxX) rMaxX = v.X;
                    if (v.Y < rMinY) rMinY = v.Y;
                    if (v.Y > rMaxY) rMaxY = v.Y;
                }

                double rpad = req.PaddingMm / FT;
                bool doX = horizontal || req.Axis.Equals("both", StringComparison.OrdinalIgnoreCase);
                bool doY = !horizontal || req.Axis.Equals("both", StringComparison.OrdinalIgnoreCase);

                double rx0 = doX ? rMinX - rpad : cb.Min.X;
                double rx1 = doX ? rMaxX + rpad : cb.Max.X;
                double ry0 = doY ? rMinY - rpad : cb.Min.Y;
                double ry1 = doY ? rMaxY + rpad : cb.Max.Y;

                if (rx1 - rx0 < 1e-6 || ry1 - ry0 < 1e-6)
                    return Fail("依房間邊界裁切後寬或高變成零／負值。");

                result.Seed = $"房間 {rh}，視線深度 {rh.DepthMm}mm 處取樣到"
                              + (rh.Alternatives.Count > 0
                                 ? $"；同點也打到 {string.Join("、", rh.Alternatives)}，已取面積最小者"
                                 : "");
                result.Rule = "裁到房間邊界（房間邊界即牆內面）";
                result.After = Rect(rx0, rx1, ry0, ry1);
                if (!req.Apply) return Ok(result);
                var rErr = ApplyCrop(view, req, rx0, rx1, ry0, ry1);
                return rErr ?? Ok(result);
            }

            // 搜尋體積：現有裁切框往外擴 searchPaddingMm
            double sp = req.SearchPaddingMm / FT;
            var searchMin = new XYZ(cb.Min.X - sp, cb.Min.Y - sp, cb.Min.Z);
            var searchMax = new XYZ(cb.Max.X + sp, cb.Max.Y + sp, cb.Max.Z);
            var hostSearchPts = BoxCorners(searchMin, searchMax)
                .Select(p => cb.Transform.OfPoint(p)).ToList();

            var cands = new List<Cand>();

            // ── 明確指定元件時，直接用它們的範圍 ──────────────────────
            if (req.ElementIds != null && req.ElementIds.Count > 0)
            {
                foreach (var id in req.ElementIds)
                {
                    var el = _doc.GetElement(new ElementId((int)id));
                    if (el == null)
                    { result.Skipped.Add(new SkippedDto { ElementId = id, Reason = "元件不存在" }); continue; }
                    var bb = el.get_BoundingBox(view) ?? el.get_BoundingBox(null);
                    if (bb == null)
                    { result.Skipped.Add(new SkippedDto { ElementId = id, Reason = "取不到範圍框" }); continue; }

                    var c = MakeCand(el.Id.IntegerValue, "本模型", TypeNameOf(el, _doc),
                                     0, "指定", bb, Transform.Identity, inv, horizontal);
                    cands.Add(c);
                    result.Matched.Add(c.Dto);
                }
            }
            else
            {
                // ── 本模型 + 各連結模型的牆 ────────────────────────────
                var viewDir = view.ViewDirection;
                CollectWalls(_doc, Transform.Identity, "本模型", view, viewDir,
                             req, include, exclude, inv, horizontal, hostSearchPts, cands, result);

                if (req.IncludeLinks)
                {
                    foreach (var li in new FilteredElementCollector(_doc, view.Id)
                                 .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                    {
                        var ldoc = li.GetLinkDocument();
                        if (ldoc == null)
                        {
                            result.Skipped.Add(new SkippedDto
                            { ElementId = li.Id.IntegerValue, Reason = "連結未載入，跳過" });
                            continue;
                        }
                        var xf = li.GetTotalTransform();
                        var label = ldoc.Title;
                        result.SearchedLinks.Add(label);
                        CollectWalls(ldoc, xf, label, null, viewDir,
                                     req, include, exclude, inv, horizontal, hostSearchPts, cands, result);
                    }
                }
            }

            if (cands.Count == 0)
            {
                result.Skipped.Add(new SkippedDto
                {
                    ElementId = 0,
                    Reason = result.SearchedLinks.Count == 0 && req.IncludeLinks
                        ? "這個視圖裡看不到任何已載入的 RevitLinkInstance，本模型也沒有符合的牆。" +
                          "請確認連結在此視圖是可見的（可見性/圖形設定的「Revit 連結」分頁）。"
                        : $"搜尋了 {result.SearchedLinks.Count} 個連結，但沒有牆符合條件。" +
                          "nearMiss 列出範圍內被篩掉的牆與它們的類型／厚度，可據此調整 minThicknessMm 或 includeTypeNames。"
                });
                return Ok(result);
            }

            // ── 決定裁切邊界 ──────────────────────────────────────────
            bool outerEdge = !req.WallEdge.Equals("inner", StringComparison.OrdinalIgnoreCase);

            // 種子：給了 seedElementId 就用該元件（通常是房間）的範圍當內界，
            // 左界只找完全在它左邊的牆、右界只找完全在它右邊的牆。
            // 沒給就退回用現有裁切框中心當一個點。
            double seedLo, seedHi;
            if (req.SeedElementId.HasValue)
            {
                var se = _doc.GetElement(new ElementId((int)req.SeedElementId.Value));
                if (se == null)
                    return Fail($"seedElementId {req.SeedElementId.Value} 不存在。");
                var sbb = se.get_BoundingBox(view) ?? se.get_BoundingBox(null);
                if (sbb == null)
                    return Fail($"seedElementId {req.SeedElementId.Value} 取不到範圍框。");

                double sMin = double.MaxValue, sMax = double.MinValue;
                foreach (var p in Corners(sbb))
                {
                    var v = inv.OfPoint(p);
                    double a = horizontal ? v.X : v.Y;
                    if (a < sMin) sMin = a;
                    if (a > sMax) sMax = a;
                }
                seedLo = sMin; seedHi = sMax;
                result.Seed = $"元件 {req.SeedElementId.Value}（{SafeName(se)}）" +
                              $" {Math.Round(sMin * FT)}~{Math.Round(sMax * FT)}mm";
            }
            else
            {
                // 沒指定種子就自己找出這張圖在畫的房間，用房間範圍當內界。
                var rh = ViewRoomFinder.Find(_doc, view, req.RoomSearchDepthMm);
                if (rh != null)
                {
                    double aMin = double.MaxValue, aMax = double.MinValue;
                    foreach (var p in rh.Corners)
                    {
                        var v = inv.OfPoint(p);
                        double a = horizontal ? v.X : v.Y;
                        if (a < aMin) aMin = a;
                        if (a > aMax) aMax = a;
                    }
                    seedLo = aMin; seedHi = aMax;
                    result.Seed = $"自動判定房間 {rh} {Math.Round(aMin * FT)}~{Math.Round(aMax * FT)}mm"
                                  + (rh.Alternatives.Count > 0
                                     ? $"；同點也打到 {string.Join("、", rh.Alternatives)}"
                                     : "");
                }
                else
                {
                    double c0 = horizontal ? (cb.Min.X + cb.Max.X) * 0.5 : (cb.Min.Y + cb.Max.Y) * 0.5;
                    seedLo = seedHi = c0;
                    result.Seed = $"現有裁切框中心 {Math.Round(c0 * FT)}mm" +
                                  "（取樣不到房間，建議改用 seedElementId 指定）";
                }
            }

            double lo, hi;
            Cand? loCand = null, hiCand = null;

            if (req.Mode.Equals("extent", StringComparison.OrdinalIgnoreCase))
            {
                lo = cands.Min(c => c.Min);
                hi = cands.Max(c => c.Max);
                loCand = cands.OrderBy(c => c.Min).First();
                hiCand = cands.OrderByDescending(c => c.Max).First();
            }
            else
            {
                // bounding：種子範圍外側各找最近的一道
                const double tol = 1.0 / FT;   // 1mm
                var left  = cands.Where(c => c.Max <= seedLo + tol).OrderByDescending(c => c.Max).ToList();
                var right = cands.Where(c => c.Min >= seedHi - tol).OrderBy(c => c.Min).ToList();

                if (left.Count == 0 || right.Count == 0)
                {
                    result.Skipped.Add(new SkippedDto
                    {
                        ElementId = 0,
                        Reason = $"種子{(left.Count == 0 ? "左" : "右")}側找不到符合條件的牆。" +
                                 "可改用 seedElementId 指定房間、放寬 wallRun=\"any\"、" +
                                 "加大 searchPaddingMm，或改用 mode=\"extent\"。"
                    });
                    return Ok(result);
                }

                loCand = left[0];
                hiCand = right[0];
                lo = outerEdge ? loCand.Min : loCand.Max;
                hi = outerEdge ? hiCand.Max : hiCand.Min;
            }

            if (horizontal) { result.LeftWall = loCand?.Dto; result.RightWall = hiCand?.Dto; }
            else            { result.BottomRef = loCand?.Dto; result.TopRef = hiCand?.Dto; }

            double pad = req.PaddingMm / FT;
            lo -= pad; hi += pad;

            double fMinX = cb.Min.X, fMaxX = cb.Max.X;
            double fMinY = cb.Min.Y, fMaxY = cb.Max.Y;
            if (horizontal) { fMinX = lo; fMaxX = hi; }
            else            { fMinY = lo; fMaxY = hi; }

            if (fMaxX - fMinX < 1e-6 || fMaxY - fMinY < 1e-6)
                return Fail("裁切後寬或高變成零／負值，請調整 paddingMm 或 wallEdge。");

            result.After = Rect(fMinX, fMaxX, fMinY, fMaxY);

            if (!req.Apply) return Ok(result);

            var applyErr = ApplyCrop(view, req, fMinX, fMaxX, fMinY, fMaxY);
            return applyErr ?? Ok(result);
        }

        // ─── 收集某個模型（本模型或連結）裡符合條件的牆 ────────────────────────
        private static void CollectWalls(
            Document src, Transform xf, string label, View? hostView, XYZ viewDir,
            CropViewRequest req, List<string> include, List<string> exclude,
            Transform inv, bool horizontal, List<XYZ> hostSearchPts,
            List<Cand> cands, CropViewResult result)
        {
            bool perpOnly = req.WallRun.Equals("perpendicular", StringComparison.OrdinalIgnoreCase);
            var xfInv = xf.Inverse;
            var pts = hostSearchPts.Select(p => xfInv.OfPoint(p)).ToList();
            var omin = new XYZ(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z));
            var omax = new XYZ(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z));
            if (omax.X - omin.X < 1e-6 || omax.Y - omin.Y < 1e-6 || omax.Z - omin.Z < 1e-6) return;

            var filter = new BoundingBoxIntersectsFilter(new Outline(omin, omax));

            var col = hostView != null
                ? new FilteredElementCollector(src, hostView.Id).OfCategory(BuiltInCategory.OST_Walls)
                : new FilteredElementCollector(src).OfCategory(BuiltInCategory.OST_Walls);

            IList<Element> walls;
            try { walls = col.WhereElementIsNotElementType().WherePasses(filter).ToElements(); }
            catch (Exception ex)
            {
                result.Skipped.Add(new SkippedDto { ElementId = 0, Reason = $"{label}：搜尋失敗 {ex.Message}" });
                return;
            }

            foreach (var e in walls)
            {
                string typeName = TypeNameOf(e, src);
                double thick    = ThicknessMm(e);
                var bb = e.get_BoundingBox(null);
                if (bb == null) continue;

                if (exclude.Any(k => typeName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;

                // 牆身走向：室內立面的側牆是往畫面內延伸的（定位線方向 ∥ 視線方向），
                // 跟畫面平行的背牆、走廊長牆不該當左右邊界。
                double align = RunAlignment(e, xf, viewDir);

                string? matchedBy = null;
                var hit = include.FirstOrDefault(k => typeName.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit != null) matchedBy = $"類型名稱含「{hit}」";
                else if (thick >= req.MinThicknessMm - 0.5)
                    matchedBy = $"厚度 {Math.Round(thick)}mm ≥ {req.MinThicknessMm}mm";

                if (matchedBy != null && perpOnly && align < 0.7)
                    matchedBy = null;   // 條件符合但走向不對，當作未符合並列進 nearMiss

                var c = MakeCand(e.Id.IntegerValue, label, typeName, thick,
                                 matchedBy ?? (align < 0.7 ? $"走向不符（{align:0.00}）" : "未符合"),
                                 bb, xf, inv, horizontal);
                c.Dto.RunAlignment = Math.Round(align, 3);

                if (matchedBy == null)
                {
                    if (result.NearMiss.Count < 40) result.NearMiss.Add(c.Dto);
                    continue;
                }
                cands.Add(c);
                if (result.Matched.Count < 60) result.Matched.Add(c.Dto);
            }
        }

        // ─── 輔助 ──────────────────────────────────────────────────────────────
        private static Cand MakeCand(long id, string source, string typeName, double thickMm,
            string matchedBy, BoundingBoxXYZ bb, Transform xf, Transform inv, bool horizontal)
        {
            double min = double.MaxValue, max = double.MinValue;
            double pmin = double.MaxValue, pmax = double.MinValue;

            foreach (var p in Corners(bb))
            {
                var v = inv.OfPoint(xf.OfPoint(p));
                double a = horizontal ? v.X : v.Y;
                double b = horizontal ? v.Y : v.X;
                if (a < min)  min  = a;
                if (a > max)  max  = a;
                if (b < pmin) pmin = b;
                if (b > pmax) pmax = b;
            }

            return new Cand
            {
                Min = min, Max = max, PerpMin = pmin, PerpMax = pmax,
                Dto = new CropCandidateDto
                {
                    ElementId   = id,
                    Source      = source,
                    TypeName    = typeName,
                    ThicknessMm = Math.Round(thickMm, 1),
                    MatchedBy   = matchedBy,
                    MinMm       = Math.Round(min * FT, 1),
                    MaxMm       = Math.Round(max * FT, 1),
                }
            };
        }

        /// <summary>
        /// 牆定位線方向與視線方向的夾角餘弦絕對值（換算到主模型座標後比較）。
        /// 1 = 完全往畫面內延伸（立面上會被剖到的側牆）；0 = 與畫面平行（背牆）。
        /// 取不到定位線時回 1，交給其他條件判斷。
        /// </summary>
        private static double RunAlignment(Element e, Transform xf, XYZ viewDir)
        {
            try
            {
                if (e.Location is not LocationCurve lc) return 1.0;
                var curve = lc.Curve;
                if (curve == null) return 1.0;

                var d = curve.GetEndPoint(1) - curve.GetEndPoint(0);
                if (d.GetLength() < 1e-9) return 1.0;

                var world = xf.OfVector(d).Normalize();
                var v = new XYZ(viewDir.X, viewDir.Y, 0);
                if (v.GetLength() < 1e-9) return 1.0;
                v = v.Normalize();

                var w = new XYZ(world.X, world.Y, 0);
                if (w.GetLength() < 1e-9) return 1.0;
                w = w.Normalize();

                return Math.Abs(w.DotProduct(v));
            }
            catch { return 1.0; }
        }

        /// <summary>實際寫入裁切框。成功回 null，失敗回錯誤 JSON。</summary>
        private string? ApplyCrop(View view, CropViewRequest req,
                                  double minX, double maxX, double minY, double maxY)
        {
            using var tx = new Transaction(_doc, "MCP 設定視圖裁切範圍");
            tx.Start();
            try
            {
                view.CropBoxActive = true;
                if (req.CropVisible.HasValue) view.CropBoxVisible = req.CropVisible.Value;

                var box = view.CropBox;
                box.Min = new XYZ(minX, minY, box.Min.Z);
                box.Max = new XYZ(maxX, maxY, box.Max.Z);
                view.CropBox = box;

                // 標註裁剪放大：模型裁剪收緊後，貼邊的標籤會被擠回框內並長出引線，
                // 把標註裁剪往外放就能讓標籤留在原位。
                if (req.AnnotationCropMm > 0)
                {
                    var ap = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                    if (ap != null && !ap.IsReadOnly) ap.Set(1);

                    var mgr = view.GetCropRegionShapeManager();
                    double off = req.AnnotationCropMm / FT;
                    mgr.LeftAnnotationCropOffset   = off;
                    mgr.RightAnnotationCropOffset  = off;
                    mgr.TopAnnotationCropOffset    = off;
                    mgr.BottomAnnotationCropOffset = off;
                }
            }
            catch (Exception ex)
            {
                tx.RollBack();
                return Fail("設定裁切框失敗：" + ex.Message +
                            "（非矩形裁切造型的視圖需先在 Revit 裡把裁切造型重設為矩形）");
            }
            tx.Commit();
            return null;
        }

        private static double ThicknessMm(Element e)
        {
            try { if (e is Wall w) return w.Width * FT; } catch { }

            var p = e.get_Parameter(BuiltInParameter.WALL_ATTR_WIDTH_PARAM);
            if (p != null && p.StorageType == StorageType.Double) return p.AsDouble() * FT;

            try
            {
                var t  = e.Document.GetElement(e.GetTypeId());
                var tp = t?.get_Parameter(BuiltInParameter.WALL_ATTR_WIDTH_PARAM);
                if (tp != null && tp.StorageType == StorageType.Double) return tp.AsDouble() * FT;
            }
            catch { }
            return 0;
        }

        private static string TypeNameOf(Element e, Document d)
        {
            try { return d.GetElement(e.GetTypeId())?.Name ?? ""; } catch { return ""; }
        }

        private static string SafeName(Element e)
        {
            try { return e.Name ?? ""; } catch { return ""; }
        }

        private static CropRectDto Rect(double minX, double maxX, double minY, double maxY) => new()
        {
            LeftMm   = Math.Round(minX * FT, 1),
            RightMm  = Math.Round(maxX * FT, 1),
            BottomMm = Math.Round(minY * FT, 1),
            TopMm    = Math.Round(maxY * FT, 1),
            WidthMm  = Math.Round((maxX - minX) * FT, 1),
            HeightMm = Math.Round((maxY - minY) * FT, 1),
        };

        private static IEnumerable<XYZ> Corners(BoundingBoxXYZ bb)
        {
            var t = bb.Transform;
            foreach (var p in BoxCorners(bb.Min, bb.Max)) yield return t.OfPoint(p);
        }

        private static IEnumerable<XYZ> BoxCorners(XYZ a, XYZ b)
        {
            foreach (var x in new[] { a.X, b.X })
                foreach (var y in new[] { a.Y, b.Y })
                    foreach (var z in new[] { a.Z, b.Z })
                        yield return new XYZ(x, y, z);
        }

        private static string Ok(CropViewResult r) =>
            JsonConvert.SerializeObject(ApiResponse<CropViewResult>.Ok(r));

        private static string Fail(string msg) =>
            JsonConvert.SerializeObject(ApiResponse<CropViewResult>.Fail(msg));
    }
}
