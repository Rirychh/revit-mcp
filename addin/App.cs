// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.Reflection;
using Autodesk.Revit.UI;
using RevitMcpAddin.UI;

namespace RevitMcpAddin
{
    /// <summary>
    /// Revit Add-in 進入點：
    ///   - 隨 Revit 啟動本地 HTTP 伺服器（供外部 MCP server 呼叫）
    ///   - 註冊 Claude 聊天 Dockable Pane ＋ Ribbon 按鈕（面板內直接跟 Claude 對話）
    /// </summary>
    public class App : IExternalApplication
    {
        private RevitHttpServer? _server;
        private RevitApiExecutor? _executor;
        private ChatPanel? _panel;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // 1) 註冊 Dockable Pane（必須在 OnStartup 完成）
                _panel = new ChatPanel();
                application.RegisterDockablePane(ChatPanel.PaneId, "Claude · Revit", _panel);

                // 2) 不自建 Ribbon 按鈕——統一由 pyRevit 的「Claude AI」按鈕開啟本面板，
                //    避免兩個入口。（ShowPanelCommand 仍保留，供需要時手動掛按鈕。）

                // 3) 用 Idling 事件在主緒取得 UIApplication 後啟動 HTTP server + 綁定執行器
                application.Idling += OnIdling;

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("RevitMCP Error", $"OnStartup failed: {ex.Message}");
                return Result.Failed;
            }
        }

        private bool _started = false;

        private void OnIdling(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (_started) return;
            if (sender is not UIApplication uiApp) return;

            try
            {
                _executor = new RevitApiExecutor();          // 主緒建立 ExternalEvent
                if (_panel != null) { _panel.Executor = _executor; _panel.UiApp = uiApp; }

                _server = new RevitHttpServer(uiApp, _executor);
                _server.Start();
                _started = true;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("RevitMCP Error", $"啟動失敗：{ex.Message}");
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            _server?.Stop();
            return Result.Succeeded;
        }
    }
}
