// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using System.Collections.Generic;

namespace RevitMcpAddin.Handlers
{
    /// <summary>吞掉警告；遇到任何錯誤就整筆回復，絕不彈出對話框、絕不刪除約束或標註。</summary>
    public class RollbackOnError : IFailuresPreprocessor
    {
        public List<string> Errors { get; } = new List<string>();
        public List<string> SwallowedWarnings { get; } = new List<string>();
        public bool HadError { get; private set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
        {
            bool err = false;
            foreach (FailureMessageAccessor f in fa.GetFailureMessages())
            {
                var sev = f.GetSeverity();
                if (sev == FailureSeverity.Warning)
                {
                    SwallowedWarnings.Add(f.GetDescriptionText());
                    fa.DeleteWarning(f);
                }
                else
                {
                    err = true;
                    Errors.Add(f.GetDescriptionText());
                }
            }
            if (err) { HadError = true; return FailureProcessingResult.ProceedWithRollBack; }
            return FailureProcessingResult.Continue;
        }
    }
}