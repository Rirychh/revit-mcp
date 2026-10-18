// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using System.Globalization;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMcpAddin.Models;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 列出連結模型（ST／MEP…）裡的構件。主模型的 /elements 只掃目前文件，
    /// 連結裡的梁柱要經 RevitLinkInstance.GetLinkDocument() 才讀得到，
    /// 座標再乘上 GetTotalTransform() 換回主模型座標（本案連結是「位置 &lt;未共用&gt;」，一定要換）。
    /// 唯讀，不開交易。
    /// </summary>
    public class LinkedElementHandler
    {
        private const double FT = 304.8;
        private readonly Document _doc;
        public LinkedElementHandler(Document doc) => _doc = doc;

        // 沒指定 params 時，結構構件預設嘗試讀這些斷面參數（有就回，沒有就略過）
        private static readonly string[] DefaultSizeParams =
            { "b", "h", "B", "H", "寬度", "高度", "深度", "梁寬", "梁深", "厚度" };

        public string ListElements(NameValueCollection qs)
        {
            string source  = qs["source"]   ?? "";
            string level   = qs["level"]    ?? "";
            string family  = qs["family"]   ?? "";
            string type    = qs["type"]     ?? "";
            string mark    = qs["mark"]     ?? "";
            int    limit   = ParseInt(qs["limit"], 100);
            int    offset  = ParseInt(qs["offset"], 0);
            double? minX = ParseD(qs["minX"]), minY = ParseD(qs["minY"]), minZ = ParseD(qs["minZ"]);
            double? maxX = ParseD(qs["maxX"]), maxY = ParseD(qs["maxY"]), maxZ = ParseD(qs["maxZ"]);

            var bic = ParseCategory(qs["category"]);
            var paramNames = (qs["params"] ?? "")
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            bool useDefaultParams = paramNames.Count == 0;
            if (useDefaultParams) paramNames = DefaultSizeParams.ToList();

            var res = new LinkedElementsResult { Category = bic.ToString(), Offset = offset };
            int matched = 0;

            foreach (var li in new FilteredElementCollector(_doc)
                         .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var ldoc = li.GetLinkDocument();
                if (ldoc == null) { res.UnloadedLinks.Add(li.Name); continue; }
                if (source.Length > 0 && !Has(ldoc.Title, source) && !Has(li.Name, source)) continue;
                res.SearchedSources.Add($"{ldoc.Title} (instance {li.Id.IntegerValue})");

                var xf = li.GetTotalTransform();
                foreach (var e in new FilteredElementCollector(ldoc)
                             .OfCategory(bic).WhereElementIsNotElementType())
                {
                    var et = ldoc.GetElement(e.GetTypeId()) as ElementType;
                    string famName  = et?.FamilyName ?? "";
                    string typeName = et?.Name ?? e.Name ?? "";
                    string lvName   = GetLevelName(ldoc, e);
                    string markVal  = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";

                    if (level.Length  > 0 && !Has(lvName, level))    continue;
                    if (family.Length > 0 && !Has(famName, family))  continue;
                    if (type.Length   > 0 && !Has(typeName, type))   continue;
                    if (mark.Length   > 0 && !Has(markVal, mark))    continue;

                    var bb = e.get_BoundingBox(null);
                    if (bb == null) continue;
                    var (bmin, bmax) = TransformBox(bb, xf);

                    // 範圍篩選：給了哪個邊界就檢查哪個，外框有重疊即算
                    if (maxX.HasValue && bmin.X > maxX) continue;
                    if (minX.HasValue && bmax.X < minX) continue;
                    if (maxY.HasValue && bmin.Y > maxY) continue;
                    if (minY.HasValue && bmax.Y < minY) continue;
                    if (maxZ.HasValue && bmin.Z > maxZ) continue;
                    if (minZ.HasValue && bmax.Z < minZ) continue;

                    matched++;
                    if (matched <= offset) continue;
                    if (res.Elements.Count >= limit) { res.HasMore = true; continue; }

                    var dto = new LinkedElementDto
                    {
                        Id = e.Id.IntegerValue, Source = ldoc.Title, LinkInstanceId = li.Id.IntegerValue,
                        Category = e.Category?.Name ?? "", FamilyName = famName, TypeName = typeName,
                        Mark = markVal, LevelName = lvName, BboxMin = bmin, BboxMax = bmax,
                    };

                    if (e.Location is LocationCurve lc)
                    {
                        dto.Start = Mm(xf.OfPoint(lc.Curve.GetEndPoint(0)));
                        dto.End   = Mm(xf.OfPoint(lc.Curve.GetEndPoint(1)));
                    }
                    else if (e.Location is LocationPoint lp)
                    {
                        dto.Start = Mm(xf.OfPoint(lp.Point));
                    }

                    foreach (var n in paramNames)
                    {
                        var p = e.LookupParameter(n) ?? et?.LookupParameter(n);
                        var v = p == null ? null : ParamValue(p);
                        if (v != null) dto.Parameters[n] = v;
                        else if (!useDefaultParams) dto.Parameters[n] = "(無此參數)";
                    }

                    res.Elements.Add(dto);
                }
            }

            res.Total = matched;
            res.Count = res.Elements.Count;
            return JsonConvert.SerializeObject(ApiResponse<LinkedElementsResult>.Ok(res));
        }

        // ── helpers ─────────────────────────────────────────

        private static BuiltInCategory ParseCategory(string? raw)
        {
            string s = (raw ?? "").Trim();
            if (s.Length == 0) return BuiltInCategory.OST_StructuralFraming;
            switch (s.ToLowerInvariant())
            {
                case "beams": case "framing": case "structuralframing": case "梁":
                    return BuiltInCategory.OST_StructuralFraming;
                case "columns": case "structuralcolumns": case "柱":
                    return BuiltInCategory.OST_StructuralColumns;
                case "floors": case "slabs": case "樓板":
                    return BuiltInCategory.OST_Floors;
                case "walls": case "牆":
                    return BuiltInCategory.OST_Walls;
                case "foundation": case "structuralfoundation": case "基礎":
                    return BuiltInCategory.OST_StructuralFoundation;
            }
            if (!s.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)) s = "OST_" + s;
            if (Enum.TryParse(s, true, out BuiltInCategory bic)) return bic;
            throw new ArgumentException($"未知的品類：{raw}（可用 Beams / Columns / Floors / Walls，或 OST_ 開頭的原始名稱）");
        }

        private static string GetLevelName(Document d, Element e)
        {
            ElementId id = e.LevelId;
            if (id == null || id == ElementId.InvalidElementId)
                id = e.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElementId()
                  ?? e.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)?.AsElementId()
                  ?? ElementId.InvalidElementId;
            return (d.GetElement(id) as Level)?.Name ?? "";
        }

        /// <summary>外框 8 個角點全部換到主模型座標再取極值（連結有旋轉時才不會算錯）。</summary>
        private static (XyzMm min, XyzMm max) TransformBox(BoundingBoxXYZ bb, Transform link)
        {
            var t = link.Multiply(bb.Transform);
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            foreach (var cx in new[] { bb.Min.X, bb.Max.X })
            foreach (var cy in new[] { bb.Min.Y, bb.Max.Y })
            foreach (var cz in new[] { bb.Min.Z, bb.Max.Z })
            {
                var p = t.OfPoint(new XYZ(cx, cy, cz));
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
            }
            return (Mm(new XYZ(x0, y0, z0)), Mm(new XYZ(x1, y1, z1)));
        }

        private static XyzMm Mm(XYZ p) => new XyzMm
        {
            X = Math.Round(p.X * FT, 1), Y = Math.Round(p.Y * FT, 1), Z = Math.Round(p.Z * FT, 1)
        };

        /// <summary>長度一律回 mm 數字字串；其他型別用顯示值。</summary>
        private static string? ParamValue(Parameter p)
        {
            if (!p.HasValue) return null;
            switch (p.StorageType)
            {
                case StorageType.Double:
                    if (p.Definition.GetDataType() == SpecTypeId.Length)
                        return Math.Round(p.AsDouble() * FT, 1).ToString(CultureInfo.InvariantCulture);
                    return p.AsValueString() ?? p.AsDouble().ToString(CultureInfo.InvariantCulture);
                case StorageType.Integer:   return p.AsValueString() ?? p.AsInteger().ToString();
                case StorageType.String:    return p.AsString();
                case StorageType.ElementId: return p.AsValueString() ?? p.AsElementId().IntegerValue.ToString();
            }
            return null;
        }

        private static bool Has(string? s, string sub) =>
            (s ?? "").IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;

        private static int ParseInt(string? s, int def) =>
            int.TryParse(s, out var v) && v >= 0 ? v : def;

        private static double? ParseD(string? s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
