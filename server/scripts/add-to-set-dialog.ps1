# Revit MCP — © 2026 Rita (Rirychh)
# https://github.com/Rirychh/revit-mcp
# SPDX-License-Identifier: MIT

param([int]$RevitPid, [string]$Option, [int]$TimeoutSec = 20)
# 處理 Revit「新增至設計選項集」對話框：只勾選名稱符合 $Option 的選項，其餘取消，按確定。
# 輸出單行 JSON。找不到對話框或勾選項時不按確定，留給使用者。
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [Windows.Automation.AutomationElement]; $S = [Windows.Automation.TreeScope]
$out = @{ handled = $false; checked = @(); unchecked = @(); items = @(); note = "" }
$deadline = (Get-Date).AddSeconds($TimeoutSec); $dlg = $null
$pc = New-Object Windows.Automation.PropertyCondition($A::ProcessIdProperty, $RevitPid)
$wc = New-Object Windows.Automation.PropertyCondition($A::ControlTypeProperty, [Windows.Automation.ControlType]::Window)
while (-not $dlg -and (Get-Date) -lt $deadline) {
  foreach ($top in $A::RootElement.FindAll($S::Children, $pc)) {
    foreach ($w in @($top) + @($top.FindAll($S::Descendants, $wc))) {
      $n = $w.Current.Name
      if ($n -match '設計選項|Design Option' -and $n -notmatch '^Autodesk Revit') { $dlg = $w; break }
    }
    if ($dlg) { break }
  }
  if (-not $dlg) { Start-Sleep -Milliseconds 500 }
}
if (-not $dlg) { $out.note = "等不到對話框"; $out | ConvertTo-Json -Compress; exit }
$out.dialog = $dlg.Current.Name
$toggles = @()
foreach ($e in $dlg.FindAll($S::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
  $p = $null
  if ($e.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $toggles += ,@($e, $p) }
}
foreach ($t in $toggles) {
  $name = $t[0].Current.Name.Trim(); $out.items += $name
  $want = ($name -eq $Option) -or $name.EndsWith(": $Option") -or $name.EndsWith(":$Option")
  $state = $t[1].Current.ToggleState
  if ($want -and $state -ne 'On') { $t[1].Toggle(); $out.checked += $name }
  elseif (-not $want -and $state -eq 'On') { $t[1].Toggle(); $out.unchecked += $name }
  elseif ($want) { $out.checked += $name }
}
if ($out.checked.Count -ne 1) { $out.note = "符合「$Option」的勾選項有 $($out.checked.Count) 個，未按確定，請手動確認"; $out | ConvertTo-Json -Compress; exit }
$bc = New-Object Windows.Automation.PropertyCondition($A::ControlTypeProperty, [Windows.Automation.ControlType]::Button)
$ok = $dlg.FindAll($S::Descendants, $bc) | Where-Object { $_.Current.Name -match '^(確定|OK)$' } | Select-Object -First 1
if (-not $ok) { $out.note = "找不到確定按鈕"; $out | ConvertTo-Json -Compress; exit }
$ok.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
$out.handled = $true; $out | ConvertTo-Json -Compress
