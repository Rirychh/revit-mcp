// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";

/**
 * 一般建模與修改：移動、複製陣列、換類型、複製類型、接合、門窗、天花、屋頂、開口。
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

export function registerEditTools(server: McpServer): void {

  server.registerTool("revit_move_elements", {
    title: "Move elements",
    description: "依向量移動模型元件：deltaMm=[dx,dy,dz]（mm，模型座標）。一次可移多個元件；已釘選（pinned）或在群組內的元件會跳過並說明原因。\napply=false（預設）只回傳會移動的元件；確認後 apply=true，單一交易，任何錯誤整批回滾。",
    inputSchema: z.object({
      "elementIds": z.array(z.number().int()).min(1).max(5000).describe("元件 id"),
      "deltaMm": z.array(z.number()).min(2).max(3).describe("位移 [dx, dy, dz]（mm，模型座標；dz 可省略）"),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/elements/move", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_array_elements", {
    title: "Array elements",
    description: "照向量複製元件：每一份依序位移 deltaMm×i（i＝1..count），count=1 就是單純複製一份，count>1 是線性陣列。\n複製出來的是獨立元件（不是 Revit 的陣列群組）。一次最多產生 5000 個。apply=false（預設）只預檢；確認後 apply=true，單一交易，任何錯誤整批回滾。",
    inputSchema: z.object({
      "elementIds": z.array(z.number().int()).min(1).max(5000).describe("元件 id"),
      "deltaMm": z.array(z.number()).min(2).max(3).describe("位移 [dx, dy, dz]（mm，模型座標；dz 可省略）"),
      "count": z.number().int().describe("複製幾份").default(1),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/elements/array", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_change_type", {
    title: "Change type",
    description: "換類型（等同在「性質」選另一個類型）：typeName 用「族群: 類型」、類型名或類型 id。只能換成該元件允許的類型（Revit 的 GetValidTypes）；\n不能換的會列出可換的類型清單。apply=false（預設）回傳每個元件的換前換後；確認後 apply=true，單一交易，任何錯誤整批回滾。",
    inputSchema: z.object({
      "elementIds": z.array(z.number().int()).min(1).max(5000).describe("元件 id"),
      "typeName": z.string().describe("目標類型：「族群: 類型」、類型名或類型 id"),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/elements/change-type", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_duplicate_type", {
    title: "Duplicate type",
    description: "複製類型並改名（等同「編輯類型 → 複製」）：sourceType 用「族群: 類型」、類型名或類型 id；newName 新類型名稱。\nthicknessMm：牆、樓板、屋頂、天花類型可直接指定總厚度，程式會調整最厚的那一層，其他層不變；預檢會回報調整的是哪一層、從多少變多少。\nparameters：{參數名稱: 值}，寫到新類型（例如「類型標記」、門窗的「寬度」「高度」）；數值單位 長度 mm、面積 m²、角度 度。\napply=false（預設）只預檢；確認後 apply=true，單一交易，任何錯誤整批回滾。",
    inputSchema: z.object({
      "sourceType": z.string().describe("來源類型：「族群: 類型」、類型名或類型 id"),
      "newName": z.string().describe("新類型名稱"),
      "thicknessMm": z.number().describe("總厚度（mm，只限有複合結構的類型）").optional(),
      "parameters": z.record(z.string(), z.union([z.string(), z.number(), z.number().int(), z.boolean()])).describe("類型參數 {名稱: 值}").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/types/duplicate", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_join_geometry", {
    title: "Join geometry",
    description: "接合幾何（修改 → 接合）：pairs 指定 [[a,b],...]；或 elementIds 給一批元件，程式找出外框相交、還沒接合的兩兩組合全部接合。\naction：join（預設）／unjoin／switch（切換接合順序，決定誰切誰）。個別組合失敗不影響其他組合，會列在 notDone。\napply=false（預設）只回傳要處理的組合；確認後 apply=true。",
    inputSchema: z.object({
      "pairs": z.array(z.array(z.number().int()).min(2).max(2).describe("[a, b]")).describe("元件組合").optional(),
      "elementIds": z.array(z.number().int()).describe("一批元件，自動找相交的組合").optional(),
      "action": z.enum(["join", "unjoin", "switch"]).describe("動作").default("join"),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/geometry/join", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_place_doors_windows", {
    title: "Place doors windows",
    description: "放門窗（有主體的族群）：familyType 用「族群: 類型」；items 每個給 pointMm=[x,y]（mm），程式找最近的直牆（距離 > 牆厚/2+300 mm 視為找不到）並投影到牆中心線，\n也可用 wallId 指定牆。樓層用牆的樓層。sillHeightMm 設窗台高；flipFacing／flipHand 翻轉開門方向。\napply=false（預設）回傳每個位置找到的牆與投影點；確認後 apply=true，單一交易，任何錯誤整批回滾。",
    inputSchema: z.object({
      "familyType": z.string().describe("門或窗的「族群: 類型」"),
      "items": z.array(z.object({ "pointMm": z.array(z.number()).min(2).max(2).describe("[x, y]（mm）"), "wallId": z.number().int().describe("指定牆（省略自動找最近的）").optional(), "sillHeightMm": z.number().describe("窗台高（mm）").optional(), "flipFacing": z.boolean().describe("翻轉內外").optional(), "flipHand": z.boolean().describe("翻轉左右").optional() }).strict()).min(1).max(500).describe("要放的位置"),
      "level": z.string().describe("樓層（通常不用給，沿用牆的樓層）").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/hosted/place", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_create_ceilings", {
    title: "Create ceilings",
    description: "建立天花板：roomIds 依房間邊界（完成面）各建一片；或 loops 給封閉折線（需要 level）。heightMm 設離樓層高度。\n房間只取外圍邊界（房間內的柱位不挖空）。apply=false（預設）只預檢並列出可用天花類型；確認後 apply=true，單一交易，任何錯誤整批回滾。",
    inputSchema: z.object({
      "roomIds": z.array(z.number().int()).describe("依這些房間的邊界建立").optional(),
      "loops": z.array(z.array(z.array(z.number()).min(2).max(2).describe("[x, y]（mm）")).min(3).describe("封閉折線的頂點 [[x,y],...]（mm，不需重複起點）")).describe("封閉折線（mm）").optional(),
      "level": z.string().describe("loops 用的樓層名稱").optional(),
      "ceilingType": z.string().describe("天花類型（省略取第一個）").optional(),
      "heightMm": z.number().describe("離樓層高度（mm）").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/ceilings", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_create_roof", {
    title: "Create roof",
    description: "依跡線建立屋頂（跡線屋頂）：pointsMm 封閉折線（mm），level 屋頂所在樓層，baseOffsetMm 基準偏移。\nslopeDeg=0（預設）為平屋頂；>0 時每條邊都定義坡度（形成四坡／多坡屋頂）。apply=false（預設）只預檢並列出可用屋頂類型；確認後 apply=true。",
    inputSchema: z.object({
      "pointsMm": z.array(z.array(z.number()).min(2).max(2).describe("[x, y]（mm）")).min(3).describe("封閉折線的頂點 [[x,y],...]（mm，不需重複起點）"),
      "level": z.string().describe("屋頂所在樓層名稱"),
      "roofType": z.string().describe("屋頂類型（省略取第一個）").optional(),
      "baseOffsetMm": z.number().describe("基準偏移（mm）").optional(),
      "slopeDeg": z.number().describe("坡度（度），0＝平屋頂").default(0),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/roofs", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_create_opening", {
    title: "Create opening",
    description: "建立開口：\n- kind \"wall\"：wallId 直牆；alongMm 離牆起點的距離、widthMm 寬、sillMm 離牆底高、heightMm 高。\n- kind \"shaft\"：pointsMm 封閉折線，bottomLevel、topLevel 樓層名稱，豎井會切穿之間的樓板。\n- kind \"floor\"：hostId 樓板／屋頂／天花，pointsMm 封閉折線（垂直開口）。\napply=false（預設）只預檢；確認後 apply=true。",
    inputSchema: z.object({
      "kind": z.enum(["wall", "shaft", "floor"]).describe("開口種類"),
      "wallId": z.number().int().describe("wall：牆 id").optional(),
      "alongMm": z.number().describe("wall：離牆起點距離（mm）").optional(),
      "widthMm": z.number().describe("wall：寬（mm）").optional(),
      "sillMm": z.number().describe("wall：離牆底高（mm）").default(0),
      "heightMm": z.number().describe("wall：高（mm）").optional(),
      "pointsMm": z.array(z.array(z.number()).min(2).max(2).describe("[x, y]（mm）")).min(3).describe("封閉折線的頂點 [[x,y],...]（mm，不需重複起點）").optional(),
      "bottomLevel": z.string().describe("shaft：底部樓層").optional(),
      "topLevel": z.string().describe("shaft：頂部樓層").optional(),
      "hostId": z.number().int().describe("floor：樓板／屋頂／天花 id").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/openings", undefined, a, LONG)); } catch (e) { return err(e); } });
}
