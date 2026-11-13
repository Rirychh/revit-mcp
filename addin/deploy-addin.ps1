# Revit MCP — © 2026 Rita (Rirychh)
# https://github.com/Rirychh/revit-mcp
# SPDX-License-Identifier: MIT

<#
  部署 Revit MCP add-in（支援多版本）。

  用法：
    powershell -ExecutionPolicy Bypass -File deploy-addin.ps1
    powershell -ExecutionPolicy Bypass -File deploy-addin.ps1 -Versions 2024
    powershell -ExecutionPolicy Bypass -File deploy-addin.ps1 -Versions 2024,2025
    powershell -ExecutionPolicy Bypass -File deploy-addin.ps1 -SkipBuild

  不給 -Versions 時，部署到 2022–2027 之中這台機器有安裝的所有版本。

  部署前必須先存檔並完全關閉 Revit，否則 DLL 正在使用中無法覆蓋。
  這支腳本只會檢查，不會幫你關 Revit。
#>
param(
  # 用 string[] 而不是 int[]：經由 -File 呼叫時 "2024,2025" 會整串當成一個參數傳進來。
  [string[]] $Versions,
  [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"
$src = $PSScriptRoot
$supported = 2022..2027

function Test-RevitInstalled([int] $v) {
  # 只看 Revit.exe。解除安裝後常會留下資料夾與登錄機碼，那些不算有裝。
  Test-Path (Join-Path $env:ProgramFiles "Autodesk\Revit $v\Revit.exe")
}

function Get-TargetFramework([int] $v) {
  # 必須跟 RevitMcpAddin.csproj 的 TargetFramework 條件一致。
  if ($v -ge 2027) { "net10.0-windows" }
  elseif ($v -ge 2025) { "net8.0-windows" }
  else { "net48" }
}

# ── 決定要部署哪些版本 ─────────────────────────────────────────────
if ($Versions) {
  $targets = @()
  foreach ($item in ($Versions -split '[,;\s]+' | Where-Object { $_ })) {
    $n = 0
    if (-not [int]::TryParse($item, [ref] $n) -or $supported -notcontains $n) {
      Write-Host "[ERROR] 不支援的版本：$item（支援 $($supported[0])–$($supported[-1])）" -ForegroundColor Red
      exit 1
    }
    if ($targets -notcontains $n) { $targets += $n }
  }
  foreach ($v in $targets) {
    if (-not (Test-RevitInstalled $v)) {
      Write-Host "[SKIP] 這台機器沒有安裝 Revit $v" -ForegroundColor Yellow
    }
  }
  $targets = @($targets | Where-Object { Test-RevitInstalled $_ })
} else {
  $targets = @($supported | Where-Object { Test-RevitInstalled $_ })
}

if ($targets.Count -eq 0) {
  Write-Host "[ERROR] 找不到可部署的 Revit（檢查範圍 $($supported[0])–$($supported[-1])）。" -ForegroundColor Red
  exit 1
}
Write-Host "要部署的版本：$($targets -join ', ')"

# ── Revit 必須已關閉（只檢查，不強制關）───────────────────────────
$running = @(Get-Process Revit -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
  Write-Host "[ERROR] Revit 還在執行中。請先存檔並完全關閉後再試一次：" -ForegroundColor Red
  foreach ($p in $running) {
    Write-Host ("        pid {0}  {1}" -f $p.Id, $p.MainWindowTitle) -ForegroundColor Red
  }
  exit 1
}

# ── 建置工具：csproj 用 C# 14，需要 .NET 10 SDK ────────────────────
$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet10\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
$env:DOTNET_CLI_UI_LANGUAGE = "en"

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"

foreach ($v in $targets) {
  Write-Host "`n=== Revit $v ===" -ForegroundColor Cyan

  if (-not $SkipBuild) {
    Write-Host "建置中..."
    $log = & $dotnet build "$src\RevitMcpAddin.csproj" -c Release "-p:RevitVersion=$v" -v q --nologo "-clp:ErrorsOnly;NoSummary"
    if ($LASTEXITCODE -ne 0) {
      Write-Host "[ERROR] Revit $v 建置失敗：" -ForegroundColor Red
      $log | Select-Object -First 40 | ForEach-Object { Write-Host "        $_" }
      exit 1
    }
  }

  $tfm = Get-TargetFramework $v
  $dll = "$src\bin\Revit$v\Release\$tfm\RevitMcpAddin.dll"
  if (-not (Test-Path $dll)) { Write-Host "[ERROR] 找不到建置輸出：$dll" -ForegroundColor Red; exit 1 }

  $dstDir = Join-Path $env:ProgramData "Autodesk\Revit\Addins\$v"
  New-Item -ItemType Directory -Force -Path $dstDir | Out-Null

  $dstDll = "$dstDir\RevitMcpAddin.dll"
  if (Test-Path $dstDll) { Copy-Item $dstDll "$dstDll.bak_$stamp" -Force }

  Copy-Item $dll                        $dstDll -Force
  Copy-Item "$src\RevitMcpAddin.addin"  "$dstDir\RevitMcpAddin.addin" -Force

  Write-Host "[DONE] 已部署到 $dstDir" -ForegroundColor Green
}

Write-Host "`n完成。開啟 Revit 後，每個實例會自動綁定 48884 起的第一個空埠；" -ForegroundColor Green
Write-Host "在 Claude 用 revit_list_instances 查看、revit_use_instance 切換。" -ForegroundColor Green
