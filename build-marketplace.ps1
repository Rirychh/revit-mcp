# build-marketplace.ps1 — 產生 Autodesk App Store 用的 RevitMCP.bundle（Revit 2022–2027）
# 需要：%LOCALAPPDATA%\Microsoft\dotnet10\dotnet.exe、Node.js 22+
# 已產生的 _bundle\RevitMCP.bundle 內的 .addin／PackageContents.xml GUID 固定，改版只更新二進位與 AppVersion。
$ErrorActionPreference = 'Stop'
$M = $PSScriptRoot; $B = "$M\_bundle\RevitMCP.bundle"
$dn = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet10\dotnet.exe'; $env:DOTNET_CLI_UI_LANGUAGE = 'en'
foreach ($v in 2022..2027) {
  & $dn build "$M\addin\RevitMcpAddin.csproj" -c Release "-p:RevitVersion=$v" -p:Marketplace=true -o "$M\_out\$v" --nologo -v q -clp:"ErrorsOnly;NoSummary"
  if ($LASTEXITCODE) { throw "Revit $v build failed" }
  Get-ChildItem "$M\_out\$v" -File | Where-Object Extension -ne '.pdb' | Copy-Item -Destination "$B\Contents\$v" -Force
}
Push-Location "$M\server"
cmd /c "npm.cmd ci --no-audit --no-fund --loglevel=error && npm.cmd i --no-save --no-audit --no-fund --loglevel=error esbuild postject && npx.cmd tsc -p . && npx.cmd esbuild dist/index.js --bundle --platform=node --format=cjs --target=node22 --outfile=sea/bundle.cjs"
if ($LASTEXITCODE) { throw 'server build failed' }
'{"main":"bundle.cjs","output":"sea-prep.blob","disableExperimentalSEAWarning":true,"useCodeCache":false}' | Set-Content -Encoding ascii sea\sea-config.json
Push-Location sea; node --experimental-sea-config sea-config.json
Copy-Item (Get-Command node).Source revit-mcp-server.exe -Force
cmd /c "npx.cmd postject revit-mcp-server.exe NODE_SEA_BLOB sea-prep.blob --sentinel-fuse NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2 --overwrite"
Pop-Location; Pop-Location
Copy-Item "$M\server\sea\revit-mcp-server.exe" "$B\Contents\server\" -Force
Compress-Archive -Path $B -DestinationPath "$M\_bundle\RevitMCP.bundle.zip" -Force
Write-Host "done: $M\_bundle\RevitMCP.bundle.zip"