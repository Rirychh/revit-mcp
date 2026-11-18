// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using System;
using System.Runtime.CompilerServices;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 讓直接使用 Transaction 的 Handler 不會彈出 Revit 的失敗對話框。
    /// 對話框會卡住 Revit 主執行緒：呼叫端等到逾時，之後的請求全部排隊。
    ///
    /// 用法：把 tx.Start() 換成 TxGuard.Start(tx)，tx.Commit() 換成 TxGuard.Commit(tx)。
    ///  - 警告：吞掉（不跳對話框），照常提交。
    ///  - 錯誤：整筆回復，Commit 丟出例外並帶 Revit 的錯誤說明；模型沒有變更。
    /// </summary>
    internal static class TxGuard
    {
        private static readonly ConditionalWeakTable<Transaction, RollbackOnError> _armed = new();

        public static void Start(Transaction tx)
        {
            tx.Start();
            var pre = new RollbackOnError();
            var fo = tx.GetFailureHandlingOptions();
            fo.SetFailuresPreprocessor(pre);
            fo.SetClearAfterRollback(true);
            tx.SetFailureHandlingOptions(fo);
            _armed.Remove(tx);
            _armed.Add(tx, pre);
        }

        public static void Commit(Transaction tx)
        {
            _armed.TryGetValue(tx, out var pre);
            var st = tx.Commit();
            if (st == TransactionStatus.Committed && (pre == null || !pre.HadError)) return;

            var why = pre != null && pre.Errors.Count > 0 ? string.Join("；", pre.Errors) : $"交易未提交（{st}）";
            throw new InvalidOperationException("Revit 拒絕這次修改，已整筆回復、模型沒有變更：" + why);
        }
    }
}
