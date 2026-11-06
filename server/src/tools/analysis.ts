// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";

/** 唯讀分析工具。幾何一律 mm、專案內部座標。 */

type Any = Record<string, unknown>;
const ok  = (d: unknown) => ({ content: [{ type: "text" as const, text: "```json\n" + JSON.stringify(d, null, 2) + "\n```" }], structuredContent: d as Any });
const err = (e: unknown) => ({ content: [{ type: "text" as const, text: `Error: ${e instanceof Error ? e.message : e}` }] });
const R = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false };
const LONG = 10 * 60 * 1000;

export function registerAnalysisTools(server: McpServer): void {

  server.registerTool("revit_check_clashes", {
    title: "Check clashes",
    description: `碰撞檢查（唯讀）：用 Revit 實體幾何干涉找出兩組品類之間的硬碰撞，回報兩側元件（id、品類、族、類型、樓層）與碰撞位置（mm）。
  - 只給 setA：setA 自己互查
  - setA、setB 都給：setA 對 setB（例如結構梁 vs 風管、水管）
  - 都不給：牆、樓板、屋頂、天花、結構柱、結構梁、風管、水管、電纜架、導管互查
品類可用 OST_ 名稱、英文別名（walls、floors、beams、ducts、pipes、cabletrays、conduits…）或模型裡的品類顯示名。
已自動接合的混凝土、沒有實體幾何的元件不會被回報；連結模型不在範圍內。大模型可能跑很久。
找到的元件可接 revit_select_elements 在 Revit 中檢視。
Args:
  - setA / setB: 品類清單
  - levels: 只看這些樓層的元件（樓層名稱）
  - maxClashes: 最多回報幾組（預設 200）
  - csvPath: 給完整路徑則寫出 CSV（UTF-8 BOM）`,
    inputSchema: z.object({
      setA: z.array(z.string()).optional(),
      setB: z.array(z.string()).optional(),
      levels: z.array(z.string()).optional(),
      maxClashes: z.number().int().min(1).max(5000).default(200),
      csvPath: z.string().optional(),
    }).strict(), annotations: R,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/analysis/clashes", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_material_quantities", {
    title: "Material quantities",
    description: `材料數量（唯讀）：依材料（或品類＋材料）彙總元件的材料體積（m³）與面積（m²）。
數值是 Revit 元件的材料體積／面積，未扣搭接、損耗與施工放樣，不等於估驗數量。
Args:
  - categories: 只算這些品類（省略 = 所有模型品類，大模型較慢）
  - groupBy: "material"（預設）｜"category-material"
  - csvPath: 給完整路徑則寫出 CSV（UTF-8 BOM）`,
    inputSchema: z.object({
      categories: z.array(z.string()).optional(),
      groupBy: z.enum(["material", "category-material"]).default("material"),
      csvPath: z.string().optional(),
    }).strict(), annotations: R,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/analysis/material-quantities", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_export_room_data", {
    title: "Export room data",
    description: `房間資料（唯讀）：每個房間的編號、名稱、樓層、面積（m²）、周長（m）、體積（m³）、無邊界高度、是否放置／封閉，可選邊界座標（mm，取完成面）。
面積是 Revit 房間面積（依專案的房間面積計算設定），不是法規面積。只要房間清單時用 revit_list_rooms 比較快。
Args:
  - level: 只看這個樓層
  - includeBoundary: 是否附邊界座標（預設 false）
  - csvPath: 給完整路徑則寫出 CSV（UTF-8 BOM）`,
    inputSchema: z.object({
      level: z.string().optional(),
      includeBoundary: z.boolean().default(false),
      csvPath: z.string().optional(),
    }).strict(), annotations: R,
  }, async (a) => { try { return ok(await revitRequest<Any>("get", "/analysis/rooms", a, undefined, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_model_statistics", {
    title: "Model statistics",
    description: `模型統計（唯讀，比 revit_model_health 快）：實例總數、各模型品類實例數、族群數、類型數、圖紙、樓層、房間、警告數、各類型視圖數。
要看警告分類、未使用項目、CAD 與連結狀態用 revit_model_health；找拖慢模型的主因用 revit_performance_report。`,
    inputSchema: z.object({}).strict(), annotations: R,
  }, async () => { try { return ok(await revitRequest<Any>("get", "/analysis/statistics", undefined, undefined, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_get_selection", {
    title: "Get current selection",
    description: `讀取使用者目前在 Revit 中選取的元件（唯讀）：id、品類、族、類型、名稱、樓層，以及作用中視圖名稱。
要「設定」選取用 revit_select_elements。`,
    inputSchema: z.object({}).strict(), annotations: R,
  }, async () => { try { return ok(await revitRequest<Any>("get", "/selection")); } catch (e) { return err(e); } });
}
