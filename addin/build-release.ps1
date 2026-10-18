# Revit MCP — © 2026 Rita (Rirychh)
# https://github.com/Rirychh/revit-mcp
# SPDX-License-Identifier: MIT

# ============================================================
# 產出同仁用安裝包：installer\out\RevitMCP安裝包_v<版本>.zip
#   1) 編譯 add-in 2022–2027   2) 編譯 MCP server（TS）
#   3) 組裝 payload（add-in、server＋正式相依套件、隨附 node.exe）  4) 打包 zip
# 用法：powershell -ExecutionPolicy Bypass -File build-release.ps1 [-SkipBuild]
# ============================================================
param([switch]$SkipBuild)
$ErrorActionPreference = "Stop"
$root   = $PSScriptRoot
$tsRepo = Join-Path (Split-Path $root) "revit-mcp"
$out    = Join-Path $root "installer\out"
$stage  = Join-Path $out "RevitMCP安裝包"
$ver    = ([xml](Get-Content (Join-Path $root "RevitMcpAddin.csproj") -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$Versions = 2022..2027

if (-not $SkipBuild) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "build-all.ps1")
    if ($LASTEXITCODE -ne 0) { throw "add-in 編譯失敗" }
    Push-Location $tsRepo; try { & npm.cmd run build; if ($LASTEXITCODE -ne 0) { throw "MCP server 編譯失敗" } } finally { Pop-Location }
}

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
$payload = Join-Path $stage "payload"

# 安裝程式本體
Copy-Item (Join-Path $root "installer\_雙擊我_RevitMCP安裝.hta") $stage
Copy-Item (Join-Path $root "installer\install_revitmcp.ps1") $stage
Copy-Item (Join-Path $root "installer\安裝說明.txt") $stage

# add-in
foreach ($v in $Versions) {
    $dll = Get-ChildItem (Join-Path $root "bin\Revit$v\Release") -Recurse -Filter RevitMcpAddin.dll | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $dll) { throw "找不到 Revit $v 的編譯結果" }
    $d = Join-Path $payload "addin\$v"; New-Item -ItemType Directory -Force $d | Out-Null
    Copy-Item $dll.FullName $d
}

# MCP server：dist＋正式相依套件（不含 TypeScript 等開發套件）
$mcp = Join-Path $payload "mcp"
New-Item -ItemType Directory -Force $mcp | Out-Null
Copy-Item (Join-Path $tsRepo "dist") $mcp -Recurse
Copy-Item (Join-Path $tsRepo "package.json"), (Join-Path $tsRepo "package-lock.json") $mcp
Get-ChildItem (Join-Path $mcp "dist") -Recurse -Include *.map, *.d.ts | Remove-Item -Force
Push-Location $mcp; try { & npm.cmd ci --omit=dev --no-audit --no-fund --loglevel=error; if ($LASTEXITCODE -ne 0) { throw "npm ci 失敗" } } finally { Pop-Location }

# 隨附 Node.js（同仁不必另外安裝）
New-Item -ItemType Directory -Force (Join-Path $payload "node") | Out-Null
Copy-Item (Get-Command node.exe).Source (Join-Path $payload "node\node.exe")

[IO.File]::WriteAllText((Join-Path $payload "version.txt"), "RevitMCP $ver  build $(Get-Date -Format 'yyyy-MM-dd HH:mm')`r`n", (New-Object Text.UTF8Encoding($false)))

# 打包（Python zipfile：zip 內一律用 / 路徑）
$zip = Join-Path $out "RevitMCP安裝包_v$ver.zip"
$py = @'
import sys, os, zipfile
root, out = sys.argv[1], sys.argv[2]
base = os.path.basename(root.rstrip("\\/"))
if os.path.exists(out): os.remove(out)
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for dp, dn, fn in os.walk(root):
        for f in fn:
            full = os.path.join(dp, f)
            z.write(full, base + "/" + os.path.relpath(full, root).replace(os.sep, "/"))
with zipfile.ZipFile(out) as z:
    names = z.namelist()
    assert not [n for n in names if "\\" in n]
    print("entries:", len(names), " size: %.1f MB" % (os.path.getsize(out) / 1048576))
'@
$pyFile = Join-Path $env:TEMP "revitmcp_pack.py"
[IO.File]::WriteAllText($pyFile, $py, (New-Object Text.UTF8Encoding($false)))
$python = @("C:\Python314\python.exe", "C:\Python313\python.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $python) { $python = (Get-Command python).Source }
& $python $pyFile $stage $zip
if ($LASTEXITCODE -ne 0) { throw "打包失敗" }
Write-Output "完成：$zip"
