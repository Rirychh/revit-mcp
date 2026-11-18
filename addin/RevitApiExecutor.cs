// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Autodesk.Revit.UI;

namespace RevitMcpAddin
{
    /// <summary>
    /// 在 Revit 主執行緒同步執行工作的共用執行器。
    /// Revit API 只能在主緒呼叫；背景緒（HTTP server／聊天面板）透過本類別
    /// 用 ExternalEvent 把工作排到主緒，並同步等待結果。
    ///
    /// 每個呼叫各自一個 Job（結果、例外、完成訊號都不共用），所以：
    ///  - 等待逾時而工作還沒開始 → 標記取消，之後主緒輪到它時直接略過，模型不會在
    ///    呼叫端已收到「失敗」之後才被改到。
    ///  - 等待逾時而工作已在執行 → 無法中止 Revit API，回報「仍在執行」；
    ///    晚到的結果只寫回它自己的 Job，不會污染下一個呼叫。
    ///  - 帶 key 的工作（會改模型的請求）仍在排隊或執行時，同一個 key 再進來會被拒絕，
    ///    避免呼叫端逾時後重送，讓同一個修改做兩次。
    /// </summary>
    public class RevitApiExecutor : IExternalEventHandler
    {
        private const int Queued = 0, Running = 1, Finished = 2, Cancelled = 3;

        private sealed class Job
        {
            public Func<UIApplication, string> Work = _ => "";
            public string Result = "";
            public Exception? Error;
            public int State = Queued;
            public string? Key;
            public readonly ManualResetEventSlim Done = new(false);
        }

        private readonly ExternalEvent _externalEvent;
        private readonly ConcurrentQueue<Job> _queue = new();
        private readonly ConcurrentDictionary<string, Job> _inflight = new();

        public RevitApiExecutor()
        {
            _externalEvent = ExternalEvent.Create(this);
        }

        /// <summary>
        /// 背景緒呼叫：把 job 排到 Revit 主緒執行並同步等待回傳。
        /// key 不為 null 時啟用重複執行防護（同一 key 尚未結束就不再排入）。
        /// </summary>
        public string Execute(Func<UIApplication, string> job, int timeoutMs = 300000, string? key = null)
        {
            var j = new Job { Work = job, Key = key };

            if (key != null && !_inflight.TryAdd(key, j))
                throw new InvalidOperationException(
                    "相同的請求還在 Revit 裡排隊或執行中，這次沒有重複送出。" +
                    "請等它完成後用查詢工具確認結果，不要重送同一個修改。");

            _queue.Enqueue(j);
            _externalEvent.Raise();

            if (!j.Done.Wait(timeoutMs))
            {
                // 還沒輪到：取消，主緒之後看到 Cancelled 會略過。
                if (Interlocked.CompareExchange(ref j.State, Cancelled, Queued) == Queued)
                {
                    if (key != null) _inflight.TryRemove(key, out _);
                    throw new TimeoutException(
                        $"Revit 主執行緒 {timeoutMs / 1000} 秒內沒有空檔（可能有對話框開著、指令進行中或沒有開啟文件）。" +
                        "這個請求沒有執行，已取消，模型沒有變更。");
                }

                // 已經在跑：Revit API 無法中途打斷，只能據實回報。
                // （剛好在逾時的瞬間完成就當作完成，往下回傳結果。）
                if (Volatile.Read(ref j.State) != Finished)
                throw new TimeoutException(
                    $"Revit 已開始執行這個請求，但 {timeoutMs / 1000} 秒內沒有完成，可能仍在進行。" +
                    "請勿重送同一個修改；等 Revit 忙完後先用查詢工具確認結果。");
            }

            if (j.Error != null) throw j.Error;
            return j.Result;
        }

        /// <summary>由 Revit 在主緒回呼執行：把佇列裡還有效的工作依序做完。</summary>
        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var j))
            {
                if (Interlocked.CompareExchange(ref j.State, Running, Queued) != Queued)
                    continue;   // 已被呼叫端取消

                try { j.Result = j.Work(app) ?? ""; }
                catch (Exception ex) { j.Error = ex; }
                finally
                {
                    Interlocked.Exchange(ref j.State, Finished);
                    if (j.Key != null) _inflight.TryRemove(j.Key, out _);
                    j.Done.Set();
                }
            }
        }

        public string GetName() => "RevitMCP Api Executor";
    }
}
