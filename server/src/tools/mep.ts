// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";

/** MEP 建模。幾何一律 mm、專案內部座標；寫入前一律先 apply=false 預檢。 */

type Any = Record<string, unknown>;
const ok  = (d: unknown) => ({ content: [{ type: "text" as const, text: "```json\n" + JSON.stringify(d, null, 2) + "\n```" }], structuredContent: d as Any });
const err = (e: unknown) => ({ content: [{ type: "text" as const, text: `Error: ${e instanceof Error ? e.message : e}` }] });
const W = { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false };
const LONG = 5 * 60 * 1000;

const Pt3 = z.array(z.number()).min(2).max(3).describe("[x, y, z] mm；缺 z 時為 0");
const Segment = z.object({ start: Pt3, end: Pt3 }).strict();

export function registerMepTools(server: McpServer): void {

  server.registerTool("revit_create_ducts", {
    title: "Create ducts",
    description: `建立風管：每段由 start、end 兩點（mm）定義，一次可建多段。
apply=false（預設）只預檢：回傳會用的風管類型、系統類型、樓層，以及模型裡可用的類型清單，不改模型。確認後再用 apply=true 建立，單一交易，任何錯誤整批回滾。
不會自動產生接頭（彎頭、三通）；相連的段落之間需另外處理。
Args:
  - segments: [{ start:[x,y,z], end:[x,y,z] }]
  - ductType: 風管類型（「族群: 類型」或類型名；省略取第一個）
  - systemType: 風管系統類型名（例如 Supply Air；省略取第一個）
  - level: 參考樓層名（省略取最低點以下最近的樓層）
  - widthMm / heightMm: 矩形風管尺寸；diameterMm: 圓形風管直徑
  - apply: false = 預檢；true = 建立`,
    inputSchema: z.object({
      segments: z.array(Segment).min(1).max(500),
      ductType: z.string().optional(),
      systemType: z.string().optional(),
      level: z.string().optional(),
      widthMm: z.number().positive().optional(),
      heightMm: z.number().positive().optional(),
      diameterMm: z.number().positive().optional(),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/mep/ducts", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_create_pipes", {
    title: "Create pipes",
    description: `建立水管：每段由 start、end 兩點（mm）定義，一次可建多段。
apply=false（預設）只預檢，回傳會用的水管類型、管路系統類型、樓層與可用類型清單。確認後再用 apply=true 建立，單一交易，任何錯誤整批回滾。
不會自動產生管件（彎頭、三通）。
Args:
  - segments: [{ start:[x,y,z], end:[x,y,z] }]
  - pipeType: 水管類型（省略取第一個）
  - systemType: 管路系統類型名（例如 Domestic Cold Water；省略取第一個）
  - level: 參考樓層名
  - diameterMm: 管徑
  - apply: false = 預檢；true = 建立`,
    inputSchema: z.object({
      segments: z.array(Segment).min(1).max(500),
      pipeType: z.string().optional(),
      systemType: z.string().optional(),
      level: z.string().optional(),
      diameterMm: z.number().positive().optional(),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/mep/pipes", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_create_mep_system", {
    title: "Create MEP system",
    description: `把設備、末端（風口、器具）的空接頭組成一個風管系統或管路系統。只收「還沒接上任何系統」且領域相符的接頭。
apply=false（預設）只預檢，逐一回報每個元件有幾個可用接頭。確認後再用 apply=true 建立。
Args:
  - kind: "duct"（風管系統）｜"pipe"（管路系統）
  - elementIds: 設備／末端元件 id
  - systemType: 系統類型名（省略取第一個）
  - name: 系統名稱（省略自動產生）
  - apply: false = 預檢；true = 建立`,
    inputSchema: z.object({
      kind: z.enum(["duct", "pipe"]).default("duct"),
      elementIds: z.array(z.number().int()).min(1),
      systemType: z.string().optional(),
      name: z.string().optional(),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/mep/systems", undefined, a, LONG)); } catch (e) { return err(e); } });
}
