// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { readFileSync } from "node:fs";
import { join } from "node:path";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";

/**
 * 工具分組載入：tools/list 全開約 20 萬字元，每個對話都要付這筆成本。
 *
 *  - 環境變數 REVIT_MCP_TOOLSETS 指定啟動時開哪些組（逗號分隔），例如 "drawing,qc"。
 *    沒設或 "all"＝全部開（與舊版行為相同）。core 一定開。
 *  - 執行中用 revit_toolsets 工具開關各組；SDK 會送 tools/list_changed 通知。
 *    客戶端不支援這個通知時，要重開對話才看得到變更。
 *  - %LOCALAPPDATA%\revit-mcp\rules.json 的 disabledTools 列出的工具一律不提供
 *    （add-in 端也會擋，見 addin/Handlers/RouteGuards.cs）。
 *
 * 必須在 installTargetGuard／installCompactTools 之後安裝（這層在最外面）。
 */

export const TOOLSETS: Record<string, string> = {
  core:     "文件、多 Revit 切換、元件查詢與基本修改、視圖清單與截圖（一定開）",
  modeling: "建模與修改：牆／樓板／柱、門窗、天花、屋頂、樓梯、帷幕、族群、類型與材料",
  drawing:  "出圖：標籤、尺寸、文字、視圖設定、圖紙、修訂、製圖視圖、明細表、CAD 轉 BIM",
  qc:       "模型健檢、警告、清除、碰撞、數量、專案設定與 DWG／NWC 匯出",
  mep:      "風管、水管、MEP 系統",
  site:     "基地模、地界線、連結模型裡的元件與房間",
  export:   "IFC、明細表 CSV 匯出",
};

type Registered = { enable(): void; disable(): void; remove(): void; enabled: boolean };

const byGroup = new Map<string, Map<string, Registered>>();
let current = "core";

/** rules.json 的 disabledTools；檔案不存在或格式不對就當成沒有。 */
export function readDisabledTools(): Set<string> {
  try {
    const base = process.env.LOCALAPPDATA;
    if (!base) return new Set();
    const cfg = JSON.parse(readFileSync(join(base, "revit-mcp", "rules.json"), "utf8").replace(/^﻿/, ""));
    return new Set(Array.isArray(cfg?.disabledTools) ? cfg.disabledTools.map(String) : []);
  } catch {
    return new Set();
  }
}

/** 解析 REVIT_MCP_TOOLSETS：回傳啟動時要開的組；不認得的名稱另外回報。 */
export function parseToolsets(raw: string | undefined): { enabled: Set<string>; unknown: string[] } {
  const all = new Set(Object.keys(TOOLSETS));
  const parts = (raw ?? "").split(",").map((s) => s.trim().toLowerCase()).filter(Boolean);
  if (parts.length === 0 || parts.includes("all")) return { enabled: all, unknown: [] };
  const enabled = new Set<string>(["core"]);
  const unknown: string[] = [];
  for (const p of parts) (all.has(p) ? enabled.add(p) : unknown.push(p));
  return { enabled, unknown };
}

export function installToolsets(server: McpServer): void {
  const inner = server.registerTool.bind(server) as (...a: any[]) => any;
  const disabled = readDisabledTools();
  (server as any).registerTool = (name: string, config: any, cb: any) => {
    const tool = inner(name, config, cb) as Registered;
    if (disabled.has(name)) { tool.remove(); return tool; }
    if (!byGroup.has(current)) byGroup.set(current, new Map());
    byGroup.get(current)!.set(name, tool);
    return tool;
  };
}

/** 在 group 之下註冊一批工具。 */
export function group(name: keyof typeof TOOLSETS & string, register: () => void): void {
  const prev = current;
  current = name;
  try { register(); } finally { current = prev; }
}

function setGroup(name: string, on: boolean): void {
  for (const t of byGroup.get(name)?.values() ?? []) {
    if (on && !t.enabled) t.enable();
    if (!on && t.enabled) t.disable();
  }
}

function status(): { name: string; enabled: boolean; tools: number; description: string }[] {
  return Object.keys(TOOLSETS).map((name) => {
    const tools = [...(byGroup.get(name)?.values() ?? [])];
    return { name, enabled: tools.some((t) => t.enabled), tools: tools.length, description: TOOLSETS[name] };
  });
}

/** 依 REVIT_MCP_TOOLSETS 套用啟動時的開關，並註冊 revit_toolsets。全部工具註冊完之後呼叫。 */
export function finishToolsets(server: McpServer): void {
  const { enabled, unknown } = parseToolsets(process.env.REVIT_MCP_TOOLSETS);
  for (const name of Object.keys(TOOLSETS)) if (!enabled.has(name)) setGroup(name, false);
  if (unknown.length)
    console.error(`[revit-mcp-server] REVIT_MCP_TOOLSETS 有不認得的組：${unknown.join(", ")}（可用：${Object.keys(TOOLSETS).join(", ")}）`);

  group("core", () => {
    server.registerTool("revit_toolsets", {
      title: "List / switch Revit tool groups",
      description:
        "列出工具分組與開關狀態，或開啟／關閉某幾組工具。找不到需要的 revit_* 工具時，先用這支看它屬於哪一組並開啟。" +
        "core 不能關。變更後工具清單會更新；客戶端若沒有反應，需重開對話。",
      inputSchema: z.object({
        enable:  z.array(z.string()).optional().describe("要開啟的組名"),
        disable: z.array(z.string()).optional().describe("要關閉的組名（core 會被忽略）"),
      }).strict(),
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    }, async ({ enable, disable }: { enable?: string[]; disable?: string[] }) => {
      const bad = [...(enable ?? []), ...(disable ?? [])].filter((n) => !(n in TOOLSETS));
      for (const n of enable ?? []) if (n in TOOLSETS) setGroup(n, true);
      for (const n of disable ?? []) if (n in TOOLSETS && n !== "core") setGroup(n, false);
      const rows = status();
      const text =
        rows.map((r) => `- ${r.enabled ? "開" : "關"}　${r.name}（${r.tools} 支）：${r.description}`).join("\n") +
        (bad.length ? `\n不認得的組名：${bad.join(", ")}` : "");
      return { content: [{ type: "text" as const, text }], structuredContent: { toolsets: rows } };
    });
  });
}
