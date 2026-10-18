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
    /// <summary>唯讀：打開族群文件，回報參考平面／立面視圖方向／幾何草圖平面。不做任何修改。</summary>
    public class FamilyInspectHandler
    {
        private readonly Document _doc;
        public FamilyInspectHandler(Document doc) => _doc = doc;

        private static string Ok(object data) => JsonConvert.SerializeObject(new { success = true, data, error = (string)null });
        private static string Fail(string msg) => JsonConvert.SerializeObject(new { success = false, data = (object)null, error = msg });

        private const double F = 304.8;
        private static object V(XYZ p) => p == null ? null : (object)new { x = Math.Round(p.X * F, 2), y = Math.Round(p.Y * F, 2), z = Math.Round(p.Z * F, 2) };
        private static object D(XYZ p) => p == null ? null : (object)new { x = Math.Round(p.X, 4), y = Math.Round(p.Y, 4), z = Math.Round(p.Z, 4) };

        // POST /families/inspect  { "name":"BAF_衛生紙架" }
        public string Inspect(string body)
        {
            var r = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            string name = (string)r["name"] ?? "";
            if (name == "") return Fail("需要 name。");

            var fams = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>().ToList();
            var fam = fams.FirstOrDefault(f => f.Name == name)
                   ?? fams.FirstOrDefault(f => f.Name.Contains(name));
            if (fam == null)
                return Fail("找不到族群。相近名稱：" + string.Join(", ", fams.Where(f => f.Name.Contains(name.Substring(0, Math.Min(2, name.Length)))).Select(f => f.Name).Take(20)));

            if (!fam.IsEditable) return Fail("族群不可編輯（可能是內建／系統族群）：" + fam.Name);

            Document fd = null;
            try
            {
                fd = _doc.EditFamily(fam);
                var owner = fd.OwnerFamily;

                var pWp = owner.get_Parameter(BuiltInParameter.FAMILY_WORK_PLANE_BASED);
                var pAlw = owner.get_Parameter(BuiltInParameter.FAMILY_ALWAYS_VERTICAL);

                var rps = new FilteredElementCollector(fd).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                    .Select(rp => new
                    {
                        id = rp.Id.IntegerValue,
                        name = rp.Name,
                        normal = D(rp.Normal),
                        bubble = V(rp.BubbleEnd),
                        free = V(rp.FreeEnd),
                        isRef = rp.get_Parameter(BuiltInParameter.ELEM_REFERENCE_NAME)?.AsValueString()
                    }).OrderBy(a => a.name).ToList();

                var views = new FilteredElementCollector(fd).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate)
                    .Select(v => new
                    {
                        name = v.Name,
                        type = v.ViewType.ToString(),
                        viewDir = D(v.ViewDirection),
                        upDir = D(v.UpDirection),
                        rightDir = D(v.RightDirection)
                    }).ToList();

                var forms = new List<object>();
                foreach (var e in new FilteredElementCollector(fd).OfClass(typeof(GenericForm)).Cast<GenericForm>())
                {
                    XYZ sketchNormal = null; XYZ sketchOrigin = null; string kind = e.GetType().Name;
                    try
                    {
                        if (e is Extrusion ex && ex.Sketch?.SketchPlane != null)
                        {
                            var pl = ex.Sketch.SketchPlane.GetPlane();
                            sketchNormal = pl.Normal; sketchOrigin = pl.Origin;
                        }
                    }
                    catch { }
                    BoundingBoxXYZ bb = null;
                    try { bb = e.get_BoundingBox(null); } catch { }
                    forms.Add(new
                    {
                        id = e.Id.IntegerValue,
                        kind,
                        isSolid = e.IsSolid,
                        sketchNormal = D(sketchNormal),
                        sketchOrigin = V(sketchOrigin),
                        bbMin = bb == null ? null : V(bb.Min),
                        bbMax = bb == null ? null : V(bb.Max)
                    });
                }

                var nested = new FilteredElementCollector(fd).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Select(fi => new
                    {
                        id = fi.Id.IntegerValue,
                        symbol = fi.Symbol?.Name,
                        family = fi.Symbol?.Family?.Name,
                        facing = D(fi.FacingOrientation),
                        hand = D(fi.HandOrientation),
                        origin = V((fi.Location as LocationPoint)?.Point)
                    }).ToList();

                var others = new FilteredElementCollector(fd).WhereElementIsNotElementType()
                    .Where(e => !(e is ReferencePlane) && !(e is View) && !(e is GenericForm) && !(e is FamilyInstance))
                    .GroupBy(e => e.GetType().Name)
                    .Select(g => new { type = g.Key, count = g.Count() }).ToList();

                var result = new
                {
                    family = fam.Name,
                    category = owner.FamilyCategory?.Name,
                    placementType = owner.FamilyPlacementType.ToString(),
                    workPlaneBased = pWp != null && pWp.AsInteger() == 1,
                    alwaysVertical = pAlw != null && pAlw.AsInteger() == 1,
                    templateFile = fd.Title,
                    referencePlanes = rps,
                    views,
                    forms,
                    nestedInstances = nested,
                    otherElementTypes = others
                };

                fd.Close(false); fd = null;
                return Ok(result);
            }
            catch (Exception ex)
            {
                try { if (fd != null) fd.Close(false); } catch { }
                return Fail("檢查族群失敗：" + ex.Message);
            }
        }
    }
}