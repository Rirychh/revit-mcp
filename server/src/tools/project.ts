// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";

/**
 * 專案設定與輸出：專案資訊、單位、專案參數、階段、工作集、DWG／NWC 匯出、全面清除。
 * 功能構想參考 MLabs Revit MCP 公開的工具清單（mlabsolution.com/revit-mcp），程式獨立實作。
 * 本檔由 revit-mcp-addin-src 的工具規格產生，與 tools-manifest.json 的 input_schema 一致；改參數請兩邊一起改。
 * 寫入工具一律先 apply=false 預檢。座標：視圖平面 uv（mm），或模型座標 mm。
 */

type Any = Record<string, unknown>;
const ok  = (d: unknown) => ({ content: [{ type: "text" as const, text: "```json\n" + JSON.stringify(d, null, 2) + "\n```" }], structuredContent: d as Any });
const err = (e: unknown) => ({ content: [{ type: "text" as const, text: `Error: ${e instanceof Error ? e.message : e}` }] });
const R = { readOnlyHint: true,  destructiveHint: false, idempotentHint: true,  openWorldHint: false };
const W = { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false };
const D = { readOnlyHint: false, destructiveHint: true,  idempotentHint: false, openWorldHint: false };
const LONG = 5 * 60 * 1000;

/** GET：陣列參數轉成逗號分隔字串放進 query。 */
const q = (a: Any, join: string[] = []) => Object.fromEntries(Object.entries(a).filter(([, v]) => v !== undefined).map(([k, v]) => [k, join.includes(k) && Array.isArray(v) ? v.join(",") : v]));

export function registerProjectTools(server: McpServer): void {

  server.registerTool("revit_project_info", {
    title: "Project info",
    description: "專案資訊（管理 → 專案資訊）：不給 set 時回傳所有欄位與目前值；set {參數名稱: 值} 寫入，參數名稱照回傳的名稱。apply=false（預設）只預檢；任一欄位無法設定就整批不執行。",
    inputSchema: z.object({
      "set": z.record(z.string(), z.union([z.string(), z.number(), z.number().int(), z.boolean()])).describe("{參數名稱: 值}").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/project/info", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_project_units", {
    title: "Project units",
    description: "專案單位（管理 → 專案單位）：不給任何值時回傳目前設定；length（mm／cm／m）、lengthDecimals、area（m2／cm2／mm2）、areaDecimals、volume（m3／cm3）、volumeDecimals、angleDecimals。apply=false（預設）只預檢。",
    inputSchema: z.object({
      "length": z.enum(["mm", "cm", "m"]).describe("長度單位").optional(),
      "lengthDecimals": z.number().int().describe("長度小數位數").optional(),
      "area": z.enum(["m2", "cm2", "mm2"]).describe("面積單位").optional(),
      "areaDecimals": z.number().int().describe("面積小數位數").optional(),
      "volume": z.enum(["m3", "cm3"]).describe("體積單位").optional(),
      "volumeDecimals": z.number().int().describe("體積小數位數").optional(),
      "angleDecimals": z.number().int().describe("角度小數位數").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/project/units", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_add_project_parameter", {
    title: "Add project parameter",
    description: "新增專案參數：name、dataType（text／number／integer／length／area／yesNo）、categories 品類、binding（instance 實例／type 類型），參數放在「資料」群組。\n做法是用暫存的共用參數檔建立定義再綁定到專案（所以實際上是共用參數，可用於明細表與標籤），不會改動你原本的共用參數檔設定。apply=false（預設）只預檢。",
    inputSchema: z.object({
      "name": z.string().describe("參數名稱"),
      "dataType": z.enum(["text", "number", "integer", "length", "area", "yesNo"]).describe("資料型別").default("text"),
      "categories": z.array(z.string()).min(1).describe("品類（例如 Walls、Doors、Rooms）"),
      "binding": z.enum(["instance", "type"]).describe("實例或類型參數").default("instance"),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/project/parameters", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_set_phases", {
    title: "Set phases",
    description: "階段：不給 elementIds、viewIds 時列出所有階段與階段篩選。elementIds 搭配 created（建立階段）、demolished（拆除階段，\"none\" 清除）；viewIds 搭配 viewPhase、phaseFilter。apply=false（預設）只預檢。",
    inputSchema: z.object({
      "elementIds": z.array(z.number().int()).describe("元件 id").optional(),
      "created": z.string().describe("建立階段名稱").optional(),
      "demolished": z.string().describe("拆除階段名稱；\"none\" 清除").optional(),
      "viewIds": z.array(z.number().int()).describe("視圖 id").optional(),
      "viewPhase": z.string().describe("視圖階段").optional(),
      "phaseFilter": z.string().describe("階段篩選").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/project/phases", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_worksets", {
    title: "Worksets",
    description: "工作集（工作共用模型）：action list 列出使用者工作集與目前作用中工作集；create 新建（name）；move 把 elementIds 移到 worksetName（被其他人借用的會略過）。完成後記得與中央同步（revit_sync_with_central）。apply=false（預設）只預檢。",
    inputSchema: z.object({
      "action": z.enum(["list", "create", "move"]).describe("動作").default("list"),
      "name": z.string().describe("create：工作集名稱").optional(),
      "elementIds": z.array(z.number().int()).describe("move：元件 id").optional(),
      "worksetName": z.string().describe("move：目標工作集").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/project/worksets", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_export_dwg", {
    title: "Export dwg",
    description: "匯出 DWG：viewIds 或 sheetNumbers，folder 匯出資料夾，setupName 用專案裡既有的匯出設定（預檢會列出），mergedViews 把圖紙上的視圖合併成單一檔。只寫檔、不改模型。apply=false（預設）只預檢。",
    inputSchema: z.object({
      "viewIds": z.array(z.number().int()).describe("視圖 id").optional(),
      "sheetNumbers": z.array(z.string()).describe("圖號").optional(),
      "folder": z.string().describe("匯出資料夾"),
      "setupName": z.string().describe("匯出設定名稱").optional(),
      "mergedViews": z.boolean().describe("合併視圖").default(false),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/export/dwg", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_export_nwc", {
    title: "Export nwc",
    description: "匯出 Navisworks NWC：folder、fileName（省略用模型名稱），viewId 指定 3D 視圖（省略匯出整個模型）。需要已安裝 Navisworks Exporters for Revit，沒有會直接說明。只寫檔、不改模型。apply=false（預設）只預檢。",
    inputSchema: z.object({
      "folder": z.string().describe("匯出資料夾"),
      "fileName": z.string().describe("檔名（不含副檔名）").optional(),
      "viewId": z.number().int().describe("3D 視圖 id").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/export/nwc", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_purge_all", {
    title: "Purge all",
    description: "全面清除未使用項目（管理 → 清除未使用的項目）：dryRun=true（預設）回報第一輪會清掉多少、依品類統計；dryRun=false 重複清除 passes 輪（刪掉族群後材質等才會變成未使用）。\n會刪除元件，執行前請先跟使用者確認。需要 Revit 2024 以上；2022／2023 請用 revit_purge_scan／revit_purge_delete。",
    inputSchema: z.object({
      "dryRun": z.boolean().describe("只回報不刪除").default(true),
      "passes": z.number().int().describe("清除幾輪（1–5）").default(3),
    }).strict(), annotations: D,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/purge/all", undefined, a, LONG)); } catch (e) { return err(e); } });
}
