// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMcpAddin.Models;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 把選取的直線段（細部線 / 模型線）整理成封閉、不自相交的迴圈。
    /// - 端點吸附（容差內接合缺口 → 封閉）
    /// - 拓樸檢查（開放端 degree=1、分岔 degree>=3、連通分量）
    /// - 退化線段（吸附後長度小於 ShortCurveTolerance）過濾
    /// - 自相交檢查（XY 平面 proper-crossing；適用平面視圖）
    /// apply=false 只回報（dry-run）；apply=true 於單一 Transaction 內 SetGeometryCurve。
    /// 註：只吸附端點，不會自動解開交叉（避免改變圖形意圖），交叉僅回報。
    /// </summary>
    public class LineLoopHandler
    {
        private readonly Document _doc;
        public LineLoopHandler(Document doc) => _doc = doc;

        public string CloseLoop(string body)
        {
            var req = JsonConvert.DeserializeObject<CloseLoopRequest>(body)
                      ?? throw new ArgumentException("Invalid request body.");
            if (req.ElementIds == null || req.ElementIds.Count < 2)
                return JsonConvert.SerializeObject(
                    ApiResponse<CloseLoopResult>.Fail("elementIds 需至少 2 個直線段。"));

            double tolMm  = req.ToleranceMm <= 0 ? 3.0 : req.ToleranceMm;
            double tolFt  = tolMm / 304.8;
            double degenFt = _doc.Application.ShortCurveTolerance;   // Revit 最短曲線容差（feet）

            // 1) 收集直線段
            var ids     = new List<long>();
            var p0      = new List<XYZ>();
            var p1      = new List<XYZ>();
            var skipped = new List<long>();
            foreach (var id in req.ElementIds)
            {
                var el = _doc.GetElement(new ElementId((int)id));
                if (el is CurveElement ce && ce.GeometryCurve is Line ln)
                {
                    ids.Add(id);
                    p0.Add(ln.GetEndPoint(0));
                    p1.Add(ln.GetEndPoint(1));
                }
                else skipped.Add(id);
            }

            int n = ids.Count;
            if (n < 2)
                return JsonConvert.SerializeObject(
                    ApiResponse<CloseLoopResult>.Fail("有效直線段不足（需至少 2 條可用直線）。"));

            // 2) 端點群聚（union-find），2n 個端點
            var pts = new List<XYZ>(2 * n);
            for (int i = 0; i < n; i++) { pts.Add(p0[i]); pts.Add(p1[i]); }

            var parent = Enumerable.Range(0, 2 * n).ToArray();
            int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
            void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }
            for (int a = 0; a < 2 * n; a++)
                for (int b = a + 1; b < 2 * n; b++)
                    if (pts[a].DistanceTo(pts[b]) <= tolFt) Union(a, b);

            // 群集質心
            var members = new Dictionary<int, List<int>>();
            for (int k = 0; k < 2 * n; k++)
            {
                int r = Find(k);
                if (!members.TryGetValue(r, out var lst)) { lst = new List<int>(); members[r] = lst; }
                lst.Add(k);
            }
            var centroid = new Dictionary<int, XYZ>();
            foreach (var kv in members)
            {
                int c = kv.Value.Count;
                double sx = kv.Value.Sum(m => pts[m].X);
                double sy = kv.Value.Sum(m => pts[m].Y);
                double sz = kv.Value.Sum(m => pts[m].Z);
                centroid[kv.Key] = new XYZ(sx / c, sy / c, sz / c);
            }

            var root = new int[2 * n];
            for (int k = 0; k < 2 * n; k++) root[k] = Find(k);

            // 吸附後的新端點
            var np0 = new XYZ[n];
            var np1 = new XYZ[n];
            for (int i = 0; i < n; i++) { np0[i] = centroid[root[2 * i]]; np1[i] = centroid[root[2 * i + 1]]; }

            // 3) 拓樸：每個群集的度數
            var degree = new Dictionary<int, int>();
            foreach (var r in root) degree[r] = degree.TryGetValue(r, out var d) ? d + 1 : 1;
            int openEnds = degree.Values.Count(d => d == 1);   // 缺口接不起來
            int branches = degree.Values.Count(d => d >= 3);   // 分岔 / 自我碰觸

            // 連通分量（線層級 union-find）
            var lp = Enumerable.Range(0, n).ToArray();
            int LFind(int a) { while (lp[a] != a) { lp[a] = lp[lp[a]]; a = lp[a]; } return a; }
            var rootLines = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
                foreach (var r in new[] { root[2 * i], root[2 * i + 1] })
                {
                    if (!rootLines.TryGetValue(r, out var lst)) { lst = new List<int>(); rootLines[r] = lst; }
                    lst.Add(i);
                }
            foreach (var kv in rootLines)
                for (int j = 1; j < kv.Value.Count; j++)
                { int a = LFind(kv.Value[0]), b = LFind(kv.Value[j]); if (a != b) lp[a] = b; }
            int components = Enumerable.Range(0, n).Select(LFind).Distinct().Count();

            // 退化線段（吸附後長度過短）
            var degenerate = new List<long>();
            for (int i = 0; i < n; i++)
                if (np0[i].DistanceTo(np1[i]) < degenFt) degenerate.Add(ids[i]);

            // 4) 自相交（XY proper-crossing）；共用群集的相鄰對跳過
            static double Cross(XYZ a, XYZ b, XYZ c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            static bool ProperCross(XYZ a, XYZ b, XYZ c, XYZ d)
            {
                double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
                return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
                    && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
            }
            var crossings = new List<long[]>();
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    var si = new HashSet<int> { root[2 * i], root[2 * i + 1] };
                    if (si.Contains(root[2 * j]) || si.Contains(root[2 * j + 1])) continue;
                    if (ProperCross(np0[i], np1[i], np0[j], np1[j]))
                        crossings.Add(new[] { ids[i], ids[j] });
                }

            // 將調整的線數（端點有位移者）
            int moved = 0;
            for (int i = 0; i < n; i++)
                if (p0[i].DistanceTo(np0[i]) > 1e-9 || p1[i].DistanceTo(np1[i]) > 1e-9) moved++;

            bool isClosed = openEnds == 0 && branches == 0 && components == 1;

            // 5) apply：於單一 Transaction 內吸附端點
            int changed = 0;
            var failed = new List<string>();
            if (req.Apply)
            {
                var degSet = new HashSet<long>(degenerate);
                using var tx = new Transaction(_doc, "MCP 整理封閉線");
                tx.Start();
                for (int i = 0; i < n; i++)
                {
                    if (degSet.Contains(ids[i])) continue;
                    if (p0[i].DistanceTo(np0[i]) <= 1e-9 && p1[i].DistanceTo(np1[i]) <= 1e-9) continue;
                    try
                    {
                        var el = (CurveElement)_doc.GetElement(new ElementId((int)ids[i]));
                        el.SetGeometryCurve(Line.CreateBound(np0[i], np1[i]), false);
                        changed++;
                    }
                    catch (Exception ex) { failed.Add($"{ids[i]}: {ex.Message}"); }
                }
                tx.Commit();
            }

            var result = new CloseLoopResult
            {
                Applied        = req.Apply,
                LineCount      = n,
                SkippedNonLine = skipped,
                ToleranceMm    = tolMm,
                MovedLines     = moved,
                ChangedLines   = changed,
                IsClosed       = isClosed,
                Components     = components,
                OpenEnds       = openEnds,
                BranchPoints   = branches,
                Degenerate     = degenerate,
                Crossings      = crossings,
                Failed         = failed
            };
            return JsonConvert.SerializeObject(ApiResponse<CloseLoopResult>.Ok(result));
        }
    }
}
