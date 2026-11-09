// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using static RevitMcpAddin.Handlers.PortUtil;
using static RevitMcpAddin.Handlers.ViewPlane;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 視圖設定：視圖範圍、詳細等級／視覺樣式／專業／範圍框、視圖篩選器、建立視圖樣板、隱藏與隔離、3D 剖面框、
    /// 建立立面與詳圖索引。全部 apply=false 預檢；apply=true 單一交易，任何錯誤整批回滾。
    /// </summary>
    public class ViewSetupHandler
    {
        private readonly Document _doc;
        public ViewSetupHandler(Document doc) => _doc = doc;

        private List<View> Views(JToken? ids, List<object> bad)
        {
            var o = new List<View>();
            foreach (var id in Ids(ids))
            {
                if (Get(_doc, id) is View v && !v.IsTemplate) o.Add(v);
                else if (Get(_doc, id) is View t && t.IsTemplate) o.Add(t);
                else bad.Add(new { viewId = id, reason = "不是視圖" });
            }
            return o;
        }

        private static string Fmt(Exception ex) => ex.Message.Split('\n')[0];

        // POST /views/range { viewIds, cutMm?, topMm?, bottomMm?, depthMm?, apply }  各值為相對所屬樓層的偏移
        public string ViewRange(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false; var bad = new List<object>();
            var views = Views(r["viewIds"], bad).Where(v =>
            {
                if (v is ViewPlan) return true;
                bad.Add(new { viewId = v.Id.Value, reason = "視圖範圍只適用平面視圖" }); return false;
            }).Cast<ViewPlan>().ToList();
            if (views.Count == 0) return Fail("沒有可設定的平面視圖：" + Newtonsoft.Json.JsonConvert.SerializeObject(bad));
            var set = new Dictionary<PlanViewPlane, double?>
            {
                [PlanViewPlane.CutPlane] = (double?)r["cutMm"], [PlanViewPlane.TopClipPlane] = (double?)r["topMm"],
                [PlanViewPlane.BottomClipPlane] = (double?)r["bottomMm"], [PlanViewPlane.ViewDepthPlane] = (double?)r["depthMm"],
            };
            object Read(ViewPlan v)
            {
                var vr = v.GetViewRange();
                string N(PlanViewPlane p) { var id = vr.GetLevelId(p); return id == ElementId.InvalidElementId ? "（無限制）" : (_doc.GetElement(id)?.Name ?? "?"); }
                return new
                {
                    viewId = v.Id.Value, name = v.Name, template = v.ViewTemplateId != ElementId.InvalidElementId ? _doc.GetElement(v.ViewTemplateId)?.Name : null,
                    topMm = new { level = N(PlanViewPlane.TopClipPlane), offset = ToMm(vr.GetOffset(PlanViewPlane.TopClipPlane)) },
                    cutMm = new { level = N(PlanViewPlane.CutPlane), offset = ToMm(vr.GetOffset(PlanViewPlane.CutPlane)) },
                    bottomMm = new { level = N(PlanViewPlane.BottomClipPlane), offset = ToMm(vr.GetOffset(PlanViewPlane.BottomClipPlane)) },
                    depthMm = new { level = N(PlanViewPlane.ViewDepthPlane), offset = ToMm(vr.GetOffset(PlanViewPlane.ViewDepthPlane)) },
                };
            }
            var current = views.Select(Read).ToList();
            if (!apply || set.Values.All(x => x == null)) return Ok(new { preflight = true, current, willSet = set.Where(k => k.Value != null).ToDictionary(k => k.Key.ToString(), k => k.Value), skipped = bad });
            var failed = new List<object>();
            var res = RunTx(_doc, "MCP View Range", () =>
            {
                foreach (var v in views)
                {
                    try
                    {
                        var vr = v.GetViewRange();
                        foreach (var kv in set) if (kv.Value.HasValue) vr.SetOffset(kv.Key, kv.Value.Value / FT);
                        v.SetViewRange(vr);
                    }
                    catch (Exception ex) { failed.Add(new { viewId = v.Id.Value, reason = Fmt(ex) + (v.ViewTemplateId != ElementId.InvalidElementId ? "（視圖樣板可能鎖定了視圖範圍，請改樣板）" : "") }); }
                }
            });
            return res.committed ? Ok(new { applied = true, after = views.Select(Read), notApplied = failed, skipped = bad, warnings = res.warnings })
                : Fail("設定失敗，已整批回滾：" + string.Join("；", res.errors));
        }

        // POST /views/display { viewIds, detailLevel?, displayStyle?, discipline?, scopeBox?, apply }
        public string Display(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false; var bad = new List<object>();
            var views = Views(r["viewIds"], bad);
            if (views.Count == 0) return Fail("沒有可設定的視圖。");
            string? dl = (string?)r["detailLevel"], ds = (string?)r["displayStyle"], disc = (string?)r["discipline"], sb = (string?)r["scopeBox"];
            var dlMap = new Dictionary<string, ViewDetailLevel> { ["coarse"] = ViewDetailLevel.Coarse, ["medium"] = ViewDetailLevel.Medium, ["fine"] = ViewDetailLevel.Fine };
            var dsMap = new Dictionary<string, DisplayStyle> { ["wireframe"] = DisplayStyle.Wireframe, ["hiddenLine"] = DisplayStyle.HLR, ["shaded"] = DisplayStyle.Shading,
                ["shadedWithEdges"] = DisplayStyle.ShadingWithEdges, ["consistentColors"] = DisplayStyle.FlatColors, ["realistic"] = DisplayStyle.Realistic };
            var discMap = new Dictionary<string, int> { ["architectural"] = 1, ["structural"] = 2, ["mechanical"] = 4, ["electrical"] = 8, ["plumbing"] = 4096, ["coordination"] = 4095 };
            Element? scope = null;
            if (!string.IsNullOrWhiteSpace(sb))
            {
                var boxes = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType().ToList();
                scope = sb == "none" ? null : boxes.FirstOrDefault(b => b.Name == sb) ?? boxes.FirstOrDefault(b => b.Name.IndexOf(sb!, StringComparison.OrdinalIgnoreCase) >= 0);
                if (sb != "none" && scope == null) return Fail("找不到範圍框「" + sb + "」。可用：" + string.Join("、", boxes.Select(b => b.Name)));
            }
            if (dl != null && !dlMap.ContainsKey(dl)) return Fail("detailLevel 要是 coarse／medium／fine。");
            if (ds != null && !dsMap.ContainsKey(ds)) return Fail("displayStyle 要是 " + string.Join("／", dsMap.Keys) + "。");
            if (disc != null && !discMap.ContainsKey(disc)) return Fail("discipline 要是 " + string.Join("／", discMap.Keys) + "。");
            var current = views.Select(v => new { viewId = v.Id.Value, name = v.Name, viewType = v.ViewType.ToString(), detailLevel = v.DetailLevel.ToString(), displayStyle = v.DisplayStyle.ToString(),
                template = v.ViewTemplateId != ElementId.InvalidElementId ? _doc.GetElement(v.ViewTemplateId)?.Name : null }).ToList();
            if (!apply) return Ok(new { preflight = true, current, willSet = new { detailLevel = dl, displayStyle = ds, discipline = disc, scopeBox = sb }, skipped = bad });
            var failed = new List<object>();
            var res = RunTx(_doc, "MCP View Display", () =>
            {
                foreach (var v in views)
                {
                    void Try(string what, Action a) { try { a(); } catch (Exception ex) { failed.Add(new { viewId = v.Id.Value, what, reason = Fmt(ex) }); } }
                    if (dl != null) Try("detailLevel", () => v.DetailLevel = dlMap[dl]);
                    if (ds != null) Try("displayStyle", () => v.DisplayStyle = dsMap[ds]);
                    if (disc != null) Try("discipline", () => { var p = v.get_Parameter(BuiltInParameter.VIEW_DISCIPLINE); if (p == null || p.IsReadOnly) throw new InvalidOperationException("這個視圖的專業參數不能改（可能被視圖樣板鎖定）"); p.Set(discMap[disc]); });
                    if (sb != null) Try("scopeBox", () => { var p = v.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP); if (p == null || p.IsReadOnly) throw new InvalidOperationException("這個視圖不能指定範圍框"); p.Set(scope?.Id ?? ElementId.InvalidElementId); });
                }
            });
            return res.committed ? Ok(new { applied = true, views = views.Count, notApplied = failed, skipped = bad, warnings = res.warnings })
                : Fail("設定失敗，已整批回滾：" + string.Join("；", res.errors));
        }

        // POST /views/filters
        // { name, categories:[...], parameter, operator, value, valueUnit?:"mm"|"m2"|"none", viewIds?, visible?:true,
        //   colorRgb?:[r,g,b], halftone?, lineWeight?, apply }
        public string Filter(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            string name = ((string?)r["name"] ?? "").Trim();
            if (name.Length == 0) return Fail("name 必填（篩選器名稱）。");
            var (cats, unknown) = Categories(_doc, Strings(r["categories"]));
            if (cats.Count == 0) return Fail("categories 必填，且要是可辨識的品類。" + (unknown.Count > 0 ? "無法辨識：" + string.Join("、", unknown) : ""));
            var catIds = cats.Select(c => new ElementId(c)).ToList();
            var existing = new FilteredElementCollector(_doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>().FirstOrDefault(f => f.Name == name);

            // 參數：在這些品類共同可篩選的參數裡依名稱找
            ElementId? pid = null; string pname = (string?)r["parameter"] ?? ""; var avail = new List<string>();
            foreach (var id in ParameterFilterUtilities.GetFilterableParametersInCommon(_doc, catIds))
            {
                string n = id.Value < 0 ? LabelUtils.GetLabelFor((BuiltInParameter)(int)id.Value) : (_doc.GetElement(id)?.Name ?? "");
                avail.Add(n);
                if (pid == null && n == pname) pid = id;
            }
            if (existing == null && pid == null) return Fail("這些品類沒有共同的參數「" + pname + "」。可用：" + string.Join("、", avail.Distinct().OrderBy(x => x).Take(80)));
            string op = ((string?)r["operator"] ?? "equals").ToLowerInvariant();
            var val = r["value"]; string unit = ((string?)r["valueUnit"] ?? "none").ToLowerInvariant();

            FilterRule? rule = null; string? ruleErr = null;
            if (existing == null)
            {
                try { rule = MakeRule(pid!, op, val, unit); } catch (Exception ex) { ruleErr = Fmt(ex); }
                if (rule == null) return Fail("無法建立篩選規則：" + (ruleErr ?? "operator 與 value 不相容"));
            }
            var bad = new List<object>(); var views = Views(r["viewIds"], bad);
            bool visible = (bool?)r["visible"] ?? true;
            var ogs = new OverrideGraphicSettings(); bool hasOgs = false;
            if (r["colorRgb"] is JArray c && c.Count == 3)
            {
                var col = new Color((byte)(int)c[0], (byte)(int)c[1], (byte)(int)c[2]);
                ogs.SetProjectionLineColor(col); ogs.SetCutLineColor(col);
                var solid = new FilteredElementCollector(_doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
                if (solid != null) { ogs.SetSurfaceForegroundPatternId(solid.Id); ogs.SetSurfaceForegroundPatternColor(col); ogs.SetCutForegroundPatternId(solid.Id); ogs.SetCutForegroundPatternColor(col); }
                hasOgs = true;
            }
            if ((bool?)r["halftone"] == true) { ogs.SetHalftone(true); hasOgs = true; }
            if ((int?)r["lineWeight"] is int lw && lw >= 1 && lw <= 16) { ogs.SetProjectionLineWeight(lw); hasOgs = true; }
            var plan = new
            {
                filter = name, reuseExisting = existing != null, categories = cats.Select(x => x.ToString()), parameter = pname, op, value = val?.ToString(), valueUnit = unit,
                applyTo = views.Select(v => new { viewId = v.Id.Value, name = v.Name, controlledByTemplate = !v.IsTemplate && v.ViewTemplateId != ElementId.InvalidElementId }),
                visible, overrides = hasOgs, skipped = bad,
            };
            if (!apply) return Ok(new { preflight = true, plan });
            var failed = new List<object>(); long fid = 0;
            var res = RunTx(_doc, "MCP View Filter", () =>
            {
                var f = existing ?? ParameterFilterElement.Create(_doc, name, catIds, new ElementParameterFilter(rule!));
                fid = f.Id.Value;
                foreach (var v in views)
                {
                    try
                    {
                        if (!v.GetFilters().Contains(f.Id)) v.AddFilter(f.Id);
                        v.SetFilterVisibility(f.Id, visible);
                        if (hasOgs) v.SetFilterOverrides(f.Id, ogs);
                    }
                    catch (Exception ex) { failed.Add(new { viewId = v.Id.Value, reason = Fmt(ex) + "（套了視圖樣板的視圖請改把篩選器加到樣板上）" }); }
                }
            });
            return res.committed ? Ok(new { applied = true, filterId = fid, plan, notApplied = failed, warnings = res.warnings })
                : Fail("建立篩選器失敗，已整批回滾：" + string.Join("；", res.errors));
        }

        private static FilterRule MakeRule(ElementId pid, string op, JToken? v, string unit)
        {
            if (v == null) throw new ArgumentException("value 必填");
            if (v.Type == JTokenType.String)
            {
                string s = (string)v!;
#if REVIT2023_OR_GREATER
                return op switch
                {
                    "equals" => ParameterFilterRuleFactory.CreateEqualsRule(pid, s),
                    "notequals" => ParameterFilterRuleFactory.CreateNotEqualsRule(pid, s),
                    "contains" => ParameterFilterRuleFactory.CreateContainsRule(pid, s),
                    "notcontains" => ParameterFilterRuleFactory.CreateNotContainsRule(pid, s),
                    "beginswith" => ParameterFilterRuleFactory.CreateBeginsWithRule(pid, s),
                    _ => throw new ArgumentException("文字值的 operator 要是 equals／notEquals／contains／notContains／beginsWith"),
                };
#else
                return op switch
                {
                    "equals" => ParameterFilterRuleFactory.CreateEqualsRule(pid, s, false),
                    "notequals" => ParameterFilterRuleFactory.CreateNotEqualsRule(pid, s, false),
                    "contains" => ParameterFilterRuleFactory.CreateContainsRule(pid, s, false),
                    "notcontains" => ParameterFilterRuleFactory.CreateNotContainsRule(pid, s, false),
                    "beginswith" => ParameterFilterRuleFactory.CreateBeginsWithRule(pid, s, false),
                    _ => throw new ArgumentException("文字值的 operator 要是 equals／notEquals／contains／notContains／beginsWith"),
                };
#endif
            }
            if (v.Type == JTokenType.Integer && unit == "none" && (op == "equals" || op == "notequals"))
                return op == "equals" ? ParameterFilterRuleFactory.CreateEqualsRule(pid, (int)v) : ParameterFilterRuleFactory.CreateNotEqualsRule(pid, (int)v);
            if (v.Type != JTokenType.Integer && v.Type != JTokenType.Float) throw new ArgumentException("value 要是文字或數字");
            double d = (double)v; d = unit == "mm" ? d / 304.8 : unit == "m2" ? d / 0.09290304 : unit == "m3" ? d / 0.028316846592 : d;
            const double eps = 1e-6;
            return op switch
            {
                "equals" => ParameterFilterRuleFactory.CreateEqualsRule(pid, d, eps),
                "notequals" => ParameterFilterRuleFactory.CreateNotEqualsRule(pid, d, eps),
                "greater" => ParameterFilterRuleFactory.CreateGreaterRule(pid, d, eps),
                "greaterorequal" => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(pid, d, eps),
                "less" => ParameterFilterRuleFactory.CreateLessRule(pid, d, eps),
                "lessorequal" => ParameterFilterRuleFactory.CreateLessOrEqualRule(pid, d, eps),
                _ => throw new ArgumentException("數值的 operator 要是 equals／notEquals／greater／greaterOrEqual／less／lessOrEqual"),
            };
        }

        // POST /views/template-from-view { viewId, name, apply }
        public string TemplateFromView(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            if (Get(_doc, (long?)r["viewId"] ?? 0) is not View v || v.IsTemplate) return Fail("viewId 要是一般視圖。");
            string name = ((string?)r["name"] ?? "").Trim(); if (name.Length == 0) return Fail("name 必填。");
            if (new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>().Any(x => x.IsTemplate && x.Name == name)) return Fail("已經有同名的視圖樣板「" + name + "」。");
            if (!apply) return Ok(new { preflight = true, from = v.Name, viewType = v.ViewType.ToString(), newTemplate = name });
            long id = 0;
            var res = RunTx(_doc, "MCP Create View Template", () => { var t = v.CreateViewTemplate(); t.Name = name; id = t.Id.Value; });
            return res.committed ? Ok(new { applied = true, templateId = id, name, from = v.Name, note = "用 revit_apply_view_template 套到其他視圖。" })
                : Fail("建立視圖樣板失敗：" + string.Join("；", res.errors));
        }

        // POST /views/visibility { viewId, action, elementIds?, categories?, apply }
        // action：isolate／hide／isolateCategories／hideCategories／resetTemporary（暫時）；hidePermanent／unhidePermanent（永久）
        public string Visibility(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            if (Get(_doc, (long?)r["viewId"] ?? 0) is not View v || v.IsTemplate) return Fail("viewId 要是一般視圖。");
            string action = ((string?)r["action"] ?? "").Trim();
            var ids = Ids(r["elementIds"]).Select(ElementIdCompat.NewId).ToList();
            var (cats, unknown) = Categories(_doc, Strings(r["categories"]));
            var catIds = cats.Select(c => new ElementId(c)).ToList();
            var needIds = new[] { "isolate", "hide", "hidePermanent", "unhidePermanent" }; var needCats = new[] { "isolateCategories", "hideCategories" };
            if (needIds.Contains(action) && ids.Count == 0) return Fail("action「" + action + "」需要 elementIds。");
            if (needCats.Contains(action) && catIds.Count == 0) return Fail("action「" + action + "」需要 categories。" + (unknown.Count > 0 ? "無法辨識：" + string.Join("、", unknown) : ""));
            if (!needIds.Contains(action) && !needCats.Contains(action) && action != "resetTemporary") return Fail("action 要是 isolate／hide／isolateCategories／hideCategories／resetTemporary／hidePermanent／unhidePermanent。");
            var plan = new { view = v.Name, action, elements = ids.Count, categories = cats.Select(c => c.ToString()), temporary = !action.EndsWith("Permanent") };
            if (!apply) return Ok(new { preflight = true, plan });
            var res = RunTx(_doc, "MCP View Visibility", () =>
            {
                switch (action)
                {
                    case "isolate": v.IsolateElementsTemporary(ids); break;
                    case "hide": v.HideElementsTemporary(ids); break;
                    case "isolateCategories": v.IsolateCategoriesTemporary(catIds); break;
                    case "hideCategories": v.HideCategoriesTemporary(catIds); break;
                    case "resetTemporary": v.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate); break;
                    case "hidePermanent": v.HideElements(ids.Where(i => _doc.GetElement(i)?.CanBeHidden(v) == true).ToList()); break;
                    case "unhidePermanent": v.UnhideElements(ids); break;
                }
            });
            return res.committed ? Ok(new { applied = true, plan, warnings = res.warnings })
                : Fail("設定可見性失敗：" + string.Join("；", res.errors));
        }

        // POST /views/section-box { viewId, elementIds?, minMm?, maxMm?, marginMm?:500, off?:false, apply }
        public string SectionBox(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            if (Get(_doc, (long?)r["viewId"] ?? 0) is not View3D v || v.IsTemplate) return Fail("viewId 要是 3D 視圖。");
            bool off = (bool?)r["off"] ?? false; double m = ((double?)r["marginMm"] ?? 500) / FT;
            XYZ? mn = null, mx = null;
            if (r["minMm"] is JArray a && r["maxMm"] is JArray b && a.Count == 3 && b.Count == 3)
            { mn = new XYZ((double)a[0] / FT, (double)a[1] / FT, (double)a[2] / FT); mx = new XYZ((double)b[0] / FT, (double)b[1] / FT, (double)b[2] / FT); }
            else foreach (var id in Ids(r["elementIds"]))
            {
                var bb = Get(_doc, id)?.get_BoundingBox(null); if (bb == null) continue;
                mn = mn == null ? bb.Min : new XYZ(Math.Min(mn.X, bb.Min.X), Math.Min(mn.Y, bb.Min.Y), Math.Min(mn.Z, bb.Min.Z));
                mx = mx == null ? bb.Max : new XYZ(Math.Max(mx.X, bb.Max.X), Math.Max(mx.Y, bb.Max.Y), Math.Max(mx.Z, bb.Max.Z));
            }
            if (!off && (mn == null || mx == null)) return Fail("要給 elementIds（框住這些元件）或 minMm／maxMm，或 off:true 關閉剖面框。");
            if (!off) { mn = mn! - new XYZ(m, m, m); mx = mx! + new XYZ(m, m, m); }
            var plan = new { view = v.Name, off, minMm = off ? null : Mm(mn!), maxMm = off ? null : Mm(mx!) };
            if (!apply) return Ok(new { preflight = true, plan });
            var res = RunTx(_doc, "MCP Section Box", () =>
            {
                if (off) { v.IsSectionBoxActive = false; return; }
                v.IsSectionBoxActive = true;
                v.SetSectionBox(new BoundingBoxXYZ { Min = mn!, Max = mx! });
            });
            return res.committed ? Ok(new { applied = true, plan }) : Fail("設定剖面框失敗：" + string.Join("；", res.errors));
        }

        // POST /views/elevations { planViewId, pointMm:[x,y], faces:["north"|"east"|"south"|"west"], scale?, namePrefix?, apply }
        // face＝要看的建築立面：north＝北向立面（看北側外牆，視線朝南）。
        public string Elevations(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            if (Get(_doc, (long?)r["planViewId"] ?? 0) is not ViewPlan plan) return Fail("planViewId 要是平面視圖（立面標記放在這張平面上）。");
            if (r["pointMm"] is not JArray pa || pa.Count < 2) return Fail("pointMm 必填：立面標記的位置 [x, y]（mm）。");
            var faces = Strings(r["faces"]).Select(s => s.ToLowerInvariant()).Distinct().ToList();
            if (faces.Count == 0) faces = new List<string> { "north", "east", "south", "west" };
            var look = new Dictionary<string, XYZ> { ["north"] = -XYZ.BasisY, ["south"] = XYZ.BasisY, ["east"] = -XYZ.BasisX, ["west"] = XYZ.BasisX };
            if (faces.Any(f => !look.ContainsKey(f))) return Fail("faces 只能是 north／east／south／west。");
            var vft = new FilteredElementCollector(_doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.Elevation);
            if (vft == null) return Fail("模型裡沒有立面視圖類型。");
            int scale = (int?)r["scale"] ?? 100; string prefix = (string?)r["namePrefix"] ?? "";
            var origin = new XYZ((double)pa[0] / FT, (double)pa[1] / FT, plan.GenLevel?.Elevation ?? 0);
            var pv = new { planView = plan.Name, pointMm = Mm(origin), faces, scale, viewType = vft.Name };
            if (!apply) return Ok(new { preflight = true, plan = pv, note = "north＝北向立面（看北側外牆，視線朝南），其餘類推。" });
            var made = new List<object>();
            var res = RunTx(_doc, "MCP Create Elevations", () =>
            {
                var marker = ElevationMarker.CreateElevationMarker(_doc, vft.Id, origin, scale);
                for (int i = 0; i < 4; i++)
                {
                    var ev = marker.CreateElevation(_doc, plan.Id, i);
                    var lookDir = -ev.ViewDirection;
                    var f = look.FirstOrDefault(kv => kv.Value.DotProduct(lookDir) > 0.99).Key;
                    if (f == null || !faces.Contains(f)) { _doc.Delete(ev.Id); continue; }
                    if (!string.IsNullOrWhiteSpace(prefix)) { try { ev.Name = prefix + f; } catch { } }
                    made.Add(new { viewId = ev.Id.Value, name = ev.Name, face = f });
                }
            });
            return res.committed ? Ok(new { applied = true, elevations = made, plan = pv, warnings = res.warnings })
                : Fail("建立立面失敗，已回滾：" + string.Join("；", res.errors));
        }

        // POST /views/callout { parentViewId, rectUv:[u0,v0,u1,v1], name?, scale?, apply }
        public string Callout(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            if (Get(_doc, (long?)r["parentViewId"] ?? 0) is not View pv || pv.IsTemplate || pv is View3D || pv is ViewSheet) return Fail("parentViewId 要是平面、剖面或立面視圖。");
            if (r["rectUv"] is not JArray ra || ra.Count != 4) return Fail("rectUv 要是 [u0,v0,u1,v1]（視圖平面座標 mm）。");
            double u0 = Math.Min((double)ra[0], (double)ra[2]), u1 = Math.Max((double)ra[0], (double)ra[2]), v0 = Math.Min((double)ra[1], (double)ra[3]), v1 = Math.Max((double)ra[1], (double)ra[3]);
            var depth = pv is ViewPlan vp && vp.GenLevel != null ? new XYZ(0, 0, vp.GenLevel.Elevation) : pv.Origin;
            var p1 = ToModel(pv, u0, v0, depth); var p2 = ToModel(pv, u1, v1, depth);
            string? name = (string?)r["name"]; int? scale = (int?)r["scale"];
            var plan = new { parent = pv.Name, rectUv = new[] { u0, v0, u1, v1 }, name, scale };
            if (!apply) return Ok(new { preflight = true, plan });
            long id = 0; string? finalName = null;
            var res = RunTx(_doc, "MCP Create Callout", () =>
            {
                var cv = ViewSection.CreateCallout(_doc, pv.Id, pv.GetTypeId(), p1, p2);
                if (!string.IsNullOrWhiteSpace(name)) cv.Name = name!;
                if (scale.HasValue) cv.Scale = scale.Value;
                id = cv.Id.Value; finalName = cv.Name;
            });
            return res.committed ? Ok(new { applied = true, viewId = id, name = finalName, plan })
                : Fail("建立詳圖索引失敗，已回滾：" + string.Join("；", res.errors));
        }
    }
}
