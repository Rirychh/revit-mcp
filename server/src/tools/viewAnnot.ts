// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";

/**
 * 視圖設定與一般註解：視圖範圍、顯示、篩選器、樣板、隱藏隔離、剖面框、立面、詳圖索引、文字、高程點、填滿區域、詳圖元件、房間標籤、修訂雲形線。
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

export function registerViewAnnotTools(server: McpServer): void {

  server.registerTool("revit_set_view_range", {
    title: "Set view range",
    description: "平面視圖的「視圖範圍」：cutMm 剖切面、topMm 頂部、bottomMm 底部、depthMm 視圖深度，都是相對各自所屬樓層的偏移（mm）。\napply=false（預設）回報每張視圖目前的視圖範圍；只給要改的值，apply=true 寫入。被視圖樣板鎖定的視圖會列在 notApplied。",
    inputSchema: z.object({
      "viewIds": z.array(z.number().int()).min(1).max(500).describe("視圖 id（也可以是視圖樣板 id）"),
      "cutMm": z.number().describe("剖切面偏移（mm）").optional(),
      "topMm": z.number().describe("頂部偏移（mm）").optional(),
      "bottomMm": z.number().describe("底部偏移（mm）").optional(),
      "depthMm": z.number().describe("視圖深度偏移（mm）").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/views/range", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_set_view_display", {
    title: "Set view display",
    description: "設定視圖顯示：detailLevel（coarse／medium／fine）、displayStyle（wireframe／hiddenLine／shaded／shadedWithEdges／consistentColors／realistic）、\ndiscipline（architectural／structural／mechanical／electrical／plumbing／coordination）、scopeBox（範圍框名稱；\"none\" 取消）。\n比例與名稱請用 revit_update_view_properties。被視圖樣板鎖定的項目會列在 notApplied。apply=false（預設）回報目前值；apply=true 寫入。",
    inputSchema: z.object({
      "viewIds": z.array(z.number().int()).min(1).max(500).describe("視圖 id（也可以是視圖樣板 id）"),
      "detailLevel": z.enum(["coarse", "medium", "fine"]).describe("詳細等級").optional(),
      "displayStyle": z.enum(["wireframe", "hiddenLine", "shaded", "shadedWithEdges", "consistentColors", "realistic"]).describe("視覺樣式").optional(),
      "discipline": z.enum(["architectural", "structural", "mechanical", "electrical", "plumbing", "coordination"]).describe("專業").optional(),
      "scopeBox": z.string().describe("範圍框名稱；\"none\" 取消").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/views/display", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_create_view_filter", {
    title: "Create view filter",
    description: "建立「視圖篩選器」並套用：categories 品類（例如 Walls、Doors），parameter 參數名稱（須是這些品類共同可篩選的參數，找不到會列出可用的），\noperator：文字值用 equals／notEquals／contains／notContains／beginsWith；數值用 equals／notEquals／greater／greaterOrEqual／less／lessOrEqual。\n數值長度請加 valueUnit:\"mm\"（面積 \"m2\"）。viewIds 套到哪些視圖（可以是視圖樣板）；visible=false 隱藏符合的元件；colorRgb [r,g,b] 實心填色；halftone；lineWeight 1–16。\n同名篩選器已存在時直接沿用（只做套用）。套了視圖樣板的視圖請改套到樣板。apply=false（預設）只預檢。",
    inputSchema: z.object({
      "name": z.string().describe("篩選器名稱"),
      "categories": z.array(z.string()).min(1).describe("品類（例如 Walls、Doors、Rooms）"),
      "parameter": z.string().describe("參數名稱").optional(),
      "operator": z.enum(["equals", "notEquals", "contains", "notContains", "beginsWith", "greater", "greaterOrEqual", "less", "lessOrEqual"]).describe("條件").default("equals"),
      "value": z.union([z.string(), z.number(), z.number().int()]).describe("比較值").optional(),
      "valueUnit": z.enum(["none", "mm", "m2", "m3"]).describe("數值單位").default("none"),
      "viewIds": z.array(z.number().int()).describe("要套用的視圖或視圖樣板").optional(),
      "visible": z.boolean().describe("符合的元件是否顯示").default(true),
      "colorRgb": z.array(z.number().int()).min(3).max(3).describe("[r, g, b]").optional(),
      "halftone": z.boolean().describe("半色調").optional(),
      "lineWeight": z.number().int().describe("投影線寬 1–16").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/views/filters", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_create_view_template", {
    title: "Create view template",
    description: "從現有視圖的設定建立視圖樣板（等同「從目前視圖建立樣板」），再用 revit_apply_view_template 套到其他視圖。apply=false（預設）只預檢。",
    inputSchema: z.object({
      "viewId": z.number().int().describe("來源視圖"),
      "name": z.string().describe("樣板名稱"),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/views/template-from-view", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_set_visibility", {
    title: "Set visibility",
    description: "視圖中的隱藏與隔離：\n- 暫時（不影響出圖、可重設）：isolate／hide（elementIds）、isolateCategories／hideCategories（categories）、resetTemporary。\n- 永久（在此視圖隱藏圖元）：hidePermanent／unhidePermanent（elementIds）。\n整個品類永久隱藏請用 revit_set_category_visibility。apply=false（預設）只預檢。",
    inputSchema: z.object({
      "viewId": z.number().int().describe("視圖 id"),
      "action": z.enum(["isolate", "hide", "isolateCategories", "hideCategories", "resetTemporary", "hidePermanent", "unhidePermanent"]).describe("動作"),
      "elementIds": z.array(z.number().int()).describe("元件 id").optional(),
      "categories": z.array(z.string()).describe("品類").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/views/visibility", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_set_section_box", {
    title: "Set section box",
    description: "設定 3D 視圖的剖面框：elementIds 框住這些元件（外擴 marginMm），或 minMm／maxMm 指定 [x,y,z]（mm）；off:true 關閉剖面框。apply=false（預設）只預檢。",
    inputSchema: z.object({
      "viewId": z.number().int().describe("3D 視圖 id"),
      "elementIds": z.array(z.number().int()).describe("框住這些元件").optional(),
      "minMm": z.array(z.number()).min(3).max(3).describe("[x,y,z]").optional(),
      "maxMm": z.array(z.number()).min(3).max(3).describe("[x,y,z]").optional(),
      "marginMm": z.number().describe("外擴（mm）").default(500),
      "off": z.boolean().describe("關閉剖面框").default(false),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/views/section-box", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_create_elevations", {
    title: "Create elevations",
    description: "建立立面：在 planViewId 的平面上、pointMm=[x,y] 放立面標記（要放在建築外側，立面才看得到整面外牆），faces 指定要哪幾向立面（north／east／south／west，省略＝四向）。\nnorth＝北向立面：看建築北側外牆，視線朝南；其餘類推。namePrefix 給定時視圖命名為「前綴＋方向」。立面的裁剪範圍用 revit_crop_view 或在 Revit 調整。apply=false（預設）只預檢。",
    inputSchema: z.object({
      "planViewId": z.number().int().describe("放標記的平面視圖"),
      "pointMm": z.array(z.number()).min(2).max(2).describe("[x, y]（mm）"),
      "faces": z.array(z.enum(["north", "east", "south", "west"])).describe("立面方向").optional(),
      "scale": z.number().int().describe("比例分母").default(100),
      "namePrefix": z.string().describe("視圖名稱前綴").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/views/elevations", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_create_callout", {
    title: "Create callout",
    description: "建立詳圖索引（callout）：在 parentViewId 上用 rectUv 框出範圍（座標用視圖平面 uv（mm）：u＝視圖右方、v＝視圖上方；平面視圖的 uv 就是模型 X、Y，圖紙上是圖紙 mm。），可指定 name 與 scale。apply=false（預設）只預檢。",
    inputSchema: z.object({
      "parentViewId": z.number().int().describe("母視圖 id"),
      "rectUv": z.array(z.number()).min(4).max(4).describe("[u0, v0, u1, v1]（視圖平面座標 mm）"),
      "name": z.string().describe("視圖名稱").optional(),
      "scale": z.number().int().describe("比例分母").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/views/callout", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_add_text_notes", {
    title: "Add text notes",
    description: "放文字註記：viewId 可以是平面、剖面、立面、詳圖、製圖視圖或圖紙；notes 每筆 text 與 pointUv，widthMm 為圖紙上的換行寬度。座標用視圖平面 uv（mm）：u＝視圖右方、v＝視圖上方；平面視圖的 uv 就是模型 X、Y，圖紙上是圖紙 mm。apply=false（預設）只預檢並列出可用文字類型。",
    inputSchema: z.object({
      "viewId": z.number().int().describe("視圖或圖紙 id"),
      "notes": z.array(z.object({ "text": z.string().describe("文字"), "pointUv": z.array(z.number()).min(2).max(2).describe("視圖平面座標 [u, v]（mm）：u＝視圖右方、v＝視圖上方；平面視圖等於模型 X、Y；圖紙上是圖紙 mm"), "widthMm": z.number().describe("換行寬度（圖紙 mm）").optional() }).strict()).min(1).max(500).describe("文字"),
      "textType": z.string().describe("文字類型").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/annotations/text", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_add_spot_elevations", {
    title: "Add spot elevations",
    description: "放高程點：items 每筆 elementId（樓板、屋頂、天花等主體），可加 pointMm=[x,y] 指定點位（投影到頂面），省略取外框中心。hasLeader 帶引線。apply=false（預設）回報投影到的點位。",
    inputSchema: z.object({
      "viewId": z.number().int().describe("平面、剖面或立面視圖"),
      "items": z.array(z.object({ "elementId": z.number().int(), "pointMm": z.array(z.number()).min(2).max(2).describe("[x, y]（mm）").optional() }).strict()).min(1).describe("要標的元件"),
      "hasLeader": z.boolean().describe("帶引線").default(true),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/annotations/spot-elevations", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_add_filled_regions", {
    title: "Add filled regions",
    description: "畫填滿區域：regions 每個是封閉折線（座標用視圖平面 uv（mm）：u＝視圖右方、v＝視圖上方；平面視圖的 uv 就是模型 X、Y，圖紙上是圖紙 mm。），regionType 填滿區域類型名稱。apply=false（預設）只預檢並列出可用類型。",
    inputSchema: z.object({
      "viewId": z.number().int().describe("視圖 id"),
      "regions": z.array(z.array(z.array(z.number()).min(2).max(2).describe("[u, v]")).min(3).describe("封閉折線 [[u,v],...]（視圖平面座標 mm）")).min(1).describe("區域"),
      "regionType": z.string().describe("填滿區域類型").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/annotations/filled-regions", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_place_detail_components", {
    title: "Place detail components",
    description: "放詳圖元件：familyType 用「族群: 類型」（找不到會列出已載入的詳圖元件；沒載入的先用 revit_load_families），items 每筆 pointUv 與 rotationDeg。座標用視圖平面 uv（mm）：u＝視圖右方、v＝視圖上方；平面視圖的 uv 就是模型 X、Y，圖紙上是圖紙 mm。apply=false（預設）只預檢。",
    inputSchema: z.object({
      "viewId": z.number().int().describe("視圖 id"),
      "familyType": z.string().describe("詳圖元件「族群: 類型」"),
      "items": z.array(z.object({ "pointUv": z.array(z.number()).min(2).max(2).describe("視圖平面座標 [u, v]（mm）：u＝視圖右方、v＝視圖上方；平面視圖等於模型 X、Y；圖紙上是圖紙 mm"), "rotationDeg": z.number().describe("旋轉角（度）").optional() }).strict()).min(1).describe("位置"),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/annotations/detail-components", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_tag_rooms", {
    title: "Tag rooms",
    description: "放房間標籤：roomIds 指定房間，省略＝這張平面所在樓層、還沒有標籤的房間；標籤放在房間位置點。tagType 房間標籤「族群: 類型」。apply=false（預設）只預檢。",
    inputSchema: z.object({
      "viewId": z.number().int().describe("平面視圖 id"),
      "roomIds": z.array(z.number().int()).describe("房間 id").optional(),
      "tagType": z.string().describe("房間標籤「族群: 類型」").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/annotations/room-tags", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_add_revision_clouds", {
    title: "Add revision clouds",
    description: "畫修訂雲形線：rectsUv 每個 [u0,v0,u1,v1]（圖紙上是圖紙 mm，視圖上是視圖平面 mm）。revisionId 用既有修訂（revit_list_revisions），\n或 newRevision {description, date, issuedBy, issuedTo} 新建一筆。已發佈的修訂不能再加。雲形線的說明文字可接著用 revit_annotate_revision_clouds。apply=false（預設）只預檢。",
    inputSchema: z.object({
      "viewId": z.number().int().describe("圖紙或視圖 id"),
      "rectsUv": z.array(z.array(z.number()).min(4).max(4).describe("[u0, v0, u1, v1]（視圖平面座標 mm）")).min(1).describe("矩形"),
      "revisionId": z.number().int().describe("既有修訂 id").optional(),
      "newRevision": z.object({ "description": z.string(), "date": z.string().optional(), "issuedBy": z.string().optional(), "issuedTo": z.string().optional() }).strict().optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/revisions/clouds", undefined, a, LONG)); } catch (e) { return err(e); } });
}
