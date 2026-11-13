// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";

/**
 * 樓梯、欄杆、輪廓族、掃掠（牆飾條／分割縫、樓板邊緣、封簷板、排水溝）。
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

export function registerStairsSweepTools(server: McpServer): void {

  server.registerTool("revit_create_stairs", {
    title: "Create stairs",
    description: "建立組合樓梯：baseLevel、topLevel（樓層名稱）；startMm＝第一跑起點的中心線位置 [x,y]（mm）；directionDeg＝第一跑上樓方向（0＝+X、90＝+Y）。\nshape：straight 直跑、L（第一跑後經方形平台轉 90°）、U（經平台迴轉 180°，gapMm＝兩跑之間的間距）；turn left／right＝轉向。\n級數依樓高與樓梯類型的最大級高自動計算；firstRunRisers 指定第一跑級數（L／U）；treadDepthMm 級深（省略用類型的最小級深）；widthMm 梯段寬（預設 1200）。\nRevit 建樓梯時會依專案設定自動加欄杆：railingType 把欄杆換成指定類型；railings=false 刪掉欄杆；railings=true 時若沒有欄杆會補上。apply=false（預設）只預檢；任何錯誤整批回滾。",
    inputSchema: z.object({
      "baseLevel": z.string().describe("底部樓層"),
      "topLevel": z.string().describe("頂部樓層"),
      "startMm": z.array(z.number()).min(2).max(2).describe("[x, y]（mm）"),
      "directionDeg": z.number().describe("第一跑上樓方向（度，0＝+X）").default(0),
      "shape": z.enum(["straight", "L", "U"]).describe("樓梯形狀").default("straight"),
      "turn": z.enum(["left", "right"]).describe("L／U 的轉向").default("left"),
      "widthMm": z.number().describe("梯段寬 mm").default(1200),
      "firstRunRisers": z.number().int().describe("第一跑級數（L／U）").optional(),
      "treadDepthMm": z.number().describe("級深 mm").optional(),
      "landingDepthMm": z.number().describe("U 型平台深度 mm（省略＝梯段寬）").optional(),
      "gapMm": z.number().describe("U 型兩跑間距 mm").default(0),
      "stairsType": z.string().describe("樓梯類型（族群: 類型 或類型名）").optional(),
      "railings": z.boolean().describe("false＝刪掉自動欄杆；true＝沒有時補上").optional(),
      "railingType": z.string().describe("欄杆類型").optional(),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/stairs", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_create_railings", {
    title: "Create railings",
    description: "建立欄杆：給 stairsIds 會在每座沒有欄杆的樓梯上加欄杆（position treads 踏板／stringer 側版）；已有欄杆的樓梯在給了 railingType 時改成該類型，否則略過；否則用 pointsMm 折線路徑（mm）＋ level 建立。\nrailingType 指定類型（省略用第一個）。apply=false（預設）只預檢並列出可用類型；任何錯誤整批回滾。",
    inputSchema: z.object({
      "stairsIds": z.array(z.number().int()).describe("樓梯 id").optional(),
      "pointsMm": z.array(z.array(z.number()).min(2).max(2).describe("[x, y]（mm）")).describe("路徑折線點（mm）").optional(),
      "level": z.string().describe("路徑欄杆所在樓層").optional(),
      "railingType": z.string().describe("欄杆類型").optional(),
      "position": z.enum(["treads", "stringer"]).describe("掛在樓梯時的位置").default("treads"),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/railings", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_create_profile_family", {
    title: "Create profile family",
    description: "建立輪廓族：name（族名稱＝檔名）、pointsMm 封閉輪廓 [[x,y],...]（mm；x 水平、y 垂直，原點＝插入點；首尾自動相接、不可自相交）。\n用公制輪廓樣板（template 可指定其他 .rft 名稱或完整路徑），存到 folder（預設 %LOCALAPPDATA%\\revit-mcp\\profiles），load=true（預設）載入目前專案；同名族會被覆寫重新載入。\n建好後可在 revit_create_sweeps 的 profile 參數使用。apply=false（預設）只預檢。",
    inputSchema: z.object({
      "name": z.string().describe("輪廓族名稱"),
      "pointsMm": z.array(z.array(z.number()).min(2).max(2).describe("[x, y]（mm）")).min(3).describe("封閉輪廓點（mm）"),
      "template": z.string().describe("族樣板名稱或路徑").optional(),
      "folder": z.string().describe("存檔資料夾").optional(),
      "load": z.boolean().describe("載入目前專案").default(true),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/families/profile", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_create_sweeps", {
    title: "Create sweeps",
    description: "建立掃掠：\n- kind wallSweep 牆飾條／wallReveal 分割縫：wallIds、typeName（類型）、profile（輪廓族: 類型，省略沿用類型的輪廓）、heightMm（離牆底或牆頂 measuredFrom 的距離）、side exterior／interior、offsetMm（離牆面）、vertical（垂直飾條）。\n- kind slabEdge 樓板邊緣：hostIds＝樓板 id，沿頂面外圍每條邊。\n- kind fascia 封簷板／gutter 排水溝：hostIds＝屋頂 id，沿最低的水平簷口邊。\n樓板邊緣、封簷板、排水溝的輪廓跟著類型；要換輪廓請先用 revit_duplicate_type 複製類型再改「輪廓」類型參數。apply=false（預設）只預檢並列出可用類型與輪廓；任何錯誤整批回滾。",
    inputSchema: z.object({
      "kind": z.enum(["wallSweep", "wallReveal", "slabEdge", "fascia", "gutter"]).describe("掃掠種類"),
      "wallIds": z.array(z.number().int()).describe("牆 id（wallSweep／wallReveal）").optional(),
      "hostIds": z.array(z.number().int()).describe("樓板或屋頂 id（slabEdge／fascia／gutter）").optional(),
      "typeName": z.string().describe("類型").optional(),
      "profile": z.string().describe("輪廓（輪廓族: 類型）").optional(),
      "heightMm": z.number().describe("距離 mm").default(0),
      "measuredFrom": z.enum(["base", "top"]).describe("距離量自牆底或牆頂").default("base"),
      "side": z.enum(["exterior", "interior"]).describe("牆的哪一側").default("exterior"),
      "offsetMm": z.number().describe("離牆面 mm").default(0),
      "vertical": z.boolean().describe("垂直飾條").default(false),
      "apply": z.boolean().describe("false＝預檢（不改模型），true＝執行").default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/sweeps", undefined, a, LONG)); } catch (e) { return err(e); } });
}
