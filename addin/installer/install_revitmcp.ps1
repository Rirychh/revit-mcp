# Revit MCP — © 2026 Rita (Rirychh)
# https://github.com/Rirychh/revit-mcp
# SPDX-License-Identifier: MIT

# ============================================================
# Revit MCP 安裝腳本（由 _雙擊我_RevitMCP安裝.hta 呼叫；存檔必須 UTF-8 with BOM）
#   -Mode install   安裝 Revit add-in（2022–2027 有裝的版本）→ MCP server → Claude Desktop 設定（v1.3.0 起不需要授權碼）
#   -Mode uninstall 移除 add-in、MCP server、Claude Desktop 設定
# 進度寫入 %TEMP%\revitmcp_progress.txt（STEPn|... / DONE|... / FAIL|...）供 HTA 輪詢
# 測試用：-TestRoot <資料夾> 會把 APPDATA / LOCALAPPDATA / ProgramData 全部改到該資料夾底下
# ============================================================
param(
    [ValidateSet("install","uninstall")][string]$Mode = "install",
    [string]$TestRoot = ""
)
$ErrorActionPreference = "Stop"
$Utf8NoBom = New-Object Text.UTF8Encoding($false)
$Versions = 2022..2027

# ── 路徑 ─────────────────────────────────────────────
if ($TestRoot) {
    $AppData = Join-Path $TestRoot "Roaming"; $LocalAppData = Join-Path $TestRoot "Local"; $ProgramData = Join-Path $TestRoot "ProgramData"
} else {
    $AppData = $env:APPDATA; $LocalAppData = $env:LOCALAPPDATA; $ProgramData = $env:ProgramData
}
$OldLicFile = Join-Path $AppData "Rirychh\RevitMCP\license.key"   # v1.2.0 留下的授權碼，已不使用
$InstallDir = Join-Path $LocalAppData "Rirychh\RevitMCP"
$Payload    = Join-Path $PSScriptRoot "payload"
$Progress   = Join-Path $env:TEMP "revitmcp_progress.txt"

function Report($line) {
    for ($i = 0; $i -lt 12; $i++) {
        try { Add-Content -Path $Progress -Value $line -Encoding Unicode -ErrorAction Stop; break }
        catch { Start-Sleep -Milliseconds 150 }
    }
}
for ($i = 0; $i -lt 12; $i++) { try { Set-Content -Path $Progress -Value "START" -Encoding Unicode -ErrorAction Stop; break } catch { Start-Sleep -Milliseconds 150 } }

# ── Claude Desktop 設定檔（一般安裝與 Microsoft Store 版都處理）──
function Get-ClaudeConfigs {
    $list = @()
    $d1 = Join-Path $AppData "Claude"
    if (Test-Path $d1) { $list += (Join-Path $d1 "claude_desktop_config.json") }
    $pk = Join-Path $LocalAppData "Packages"
    if (Test-Path $pk) {
        Get-ChildItem $pk -Directory -Filter "Claude_*" -ErrorAction SilentlyContinue | ForEach-Object {
            $d = Join-Path $_.FullName "LocalCache\Roaming\Claude"
            if (Test-Path $d) { $list += (Join-Path $d "claude_desktop_config.json") }
        }
    }
    return $list
}

function Set-ClaudeEntry([string]$cfgPath, $entry) {
    if (Test-Path $cfgPath) {
        Copy-Item $cfgPath ($cfgPath + ".bak_revitmcp_" + (Get-Date -Format "yyyyMMdd_HHmmss")) -Force
        $raw = [IO.File]::ReadAllText($cfgPath)
        try { $config = $raw | ConvertFrom-Json } catch { throw "Claude 設定檔不是有效的 JSON：$cfgPath" }
        if ($null -eq $config) { $config = [PSCustomObject]@{} }
    } else { $config = [PSCustomObject]@{} }
    if (-not ($config.PSObject.Properties.Name -contains "mcpServers")) {
        $config | Add-Member -MemberType NoteProperty -Name "mcpServers" -Value ([PSCustomObject]@{})
    }
    if ($null -eq $entry) {
        if ($config.mcpServers.PSObject.Properties.Name -contains "revit") { $config.mcpServers.PSObject.Properties.Remove("revit") }
    } elseif ($config.mcpServers.PSObject.Properties.Name -contains "revit") { $config.mcpServers.revit = $entry }
    else { $config.mcpServers | Add-Member -MemberType NoteProperty -Name "revit" -Value $entry }
    $json = $config | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($cfgPath, $json, $Utf8NoBom)          # 不能有 BOM
    $null = [IO.File]::ReadAllText($cfgPath) | ConvertFrom-Json      # 寫完再讀一次確認是有效 JSON
}

function Get-InstalledRevit {
    $Versions | Where-Object { Test-Path "C:\Program Files\Autodesk\Revit $_\Revit.exe" }
}

