// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";

type Any = Record<string, unknown>;
const ok  = (d: unknown) => ({ content: [{ type: "text" as const, text: "```json\n" + JSON.stringify(d, null, 2) + "\n```" }], structuredContent: d as Any });
const errOut = (e: unknown) => ({ content: [{ type: "text" as const, text: `Error: ${e instanceof Error ? e.message : e}` }] });
const docArg = z.string().optional().describe("目標文件名關鍵字；省略＝作用中文件");
const LONG = 30 * 60 * 1000;

interface PropertyLineAltResult {
  applied: boolean;
  name: string;
  source: string;
  lineStyle: string;
  vertexCount: number;
  areaM2: number;
  perimeterM: number;
  selfIntersecting: boolean;
  crossings: number[][];
  verticesMm: number[][];
  replaced: number;
  curveIds: number[];
  groupId: number;
  groupName?: string;
  warnings: string[];
}

export function registerSiteTools(server: McpServer): void {
  // ── revit_create_property_line ──────────────────────────────────────────
  server.registerTool(
    "revit_create_property_line",
    {
      title: "Create Property Line (substitute)",
      description: `Draw a site property line as a closed loop of model lines on a dedicated "地界線" line style, grouped as a model group.

Revit 2024's API has no public PropertyLine.Create, so a native property line cannot be made from code. This tool draws a substitute instead: model lines with a red dash-dot line style (created if missing), grouped as "地界線_<name>". The area (㎡) and perimeter (m) go into the Comments of every line and of the group. A native property-line tag or bearing table is not available.

Boundary source, pick one:
  - points: vertices [[x,y],...] in mm, project internal coordinates (same as revit_list_elements)
  - layer (+ optional cadLink): take the longest closed polyline on that layer of a linked/imported CAD file
    (falls back to chaining loose straight lines on that layer)

Run with apply=false first: it returns vertex count, area, perimeter, self-intersection check and the resolved vertices.
apply=true creates the lines in one transaction. With replace=true (default) an earlier property line of the same name is removed first.

Args:
  - points (number[][], optional): [[x,y],...] mm
  - cadLink (string, optional): part of the CAD link name, e.g. "site_plan"
  - layer (string, optional): CAD layer name, e.g. "S-SITE_BOUNDARY"
  - name (string): label, default "基地"
  - lineStyleName (string): default "地界線"
  - elevationMm (number): Z of the lines, default 0
  - group (boolean): default true
  - replace (boolean): default true
  - apply (boolean): default false (preview)`,
      inputSchema: z.object({
        points:        z.array(z.array(z.number()).min(2).max(3)).min(3).optional().describe("Vertices [[x,y],...] in mm"),
        cadLink:       z.string().optional().describe("Part of the CAD link/import name"),
        layer:         z.string().optional().describe("CAD layer holding the boundary"),
        name:          z.string().default("基地").describe("Label used in group name and comments"),
        lineStyleName: z.string().default("地界線").describe("Line style (subcategory of Lines)"),
        colorRgb:      z.array(z.number().int().min(0).max(255)).length(3).optional().describe("Line style colour when it is created"),
        lineWeight:    z.number().int().min(1).max(16).optional().describe("Projection line weight when the style is created"),
        elevationMm:   z.number().default(0).describe("Elevation of the lines in mm"),
        group:         z.boolean().default(true),
        replace:       z.boolean().default(true),
        apply:         z.boolean().default(false).describe("false = preview, true = create"),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async (args) => {
      try {
        if (!args.points && !args.layer)
          return { content: [{ type: "text", text: "Error: 請提供 points 或 layer 其中之一。" }] };
        const r = await revitRequest<PropertyLineAltResult>("post", "/site/property-line", undefined, args);
        const lines = [
          `# 地界線（替代元素）— ${r.applied ? "已建立" : "預覽"}`,
          `- 名稱：${r.name}　來源：${r.source}　線型：${r.lineStyle}`,
          `- 頂點：${r.vertexCount}　面積：${r.areaM2} ㎡　周長：${r.perimeterM} m`,
          `- 自相交：${r.selfIntersecting ? `是 ✘（${r.crossings.map(c => c.join("×")).join(", ")}）` : "否 ✔"}`,
        ];
        if (r.applied) {
          lines.push(`- 模型線：${r.curveIds.length} 條${r.replaced ? `（取代舊線 ${r.replaced} 條）` : ""}`);
          if (r.groupId) lines.push(`- 群組：${r.groupName}（id ${r.groupId}）`);
        } else {
          lines.push(`- 頂點（mm）：${r.verticesMm.map(v => `(${v[0]}, ${v[1]})`).join(" ")}`);
          lines.push(`\n> 這是預覽。確認後加 apply=true 才會建立。`);
        }
        if (r.warnings.length) lines.push(`- 警告：${r.warnings.join("; ")}`);
        return { content: [{ type: "text", text: lines.join("\n") }], structuredContent: r as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_simplify_site ──────────────────────────────────────────────────
  server.registerTool("revit_simplify_site", {
    title: "Simplify site model (DirectShape + Toposolid)",
    description: `基地模精簡：DirectShape 共平面三角面合併＋Toposolid 格網減點，用來把動輒數十萬面的基地模拉回可操作的量。
⚠ apply=true 會重建幾何並直接寫入模型，原始三角面無法還原（Ctrl+Z 以外沒有退路）—— 先用 apply=false 預檢並向使用者確認，大模型建議先存檔。
每次呼叫（含預檢）都會把進度寫到 %TEMP%\\mcp_site_simplify.log。大模型可能跑很久（本工具逾時設 30 分鐘）。
Args:
  - document: 目標文件名關鍵字；省略＝作用中文件
  - applicationId: 只處理這個 applicationId 的 DirectShape（預設 RiR_site）
  - angleDeg / distMm: 共平面判定的角度（預設 1 度）與距離（預設 5mm）容差
  - topoCellM: Toposolid 減點格網邊長（m，預設 45）；<=0 ＝跳過地形
  - limit: 最多處理幾個元素；0 ＝不限（預設 0）
  - apply: false = 預檢（只回報將精簡什麼，不改模型）；確認後再用 true 執行`,
    inputSchema: z.object({
      document:      docArg,
      applicationId: z.string().default("RiR_site"),
      angleDeg:      z.number().default(1),
      distMm:        z.number().default(5),
      topoCellM:     z.number().default(45),
      limit:         z.number().int().min(0).default(0),
      apply:         z.boolean().default(false),
    }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/site/simplify", undefined, a, LONG)); } catch (e) { return errOut(e); } });

  // ── revit_site_directshapes ──────────────────────────────────────────────
  server.registerTool("revit_site_directshapes", {
    title: "Query / delete site DirectShape elements",
    description: `查詢（apply=false，唯讀）或刪除（apply=true）符合條件的 DirectShape 基地模元素。
⚠ apply=true 會直接刪除所有符合條件的 DirectShape，沒有第二層確認，Ctrl+Z 以外無法還原。務必先用 apply=false 看清單。
⚠⚠ 範圍過濾是 all-or-nothing：minX / minY / maxX / maxY 必須四個都給才生效。只給三個的話 handler 會整段忽略範圍過濾 ——
在 apply=true 下那就等於「刪掉全部符合其他條件的元素」。本工具因此在送出前自行檢查：只給一部分就直接擋下不送請求。
另外注意此路由的 applicationId 沒有預設值：什麼過濾條件都不給就是「全部」。
Args:
  - document: 目標文件名關鍵字；省略＝作用中文件
  - applicationId / dataIdPrefix / commentPrefix / nameContains: 過濾條件
  - minX / minY / maxX / maxY: 範圍過濾（mm），四個都要給
  - limit: 最多回報／處理幾個（預設 200）
  - apply: false = 只查詢（預設）；true = 刪除符合的元素`,
    inputSchema: z.object({
      document:      docArg,
      applicationId: z.string().optional(),
      dataIdPrefix:  z.string().optional(),
      commentPrefix: z.string().optional(),
      nameContains:  z.string().optional(),
      minX: z.number().optional(), minY: z.number().optional(),
      maxX: z.number().optional(), maxY: z.number().optional(),
      limit: z.number().int().min(1).default(200),
      apply: z.boolean().default(false),
    }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
  }, async (a) => {
    const bounds = [a.minX, a.minY, a.maxX, a.maxY];
    const given = bounds.filter((v) => v !== undefined).length;
    if (given > 0 && given < 4)
      return errOut(
        "範圍過濾必須 minX / minY / maxX / maxY 四個都給。" +
        "只給一部分的話 Revit 端會整段忽略範圍過濾，apply=true 時會變成「全部刪」—— 所以這裡直接擋下，不送請求。"
      );
    try { return ok(await revitRequest<Any>("post", "/site/directshapes", undefined, a, LONG)); } catch (e) { return errOut(e); }
  });

  // ── revit_draw_cad_lines ─────────────────────────────────────────────────
  server.registerTool("revit_draw_cad_lines", {
    title: "Draw CAD polylines as Revit model lines",
    description: `把外部轉出的 CAD 折線座標畫成 Revit 模型線，每條線的註解寫成 CAD:{tag}|{style}，方便整批辨識與取代。
⚠ 這支會刪東西：replace 預設 true，會先刪掉所有註解以 CAD:{tag}| 開頭的既有 CurveElement；removeCadLinks 還會刪掉名稱符合的 CADLinkType。
apply=true 才會真的寫入／刪除；先用 apply=false 預檢並向使用者確認 tag 與 removeCadLinks。
Args:
  - document: 目標文件名關鍵字；省略＝作用中文件
  - elevationMm: 模型線高程（mm，預設 0）
  - tag: 註解標籤，用來辨識／取代這批線（預設 "cad"）
  - replace: 先刪掉同 tag 的既有線（預設 true）
  - styles: [{name, rgb?, weight?, dash?}] 線型式定義，不存在就建立
  - curves: [{pts: [[x,y],...], style?, closed?}]（mm）；少於 2 點的會被跳過
  - removeCadLinks: 要一併刪除的 CADLinkType 名稱關鍵字
  - apply: false = 預檢（不改模型）；確認後再用 true 執行`,
    inputSchema: z.object({
      document:    docArg,
      elevationMm: z.number().default(0),
      tag:         z.string().default("cad"),
      replace:     z.boolean().default(true),
      styles: z.array(z.object({
        name:   z.string().min(1),
        rgb:    z.array(z.number().int().min(0).max(255)).length(3).optional().describe("顏色；省略＝灰 128,128,128"),
        weight: z.number().int().optional().describe("線寬；省略＝1"),
        dash:   z.string().optional().describe("虛線樣式關鍵字，以 | 分隔，比對 LinePatternElement 名稱"),
      }).strict()).optional(),
      curves: z.array(z.object({
        style:  z.string().optional(),
        pts:    z.array(z.array(z.number()).length(2)).min(2).describe("折線點 [[x,y],...]（mm）"),
        closed: z.boolean().default(false),
      }).strict()).optional(),
      removeCadLinks: z.array(z.string()).optional(),
      apply: z.boolean().default(false),
    }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/lines/draw", undefined, a, LONG)); } catch (e) { return errOut(e); } });
}
