# Revit MCP — © 2026 Rita (Rirychh)
# https://github.com/Rirychh/revit-mcp
# SPDX-License-Identifier: MIT

# ============================================================
# Revit MCP 授權管理工具（發行者專用，只在發行者電腦上執行）
#   私鑰：%APPDATA%\Rirychh\RevitMCP-Issuer\issuer.key（DPAPI，只有這個 Windows 帳號解得開）
#   發出紀錄：issued.csv；撤銷名單：revoked.json（簽章，放 NAS 供 add-in 讀取）
# 用法：
#   雙擊「授權管理.bat」→ 互動選單
#   或：授權管理.ps1 -Action init|issue|revoke|list|config|export|import|pubkey
#       -Request <申請碼> -Name <姓名> -Months 12 -LicenseId <id> -RevokedPath <UNC>
# ============================================================
param(
    [string]$Action = "",
    [string]$Request = "",
    [string]$Name = "",
    [int]$Months = 12,
    [string]$LicenseId = "",
    [string]$RevokedPath = "",
    [string]$BackupFile = "",
    [string]$Password = ""
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Security
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$Dir       = Join-Path $env:APPDATA "Rirychh\RevitMCP-Issuer"
$KeyFile   = Join-Path $Dir "issuer.key"
$CfgFile   = Join-Path $Dir "config.json"
$IssuedCsv = Join-Path $Dir "issued.csv"
$RevLocal  = Join-Path $Dir "revoked.json"
$PubOut    = Join-Path $PSScriptRoot "public-key.txt"
New-Item -ItemType Directory -Force $Dir | Out-Null
$Utf8 = New-Object Text.UTF8Encoding($false)

function B64U([byte[]]$b) { [Convert]::ToBase64String($b).TrimEnd('=').Replace('+','-').Replace('/','_') }
function UnB64U([string]$s) { $s = $s.Replace('-','+').Replace('_','/'); switch ($s.Length % 4) { 2 { $s += '==' } 3 { $s += '=' } }; [Convert]::FromBase64String($s) }

function Load-Config {
    if (Test-Path $CfgFile) { return Get-Content $CfgFile -Raw -Encoding UTF8 | ConvertFrom-Json }
    return [PSCustomObject]@{ revokedPath = "" }
}
function Save-Config($c) { [IO.File]::WriteAllText($CfgFile, ($c | ConvertTo-Json), $Utf8) }

function Load-Key {
    if (-not (Test-Path $KeyFile)) { throw "尚未建立金鑰，請先執行 init（選單 1）。" }
    $blob = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($KeyFile), $null, 'CurrentUser')
    $k = [Security.Cryptography.CngKey]::Import($blob, [Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob)
    return New-Object Security.Cryptography.ECDsaCng($k)
}
function PubFromBlob([byte[]]$blob) {
    # ECCPRIVATEBLOB / ECCPUBLICBLOB: magic(4) cbKey(4) X(32) Y(32) [d(32)]
    $x = New-Object byte[] 32; $y = New-Object byte[] 32
    [Array]::Copy($blob, 8, $x, 0, 32); [Array]::Copy($blob, 40, $y, 0, 32)
    return @{ X = [Convert]::ToBase64String($x); Y = [Convert]::ToBase64String($y) }
}
function Sign([string]$text) {
    $ec = Load-Key
    try { return B64U ($ec.SignData([Text.Encoding]::ASCII.GetBytes($text), [Security.Cryptography.HashAlgorithmName]::SHA256)) }
    finally { $ec.Dispose() }
}

function Do-Init {
    if (Test-Path $KeyFile) { throw "金鑰已存在（$KeyFile）。重建會讓所有已發授權失效，若真的要重建請先手動刪除該檔。" }
    $p = New-Object Security.Cryptography.CngKeyCreationParameters
    $p.ExportPolicy = [Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
    $k = [Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::ECDsaP256, $null, $p)
    $blob = $k.Export([Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob)
    [IO.File]::WriteAllBytes($KeyFile, [Security.Cryptography.ProtectedData]::Protect($blob, $null, 'CurrentUser'))
    $pub = PubFromBlob $blob
    [IO.File]::WriteAllText($PubOut, "X=$($pub.X)`r`nY=$($pub.Y)`r`n", $Utf8)
    Write-Output "已建立金鑰：$KeyFile"
    Write-Output "公鑰已寫入：$PubOut（編譯 add-in 與安裝包時使用）"
    Write-Output "請立刻用 export 匯出一份有密碼的備份，存到安全的地方。遺失私鑰只能重建並重發所有授權。"
}

function Do-PubKey {
    $ec = Load-Key
    try { $pub = PubFromBlob ($ec.Key.Export([Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)) } finally { $ec.Dispose() }
    [IO.File]::WriteAllText($PubOut, "X=$($pub.X)`r`nY=$($pub.Y)`r`n", $Utf8)
    Write-Output "X=$($pub.X)"; Write-Output "Y=$($pub.Y)"
}

function Parse-Request([string]$req) {
    $req = $req.Trim()
    if (-not $req.StartsWith("RMCPREQ-")) { throw "申請碼格式不對（應以 RMCPREQ- 開頭）。" }
    $j = [Text.Encoding]::UTF8.GetString((UnB64U $req.Substring(8))) | ConvertFrom-Json
    if (-not $j.u -or -not $j.m) { throw "申請碼內容不完整。" }
    return $j
}

function Do-Issue {
    if ($Request -eq "") { $script:Request = Read-Host "貼上同仁的申請碼" }
    $r = Parse-Request $Request
    Write-Output ("申請者：帳號 {0}｜電腦 {1}" -f $r.u, $r.h)
    if ($Name -eq "") { $script:Name = Read-Host "姓名／備註" }
    if ($script:Interactive) { $mIn = Read-Host "有效月數（直接 Enter = $Months）"; if ($mIn -match '^\d+$') { $script:Months = [int]$mIn } }
    $cfg = Load-Config
    $today = Get-Date
    $id = "RM-" + $today.ToString("yyyyMMdd") + "-" + ([Guid]::NewGuid().ToString("N").Substring(0,6).ToUpper())
    $payload = [ordered]@{
        v = 1; id = $id; n = $Name; u = "$($r.u)".ToLower(); m = "$($r.m)".ToLower(); h = "$($r.h)"
        iat = $today.ToString("yyyy-MM-dd"); exp = $today.AddMonths($Months).ToString("yyyy-MM-dd"); r = "$($cfg.revokedPath)"
    }
    $p64 = B64U ($Utf8.GetBytes(($payload | ConvertTo-Json -Compress)))
    $code = "RMCP1.$p64." + (Sign $p64)
    if (-not (Test-Path $IssuedCsv)) { [IO.File]::WriteAllText($IssuedCsv, "id,name,user,host,issued,expires`r`n", (New-Object Text.UTF8Encoding($true))) }
    Add-Content $IssuedCsv ("{0},{1},{2},{3},{4},{5}" -f $id, $Name, $payload.u, $payload.h, $payload.iat, $payload.exp) -Encoding UTF8
    try { Set-Clipboard -Value $code } catch {}
    Write-Output ""
    Write-Output "授權碼（已複製到剪貼簿）："
    Write-Output $code
    Write-Output ""
    Write-Output "授權編號 $id，到期 $($payload.exp)"
}

function Read-Revoked {
    if (-not (Test-Path $RevLocal)) { return @() }
    $o = Get-Content $RevLocal -Raw -Encoding UTF8 | ConvertFrom-Json
    $j = [Text.Encoding]::UTF8.GetString((UnB64U $o.payload)) | ConvertFrom-Json
    return @($j.revoked)
}
function Write-Revoked([string[]]$ids) {
    $payload = [ordered]@{ v = 1; updated = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ss"); revoked = @($ids) }
    $p64 = B64U ($Utf8.GetBytes(($payload | ConvertTo-Json -Compress)))
    $doc = [ordered]@{ payload = $p64; sig = (Sign $p64) } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($RevLocal, $doc, $Utf8)
    $cfg = Load-Config
    if ($cfg.revokedPath) {
        try { [IO.File]::WriteAllText($cfg.revokedPath, $doc, $Utf8); Write-Output "已更新 NAS 撤銷名單：$($cfg.revokedPath)" }
        catch { Write-Output "[警告] 寫入 NAS 失敗：$($_.Exception.Message)。本機副本已更新：$RevLocal" }
    } else { Write-Output "[注意] 尚未設定 NAS 撤銷名單位置（config），只更新了本機副本。" }
}

function Do-Revoke {
    if ($LicenseId -eq "") { Do-List; $script:LicenseId = Read-Host "要撤銷的授權編號" }
    $ids = @(Read-Revoked)
    if ($ids -contains $LicenseId) { Write-Output "$LicenseId 已在撤銷名單中。"; return }
    Write-Revoked (@($ids) + $LicenseId)
    Write-Output "已撤銷 $LicenseId。同仁的 Revit 下次啟動並連得到 NAS 時生效。"
}

function Do-List {
    if (-not (Test-Path $IssuedCsv)) { Write-Output "尚未發出任何授權。"; return }
    $rev = @(Read-Revoked)
    Import-Csv $IssuedCsv -Encoding UTF8 | ForEach-Object {
        $st = if ($rev -contains $_.id) { "已撤銷" } elseif ([datetime]$_.expires -lt (Get-Date)) { "已到期" } else { "有效" }
        [PSCustomObject]@{ 編號 = $_.id; 姓名 = $_.name; 帳號 = $_.user; 電腦 = $_.host; 到期 = $_.expires; 狀態 = $st }
    } | Format-Table -AutoSize | Out-String -Width 200
}

function Do-Config {
    $cfg = Load-Config
    if ($RevokedPath -eq "") { Write-Output "目前撤銷名單位置：$($cfg.revokedPath)"; $script:RevokedPath = Read-Host "新的位置（完整檔案路徑，例如 \\nas\...\revoked.json；直接 Enter 不變）" }
    if ($RevokedPath -ne "") { $cfg.revokedPath = $RevokedPath; Save-Config $cfg; Write-Output "已設定：$RevokedPath（之後發出的授權碼才會帶這個位置）" }
    if ((Test-Path $KeyFile) -and $cfg.revokedPath) { Write-Revoked @(Read-Revoked) }
}

function Get-Aes([string]$pw, [byte[]]$salt) {
    $kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes($pw, $salt, 200000, [Security.Cryptography.HashAlgorithmName]::SHA256)
    $aes = [Security.Cryptography.Aes]::Create(); $aes.Key = $kdf.GetBytes(32); $aes.IV = $kdf.GetBytes(16); return $aes
}
function Do-Export {
    if ($BackupFile -eq "") { $script:BackupFile = Read-Host "備份檔完整路徑（例如 D:\backup\revitmcp-issuer.bak）" }
    if ($Password -eq "") { $s = Read-Host "備份密碼" -AsSecureString; $script:Password = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)) }
    $blob = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($KeyFile), $null, 'CurrentUser')
    $salt = New-Object byte[] 16; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($salt)
    $aes = Get-Aes $Password $salt
    $enc = $aes.CreateEncryptor().TransformFinalBlock($blob, 0, $blob.Length)
    [IO.File]::WriteAllBytes($BackupFile, [byte[]]($salt + $enc))
    Write-Output "已匯出加密備份：$BackupFile"
}
function Do-Import {
    if (Test-Path $KeyFile) { throw "本機已有金鑰，若要以備份覆蓋請先手動刪除 $KeyFile。" }
    if ($BackupFile -eq "") { $script:BackupFile = Read-Host "備份檔完整路徑" }
    if ($Password -eq "") { $s = Read-Host "備份密碼" -AsSecureString; $script:Password = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)) }
    $all = [IO.File]::ReadAllBytes($BackupFile)
    $salt = $all[0..15]; $enc = $all[16..($all.Length - 1)]
    $aes = Get-Aes $Password ([byte[]]$salt)
    $blob = $aes.CreateDecryptor().TransformFinalBlock([byte[]]$enc, 0, $enc.Length)
    [IO.File]::WriteAllBytes($KeyFile, [Security.Cryptography.ProtectedData]::Protect($blob, $null, 'CurrentUser'))
    Do-PubKey | Out-Null
    Write-Output "已匯入金鑰。"
}

