// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";

/**
 * 標籤與尺寸排版、標號截圖、DWG 轉 BIM、明細表與批次參數、MEP 接頭。
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

export function registerLayoutTools(server: McpServer): void {

  server.registerTool("revit_inspect_tags", {
    title: "Inspect tags",
    description: "檢查視圖裡的標籤（唯讀）。回傳每個標籤的標籤頭位置 headUv、被標元件中心 anchorUv、引線轉折 elbowUv、外框 boxUv、引線長度，\n以及 overlaps（外框互相重疊的標籤對；外框含引線範圍，引線交錯也會算重疊）、headOnHost（標籤頭壓在被標元件上）、outsideCrop（跑出裁剪框）。\n位置一律是視圖平面座標 uv（mm）：u＝視圖右方、v＝視圖上方；調整或排列標籤時傳同一套座標。",
    inputSchema: z.object({
      "viewId": z.number().int().describe("視圖 ElementId"),
      "limit": z.number().int().describe("最多回傳幾個標籤").default(300),
    }).strict(), annotations: R,
  }, async (a) => { try { return ok(await revitRequest<Any>("get", "/annotations/tags/inspect", q(a as Any, []), undefined, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_adjust_tags", {
    title: "Adjust tags",
    description: "逐一調整標籤：移動標籤頭（headUv 絕對位置，或 deltaMm 位移，距離不受限制）、alignToAnchor 讓標籤頭與被標元件同高（水平引線）或同一直線（垂直引線）、\nelbow 設定引線轉折：orthogonal＝從元件先垂直再水平到標籤頭的直角引線；straight＝把轉折放在中點，接近一直線；keep＝不動。\n座標用 revit_inspect_tags 的 uv（mm）。apply=false（預設）只回傳每個標籤的移動前後位置；確認後 apply=true，單一交易，任何錯誤整批回滾。",
    inputSchema: z.object({
      "viewId": z.number().int().describe("視圖 ElementId"),
      "moves": z.array(z.object({ "tagId": z.number().int().describe("標籤 ElementId"), "headUv": z.array(z.number()).min(2).max(2).describe("視圖平面座標 [u, v]（mm）：u＝視圖右方、v＝視圖上方，與 revit_inspect_tags 回傳的座標同一套").optional(), "deltaMm": z.array(z.number()).min(2).max(2).describe("位移 [du, dv]（mm）").optional(), "alignToAnchor": z.enum(["horizontal", "vertical"]).describe("horizontal＝標籤頭與被標元件中心同高；vertical＝同一條垂直線").optional(), "hasLeader": z.boolean().describe("是否帶引線").optional(), "elbow": z.enum(["keep", "orthogonal", "straight"]).describe("引線轉折").default("keep"), "orientation": z.enum(["horizontal", "vertical"]).describe("標籤方向").optional() }).strict()).min(1).max(1000).describe("要調整的標籤"),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/annotations/tags/adjust", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_arrange_tags", {
    title: "Arrange tags",
    description: "把一批標籤的標籤頭排到邊界外側，例如詳圖立面的標籤排到房間兩側：\nboundary 可用 \"crop\"（視圖裁剪框）、\"tagged\"（被標元件的外框，預設）、元件 id（例如房間或牆）或 [u0,v0,u1,v1]（mm）；\nside 為 auto（依元件位置分左右）或 left/right/top/bottom；標籤頭距邊界 marginMm，同側相鄰標籤至少間隔 spacingMm；\n引線 elbow：orthogonal（直角，預設）或 straight。被標元件與標籤頭同高時引線會是一條水平線。\napply=false（預設）只回傳排列結果；確認後 apply=true，單一交易，任何錯誤整批回滾。",
    inputSchema: z.object({
      "viewId": z.number().int().describe("視圖 ElementId"),
      "tagIds": z.array(z.number().int()).describe("只排這些標籤（省略＝視圖中所有讀得到被標元件的標籤）").optional(),
      "boundary": z.union([z.string(), z.number().int(), z.array(z.number())]).describe("\"crop\"｜\"tagged\"｜元件 id｜[u0,v0,u1,v1]（mm）").optional(),
      "side": z.enum(["auto", "left", "right", "top", "bottom"]).describe("排在哪一側").default("auto"),
      "marginMm": z.number().describe("標籤頭距邊界（mm）").default(500),
      "spacingMm": z.number().describe("同側最小間距（mm）").default(400),
      "elbow": z.enum(["orthogonal", "straight"]).describe("引線轉折").default("orthogonal"),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/annotations/tags/arrange", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_create_wall_dimension_chains", {
    title: "Create wall dimension chains",
    description: "在平面視圖為直牆自動建立連續尺寸線：量測方向 horizontal 抓垂直走向的牆、vertical 抓水平走向的牆，both 兩向各一條。\nfaces：both（牆兩側面，可讀出牆厚與淨距）、exterior、interior；includeGrids 一併串軸線。同一位置重疊的參考面只留一個。\n尺寸線放在 side 那一側、距最外側牆端 offsetMm。預檢會回報每條尺寸鏈的參考數、各段長度與過短段落數。\napply=false（預設）只預檢；確認後 apply=true，單一交易，任何錯誤整批回滾。弧牆不支援。",
    inputSchema: z.object({
      "viewId": z.number().int().describe("平面視圖 ElementId"),
      "wallIds": z.array(z.number().int()).describe("只標這些牆（省略＝視圖中所有直牆）").optional(),
      "direction": z.enum(["horizontal", "vertical", "both"]).describe("量測方向").default("both"),
      "faces": z.enum(["both", "exterior", "interior"]).describe("參考牆的哪一側面").default("both"),
      "includeGrids": z.boolean().describe("一併串上軸線").default(false),
      "side": z.enum(["auto", "top", "bottom", "left", "right"]).describe("尺寸線放哪一側（auto：水平向放上方、垂直向放右方）").default("auto"),
      "offsetMm": z.number().describe("尺寸線距最外側牆端（mm）").default(1000),
      "dimensionTypeName": z.string().describe("線性尺寸類型名稱（省略用預設）").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/annotations/dimensions/wall-chains", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_inspect_dimensions", {
    title: "Inspect dimensions",
    description: "檢查視圖裡的線性尺寸（唯讀）：每個尺寸的類型、參考數、各段長度 segmentsMm、短於 minSegmentMm 的段數、尺寸線位置（uv），\n以及 crowdedPairs：平行且距離小於 300 mm、可能疊在一起的尺寸線。",
    inputSchema: z.object({
      "viewId": z.number().int().describe("視圖 ElementId"),
      "minSegmentMm": z.number().describe("小於這個長度的段落算過短（mm）").default(100),
    }).strict(), annotations: R,
  }, async (a) => { try { return ok(await revitRequest<Any>("get", "/annotations/dimensions/inspect", q(a as Any, []), undefined, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_adjust_dimensions", {
    title: "Adjust dimensions",
    description: "調整視圖中的尺寸：moves 逐一移動（offsetMm＝垂直於尺寸線平移、正負代表方向；deltaMm＝[du,dv] 位移，mm），deleteIds 刪除尺寸（例如重複的）。\napply=false（預設）只預檢；確認後 apply=true，單一交易，任何錯誤整批回滾。刪除前請先跟使用者確認。",
    inputSchema: z.object({
      "viewId": z.number().int().describe("視圖 ElementId"),
      "moves": z.array(z.object({ "dimensionId": z.number().int().describe("尺寸 ElementId"), "offsetMm": z.number().describe("垂直於尺寸線平移（mm）").optional(), "deltaMm": z.array(z.number()).min(2).max(2).describe("[du, dv] 位移（mm）").optional() }).strict()).describe("要移動的尺寸").optional(),
      "deleteIds": z.array(z.number().int()).describe("要刪除的尺寸 id").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: D,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/annotations/dimensions/adjust", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_labeled_snapshot", {
    title: "Labeled snapshot",
    description: "標號截圖：在視圖的元件中心暫時放編號文字（由上而下、由左而右編號；labelBy:\"id\" 改標元件 id），可選擇只顯示這些元件（isolate），預設暫時把裁剪框縮到被標元件範圍（cropToElements），\n輸出 PNG 後整個交易回滾，模型不留任何變更。回傳 filePath 與 labels（label→elementId、品類、名稱、中心 uv、外框 uv）。\n用途：看圖判斷空間關係後，用 label 對回元件 id，再交給標籤、尺寸、修改等工具。3D 視圖不能用（不能放文字註記）。",
    inputSchema: z.object({
      "viewId": z.number().int().describe("視圖 ElementId"),
      "elementIds": z.array(z.number().int()).describe("只標這些元件").optional(),
      "categories": z.array(z.string()).describe("只標這些品類（例如 Doors、Walls、PlumbingFixtures）").optional(),
      "maxLabels": z.number().int().describe("最多標幾個（上限 300）").default(80),
      "isolate": z.boolean().describe("只顯示被標的元件").default(false),
      "labelBy": z.enum(["index", "id"]).describe("標數字或元件 id").default("index"),
      "pixelSize": z.number().int().describe("圖片長邊像素").default(2048),
      "cropToElements": z.boolean().describe("暫時把裁剪框縮到被標元件範圍（截圖較清楚，同樣會回滾）").default(true),
      "cropMarginMm": z.number().describe("裁剪框外擴（mm）").default(1500),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/views/labeled-snapshot", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_cad_extract", {
    title: "Cad extract",
    description: "萃取平面視圖中 DWG（連結或匯入）的幾何，存成這次 Revit 工作階段的快取（最多 5 份），不改模型：\n線段、封閉聚合線、首尾相接的線段串成的封閉迴圈（chainLines）、整圓。回傳 cacheId、各圖層的線段／封閉迴圈／矩形／圓數量與範圍。\n可用 layers（圖層名關鍵字）與 regionMm（[minX,minY,maxX,maxY]，模型座標 mm）縮小範圍。\n接著用 revit_cad_generate_bim 依快取產生柱或樓板。",
    inputSchema: z.object({
      "viewId": z.number().int().describe("放有 DWG 的平面視圖"),
      "cadName": z.string().describe("DWG 名稱關鍵字（視圖裡有多個 DWG 時）").optional(),
      "layers": z.array(z.string()).describe("只萃取這些圖層（名稱關鍵字）").optional(),
      "regionMm": z.array(z.number()).min(4).max(4).describe("[minX,minY,maxX,maxY]（模型座標 mm）").optional(),
      "chainLines": z.boolean().describe("把首尾相接的線段串成封閉迴圈").default(true),
    }).strict(), annotations: R,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/cad/extract", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_cad_generate_bim", {
    title: "Cad generate bim",
    description: "依 DWG 快取產生 BIM 元件：\n- mode \"columns\"：圖層上的矩形迴圈與圓 → 柱（中心、旋轉角由程式從 CAD 計算）。預檢回報各尺寸數量與可用的柱類型；族群類型的斷面尺寸不會自動改，請用 onlySize 一次放一個尺寸，並指定對應的 familyType。\n- mode \"floors\"：圖層上面積 ≥ minAreaM2 的封閉迴圈 → 樓板。\napply=false（預設）只預檢；確認後 apply=true，單一交易，任何錯誤整批回滾。",
    inputSchema: z.object({
      "cacheId": z.string().describe("revit_cad_extract 回傳的 cacheId"),
      "mode": z.enum(["columns", "floors"]).describe("要產生的元件"),
      "layer": z.string().describe("圖層名稱或關鍵字"),
      "familyType": z.string().describe("「族群: 類型」：柱類型或樓板類型").optional(),
      "level": z.string().describe("樓層名稱（省略＝萃取時視圖的樓層）").optional(),
      "onlySize": z.string().describe("柱：只放這個尺寸，例如 \"600x600\"、\"500x800\"（短邊在前）或 \"Ø600\"；尺寸清單看預檢的 allSizesOnLayer").optional(),
      "topOffsetMm": z.number().describe("柱：頂部約束設為同樓層並偏移這個高度（mm）").optional(),
      "minSizeMm": z.number().describe("柱：最小邊長（mm）").default(100),
      "maxSizeMm": z.number().describe("柱：最大邊長（mm）").default(3000),
      "minAreaM2": z.number().describe("樓板：最小面積（m²）").default(1),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/cad/generate", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_list_schedules", {
    title: "List schedules",
    description: "列出模型中的明細表（不含樣板、圖框修訂表、關鍵註記表）：scheduleId、名稱、品類、欄位數。可用 nameContains 篩選名稱。",
    inputSchema: z.object({
      "nameContains": z.string().describe("名稱關鍵字").optional(),
    }).strict(), annotations: R,
  }, async (a) => { try { return ok(await revitRequest<Any>("get", "/schedules/list", q(a as Any, []), undefined, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_get_schedule_data", {
    title: "Get schedule data",
    description: "讀取明細表的表頭與本體儲存格文字（照明細表畫面原樣，含分組與小計列），可用 offset／maxRows 分頁。scheduleId 或 name 擇一。",
    inputSchema: z.object({
      "scheduleId": z.number().int().describe("明細表 ElementId").optional(),
      "name": z.string().describe("明細表名稱（完全相同優先，否則包含）").optional(),
      "offset": z.number().int().describe("從第幾列開始").default(0),
      "maxRows": z.number().int().describe("最多幾列（上限 2000）").default(300),
    }).strict(), annotations: R,
  }, async (a) => { try { return ok(await revitRequest<Any>("get", "/schedules/data", q(a as Any, []), undefined, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_batch_set_parameters", {
    title: "Batch set parameters",
    description: "批次設定參數，全有或全無：預檢逐筆檢查元件、參數、唯讀、型別，只要有一筆有問題，apply=true 就整批不執行。\n數值單位：長度 mm、面積 m²、體積 m³、角度 度；給字串時交給 Revit 依專案單位解析（例如 \"1200\" 或 \"1.2 m\"）。\n類型參數會影響同類型的所有元件，預設拒絕；確定要改請加 allowTypeParameters:true。\napply=false（預設）回傳每筆的目前值與新值；確認後 apply=true，單一交易，任何錯誤整批回滾。",
    inputSchema: z.object({
      "updates": z.array(z.object({ "elementId": z.number().int().describe("元件 id"), "parameter": z.string().describe("參數名稱"), "value": z.union([z.string(), z.number(), z.number().int(), z.boolean()]).describe("新值") }).strict()).min(1).max(5000).describe("要設定的參數"),
      "allowTypeParameters": z.boolean().describe("允許修改類型參數").default(false),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/parameters/batch", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_inspect_mep_connectors", {
    title: "Inspect mep connectors",
    description: "列出 MEP 元件的接頭（唯讀）：connectorId、領域、形狀、位置（mm）、方向、尺寸、是否已接、接到的元件、所屬系統。用來決定 revit_route_mep_connectors 要接哪兩個接頭。",
    inputSchema: z.object({
      "elementIds": z.array(z.number().int()).min(1).max(200).describe("元件 id"),
    }).strict(), annotations: R,
  }, async (a) => { try { return ok(await revitRequest<Any>("get", "/mep/connectors", q(a as Any, ["elementIds"]), undefined, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_route_mep_connectors", {
    title: "Route mep connectors",
    description: "在兩個元件的空接頭之間自動接管：from、to 各給 elementId（可加 connectorId；省略時在兩邊的空接頭中挑同領域、距離最近的一對）。\npath：orthogonal（預設，各自沿接頭方向伸出 clearanceMm 後以直角路徑連接）或 direct（一段直線）。水管或風管依接頭領域自動判斷。\n轉角會嘗試放彎頭（fittings），放不上時列在 fittingWarnings、管段照樣保留（通常是管路類型的佈線偏好沒設彎頭）。\napply=false（預設）回傳路徑點與段數；確認後 apply=true，單一交易，任何錯誤整批回滾。",
    inputSchema: z.object({
      "from": z.object({ "elementId": z.number().int(), "connectorId": z.number().int().optional() }).strict(),
      "to": z.object({ "elementId": z.number().int(), "connectorId": z.number().int().optional() }).strict(),
      "path": z.enum(["orthogonal", "direct"]).describe("路徑").default("orthogonal"),
      "clearanceMm": z.number().describe("沿接頭方向先伸出的長度（mm）").default(300),
      "pipeType": z.string().describe("水管類型").optional(),
      "ductType": z.string().describe("風管類型").optional(),
      "systemType": z.string().describe("中間段使用的系統類型（省略沿用接頭的系統）").optional(),
      "level": z.string().describe("參考樓層").optional(),
      "diameterMm": z.number().describe("管徑／圓形風管直徑").optional(),
      "widthMm": z.number().describe("矩形風管寬").optional(),
      "heightMm": z.number().describe("矩形風管高").optional(),
      "fittings": z.boolean().describe("轉角放彎頭").default(true),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/mep/route", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_audit_mep", {
    title: "Audit mep",
    description: "全模型 MEP 稽核（唯讀）：水管、風管、電纜架與導管的開口端（依品類統計＋樣本位置），以及設備、末端接頭沒有歸入任何系統的清單。開口端包含刻意留設的預留管，需依位置判斷。",
    inputSchema: z.object({
      "limit": z.number().int().describe("每類最多列幾筆樣本").default(200),
    }).strict(), annotations: R,
  }, async (a) => { try { return ok(await revitRequest<Any>("get", "/mep/audit", q(a as Any, []), undefined, LONG)); } catch (e) { return err(e); } });
}
