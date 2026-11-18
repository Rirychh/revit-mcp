// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { z } from "zod";

/**
 * 早期的建模工具（create_element、create_walls…）在 add-in 端本來沒有預檢開關。
 * 現在由 add-in 的 RouteGuards 統一補上：沒帶 apply=true 就回 { preflight:true, … }，
 * 不進 Handler、不開交易。這裡是 MCP 端對應的參數與回應處理。
 */
export const applyArg = z.boolean().default(false)
  .describe("false（預設）＝只預檢，回報請求內容與檢查結果，模型不變；true＝執行");

type ToolResult = { content: { type: "text"; text: string }[]; structuredContent?: Record<string, unknown> };

/** add-in 回的是預檢結果時，轉成工具回應；不是就回 null，讓原本的格式化繼續。 */
export function preflightResult(r: unknown): ToolResult | null {
  if (!r || typeof r !== "object" || (r as { preflight?: unknown }).preflight !== true) return null;
  const o = r as Record<string, unknown>;
  const checks = Array.isArray(o.checks) && o.checks.length
    ? "\n" + (o.checks as unknown[]).map((c) => `- ${String(c)}`).join("\n") : "";
  const text =
    `預檢：模型沒有變更。${checks}\n` +
    "```json\n" + JSON.stringify({ request: o.request, counts: o.counts }, null, 2) + "\n```\n" +
    "確認後加 apply=true 執行。";
  return { content: [{ type: "text", text }], structuredContent: o };
}