function Menu {
    $script:Interactive = $true
    while ($true) {
        Write-Output ""
        Write-Output "==== Revit MCP 授權管理 ===="
        Write-Output " 1. 建立金鑰（第一次使用）"
        Write-Output " 2. 發出授權碼"
        Write-Output " 3. 撤銷授權"
        Write-Output " 4. 列出已發授權"
        Write-Output " 5. 設定 NAS 撤銷名單位置"
        Write-Output " 6. 匯出金鑰備份（加密）"
        Write-Output " 7. 由備份匯入金鑰"
        Write-Output " 0. 離開"
        $c = Read-Host "選擇"
        try {
            switch ($c) {
                "1" { Do-Init } "2" { $script:Request = ""; $script:Name = ""; Do-Issue } "3" { $script:LicenseId = ""; Do-Revoke }
                "4" { Do-List } "5" { $script:RevokedPath = ""; Do-Config } "6" { $script:BackupFile = ""; $script:Password = ""; Do-Export }
                "7" { $script:BackupFile = ""; $script:Password = ""; Do-Import } "0" { return }
            }
        } catch { Write-Output ("[錯誤] " + $_.Exception.Message) }
    }
}

switch ($Action) {
    "init" { Do-Init } "issue" { Do-Issue } "revoke" { Do-Revoke } "list" { Do-List } "config" { Do-Config }
    "export" { Do-Export } "import" { Do-Import } "pubkey" { Do-PubKey } default { Menu }
}
