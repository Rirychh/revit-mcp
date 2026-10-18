// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMcpAddin.Models;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 建立製圖視圖，並在裡面畫框線與文字 —— 拿來做索引表／位置配置圖這種
    /// 「不是從模型長出來、但要放在圖紙上」的圖。
    ///
    /// 座標一律用「圖面上的 mm」：處理時會乘上視圖比例換成模型單位，
    /// 所以同一份版面配置換比例印出來大小一樣。
    /// </summary>
    public class DraftingHandler
    {
        private const double FT = 304.8;
        private readonly Document _doc;

        public DraftingHandler(Document doc) => _doc = doc;

        // ── 可用的類型：製圖視圖族群類型、文字類型、線型式 ─────────────────
        public string ListTypes(NameValueCollection qs)
        {
            var res = new DraftingTypesResult();

            foreach (var vft in new FilteredElementCollector(_doc)
                         .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                         .Where(v => v.ViewFamily == ViewFamily.Drafting))
                res.ViewFamilyTypes.Add(new NamedIdDto { Id = vft.Id.IntegerValue, Name = vft.Name });

            foreach (var tt in new FilteredElementCollector(_doc)
                         .OfClass(typeof(TextNoteType)).Cast<TextNoteType>())
                res.TextNoteTypes.Add(new NamedIdDto { Id = tt.Id.IntegerValue, Name = tt.Name });

            var lineCat = _doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            if (lineCat != null)
                foreach (Category sub in lineCat.SubCategories)
                    res.LineStyles.Add(new NamedIdDto { Id = sub.Id.IntegerValue, Name = sub.Name });

            res.ViewFamilyTypes = res.ViewFamilyTypes.OrderBy(x => x.Name).ToList();
            res.TextNoteTypes   = res.TextNoteTypes.OrderBy(x => x.Name).ToList();
            res.LineStyles      = res.LineStyles.OrderBy(x => x.Name).ToList();
            return JsonConvert.SerializeObject(ApiResponse<DraftingTypesResult>.Ok(res));
        }

        // ── 建立製圖視圖 ─────────────────────────────────────────────────
        public string CreateDraftingView(string body)
        {
            var req = JsonConvert.DeserializeObject<CreateDraftingViewRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");
            if (string.IsNullOrWhiteSpace(req.Name))
                return Fail<CreateDraftingViewResult>("需要 name。");

            var vft = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .Where(v => v.ViewFamily == ViewFamily.Drafting)
                .FirstOrDefault(v => string.IsNullOrEmpty(req.ViewFamilyTypeName) || v.Name == req.ViewFamilyTypeName);
            if (vft == null)
                return Fail<CreateDraftingViewResult>("找不到製圖視圖的視圖族群類型。用 GET /drafting/types 查。");

            var existing = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewDrafting)).Cast<ViewDrafting>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == req.Name);
            if (existing != null)
                return JsonConvert.SerializeObject(ApiResponse<CreateDraftingViewResult>.Ok(
                    new CreateDraftingViewResult
                    {
                        ViewId = existing.Id.IntegerValue, ViewName = existing.Name,
                        Scale = existing.Scale, AlreadyExisted = true
                    }));

            if (!req.Apply)
                return JsonConvert.SerializeObject(ApiResponse<CreateDraftingViewResult>.Ok(
                    new CreateDraftingViewResult { ViewId = 0, ViewName = req.Name, Scale = req.Scale }));

            ViewDrafting view;
            using (var tx = new Transaction(_doc, "MCP 建立製圖視圖"))
            {
                tx.Start();
                view = ViewDrafting.Create(_doc, vft.Id);
                try { view.Name = req.Name; } catch { }
                try { view.Scale = req.Scale; } catch { }
                tx.Commit();
            }
            return JsonConvert.SerializeObject(ApiResponse<CreateDraftingViewResult>.Ok(
                new CreateDraftingViewResult
                { ViewId = view.Id.IntegerValue, ViewName = view.Name, Scale = view.Scale }));
        }

        // ── 改製圖視圖的比例／名稱 ────────────────────────────────────────
        public string UpdateDraftingView(string body)
        {
            var req = JsonConvert.DeserializeObject<UpdateDraftingViewRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");

            if (_doc.GetElement(new ElementId((int)req.ViewId)) is not View view)
                return Fail<CreateDraftingViewResult>($"View {req.ViewId} not found.");

            using (var tx = new Transaction(_doc, "MCP 修改視圖"))
            {
                tx.Start();
                if (req.Scale.HasValue)
                {
                    try { view.Scale = req.Scale.Value; }
                    catch (Exception ex) { tx.RollBack(); return Fail<CreateDraftingViewResult>("比例設定失敗：" + ex.Message); }
                }
                if (!string.IsNullOrWhiteSpace(req.Name))
                {
                    try { view.Name = req.Name!; }
                    catch (Exception ex) { tx.RollBack(); return Fail<CreateDraftingViewResult>("改名失敗：" + ex.Message); }
                }
                tx.Commit();
            }
            return JsonConvert.SerializeObject(ApiResponse<CreateDraftingViewResult>.Ok(
                new CreateDraftingViewResult
                { ViewId = view.Id.IntegerValue, ViewName = view.Name, Scale = view.Scale, AlreadyExisted = true }));
        }

        // ── 依 id 刪除元素（比整個視圖清空安全） ──────────────────────────
        public string DeleteElements(string body)
        {
            var req = JsonConvert.DeserializeObject<DeleteElementsRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");

            var res = new DeleteElementsResult { Applied = req.Apply };
            var ids = new List<ElementId>();
            foreach (var id in req.ElementIds)
            {
                var e = _doc.GetElement(new ElementId((int)id));
                if (e == null) { res.Failed.Add($"[{id}] 不存在"); continue; }
                ids.Add(e.Id);
                res.Matched.Add($"[{id}] {e.Category?.Name} {e.Name}");
            }
            if (!req.Apply) return JsonConvert.SerializeObject(ApiResponse<DeleteElementsResult>.Ok(res));

            using (var tx = new Transaction(_doc, "MCP 刪除元素"))
            {
                tx.Start();
                foreach (var id in ids)
                {
                    try { _doc.Delete(id); res.Deleted++; }
                    catch (Exception ex) { res.Failed.Add($"[{id.IntegerValue}] {ex.Message}"); }
                }
                tx.Commit();
            }
            return JsonConvert.SerializeObject(ApiResponse<DeleteElementsResult>.Ok(res));
        }

        // ── 在視圖裡畫框線與文字 ──────────────────────────────────────────
        public string AddContent(string body)
        {
            var req = JsonConvert.DeserializeObject<DraftingContentRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");

            if (_doc.GetElement(new ElementId((int)req.ViewId)) is not View view)
                return Fail<DraftingContentResult>($"View {req.ViewId} not found.");

            var res = new DraftingContentResult
            { ViewId = req.ViewId, ViewName = view.Name, Applied = req.Apply };

            // 圖面 mm → 模型單位：乘上視圖比例，再除以 304.8
            double k = view.Scale / FT;

            var textType = new FilteredElementCollector(_doc)
                .OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
                .FirstOrDefault(t => string.IsNullOrEmpty(req.TextTypeName) || t.Name == req.TextTypeName);
            if (textType == null && (req.Texts?.Count ?? 0) > 0)
                return Fail<DraftingContentResult>("找不到文字類型。用 GET /drafting/types 查。");
            res.TextTypeUsed = textType?.Name ?? "";

            GraphicsStyle? style = null;
            if (!string.IsNullOrEmpty(req.LineStyleName))
            {
                var lineCat = _doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                foreach (Category sub in lineCat.SubCategories)
                    if (sub.Name == req.LineStyleName)
                        style = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                if (style == null)
                    return Fail<DraftingContentResult>($"找不到線型式「{req.LineStyleName}」。");
            }

            var curves = new List<Line>();
            foreach (var b in req.Boxes ?? new List<BoxDto>())
            {
                double x0 = b.X * k, y0 = b.Y * k, x1 = (b.X + b.W) * k, y1 = (b.Y + b.H) * k;
                if (Math.Abs(x1 - x0) < 1e-9 || Math.Abs(y1 - y0) < 1e-9)
                { res.Skipped.Add($"框 ({b.X},{b.Y}) 寬或高為零"); continue; }
                curves.Add(Line.CreateBound(new XYZ(x0, y0, 0), new XYZ(x1, y0, 0)));
                curves.Add(Line.CreateBound(new XYZ(x1, y0, 0), new XYZ(x1, y1, 0)));
                curves.Add(Line.CreateBound(new XYZ(x1, y1, 0), new XYZ(x0, y1, 0)));
                curves.Add(Line.CreateBound(new XYZ(x0, y1, 0), new XYZ(x0, y0, 0)));
            }
            foreach (var l in req.Lines ?? new List<LineDto>())
            {
                var a = new XYZ(l.X1 * k, l.Y1 * k, 0);
                var c = new XYZ(l.X2 * k, l.Y2 * k, 0);
                if (a.DistanceTo(c) < 1e-9) { res.Skipped.Add($"線段 ({l.X1},{l.Y1}) 長度為零"); continue; }
                curves.Add(Line.CreateBound(a, c));
            }

            res.PlannedCurves = curves.Count;
            res.PlannedTexts  = req.Texts?.Count ?? 0;
            if (!req.Apply) return JsonConvert.SerializeObject(ApiResponse<DraftingContentResult>.Ok(res));

            using (var tx = new Transaction(_doc, "MCP 製圖視圖內容"))
            {
                tx.Start();
                foreach (var c in curves)
                {
                    try
                    {
                        var dc = _doc.Create.NewDetailCurve(view, c);
                        if (style != null) dc.LineStyle = style;
                        res.CurvesCreated++;
                    }
                    catch (Exception ex) { res.Skipped.Add("線段：" + ex.Message); }
                }

                foreach (var t in req.Texts ?? new List<TextDto>())
                {
                    try
                    {
                        // 字級是類型屬性，所以每段文字可以指定自己的類型來換大小
                        var tt = textType;
                        if (!string.IsNullOrWhiteSpace(t.TextTypeName))
                        {
                            tt = new FilteredElementCollector(_doc)
                                .OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
                                .FirstOrDefault(x => x.Name == t.TextTypeName);
                            if (tt == null)
                            { res.Skipped.Add($"文字「{t.Text}」：找不到類型「{t.TextTypeName}」"); continue; }
                        }

                        var opt = new TextNoteOptions(tt!.Id)
                        {
                            HorizontalAlignment = t.Align.Equals("center", StringComparison.OrdinalIgnoreCase)
                                ? HorizontalTextAlignment.Center
                                : t.Align.Equals("right", StringComparison.OrdinalIgnoreCase)
                                    ? HorizontalTextAlignment.Right
                                    : HorizontalTextAlignment.Left
                        };

                        var pt = new XYZ(t.X * k, t.Y * k, 0);
                        var note = t.WidthMm.HasValue && t.WidthMm.Value > 0
                            ? TextNote.Create(_doc, view.Id, pt, t.WidthMm.Value * k, t.Text ?? "", opt)
                            : TextNote.Create(_doc, view.Id, pt, t.Text ?? "", opt);

                        try
                        {
                            note.VerticalAlignment = t.VAlign.Equals("middle", StringComparison.OrdinalIgnoreCase)
                                ? VerticalTextAlignment.Middle
                                : t.VAlign.Equals("bottom", StringComparison.OrdinalIgnoreCase)
                                    ? VerticalTextAlignment.Bottom
                                    : VerticalTextAlignment.Top;
                        }
                        catch { }

                        if (t.LeaderX.HasValue && t.LeaderY.HasValue)
                        {
                            bool toLeft = t.LeaderSide.Equals("left", StringComparison.OrdinalIgnoreCase)
                                || (t.LeaderSide.Equals("auto", StringComparison.OrdinalIgnoreCase)
                                    && t.LeaderX.Value < t.X);
                            var lt = t.LeaderArc
                                ? (toLeft ? TextNoteLeaderTypes.TNLT_ARC_L      : TextNoteLeaderTypes.TNLT_ARC_R)
                                : (toLeft ? TextNoteLeaderTypes.TNLT_STRAIGHT_L : TextNoteLeaderTypes.TNLT_STRAIGHT_R);

                            var ld = note.AddLeader(lt);
                            ld.End = new XYZ(t.LeaderX.Value * k, t.LeaderY.Value * k, 0);
                            if (t.LeaderElbowX.HasValue && t.LeaderElbowY.HasValue)
                                ld.Elbow = new XYZ(t.LeaderElbowX.Value * k, t.LeaderElbowY.Value * k, 0);
                            res.LeadersCreated++;
                        }

                        if (Math.Abs(t.RotationDeg) > 1e-9)
                        {
                            try
                            {
                                var ax = Line.CreateBound(pt, pt + XYZ.BasisZ);
                                ElementTransformUtils.RotateElement(_doc, note.Id, ax, t.RotationDeg * Math.PI / 180.0);
                            }
                            catch (Exception rex) { res.Skipped.Add("旋轉:" + rex.Message); }
                        }

                        res.TextsCreated++;
                    }
                    catch (Exception ex) { res.Skipped.Add($"文字「{t.Text}」：" + ex.Message); }
                }
                tx.Commit();
            }
            return JsonConvert.SerializeObject(ApiResponse<DraftingContentResult>.Ok(res));
        }

        /// <summary>清掉視圖裡的細部線與文字，方便重畫。</summary>
        public string ClearContent(string body)
        {
            var req = JsonConvert.DeserializeObject<DraftingContentRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");
            if (_doc.GetElement(new ElementId((int)req.ViewId)) is not View view)
                return Fail<DraftingContentResult>($"View {req.ViewId} not found.");

            var res = new DraftingContentResult
            { ViewId = req.ViewId, ViewName = view.Name, Applied = req.Apply };

            var ids = new List<ElementId>();
            ids.AddRange(new FilteredElementCollector(_doc, view.Id)
                .OfClass(typeof(CurveElement)).ToElementIds());
            ids.AddRange(new FilteredElementCollector(_doc, view.Id)
                .OfClass(typeof(TextNote)).ToElementIds());

            res.PlannedCurves = ids.Count;
            if (!req.Apply) return JsonConvert.SerializeObject(ApiResponse<DraftingContentResult>.Ok(res));

            using (var tx = new Transaction(_doc, "MCP 清空製圖視圖"))
            {
                tx.Start();
                foreach (var id in ids)
                {
                    try { _doc.Delete(id); res.CurvesCreated++; }
                    catch (Exception ex) { res.Skipped.Add(ex.Message); }
                }
                tx.Commit();
            }
            return JsonConvert.SerializeObject(ApiResponse<DraftingContentResult>.Ok(res));
        }

        private static string Fail<T>(string msg) =>
            JsonConvert.SerializeObject(ApiResponse<T>.Fail(msg));
    }
}
