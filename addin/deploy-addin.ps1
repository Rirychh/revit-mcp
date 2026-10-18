# Revit MCP — © 2026 Rita (Rirychh)
# https://github.com/Rirychh/revit-mcp
# SPDX-License-Identifier: MIT

<#
  部署 Revit MCP add-in（支援多版本）。

  用法：
    powershell -ExecutionPolicy Bypass -File deploy-addin.ps1
    powershell -ExecutionPolicy Bypass -File deploy-addin.ps1 -Versions 2024
    powershell -ExecutionPolicy Bypass -File deploy-addin.ps1 -SkipBuild

  部署前必須完全關閉 Revit，否則 DLL 正在使用中無法覆蓋。
#>
param(
  [int[]] $Versions = @(2025, 2024, 2022),
  [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"
$src = $PSScriptRoot

if (Get-Process Revit -ErrorAction SilentlyContinue) {
  Write-Host "[ERROR] Revit 還在執行中，請完全關閉後再試一次。" -ForegroundColor Red
  exit 1
}

$stamp = Get-Date -Format "yyyyMMdd_HHmmss"

foreach ($v in $Versions) {
  Write-Host "`n=== Revit $v ===" -ForegroundColor Cyan

  if (-not (Test-Path "C:\Program Files\Autodesk\Revit $v\RevitAPI.dll")) {
    Write-Host "[SKIP] 這台機器沒有安裝 Revit $v" -ForegroundColor Yellow
    continue
  }

  if (-not $SkipBuild) {
    Write-Host "建置中..."
    & dotnet build "$src\RevitMcpAddin.csproj" -c Release -p:RevitVersion=$v -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "[ERROR] Revit $v 建置失敗" -ForegroundColor Red; exit 1 }
  }

  $tfm = if ($v -ge 2025) { "net8.0-windows" } else { "net48" }
  $dll = "$src\bin\Revit$v\Release\$tfm\RevitMcpAddin.dll"
  if (-not (Test-Path $dll)) { Write-Host "[ERROR] 找不到建置輸出：$dll" -ForegroundColor Red; exit 1 }

  $dstDir = "C:\ProgramData\Autodesk\Revit\Addins\$v"
  New-Item -ItemType Directory -Force -Path $dstDir | Out-Null

  $dstDll = "$dstDir\RevitMcpAddin.dll"
  if (Test-Path $dstDll) { Copy-Item $dstDll "$dstDll.bak_$stamp" -Force }

  Copy-Item $dll                        $dstDll -Force
  Copy-Item "$src\RevitMcpAddin.addin"  "$dstDir\RevitMcpAddin.addin" -Force

  Write-Host "[DONE] 已部署到 $dstDir" -ForegroundColor Green
}

Write-Host "`n完成。開啟 Revit 後，每個實例會自動綁定 48884 起的第一個空埠；" -ForegroundColor Green
Write-Host "在 Claude 用 revit_list_instances 查看、revit_use_instance 切換。" -ForegroundColor Green
