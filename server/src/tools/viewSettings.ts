// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";

type Any = Record<string, unknown>;
const ok  = (d: unknown) => ({ content: [{ type: "text" as const, text: "```json\n" + JSON.stringify(d, null, 2) + "\n```" }], structuredContent: d as Any });
const err = (e: unknown) => ({ content: [{ type: "text" as const, text: `Error: ${e instanceof Error ? e.message : e}` }] });
const W = { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false };
const R = { readOnlyHint: true,  destructiveHint: false, idempotentHint: true,  openWorldHint: false };
const applyDesc = "false = 預檢（只回報將做什麼，不改模型）；確認後再用 true 執行";

export function registerViewSettingsTools(server: McpServer): void {

  server.registerTool("revit_list_view_templates", {
    title: "List view templates",
    description: "列出所有視圖樣板（id、名稱、視圖類型、目前套用的視圖數）。",
    inputSchema: z.object({}).strict(), annotations: R,
  }, async () => { try { return ok(await revitRequest<Any>("get", "/views/templates")); } catch (e) { return err(e); } });

  server.registerTool("revit_apply_view_template", {
    title: "Apply view template to views",
    description: `把視圖樣板套用到多個視圖。
Args:
  - viewIds: 目標視圖 id
  - templateName: 樣板名稱（完全相符優先，否則包含即可）；傳 "" 代表移除樣板
  - templateId: 或直接給樣板 id
  - apply: ${applyDesc}`,
    inputSchema: z.object({
      viewIds: z.array(z.number().int()).min(1),
      templateName: z.string().optional(), templateId: z.number().int().optional(),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/views/template", undefined, a)); } catch (e) { return err(e); } });

  server.registerTool("revit_set_category_visibility", {
    title: "Hide / show categories in views",
    description: `在視圖中隱藏或顯示品類（可見性/圖形取代）。對連結模型中同品類的元件也有效（連結顯示設定為「依主體視圖」時）。
視圖若由樣板控制 V/G，會改在樣板上，並列出受影響的視圖。
Args:
  - viewIds: 目標視圖 id
  - categories: OST_ 名稱（如 OST_SiteProperty、OST_Mass）或品類顯示名稱（如 地界線、量體）
  - hidden: true 隱藏（預設）／false 顯示
  - apply: ${applyDesc}`,
    inputSchema: z.object({
      viewIds: z.array(z.number().int()).min(1),
      categories: z.array(z.string()).min(1),
      hidden: z.boolean().default(true),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/views/categories", undefined, a)); } catch (e) { return err(e); } });

  server.registerTool("revit_delete_in_link", {
    title: "Delete elements inside a linked .rvt",
    description: `刪除「被連結的 Revit 檔」裡的元件，並把結果存回那個檔案。
執行時：在本 Revit 所有主檔卸載該連結 → 開啟連結檔 → 刪除 → 存檔 → 關閉 → 重新載入。
連結檔若是工作共用檔，或在本 Revit 開著且有未存變更，會拒絕。其他 Revit 若也連結此檔，需在那邊手動重新載入。
Args:
  - link: 連結檔名關鍵字（如 基地模）
  - categories: OST_ 名稱，如 ["OST_SiteProperty"]（地界線）
  - elementIds: 或指定連結檔內的元件 id（預檢會列出）
  - apply: ${applyDesc}`,
    inputSchema: z.object({
      link: z.string(),
      categories: z.array(z.string()).optional(),
      elementIds: z.array(z.number().int()).optional(),
      apply: z.boolean().default(false),
    }).strict(), annotations: { ...W, destructiveHint: true },
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/links/delete-elements", undefined, a, 10 * 60 * 1000)); } catch (e) { return err(e); } });

  // ── revit_update_view_properties ─────────────────────────────────────────
  server.registerTool("revit_update_view_properties", {
    title: "Update a view's scale / name",
    description: `修改視圖的比例與名稱。對任何視圖都有效（路由字面在 /drafting 下只是歷史因素）。
⚠ 這支沒有 apply／dryRun 預覽開關：每次呼叫都直接開 Transaction 寫入模型。請先確認 viewId 與新值再呼叫。
scale 與 name 兩個都省略時不會有任何變更（白跑一次交易）。
Args:
  - viewId: 視圖 ElementId
  - scale: 新比例分母（1:n）；省略＝不改
  - name: 新視圖名稱；省略或空白＝不改`,
    inputSchema: z.object({
      viewId: z.number().int().describe("視圖 ElementId"),
      scale:  z.number().int().positive().optional().describe("新比例分母（1:n）；省略＝不改"),
      name:   z.string().optional().describe("新視圖名稱；省略或空白＝不改"),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/drafting/view/update", undefined, a)); } catch (e) { return err(e); } });
}
