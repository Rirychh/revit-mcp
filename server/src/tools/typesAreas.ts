// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";

/**
 * 面積平面、帷幕牆格線／豎框／嵌板、牆附著、構造層、材料、群組、明細表排序篩選、族類型參數與公式。
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

export function registerTypesAreasTools(server: McpServer): void {

  server.registerTool("revit_area_plans", {
    title: "Area plans",
    description: "面積平面（法規面積計算用）：\n- action list：面積配置、各面積平面與其中的面積（m²）。\n- action createPlan：scheme（面積配置名稱，省略用第一個）、levels（樓層名稱清單）。建好的平面是空的。\n- action boundaries：viewId（面積平面）＋ loopsMm（封閉迴圈 [[[x,y],...]]，mm）或 roomIds（沿房間完成面邊界）。\n- action areas：viewId ＋ items [{ pointMm:[x,y], name?, number? }]，點要落在邊界圍起來的範圍內；回傳面積（enclosed=false 表示沒有封閉）。\napply=false（預設）只預檢；任何錯誤整批回滾。",
    inputSchema: z.object({
      "action": z.enum(["list", "createPlan", "boundaries", "areas"]).describe("動作").default("list"),
      "scheme": z.string().describe("面積配置名稱").optional(),
      "levels": z.array(z.string()).describe("樓層名稱（createPlan）").optional(),
      "viewId": z.number().int().describe("面積平面 id").optional(),
      "loopsMm": z.array(z.array(z.array(z.number()).min(2).max(2).describe("[x, y]（mm）")).describe("迴圈點")).describe("封閉迴圈（boundaries）").optional(),
      "roomIds": z.array(z.number().int()).describe("沿這些房間的邊界畫（boundaries）").optional(),
      "items": z.array(z.object({ "pointMm": z.array(z.number()).min(2).max(2).describe("[x, y]（mm）"), "name": z.string().describe("面積名稱").optional(), "number": z.string().describe("面積編號").optional() }).strict()).describe("要放的面積（areas）").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/areas", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_curtain_wall", {
    title: "Curtain wall",
    description: "帷幕牆編輯（wallId＝帷幕牆）：\n- action info：格線位置（mm）、嵌板依類型分組與 id、豎框統計、可用豎框與嵌板類型。\n- action grid：verticalMm（離牆起點的距離，垂直格線）、horizontalMm（離牆底的高度，水平格線）。\n- action mullions：mullionType，gridLineIds（省略＝全部格線）；邊框豎框跟著牆類型設定。\n- action panels：panelType，panelIds（省略＝全部嵌板），例如換成玻璃門或實心嵌板。\napply=false（預設）只預檢；任何錯誤整批回滾。",
    inputSchema: z.object({
      "action": z.enum(["info", "grid", "mullions", "panels"]).describe("動作").default("info"),
      "wallId": z.number().int().describe("帷幕牆 id"),
      "verticalMm": z.array(z.number()).describe("垂直格線：離牆起點距離 mm").optional(),
      "horizontalMm": z.array(z.number()).describe("水平格線：離牆底高度 mm").optional(),
      "mullionType": z.string().describe("豎框類型").optional(),
      "gridLineIds": z.array(z.number().int()).describe("格線 id").optional(),
      "panelType": z.string().describe("嵌板類型").optional(),
      "panelIds": z.array(z.number().int()).describe("嵌板 id").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/curtain", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_attach_walls", {
    title: "Attach walls",
    description: "牆附著（修改牆 → 附著頂部／底部）：wallIds、targetId（屋頂、樓板、天花或參考平面）、location top／base，detach=true 取消附著。需要 Revit 2025 以上。apply=false（預設）只預檢並列出不能附著的牆；任何錯誤整批回滾。",
    inputSchema: z.object({
      "wallIds": z.array(z.number().int()).min(1).describe("牆 id"),
      "targetId": z.number().int().describe("附著目標 id"),
      "location": z.enum(["top", "base"]).describe("附著牆頂或牆底").default("top"),
      "detach": z.boolean().describe("取消附著").default(false),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/walls/attach", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_set_type_layers", {
    title: "Set type layers",
    description: "構造層（編輯類型 → 結構）：typeName（族群: 類型 或類型名）、category（walls／floors／roofs／ceilings，名稱重複時用來縮小範圍）。\n不給 layers＝回傳目前構造層。layers 依序由外到內（牆）或由上到下（樓板／屋頂）：[{ function: structure／substrate／insulation／finish1／finish2／membrane, material（材料名稱）, thicknessMm, core? }]；\ncore 省略時 structure 層視為核心，核心層要連續。會改到這個類型的所有元件，只想改部分請先 revit_duplicate_type。apply=false（預設）只預檢；任何錯誤整批回滾。",
    inputSchema: z.object({
      "typeName": z.string().describe("類型"),
      "category": z.enum(["walls", "floors", "roofs", "ceilings"]).describe("品類").optional(),
      "layers": z.array(z.object({ "function": z.enum(["structure", "substrate", "insulation", "finish1", "finish2", "membrane"]).describe("層功能").default("structure"), "material": z.string().describe("材料名稱").optional(), "thicknessMm": z.number().describe("厚度 mm"), "core": z.boolean().describe("核心層").optional() }).strict()).describe("構造層（由外到內）").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/types/layers", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_materials", {
    title: "Materials",
    description: "材料：\n- action list：filter（名稱關鍵字）。\n- action create：name、basedOn（複製這個材料）、colorRgb [r,g,b]（著色顏色）、transparency 0–100、materialClass。彩現外觀用 revit_set_material_appearance。\n- action assign：material、elementIds 或 typeNames（「族群: 類型」）、parameter（有多個材料參數時指定）。牆、樓板等系統族的材料在構造層，請用 revit_set_type_layers。\napply=false（預設）只預檢；任何錯誤整批回滾。",
    inputSchema: z.object({
      "action": z.enum(["list", "create", "assign"]).describe("動作").default("list"),
      "filter": z.string().describe("名稱關鍵字").optional(),
      "name": z.string().describe("新材料名稱").optional(),
      "basedOn": z.string().describe("複製自").optional(),
      "colorRgb": z.array(z.number().int()).min(3).max(3).describe("[r,g,b]").optional(),
      "transparency": z.number().int().describe("透明度 0–100").optional(),
      "materialClass": z.string().describe("材料類別").optional(),
      "material": z.string().describe("要指定的材料").optional(),
      "elementIds": z.array(z.number().int()).describe("元件 id").optional(),
      "typeNames": z.array(z.string()).describe("類型（族群: 類型）").optional(),
      "parameter": z.string().describe("材料參數名稱").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/materials", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_groups", {
    title: "Groups",
    description: "群組：action list 列出群組類型與實例數；create 把 elementIds 建成群組（name 命名群組類型；已在群組內的元件不行）；place 把 groupType 放到 pointsMm（群組原點，mm）。解除群組用 revit_ungroup_groups。apply=false（預設）只預檢；任何錯誤整批回滾。",
    inputSchema: z.object({
      "action": z.enum(["list", "create", "place"]).describe("動作").default("list"),
      "elementIds": z.array(z.number().int()).describe("元件 id（create）").optional(),
      "name": z.string().describe("群組類型名稱（create）").optional(),
      "groupType": z.string().describe("群組類型（place）").optional(),
      "pointsMm": z.array(z.array(z.number()).describe("[x,y] 或 [x,y,z]")).describe("放置點 mm（place）").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/groups", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_schedule_settings", {
    title: "Schedule settings",
    description: "明細表設定（scheduleId）：不給其他參數＝回傳目前欄位、排序、篩選與可用的篩選運算子。\nsort [{ field, descending?, header?, footer?, blankLine? }] 取代原本的排序／群組；filters [{ field, op（Equal、NotEqual、GreaterThan、LessThan、Contains、BeginsWith、HasValue…）, value? }] 取代原本的篩選，最多 8 個，長度 mm、面積 m²；\nitemize 逐項列出、grandTotal 總計、hide／show 隱藏或顯示欄位。field 用欄位名稱或欄標題。apply=false（預設）只預檢；任何錯誤整批回滾。",
    inputSchema: z.object({
      "scheduleId": z.number().int().describe("明細表 id"),
      "sort": z.array(z.object({ "field": z.string().describe("欄位"), "descending": z.boolean().describe("遞減").optional(), "header": z.boolean().describe("群組頁首").optional(), "footer": z.boolean().describe("群組頁尾").optional(), "blankLine": z.boolean().describe("空白行").optional() }).strict()).describe("排序／群組").optional(),
      "filters": z.array(z.object({ "field": z.string().describe("欄位"), "op": z.string().describe("運算子"), "value": z.union([z.string(), z.number()]).describe("值").optional() }).strict()).describe("篩選").optional(),
      "itemize": z.boolean().describe("逐項列出每個實例").optional(),
      "grandTotal": z.boolean().describe("顯示總計").optional(),
      "hide": z.array(z.string()).describe("要隱藏的欄位").optional(),
      "show": z.array(z.string()).describe("要顯示的欄位").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/schedules/settings", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_family_type_params", {
    title: "Family type params",
    description: "族類型參數：family（族名稱）、type（省略＝全部類型）。\n不給 set／formulas＝列出各類型的參數值（長度 mm、角度度、面積 m²）。set {參數: 值} 直接改專案中的類型參數（受公式驅動的參數是唯讀）。\nformulas {參數: 公式}（空字串＝清除公式）會開啟族檔設定公式後重新載入，專案中其他參數值保留；公式語法同 Revit（例如 \"寬度 / 2\"）。apply=false（預設）只預檢；任何錯誤不會留下變更。",
    inputSchema: z.object({
      "family": z.string().describe("族名稱"),
      "type": z.string().describe("類型名稱").optional(),
      "set": z.record(z.string(), z.union([z.string(), z.number(), z.number().int(), z.boolean()])).describe("{參數: 值}").optional(),
      "formulas": z.record(z.string(), z.string()).describe("{參數: 公式}").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/families/type-params", undefined, a, LONG)); } catch (e) { return err(e); } });
}
