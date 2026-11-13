# Revit MCP — © 2026 Rita (Rirychh)
# https://github.com/Rirychh/revit-mcp
# SPDX-License-Identifier: MIT

# 把 RevitMCP.ico 設到「Revit MCP 安裝」視窗（標題列＋工作列）。由 _雙擊我_RevitMCP安裝.hta 開啟時在背景呼叫；存檔必須 UTF-8 with BOM
# 原因：HTA 使用 IE11 文件模式時，<hta:application icon=...> 不會生效
$ErrorActionPreference = 'SilentlyContinue'
$ico = Join-Path $PSScriptRoot 'RevitMCP.ico'
if (-not (Test-Path -LiteralPath $ico)) { exit }
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class HtaIcon {
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr LoadImage(IntPtr h, string name, uint type, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
}
"@
$title = 'Revit MCP 安裝'
for ($i = 0; $i -lt 50; $i++) {
    $hw = [HtaIcon]::FindWindow('HTML Application Host Window Class', $title)
    if ($hw -ne [IntPtr]::Zero) {
        $big = [HtaIcon]::LoadImage([IntPtr]::Zero, $ico, 1, [HtaIcon]::GetSystemMetrics(11), [HtaIcon]::GetSystemMetrics(12), 0x10)
        $sm  = [HtaIcon]::LoadImage([IntPtr]::Zero, $ico, 1, [HtaIcon]::GetSystemMetrics(49), [HtaIcon]::GetSystemMetrics(50), 0x10)
        [void][HtaIcon]::SendMessage($hw, 0x80, [IntPtr]1, $big)
        [void][HtaIcon]::SendMessage($hw, 0x80, [IntPtr]0, $sm)
        break
    }
    Start-Sleep -Milliseconds 200
}