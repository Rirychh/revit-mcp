// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitMcpAddin.Handlers
{
    /// <summary>
    /// 交易期間吞掉警告對話框（例如「房間計算只有在不考慮下列元素的情況下才會成功」）。
    /// 只處理 Warning：解除警告後繼續。真正的 Error 仍然照常拋出，不會被靜默。
    /// </summary>
    public class SwallowWarnings : IFailuresPreprocessor
    {
        public List<string> Swallowed { get; } = new List<string>();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
        {
            foreach (FailureMessageAccessor f in fa.GetFailureMessages())
            {
                if (f.GetSeverity() == FailureSeverity.Warning)
                {
                    Swallowed.Add(f.GetDescriptionText());
                    fa.DeleteWarning(f);
                }
            }
            return FailureProcessingResult.Continue;
        }
    }
}