// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMcpAddin.Handlers;

namespace RevitMcpAddin
{
    /// <summary>
    /// 在 Revit 程序內部啟動輕量 HTTP 伺服器（127.0.0.1:48884 起往上找空埠）
    /// 供 MCP Server 呼叫 Revit API。多個 Revit 實例各自綁一個埠。
    ///
    /// 存取控制（三層，全部 fail closed）：
    ///   1) 只綁 127.0.0.1（不是 "localhost"，避免靠 http.sys 的主機名特例而實際聽在 ::）
    ///   2) 每次啟動產生隨機 token，寫到 %LOCALAPPDATA%\revit-mcp\port-{port}.token；
    ///      所有請求都必須帶 X-Revit-Mcp-Token，否則 401。
    ///   3) 帶 Origin header 的請求一律 403 —— 本機 MCP client 不會帶，瀏覽器一定帶。
    /// 注意：Revit 內的聊天面板不走 HTTP（直接呼叫 static Route），不受本檔認證影響。
    /// </summary>
    public class RevitHttpServer
    {
        /// <summary>掃描起始埠。每個 Revit 實例會佔用範圍內的其中一個埠。</summary>
        public const int PortRangeStart = 48884;
        public const int PortRangeCount = 10;

        /// <summary>Client 必須帶的認證 header 名稱。</summary>
        public const string TokenHeader = "X-Revit-Mcp-Token";

        /// <summary>本實例實際綁定的埠；尚未啟動時為 0。</summary>
        public static int CurrentPort { get; private set; }

        /// <summary>本次啟動的 shared secret；尚未啟動時為 null（null 代表一律拒絕）。</summary>
        private static string? _token;

        private HttpListener? _listener;
        private readonly UIApplication _uiApp;
        private readonly RevitApiExecutor _executor;
        private CancellationTokenSource? _cts;

        public RevitHttpServer(UIApplication uiApp, RevitApiExecutor executor)
        {
            _uiApp = uiApp;
            _executor = executor;
        }

        /// <summary>
        /// 從 PortRangeStart 往上找第一個可用的埠並綁定。
        /// 這讓多個 Revit 實例能同時各自跑一個 HTTP server，
        /// 由 MCP server 端掃描整個範圍來探索與選擇目標實例。
        /// </summary>
        public void Start()
        {
            Exception? last = null;

            for (int port = PortRangeStart; port < PortRangeStart + PortRangeCount; port++)
            {
                var listener = new HttpListener();
                // 明確綁 IP。用 "localhost" 時 http.sys 會把它當成通用主機名特例，
                // 實際 TCP 監聽位址是 ::（全介面），只靠 Host header 過濾。
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    _listener   = listener;
                    CurrentPort = port;
                    // 先備好 token 再開始收請求，避免「已在聽但還沒有 token」的空窗。
                    _token = GenerateToken();
                    WriteTokenFile(port, _token);
                    _cts = new CancellationTokenSource();
                    Task.Run(() => ListenLoop(_cts.Token));
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    _token = null;
                    try { listener.Close(); } catch { }
                }
            }

            throw new InvalidOperationException(
                $"No free port in {PortRangeStart}-{PortRangeStart + PortRangeCount - 1}. " +
                $"Last error: {last?.Message}");
        }

        public void Stop()
        {
            var port = CurrentPort;
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            _listener   = null;
            // 先關掉認證再刪檔：就算刪檔失敗，_token = null 也讓後續請求一律 401。
            _token      = null;
            CurrentPort = 0;
            if (port != 0) DeleteTokenFile(port);
        }

        // ── 認證：token 檔 ───────────────────────────────────────────

