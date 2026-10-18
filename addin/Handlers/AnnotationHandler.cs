// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMcpAddin.Models;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 視圖標註：設備編號標籤 + 尺寸鏈。
    /// 兩支 API 都預設 apply=false（乾跑，只回報要做什麼），
    /// 確認結果無誤後再送 apply=true 實際寫入模型（單一 Transaction）。
    /// </summary>
    public class AnnotationHandler
    {
        private const double FT = 304.8;          // 1 foot = 304.8 mm
        private readonly Document _doc;

        // RestrictToRoom 算出來的房間水平範圍（視圖座標），引線分邊排版要用。
        private double _roomXMin, _roomXMax;
        private bool   _roomSpanKnown;

        public AnnotationHandler(Document doc) => _doc = doc;

        /// <summary>參考點：一個幾何參考 + 它投影在尺寸方向上的位置（feet）。</summary>
        private class RefPick
        {
            public Reference Ref = null!;
            public double    Pos;
            public Element   Owner = null!;
        }

        /// <summary>要標註的對象。來自連結時帶著它的 RevitLinkInstance 與座標轉換。</summary>
        private class TagTarget
        {
            public Element              El     = null!;
            public RevitLinkInstance?   Link;
            public Transform            Xf     = Transform.Identity;
            public string               Source = "本模型";
        }

        // ─── 列出可用的標籤類型 ────────────────────────────────────────────────
        public string ListTagTypes(NameValueCollection qs)
        {
            var filter = qs["category"];

            var types = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(s => s.Category != null && s.Category.CategoryType == CategoryType.Annotation)
                .Where(s => string.IsNullOrEmpty(filter)
                            || s.Category!.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                            || s.FamilyName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                            || s.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(s => new TagTypeDto
                {
                    Id         = s.Id.IntegerValue,
                    FamilyName = s.FamilyName,
                    TypeName   = s.Name,
                    Category   = s.Category!.Name
                })
                .OrderBy(t => t.Category).ThenBy(t => t.FamilyName).ThenBy(t => t.TypeName)
                .ToList();

            return JsonConvert.SerializeObject(ApiResponse<List<TagTypeDto>>.Ok(types));
        }

        // ─── 讀出某張視圖現有的標註，用來照抄既有圖面的標註慣例 ─────────────────
        public string ListViewAnnotations(NameValueCollection qs)
        {
            if (!long.TryParse(qs["viewId"], out var vid))
                return Fail<ViewAnnotationsResult>("需要 viewId 參數。");
            if (_doc.GetElement(new ElementId((int)vid)) is not View view)
                return Fail<ViewAnnotationsResult>($"View {vid} not found.");

            var res = new ViewAnnotationsResult { ViewId = vid, ViewName = view.Name };

            foreach (var t in new FilteredElementCollector(_doc, view.Id)
                         .OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                var sym = _doc.GetElement(t.GetTypeId()) as FamilySymbol;
                var dto = new ViewTagDto
                {
                    TagId      = t.Id.IntegerValue,
                    Category   = t.Category?.Name ?? "",
                    FamilyName = sym?.FamilyName ?? "",
                    TypeName   = sym?.Name ?? "",
                    HasLeader  = t.HasLeader
                };
                try { dto.Text = t.TagText ?? ""; } catch { dto.Text = ""; }

                foreach (var r in t.GetTaggedReferences())
                {
                    var linked = r.LinkedElementId;
                    if (linked != null && linked != ElementId.InvalidElementId)
                    {
                        dto.TaggedIds.Add(linked.IntegerValue);
                        var li = _doc.GetElement(r.ElementId) as RevitLinkInstance;
                        var ld = li?.GetLinkDocument();
                        var le = ld?.GetElement(linked);
                        dto.TaggedNames.Add(le != null ? $"{SafeName(le)}（連結）" : "（連結元件）");
                    }
                    else
                    {
                        dto.TaggedIds.Add(r.ElementId.IntegerValue);
                        var he = _doc.GetElement(r.ElementId);
                        dto.TaggedNames.Add(he != null ? SafeName(he) : "");
                    }
                }
                res.Tags.Add(dto);
            }

            foreach (var d in new FilteredElementCollector(_doc, view.Id)
                         .OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                var dto = new ViewDimDto
                {
                    DimensionId = d.Id.IntegerValue,
                    TypeName    = (_doc.GetElement(d.GetTypeId()) as ElementType)?.Name ?? "",
                    Category    = d.Category?.Name ?? ""
                };
                try
                {
                    if (d.NumberOfSegments > 0)
                        foreach (DimensionSegment s in d.Segments)
                            dto.SegmentsMm.Add(Math.Round((s.Value ?? 0) * FT));
                    else if (d.Value.HasValue)
                        dto.SegmentsMm.Add(Math.Round(d.Value.Value * FT));
                }
                catch { }
                res.Dimensions.Add(dto);
            }

            foreach (var n in new FilteredElementCollector(_doc, view.Id)
                         .OfClass(typeof(TextNote)).Cast<TextNote>())
            {
                double k = view.Scale > 0 ? view.Scale : 1;   // 模型單位 → 圖面 mm
                res.TextNotes.Add(new ViewTextDto
                {
                    TextNoteId = n.Id.IntegerValue,
                    TypeName   = (_doc.GetElement(n.GetTypeId()) as ElementType)?.Name ?? "",
                    Text       = n.Text?.Trim() ?? "",
                    X          = Math.Round(n.Coord.X * FT / k, 1),
                    Y          = Math.Round(n.Coord.Y * FT / k, 1)
                });
            }

            return JsonConvert.SerializeObject(ApiResponse<ViewAnnotationsResult>.Ok(res));
        }

        // ─── 刪掉視圖裡的標籤（跑錯時的退路，不必靠 Ctrl+Z） ─────────────────────
        public string DeleteViewTags(string body)
        {
            var req = JsonConvert.DeserializeObject<DeleteTagsRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");

            if (_doc.GetElement(new ElementId((int)req.ViewId)) is not View view)
                return Fail<DeleteTagsResult>($"View {req.ViewId} not found.");

            var res = new DeleteTagsResult
            { ViewId = req.ViewId, ViewName = view.Name, Applied = req.Apply };

            var victims = new List<(IndependentTag tag, string label)>();
            foreach (var t in new FilteredElementCollector(_doc, view.Id)
                         .OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                var sym  = _doc.GetElement(t.GetTypeId()) as FamilySymbol;
                string fam = sym?.FamilyName ?? "";
                string typ = sym?.Name ?? "";
                string txt; try { txt = (t.TagText ?? "").Trim(); } catch { txt = ""; }

                if (req.TagIds != null && req.TagIds.Count > 0)
                {
                    if (!req.TagIds.Contains(t.Id.IntegerValue)) continue;
                }
                else
                {
                    if (!string.IsNullOrEmpty(req.TagFamilyName) && fam != req.TagFamilyName) continue;
                    if (!string.IsNullOrEmpty(req.TagTypeName)   && typ != req.TagTypeName)   continue;
                    if (req.BlankTextOnly && txt.Length > 0) continue;
                }
                victims.Add((t, $"[{t.Id.IntegerValue}] {fam} : {typ} '{txt}'"));
            }

            res.Matched = victims.Select(v => v.label).ToList();
            if (!req.Apply) return JsonConvert.SerializeObject(ApiResponse<DeleteTagsResult>.Ok(res));

            using (var tx = new Transaction(_doc, "MCP 刪除視圖標籤"))
            {
                tx.Start();
                foreach (var v in victims)
                {
                    try { _doc.Delete(v.tag.Id); res.Deleted++; }
                    catch (Exception ex) { res.Failed.Add($"{v.label} → {ex.Message}"); }
                }
                tx.Commit();
            }
            return JsonConvert.SerializeObject(ApiResponse<DeleteTagsResult>.Ok(res));
        }

        // ─── 在視圖內為元件放置標籤 ────────────────────────────────────────────
        public string TagElements(string body)
        {
            var req = JsonConvert.DeserializeObject<TagElementsRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");

            if (_doc.GetElement(new ElementId((int)req.ViewId)) is not View view)
                return Fail<TagElementsResult>($"View {req.ViewId} not found.");
            if (view.IsTemplate)
                return Fail<TagElementsResult>("視圖樣板不能放標籤。");

            var result = new TagElementsResult
            {
                ViewId   = req.ViewId,
                ViewName = view.Name,
                Applied  = req.Apply
            };

            var targets = CollectTargets(req, view, result);
            targets = RestrictToRoom(targets, view, req, result);
            if (req.SkipTagged)    targets = RemoveAlreadyTagged(targets, view, result);
            if (req.SkipBlankMark) targets = RemoveBlankMark(targets, result);
            targets = FilterByMark(targets, req, result);
            if (req.DedupeByMark)  targets = DedupeByMark(targets, view, result);
            result.Candidates = targets.Count;

            FamilySymbol? tagSymbol = null;
            if (!string.IsNullOrEmpty(req.TagFamilyName) || !string.IsNullOrEmpty(req.TagTypeName))
            {
                tagSymbol = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .FirstOrDefault(s => s.Category != null
                        && s.Category.CategoryType == CategoryType.Annotation
                        && (string.IsNullOrEmpty(req.TagFamilyName) || s.FamilyName == req.TagFamilyName)
                        && (string.IsNullOrEmpty(req.TagTypeName)   || s.Name       == req.TagTypeName));

                if (tagSymbol == null)
                    return Fail<TagElementsResult>(
                        $"找不到標籤類型 '{req.TagFamilyName} : {req.TagTypeName}'。" +
                        "用 GET /annotations/tagtypes 查目前模型有哪些標籤族群/類型。");
            }
            result.TagTypeUsed = tagSymbol != null
                ? $"{tagSymbol.FamilyName} : {tagSymbol.Name}"
                : "(依類別預設標籤)";

            var orient = req.Orientation.Equals("vertical", StringComparison.OrdinalIgnoreCase)
                ? TagOrientation.Vertical
                : TagOrientation.Horizontal;

            var right = view.RightDirection;
            var up    = view.UpDirection;
            double off = req.OffsetMm / FT;
            XYZ delta = req.OffsetDirection.ToLowerInvariant() switch
            {
                "up"    =>  up    * off,
                "down"  => -up    * off,
                "left"  => -right * off,
                "right" =>  right * off,
                _       => XYZ.Zero
            };

            bool sideLeaders = req.Layout.Equals("sideLeaders", StringComparison.OrdinalIgnoreCase);
            var heads = sideLeaders ? LayoutSideLeaders(targets, view, req)
                                    : new Dictionary<long, XYZ>();
            bool leader = req.HasLeader || sideLeaders;
            if (sideLeaders)
                result.Scope += $"；標籤頭分邊拉出（房間邊界外 {req.LeaderMarginMm}mm，同側最小間距 {req.LeaderSpacingMm}mm）";

            // 乾跑：只回報預定放置點，不動模型
            if (!req.Apply)
            {
                foreach (var t in targets)
                {
                    var c = CenterInView(t, view);
                    if (c == null)
                    {
                        result.Skipped.Add(new SkippedDto
                        { ElementId = t.El.Id.IntegerValue, Reason = "視圖內取不到範圍框" });
                        continue;
                    }
                    var pt = heads.TryGetValue(t.El.Id.IntegerValue, out var h0) ? h0 : c + delta;
                    result.Placed.Add(new TagPlacementDto
                    {
                        ElementId   = t.El.Id.IntegerValue,
                        ElementName = SafeName(t.El),
                        Category    = t.El.Category?.Name ?? "",
                        Source      = t.Source,
                        TagId       = 0,
                        TagText     = "(乾跑，尚未建立)",
                        X = pt.X * FT, Y = pt.Y * FT, Z = pt.Z * FT
                    });
                }
                return JsonConvert.SerializeObject(ApiResponse<TagElementsResult>.Ok(result));
            }

            using (var tx = new Transaction(_doc, "MCP 放置設備標籤"))
            {
                tx.Start();
                if (tagSymbol != null && !tagSymbol.IsActive) tagSymbol.Activate();

                foreach (var t in targets)
                {
                    try
                    {
                        var c = CenterInView(t, view);
                        if (c == null)
                        {
                            result.Skipped.Add(new SkippedDto
                            { ElementId = t.El.Id.IntegerValue, Reason = "視圖內取不到範圍框" });
                            continue;
                        }
                        // 分邊排版時 pt 就是標籤頭的位置，引線由 Revit 自動接回元件。
                        var pt = heads.TryGetValue(t.El.Id.IntegerValue, out var h1) ? h1 : c + delta;

                        // 連結裡的元件要用 link reference，否則 Revit 不知道標的在哪個連結
                        var rf = t.Link != null
                            ? new Reference(t.El).CreateLinkReference(t.Link)
                            : new Reference(t.El);

                        var tag = tagSymbol != null
                            ? IndependentTag.Create(_doc, tagSymbol.Id, view.Id, rf,
                                                    leader, orient, pt)
                            : IndependentTag.Create(_doc, view.Id, rf, leader,
                                                    TagMode.TM_ADDBY_CATEGORY, orient, pt);

                        string txt;
                        try { txt = tag.TagText ?? ""; } catch { txt = ""; }

                        result.Placed.Add(new TagPlacementDto
                        {
                            ElementId   = t.El.Id.IntegerValue,
                            ElementName = SafeName(t.El),
                            Category    = t.El.Category?.Name ?? "",
                            Source      = t.Source,
                            TagId       = tag.Id.IntegerValue,
                            TagText     = txt,
                            X = pt.X * FT, Y = pt.Y * FT, Z = pt.Z * FT
                        });
                    }
                    catch (Exception ex)
                    {
                        result.Skipped.Add(new SkippedDto
                        { ElementId = t.El.Id.IntegerValue, Reason = ex.Message });
                    }
                }
                tx.Commit();
            }

            return JsonConvert.SerializeObject(ApiResponse<TagElementsResult>.Ok(result));
        }

        // ─── 建立尺寸鏈 ────────────────────────────────────────────────────────
        public string CreateDimension(string body)
        {
            var req = JsonConvert.DeserializeObject<DimensionRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");

            if (_doc.GetElement(new ElementId((int)req.ViewId)) is not View view)
                return Fail<DimensionResult>($"View {req.ViewId} not found.");
            if (view.IsTemplate)
                return Fail<DimensionResult>("視圖樣板不能加尺寸標註。");
            if (req.ElementIds == null || req.ElementIds.Count < 2)
                return Fail<DimensionResult>("elementIds 需至少 2 個元件才能串成尺寸鏈。");

            bool horizontal = !req.Direction.Equals("vertical", StringComparison.OrdinalIgnoreCase);
            XYZ dimDir = horizontal ? view.RightDirection : view.UpDirection;   // 尺寸線方向
            XYZ offDir = horizontal ? view.UpDirection    : view.RightDirection; // 偏移方向

            var result = new DimensionResult
            {
                ViewId    = req.ViewId,
                ViewName  = view.Name,
                Direction = horizontal ? "horizontal" : "vertical",
                Applied   = req.Apply
            };

            var opt = new Options
            {
                ComputeReferences        = true,
                IncludeNonVisibleObjects = false,
                View                     = view
            };

            var picks = new List<RefPick>();
            var used  = new List<Element>();

            foreach (var id in req.ElementIds)
            {
                var el = _doc.GetElement(new ElementId((int)id));
                if (el == null)
                {
                    result.Skipped.Add(new SkippedDto { ElementId = id, Reason = "元件不存在" });
                    continue;
                }

                List<RefPick> refs;
                try { refs = PlanarFaceRefs(el, opt, dimDir); }
                catch (Exception ex)
                {
                    result.Skipped.Add(new SkippedDto { ElementId = id, Reason = "讀幾何失敗：" + ex.Message });
                    continue;
                }
                if (refs.Count == 0) refs = FamilyCenterRef(el, dimDir);

                if (refs.Count == 0)
                {
                    result.Skipped.Add(new SkippedDto
                    { ElementId = id, Reason = "找不到與尺寸方向垂直的參考面（可改用另一個 direction）" });
                    continue;
                }

                used.Add(el);
                picks.AddRange(refs);
                result.References.Add(new DimensionRefDto
                {
                    ElementId  = id,
                    Name       = SafeName(el),
                    Category   = el.Category?.Name ?? "",
                    RefCount   = refs.Count,
                    PositionMm = refs.Min(r => r.Pos) * FT
                });
            }

            // 依尺寸方向排序，並把 1mm 內重合的參考視為同一點
            picks.Sort((a, b) => a.Pos.CompareTo(b.Pos));
            var uniq = new List<RefPick>();
            foreach (var p in picks)
                if (uniq.Count == 0 || Math.Abs(p.Pos - uniq[uniq.Count - 1].Pos) > (1.0 / FT))
                    uniq.Add(p);

            if (uniq.Count < 2)
                return Fail<DimensionResult>("有效參考少於 2 個，無法建立尺寸鏈。");

            for (int i = 1; i < uniq.Count; i++)
                result.SegmentsMm.Add(Math.Round((uniq[i].Pos - uniq[i - 1].Pos) * FT, 1));
            result.SegmentCount = uniq.Count - 1;

            DimensionType? dimType = null;
            if (!string.IsNullOrEmpty(req.DimensionTypeName))
            {
                dimType = new FilteredElementCollector(_doc)
                    .OfClass(typeof(DimensionType)).Cast<DimensionType>()
                    .FirstOrDefault(t => t.Name == req.DimensionTypeName);
                if (dimType == null)
                    return Fail<DimensionResult>($"找不到尺寸型式 '{req.DimensionTypeName}'。");
            }
            result.DimensionType = dimType?.Name ?? "(視圖預設)";

            // 尺寸線擺放位置：取所有元件在視圖內的外緣，往外推 offsetMm
            double pMin = double.MaxValue, pMax = double.MinValue;
            foreach (var e in used)
            {
                var bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
                if (bb == null) continue;
                foreach (var c in Corners(bb))
                {
                    double v = c.DotProduct(offDir);
                    if (v < pMin) pMin = v;
                    if (v > pMax) pMax = v;
                }
            }
            if (pMin > pMax)
                return Fail<DimensionResult>("取不到元件在視圖中的範圍，無法決定尺寸線位置。");

            bool positive;
            switch (req.Side.ToLowerInvariant())
            {
                case "top":
                case "right":  positive = true;  break;
                case "bottom":
                case "left":   positive = false; break;
                default:       positive = horizontal; break;   // auto：水平放上方、垂直放左側
            }
            double perp = positive ? pMax + req.OffsetMm / FT : pMin - req.OffsetMm / FT;

            XYZ o = view.Origin;
            XYZ PointAt(double along) =>
                o + dimDir * (along - o.DotProduct(dimDir))
                  + offDir * (perp  - o.DotProduct(offDir));

            var p1 = PointAt(uniq[0].Pos);
            var p2 = PointAt(uniq[uniq.Count - 1].Pos);
            if (p1.DistanceTo(p2) < _doc.Application.ShortCurveTolerance)
                return Fail<DimensionResult>("首尾參考距離過短，無法建立尺寸線。");

            if (!req.Apply)
                return JsonConvert.SerializeObject(ApiResponse<DimensionResult>.Ok(result));

            var refArray = new ReferenceArray();
            foreach (var p in uniq) refArray.Append(p.Ref);

            using (var tx = new Transaction(_doc, "MCP 建立尺寸鏈"))
            {
                tx.Start();
                var line = Line.CreateBound(p1, p2);
                var dim  = dimType != null
                    ? _doc.Create.NewDimension(view, line, refArray, dimType)
                    : _doc.Create.NewDimension(view, line, refArray);
                tx.Commit();
                result.DimensionId = dim.Id.IntegerValue;
            }

            return JsonConvert.SerializeObject(ApiResponse<DimensionResult>.Ok(result));
        }

        // ─── 輔助 ──────────────────────────────────────────────────────────────
        private List<TagTarget> CollectTargets(TagElementsRequest req, View view, TagElementsResult result)
        {
            var targets = new List<TagTarget>();

            if (req.ElementIds != null && req.ElementIds.Count > 0)
            {
                var visible = new HashSet<int>(new FilteredElementCollector(_doc, view.Id)
                    .WhereElementIsNotElementType()
                    .ToElementIds().Select(i => i.IntegerValue));

                foreach (var id in req.ElementIds)
                {
                    var el = _doc.GetElement(new ElementId((int)id));
                    if (el == null)
                    { result.Skipped.Add(new SkippedDto { ElementId = id, Reason = "元件不存在" }); continue; }
                    if (!visible.Contains((int)id))
                    { result.Skipped.Add(new SkippedDto { ElementId = id, Reason = "這個視圖看不到該元件" }); continue; }
                    targets.Add(new TagTarget { El = el });
                }
                return targets;
            }

            var cats = (req.Categories != null && req.Categories.Count > 0)
                ? req.Categories
                : new List<string> { "PlumbingFixtures" };

            var bics = new List<BuiltInCategory>();
            foreach (var c in cats)
            {
                if (!TryGetBuiltInCategory(c, out var bic))
                { result.Skipped.Add(new SkippedDto { ElementId = 0, Reason = $"未知類別 '{c}'" }); continue; }
                bics.Add(bic);
            }

            // 本模型
            foreach (var bic in bics)
                foreach (var e in new FilteredElementCollector(_doc, view.Id)
                             .OfCategory(bic).WhereElementIsNotElementType().ToElements())
                    targets.Add(new TagTarget { El = e });

            if (!req.IncludeLinks) return targets;

            // 連結：用視圖的裁切框體積在連結座標系裡框出搜尋範圍
            var cb = view.CropBox;
            var hostPts = BoxCorners(cb.Min, cb.Max).Select(p => cb.Transform.OfPoint(p)).ToList();

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

                var xf    = li.GetTotalTransform();
                var xfInv = xf.Inverse;
                var pts   = hostPts.Select(p => xfInv.OfPoint(p)).ToList();
                var omin  = new XYZ(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z));
                var omax  = new XYZ(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z));
                if (omax.X - omin.X < 1e-6 || omax.Y - omin.Y < 1e-6 || omax.Z - omin.Z < 1e-6) continue;

                var bbFilter = new BoundingBoxIntersectsFilter(new Outline(omin, omax));

                foreach (var bic in bics)
                {
                    IList<Element> found;
                    try
                    {
                        found = new FilteredElementCollector(ldoc).OfCategory(bic)
                            .WhereElementIsNotElementType().WherePasses(bbFilter).ToElements();
                    }
                    catch (Exception ex)
                    {
                        result.Skipped.Add(new SkippedDto
                        { ElementId = li.Id.IntegerValue, Reason = $"{ldoc.Title}：搜尋失敗 {ex.Message}" });
                        continue;
                    }
                    foreach (var e in found)
                        targets.Add(new TagTarget
                        { El = e, Link = li, Xf = xf, Source = ldoc.Title });
                }
            }
            return targets;
        }

        /// <summary>
        /// 用種子元件（這張立面的裝修背牆）把範圍收斂到「這一間」：
        /// 水平方向要落在種子的左右範圍內，深度方向要在種子前方 maxDepthMm 以內。
        /// 立面的遠端裁剪常設得很深，不篩的話背景房間的設備也會被抓進來。
        /// </summary>
        private List<TagTarget> RestrictToRoom(List<TagTarget> targets, View view,
                                               TagElementsRequest req, TagElementsResult result)
        {
            var right = view.RightDirection;
            var dir   = view.ViewDirection;      // 由觀者指向畫面內
            double sMinX, sMaxX, nearD, farD;

            if (req.SeedElementId.HasValue)
            {
                var seed = _doc.GetElement(new ElementId((int)req.SeedElementId.Value));
                if (seed == null)
                {
                    result.Skipped.Add(new SkippedDto
                    { ElementId = req.SeedElementId.Value, Reason = "種子元件不存在，未套用房間範圍篩選" });
                    return targets;
                }
                var seedBb = seed.get_BoundingBox(view) ?? seed.get_BoundingBox(null);
                if (seedBb == null)
                {
                    result.Skipped.Add(new SkippedDto
                    { ElementId = req.SeedElementId.Value, Reason = "種子元件取不到範圍框，未套用房間範圍篩選" });
                    return targets;
                }

                sMinX = double.MaxValue; sMaxX = double.MinValue;
                double sDepth = double.MinValue;     // 背牆的深度位置（取最深的一面）
                foreach (var p in Corners(seedBb))
                {
                    double x = p.DotProduct(right);
                    double d = p.DotProduct(dir);
                    if (x < sMinX) sMinX = x;
                    if (x > sMaxX) sMaxX = x;
                    if (d > sDepth) sDepth = d;
                }
                nearD = sDepth - req.MaxDepthMm / FT;   // 房間前緣
                farD  = sDepth + 100.0 / FT;            // 背牆再往後 10cm
                result.Scope = $"種子元件 {req.SeedElementId.Value}（{SafeName(seed)}）";
            }
            else
            {
                // 沒指定種子就自己找出這張圖在畫的房間，用房間邊界當範圍。
                var hit = ViewRoomFinder.Find(_doc, view, req.RoomSearchDepthMm);
                if (hit == null)
                {
                    result.Skipped.Add(new SkippedDto
                    {
                        ElementId = 0,
                        Reason = "取樣不到這張圖在畫的房間，未套用房間範圍篩選；" +
                                 "可用 seedElementId 指定裝修背牆"
                    });
                    return targets;
                }

                sMinX = double.MaxValue; sMaxX = double.MinValue;
                double dMin = double.MaxValue, dMax = double.MinValue;
                foreach (var p in hit.Corners)
                {
                    double x = p.DotProduct(right);
                    double d = p.DotProduct(dir);
                    if (x < sMinX) sMinX = x;
                    if (x > sMaxX) sMaxX = x;
                    if (d < dMin) dMin = d;
                    if (d > dMax) dMax = d;
                }
                // 房間邊界是結構面，裝修面與設備會落在它前後一點點，前後各放 150mm。
                double tolD = 150.0 / FT;
                nearD = dMin - tolD;
                farD  = dMax + tolD;
                result.Scope = $"房間 {hit}"
                               + (hit.Alternatives.Count > 0
                                  ? $"；同點也打到 {string.Join("、", hit.Alternatives)}，已取面積最小者"
                                  : "");
            }

            double tolX = 50.0 / FT;                  // 水平容許 5cm
            _roomXMin = sMinX; _roomXMax = sMaxX; _roomSpanKnown = true;

            var kept = new List<TagTarget>();
            foreach (var t in targets)
            {
                var sp = SpanInView(t, view);
                if (sp == null)
                {
                    result.Skipped.Add(new SkippedDto
                    { ElementId = t.El.Id.IntegerValue, Reason = "取不到範圍框" });
                    continue;
                }
                var (eXMin, eXMax, eDMin, eDMax) = sp.Value;
                double cx = (eXMin + eXMax) * 0.5;
                double cd = (eDMin + eDMax) * 0.5;

                // 兩種都算「服務這一間」：設備落在房間裡（中心點在範圍內），
                // 或大面材料把整個房間蓋住（天花、地坪是整層一大片，中心點在別處）。
                bool inside = cx >= sMinX - tolX && cx <= sMaxX + tolX && cd >= nearD && cd <= farD;
                bool covers = eXMin <= sMinX + tolX && eXMax >= sMaxX - tolX
                              && eDMin <= nearD + tolX && eDMax >= farD - tolX;

                if (!inside && !covers)
                {
                    bool xOut = cx < sMinX - tolX || cx > sMaxX + tolX;
                    result.Skipped.Add(new SkippedDto
                    {
                        ElementId = t.El.Id.IntegerValue,
                        Reason = xOut
                            ? $"{SafeName(t.El)}：水平超出這一間的範圍，也沒有蓋住這一間"
                            : $"{SafeName(t.El)}：深度落在 {Math.Round(nearD * FT)}~{Math.Round(farD * FT)}mm 之外，屬於別間"
                    });
                    continue;
                }
                kept.Add(t);
            }
            return kept;
        }

        /// <summary>
        /// 引線分邊排版：以房間中線為界，靠左的設備標籤頭往左拉、靠右的往右拉，
        /// 頭排在房間邊界外 leaderMarginMm 的空白區，引線水平指回設備。
        /// 同一側的頭垂直太靠近就往上推開。頭會落在模型裁切框外，
        /// 所以裁切時要一併把 annotationCropMm 放大，否則 Revit 會把頭擠回框內。
        /// </summary>
        private Dictionary<long, XYZ> LayoutSideLeaders(List<TagTarget> targets, View view,
                                                        TagElementsRequest req)
        {
            var heads = new Dictionary<long, XYZ>();
            if (!_roomSpanKnown) return heads;

            var right = view.RightDirection;
            var up    = view.UpDirection;
            var dir   = view.ViewDirection;

            double margin  = req.LeaderMarginMm  / FT;
            double spacing = req.LeaderSpacingMm / FT;
            double xMid    = (_roomXMin + _roomXMax) * 0.5;

            var left  = new List<(TagTarget t, double y, double d)>();
            var rightS = new List<(TagTarget t, double y, double d)>();

            foreach (var t in targets)
            {
                var c = CenterInView(t, view);
                if (c == null) continue;
                double x = c.DotProduct(right);
                double y = c.DotProduct(up);
                double d = c.DotProduct(dir);
                if (x < xMid) left.Add((t, y, d));
                else          rightS.Add((t, y, d));
            }

            PlaceColumn(left,   _roomXMin - margin, spacing, right, up, dir, heads);
            PlaceColumn(rightS, _roomXMax + margin, spacing, right, up, dir, heads);
            return heads;
        }

        /// <summary>把一側的標籤頭排在同一條垂直線上，由下往上推開，避免互相重疊。</summary>
        private static void PlaceColumn(List<(TagTarget t, double y, double d)> items,
                                        double headX, double spacing,
                                        XYZ right, XYZ up, XYZ dir,
                                        Dictionary<long, XYZ> heads)
        {
            if (items.Count == 0) return;

            var sorted = items.OrderBy(i => i.y).ToList();
            double prev = double.MinValue;
            foreach (var it in sorted)
            {
                double y = it.y;
                if (prev != double.MinValue && y < prev + spacing) y = prev + spacing;
                prev = y;
                heads[it.t.El.Id.IntegerValue] =
                    right.Multiply(headX) + up.Multiply(y) + dir.Multiply(it.d);
            }
        }

        /// <summary>元件在視圖座標下的水平與深度範圍（已套用連結變換）。</summary>
        private static (double xMin, double xMax, double dMin, double dMax)? SpanInView(TagTarget t, View view)
        {
            var bb = t.El.get_BoundingBox(view) ?? t.El.get_BoundingBox(null);
            if (bb == null) return null;

            var right = view.RightDirection;
            var dir   = view.ViewDirection;
            double xMin = double.MaxValue, xMax = double.MinValue;
            double dMin = double.MaxValue, dMax = double.MinValue;

            foreach (var p0 in Corners(bb))
            {
                var p = t.Xf.OfPoint(p0);
                double x = p.DotProduct(right);
                double d = p.DotProduct(dir);
                if (x < xMin) xMin = x;
                if (x > xMax) xMax = x;
                if (d < dMin) dMin = d;
                if (d > dMax) dMax = d;
            }
            return (xMin, xMax, dMin, dMax);
        }

        /// <summary>
        /// 同一個編號只放一個標籤 —— A9-13 的材料標籤是「牆一個、地一個、天花一個」，
        /// 不是每道牆各一個。同編號的取最靠背牆（視線最深）的那個元件。
        /// </summary>
        private static List<TagTarget> DedupeByMark(List<TagTarget> targets, View view,
                                                    TagElementsResult result)
        {
            var dir = view.ViewDirection;
            var best = new Dictionary<string, (TagTarget t, double depth)>();
            var order = new List<string>();

            foreach (var t in targets)
            {
                string mark = MarkOf(t.El);
                if (string.IsNullOrWhiteSpace(mark)) mark = $"#{t.El.Id.IntegerValue}";

                var sp = SpanInView(t, view);
                double depth = sp.HasValue ? (sp.Value.dMin + sp.Value.dMax) * 0.5 : double.MinValue;

                if (!best.TryGetValue(mark, out var cur)) { best[mark] = (t, depth); order.Add(mark); }
                else if (depth > cur.depth)
                {
                    result.Skipped.Add(new SkippedDto
                    { ElementId = cur.t.El.Id.IntegerValue, Reason = $"{mark}：同編號已有更靠背牆的元件，只放一個" });
                    best[mark] = (t, depth);
                }
                else
                {
                    result.Skipped.Add(new SkippedDto
                    { ElementId = t.El.Id.IntegerValue, Reason = $"{mark}：同編號只放一個，已取更靠背牆的那個" });
                }
            }
            return order.Select(m => best[m].t).ToList();
        }

        /// <summary>依類型標記的格式篩選。材料是「字母+數字-數字」（W3-11），設備不是（D9、B2+C2）。</summary>
        private static List<TagTarget> FilterByMark(List<TagTarget> targets, TagElementsRequest req,
                                                    TagElementsResult result)
        {
            Regex? inc = string.IsNullOrWhiteSpace(req.MarkInclude) ? null
                       : new Regex(req.MarkInclude, RegexOptions.IgnoreCase);
            Regex? exc = string.IsNullOrWhiteSpace(req.MarkExclude) ? null
                       : new Regex(req.MarkExclude, RegexOptions.IgnoreCase);
            if (inc == null && exc == null) return targets;

            var kept = new List<TagTarget>();
            foreach (var t in targets)
            {
                string mark = MarkOf(t.El);
                if (inc != null && !inc.IsMatch(mark))
                {
                    result.Skipped.Add(new SkippedDto
                    { ElementId = t.El.Id.IntegerValue, Reason = $"編號「{mark}」不符 markInclude" });
                    continue;
                }
                if (exc != null && exc.IsMatch(mark))
                {
                    result.Skipped.Add(new SkippedDto
                    { ElementId = t.El.Id.IntegerValue, Reason = $"編號「{mark}」符合 markExclude，屬於另一種標籤" });
                    continue;
                }
                kept.Add(t);
            }
            return kept;
        }

        /// <summary>類型標記：先看實例參數，沒有再看類型參數。</summary>
        private static string MarkOf(Element e)
        {
            var p = e.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
            var s = p?.AsString();
            if (!string.IsNullOrWhiteSpace(s)) return s!.Trim();

            var te = e.Document.GetElement(e.GetTypeId());
            var tp = te?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
            return tp?.AsString()?.Trim() ?? "";
        }

        /// <summary>類型標記是空的就不放 —— 空白菱形對圖面沒有意義，列進 skipped 讓使用者去補資料。</summary>
        private static List<TagTarget> RemoveBlankMark(List<TagTarget> targets, TagElementsResult result)
        {
            var kept = new List<TagTarget>();
            foreach (var t in targets)
            {
                string mark = "";
                try
                {
                    var p = t.El.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
                    if (p == null)
                    {
                        var ty = t.El.Document.GetElement(t.El.GetTypeId());
                        p = ty?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
                    }
                    mark = p?.AsString() ?? "";
                }
                catch { }

                if (string.IsNullOrWhiteSpace(mark))
                    result.Skipped.Add(new SkippedDto
                    {
                        ElementId = t.El.Id.IntegerValue,
                        Reason = $"{SafeName(t.El)}：類型標記是空的，需要先在族群類型補編號"
                    });
                else kept.Add(t);
            }
            return kept;
        }

        private List<TagTarget> RemoveAlreadyTagged(List<TagTarget> targets, View view, TagElementsResult result)
        {
            // key = "連結實體id:元件id"，本模型的連結實體 id 記 0
            var tagged = new HashSet<string>();
            foreach (var t in new FilteredElementCollector(_doc, view.Id)
                         .OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                try
                {
                    foreach (var r in t.GetTaggedReferences())
                    {
                        if (r.LinkedElementId != null && r.LinkedElementId != ElementId.InvalidElementId)
                            tagged.Add($"{r.ElementId.IntegerValue}:{r.LinkedElementId.IntegerValue}");
                        else
                            tagged.Add($"0:{r.ElementId.IntegerValue}");
                    }
                }
                catch { /* 讀不到就略過，最多重複標一次 */ }
            }

            var kept = new List<TagTarget>();
            foreach (var t in targets)
            {
                var key = $"{(t.Link != null ? t.Link.Id.IntegerValue : 0)}:{t.El.Id.IntegerValue}";
                if (tagged.Contains(key))
                    result.Skipped.Add(new SkippedDto
                    { ElementId = t.El.Id.IntegerValue, Reason = $"已有標籤（{t.Source}）" });
                else kept.Add(t);
            }
            return kept;
        }

        /// <summary>對象在主模型座標系裡的中心點。連結元件會先套上連結的轉換。</summary>
        private static XYZ? CenterInView(TagTarget t, View view)
        {
            if (t.Link == null)
            {
                var bb0 = t.El.get_BoundingBox(view) ?? t.El.get_BoundingBox(null);
                if (bb0 == null) return null;
                return (bb0.Min + bb0.Max) * 0.5;
            }

            var bb = t.El.get_BoundingBox(null);
            if (bb == null) return null;

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var p in Corners(bb))
            {
                var w = t.Xf.OfPoint(p);
                if (w.X < minX) minX = w.X;
                if (w.Y < minY) minY = w.Y;
                if (w.Z < minZ) minZ = w.Z;
                if (w.X > maxX) maxX = w.X;
                if (w.Y > maxY) maxY = w.Y;
                if (w.Z > maxZ) maxZ = w.Z;
            }
            return new XYZ((minX + maxX) * 0.5, (minY + maxY) * 0.5, (minZ + maxZ) * 0.5);
        }

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

        private static string SafeName(Element e)
        {
            try { return e.Name ?? ""; } catch { return ""; }
        }

        private static string Fail<T>(string msg) =>
            JsonConvert.SerializeObject(ApiResponse<T>.Fail(msg));

        /// <summary>
        /// 取元件幾何中與尺寸方向垂直的平面（法線平行 dimDir），
        /// 只保留最外側兩面（最小、最大），避免一個元件貢獻太多參考點。
        /// </summary>
        private static List<RefPick> PlanarFaceRefs(Element e, Options opt, XYZ dimDir)
        {
            var found = new List<RefPick>();
            var ge = e.get_Geometry(opt);
            if (ge == null) return found;

            void Walk(GeometryElement g)
            {
                foreach (GeometryObject go in g)
                {
                    if (go is GeometryInstance gi)
                    {
                        var inner = gi.GetInstanceGeometry();
                        if (inner != null) Walk(inner);
                        continue;
                    }
                    if (go is Solid solid && solid.Faces.Size > 0)
                    {
                        foreach (Face f in solid.Faces)
                        {
                            if (f is not PlanarFace pf) continue;
                            if (f.Reference == null) continue;
                            if (Math.Abs(pf.FaceNormal.DotProduct(dimDir)) < 0.99) continue;
                            found.Add(new RefPick
                            { Ref = f.Reference, Pos = pf.Origin.DotProduct(dimDir), Owner = e });
                        }
                    }
                }
            }

            Walk(ge);
            if (found.Count > 2)
            {
                found.Sort((a, b) => a.Pos.CompareTo(b.Pos));
                found = new List<RefPick> { found[0], found[found.Count - 1] };
            }
            return found;
        }

        /// <summary>
        /// 幾何面抓不到時的退路：用族群實體本身的中心參考平面。
        /// 位置以實體範圍框中心投影估算，只用來決定排序。
        /// </summary>
        private static List<RefPick> FamilyCenterRef(Element e, XYZ dimDir)
        {
            var found = new List<RefPick>();
            if (e is not FamilyInstance fi) return found;

            var wanted = Math.Abs(dimDir.Z) > 0.7
                ? new[] { FamilyInstanceReferenceType.CenterElevation }
                : new[] { FamilyInstanceReferenceType.CenterLeftRight,
                          FamilyInstanceReferenceType.CenterFrontBack };

            foreach (var t in wanted)
            {
                IList<Reference> rs;
                try { rs = fi.GetReferences(t); } catch { continue; }
                if (rs == null || rs.Count == 0) continue;

                var bb = fi.get_BoundingBox(null);
                double pos = bb != null ? ((bb.Min + bb.Max) * 0.5).DotProduct(dimDir) : 0;
                found.Add(new RefPick { Ref = rs[0], Pos = pos, Owner = e });
                break;
            }
            return found;
        }

        private static bool TryGetBuiltInCategory(string name, out BuiltInCategory bic)
        {
            var map = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
            {
                ["PlumbingFixtures"]    = BuiltInCategory.OST_PlumbingFixtures,
                ["SpecialityEquipment"] = BuiltInCategory.OST_SpecialityEquipment,
                ["MechanicalEquipment"] = BuiltInCategory.OST_MechanicalEquipment,
                ["ElectricalFixtures"]  = BuiltInCategory.OST_ElectricalFixtures,
                ["LightingFixtures"]    = BuiltInCategory.OST_LightingFixtures,
                ["GenericModel"]        = BuiltInCategory.OST_GenericModel,
                ["Casework"]            = BuiltInCategory.OST_Casework,
                ["Furniture"]           = BuiltInCategory.OST_Furniture,
                ["Doors"]               = BuiltInCategory.OST_Doors,
                ["Windows"]             = BuiltInCategory.OST_Windows,
                ["Walls"]               = BuiltInCategory.OST_Walls,
                ["Ceilings"]            = BuiltInCategory.OST_Ceilings,
                ["Floors"]              = BuiltInCategory.OST_Floors,
                ["Rooms"]               = BuiltInCategory.OST_Rooms,
                ["CurtainWallPanels"]   = BuiltInCategory.OST_CurtainWallPanels,
            };
            return map.TryGetValue(name, out bic);
        }
    }
}
