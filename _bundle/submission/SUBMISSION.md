# Revit MCP — Autodesk App Store 提交資料

提交入口：Autodesk App Store Publisher Center（需 Rita 用自己的 Autodesk 帳號登入並簽 Publisher Agreement）
上傳物：`RevitMCP.bundle` 整個資料夾壓成 zip。Autodesk 會依 PackageContents.xml 自行做 .msi，再寄回給你測試。

## 表單欄位草稿

| 欄位 | 內容 |
|---|---|
| App name | Revit MCP |
| Price | Free |
| Publisher | Rita (Rirychh) |
| Supported versions | Revit 2027（必填）；2022–2026 |
| OS | Windows 64-bit |
| Category | Productivity / AI |
| Support | https://github.com/Rirychh/revit-mcp/issues ＋ 一個對外 email（待填） |
| Privacy policy URL | 把 PRIVACY.md 放到 GitHub 後的網址 |
| Help | 自動帶入 Contents/Resources/help.html |

### Short description (EN, ≤ 255 chars)
Connect AI to Revit. Chat with Claude, ChatGPT or Gemini inside Revit using your own API key, or control Revit from Claude Desktop via MCP. 160+ tools for querying, modeling, annotation, sheets and export, with preview-before-apply.

### Description (EN)
Revit MCP lets AI assistants work directly in your open Revit model.

- **AI Chat panel** – a dockable pane inside Revit. Bring your own API key for Anthropic Claude, OpenAI or Google Gemini.
- **Claude Desktop integration** – one click on *Connect Claude Desktop* registers the bundled MCP server; Claude can then read and edit your model.
- **160+ tools** – levels, grids, walls, floors, doors and windows, rooms, views, sheets, tags, dimensions, schedules, families, worksets, design options, DWG/IFC/NWC/PDF export, model health and warnings.
- **Safe by default** – every tool that changes the model previews first and applies only after you confirm; each call is one transaction and rolls back on any error.
- **Local only** – the add-in listens on 127.0.0.1 and sends no telemetry.

Requirements: for AI Chat, an API key from Anthropic, OpenAI or Google; for MCP, Claude Desktop.

### 中文說明
Revit MCP 讓 AI 直接操作開著的 Revit 模型：Revit 內建 AI 對話面板（自備 Claude／OpenAI／Gemini API key），或一鍵連接 Claude Desktop 經 MCP 控制 Revit。160 多個工具涵蓋查詢、建模、註解、圖紙與匯出；會改模型的工具一律先預覽、確認後才執行。

## 截圖清單（需在 Revit 實拍，1–5 張，建議 1280×800 以上）
1. 功能區「Revit MCP」頁籤三顆按鈕
2. AI 對話面板，示範一句指令與回應
3. Claude Desktop 對話中操作 Revit（預覽 → 確認 → 結果）
4. 批次建立圖紙或標註的結果
5. 連接 Claude Desktop 成功對話框

## 送審前待辦（Rita）
- [ ] 註冊 Autodesk Developer Symbol（Registered Developer Symbol, RDS），把 4 碼代號換掉 .addin 的 `VendorId`（目前暫填 `RIRY`）
- [ ] 在 PackageContents.xml 的 CompanyDetails 填對外 email
- [ ] PRIVACY.md 放上 GitHub，網址填入表單
- [ ] 在乾淨的電腦（或先停用九典內部版 add-in）裝 bundle 實測 Revit 2022–2027 各版啟動、三顆按鈕、F1
- [ ] 確認九典對個人上架工具的智財規定
- [ ] 截圖 1–5
- [ ] （建議）revit-mcp-server.exe 做程式碼簽章，未簽章的 exe 可能被 SmartScreen／防毒攔下

## 手動測試方式
把 `RevitMCP.bundle` 複製到 `%APPDATA%\Autodesk\ApplicationPlugins\`，重開 Revit。移除時刪掉該資料夾。