        /// <summary>token 檔路徑。TS client 端讀同一個路徑。</summary>
        private static string TokenFilePath(int port) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "revit-mcp", $"port-{port}.token");

        private static string GenerateToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>每次啟動覆蓋。資料夾不存在就建。</summary>
        private static void WriteTokenFile(int port, string token)
        {
            var path = TokenFilePath(port);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 不帶 BOM：client 端直接讀字串比對，BOM 會讓比對失敗。
            File.WriteAllText(path, token, new UTF8Encoding(false));
        }

        private static void DeleteTokenFile(int port)
        {
            try
            {
                var path = TokenFilePath(port);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                // 刪不掉不是致命問題：server 已停，token 也已作廢，下次啟動會覆蓋。
                Console.Error.WriteLine($"[RevitMCP] token file cleanup failed: {ex.Message}");
            }
        }

        /// <summary>定時比較，避免用字串比較洩漏 token 前綴。</summary>
        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>
        /// 認證閘門。回 true＝通過；回 false＝拒絕，status/reason 為要回給 client 的內容。
        /// fail closed：_token 為 null（尚未啟動／已停止）時一律拒絕，
        /// 絕不因為「沒有 token 可比對」而放行。
        /// </summary>
        private static bool Authorize(HttpListenerRequest req, out int status, out string reason)
        {
            // 防線 0：只接受 loopback 來源。prefix 已綁 127.0.0.1，這層是雙重保險。
            var remote = req.RemoteEndPoint?.Address;
            if (remote == null || !IPAddress.IsLoopback(remote))
            {
                status = 403; reason = "Forbidden: non-loopback origin.";
                return false;
            }

            // 防線 1：帶 Origin 的一律拒絕。
            // 本機 MCP client（Node/axios）不會帶 Origin；瀏覽器發的跨源請求一定帶，
            // 而 text/plain 的 POST 屬 CORS simple request、不觸發 preflight，
            // 所以必須在這裡自己擋，不能靠 CORS。
            var origin = req.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin))
            {
                status = 403; reason = "Forbidden: browser-originated request.";
                return false;
            }

            // 防線 2：shared secret。
            var expected = _token;
            var presented = req.Headers[TokenHeader];
            if (string.IsNullOrEmpty(expected) ||
                string.IsNullOrEmpty(presented) ||
                !ConstantTimeEquals(presented!, expected!))
            {
                status = 401; reason = $"Unauthorized: missing or invalid {TokenHeader}.";
                return false;
            }

            status = 200; reason = "";
            return true;
        }

        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var listener = _listener;
                    if (listener == null) break;

                    var ctx = await listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(ctx), ct);
                }
                catch (Exception) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[RevitMCP] ListenLoop error: {ex.Message}");
                }
            }
        }

        private async Task HandleRequest(HttpListenerContext ctx)
        {
            var req  = ctx.Request;
            var resp = ctx.Response;
            resp.ContentType = "application/json; charset=utf-8";

            try
            {
                // 認證最優先：不通過就直接回應並結束，連 body 都不讀、不進任何 Handler。
                if (!Authorize(req, out var denyStatus, out var denyReason))
                {
                    var deny = Encoding.UTF8.GetBytes(
                        JsonConvert.SerializeObject(new { success = false, error = denyReason }));
                    resp.StatusCode = denyStatus;
                    resp.ContentLength64 = deny.Length;
                    await resp.OutputStream.WriteAsync(deny, 0, deny.Length);
                    return;
                }

                var path   = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
                var method = req.HttpMethod.ToUpperInvariant();
                string body = "";

                // 一律以 UTF-8 讀 body。HttpListener 在 Content-Type 沒帶 charset 時，
                // ContentEncoding 會回系統預設編碼（中文 Windows 是 Big5），中文參數會變亂碼。
                if (req.HasEntityBody)
                    using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
                        body = await sr.ReadToEndAsync();

                // /ping：直接在監聽執行緒回答，不排隊進 Revit 主執行緒。
                // 用途是分辨「Revit 沒開」與「Revit 開著但忙碌／有對話框擋住」——
                // 後者連 /info 都會逾時，因為 /info 也要等主執行緒。
                if (path == "/ping" && method == "GET")
                {
                    var ping = JsonConvert.SerializeObject(new
                    {
                        success = true,
                        data = new
                        {
                            server = "revit-mcp-addin",
                            port   = CurrentPort,
                            pid    = System.Diagnostics.Process.GetCurrentProcess().Id
                        }
                    });
                    var pb = Encoding.UTF8.GetBytes(ping);
                    resp.StatusCode = 200;
                    resp.ContentLength64 = pb.Length;
                    await resp.OutputStream.WriteAsync(pb, 0, pb.Length);
                    return;
                }

                // Dispatch 路由到各 Handler（透過 ExternalEvent 在 Revit 主執行緒執行）
                string json = await Task.Run(() =>
                    _executor.Execute(app => Route(app, method, path, body, ParseQuery(req.Url))));

                var bytes = Encoding.UTF8.GetBytes(json);
                resp.StatusCode = 200;
                resp.ContentLength64 = bytes.Length;
                await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
            catch (Exception ex)
            {
                var err = JsonConvert.SerializeObject(new { success = false, error = ex.Message });
                var bytes = Encoding.UTF8.GetBytes(err);
                resp.StatusCode = 500;
                resp.ContentLength64 = bytes.Length;
                await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            }
            finally { resp.OutputStream.Close(); }
        }

        /// <summary>路由到各 Handler。static＋傳入 UIApplication，供 HTTP server 與聊天面板共用。</summary>
        public static string Route(UIApplication uiApp, string method, string path, string body,
                             System.Collections.Specialized.NameValueCollection qs)
        {
            // /info：多實例探索用。刻意放在 ActiveUIDocument 檢查之前，
            // 因為還沒開啟任何模型的 Revit 也必須能被辨識出來。
            if (path == "/info" && method == "GET")
                return BuildInfo(uiApp);

            // /families/build：建族不需要開啟專案（zero-doc 也可），放在文件檢查之前
            if (path == "/families/build" && method == "POST")
                return new FamilyHandler(uiApp).Build(body);
            if (path == "/families/labels" && method == "POST")
                return new FamilyHandler(uiApp).ScanLabels(body);

            // /project/new：由樣板建立新專案（含長度單位與中央檔），
            // 不需要先開啟任何模型，所以放在文件檢查之前。
            if (path == "/documents/open" && method == "POST")
                return new SiteSimplifyHandler(uiApp).Open(body);

            if (path == "/project/new" && method == "POST")
                return new BuildHandler(uiApp).NewProject(body);

            var doc = uiApp.ActiveUIDocument?.Document
                      ?? throw new InvalidOperationException("No active Revit document.");

            var elementHandler = new ElementHandler(doc);
            var viewHandler    = new ViewHandler(uiApp, doc);
            var exportHandler  = new ExportHandler(uiApp, doc);

            return path switch
            {
                // ── 元件 ────────────────────────────────────
                "/elements" when method == "GET"  => elementHandler.ListElements(qs),
                "/elements" when method == "POST" => elementHandler.CreateElement(body),
                "/elements/rotate"     when method == "POST" => new TransformHandler(doc).Rotate(body),
                "/families/inspect"    when method == "POST" => new FamilyInspectHandler(doc).Inspect(body),
                "/elements/align-upright" when method == "POST" => new AlignUprightHandler(doc).Align(body),
                "/elements/constraints" when method == "POST" => new ConstraintFinderHandler(doc).Find(body),
                "/groups/ungroup" when method == "POST" => new UngroupHandler(doc).Ungroup(body),
                "/sync"                when method == "POST" => new SyncHandler(doc).Sync(body),
                "/sync/status"         when method == "GET"  => new SyncHandler(doc).Status(),
                "/elements/copy" when method == "POST" => new CopyHandler(doc).CopyElements(body),
                "/elements/snap" when method == "POST" => new SnapHandler(doc).Snap(body),
                // 連結模型（ST/MEP）裡的構件；必須排在 /elements/{id} 之前，否則會被當成 id 解析
                "/elements/linked" when method == "GET" => new LinkedElementHandler(doc).ListElements(qs),

                // /elements/{id}
                var p when p.StartsWith("/elements/") && method == "GET"
                    => elementHandler.GetElement(ParseId(p, "/elements/")),
                var p when p.StartsWith("/elements/") && method == "PUT"
                    => elementHandler.ModifyElement(ParseId(p, "/elements/"), body),

                // ── 樓層 / 房間 ──────────────────────────────
                "/levels" when method == "GET" => elementHandler.ListLevels(),

                // ── 批次建模（標高／牆／樓板／圓柱）──────────
                "/levels"     when method == "POST" => new BuildHandler(uiApp, doc).CreateLevels(body),
                "/buildtypes" when method == "GET"  => new BuildHandler(uiApp, doc).ListBuildTypes(qs),
                "/walls"      when method == "POST" => new BuildHandler(uiApp, doc).CreateWalls(body),
                "/floors"     when method == "POST" => new BuildHandler(uiApp, doc).CreateFloors(body),
                "/columns"    when method == "POST" => new BuildHandler(uiApp, doc).CreateColumns(body),

                "/rooms"  when method == "GET" => elementHandler.ListRooms(qs),
                "/rooms/linked" when method == "GET" => new LinkedRoomHandler(doc).ListRooms(qs),

                // ── 房間放大圖 / 圖紙 / 排圖 ────────────
                "/rooms/views"   when method == "POST" => new RoomSheetHandler(doc).CreateRoomViews(body),
                "/sheets/layout" when method == "GET"  => new RoomSheetHandler(doc).ListSheets(qs["number"]),
                "/sheets/batch-create" when method == "POST" => new RoomSheetHandler(doc).CreateSheets(body),
                "/sheets/place"  when method == "POST" => new RoomSheetHandler(doc).PlaceViewports(body),
                "/documents"       when method == "GET"  => new RoomSheetHandler(doc).ListDocuments(),
                "/documents/close" when method == "POST" => new RoomSheetHandler(doc).CloseDocument(body),
                "/drafting/view/update" when method == "POST" => new DraftingHandler(doc).UpdateDraftingView(body),
                "/drafting/delete"      when method == "POST" => new DraftingHandler(doc).DeleteElements(body),

                // ── 製圖視圖（索引表／位置配置圖）────────────
                "/drafting/types"   when method == "GET"  => new DraftingHandler(doc).ListTypes(qs),
                "/drafting/view"    when method == "POST" => new DraftingHandler(doc).CreateDraftingView(body),
                "/drafting/content" when method == "POST" => new DraftingHandler(doc).AddContent(body),
                "/drafting/clear"   when method == "POST" => new DraftingHandler(doc).ClearContent(body),

                // ── 視圖 ─────────────────────────────────────
                "/views"  when method == "GET"  => viewHandler.ListViews(qs),
                "/views"  when method == "POST" => viewHandler.CreateView(body),

                "/views/crop" when method == "POST" => new CropHandler(doc).CropView(body),
                "/views/duplicate" when method == "POST" => new DuplicateHandler(doc).DuplicateViews(body),
                "/views/rotate-crop" when method == "POST" => new CropRotateHandler(doc).RotateCrop(body),
                    "/views/templates"  when method == "GET"  => new ViewSettingsHandler(doc).ListTemplates(),
                    "/views/template"   when method == "POST" => new ViewSettingsHandler(doc).ApplyTemplate(body),
                    "/views/categories" when method == "POST" => new ViewSettingsHandler(doc).SetCategories(body),

                var p when p.StartsWith("/views/") && p.EndsWith("/screenshot") && method == "GET"
                    => viewHandler.TakeScreenshot(ParseId(p, "/views/", "/screenshot")),

                // ── 圖紙／修訂 ───────────────────────────────
                "/sheets"                 when method == "GET"  => new SheetHandler(doc).ListSheets(),
                "/sheets/create"          when method == "POST" => new SheetHandler(doc).CreateSheet(body),
                "/sheets/starting-view"   when method == "POST" => new SheetHandler(doc).SetStartingView(body),
                "/sheets/export-pdf"      when method == "POST" => new SheetHandler(doc).ExportPdf(body),
                "/sheets/revision-notes"  when method == "POST" => new SheetHandler(doc).RevisionNotes(body),
                "/revisions"              when method == "GET"  => new SheetHandler(doc).ListRevisions(),
                "/revisions/cloud-notes"  when method == "POST" => new SheetHandler(doc).CloudNotes(body),

                // ── 匯出 ─────────────────────────────────────
                "/export/ifc"      when method == "POST" => exportHandler.ExportIfc(body),
                "/export/schedule" when method == "POST" => exportHandler.ExportSchedule(body),

                // ── 線段整理（封閉/不自相交）────────────────
                "/lines/close-loop" when method == "POST" => new LineLoopHandler(doc).CloseLoop(body),

                // ── 地界線（替代元素：模型線＋群組）─────────────
                "/site/property-line" when method == "POST" => new PropertyLineAltHandler(doc).Create(body),
                "/links/cad"             when method == "POST" => new SiteSimplifyHandler(uiApp).LinkCad(body),
                "/mass/boxes"            when method == "POST" => new MassBoxHandler(uiApp).Boxes(body),
                "/site/simplify"         when method == "POST" => new SiteSimplifyHandler(uiApp).Simplify(body),
                "/site/directshapes"     when method == "POST" => new SiteSimplifyHandler(uiApp).DirectShapes(body),
                "/lines/draw"            when method == "POST" => new CadLinesHandler(uiApp).Draw(body),
                "/links/move"            when method == "POST" => new CadLinesHandler(uiApp).MoveLink(body),
                "/materials/appearance"  when method == "POST" => new CadLinesHandler(uiApp).Appearance(body),
                "/site/materials"        when method == "POST" => new SiteSimplifyHandler(uiApp).Materials(body),

                // ── 文件操作：存檔／切換／連結／清除模型 ──────────
                "/documents/info"        when method == "GET"  => new DocumentOpsHandler(uiApp).Info(qs),
                "/design-options"        when method == "GET"  => new DesignOptionHandler(uiApp).List(qs),
                "/selection"             when method == "POST" => new DesignOptionHandler(uiApp).Select(body),
                "/documents/save"        when method == "POST" => new DocumentOpsHandler(uiApp).Save(body),
                    "/links/delete-elements" when method == "POST" => new LinkEditHandler(uiApp).DeleteElements(body),
                "/documents/activate"    when method == "POST" => new DocumentOpsHandler(uiApp).Activate(body),
                "/documents/clear-model" when method == "POST" => new DocumentOpsHandler(uiApp).ClearModel(body),
                "/links/revit"           when method == "POST" => new DocumentOpsHandler(uiApp).LinkRevit(body),

                // ── 模型健檢／效能／警告 ─────────────────────
                "/qc/health"        when method == "GET"  => new ModelQcHandler(doc).Health(qs),
                "/qc/performance"   when method == "POST" => new ModelQcHandler(doc).Performance(body),
                "/warnings"         when method == "GET"  => new WarningsHandler(doc).List(qs),
                "/warnings/resolve" when method == "POST" => new WarningsHandler(doc).Resolve(body),

                // ── 清除未使用的族 ─────────────────────────
                "/purge/scan"   when method == "POST" => new PurgeHandler(doc).Scan(body),
                "/purge/delete" when method == "POST" => new PurgeHandler(doc).Delete(body),

                // ── 標註：設備編號標籤 / 尺寸鏈 ──────────────
                "/annotations/tagtypes"   when method == "GET"  => new AnnotationHandler(doc).ListTagTypes(qs),
                "/annotations/viewtags"   when method == "GET"  => new AnnotationHandler(doc).ListViewAnnotations(qs),
                "/annotations/tags"       when method == "POST" => new AnnotationHandler(doc).TagElements(body),
                "/annotations/deletetags" when method == "POST" => new AnnotationHandler(doc).DeleteViewTags(body),
                "/annotations/dimensions" when method == "POST" => new AnnotationHandler(doc).CreateDimension(body),

                _ => JsonConvert.SerializeObject(new { success = false, error = $"Unknown route: {method} {path}" })
            };
        }

        /// <summary>
        /// 自己解析查詢字串。HttpListener 的 QueryString 用系統預設編碼解 %XX，
        /// 中文 Windows 會走 Big5，?name=廁所 就變成亂碼，所以一律用 UTF-8 自己解。
        /// </summary>
        private static NameValueCollection ParseQuery(Uri? url)
        {
            var nv = new NameValueCollection();
            var q = url?.Query;
            if (string.IsNullOrEmpty(q)) return nv;
            if (q!.StartsWith("?")) q = q.Substring(1);

            foreach (var pair in q.Split('&'))
            {
                if (pair.Length == 0) continue;
                var i = pair.IndexOf('=');
                var k = i < 0 ? pair : pair.Substring(0, i);
                var v = i < 0 ? "" : pair.Substring(i + 1);
                nv[Uri.UnescapeDataString(k.Replace("+", " "))] =
                    Uri.UnescapeDataString(v.Replace("+", " "));
            }
            return nv;
        }

        /// <summary>
        /// 回報這個 Revit 實例的身分，讓 MCP server 在多實例情境下能分辨
        /// 「48884 是哪個 Revit、開的是哪個模型」。
        /// </summary>
        private static string BuildInfo(UIApplication uiApp)
        {
            var app  = uiApp.Application;
            var docs = new List<string>();

            foreach (Autodesk.Revit.DB.Document d in app.Documents)
                if (!d.IsLinked)
                    docs.Add(string.IsNullOrEmpty(d.PathName) ? d.Title : d.PathName);

            var activeUiDoc = uiApp.ActiveUIDocument;
            var active      = activeUiDoc?.Document;

            var info = new
            {
                server             = "revit-mcp-addin",
                port               = CurrentPort,
                pid                = System.Diagnostics.Process.GetCurrentProcess().Id,
                revitVersion       = app.VersionNumber,
                revitBuild         = app.VersionBuild,
                username           = app.Username,
                activeDocument     = active?.Title ?? "",
                activeDocumentPath = active?.PathName ?? "",
                activeView         = activeUiDoc?.ActiveView?.Name ?? "",
                documents          = docs,
                addinVersion       = typeof(RevitHttpServer).Assembly.GetName().Version?.ToString(),
            };

            return JsonConvert.SerializeObject(new { success = true, data = info });
        }

        private static long ParseId(string path, string prefix, string? suffix = null)
        {
            var part = path.Substring(prefix.Length);
            if (suffix != null && part.EndsWith(suffix))
                part = part.Substring(0, part.Length - suffix.Length);
            return long.TryParse(part, out var id) ? id
                : throw new ArgumentException($"Invalid element id in path: {path}");
        }
    }
}
