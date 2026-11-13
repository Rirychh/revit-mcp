// Revit MCP © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT
#if MARKETPLACE
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMcpAddin.UI
{
    /// <summary>把隨附的 revit-mcp-server.exe 寫入 Claude Desktop 的 claude_desktop_config.json（保留其他設定）。</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class ConnectClaudeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
                string exe = Path.GetFullPath(Path.Combine(asmDir, "..", "server", "revit-mcp-server.exe"));
                if (!File.Exists(exe))
                {
                    TaskDialog.Show("Revit MCP", "找不到 MCP server：\n" + exe + "\n請重新安裝。");
                    return Result.Failed;
                }

                var configs = FindConfigs();
                if (configs.Count == 0)
                {
                    TaskDialog.Show("Revit MCP", "找不到 Claude Desktop。請先安裝並開啟一次 Claude Desktop，再按這個按鈕。");
                    return Result.Cancelled;
                }

                foreach (var cfg in configs) Write(cfg, exe);

                TaskDialog.Show("Revit MCP",
                    "已連接 Claude Desktop。\n\n請完全結束 Claude Desktop（系統匣圖示 → 結束）後重新開啟，" +
                    "就能在 Claude 中使用 Revit 工具。\n\n設定檔：\n" + string.Join("\n", configs));
                return Result.Succeeded;
            }
            catch (System.Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>一般安裝版在 %APPDATA%\Claude；Microsoft Store（MSIX）版在套件的虛擬化 Roaming 目錄。</summary>
        private static List<string> FindConfigs()
        {
            var list = new List<string>();
            string roaming = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            string local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            string std = Path.Combine(roaming, "Claude");
            if (Directory.Exists(std)) list.Add(Path.Combine(std, "claude_desktop_config.json"));
            string pkgs = Path.Combine(local, "Packages");
            if (Directory.Exists(pkgs))
            {
                foreach (var d in Directory.GetDirectories(pkgs, "Claude_*"))
                {
                    string v = Path.Combine(d, "LocalCache", "Roaming", "Claude");
                    if (Directory.Exists(v)) list.Add(Path.Combine(v, "claude_desktop_config.json"));
                }
            }
            return list;
        }

        private static void Write(string cfgPath, string exe)
        {
            JObject root = File.Exists(cfgPath)
                ? (JObject.Parse(File.ReadAllText(cfgPath)) ?? new JObject())
                : new JObject();
            if (File.Exists(cfgPath)) File.Copy(cfgPath, cfgPath + ".bak-revitmcp", true);
            if (root["mcpServers"] is not JObject servers) { servers = new JObject(); root["mcpServers"] = servers; }
            servers["revit"] = new JObject { ["command"] = exe, ["args"] = new JArray() };
            File.WriteAllText(cfgPath, root.ToString(Formatting.Indented), new System.Text.UTF8Encoding(false));
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class HelpCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Process.Start(new ProcessStartInfo(MarketplaceRibbon.HelpUrl) { UseShellExecute = true });
            return Result.Succeeded;
        }
    }
}
#endif