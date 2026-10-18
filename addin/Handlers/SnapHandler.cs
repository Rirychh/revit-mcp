// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMcpAddin.Models;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 把既有元件的平面位置吸附到天花模矩格線上（例如 60x60 明架天花）。
    /// 只動 XY，Z 與主體不變，所以掛在天花上的燈具／灑水頭吸附後仍掛在同一片天花。
    ///格線相位由 originX/originY 與 moduleMm 決定：格線在 origin + k*module，
    /// target="center" 吸到格心（origin + k*module + module/2），"line" 吸到格線交點。
    /// </summary>
    public class SnapHandler
    {
        private readonly Document _doc;
        public SnapHandler(Document doc) => _doc = doc;

        private const double MM = 304.8;

        public string Snap(string body)
        {
            var req = JsonConvert.DeserializeObject<SnapRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");

            double module = req.ModuleMm > 0 ? req.ModuleMm : 600.0;
            double half   = string.Equals(req.Target, "line", StringComparison.OrdinalIgnoreCase)
                            ? 0.0 : module / 2.0;

            var targets = Resolve(req);
            var result = new SnapResult
            {
                DryRun     = req.DryRun,
                ModuleMm   = module,
                Target     = string.IsNullOrEmpty(req.Target) ? "center" : req.Target!,
                OriginX    = req.OriginX,
                OriginY    = req.OriginY,
                MatchCount = targets.Count
            };

            var plan = new List<(Element E, XYZ Delta, double Dx, double Dy)>();
            foreach (var e in targets)
            {
                var p = ElementHandler.GetPoint(e);
                if (p == null) { result.Skipped++; continue; }

                double xMm = p.X * MM, yMm = p.Y * MM;
                double nx = Snap1(xMm, req.OriginX, module, half);
                double ny = Snap1(yMm, req.OriginY, module, half);
                double dx = nx - xMm, dy = ny - yMm;

                if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) { result.AlreadyOnGrid++; continue; }
                plan.Add((e, new XYZ(dx / MM, dy / MM, 0), dx, dy));
            }

            result.MaxDxMm = plan.Count == 0 ? 0 : plan.Max(t => Math.Abs(t.Dx));
            result.MaxDyMm = plan.Count == 0 ? 0 : plan.Max(t => Math.Abs(t.Dy));
            result.MoveCount = plan.Count;

            foreach (var t in plan.Take(req.SampleCount > 0 ? req.SampleCount : 8))
                result.Samples.Add(new SnapSample
                {
                    Id = t.E.Id.IntegerValue,
                    Name = (t.E as FamilyInstance)?.Symbol.Name ?? t.E.Name,
                    DxMm = Math.Round(t.Dx, 1),
                    DyMm = Math.Round(t.Dy, 1)
                });

            if (req.DryRun || plan.Count == 0) return Json(result);

            var swallow = new SwallowWarnings();
            using var tx = new Transaction(_doc, "MCP 吸附至天花格線");
            tx.Start();
            var opts = tx.GetFailureHandlingOptions();
            opts.SetFailuresPreprocessor(swallow);
            opts.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(opts);

            foreach (var t in plan)
            {
                try { ElementTransformUtils.MoveElement(_doc, t.E.Id, t.Delta); result.Moved++; }
                catch (Exception ex)
                {
                    result.Skipped++;
                    result.Warnings.Add($"{t.E.Id.IntegerValue} 移動失敗：{ex.Message}");
                }
            }
            tx.Commit();

            foreach (var s in swallow.Swallowed.Distinct().Take(10)) result.Warnings.Add("[已吞警告] " + s);
            return Json(result);
        }

        /// <summary>把一個座標吸到 origin + k*module + half 最近的一格。</summary>
        private static double Snap1(double v, double origin, double module, double half)
        {
            double k = Math.Round((v - origin - half) / module);
            return origin + k * module + half;
        }

        private List<Element> Resolve(SnapRequest req)
        {
            if (req.ElementIds != null && req.ElementIds.Count > 0)
                return req.ElementIds.Select(id => _doc.GetElement(new ElementId((int)id)))
                                     .Where(e => e != null).ToList()!;

            var collector = new FilteredElementCollector(_doc).WhereElementIsNotElementType();
            var bics = (req.Categories ?? new List<string>())
                .Select(n => ElementHandler.TryCategory(n, out var b) ? (BuiltInCategory?)b : null)
                .Where(b => b.HasValue).Select(b => b!.Value).ToList();
            if (bics.Count == 1) collector = collector.OfCategory(bics[0]);
            else if (bics.Count > 1) collector = collector.WherePasses(new ElementMulticategoryFilter(bics));

            var fams  = req.FamilyNames ?? new List<string>();
            var hosts = req.HostIds ?? new List<long>();

            return collector.Cast<Element>()
                .Where(e => string.IsNullOrEmpty(req.LevelName) || LevelNameOf(e) == req.LevelName)
                .Where(e => fams.Count == 0 || (e is FamilyInstance f && fams.Contains(f.Symbol.FamilyName)))
                .Where(e => hosts.Count == 0 ||
                            (e is FamilyInstance f2 && f2.Host != null && hosts.Contains(f2.Host.Id.IntegerValue)))
                .Where(e => req.BBox == null || ElementHandler.InBBox(ElementHandler.GetPoint(e), req.BBox))
                .ToList();
        }

        private static string LevelNameOf(Element e)
        {
            if (e.LevelId == ElementId.InvalidElementId) return "";
            return e.Document.GetElement(e.LevelId)?.Name ?? "";
        }

        private static string Json(SnapResult r) =>
            JsonConvert.SerializeObject(ApiResponse<SnapResult>.Ok(r));
    }
}
