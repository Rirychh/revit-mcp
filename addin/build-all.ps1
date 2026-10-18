# Revit MCP — © 2026 Rita (Rirychh)
# https://github.com/Rirychh/revit-mcp
# SPDX-License-Identifier: MIT

param([string[]]$Versions = @("2022","2023","2024","2025","2026","2027"))
# 一次編譯所有 Revit 版本。輸出：bin\Revit<ver>\Release\<tfm>\RevitMcpAddin.dll
$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet10\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
$env:DOTNET_CLI_UI_LANGUAGE = "en"
$proj = Join-Path $PSScriptRoot "RevitMcpAddin.csproj"
$ok = @(); $bad = @()
foreach ($v in $Versions) {
    $log = & $dotnet build $proj -c Release "-p:RevitVersion=$v" --nologo -v q -clp:"ErrorsOnly;NoSummary" 2>&1
    if ($LASTEXITCODE -eq 0) { $ok += $v; Write-Output "[OK]   Revit $v" }
    else {
        $bad += $v; Write-Output "[FAIL] Revit $v"
        $log | Select-String "error" | ForEach-Object { $_.Line -replace [regex]::Escape($PSScriptRoot + "\"), "" } |
            Sort-Object -Unique | Select-Object -First 40 | ForEach-Object { "       $_" }
    }
}
Write-Output ("OK: " + ($ok -join ",") + "   FAIL: " + ($bad -join ","))
if ($bad.Count) { exit 1 }
