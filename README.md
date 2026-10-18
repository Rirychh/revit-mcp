# Revit MCP

© 2026 Rita (Rirychh)

讓 Claude 透過 [Model Context Protocol](https://modelcontextprotocol.io) 讀取與操作 Autodesk Revit 模型。支援 Revit 2022–2027。

## 架構

```
Claude Desktop ──stdio──▶ server/ (Node.js MCP server) ──HTTP 127.0.0.1 + token──▶ addin/ (Revit 增益集)
```

| 資料夾 | 內容 |
|---|---|
| `addin/` | C# Revit 增益集：在 Revit 內啟動本機 HTTP 服務、執行 Revit API；另含 Claude 對話面板 |
| `addin/installer/` | HTA 安裝精靈與安裝腳本 |
| `addin/license-tool/` | 授權碼發行工具（ECDSA P-256 簽章，綁 Windows 帳號／電腦／到期日） |
| `server/` | TypeScript MCP server，提供約 85 個 Revit 工具（查詢、建模、圖紙、匯出、模型 QC、Purge 等） |

## 支援的 AI

**Revit 內的對話面板**：上方「AI」選單切換 Claude／GPT／Gemini，各家 API 金鑰分開存在 Windows 認證管理員。「模型」欄可改模型 ID（預設 `claude-sonnet-5`、`gpt-5`、`gemini-2.5-pro`）。三家共用同一份 `tools-manifest.json`；Gemini 不支援的 JSON Schema 關鍵字會自動轉換。

**外部 MCP 客戶端**（透過 `server/`）：

| 客戶端 | 設定檔 |
|---|---|
| Claude Desktop | 安裝精靈自動寫入 `claude_desktop_config.json` |
| Gemini CLI | `%USERPROFILE%\.gemini\settings.json` 的 `mcpServers` |
| Codex CLI（GPT） | `%USERPROFILE%\.codex\config.toml` 的 `[mcp_servers.revit]` |

Gemini CLI：

```json
{
  "mcpServers": {
    "revit": {
      "command": "C:\\Users\\<you>\\AppData\\Local\\Rirychh\\RevitMCP\\node\\node.exe",
      "args": ["C:\\Users\\<you>\\AppData\\Local\\Rirychh\\RevitMCP\\server\\dist\\index.js"],
      "timeout": 600000
    }
  }
}
```

Codex CLI：

```toml
[mcp_servers.revit]
command = 'C:\Users\<you>\AppData\Local\Rirychh\RevitMCP\node\node.exe'
args = ['C:\Users\<you>\AppData\Local\Rirychh\RevitMCP\server\dist\index.js']
tool_timeout_sec = 600
```

ChatGPT 網頁版／桌面版只能連遠端 MCP server，不能連這個本機 stdio server。

## 編譯

增益集（需 .NET 8 SDK；Revit 2027 需 .NET 10 SDK）：

```powershell
cd addin
.\build-all.ps1                 # 全部版本
dotnet build -c Release -p:RevitVersion=2024   # 單一版本
```

MCP server（需 Node.js 18+）：

```powershell
cd server
npm install
npm run build
```

## 授權機制

發佈版不含發行者公鑰。要使用授權機制：

1. 執行 `addin/license-tool/授權管理.bat`，選 init 建立金鑰（私鑰以 DPAPI 存在 `%APPDATA%\Rirychh\RevitMCP-Issuer`，不會進 repo）。
2. 把產生的 `public-key.txt` 內 X／Y 值填入 `addin/Licensing/LicenseManager.cs` 的 `PubX`、`PubY`，重新編譯。
3. 使用者安裝後取得申請碼，以同一工具發出授權碼。

## 安全

- HTTP 服務只綁 `127.0.0.1`，每次啟動產生 shared-secret token，MCP server 帶 token 才能呼叫。
- Anthropic API 金鑰（對話面板用）以 DPAPI 加密存在使用者本機。

## 授權條款

[MIT](LICENSE) © 2026 Rita (Rirychh)