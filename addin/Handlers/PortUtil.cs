// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitMcpAddin.Handlers
{
    /// <summary>元件摘要（JSON 欄位名用小寫，與其他 Handler 的輸出一致）。</summary>
    internal sealed class ElemDto
    {
#pragma warning disable IDE1006
        public long id;
        public string category = "", family = "", type = "", name = "", level = "";
#pragma warning restore IDE1006
    }

    /// <summary>
    /// 分析／MEP／建模／修改／載入族群這批 Handler 共用的小工具。
    /// 幾何一律 mm、專案內部座標（與 BuildHandler、ElementHandler 相同：mm / 304.8 = ft）。
    /// </summary>
    internal static class PortUtil
    {
        public const double FtPerMm = 1.0 / 304.8;
        public const double M2PerFt2 = 0.09290304;
        public const double M3PerFt3 = 0.028316846592;

        public static string Ok(object data) =>
            JsonConvert.SerializeObject(new { success = true, data, error = (string?)null });

        public static string Fail(string msg) =>
            JsonConvert.SerializeObject(new { success = false, data = (object?)null, error = msg });

        public static JObject Body(string body) =>
            string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);

        public static double ToMm(double ft) => Math.Round(ft * 304.8, 1);

        public static object Mm(XYZ p) => new[] { ToMm(p.X), ToMm(p.Y), ToMm(p.Z) };

        /// <summary>[x,y] 或 [x,y,z]（mm）→ XYZ（ft）。缺 z 時用 defaultZFt。</summary>
        public static XYZ? Pt(JToken? t, double defaultZFt = 0)
        {
            if (t is JArray a && a.Count >= 2)
            {
                double x = (double)a[0], y = (double)a[1];
                double z = a.Count >= 3 ? (double)a[2] * FtPerMm : defaultZFt;
                return new XYZ(x * FtPerMm, y * FtPerMm, z);
            }
            if (t is JObject o && o["x"] != null && o["y"] != null)
            {
                double z = o["z"] != null ? (double)o["z"]! * FtPerMm : defaultZFt;
                return new XYZ((double)o["x"]! * FtPerMm, (double)o["y"]! * FtPerMm, z);
            }
            return null;
        }

        public static List<long> Ids(JToken? t) =>
            (t as JArray)?.Select(x => (long)x).ToList() ?? new List<long>();

        public static List<string> Strings(JToken? t) =>
            (t as JArray)?.Select(x => (string?)x ?? "").Where(s => s.Length > 0).ToList() ?? new List<string>();

        public static Element? Get(Document doc, long id) => doc.GetElement(ElementIdCompat.NewId(id));

        public static bool InGroup(Element el) =>
            el.GroupId != null && el.GroupId != ElementId.InvalidElementId;

        public static string TypeName(Document doc, Element el)
        {
            var t = doc.GetElement(el.GetTypeId()) as ElementType;
            return t == null ? "" : t.Name;
        }

        public static string FamilyName(Document doc, Element el)
        {
            if (el is FamilyInstance fi) return fi.Symbol?.FamilyName ?? "";
            var t = doc.GetElement(el.GetTypeId()) as ElementType;
            return t?.FamilyName ?? "";
        }

        public static ElemDto Describe(Document doc, Element el) => new ElemDto
        {
            id = el.Id.Value,
            category = el.Category?.Name ?? "",
            family = FamilyName(doc, el),
            type = TypeName(doc, el),
            name = el.Name,
            level = LevelName(doc, el),
        };

        public static string LevelName(Document doc, Element el)
        {
            try
            {
                if (el.LevelId != null && el.LevelId != ElementId.InvalidElementId)
                    return doc.GetElement(el.LevelId)?.Name ?? "";
            }
            catch { }
            return "";
        }

        public static XYZ? Center(Element el)
        {
            if (el.Location is LocationPoint lp) return lp.Point;
            if (el.Location is LocationCurve lc) return lc.Curve.Evaluate(0.5, true);
            var bb = el.get_BoundingBox(null);
            return bb == null ? null : (bb.Min + bb.Max) / 2.0;
        }

        // ── 樓層 ─────────────────────────────────────────────
        public static List<Level> Levels(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).ToList();

        /// <summary>依名稱找樓層（完全相符優先，否則包含）；沒給名稱時取「不高於 zFt 的最高樓層」，再不行取最低樓層。</summary>
        public static Level? FindLevel(Document doc, string? name, double zFt)
        {
            var all = Levels(doc);
            if (all.Count == 0) return null;
            if (!string.IsNullOrWhiteSpace(name))
                return all.FirstOrDefault(l => l.Name == name)
                       ?? all.FirstOrDefault(l => l.Name.IndexOf(name!, StringComparison.OrdinalIgnoreCase) >= 0);
            return all.LastOrDefault(l => l.Elevation <= zFt + 1e-6) ?? all.First();
        }

        // ── 品類 ─────────────────────────────────────────────
        private static readonly Dictionary<string, BuiltInCategory> Alias =
            new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
            {
                ["walls"] = BuiltInCategory.OST_Walls, ["wall"] = BuiltInCategory.OST_Walls,
                ["floors"] = BuiltInCategory.OST_Floors, ["floor"] = BuiltInCategory.OST_Floors,
                ["roofs"] = BuiltInCategory.OST_Roofs, ["ceilings"] = BuiltInCategory.OST_Ceilings,
                ["doors"] = BuiltInCategory.OST_Doors, ["windows"] = BuiltInCategory.OST_Windows,
                ["columns"] = BuiltInCategory.OST_Columns,
                ["structuralcolumns"] = BuiltInCategory.OST_StructuralColumns,
                ["beams"] = BuiltInCategory.OST_StructuralFraming, ["framing"] = BuiltInCategory.OST_StructuralFraming,
                ["structuralframing"] = BuiltInCategory.OST_StructuralFraming,
                ["foundations"] = BuiltInCategory.OST_StructuralFoundation,
                ["ducts"] = BuiltInCategory.OST_DuctCurves, ["duct"] = BuiltInCategory.OST_DuctCurves,
                ["ductfittings"] = BuiltInCategory.OST_DuctFitting,
                ["pipes"] = BuiltInCategory.OST_PipeCurves, ["pipe"] = BuiltInCategory.OST_PipeCurves,
                ["pipefittings"] = BuiltInCategory.OST_PipeFitting,
                ["cabletrays"] = BuiltInCategory.OST_CableTray, ["conduits"] = BuiltInCategory.OST_Conduit,
                ["mechanicalequipment"] = BuiltInCategory.OST_MechanicalEquipment,
                ["plumbingfixtures"] = BuiltInCategory.OST_PlumbingFixtures,
                ["airterminals"] = BuiltInCategory.OST_DuctTerminal, ["ductterminal"] = BuiltInCategory.OST_DuctTerminal,
                ["sprinklers"] = BuiltInCategory.OST_Sprinklers,
                ["lightingfixtures"] = BuiltInCategory.OST_LightingFixtures,
                ["furniture"] = BuiltInCategory.OST_Furniture, ["casework"] = BuiltInCategory.OST_Casework,
                ["genericmodel"] = BuiltInCategory.OST_GenericModel, ["stairs"] = BuiltInCategory.OST_Stairs,
                ["railings"] = BuiltInCategory.OST_StairsRailing, ["rooms"] = BuiltInCategory.OST_Rooms,
                ["curtainpanels"] = BuiltInCategory.OST_CurtainWallPanels,
                ["specialtyequipment"] = BuiltInCategory.OST_SpecialityEquipment,
            };

        /// <summary>接受 OST_ 名稱、英文別名（walls、ducts…）或模型裡的品類顯示名（例如「牆」）。</summary>
        public static BuiltInCategory? Category(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var n = name.Trim();
            if (Alias.TryGetValue(n.Replace(" ", "").Replace("_", ""), out var a)) return a;
            var ost = n.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) ? n : "OST_" + n;
            foreach (BuiltInCategory b in Enum.GetValues(typeof(BuiltInCategory)))
                if (string.Equals(b.ToString(), ost, StringComparison.OrdinalIgnoreCase)) return b;
            foreach (Category c in doc.Settings.Categories)
                if (string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))
                    return (BuiltInCategory)(int)c.Id.Value;
            return null;
        }

        public static (List<BuiltInCategory> ok, List<string> unknown) Categories(Document doc, IEnumerable<string> names)
        {
            var ok = new List<BuiltInCategory>(); var bad = new List<string>();
            foreach (var n in names)
            {
                var b = Category(doc, n);
                if (b == null) bad.Add(n); else if (!ok.Contains(b.Value)) ok.Add(b.Value);
            }
            return (ok, bad);
        }

        public static ElementFilter CategoryFilter(IList<BuiltInCategory> cats) =>
            cats.Count == 1
                ? (ElementFilter)new ElementCategoryFilter(cats[0])
                : new ElementMulticategoryFilter(cats.ToList());

        // ── 類型 ─────────────────────────────────────────────
        /// <summary>依名稱找類型：完全相符「族群: 類型」或類型名優先，否則包含；沒給名稱取第一個。</summary>
        public static T? FindType<T>(IList<T> types, string? name) where T : ElementType
        {
            if (types.Count == 0) return null;
            if (string.IsNullOrWhiteSpace(name)) return types[0];
            string full(T t) => $"{t.FamilyName}: {t.Name}";
            return types.FirstOrDefault(t => t.Name == name || full(t) == name)
                   ?? types.FirstOrDefault(t => full(t).IndexOf(name!, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static List<string> TypeNames<T>(IEnumerable<T> types) where T : ElementType =>
            types.Select(t => $"{t.FamilyName}: {t.Name}").ToList();

        // ── 交易 ─────────────────────────────────────────────
        /// <summary>
        /// 單一交易執行；任何 Error 都整筆回滾（RollbackOnError），警告吞掉並回報。
        /// 回傳 committed=false 時，模型沒有任何變更。
        /// </summary>
        public static (bool committed, List<string> errors, List<string> warnings) RunTx(
            Document doc, string name, Action body)
        {
            var pre = new RollbackOnError();
            var errors = new List<string>();
            using (var tx = new Transaction(doc, name))
            {
                tx.Start();
                var fo = tx.GetFailureHandlingOptions();
                fo.SetFailuresPreprocessor(pre);
                fo.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(fo);
                try
                {
                    body();
                    var st = tx.Commit();
                    if (st != TransactionStatus.Committed || pre.HadError)
                    {
                        errors.AddRange(pre.Errors);
                        if (errors.Count == 0) errors.Add($"交易未提交（{st}）");
                        return (false, errors, pre.SwallowedWarnings);
                    }
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    errors.Add(ex.Message);
                    errors.AddRange(pre.Errors);
                    return (false, errors, pre.SwallowedWarnings);
                }
            }
            return (true, errors, pre.SwallowedWarnings);
        }

        public static void SetDouble(Element el, BuiltInParameter bip, double ft)
        {
            var p = el.get_Parameter(bip);
            if (p != null && !p.IsReadOnly) p.Set(ft);
        }
    }
}
