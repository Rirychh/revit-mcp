// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMcpAddin.Models;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 跨樓層／區域複製天花設備。
    /// 面掛族群（HostFace 不為 null，例如天花上的平板燈、灑水頭）不能直接位移複製，
    /// 會失去主體；這裡改成在目標層天花底面上重新放置，再把旋轉與自訂參數搬過去。
    /// 其餘（樓層式）元件走 ElementTransformUtils 位移複製。
    /// </summary>
    public class CopyHandler
    {
        private readonly Document _doc;
        public CopyHandler(Document doc) => _doc = doc;

        private const double MM = 304.8;

        public string CopyElements(string body)
        {
            var req = JsonConvert.DeserializeObject<CopyElementsRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");

            var sources = ResolveSources(req);
            var targetLevel = string.IsNullOrEmpty(req.TargetLevelName) ? null : FindLevel(req.TargetLevelName);

            var result = new CopyElementsResult
            {
                DryRun      = req.DryRun,
                SourceCount = sources.Count,
                SourceLevel = req.SourceLevelName ?? "",
                TargetLevel = targetLevel?.Name ?? ""
            };

            var pts = sources.Select(ElementHandler.GetPoint).Where(p => p != null).Cast<XYZ>().ToList();
            if (pts.Count > 0)
                result.SourceBBox = new BBoxDto
                {
                    MinX = pts.Min(p => p.X) * MM, MinY = pts.Min(p => p.Y) * MM,
                    MaxX = pts.Max(p => p.X) * MM, MaxY = pts.Max(p => p.Y) * MM
                };

            result.Groups = sources.OfType<FamilyInstance>()
                .GroupBy(f => new { f.Symbol.FamilyName, TypeName = f.Symbol.Name, Cat = f.Category?.Name ?? "" })
                .Select(g => new CopyGroupDto
                {
                    FamilyName = g.Key.FamilyName, TypeName = g.Key.TypeName, Category = g.Key.Cat,
                    Count = g.Count(), FaceHosted = g.Count(IsFaceHosted)
                })
                .OrderByDescending(g => g.Count).ToList();

            if (targetLevel == null)
            {
                result.Warnings.Add("未指定或找不到 targetLevelName，只回報來源統計。");
                return Json(result);
            }

            var ceilings = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Ceilings)
                .WhereElementIsNotElementType()
                .Where(c => c.LevelId == targetLevel.Id)
                .ToList();
            result.TargetCeilings = ceilings.Count;

            if (req.DryRun) return Json(result);
            if (sources.Count == 0) { result.Warnings.Add("來源為空，未執行。"); return Json(result); }

            Execute(req, sources, targetLevel, ceilings, result);
            return Json(result);
        }

        // ── 實際執行 ──────────────────────────────────────────────────────
        private void Execute(CopyElementsRequest req, List<Element> sources, Level targetLevel,
                             List<Element> ceilings, CopyElementsResult result)
        {
            double dx = req.Dx / MM, dy = req.Dy / MM;
            double srcElev = SourceElevation(req, sources);
            double dz = targetLevel.Elevation - srcElev;

            var faces = req.RehostToCeiling ? BuildCeilingFaces(ceilings) : new List<FaceRef>();
            if (req.RehostToCeiling && faces.Count == 0)
                result.Warnings.Add("目標層找不到可掛載的天花底面，面掛族群會被略過。");

            var plainIds = new List<ElementId>();

            var swallow = new SwallowWarnings();
            using var tx = new Transaction(_doc, "MCP 複製天花設備");
            tx.Start();
            var fho = tx.GetFailureHandlingOptions();
            fho.SetFailuresPreprocessor(swallow);
            fho.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(fho);

            foreach (var e in sources)
            {
                var fi = e as FamilyInstance;
                if (req.RehostToCeiling && fi != null && IsFaceHosted(fi))
                {
                    if (!TryPlaceOnCeiling(fi, dx, dy, faces, result))
                        result.SkippedCount++;
                }
                else
                {
                    plainIds.Add(e.Id);
                }
            }

            if (plainIds.Count > 0)
            {
                try
                {
                    var newIds = ElementTransformUtils.CopyElements(_doc, plainIds, new XYZ(dx, dy, dz));
                    foreach (var id in newIds)
                    {
                        result.CreatedIds.Add(id.IntegerValue);
                        RetargetLevel(_doc.GetElement(id), targetLevel);
                    }
                    result.CreatedCount += newIds.Count;
                }
                catch (Exception ex)
                {
                    result.SkippedCount += plainIds.Count;
                    result.Warnings.Add("位移複製失敗：" + ex.Message);
                }
            }

            tx.Commit();

            foreach (var s in swallow.Swallowed.Distinct().Take(10))
                result.Warnings.Add("[已吞警告] " + s);
        }

        // ── 面掛族群：在目標天花底面重新放置 ──────────────────────────────
        private bool TryPlaceOnCeiling(FamilyInstance src, double dx, double dy,
                                       List<FaceRef> faces, CopyElementsResult result)
        {
            var p = ElementHandler.GetPoint(src);
            if (p == null) { result.Warnings.Add($"{src.Id.IntegerValue} 無放置點"); return false; }

            double tx = p.X + dx, ty = p.Y + dy;
            var hit = FindFaceAt(faces, tx, ty);
            if (hit == null)
            {
                result.Warnings.Add($"{src.Id.IntegerValue}（{src.Symbol.FamilyName}）目標位置無天花面");
                return false;
            }

            try
            {
                var symbol = src.Symbol;
                if (!symbol.IsActive) symbol.Activate();

                var inst = _doc.Create.NewFamilyInstance(
                    hit.Value.Reference, hit.Value.Point, XYZ.BasisX, symbol);

                MatchRotation(src, inst);
                CopyCustomParams(src, inst);

                result.CreatedIds.Add(inst.Id.IntegerValue);
                result.CreatedCount++;
                return true;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{src.Id.IntegerValue} 放置失敗：{ex.Message}");
                return false;
            }
        }

        private void MatchRotation(FamilyInstance src, FamilyInstance dst)
        {
            if (src.Location is not LocationPoint slp || dst.Location is not LocationPoint dlp) return;
            double delta = slp.Rotation - dlp.Rotation;
            if (Math.Abs(delta) < 1e-9) return;
            var axis = Line.CreateBound(dlp.Point, dlp.Point + XYZ.BasisZ);
            try { ElementTransformUtils.RotateElement(_doc, dst.Id, axis, delta); } catch { }
        }

        /// <summary>只搬非內建參數（專案／共用參數），避開樓層、主體、標記、階段等會打架的內建欄位。</summary>
        private static void CopyCustomParams(Element src, Element dst)
        {
            foreach (Parameter sp in src.Parameters)
            {
                if (!sp.HasValue || sp.IsReadOnly) continue;
                if (sp.Definition is not InternalDefinition idef) continue;
                if (idef.BuiltInParameter != BuiltInParameter.INVALID) continue;

                var dp = dst.LookupParameter(sp.Definition.Name);
                if (dp == null || dp.IsReadOnly || dp.StorageType != sp.StorageType) continue;

                try
                {
                    switch (sp.StorageType)
                    {
                        case StorageType.String:  dp.Set(sp.AsString() ?? ""); break;
                        case StorageType.Double:  dp.Set(sp.AsDouble());       break;
                        case StorageType.Integer: dp.Set(sp.AsInteger());      break;
                        case StorageType.ElementId: dp.Set(sp.AsElementId());  break;
                    }
                }
                catch { }
            }
        }

        // ── 天花底面蒐集 ──────────────────────────────────────────────────
        private struct FaceRef
        {
            public Reference Reference;
            public PlanarFace Face;
            public XYZ Point;
        }

        private static List<FaceRef> BuildCeilingFaces(List<Element> ceilings)
        {
            var opt = new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Medium
            };

            var list = new List<FaceRef>();
            foreach (var c in ceilings)
            {
                GeometryElement? geo = null;
                try { geo = c.get_Geometry(opt); } catch { }
                if (geo == null) continue;

                foreach (var go in geo)
                {
                    if (go is not Solid s || s.Faces.Size == 0) continue;
                    foreach (Face f in s.Faces)
                    {
                        if (f is PlanarFace pf && pf.FaceNormal.Z < -0.99 && pf.Reference != null)
                            list.Add(new FaceRef { Reference = pf.Reference, Face = pf, Point = XYZ.Zero });
                    }
                }
            }
            return list;
        }

        /// <summary>找出 (x,y) 正上方的天花底面，回傳可用的掛載參考與落點。</summary>
        private static FaceRef? FindFaceAt(List<FaceRef> faces, double x, double y)
        {
            foreach (var fr in faces)
            {
                var pf = fr.Face;
                var probe = new XYZ(x, y, pf.Origin.Z);
                IntersectionResult? res = null;
                try { res = pf.Project(probe); } catch { }
                if (res == null) continue;
                if (!pf.IsInside(res.UVPoint)) continue;

                return new FaceRef { Reference = fr.Reference, Face = pf, Point = res.XYZPoint };
            }
            return null;
        }

        // ── 來源解析 ──────────────────────────────────────────────────────
        private List<Element> ResolveSources(CopyElementsRequest req)
        {
            if (req.SourceIds != null && req.SourceIds.Count > 0)
                return req.SourceIds
                    .Select(id => _doc.GetElement(new ElementId((int)id)))
                    .Where(e => e != null).ToList()!;

            var collector = (req.SourceViewId.HasValue && req.SourceViewId.Value > 0)
                ? new FilteredElementCollector(_doc, new ElementId((int)req.SourceViewId.Value))
                      .WhereElementIsNotElementType()
                : new FilteredElementCollector(_doc).WhereElementIsNotElementType();

            var bics = (req.Categories ?? new List<string>())
                .Select(n => ElementHandler.TryCategory(n, out var b) ? (BuiltInCategory?)b : null)
                .Where(b => b.HasValue).Select(b => b!.Value).ToList();
            if (bics.Count == 1) collector = collector.OfCategory(bics[0]);
            else if (bics.Count > 1) collector = collector.WherePasses(new ElementMulticategoryFilter(bics));

            var fams = req.FamilyNames ?? new List<string>();

            return collector.Cast<Element>()
                .Where(e => string.IsNullOrEmpty(req.SourceLevelName) || LevelNameOf(e) == req.SourceLevelName)
                .Where(e => fams.Count == 0 || (e is FamilyInstance f && fams.Contains(f.Symbol.FamilyName)))
                .Where(e => req.BBox == null || ElementHandler.InBBox(ElementHandler.GetPoint(e), req.BBox))
                .ToList();
        }

        private double SourceElevation(CopyElementsRequest req, List<Element> sources)
        {
            if (!string.IsNullOrEmpty(req.SourceLevelName))
            {
                var lv = FindLevel(req.SourceLevelName!);
                if (lv != null) return lv.Elevation;
            }
            foreach (var e in sources)
            {
                if (e.LevelId != ElementId.InvalidElementId &&
                    _doc.GetElement(e.LevelId) is Level lv2) return lv2.Elevation;
            }
            return 0;
        }

        private Level? FindLevel(string name) => new FilteredElementCollector(_doc)
            .OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == name);

        private static void RetargetLevel(Element? e, Level target)
        {
            if (e == null) return;
            foreach (var bip in new[] { BuiltInParameter.FAMILY_LEVEL_PARAM,
                                        BuiltInParameter.SCHEDULE_LEVEL_PARAM,
                                        BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM })
            {
                var p = e.get_Parameter(bip);
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.ElementId)
                {
                    try { p.Set(target.Id); return; } catch { }
                }
            }
        }

        private static string LevelNameOf(Element e)
        {
            if (e.LevelId == ElementId.InvalidElementId) return "";
            return e.Document.GetElement(e.LevelId)?.Name ?? "";
        }

        private static bool IsFaceHosted(FamilyInstance fi)
        {
            try { return fi.HostFace != null; } catch { return false; }
        }

        private static string Json(CopyElementsResult r) =>
            JsonConvert.SerializeObject(ApiResponse<CopyElementsResult>.Ok(r));
    }
}
