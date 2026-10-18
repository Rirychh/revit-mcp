// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMcpAddin.UI
{
    /// <summary>Ribbon 按鈕：顯示 Claude 聊天面板。</summary>
    [Transaction(TransactionMode.Manual)]
    public class ShowPanelCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var pane = commandData.Application.GetDockablePane(ChatPanel.PaneId);
                pane.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
