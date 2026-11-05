// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using static RevitMcpAddin.Handlers.PortUtil;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// MEP 接頭檢查、兩個接頭之間自動接管（直接或直角繞行）、全模型開口端稽核。
    /// （功能構想參考 MLabs Revit MCP 公開的工具清單，程式獨立實作）
    /// </summary>
    public class MepConnectorHandler
    {
        private const double FT = 304.8;
        private readonly Document _doc;
        public MepConnectorHandler(Document doc) => _doc = doc;

        private static ConnectorManager? Cm(Element? e) => e switch
        {
            MEPCurve mc => mc.ConnectorManager,
            FamilyInstance fi => fi.MEPModel?.ConnectorManager,
            _ => null,
        };

        private static object Dto(Connector c)
        {
            object? size = null, dir = null;
            try
            {
                size = c.Shape == ConnectorProfileType.Round ? new { diameterMm = Math.Round(c.Radius * 2 * FT) } : (object)new { widthMm = Math.Round(c.Width * FT), heightMm = Math.Round(c.Height * FT) };
            }
            catch { }
            try { var z = c.CoordinateSystem.BasisZ; dir = new[] { Math.Round(z.X, 3), Math.Round(z.Y, 3), Math.Round(z.Z, 3) }; } catch { }
            var others = new List<long>();
            try { foreach (Connector o in c.AllRefs) if (o.Owner != null && o.Owner.Id != c.Owner.Id && o.ConnectorType != ConnectorType.Logical) others.Add(o.Owner.Id.Value); } catch { }
            string? sys = null; try { sys = c.MEPSystem?.Name; } catch { }
            return new
            {
                connectorId = c.Id, domain = c.Domain.ToString(), shape = c.Shape.ToString(), type = c.ConnectorType.ToString(),
                originMm = new[] { Math.Round(c.Origin.X * FT), Math.Round(c.Origin.Y * FT), Math.Round(c.Origin.Z * FT) },
                direction = dir, size, isConnected = c.IsConnected, connectedTo = others.Distinct(), system = sys,
            };
        }

        // GET /mep/connectors?elementIds=1,2,3
        public string Inspect(NameValueCollection qs)
        {
            var ids = (qs["elementIds"] ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => long.TryParse(s, out var v) ? v : 0).Where(v => v > 0).ToList();
            if (ids.Count == 0) return Fail("elementIds 必填（逗號分隔）。");
            var rows = ids.Select(id =>
            {
                var e = Get(_doc, id); var cm = Cm(e);
                if (e == null) return (object)new { elementId = id, error = "找不到元件" };
                if (cm == null) return new { elementId = id, name = e.Name, category = e.Category?.Name, error = "這個元件沒有 MEP 接頭" };
                var list = new List<object>(); foreach (Connector c in cm.Connectors) if (c.ConnectorType != ConnectorType.Logical) list.Add(Dto(c));
                return new { elementId = id, name = e.Name, category = e.Category?.Name, connectors = list };
            }).ToList();
            return Ok(new { elements = rows });
        }

        // 接頭候選：有指定 connectorId 就只用那一個；否則取所有空的水管／風管端接頭
        private List<Connector> Candidates(JToken? spec)
        {
            if (spec == null) throw new ArgumentException("from 與 to 都必填。");
            long id = (long?)spec["elementId"] ?? 0;
            var cm = Cm(Get(_doc, id)) ?? throw new ArgumentException("元件 " + id + " 沒有 MEP 接頭。");
            var all = cm.Connectors.Cast<Connector>().Where(c => c.ConnectorType == ConnectorType.End).ToList();
            int? cid = (int?)spec["connectorId"];
            if (cid.HasValue) return new List<Connector> { all.FirstOrDefault(c => c.Id == cid.Value) ?? throw new ArgumentException($"元件 {id} 沒有接頭 {cid}。") };
            var free = all.Where(c => !c.IsConnected && (c.Domain == Domain.DomainPiping || c.Domain == Domain.DomainHvac)).ToList();
            if (free.Count == 0) throw new ArgumentException($"元件 {id} 沒有可用的空接頭（水管或風管）。");
            return free;
        }

        private static List<XYZ> Simplify(List<XYZ> pts)
        {
            var o = new List<XYZ>();
            foreach (var p in pts) if (o.Count == 0 || o[o.Count - 1].DistanceTo(p) > 1 / FT) o.Add(p);
            for (int i = o.Count - 2; i >= 1; i--)
            {
                var a = (o[i] - o[i - 1]).Normalize(); var b = (o[i + 1] - o[i]).Normalize();
                if (a.DotProduct(b) > 0.9999) o.RemoveAt(i);
            }
            return o;
        }

        // POST /mep/route
        // { from:{elementId, connectorId?}, to:{elementId, connectorId?}, path?:"orthogonal"|"direct", clearanceMm?:300,
        //   pipeType?, ductType?, systemType?, level?, diameterMm?, widthMm?, heightMm?, fittings?:true, apply }
        public string Route(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            Connector a, b;
            try
            {
                var fa = Candidates(r["from"]); var fb = Candidates(r["to"]);
                var pair = fa.SelectMany(x => fb.Select(y => (x, y)))
                    .Where(p => p.x.Owner.Id != p.y.Owner.Id && p.x.Domain == p.y.Domain)
                    .OrderBy(p => p.x.Origin.DistanceTo(p.y.Origin)).FirstOrDefault();
                if (pair.x == null) throw new ArgumentException("找不到可以配對的接頭（要是不同元件、同為水管或同為風管）。");
                a = pair.x; b = pair.y;   // 沒指定接頭時，取兩邊距離最近的一對
            }
            catch (ArgumentException ex) { return Fail(ex.Message); }
            if (a.Domain != b.Domain) return Fail($"兩個接頭領域不同（{a.Domain} / {b.Domain}）。");
            if (a.IsConnected || b.IsConnected) return Fail("指定的接頭已經接上了。");
            bool pipe = a.Domain == Domain.DomainPiping;
            if (!pipe && a.Domain != Domain.DomainHvac) return Fail("目前只支援水管與風管接頭。");

            string path = ((string?)r["path"] ?? "orthogonal").ToLowerInvariant();
            double cl = ((double?)r["clearanceMm"] ?? 300) / FT;
            bool fittings = (bool?)r["fittings"] ?? true;
            XYZ A = a.Origin, B = b.Origin;
            XYZ dA, dB; try { dA = a.CoordinateSystem.BasisZ; dB = b.CoordinateSystem.BasisZ; } catch { dA = (B - A).Normalize(); dB = (A - B).Normalize(); }
            List<XYZ> pts;
            if (path == "direct") pts = new List<XYZ> { A, B };
            else
            {
                var A1 = A + dA * cl; var B1 = B + dB * cl;
                pts = Simplify(new List<XYZ> { A, A1, new XYZ(B1.X, A1.Y, A1.Z), new XYZ(B1.X, B1.Y, A1.Z), B1, B });
            }

            ElementId typeId, sysTypeId; string typeName, sysName; var typeNames = new List<string>();
            if (pipe)
            {
                var types = new FilteredElementCollector(_doc).OfClass(typeof(PipeType)).Cast<PipeType>().ToList();
                var t = FindType(types, (string?)r["pipeType"]); if (t == null) return Fail("模型裡沒有水管類型。");
                var sts = new FilteredElementCollector(_doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().ToList();
                var st = FindType(sts, (string?)r["systemType"]); if (st == null) return Fail("模型裡沒有管路系統類型。");
                typeId = t.Id; typeName = $"{t.FamilyName}: {t.Name}"; sysTypeId = st.Id; sysName = st.Name; typeNames = TypeNames(types);
            }
            else
            {
                var types = new FilteredElementCollector(_doc).OfClass(typeof(DuctType)).Cast<DuctType>().ToList();
                var t = FindType(types, (string?)r["ductType"]); if (t == null) return Fail("模型裡沒有風管類型。");
                var sts = new FilteredElementCollector(_doc).OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>().ToList();
                var st = FindType(sts, (string?)r["systemType"]); if (st == null) return Fail("模型裡沒有風管系統類型。");
                typeId = t.Id; typeName = $"{t.FamilyName}: {t.Name}"; sysTypeId = st.Id; sysName = st.Name; typeNames = TypeNames(types);
            }
            var lv = FindLevel(_doc, (string?)r["level"], Math.Min(A.Z, B.Z));
            if (lv == null) return Fail("模型裡沒有樓層。");
            double? dia = (double?)r["diameterMm"], w = (double?)r["widthMm"], h = (double?)r["heightMm"];

            var plan = new
            {
                kind = pipe ? "pipe" : "duct", path, type = typeName, systemTypeForMiddleSegments = sysName, level = lv.Name,
                from = new { elementId = a.Owner.Id.Value, connectorId = a.Id }, to = new { elementId = b.Owner.Id.Value, connectorId = b.Id },
                pointsMm = pts.Select(p => new[] { Math.Round(p.X * FT), Math.Round(p.Y * FT), Math.Round(p.Z * FT) }), segments = pts.Count - 1, fittings,
                availableTypes = typeNames.Take(40),
            };
            if (!apply) return Ok(new { preflight = true, plan });

            var ids = new List<long>(); var fitWarn = new List<string>();
            var res = RunTx(_doc, "MCP Route MEP", () =>
            {
                var segs = new List<MEPCurve>();
                MEPCurve Mk(int i)
                {
                    var p0 = pts[i]; var p1 = pts[i + 1]; int n = pts.Count - 1;
                    if (n == 1) return pipe ? Pipe.Create(_doc, typeId, lv.Id, a, b) : Duct.Create(_doc, typeId, lv.Id, a, b);
                    if (i == 0) return pipe ? Pipe.Create(_doc, typeId, lv.Id, a, p1) : Duct.Create(_doc, typeId, lv.Id, a, p1);
                    if (i == n - 1) return pipe ? Pipe.Create(_doc, typeId, lv.Id, b, p0) : Duct.Create(_doc, typeId, lv.Id, b, p0);
                    var sid = segs.Count > 0 ? (segs[0].MEPSystem?.GetTypeId() ?? sysTypeId) : sysTypeId;
                    if (sid == ElementId.InvalidElementId) sid = sysTypeId;
                    return pipe ? Pipe.Create(_doc, sid, typeId, lv.Id, p0, p1) : Duct.Create(_doc, sid, typeId, lv.Id, p0, p1);
                }
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    var mc = Mk(i);
                    if (pipe && dia.HasValue) SetDouble(mc, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, dia.Value / FT);
                    if (!pipe && dia.HasValue) SetDouble(mc, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, dia.Value / FT);
                    if (!pipe && w.HasValue) SetDouble(mc, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, w.Value / FT);
                    if (!pipe && h.HasValue) SetDouble(mc, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, h.Value / FT);
                    segs.Add(mc); ids.Add(mc.Id.Value);
                }
                if (!fittings) return;
                for (int i = 0; i + 1 < segs.Count; i++)
                {
                    var at = pts[i + 1];
                    Connector? Near(MEPCurve m) => m.ConnectorManager.Connectors.Cast<Connector>().Where(c => c.ConnectorType == ConnectorType.End && !c.IsConnected).OrderBy(c => c.Origin.DistanceTo(at)).FirstOrDefault();
                    var c1 = Near(segs[i]); var c2 = Near(segs[i + 1]);
                    if (c1 == null || c2 == null) { fitWarn.Add($"第 {i + 1} 個轉角找不到空接頭"); continue; }
                    try { var f = _doc.Create.NewElbowFitting(c1, c2); if (f != null) ids.Add(f.Id.Value); }
                    catch (Exception ex) { fitWarn.Add($"第 {i + 1} 個轉角沒放彎頭：{ex.Message}（請檢查管路類型的佈線偏好設定）"); }
                }
            });
            return res.committed
                ? Ok(new { applied = true, createdIds = ids, plan, fittingWarnings = fitWarn, warnings = res.warnings })
                : Fail("接管失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }

        // GET /mep/audit?limit=200
        public string Audit(NameValueCollection qs)
        {
            int limit = int.TryParse(qs["limit"], out var l) ? Math.Max(1, l) : 200;
            var openEnds = new List<object>(); var noSystem = new List<object>();
            var byCat = new Dictionary<string, int>(); int curves = 0, fixtures = 0, openCount = 0, noSysCount = 0;
            foreach (var e in new FilteredElementCollector(_doc).WhereElementIsNotElementType().Where(e => e is MEPCurve || (e is FamilyInstance fi && fi.MEPModel?.ConnectorManager != null)))
            {
                var cm = Cm(e); if (cm == null) continue;
                if (e is MEPCurve) curves++; else fixtures++;
                foreach (Connector c in cm.Connectors)
                {
                    if (c.ConnectorType != ConnectorType.End) continue;
                    if (c.Domain != Domain.DomainPiping && c.Domain != Domain.DomainHvac && c.Domain != Domain.DomainCableTrayConduit) continue;
                    if (!c.IsConnected)
                    {
                        openCount++; string cat = e.Category?.Name ?? "?"; byCat[cat] = byCat.TryGetValue(cat, out var n) ? n + 1 : 1;
                        if (openEnds.Count < limit) openEnds.Add(new { elementId = e.Id.Value, category = cat, name = e.Name, connectorId = c.Id, originMm = new[] { Math.Round(c.Origin.X * FT), Math.Round(c.Origin.Y * FT), Math.Round(c.Origin.Z * FT) } });
                    }
                    bool sysDomain = c.Domain == Domain.DomainPiping || c.Domain == Domain.DomainHvac;
                    MEPSystem? sys = null; try { sys = c.MEPSystem; } catch { }
                    if (sysDomain && e is FamilyInstance && sys == null)
                    {
                        noSysCount++;
                        if (noSystem.Count < limit) noSystem.Add(new { elementId = e.Id.Value, category = e.Category?.Name, name = e.Name, connectorId = c.Id, domain = c.Domain.ToString() });
                    }
                }
            }
            return Ok(new
            {
                scanned = new { mepCurves = curves, familyInstancesWithConnectors = fixtures },
                openEnds = new { count = openCount, byCategory = byCat.OrderByDescending(k => k.Value).ToDictionary(k => k.Key, k => k.Value), sample = openEnds },
                equipmentConnectorsWithoutSystem = new { count = noSysCount, sample = noSystem },
                note = "開口端包含刻意留設的管端（例如預留管）；請依位置判斷是否需要處理。",
            });
        }
    }
}