function Assert-RevitClosed {
    if ($TestRoot) { return }   # 測試模式不碰真正的 Revit
    if (Get-Process -Name Revit -ErrorAction SilentlyContinue) { throw "Revit 還開著。請先存檔、關閉所有 Revit 後再安裝。" }
}

# ============================================================
try {
    switch ($Mode) {

    "install" {
        Assert-RevitClosed
        Remove-Item $OldLicFile -Force -ErrorAction SilentlyContinue

        # STEP3 Revit add-in（每位使用者自己的 Addins 資料夾，不需要系統管理員權限）
        $revits = @(Get-InstalledRevit)
        if ($revits.Count -eq 0) { throw "這台電腦找不到 Revit 2022–2027。" }
        $done = @()
        foreach ($v in $revits) {
            $src = Join-Path $Payload "addin\$v\RevitMcpAddin.dll"
            if (-not (Test-Path $src)) { continue }
            $addinRoot = Join-Path $AppData "Autodesk\Revit\Addins\$v"
            $dllDir = Join-Path $addinRoot "RevitMcpAddin"
            New-Item -ItemType Directory -Force $dllDir | Out-Null
            Copy-Item $src (Join-Path $dllDir "RevitMcpAddin.dll") -Force
            $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Revit MCP Addin</Name>
    <Assembly>$dllDir\RevitMcpAddin.dll</Assembly>
    <FullClassName>RevitMcpAddin.App</FullClassName>
    <ClientId>12345678-1234-1234-1234-123456789012</ClientId>
    <VendorId>Rirychh</VendorId>
    <VendorDescription>Revit MCP · © 2026 Rita (Rirychh)</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
            [IO.File]::WriteAllText((Join-Path $addinRoot "RevitMcpAddin.addin"), $manifest, $Utf8NoBom)

            # 舊版裝在 ProgramData（全機共用）會與這份重複載入 → 停用舊的
            $old = Join-Path $ProgramData "Autodesk\Revit\Addins\$v\RevitMcpAddin.addin"
            if (Test-Path $old) {
                try { Rename-Item $old ($old + ".disabled") -Force }
                catch { throw "Revit $v 有舊版安裝在 $old，需要系統管理員權限才能停用。請在 HTA 上按右鍵「以系統管理員身分執行」後重試。" }
            }
            $done += $v
        }
        if ($done.Count -eq 0) { throw "安裝包裡沒有符合這台電腦 Revit 版本的檔案。" }
        Report ("STEP3|" + ($done -join ","))

        # STEP4 MCP server ＋ 隨附的 Node.js
        $srv = Join-Path $InstallDir "server"
        $nodeDir = Join-Path $InstallDir "node"
        New-Item -ItemType Directory -Force $srv, $nodeDir | Out-Null
        $rc = (Start-Process robocopy -ArgumentList @("`"$Payload\mcp`"", "`"$srv`"", "/MIR", "/NFL", "/NDL", "/NJH", "/NJS", "/NP", "/R:2", "/W:1") -Wait -PassThru -WindowStyle Hidden).ExitCode
        if ($rc -ge 8) { throw "複製 MCP server 失敗（robocopy $rc）。若 Claude Desktop 開著，請先完全關閉（含系統匣圖示）後重試。" }
        Copy-Item (Join-Path $Payload "node\node.exe") (Join-Path $nodeDir "node.exe") -Force
        Copy-Item (Join-Path $Payload "version.txt") (Join-Path $InstallDir "version.txt") -Force -ErrorAction SilentlyContinue
        Report "STEP4|server"

        # STEP5 Claude Desktop 設定
        $cfgs = @(Get-ClaudeConfigs)
        if ($cfgs.Count -eq 0) { throw "找不到 Claude Desktop 的設定資料夾。請先安裝並開過一次 Claude Desktop。" }
        $entry = [PSCustomObject]@{ command = (Join-Path $nodeDir "node.exe"); args = @((Join-Path $srv "dist\index.js")) }
        foreach ($c in $cfgs) { Set-ClaudeEntry $c $entry }
        Report "STEP5|claude"

        Report ("DONE|" + ($done -join ","))
    }

    "uninstall" {
        Assert-RevitClosed
        foreach ($v in $Versions) {
            $addinRoot = Join-Path $AppData "Autodesk\Revit\Addins\$v"
            Remove-Item (Join-Path $addinRoot "RevitMcpAddin.addin") -Force -ErrorAction SilentlyContinue
            Remove-Item (Join-Path $addinRoot "RevitMcpAddin") -Recurse -Force -ErrorAction SilentlyContinue
        }
        Report "STEP3|addin"
        Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
        Report "STEP4|server"
        foreach ($c in @(Get-ClaudeConfigs)) { if (Test-Path $c) { Set-ClaudeEntry $c $null } }
        Report "STEP5|claude"
        Report "DONE|uninstall"
    }
    }
}
catch {
    Report ("FAIL|" + $_.Exception.Message)
    exit 1
}
