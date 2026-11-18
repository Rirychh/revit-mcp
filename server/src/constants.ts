// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

// 每個 Revit 實例的 add-in 會從 PORT_RANGE_START 往上綁第一個空的埠，
// 所以掃描這個範圍就能找出目前開著的所有 Revit。
export const PORT_RANGE_START = Number(process.env.REVIT_PORT_RANGE_START ?? 48884);
export const PORT_RANGE_COUNT = Number(process.env.REVIT_PORT_RANGE_COUNT ?? 10);

// 設了 REVIT_PORT 就強制釘死在該埠，跳過探索
//（給「一個 MCP server 綁一個 Revit」的用法）。
export const PINNED_PORT = process.env.REVIT_PORT ? Number(process.env.REVIT_PORT) : null;

export const PROBE_TIMEOUT_MS   = 1_500;
// 讀取類請求的等待時間；會改模型的請求（POST/PUT）給長一點，大模型的交易常超過 30 秒。
// 兩者都可用環境變數調整。add-in 端的等待時間會比這裡少 ADDIN_TIMEOUT_MARGIN_MS，
// 讓 add-in 先逾時並明確回報「沒有執行」或「仍在執行」。
export const REQUEST_TIMEOUT_MS = Number(process.env.REVIT_REQUEST_TIMEOUT_MS ?? 30_000);
export const WRITE_TIMEOUT_MS   = Number(process.env.REVIT_WRITE_TIMEOUT_MS ?? 120_000);
export const ADDIN_TIMEOUT_MARGIN_MS = 3_000;
export const CHARACTER_LIMIT    = 25_000;

export enum ResponseFormat {
  MARKDOWN = "markdown",
  JSON     = "json",
}
