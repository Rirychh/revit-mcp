// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMcpAddin.Models;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 基地模精簡：
    ///  POST /site/simplify   DirectShape 共平面三角面合併為多邊形面（保留元素 ID／類別／名稱／註解）＋ Toposolid 格網減點
    ///  POST /documents/open  以 API 開啟並啟用 .rvt
    /// 進度寫到 %TEMP%\mcp_site_simplify.log
    /// </summary>
    public class SiteSimplifyHandler
    {
        private readonly UIApplication _uiApp;
        public SiteSimplifyHandler(UIApplication uiApp) => _uiApp = uiApp;

        private static string Ok(object o) => JsonConvert.SerializeObject(ApiResponse<object>.Ok(o));
        private static readonly string LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp_site_simplify.log");
        private static void Log(string s) { try { System.IO.File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss ") + s + Environment.NewLine); } catch { } }

        private Document Resolve(string? key)
        {
            var docs = _uiApp.Application.Documents.Cast<Document>().Where(d => !d.IsLinked).ToList();
            if (string.IsNullOrWhiteSpace(key))
                return _uiApp.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No active Revit document.");
            return docs.FirstOrDefault(d => (d.Title ?? "").IndexOf(key!, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? docs.FirstOrDefault(d => (d.PathName ?? "").IndexOf(key!, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? throw new ArgumentException($"找不到開啟中的文件：{key}（開啟中：{string.Join(", ", docs.Select(d => d.Title))}）");
        }

        private class Preproc : IFailuresPreprocessor
        {
            public List<string> Notes = new();
            public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
            {
                bool resolved = false;
                foreach (var f in fa.GetFailureMessages())
                {
                    if (f.GetSeverity() == FailureSeverity.Warning) { fa.DeleteWarning(f); continue; }
                    if (f.HasResolutions()) { fa.ResolveFailure(f); resolved = true; Notes.Add("resolved: " + f.GetDescriptionText()); }
                    else Notes.Add("error: " + f.GetDescriptionText());
                }
                return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
            }
        }

        // ── POST /documents/open {path} ─────────────────────────
        public string Open(string body)
        {
            var req = JsonConvert.DeserializeObject<OpenReq>(body ?? "{}") ?? new OpenReq();
            if (string.IsNullOrWhiteSpace(req.Path)) throw new ArgumentException("path 必填");
            var already = _uiApp.Application.Documents.Cast<Document>().FirstOrDefault(d => !d.IsLinked &&
                string.Equals(d.PathName, req.Path, StringComparison.OrdinalIgnoreCase));
            var mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(req.Path);
            var opt = new OpenOptions { Audit = req.Audit };
            var uidoc = _uiApp.OpenAndActivateDocument(mp, opt, false);
            return Ok(new { title = uidoc.Document.Title, path = uidoc.Document.PathName, wasOpen = already != null });
        }

        // ── POST /site/simplify ─────────────────────────────────
        public string Simplify(string body)
        {
            var req = JsonConvert.DeserializeObject<SimplifyReq>(body ?? "{}") ?? new SimplifyReq();
            var doc = Resolve(req.Document);
            try { System.IO.File.WriteAllText(LogPath, ""); } catch { }
            Log($"start doc={doc.Title} apply={req.Apply} limit={req.Limit}");

            double tol = req.DistMm / 304.8;
            double cosA = Math.Cos(req.AngleDeg * Math.PI / 180.0);

            var dss = new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>()
                .Where(d => string.IsNullOrEmpty(req.ApplicationId) || d.ApplicationId == req.ApplicationId)
                .ToList();
            if (req.Limit > 0) dss = dss.Take(req.Limit).ToList();

            var pp = new Preproc();
            Transaction? t = null;
            if (req.Apply)
            {
                t = new Transaction(doc, "MCP 基地模精簡");
                var o = t.GetFailureHandlingOptions();
                o.SetFailuresPreprocessor(pp); o.SetClearAfterRollback(true); o.SetForcedModalHandling(false);
                t.SetFailureHandlingOptions(o);
                t.Start();
            }
            int changed = 0, skipped = 0, failed = 0; long tri0 = 0, face1 = 0;
            var errs = new List<string>();
            var stageErr = new Dictionary<string, int>();
            var stageUse = new Dictionary<string, int>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                for (int i = 0; i < dss.Count; i++)
                {
                    var ds = dss[i];
                    try
                    {
                        var tris = new List<XYZ[]>();
                        Collect(ds.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }), tris);
                        if (tris.Count == 0) { skipped++; continue; }
                        var faces = MergeCoplanar(tris, tol, cosA);
                        if (faces == null || faces.Count >= tris.Count) { skipped++; continue; }
                        tri0 += tris.Count; face1 += faces.Count;
                        if (req.Apply)
                        {
                            string? used = null;
                            foreach (var stage in new[] { "proj-salvage", "raw-salvage", "proj-mesh" })
                            {
                                IList<GeometryObject>? geo = null;
                                try { geo = Build(stage.StartsWith("proj") ? Project(faces) : faces, stage.EndsWith("mesh")); }
                                catch (Exception ex) { Note(stageErr, stage + " build: " + ex.Message); continue; }
                                if (geo == null || geo.Count == 0) { Note(stageErr, stage + " build: empty"); continue; }
                                try { ds.SetShape(geo); used = stage; break; }
                                catch (Exception ex) { Note(stageErr, stage + " setshape: " + ex.Message); }
                            }
                            if (used == null) { failed++; continue; }
                            stageUse[used] = stageUse.TryGetValue(used, out var su) ? su + 1 : 1;
                        }
                        changed++;
                    }
                    catch (Exception ex) { failed++; if (errs.Count < 10) errs.Add($"{ds.Id.IntegerValue}: {ex.Message}"); }
                    if (i % 100 == 0) Log($"{i}/{dss.Count} changed={changed} skipped={skipped} failed={failed} {sw.Elapsed.TotalSeconds:F0}s");
                }
                Log($"directshape done changed={changed} {sw.Elapsed.TotalSeconds:F0}s");

                object? topo = null;
                if (req.TopoCellM > 0) topo = SimplifyTopo(doc, req.TopoCellM, req.Apply);
                Log("topo done");
                t?.Commit();
                Log("committed");
                return Ok(new
                {
                    applied = req.Apply, directShapes = dss.Count, changed, skipped, failed,
                    trianglesBefore = tri0, facesAfter = face1, topo, seconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
                    errors = errs, stageUse, stageErrors = stageErr.OrderByDescending(kv => kv.Value).Take(10).ToDictionary(kv => kv.Key, kv => kv.Value), failures = pp.Notes.Take(20)
                });
            }
            catch (Exception ex)
            {
                if (t != null && t.HasStarted() && !t.HasEnded()) t.RollBack();
                Log("rolled back: " + ex.Message);
                throw;
            }
        }

        // ── 幾何 ────────────────────────────────────────────────
        private static void Collect(GeometryElement? ge, List<XYZ[]> tris)
        {
            if (ge == null) return;
            foreach (var g in ge)
            {
                switch (g)
                {
                    case GeometryInstance gi: Collect(gi.GetInstanceGeometry(), tris); break;
                    case Solid s:
                        foreach (Face f in s.Faces)
                        {
                            var m = f.Triangulate();
                            for (int i = 0; i < m.NumTriangles; i++) { var tr = m.get_Triangle(i); tris.Add(new[] { tr.get_Vertex(0), tr.get_Vertex(1), tr.get_Vertex(2) }); }
                        }
                        break;
                    case Mesh mm:
                        for (int i = 0; i < mm.NumTriangles; i++) { var tr = mm.get_Triangle(i); tris.Add(new[] { tr.get_Vertex(0), tr.get_Vertex(1), tr.get_Vertex(2) }); }
                        break;
                }
            }
        }

        /// <summary>回傳每個合併面的 loops（第一個為外框）。</summary>
        private static List<List<List<XYZ>>>? MergeCoplanar(List<XYZ[]> trisIn, double tol, double cosA)
        {
            // 焊接頂點
            var verts = new List<XYZ>();
            var key = new Dictionary<(long, long, long), int>();
            int V(XYZ p)
            {
                const double q = 1e-4;
                var k = ((long)Math.Round(p.X / q), (long)Math.Round(p.Y / q), (long)Math.Round(p.Z / q));
                if (!key.TryGetValue(k, out var id)) { id = verts.Count; verts.Add(p); key[k] = id; }
                return id;
            }
            var T = new List<int[]>(); var N = new List<XYZ>();
            foreach (var tr in trisIn)
            {
                int a = V(tr[0]), b = V(tr[1]), c = V(tr[2]);
                if (a == b || b == c || a == c) continue;
                var n = (verts[b] - verts[a]).CrossProduct(verts[c] - verts[a]);
                if (n.GetLength() < 1e-12) continue;
                T.Add(new[] { a, b, c }); N.Add(n.Normalize());
            }
            if (T.Count == 0) return null;
            // 邊 → 三角形
            var edgeTris = new Dictionary<(int, int), List<int>>();
            (int, int) E(int a, int b) => a < b ? (a, b) : (b, a);
            for (int i = 0; i < T.Count; i++)
                for (int k = 0; k < 3; k++)
                {
                    var e = E(T[i][k], T[i][(k + 1) % 3]);
                    if (!edgeTris.TryGetValue(e, out var l)) edgeTris[e] = l = new List<int>(2);
                    l.Add(i);
                }
            // 區域成長（以種子平面為基準，避免漂移）
            var group = Enumerable.Repeat(-1, T.Count).ToArray();
            var groups = new List<List<int>>(); var gN = new List<XYZ>();
            for (int s = 0; s < T.Count; s++)
            {
                if (group[s] >= 0) continue;
                int gid = groups.Count; var members = new List<int> { s }; group[s] = gid;
                var n0 = N[s]; var p0 = verts[T[s][0]];
                var q = new Queue<int>(); q.Enqueue(s);
                while (q.Count > 0)
                {
                    int cur = q.Dequeue();
                    for (int k = 0; k < 3; k++)
                    {
                        foreach (var nb in edgeTris[E(T[cur][k], T[cur][(k + 1) % 3])])
                        {
                            if (group[nb] >= 0) continue;
                            if (N[nb].DotProduct(n0) < cosA) continue;
                            bool onPlane = true;
                            foreach (var vi in T[nb]) if (Math.Abs((verts[vi] - p0).DotProduct(n0)) > tol) { onPlane = false; break; }
                            if (!onPlane) continue;
                            group[nb] = gid; members.Add(nb); q.Enqueue(nb);
                        }
                    }
                }
                groups.Add(members); gN.Add(n0);
            }
            var result = new List<List<List<XYZ>>>();
            for (int g = 0; g < groups.Count; g++)
            {
                var mem = groups[g];
                if (mem.Count == 1) { result.Add(new List<List<XYZ>> { T[mem[0]].Select(i => verts[i]).ToList() }); continue; }
                var cnt = new Dictionary<(int, int), int>();
                foreach (var ti in mem) for (int k = 0; k < 3; k++) { var e = E(T[ti][k], T[ti][(k + 1) % 3]); cnt[e] = cnt.TryGetValue(e, out var c) ? c + 1 : 1; }
                var bnd = cnt.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
                var loops = Chain(bnd);
                if (loops == null) { foreach (var ti in mem) result.Add(new List<List<XYZ>> { T[ti].Select(i => verts[i]).ToList() }); continue; }
                var n = gN[g];
                var polys = loops.Select(l => Simplify(l.Select(i => verts[i]).ToList())).Where(l => l.Count >= 3).ToList();
                if (polys.Count == 0) continue;
                // 外框 = 投影面積最大
                var areas = polys.Select(l => Newell(l).DotProduct(n)).ToList();
                int oi = Enumerable.Range(0, polys.Count).OrderByDescending(i => Math.Abs(areas[i])).First();
                var face = new List<List<XYZ>>();
                var outer = polys[oi]; if (areas[oi] < 0) outer.Reverse(); face.Add(outer);
                for (int i = 0; i < polys.Count; i++)
                {
                    if (i == oi) continue;
                    var l = polys[i]; if (areas[i] > 0) l.Reverse(); face.Add(l);
                }
                result.Add(face);
            }
            return result;
        }

        private static List<List<int>>? Chain(List<(int, int)> edges)
        {
            var adj = new Dictionary<int, List<int>>();
            foreach (var (a, b) in edges)
            {
                if (!adj.TryGetValue(a, out var la)) adj[a] = la = new List<int>(); la.Add(b);
                if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = new List<int>(); lb.Add(a);
            }
            if (adj.Values.Any(l => l.Count % 2 != 0)) return null;
            var used = new HashSet<(int, int)>();
            (int, int) E(int a, int b) => a < b ? (a, b) : (b, a);
            var loops = new List<List<int>>();
            foreach (var (a0, b0) in edges)
            {
                if (used.Contains(E(a0, b0))) continue;
                var loop = new List<int> { a0 }; used.Add(E(a0, b0));
                int prev = a0, cur = b0, guard = 0;
                while (cur != a0 && guard++ < 100000)
                {
                    loop.Add(cur);
                    int nxt = -1;
                    foreach (var c in adj[cur]) if (!used.Contains(E(cur, c))) { nxt = c; break; }
                    if (nxt < 0) return null;
                    used.Add(E(cur, nxt)); prev = cur; cur = nxt;
                }
                if (cur != a0) return null;
                loops.Add(loop);
            }
            return loops;
        }

        private static List<XYZ> Simplify(List<XYZ> pts)
        {
            var o = new List<XYZ>();
            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                var a = pts[(i - 1 + n) % n]; var b = pts[i]; var c = pts[(i + 1) % n];
                var v1 = b - a; var v2 = c - b;
                if (v1.GetLength() < 1e-6) continue;
                if (v1.CrossProduct(v2).GetLength() <= 1e-6 * v1.GetLength() * Math.Max(v2.GetLength(), 1e-9) && v1.DotProduct(v2) > 0) continue;
                o.Add(b);
            }
            return o.Count >= 3 ? o : pts;
        }

        private static XYZ Newell(List<XYZ> p)
        {
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < p.Count; i++)
            {
                var a = p[i]; var b = p[(i + 1) % p.Count];
                x += (a.Y - b.Y) * (a.Z + b.Z); y += (a.Z - b.Z) * (a.X + b.X); z += (a.X - b.X) * (a.Y + b.Y);
            }
            return new XYZ(x, y, z) * 0.5;
        }

        private static void Note(Dictionary<string, int> d, string k) { if (k.Length > 160) k = k.Substring(0, 160); d[k] = d.TryGetValue(k, out var v) ? v + 1 : 1; }

        /// <summary>每個面投影到自身平面（外框 Newell 法向＋形心），消除容許誤差內的非平面。</summary>
        private static List<List<List<XYZ>>> Project(List<List<List<XYZ>>> faces)
        {
            var res = new List<List<List<XYZ>>>(faces.Count);
            foreach (var f in faces)
            {
                var outer = f[0];
                var nn = Newell(outer);
                if (nn.GetLength() < 1e-12) { res.Add(f); continue; }
                var n = nn.Normalize();
                var c = outer.Aggregate(XYZ.Zero, (a, b) => a + b) / outer.Count;
                res.Add(f.Select(l => l.Select(p => p - n * (p - c).DotProduct(n)).ToList()).ToList());
            }
            return res;
        }

        private static IList<GeometryObject>? Build(List<List<List<XYZ>>> faces, bool meshFallback) => Build(faces, meshFallback, ElementId.InvalidElementId);
        private static IList<GeometryObject>? Build(List<List<List<XYZ>>> faces, bool meshFallback, ElementId matId)
        {
            var b = new TessellatedShapeBuilder();
            b.OpenConnectedFaceSet(false);
            foreach (var f in faces)
            {
                try { b.AddFace(new TessellatedFace(f.Select(l => (IList<XYZ>)l).ToList(), matId)); } catch { }
            }
            b.CloseConnectedFaceSet();
            b.Target = TessellatedShapeBuilderTarget.AnyGeometry;
            b.Fallback = meshFallback ? TessellatedShapeBuilderFallback.Mesh : TessellatedShapeBuilderFallback.Salvage;
            b.Build();
            return b.GetBuildResult().GetGeometricalObjects();
        }

        // ── 地形 ────────────────────────────────────────────────
        private static object SimplifyTopo(Document doc, double cellM, bool apply)
        {
            var outList = new List<object>();
#if REVIT2024_OR_GREATER
            foreach (var tp in new FilteredElementCollector(doc).OfClass(typeof(Toposolid)).Cast<Toposolid>().ToList())
            {
                int oldId = tp.Id.IntegerValue;
                var ed = tp.GetSlabShapeEditor();
                var P = ed.SlabShapeVertices.Cast<SlabShapeVertex>().Select(v => v.Position).ToList();
                if (P.Count < 50) { outList.Add(new { id = tp.Id.IntegerValue, before = P.Count, skipped = true }); continue; }
                double x0 = P.Min(p => p.X), y0 = P.Min(p => p.Y), x1 = P.Max(p => p.X), y1 = P.Max(p => p.Y);
                double cell = cellM / 0.3048, edgeTol = 1.0 / 0.3048;
                var best = new Dictionary<(int, int), (double, XYZ)>();
                foreach (var p in P)
                {
                    var k = ((int)Math.Floor((p.X - x0) / cell), (int)Math.Floor((p.Y - y0) / cell));
                    double cx = x0 + (k.Item1 + 0.5) * cell, cy = y0 + (k.Item2 + 0.5) * cell;
                    double d = (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy);
                    if (!best.TryGetValue(k, out var bv) || d < bv.Item1) best[k] = (d, p);
                }
                var keep = new Dictionary<(long, long), XYZ>();
                void Add(XYZ p) => keep[((long)Math.Round(p.X * 1000), (long)Math.Round(p.Y * 1000))] = p;
                foreach (var v in best.Values) Add(v.Item2);
                foreach (var p in P)
                    if (Math.Abs(p.X - x0) < edgeTol || Math.Abs(p.X - x1) < edgeTol || Math.Abs(p.Y - y0) < edgeTol || Math.Abs(p.Y - y1) < edgeTol) Add(p);
                int newId = 0;
                if (apply)
                {
                    var lvl = tp.LevelId;
                    if (lvl == ElementId.InvalidElementId)
                        lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => Math.Abs(l.Elevation)).First().Id;
                    var nt = Toposolid.Create(doc, keep.Values.ToList(), tp.GetTypeId(), lvl);
                    var c0 = tp.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                    if (!string.IsNullOrEmpty(c0)) nt.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(c0);
                    doc.Delete(tp.Id);
                    newId = nt.Id.IntegerValue;
                }
                outList.Add(new { id = oldId, before = P.Count, after = keep.Count, newId });
            }
#endif
            return outList;
        }

        // ── POST /links/cad {document, paths[], level?, thisViewOnly} ──
        public string LinkCad(string body)
        {
            var req = JsonConvert.DeserializeObject<CadReq>(body ?? "{}") ?? new CadReq();
            var doc = Resolve(req.Document);
            if (req.Paths == null || req.Paths.Count == 0) throw new ArgumentException("paths 必填");
            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel != null).ToList();
            ViewPlan? view = null;
            if (!string.IsNullOrWhiteSpace(req.Level))
                view = plans.FirstOrDefault(v => string.Equals(v.GenLevel.Name, req.Level, StringComparison.OrdinalIgnoreCase))
                    ?? plans.FirstOrDefault(v => v.GenLevel.Name.IndexOf(req.Level!, StringComparison.OrdinalIgnoreCase) >= 0);
            view ??= plans.OrderBy(v => Math.Abs(v.GenLevel.Elevation)).FirstOrDefault();
            if (view == null) throw new InvalidOperationException("找不到樓板平面圖可放置 CAD 連結");
            var results = new List<object>();
            var pp = new Preproc();
            using var t = new Transaction(doc, "MCP 連結 CAD");
            var o = t.GetFailureHandlingOptions(); o.SetFailuresPreprocessor(pp); o.SetForcedModalHandling(false); t.SetFailureHandlingOptions(o);
            t.Start();
            foreach (var path in req.Paths)
            {
                string name = System.IO.Path.GetFileName(path);
                var existing = new FilteredElementCollector(doc).OfClass(typeof(CADLinkType)).Cast<CADLinkType>()
                    .FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    try { existing.Reload(); results.Add(new { file = name, action = "reloaded", typeId = existing.Id.IntegerValue }); }
                    catch (Exception ex) { results.Add(new { file = name, action = "exists", error = ex.Message }); }
                    continue;
                }
                var opt = new DWGImportOptions
                {
                    Placement = ImportPlacement.Origin,
                    Unit = ImportUnit.Millimeter,
                    ColorMode = ImportColorMode.Preserved,
                    ThisViewOnly = req.ThisViewOnly,
                    VisibleLayersOnly = false
                };
                bool ok = doc.Link(path, opt, view, out ElementId id);
                if (ok && id != ElementId.InvalidElementId && req.Pin)
                {
                    var el = doc.GetElement(id); if (el != null) el.Pinned = true;
                }
                results.Add(new { file = name, action = ok ? "linked" : "failed", instanceId = id.IntegerValue });
            }
            var st = t.Commit();
            return Ok(new { document = doc.Title, view = view.Name, level = view.GenLevel.Name, status = st.ToString(), results, failures = pp.Notes });
        }


        // ── POST /site/directshapes {document, applicationId, dataIdPrefix, commentPrefix, nameContains, minX,minY,maxX,maxY (mm, bbox center), apply} ──
        public string DirectShapes(string body)
        {
            var req = JsonConvert.DeserializeObject<DsReq>(body ?? "{}") ?? new DsReq();
            var doc = Resolve(req.Document);
            const double F = 304.8;
            var all = new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>().ToList();
            var rows = new List<(DirectShape ds, string dataId, string name, string cmt, string cat, double cx, double cy, double zmin, double zmax)>();
            foreach (var ds in all)
            {
                string aid = ds.ApplicationId ?? "", did = ds.ApplicationDataId ?? "", nm = ds.Name ?? "";
                string cm = ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
                if (!string.IsNullOrEmpty(req.ApplicationId) && aid != req.ApplicationId) continue;
                if (!string.IsNullOrEmpty(req.DataIdPrefix) && !did.StartsWith(req.DataIdPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(req.CommentPrefix) && !cm.StartsWith(req.CommentPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(req.NameContains) && nm.IndexOf(req.NameContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var bb = ds.get_BoundingBox(null);
                double cx = 0, cy = 0, z0 = 0, z1 = 0;
                if (bb != null) { cx = (bb.Min.X + bb.Max.X) / 2 * F; cy = (bb.Min.Y + bb.Max.Y) / 2 * F; z0 = bb.Min.Z * F; z1 = bb.Max.Z * F; }
                if (req.MinX.HasValue && req.MinY.HasValue && req.MaxX.HasValue && req.MaxY.HasValue)
                {
                    if (bb == null) continue;
                    if (cx < req.MinX || cx > req.MaxX || cy < req.MinY || cy > req.MaxY) continue;
                }
                rows.Add((ds, did, nm, cm, ds.Category?.Name ?? "", cx, cy, z0, z1));
            }
            var list = rows.Take(req.Limit).Select(r => new { id = r.ds.Id.IntegerValue, dataId = r.dataId, name = r.name, comment = r.cmt, category = r.cat,
                cx = Math.Round(r.cx), cy = Math.Round(r.cy), zMin = Math.Round(r.zmin), zMax = Math.Round(r.zmax) }).ToList();
            var byComment = rows.GroupBy(r => r.cmt.Length > 40 ? r.cmt.Substring(0, 40) : r.cmt).ToDictionary(g => g.Key, g => g.Count());
            int deleted = 0;
            if (req.Apply && rows.Count > 0)
            {
                using var t = new Transaction(doc, "MCP 刪除 DirectShape");
                t.Start();
                var ids = rows.Select(r => r.ds.Id).ToList();
                deleted = doc.Delete(ids).Count;
                t.Commit();
            }
            return Ok(new { applied = req.Apply, totalDirectShapes = all.Count, matched = rows.Count, deleted, byComment, elements = list });
        }

        public class DsReq
        {
            [JsonProperty("document")] public string? Document { get; set; }
            [JsonProperty("applicationId")] public string? ApplicationId { get; set; }
            [JsonProperty("dataIdPrefix")] public string? DataIdPrefix { get; set; }
            [JsonProperty("commentPrefix")] public string? CommentPrefix { get; set; }
            [JsonProperty("nameContains")] public string? NameContains { get; set; }
            [JsonProperty("minX")] public double? MinX { get; set; }
            [JsonProperty("minY")] public double? MinY { get; set; }
            [JsonProperty("maxX")] public double? MaxX { get; set; }
            [JsonProperty("maxY")] public double? MaxY { get; set; }
            [JsonProperty("limit")] public int Limit { get; set; } = 200;
            [JsonProperty("apply")] public bool Apply { get; set; } = false;
        }


        // ── POST /site/materials {document, applicationId, rules:[{contains:[..], material, rgb, transparency}], topoMaterial:{material,rgb}, apply} ──
        public string Materials(string body)
        {
            var req = JsonConvert.DeserializeObject<MatReq>(body ?? "{}") ?? new MatReq();
            var doc = Resolve(req.Document);
            var dss = new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>()
                .Where(d => string.IsNullOrEmpty(req.ApplicationId) || d.ApplicationId == req.ApplicationId).ToList();
            var plan = new List<(DirectShape ds, MatRule rule)>();
            var counts = new Dictionary<string, int>();
            int unmatched = 0;
            foreach (var ds in dss)
            {
                string cm = ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
                var rule = (req.Rules ?? new List<MatRule>()).FirstOrDefault(r => r.Contains != null && r.Contains.Any(k => cm.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0));
                if (rule == null) { unmatched++; continue; }
                plan.Add((ds, rule)); counts[rule.Material] = counts.TryGetValue(rule.Material, out var n) ? n + 1 : 1;
            }
            if (!req.Apply) return Ok(new { applied = false, directShapes = dss.Count, matched = plan.Count, unmatched, counts });
            try { System.IO.File.WriteAllText(LogPath, ""); } catch { }
            var pp = new Preproc();
            int done = 0, failed = 0; var errs = new List<string>();
            object? topo = null;
            using (var t = new Transaction(doc, "MCP 基地模材質"))
            {
                var o = t.GetFailureHandlingOptions(); o.SetFailuresPreprocessor(pp); o.SetForcedModalHandling(false); t.SetFailureHandlingOptions(o);
                t.Start();
                var mats = new Dictionary<string, ElementId>();
                ElementId MatId(string name, int[]? rgb, int tr)
                {
                    if (mats.TryGetValue(name, out var id)) return id;
                    var m = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(x => x.Name == name);
                    if (m == null) m = doc.GetElement(Material.Create(doc, name)) as Material;
                    if (rgb != null && rgb.Length == 3) { m!.Color = new Color((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]); m.UseRenderAppearanceForShading = false; }
                    m!.Transparency = Math.Max(0, Math.Min(100, tr));
                    mats[name] = m.Id; return m.Id;
                }
                double tol = 5 / 304.8, cosA = Math.Cos(1.0 * Math.PI / 180);
                for (int i = 0; i < plan.Count; i++)
                {
                    var (ds, rule) = plan[i];
                    try
                    {
                        var mid = MatId(rule.Material, rule.Rgb, rule.Transparency);
                        var tris = new List<XYZ[]>();
                        Collect(ds.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }), tris);
                        if (tris.Count == 0) { failed++; continue; }
                        var faces = MergeCoplanar(tris, tol, cosA) ?? tris.Select(x => new List<List<XYZ>> { x.ToList() }).ToList();
                        IList<GeometryObject>? geo = null;
                        try { geo = Build(Project(faces), true, mid); } catch { }
                        if (geo == null || geo.Count == 0) geo = Build(tris.Select(x => new List<List<XYZ>> { x.ToList() }).ToList(), true, mid);
                        if (geo == null || geo.Count == 0) { failed++; continue; }
                        ds.SetShape(geo); done++;
                    }
                    catch (Exception ex) { failed++; if (errs.Count < 8) errs.Add($"{ds.Id.IntegerValue}: {ex.Message}"); }
                    if (i % 200 == 0) Log($"mat {i}/{plan.Count} done={done} failed={failed}");
                }
                if (req.TopoMaterial != null && !string.IsNullOrEmpty(req.TopoMaterial.Material))
                {
#if REVIT2024_OR_GREATER
                    var mid = MatId(req.TopoMaterial.Material, req.TopoMaterial.Rgb, req.TopoMaterial.Transparency);
                    var notes = new List<string>();
                    var typeIds = new FilteredElementCollector(doc).OfClass(typeof(Toposolid)).Cast<Toposolid>().Select(x => x.GetTypeId()).Distinct().ToList();
                    foreach (var tid in typeIds)
                    {
                        try
                        {
                            var tt = doc.GetElement(tid) as HostObjAttributes;
                            var cs = tt?.GetCompoundStructure();
                            if (cs == null) { notes.Add($"{tid.IntegerValue}: no structure"); continue; }
                            for (int k = 0; k < cs.LayerCount; k++) cs.SetMaterialId(k, mid);
                            tt!.SetCompoundStructure(cs); notes.Add($"{tt.Name}: {cs.LayerCount} layers");
                        }
                        catch (Exception ex) { notes.Add($"{tid.IntegerValue}: {ex.Message}"); }
                    }
                    topo = notes;
#else
                    topo = new List<string> { "Toposolid 需要 Revit 2024 以上" };
#endif
                }
                t.Commit();
            }
            Log("materials committed");
            return Ok(new { applied = true, done, failed, unmatched, counts, topo, errors = errs, failures = pp.Notes.Take(10) });
        }
        public class MatRule
        {
            [JsonProperty("contains")] public List<string>? Contains { get; set; }
            [JsonProperty("material")] public string Material { get; set; } = "";
            [JsonProperty("rgb")] public int[]? Rgb { get; set; }
            [JsonProperty("transparency")] public int Transparency { get; set; }
        }
        public class MatReq
        {
            [JsonProperty("document")] public string? Document { get; set; }
            [JsonProperty("applicationId")] public string? ApplicationId { get; set; } = "RiR_site";
            [JsonProperty("rules")] public List<MatRule>? Rules { get; set; }
            [JsonProperty("topoMaterial")] public MatRule? TopoMaterial { get; set; }
            [JsonProperty("apply")] public bool Apply { get; set; }
        }

        public class CadReq
        {
            [JsonProperty("document")] public string? Document { get; set; }
            [JsonProperty("paths")] public List<string>? Paths { get; set; }
            [JsonProperty("level")] public string? Level { get; set; }
            [JsonProperty("thisViewOnly")] public bool ThisViewOnly { get; set; } = false;
            [JsonProperty("pin")] public bool Pin { get; set; } = true;
        }

        public class OpenReq { [JsonProperty("path")] public string? Path { get; set; } [JsonProperty("audit")] public bool Audit { get; set; } }
        public class SimplifyReq
        {
            [JsonProperty("document")] public string? Document { get; set; }
            [JsonProperty("applicationId")] public string? ApplicationId { get; set; } = "RiR_site";
            [JsonProperty("angleDeg")] public double AngleDeg { get; set; } = 1.0;
            [JsonProperty("distMm")] public double DistMm { get; set; } = 5.0;
            [JsonProperty("topoCellM")] public double TopoCellM { get; set; } = 45.0;
            [JsonProperty("limit")] public int Limit { get; set; } = 0;
            [JsonProperty("apply")] public bool Apply { get; set; } = false;
        }
    }
}
