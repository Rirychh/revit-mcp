// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static RevitMcpAddin.Handlers.PortUtil;
using StairsEl = Autodesk.Revit.DB.Architecture.Stairs;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// v1.10 A：樓梯（直跑／L 型／U 型）、欄杆（沿路徑或掛在樓梯）、輪廓族（.rfa）、掃掠（牆飾條／分割縫、樓板邊緣、封簷板、排水溝）。
    /// 全部 apply=false 預檢；apply=true 單一交易（樓梯用 StairsEditScope），任何錯誤整批回滾。
    /// </summary>
    public class StairsSweepHandler
    {
        private const double FT = 304.8;
        private readonly Document _doc;
        public StairsSweepHandler(Document doc) => _doc = doc;

        private Level? LevelExact(string? name) => FindLevelStrict(_doc, name);

        private string LevelNames() => string.Join("、", Levels(_doc).Select(l => l.Name));

        private static List<XYZ> Pts(JToken? t, double zFt) => PolyMm(t, zFt, dropClosing: false);

        private static CurveLoop Rect(XYZ o, XYZ u, XYZ v, double lu, double lv0, double lv1, double z)
        {
            XYZ P(double a, double b) { var q = o + u * a + v * b; return new XYZ(q.X, q.Y, z); }
            var c = new[] { P(0, lv0), P(lu, lv0), P(lu, lv1), P(0, lv1) };
            var cl = new CurveLoop();
            for (int i = 0; i < 4; i++) cl.Append(Line.CreateBound(c[i], c[(i + 1) % 4]));
            return cl;
        }

        private List<RailingType> RailTypes() =>
            new FilteredElementCollector(_doc).OfClass(typeof(RailingType)).Cast<RailingType>().ToList();

        // ── 樓梯 ─────────────────────────────────────────────
        // POST /stairs { baseLevel, topLevel, startMm:[x,y], directionDeg?:0, shape?:"straight"|"L"|"U", turn?:"left"|"right",
        //                widthMm?:1200, firstRunRisers?, treadDepthMm?, landingDepthMm?, gapMm?:0, stairsType?, railings?:false, railingType?, apply }
        public string Stairs(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var bl = LevelExact((string?)r["baseLevel"]); var tl = LevelExact((string?)r["topLevel"]);
            if (bl == null || tl == null) return Fail("需要 baseLevel、topLevel（樓層名稱）。可用：" + LevelNames());
            double h = tl.Elevation - bl.Elevation;
            if (h <= 0) return Fail("topLevel 要比 baseLevel 高。");
            var s0 = Pt(r["startMm"]);
            if (s0 == null) return Fail("startMm 要是 [x, y]（mm），第一跑起點的中心線位置。");
            string shape = ((string?)r["shape"] ?? "straight").ToUpperInvariant();
            if (shape != "STRAIGHT" && shape != "L" && shape != "U") return Fail("shape 要是 straight、L 或 U。");
            double ang = ((double?)r["directionDeg"] ?? 0) * Math.PI / 180;
            var d = new XYZ(Math.Cos(ang), Math.Sin(ang), 0); var p = new XYZ(-d.Y, d.X, 0);   // p：d 左轉 90°
            double sgn = string.Equals((string?)r["turn"], "right", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
            double w = ((double?)r["widthMm"] ?? 1200) / FT;

            var types = new FilteredElementCollector(_doc).OfClass(typeof(StairsType)).Cast<StairsType>().ToList();
            var st = string.IsNullOrWhiteSpace((string?)r["stairsType"]) ? null : FindType(types, (string?)r["stairsType"]);
            if (!string.IsNullOrWhiteSpace((string?)r["stairsType"]) && st == null) return Fail("找不到樓梯類型。可用：" + string.Join("、", TypeNames(types)));
            var refType = st ?? types.FirstOrDefault();
            if (refType == null) return Fail("模型裡沒有樓梯類型（需要組合樓梯類型）。");
            double maxR = refType.MaxRiserHeight; double tread = ((double?)r["treadDepthMm"] ?? 0) / FT;
            if (tread <= 0) tread = refType.MinTreadDepth;
            int n = (int)Math.Ceiling(h / maxR - 1e-6); if (n < 2) n = 2;
            int n1 = shape == "STRAIGHT" ? n : Math.Max(1, Math.Min(n - 1, (int?)r["firstRunRisers"] ?? (int)Math.Ceiling(n / 2.0)));
            int n2 = n - n1;
            double ld = ((double?)r["landingDepthMm"] ?? 0) / FT; if (ld <= 0) ld = w;
            double gap = ((double?)r["gapMm"] ?? 0) / FT;
            double L1 = (n1 - 1) * tread, L2 = Math.Max(0, (n2 - 1) * tread);
            var e1 = s0 + d * L1;

            bool? rails = (bool?)r["railings"];          // null／true＝保留 Revit 自動加的欄杆；false＝刪掉
            var rts = RailTypes();
            RailingType? rt = null;
            if (!string.IsNullOrWhiteSpace((string?)r["railingType"]))
            {
                rt = FindType(rts, (string?)r["railingType"]);
                if (rt == null) return Fail("找不到欄杆類型。可用：" + string.Join("、", TypeNames(rts)));
            }

            var plan = new
            {
                shape, baseLevel = bl.Name, topLevel = tl.Name, heightMm = Math.Round(h * FT), risers = n, riserHeightMm = Math.Round(h / n * FT, 1),
                treadDepthMm = Math.Round(tread * FT, 1), widthMm = Math.Round(w * FT), runs = shape == "STRAIGHT" ? new[] { n } : new[] { n1, n2 },
                run1 = new { fromMm = Mm(s0), toMm = Mm(e1), lengthMm = Math.Round(L1 * FT) },
                stairsType = $"{refType.FamilyName}: {refType.Name}", typeMaxRiserMm = Math.Round(maxR * FT, 1),
                railings = rails == false ? "刪除自動欄杆" : "保留自動欄杆", railingType = rt == null ? "（沿用專案預設）" : $"{rt.FamilyName}: {rt.Name}",
                availableStairsTypes = TypeNames(types).Take(30),
            };
            if (!apply) return Ok(new { preflight = true, plan });

            var pre = new RollbackOnError(); long sid = 0; var info = new List<object>();
            try
            {
                using (var scope = new StairsEditScope(_doc, "MCP Create Stairs"))
                {
                    var stairsId = scope.Start(bl.Id, tl.Id);
                    sid = stairsId.Value;
                    using (var t = new Transaction(_doc, "MCP Stairs Runs"))
                    {
                        t.Start();
                        var fo = t.GetFailureHandlingOptions(); fo.SetFailuresPreprocessor(new RollbackOnError()); t.SetFailureHandlingOptions(fo);
                        var stairs = (StairsEl)_doc.GetElement(stairsId);
                        if (st != null && stairs.GetTypeId() != st.Id) stairs.ChangeTypeId(st.Id);
                        stairs.DesiredRisersNumber = n;
                        stairs.ActualTreadDepth = tread;
                        double z0 = bl.Elevation;   // 梯段定位線的 Z 用模型高程（底部樓層高程）
                        var run1 = StairsRun.CreateStraightRun(_doc, stairsId, Line.CreateBound(new XYZ(s0.X, s0.Y, z0), new XYZ(e1.X, e1.Y, z0)), StairsRunJustification.Center);
                        run1.ActualRunWidth = w;
                        if (shape != "STRAIGHT")
                        {
                            // TopElevation 與 BaseElevation 同一個基準；換算成模型高程給第二跑
                            double z = z0 + (run1.TopElevation - run1.BaseElevation);
                            double landElev = run1.TopElevation;
                            CurveLoop land; XYZ s2, dir2;
                            if (shape == "L")
                            {
                                land = Rect(e1, d, p, w, -w / 2, w / 2, z);
                                s2 = e1 + d * (w / 2) + p * (sgn * w / 2); dir2 = p * sgn;
                            }
                            else
                            {
                                double a0 = sgn > 0 ? -w / 2 : -(1.5 * w + gap), a1 = sgn > 0 ? 1.5 * w + gap : w / 2;
                                land = Rect(e1, d, p, ld, a0, a1, z);
                                s2 = e1 + p * (sgn * (w + gap)); dir2 = -d;
                            }
                            StairsLanding.CreateSketchedLanding(_doc, stairsId, land, landElev);
                            if (n2 > 1)
                            {
                                var e2 = s2 + dir2 * L2;
                                var run2 = StairsRun.CreateStraightRun(_doc, stairsId, Line.CreateBound(new XYZ(s2.X, s2.Y, z), new XYZ(e2.X, e2.Y, z)), StairsRunJustification.Center);
                                run2.ActualRunWidth = w;
                            }
                        }
                        t.Commit();
                    }
                    scope.Commit(pre);
                    if (pre.HadError || _doc.GetElement(stairsId) == null)
                        return Fail("建立樓梯失敗，已回滾、模型沒有變更：" + string.Join("；", pre.Errors.DefaultIfEmpty("樓梯編輯未提交")));
                }
            }
            catch (Exception ex) { return Fail("建立樓梯失敗，已回滾、模型沒有變更：" + ex.Message); }

            var made = _doc.GetElement(ElementIdCompat.NewId(sid)) as StairsEl;
            var result = new
            {
                stairsId = sid, actualRisers = made?.ActualRisersNumber, actualRiserHeightMm = made == null ? (double?)null : Math.Round(made.ActualRiserHeight * FT, 1),
                actualTreadDepthMm = made == null ? (double?)null : Math.Round(made.ActualTreadDepth * FT, 1),
                runs = made?.GetStairsRuns().Select(id => _doc.GetElement(id) as StairsRun).Where(x => x != null).Select(x => new { id = x!.Id.Value, risers = x.ActualRisersNumber, widthMm = Math.Round(x.ActualRunWidth * FT) }),
            };
            List<long> railIds = made?.GetAssociatedRailings().Select(x => x.Value).ToList() ?? new List<long>(); List<string> railWarn = new List<string>();
            if (made != null && (rails == false || rt != null || (rails == true && railIds.Count == 0)))
            {
                var res = RunTx(_doc, "MCP Stairs Railings", () =>
                {
                    if (rails == false) { foreach (var id in railIds) _doc.Delete(ElementIdCompat.NewId(id)); railIds.Clear(); return; }
                    if (railIds.Count == 0)
                    {
                        var t2 = rt ?? rts.FirstOrDefault() ?? throw new InvalidOperationException("模型裡沒有欄杆類型。");
                        foreach (var id in Railing.Create(_doc, ElementIdCompat.NewId(sid), t2.Id, RailingPlacementPosition.Treads)) railIds.Add(id.Value);
                    }
                    else if (rt != null) foreach (var id in railIds) _doc.GetElement(ElementIdCompat.NewId(id)).ChangeTypeId(rt.Id);
                });
                if (!res.committed) railWarn.Add("樓梯已建立，但欄杆處理失敗：" + string.Join("；", res.errors));
            }
            return Ok(new { applied = true, result, plan, railingIds = railIds, warnings = pre.SwallowedWarnings.Concat(railWarn) });
        }

        // ── 欄杆 ─────────────────────────────────────────────
        // POST /railings { stairsIds?:[...] | pointsMm?:[[x,y],...] + level, railingType?, position?:"treads"|"stringer", apply }
        public string Railings(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var rts = RailTypes();
            var rt = FindType(rts, (string?)r["railingType"]);
            if (rt == null) return Fail(rts.Count == 0 ? "模型裡沒有欄杆類型。" : "找不到欄杆類型。可用：" + string.Join("、", TypeNames(rts)));
            var pos = string.Equals((string?)r["position"], "stringer", StringComparison.OrdinalIgnoreCase) ? RailingPlacementPosition.Stringer : RailingPlacementPosition.Treads;
            var stairsIds = Ids(r["stairsIds"]);
            object plan; Action<List<long>> make;
            if (stairsIds.Count > 0)
            {
                var bad = stairsIds.Where(id => Get(_doc, id) is not StairsEl).ToList();
                if (bad.Count > 0) return Fail("這些 id 不是樓梯：" + string.Join("、", bad));
                var existing = stairsIds.ToDictionary(id => id, id => ((StairsEl)Get(_doc, id)!).GetAssociatedRailings().ToList());
                bool explicitType = !string.IsNullOrWhiteSpace((string?)r["railingType"]);
                plan = new
                {
                    mode = "stairs", position = pos.ToString(), railingType = $"{rt.FamilyName}: {rt.Name}", availableTypes = TypeNames(rts).Take(30),
                    stairs = stairsIds.Select(id => new { stairsId = id, existingRailings = existing[id].Count, action = existing[id].Count == 0 ? "新增" : explicitType ? "換成指定類型" : "已有欄杆，略過（給 railingType 可換類型）" }),
                };
                make = ids =>
                {
                    foreach (var s in stairsIds)
                    {
                        if (existing[s].Count == 0) foreach (var id in Railing.Create(_doc, ElementIdCompat.NewId(s), rt.Id, pos)) ids.Add(id.Value);
                        else if (explicitType) foreach (var id in existing[s]) { _doc.GetElement(id).ChangeTypeId(rt.Id); ids.Add(id.Value); }
                    }
                };
            }
            else
            {
                var lv = LevelExact((string?)r["level"]);
                if (lv == null) return Fail("沿路徑建欄杆需要 level。可用：" + LevelNames());
                var pts = Pts(r["pointsMm"], lv.Elevation);
                if (pts.Count < 2) return Fail("pointsMm 至少 2 點（欄杆路徑，可為折線）；或改給 stairsIds。");
                var cl = new CurveLoop();
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    if (pts[i].DistanceTo(pts[i + 1]) < 10 / FT) return Fail($"第 {i} 與第 {i + 1} 點太近。");
                    cl.Append(Line.CreateBound(pts[i], pts[i + 1]));
                }
                double len = pts.Zip(pts.Skip(1), (a, b) => a.DistanceTo(b)).Sum() * FT;
                plan = new { mode = "path", level = lv.Name, segments = pts.Count - 1, lengthMm = Math.Round(len), railingType = $"{rt.FamilyName}: {rt.Name}", availableTypes = TypeNames(rts).Take(30) };
                make = ids => ids.Add(Railing.Create(_doc, cl, rt.Id, lv.Id).Id.Value);
            }
            if (!apply) return Ok(new { preflight = true, plan });
            var made = new List<long>();
            var res = RunTx(_doc, "MCP Create Railings", () => make(made));
            return res.committed ? Ok(new { applied = true, ids = made, plan, warnings = res.warnings })
                : Fail("建立欄杆失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }

        // ── 輪廓族 ───────────────────────────────────────────
        // POST /families/profile { name, pointsMm:[[x,y],...]（封閉迴圈，x 水平、y 垂直，原點＝插入點）, template?, folder?, load?:true, apply }
        public string ProfileFamily(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            string name = ((string?)r["name"] ?? "").Trim();
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return Fail("name 必填，且不能含檔名不允許的字元。");
            var pts = Pts(r["pointsMm"], 0);
            if (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) < 0.5 / FT) pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3) return Fail("pointsMm 至少 3 點（封閉輪廓，首尾會自動相接）。");
            for (int i = 0; i < pts.Count; i++)
                if (pts[i].DistanceTo(pts[(i + 1) % pts.Count]) < 1 / FT) return Fail($"第 {i} 點與下一點太近（< 1 mm）。");
            if (SelfIntersects(pts)) return Fail("輪廓自相交，請調整點位順序。");
            string tpl;
            try { tpl = FindTemplate((string?)r["template"]); } catch (Exception ex) { return Fail(ex.Message); }
            string folder = (string?)r["folder"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "revit-mcp", "profiles");
            name = Naming.Prefixed(name, "profile");
            string path = Path.Combine(folder, name + ".rfa");
            bool load = (bool?)r["load"] ?? true;
            var exists = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>().Any(f => f.Name == name);
            double area = 0; for (int i = 0; i < pts.Count; i++) { var a = pts[i]; var b = pts[(i + 1) % pts.Count]; area += a.X * b.Y - b.X * a.Y; }
            var plan = new { name, template = tpl, savePath = path, load, vertices = pts.Count, areaMm2 = Math.Round(Math.Abs(area) / 2 * FT * FT), replacesExistingFamily = exists && load };
            if (!apply) return Ok(new { preflight = true, plan });

            Document? fdoc = null;
            try
            {
                Directory.CreateDirectory(folder);
                fdoc = _doc.Application.NewFamilyDocument(tpl);
                var view = new FilteredElementCollector(fdoc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(v => !v.IsTemplate)
                           ?? throw new InvalidOperationException("輪廓樣板裡找不到平面視圖。");
                using (var t = new Transaction(fdoc, "MCP Profile"))
                {
                    t.Start();
                    for (int i = 0; i < pts.Count; i++) fdoc.FamilyCreate.NewDetailCurve(view, Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
                    t.Commit();
                }
                fdoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            }
            catch (Exception ex) { return Fail("建立輪廓族失敗、專案沒有變更：" + ex.Message); }
            finally { try { fdoc?.Close(false); } catch { } }

            if (!load) return Ok(new { applied = true, savedTo = path, loaded = false, plan });
            long famId = 0; var types = new List<string>();
            var res = RunTx(_doc, "MCP Load Profile", () =>
            {
                if (_doc.LoadFamily(path, FamilyLoad.Overwrite, out Family fam) && fam != null)
                {
                    famId = fam.Id.Value;
                    foreach (var id in fam.GetFamilySymbolIds()) types.Add($"{fam.Name}: {_doc.GetElement(id).Name}");
                }
                else
                {
                    var f2 = new FilteredElementCollector(_doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == name);
                    if (f2 != null) { famId = f2.Id.Value; foreach (var id in f2.GetFamilySymbolIds()) types.Add($"{f2.Name}: {_doc.GetElement(id).Name}"); }
                }
            });
            return res.committed ? Ok(new { applied = true, savedTo = path, loaded = true, familyId = famId, profileTypes = types, plan, warnings = res.warnings })
                : Fail($"輪廓族已存到 {path}，但載入專案失敗、專案沒有變更：" + string.Join("；", res.errors));
        }

        private string FindTemplate(string? tpl)
        {
            if (!string.IsNullOrWhiteSpace(tpl) && File.Exists(tpl)) return tpl!;
            var root = _doc.Application.FamilyTemplatePath;
            var names = new List<string>();
            if (!string.IsNullOrWhiteSpace(tpl)) names.Add(tpl!.EndsWith(".rft", StringComparison.OrdinalIgnoreCase) ? tpl! : tpl + ".rft");
            names.AddRange(new[] { "公制輪廓.rft", "Metric Profile.rft", "Profile.rft" });
            var dirs = new List<string>();
            if (Directory.Exists(root)) { dirs.Add(root); var par = Directory.GetParent(root)?.FullName; if (par != null) dirs.Add(par); }
            foreach (var n in names)
                foreach (var d in dirs)
                {
                    var hit = Directory.GetFiles(d, n, SearchOption.AllDirectories).FirstOrDefault();
                    if (hit != null) return hit;
                }
            throw new FileNotFoundException($"找不到輪廓族樣板（公制輪廓.rft／Metric Profile.rft），樣板資料夾：{root}。可用 template 指定完整路徑。");
        }

        private static bool SelfIntersects(List<XYZ> p)
        {
            int n = p.Count;
            double Cr(XYZ a, XYZ b, XYZ c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (j == i + 1 || (i == 0 && j == n - 1)) continue;
                    XYZ a = p[i], b = p[(i + 1) % n], c = p[j], d = p[(j + 1) % n];
                    if (Cr(a, b, c) * Cr(a, b, d) < 0 && Cr(c, d, a) * Cr(c, d, b) < 0) return true;
                }
            return false;
        }

        // ── 掃掠 ─────────────────────────────────────────────
        // POST /sweeps
        //  kind:"wallSweep"|"wallReveal" { wallIds, typeName?, profile?（輪廓族: 類型）, heightMm?:0, measuredFrom?:"base"|"top", side?:"exterior"|"interior", offsetMm?:0, vertical?:false, apply }
        //  kind:"slabEdge"            { hostIds（樓板）, typeName?, apply }  沿樓板頂面外圍每一條邊
        //  kind:"fascia"|"gutter"     { hostIds（屋頂）, typeName?, apply }  沿屋頂最低的水平邊（簷口）
        public string Sweeps(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            string kind = ((string?)r["kind"] ?? "").Trim();
            string k = kind.ToLowerInvariant();
            if (k == "wallsweep" || k == "wallreveal") return WallSweeps(r, k == "wallreveal", apply);
            if (k != "slabedge" && k != "fascia" && k != "gutter") return Fail("kind 要是 wallSweep、wallReveal、slabEdge、fascia 或 gutter。");

            List<ElementType> types = k switch
            {
                "slabedge" => new FilteredElementCollector(_doc).OfClass(typeof(SlabEdgeType)).Cast<ElementType>().ToList(),
                "fascia" => new FilteredElementCollector(_doc).OfClass(typeof(FasciaType)).Cast<ElementType>().ToList(),
                _ => new FilteredElementCollector(_doc).OfClass(typeof(GutterType)).Cast<ElementType>().ToList(),
            };
            var et = FindType(types, (string?)r["typeName"]);
            if (et == null) return Fail(types.Count == 0 ? $"模型裡沒有 {kind} 類型。" : "找不到類型。可用：" + string.Join("、", TypeNames(types)));
            var hosts = Ids(r["hostIds"]);
            if (hosts.Count == 0) return Fail("hostIds 必填（slabEdge 給樓板 id；fascia／gutter 給屋頂 id）。");
            var plan = new List<object>(); var bad = new List<object>(); var refs = new List<(long host, ReferenceArray ra)>();
            foreach (var hid in hosts)
            {
                var el = Get(_doc, hid);
                bool okHost = k == "slabedge" ? el is Floor : el is RoofBase;
                if (!okHost) { bad.Add(new { id = hid, reason = k == "slabedge" ? "不是樓板" : "不是屋頂" }); continue; }
                var edges = k == "slabedge" ? FloorTopEdges(el!) : RoofEaveEdges(el!);
                if (edges.Count == 0) { bad.Add(new { id = hid, reason = "找不到可用的邊" }); continue; }
                var ra = new ReferenceArray(); foreach (var e in edges) ra.Append(e.Reference);
                refs.Add((hid, ra));
                plan.Add(new { hostId = hid, edges = edges.Count, lengthMm = Math.Round(edges.Sum(e => e.ApproximateLength) * FT) });
            }
            var summary = new { kind, typeName = $"{et.FamilyName}: {et.Name}", hosts = plan, skipped = bad, availableTypes = TypeNames(types).Take(30) };
            if (refs.Count == 0) return Fail("沒有可建立的掃掠：" + Newtonsoft.Json.JsonConvert.SerializeObject(bad));
            if (!apply) return Ok(new { preflight = true, summary });
            var ids = new List<long>();
            var res = RunTx(_doc, "MCP Create Sweeps", () =>
            {
                foreach (var (_, ra) in refs)
                {
                    Element made = k switch
                    {
                        "slabedge" => _doc.Create.NewSlabEdge((SlabEdgeType)et, ra),
                        "fascia" => _doc.Create.NewFascia((FasciaType)et, ra),
                        _ => _doc.Create.NewGutter((GutterType)et, ra),
                    };
                    ids.Add(made.Id.Value);
                }
            });
            return res.committed ? Ok(new { applied = true, ids, summary, warnings = res.warnings })
                : Fail("建立掃掠失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }

        private string WallSweeps(JObject r, bool reveal, bool apply)
        {
            var cat = reveal ? BuiltInCategory.OST_Reveals : BuiltInCategory.OST_Cornices;
            var types = new FilteredElementCollector(_doc).OfCategory(cat).WhereElementIsElementType().Cast<ElementType>().ToList();
            var et = FindType(types, (string?)r["typeName"]);
            if (et == null) return Fail(types.Count == 0 ? (reveal ? "模型裡沒有分割縫類型。" : "模型裡沒有牆飾條類型。") : "找不到類型。可用：" + string.Join("、", TypeNames(types)));
            var profs = new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_ProfileFamilies).Cast<FamilySymbol>().ToList();
            FamilySymbol? prof = null;
            if (!string.IsNullOrWhiteSpace((string?)r["profile"]))
            {
                prof = FindType(profs, (string?)r["profile"]);
                if (prof == null) return Fail("找不到輪廓。可用：" + string.Join("、", TypeNames(profs).Take(60)));
            }
            var walls = new List<Wall>(); var bad = new List<long>();
            foreach (var id in Ids(r["wallIds"])) { if (Get(_doc, id) is Wall w) walls.Add(w); else bad.Add(id); }
            if (walls.Count == 0) return Fail("wallIds 必填（牆 id）。" + (bad.Count > 0 ? "不是牆：" + string.Join("、", bad) : ""));
            double hgt = ((double?)r["heightMm"] ?? 0) / FT, off = ((double?)r["offsetMm"] ?? 0) / FT;
            bool fromTop = string.Equals((string?)r["measuredFrom"], "top", StringComparison.OrdinalIgnoreCase);
            bool interior = string.Equals((string?)r["side"], "interior", StringComparison.OrdinalIgnoreCase);
            bool vertical = (bool?)r["vertical"] ?? false;
            var summary = new
            {
                kind = reveal ? "wallReveal" : "wallSweep", typeName = $"{et.FamilyName}: {et.Name}", profile = prof == null ? "（沿用類型的輪廓）" : $"{prof.FamilyName}: {prof.Name}",
                walls = walls.Count, notWalls = bad, heightMm = Math.Round(hgt * FT), measuredFrom = fromTop ? "top" : "base", side = interior ? "interior" : "exterior", vertical,
                availableTypes = TypeNames(types).Take(30), availableProfiles = TypeNames(profs).Take(40),
            };
            if (!apply) return Ok(new { preflight = true, summary });
            var ids = new List<long>();
            var res = RunTx(_doc, "MCP Wall Sweeps", () =>
            {
                if (prof != null && !prof.IsActive) { prof.Activate(); _doc.Regenerate(); }
                foreach (var w in walls)
                {
                    var info = new WallSweepInfo(reveal ? WallSweepType.Reveal : WallSweepType.Sweep, vertical)
                    {
                        Distance = hgt,
                        DistanceMeasuredFrom = fromTop ? DistanceMeasuredFrom.Top : DistanceMeasuredFrom.Base,
                        WallSide = interior ? WallSide.Interior : WallSide.Exterior,
                        WallOffset = off,
                    };
                    if (prof != null) info.ProfileId = prof.Id;
                    ids.Add(WallSweep.Create(w, et.Id, info).Id.Value);
                }
            });
            return res.committed ? Ok(new { applied = true, ids, summary, warnings = res.warnings })
                : Fail("建立牆飾條／分割縫失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }

        private static IEnumerable<Solid> Solids(Element el)
        {
            var ge = el.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine });
            if (ge == null) yield break;
            foreach (var o in ge)
            {
                if (o is Solid s && s.Faces.Size > 0) yield return s;
                else if (o is GeometryInstance gi) foreach (var o2 in gi.GetInstanceGeometry()) if (o2 is Solid s2 && s2.Faces.Size > 0) yield return s2;
            }
        }

        /// <summary>樓板頂面（面積最大、朝上的平面）的外圍迴圈邊。</summary>
        private static List<Edge> FloorTopEdges(Element el)
        {
            PlanarFace? top = null;
            foreach (var s in Solids(el))
                foreach (Face f in s.Faces)
                    if (f is PlanarFace pf && pf.FaceNormal.Z > 0.9 && (top == null || pf.Area > top.Area)) top = pf;
            if (top == null) return new List<Edge>();
            EdgeArray? outer = null; double best = -1;
            foreach (EdgeArray loop in top.EdgeLoops)
            {
                double len = 0; foreach (Edge e in loop) len += e.ApproximateLength;
                if (len > best) { best = len; outer = loop; }
            }
            var o = new List<Edge>(); if (outer != null) foreach (Edge e in outer) if (e.Reference != null) o.Add(e);
            return o;
        }

        /// <summary>屋頂朝上各面上、高程最低的水平邊（簷口）。</summary>
        private static List<Edge> RoofEaveEdges(Element el)
        {
            var cand = new List<(Edge e, double z)>();
            foreach (var s in Solids(el))
                foreach (Face f in s.Faces)
                {
                    if (f is not PlanarFace pf || pf.FaceNormal.Z < 0.05) continue;
                    foreach (EdgeArray loop in pf.EdgeLoops)
                        foreach (Edge e in loop)
                        {
                            var c = e.AsCurve(); var a = c.GetEndPoint(0); var b = c.GetEndPoint(1);
                            if (Math.Abs(a.Z - b.Z) < 1 / FT && e.Reference != null) cand.Add((e, a.Z));
                        }
                }
            if (cand.Count == 0) return new List<Edge>();
            double zmin = cand.Min(x => x.z);
            var seen = new HashSet<string>(); var o = new List<Edge>();
            foreach (var (e, z) in cand)
            {
                if (z > zmin + 1 / FT) continue;
                var c = e.AsCurve(); var m = c.Evaluate(0.5, true);
                if (seen.Add($"{Math.Round(m.X * FT)},{Math.Round(m.Y * FT)}")) o.Add(e);
            }
            return o;
        }
    }
}
