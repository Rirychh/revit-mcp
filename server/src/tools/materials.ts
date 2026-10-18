// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";

/** 材質與彩現外觀。rgb 一律 0–255 三元組，transparency 一律 0–100。 */

type Any = Record<string, unknown>;
const ok  = (d: unknown) => ({ content: [{ type: "text" as const, text: "```json\n" + JSON.stringify(d, null, 2) + "\n```" }], structuredContent: d as Any });
const err = (e: unknown) => ({ content: [{ type: "text" as const, text: `Error: ${e instanceof Error ? e.message : e}` }] });
const docArg = z.string().optional().describe("目標文件名關鍵字；省略＝作用中文件");
const LONG = 10 * 60 * 1000;

const rgb = z.array(z.number().int().min(0).max(255)).length(3);
const transparency = z.number().int().min(0).max(100).default(0);

export function registerMaterialTools(server: McpServer): void {

  // ── revit_set_material_appearance ────────────────────────────────────────
  server.registerTool("revit_set_material_appearance", {
    title: "Set material rendering appearance",
    description: `為材質建立／更新「一般（Generic）」彩現外觀（漫射色、透明度、光澤），Enscape 等彩現器看得到的那一層。
⚠ 沒有 apply／dryRun 預覽開關：每次呼叫都直接開 Transaction 寫入模型。材質或外觀資產不存在時會一併建立（外觀資產命名為「<name>_外觀」）。
只改彩現外觀，不改材質的圖面顏色／填充樣式。
Args:
  - document: 目標文件名關鍵字；省略＝作用中文件
  - materials: [{name, rgb?, transparency?, glossiness?}]
      rgb 省略＝200,200,200；transparency 0–100（預設 0）；glossiness 省略＝不改`,
    inputSchema: z.object({
      document: docArg,
      materials: z.array(z.object({
        name:         z.string().min(1).describe("材質名稱"),
        rgb:          rgb.optional().describe("漫射色；省略＝200,200,200"),
        transparency: transparency,
        glossiness:   z.number().optional().describe("光澤；省略＝不改"),
      }).strict()).min(1),
    }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/materials/appearance", undefined, a, LONG)); } catch (e) { return err(e); } });

  // ── revit_set_site_materials ─────────────────────────────────────────────
  server.registerTool("revit_set_site_materials", {
    title: "Assign site model materials by comment rules",
    description: `依註解關鍵字規則批次指定基地模 DirectShape／地形（Toposolid）的材質與顏色。
rules 依序套用，每個元素套用第一個命中的規則（contains 任一關鍵字出現在註解裡就算命中）。材質不存在時會建立。
Args:
  - document: 目標文件名關鍵字；省略＝作用中文件
  - applicationId: 只處理這個 applicationId 的 DirectShape（預設 RiR_site）
  - rules: [{contains: [關鍵字...], material, rgb, transparency?}]
  - topoMaterial: 地形的材質設定，欄位同 rules 項目
  - apply: false = 預檢（只回報將改什麼，不改模型）；確認後再用 true 執行`,
    inputSchema: z.object({
      document:      docArg,
      applicationId: z.string().default("RiR_site"),
      rules: z.array(z.object({
        contains:     z.array(z.string()).optional().describe("註解要含的關鍵字（任一命中即套用）"),
        material:     z.string().optional().describe("材質名稱"),
        rgb:          rgb.optional(),
        transparency: transparency,
      }).strict()).optional(),
      topoMaterial: z.object({
        contains:     z.array(z.string()).optional(),
        material:     z.string().optional(),
        rgb:          rgb.optional(),
        transparency: z.number().int().min(0).max(100).optional(),
      }).strict().optional().describe("地形（Toposolid）的材質設定"),
      apply: z.boolean().default(false),
    }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/site/materials", undefined, a, LONG)); } catch (e) { return err(e); } });
}
