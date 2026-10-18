// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";

/**
 * 製圖視圖（Drafting View）工具 —— 索引表、位置配置圖那類「純 2D 畫出來的圖」。
 *
 * 座標一律是「圖面 mm」（紙上的實際毫米），不是模型座標。
 * 同一組 add-in 路由底下還有 /drafting/view/update 與 /drafting/delete，
 * 它們對任何視圖都有效、不限製圖視圖，所以分別放在 viewSettings.ts 與 elements.ts。
 */

type Any = Record<string, unknown>;
const ok  = (d: unknown) => ({ content: [{ type: "text" as const, text: "```json\n" + JSON.stringify(d, null, 2) + "\n```" }], structuredContent: d as Any });
const err = (e: unknown) => ({ content: [{ type: "text" as const, text: `Error: ${e instanceof Error ? e.message : e}` }] });
const W = { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false };
const R = { readOnlyHint: true,  destructiveHint: false, idempotentHint: true,  openWorldHint: false };
const applyDesc = "false = 預檢（只回報將做什麼，不改模型）；確認後再用 true 執行";
const LONG = 10 * 60 * 1000;

const textItem = z.object({
  x:             z.number().default(0).describe("文字位置 X（圖面 mm）"),
  y:             z.number().default(0).describe("文字位置 Y（圖面 mm）"),
  text:          z.string().default("").describe("文字內容"),
  rotationDeg:   z.number().default(0),
  align:         z.enum(["left", "center", "right"]).default("left"),
  vAlign:        z.enum(["top", "middle", "bottom"]).default("top"),
  textTypeName:  z.string().optional().describe("文字類型名稱；省略用請求層級的 textTypeName"),
  widthMm:       z.number().optional().describe("文字框寬度（mm）；省略＝單行不折行"),
  leaderX:       z.number().optional().describe("引線端點 X（mm）"),
  leaderY:       z.number().optional().describe("引線端點 Y（mm）"),
  leaderElbowX:  z.number().optional().describe("引線轉折點 X（mm）"),
  leaderElbowY:  z.number().optional().describe("引線轉折點 Y（mm）"),
  leaderSide:    z.enum(["auto", "left", "right"]).default("auto"),
  leaderArc:     z.boolean().default(false),
}).strict();

export function registerDraftingTools(server: McpServer): void {

  // ── revit_list_drafting_types ────────────────────────────────────────────
  server.registerTool("revit_list_drafting_types", {
    title: "List drafting view / text / line types",
    description: `列出可用的製圖視圖族類型、文字類型、線型式。
建立製圖視圖或畫內容前先查一次，拿到正確的名稱再餵給 revit_create_drafting_view / revit_add_drafting_content。`,
    inputSchema: z.object({}).strict(), annotations: R,
  }, async () => { try { return ok(await revitRequest<Any>("get", "/drafting/types")); } catch (e) { return err(e); } });

  // ── revit_create_drafting_view ───────────────────────────────────────────
  server.registerTool("revit_create_drafting_view", {
    title: "Create a drafting view",
    description: `建立製圖視圖（索引表、位置配置圖用）。同名視圖已存在時不會重建，直接回傳既有的那一個。
Args:
  - name: 視圖名稱
  - scale: 比例分母（1:n，預設 100）
  - viewFamilyTypeName: 製圖視圖族類型名稱（用 revit_list_drafting_types 查）；省略取第一個
  - apply: ${applyDesc}`,
    inputSchema: z.object({
      name:               z.string().min(1).describe("視圖名稱"),
      scale:              z.number().int().positive().default(100),
      viewFamilyTypeName: z.string().optional(),
      apply:              z.boolean().default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/drafting/view", undefined, a)); } catch (e) { return err(e); } });

  // ── revit_add_drafting_content ───────────────────────────────────────────
  server.registerTool("revit_add_drafting_content", {
    title: "Draw boxes / lines / texts in a drafting view",
    description: `在製圖視圖裡畫矩形框線、線段與文字（可帶引線）。座標單位是「圖面 mm」，不是模型座標。
這支只會「加」東西，不會清掉既有內容；要整張重畫請先用 revit_clear_drafting_view。
Args:
  - viewId: 製圖視圖 ElementId（用 revit_list_views 或 revit_create_drafting_view 取得）
  - boxes: [{x, y, w, h}] 左下角座標與寬高（mm）
  - lines: [{x1, y1, x2, y2}]（mm）
  - texts: [{x, y, text, rotationDeg?, align?, vAlign?, textTypeName?, widthMm?, leaderX?, leaderY?, leaderElbowX?, leaderElbowY?, leaderSide?, leaderArc?}]
  - textTypeName: 請求層級的預設文字類型（各筆 texts 可個別覆寫）
  - lineStyleName: 線型式名稱
  - apply: ${applyDesc}`,
    inputSchema: z.object({
      viewId: z.number().int().describe("製圖視圖 ElementId"),
      boxes: z.array(z.object({
        x: z.number().describe("左下 X（圖面 mm）"),
        y: z.number().describe("左下 Y（圖面 mm）"),
        w: z.number().describe("寬（mm）"),
        h: z.number().describe("高（mm）"),
      }).strict()).optional(),
      lines: z.array(z.object({
        x1: z.number(), y1: z.number(), x2: z.number(), y2: z.number(),
      }).strict()).optional(),
      texts:         z.array(textItem).optional(),
      textTypeName:  z.string().optional(),
      lineStyleName: z.string().optional(),
      apply:         z.boolean().default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/drafting/content", undefined, a, LONG)); } catch (e) { return err(e); } });

  // ── revit_clear_drafting_view ────────────────────────────────────────────
  server.registerTool("revit_clear_drafting_view", {
    title: "Clear all detail lines and texts in a view",
    description: `清掉視圖裡所有細部線（CurveElement）與文字（TextNote），方便整張重畫。
⚠ apply=true 會直接刪除該視圖裡「全部」的線與文字，不能只保留一部分，Ctrl+Z 以外無法還原。
先用 apply=false 預檢看會刪幾個；只想刪特定幾個請改用 revit_delete_elements。
Args:
  - viewId: 視圖 ElementId
  - apply: false = 預檢（只回報將刪除幾個，不改模型）；確認後再用 true 執行`,
    inputSchema: z.object({
      viewId: z.number().int().describe("視圖 ElementId"),
      apply:  z.boolean().default(false),
    }).strict(),
    annotations: { ...W, destructiveHint: true },
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/drafting/clear", undefined, a)); } catch (e) { return err(e); } });
}
