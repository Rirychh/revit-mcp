// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using static RevitMcpAddin.Handlers.PortUtil;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 唯讀分析：碰撞檢查、材料數量、房間資料、模型統計、目前選取。
    /// 全部不開 Transaction；只有給 csvPath 時會寫出 CSV 檔。
    /// </summary>
    public class AnalysisHandler
    {
        private readonly UIApplication _uiApp;
        private readonly Document _doc;
        public AnalysisHandler(UIApplication uiApp, Document doc) { _uiApp = uiApp; _doc = doc; }

        private static readonly string[] DefaultClashCats =
        {
            "OST_Walls", "OST_Floors", "OST_Roofs", "OST_Ceilings", "OST_StructuralColumns",
            "OST_StructuralFraming", "OST_DuctCurves", "OST_PipeCurves", "OST_CableTray", "OST_Conduit",
        };

        // POST /analysis/clashes
        // { setA:["OST_StructuralFraming"], setB:["ducts","pipes"], levels:["3F"], maxClashes:200, csvPath? }
        // 只給 setA = setA 自己互查；都沒給 = 常見實體品類互查。連結模型不在範圍內。
        public string Clashes(string body)
        {
            var r = Body(body);
            int max = (int?)r["maxClashes"] ?? 200;
            var aNames = Strings(r["setA"]); var bNames = Strings(r["setB"]);
            if (aNames.Count == 0 && bNames.Count == 0) aNames = DefaultClashCats.ToList();
            var (aCats, aBad) = Categories(_doc, aNames);
            var (bCats, bBad) = bNames.Count > 0 ? Categories(_doc, bNames) : (aCats.ToList(), new List<string>());
            if (aCats.Count == 0 || bCats.Count == 0)
                return Fail("沒有可用的品類。不認得：" + string.Join("、", aBad.Concat(bBad)));

            var levelNames = Strings(r["levels"]);
            var levelIds = levelNames.Count == 0 ? null
                : new HashSet<long>(Levels(_doc).Where(l => levelNames.Contains(l.Name)).Select(l => l.Id.Value));

            var setA = new FilteredElementCollector(_doc).WherePasses(CategoryFilter(aCats))
                .WhereElementIsNotElementType().ToElements()
                .Where(e => levelIds == null || levelIds.Contains(e.LevelId.Value)).ToList();
            var bFilter = CategoryFilter(bCats);

            var clashes = new List<Dictionary<string, object?>>();
            var seen = new HashSet<(long, long)>();
            bool truncated = false; int failedChecks = 0;

            foreach (var a in setA)
            {
                try
                {
                    var hits = new FilteredElementCollector(_doc).WherePasses(bFilter)
                        .WhereElementIsNotElementType()
                        .WherePasses(new ElementIntersectsElementFilter(a)).ToElements();
                    foreach (var b in hits)
                    {
                        long ia = a.Id.Value, ib = b.Id.Value;
                        if (ia == ib) continue;
                        var key = ia < ib ? (ia, ib) : (ib, ia);
                        if (!seen.Add(key)) continue;
                        if (levelIds != null && !levelIds.Contains(b.LevelId.Value) && !levelIds.Contains(a.LevelId.Value)) continue;
                        var c = Center(b) ?? Center(a);
                        clashes.Add(new Dictionary<string, object?>
                        {
                            ["a"] = Describe(_doc, a), ["b"] = Describe(_doc, b),
                            ["locationMm"] = c == null ? null : Mm(c),
                        });
                        if (clashes.Count >= max) { truncated = true; break; }
                    }
                }
                catch { failedChecks++; }
                if (truncated) break;
            }

            string? csv = (string?)r["csvPath"];
            if (!string.IsNullOrWhiteSpace(csv))
            {
                var sb = new StringBuilder("A_id,A_category,A_family,A_type,A_level,B_id,B_category,B_family,B_type,B_level,X_mm,Y_mm,Z_mm\n");
                foreach (var c in clashes)
                {
                    var a = (ElemDto)c["a"]!; var b = (ElemDto)c["b"]!; var p = c["locationMm"] as double[];
                    sb.AppendLine(string.Join(",", new object[] { a.id, Q(a.category), Q(a.family), Q(a.type), Q(a.level),
                        b.id, Q(b.category), Q(b.family), Q(b.type), Q(b.level),
                        p?[0] ?? 0, p?[1] ?? 0, p?[2] ?? 0 }));
                }
                WriteCsv(csv!, sb.ToString());
            }

            return Ok(new
            {
                clashCount = clashes.Count, truncated, setAElementCount = setA.Count,
                setA = aCats.Select(c => c.ToString()), setB = bCats.Select(c => c.ToString()),
                unknownCategories = aBad.Concat(bBad), failedChecks, csvPath = csv,
                note = "依 Revit 實體幾何干涉判斷（ElementIntersectsElementFilter）。已自動接合的混凝土、沒有實體幾何的元件不會被回報；連結模型不在範圍內。",
                clashes,
            });
        }

        // POST /analysis/material-quantities
        // { categories?:["walls","floors"], groupBy:"material"|"category-material", csvPath? }
        public string MaterialQuantities(string body)
        {
            var r = Body(body);
            string groupBy = ((string?)r["groupBy"] ?? "material").ToLowerInvariant();
            var names = Strings(r["categories"]);
            var (cats, bad) = Categories(_doc, names);
            if (names.Count > 0 && cats.Count == 0) return Fail("不認得這些品類：" + string.Join("、", bad));

            var col = new FilteredElementCollector(_doc).WhereElementIsNotElementType();
            IEnumerable<Element> els = cats.Count > 0 ? col.WherePasses(CategoryFilter(cats)).ToElements()
                : col.ToElements().Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model);

            var acc = new Dictionary<string, (string cat, string mat, double vol, double area, int n)>();
            int scanned = 0;
            foreach (var e in els)
            {
                ICollection<ElementId> mids;
                try { mids = e.GetMaterialIds(false); } catch { continue; }
                if (mids.Count == 0) continue;
                scanned++;
                foreach (var mid in mids)
                {
                    double v = 0, a = 0;
                    try { v = e.GetMaterialVolume(mid); a = e.GetMaterialArea(mid, false); } catch { }
                    string mat = _doc.GetElement(mid)?.Name ?? "(無材料)";
                    string cat = e.Category?.Name ?? "";
                    string key = groupBy == "category-material" ? cat + "|" + mat : mat;
                    acc.TryGetValue(key, out var cur);
                    acc[key] = (groupBy == "category-material" ? cat : "", mat, cur.vol + v, cur.area + a, cur.n + 1);
                }
            }

            var rows = acc.Values.OrderByDescending(x => x.vol).Select(x => new
            {
                category = x.cat, material = x.mat,
                volumeM3 = Math.Round(x.vol * M3PerFt3, 3), areaM2 = Math.Round(x.area * M2PerFt2, 2), elements = x.n,
            }).ToList();

            string? csv = (string?)r["csvPath"];
            if (!string.IsNullOrWhiteSpace(csv))
            {
                var sb = new StringBuilder("category,material,volume_m3,area_m2,elements\n");
                foreach (var x in rows) sb.AppendLine($"{Q(x.category)},{Q(x.material)},{x.volumeM3},{x.areaM2},{x.elements}");
                WriteCsv(csv!, sb.ToString());
            }

            return Ok(new
            {
                groupBy, elementsWithMaterials = scanned, unknownCategories = bad, csvPath = csv,
                note = "數值是 Revit 元件的材料體積／面積（GetMaterialVolume/Area），未扣搭接、損耗與施工放樣，不等於估驗數量。",
                rows,
            });
        }

        // GET /analysis/rooms?level=3F&includeBoundary=true&csvPath=...
        public string RoomData(System.Collections.Specialized.NameValueCollection qs)
        {
            string? level = qs["level"];
            bool boundary = string.Equals(qs["includeBoundary"], "true", StringComparison.OrdinalIgnoreCase);
            var opt = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };

            var rooms = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType().Cast<Room>()
                .Where(rm => string.IsNullOrWhiteSpace(level) || rm.Level?.Name == level)
                .OrderBy(rm => rm.Level?.Elevation ?? 0).ThenBy(rm => rm.Number).ToList();

            var list = new List<object>();
            var sb = new StringBuilder("id,number,name,level,area_m2,perimeter_m,volume_m3,unbounded_height_mm,placed,enclosed\n");
            foreach (var rm in rooms)
            {
                bool placed = rm.Location != null, enclosed = rm.Area > 0;
                double vol = 0; try { vol = rm.Volume; } catch { }
                List<List<double[]>>? loops = null;
                if (boundary && enclosed)
                {
                    loops = new List<List<double[]>>();
                    try
                    {
                        foreach (var seg in rm.GetBoundarySegments(opt))
                            loops.Add(seg.Select(s => s.GetCurve().GetEndPoint(0))
                                .Select(p => new[] { ToMm(p.X), ToMm(p.Y) }).ToList());
                    }
                    catch { }
                }
                var o = new
                {
                    id = rm.Id.Value, number = rm.Number, name = rm.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? rm.Name,
                    level = rm.Level?.Name ?? "", areaM2 = Math.Round(rm.Area * M2PerFt2, 2),
                    perimeterM = Math.Round(rm.Perimeter * 0.3048, 2), volumeM3 = Math.Round(vol * M3PerFt3, 2),
                    unboundedHeightMm = ToMm(rm.UnboundedHeight), placed, enclosed, boundaryMm = loops,
                };
                list.Add(o);
                sb.AppendLine($"{o.id},{Q(o.number)},{Q(o.name)},{Q(o.level)},{o.areaM2},{o.perimeterM},{o.volumeM3},{o.unboundedHeightMm},{placed},{enclosed}");
            }

            string? csv = qs["csvPath"];
            if (!string.IsNullOrWhiteSpace(csv)) WriteCsv(csv!, sb.ToString());

            return Ok(new
            {
                count = list.Count, totalAreaM2 = Math.Round(rooms.Sum(x => x.Area) * M2PerFt2, 2), csvPath = csv,
                note = "面積是 Revit 房間面積（依專案的房間面積計算設定），不是法規面積。邊界取完成面。",
                rooms = list,
            });
        }

        // GET /analysis/statistics
        public string Statistics()
        {
            var inst = new FilteredElementCollector(_doc).WhereElementIsNotElementType().ToElements();
            var byCat = inst.Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model)
                .GroupBy(e => e.Category.Name).Select(g => new { category = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count).ToList();
            var views = new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate).GroupBy(v => v.ViewType.ToString())
                .Select(g => new { viewType = g.Key, count = g.Count() }).OrderByDescending(x => x.count).ToList();
            int families = new FilteredElementCollector(_doc).OfClass(typeof(Family)).GetElementCount();
            int types = new FilteredElementCollector(_doc).WhereElementIsElementType().GetElementCount();
            int sheets = new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).GetElementCount();
            int levels = Levels(_doc).Count;
            int rooms = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().GetElementCount();
            int warnings = 0; try { warnings = _doc.GetWarnings().Count; } catch { }

            return Ok(new
            {
                document = _doc.Title, totalInstances = inst.Count, modelInstances = byCat.Sum(x => x.count),
                families, elementTypes = types, sheets, levels, rooms, warnings,
                modelCategories = byCat, views,
                note = "較完整的健檢（警告分類、未使用項目、CAD、連結）用 revit_model_health；效能主因用 revit_performance_report。",
            });
        }

        // GET /selection
        public string Selection()
        {
            var uidoc = _uiApp.ActiveUIDocument;
            if (uidoc == null) return Fail("沒有作用中的文件。");
            var ids = uidoc.Selection.GetElementIds();
            var items = ids.Select(id => _doc.GetElement(id)).Where(e => e != null)
                .Select(e => Describe(_doc, e!)).ToList();
            return Ok(new { count = items.Count, activeView = uidoc.ActiveView?.Name ?? "", elements = items });
        }

        private static string Q(object? s)
        {
            var v = s?.ToString() ?? "";
            return v.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        private static void WriteCsv(string path, string content)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content, new UTF8Encoding(true));
        }
    }
}
