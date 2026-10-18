// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.UI;

namespace RevitMcpAddin
{
    /// <summary>
    /// 在 Revit 主執行緒同步執行工作的共用執行器。
    /// Revit API 只能在主緒呼叫；背景緒（HTTP server／聊天面板）透過本類別
    /// 用 ExternalEvent 把工作排到主緒，並同步等待結果。
    /// </summary>
    public class RevitApiExecutor : IExternalEventHandler
    {
        private readonly ExternalEvent _externalEvent;
        private Func<UIApplication, string>? _job;
        private string _result = "";
        private Exception? _error;
        private readonly ManualResetEventSlim _done = new(false);
        private readonly object _gate = new();

        public RevitApiExecutor()
        {
            _externalEvent = ExternalEvent.Create(this);
        }

        /// <summary>背景緒呼叫：把 job 排到 Revit 主緒執行並同步等待回傳。</summary>
        public string Execute(Func<UIApplication, string> job, int timeoutMs = 300000)
        {
            lock (_gate)
            {
                _job = job;
                _error = null;
                _result = "";
                _done.Reset();

                _externalEvent.Raise();
                if (!_done.Wait(timeoutMs))
                    throw new TimeoutException("Revit 主執行緒逾時未回應（可能正忙或無開啟文件）。");

                if (_error != null) throw _error;
                return _result;
            }
        }

        /// <summary>由 Revit 在主緒回呼執行。</summary>
        public void Execute(UIApplication app)
        {
            try { _result = _job?.Invoke(app) ?? ""; }
            catch (Exception ex) { _error = ex; }
            finally { _done.Set(); }
        }

        public string GetName() => "RevitMCP Api Executor";
    }
}
