# 多個 Revit 同時連 Claude

## 原本的限制

add-in 把 HTTP server 寫死在 `localhost:48884`。第二個 Revit 啟動時綁不上同一個埠，
於是完全連不到 —— 而且不會有明顯錯誤，只會覺得「Claude 改到別的檔案」。

## 現在的作法

**Add-in 端**（`RevitHttpServer.cs`）
- 從 `48884` 往上找第一個可用的埠並綁定，範圍 `48884–48893`（共 10 個實例）。
- 新增 `GET /info`，回報 `port / pid / revitVersion / activeDocument / documents`。
  這個路由刻意放在「檢查 active document」之前，所以還沒開任何模型的 Revit 也認得出來。

**MCP server 端**（`revit-mcp`）
- 每次請求才解析 baseURL，所以切換實例立刻生效。
- 掃描整個埠範圍打 `/info` 來探索，不依賴任何暫存檔，Revit 當掉也不會殘留錯誤狀態。
- 只有一個實例 → 自動選它（行為和以前一樣）。
  多個實例 → 回錯誤並列出選項，要求先指定。
- 新工具：
  - `revit_list_instances` — 列出所有實例，標示目前作用中的那一個
  - `revit_use_instance` — 用 `port` 或 `match`（檔名／路徑／版本／pid 的片段）切換
- 連線被拒（Revit 關掉）會自動清掉選擇，下次呼叫重新探索。
- 設 `REVIT_PORT` 環境變數可強制釘死某個埠，此時停用對話中切換。

## 版本

同一份程式碼可編譯給 Revit 2024 與 2022（皆為 .NET Framework 4.8）。
差別只有 `RevitAPI.dll` 路徑，以及 Newtonsoft.Json：
Revit 2024 自帶 13.0.1（用 NuGet 13.0.3），Revit 2022 只帶 11.0.2，
所以 2022 直接參考 Revit 目錄下那顆，避免版本衝突。

## 建置與部署

```powershell
# 兩版一起（需先完全關閉 Revit）
powershell -ExecutionPolicy Bypass -File deploy-addin.ps1

# 只做某一版
powershell -ExecutionPolicy Bypass -File deploy-addin.ps1 -Versions 2024

# 只建置不部署
dotnet build -c Release -p:RevitVersion=2022
```

輸出在 `bin\Revit<版本>\Release\net48\RevitMcpAddin.dll`。

MCP server：

```powershell
cd <repo>\server
npm run build      # 或 node node_modules\typescript\bin\tsc
```

改完 MCP server 要重啟 Claude Desktop 才會載入新版。

## 用法

1. 開兩個以上 Revit，各自開自己的專案。
2. 在 Claude 說「列出目前開著的 Revit」→ `revit_list_instances`。
3. 「切換到 SASSO 那個」→ `revit_use_instance`，之後所有 `revit_*` 工具都作用在那一個。
