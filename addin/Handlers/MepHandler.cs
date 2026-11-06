// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using static RevitMcpAddin.Handlers.PortUtil;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// MEP：風管、水管（兩點一段）、把設備／末端的接頭組成系統。
    /// 全部 apply=false 預檢（解析類型、系統、樓層、尺寸，不開交易）；apply=true 單一交易，任何錯誤整批回滾。
    /// </summary>
    public class MepHandler
    {
        private readonly Document _doc;
        public MepHandler(Document doc) => _doc = doc;

        private class Seg { public XYZ A = XYZ.Zero, B = XYZ.Zero; public int Index; }

        private static (List<Seg> ok, List<object> bad) Segments(JToken? t)
        {
            var ok = new List<Seg>(); var bad = new List<object>(); int i = 0;
            foreach (var s in (t as JArray) ?? new JArray())
            {
                var a = Pt(s["start"]); var b = Pt(s["end"]);
                if (a == null || b == null) bad.Add(new { index = i, reason = "start/end 要是 [x,y,z]（mm）" });
                else if (a.DistanceTo(b) < 10 * FtPerMm) bad.Add(new { index = i, reason = "長度小於 10 mm" });
                else ok.Add(new Seg { A = a, B = b, Index = i });
                i++;
            }
            return (ok, bad);
        }

        // POST /mep/ducts
        // { segments:[{start:[x,y,z],end:[x,y,z]}], ductType?, systemType?, level?,
        //   widthMm?, heightMm?, diameterMm?, apply:false }
        public string Ducts(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var (segs, bad) = Segments(r["segments"]);
            if (segs.Count == 0) return Fail("segments 沒有可用的線段。" + (bad.Count > 0 ? " 問題：" + Newtonsoft.Json.JsonConvert.SerializeObject(bad) : ""));

            var types = new FilteredElementCollector(_doc).OfClass(typeof(DuctType)).Cast<DuctType>().ToList();
            var sysTypes = new FilteredElementCollector(_doc).OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>().ToList();
            var dt = FindType(types, (string?)r["ductType"]);
            var st = FindType(sysTypes, (string?)r["systemType"]);
            if (dt == null) return Fail("模型裡沒有風管類型（請先載入機電樣板或風管類型）。");
            if (st == null) return Fail("模型裡沒有風管系統類型（MechanicalSystemType）。");
            if (!string.IsNullOrWhiteSpace((string?)r["ductType"]) && !MatchesName(dt, (string?)r["ductType"]))
                return Fail("找不到風管類型「" + r["ductType"] + "」。可用：" + string.Join("、", TypeNames(types)));

            var lv = FindLevel(_doc, (string?)r["level"], segs.Min(s => Math.Min(s.A.Z, s.B.Z)));
            if (lv == null) return Fail("模型裡沒有樓層。");
            double? w = (double?)r["widthMm"], h = (double?)r["heightMm"], d = (double?)r["diameterMm"];

            var plan = new
            {
                ductType = $"{dt.FamilyName}: {dt.Name}", systemType = st.Name, level = lv.Name,
                widthMm = w, heightMm = h, diameterMm = d, willCreate = segs.Count, skipped = bad,
                availableDuctTypes = TypeNames(types), availableSystemTypes = sysTypes.Select(x => x.Name),
            };
            if (!apply) return Ok(new { preflight = true, plan });

            var ids = new List<long>();
            var res = RunTx(_doc, "MCP Create Ducts", () =>
            {
                foreach (var s in segs)
                {
                    var duct = Duct.Create(_doc, st.Id, dt.Id, lv.Id, s.A, s.B);
                    if (w.HasValue) SetDouble(duct, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, w.Value * FtPerMm);
                    if (h.HasValue) SetDouble(duct, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, h.Value * FtPerMm);
                    if (d.HasValue) SetDouble(duct, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, d.Value * FtPerMm);
                    ids.Add(duct.Id.Value);
                }
            });
            return res.committed
                ? Ok(new { applied = true, created = ids.Count, ids, plan, warnings = res.warnings })
                : Fail("建立風管失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }

        // POST /mep/pipes
        // { segments:[...], pipeType?, systemType?, level?, diameterMm?, apply:false }
        public string Pipes(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            var (segs, bad) = Segments(r["segments"]);
            if (segs.Count == 0) return Fail("segments 沒有可用的線段。");

            var types = new FilteredElementCollector(_doc).OfClass(typeof(PipeType)).Cast<PipeType>().ToList();
            var sysTypes = new FilteredElementCollector(_doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().ToList();
            var pt = FindType(types, (string?)r["pipeType"]);
            var st = FindType(sysTypes, (string?)r["systemType"]);
            if (pt == null) return Fail("模型裡沒有水管類型。");
            if (st == null) return Fail("模型裡沒有管路系統類型（PipingSystemType）。");
            if (!string.IsNullOrWhiteSpace((string?)r["pipeType"]) && !MatchesName(pt, (string?)r["pipeType"]))
                return Fail("找不到水管類型「" + r["pipeType"] + "」。可用：" + string.Join("、", TypeNames(types)));

            var lv = FindLevel(_doc, (string?)r["level"], segs.Min(s => Math.Min(s.A.Z, s.B.Z)));
            if (lv == null) return Fail("模型裡沒有樓層。");
            double? d = (double?)r["diameterMm"];

            var plan = new
            {
                pipeType = $"{pt.FamilyName}: {pt.Name}", systemType = st.Name, level = lv.Name,
                diameterMm = d, willCreate = segs.Count, skipped = bad,
                availablePipeTypes = TypeNames(types), availableSystemTypes = sysTypes.Select(x => x.Name),
            };
            if (!apply) return Ok(new { preflight = true, plan });

            var ids = new List<long>();
            var res = RunTx(_doc, "MCP Create Pipes", () =>
            {
                foreach (var s in segs)
                {
                    var pipe = Pipe.Create(_doc, st.Id, pt.Id, lv.Id, s.A, s.B);
                    if (d.HasValue) SetDouble(pipe, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, d.Value * FtPerMm);
                    ids.Add(pipe.Id.Value);
                }
            });
            return res.committed
                ? Ok(new { applied = true, created = ids.Count, ids, plan, warnings = res.warnings })
                : Fail("建立水管失敗，已整批回滾、模型沒有變更：" + string.Join("；", res.errors));
        }

        // POST /mep/systems
        // { kind:"duct"|"pipe", elementIds:[設備/末端 id], systemType?, name?, apply:false }
        // 只收「還沒接上任何系統」且領域相符的接頭。
        public string CreateSystem(string body)
        {
            var r = Body(body); bool apply = (bool?)r["apply"] ?? false;
            string kind = ((string?)r["kind"] ?? "duct").ToLowerInvariant();
            bool isDuct = kind != "pipe";
            var domain = isDuct ? Domain.DomainHvac : Domain.DomainPiping;
            var ids = Ids(r["elementIds"]);
            if (ids.Count == 0) return Fail("elementIds 是空的。");

            ElementId sysTypeId; string sysTypeName;
            if (isDuct)
            {
                var all = new FilteredElementCollector(_doc).OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>().ToList();
                var t = FindType(all, (string?)r["systemType"]);
                if (t == null) return Fail("模型裡沒有風管系統類型。");
                sysTypeId = t.Id; sysTypeName = t.Name;
            }
            else
            {
                var all = new FilteredElementCollector(_doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().ToList();
                var t = FindType(all, (string?)r["systemType"]);
                if (t == null) return Fail("模型裡沒有管路系統類型。");
                sysTypeId = t.Id; sysTypeName = t.Name;
            }

            var set = new ConnectorSet(); var perElement = new List<object>();
            foreach (var id in ids)
            {
                var el = Get(_doc, id);
                if (!(el is FamilyInstance fi) || fi.MEPModel?.ConnectorManager == null)
                { perElement.Add(new { id, ok = false, reason = "不是有接頭的 MEP 族群實例" }); continue; }
                int n = 0;
                foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors)
                    if (c.Domain == domain && c.MEPSystem == null) { set.Insert(c); n++; }
                perElement.Add(new { id, ok = n > 0, freeConnectors = n, reason = n > 0 ? null : "沒有可用的同領域空接頭" });
            }
            if (set.Size == 0) return Fail("所選元件沒有可用的空接頭。" + Newtonsoft.Json.JsonConvert.SerializeObject(perElement));

            string name = string.IsNullOrWhiteSpace((string?)r["name"])
                ? (isDuct ? "MCP風管系統-" : "MCP管路系統-") + DateTime.Now.ToString("yyyyMMdd-HHmmss")
                : (string)r["name"]!;
            var plan = new { kind = isDuct ? "duct" : "pipe", systemType = sysTypeName, name, connectors = set.Size, perElement };
            if (!apply) return Ok(new { preflight = true, plan });

            long sysId = 0;
            var res = RunTx(_doc, "MCP Create MEP System", () =>
            {
                MEPSystem sys = isDuct
                    ? MechanicalSystem.Create(_doc, sysTypeId, name)
                    : PipingSystem.Create(_doc, sysTypeId, name);
                sys.Add(set);
                sysId = sys.Id.Value;
            });
            return res.committed
                ? Ok(new { applied = true, systemId = sysId, plan, warnings = res.warnings })
                : Fail("建立系統失敗，已回滾、模型沒有變更：" + string.Join("；", res.errors));
        }

        private static bool MatchesName(ElementType t, string? name) =>
            name == null || t.Name == name || $"{t.FamilyName}: {t.Name}".IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
