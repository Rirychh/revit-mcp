// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";
import { ResponseFormat, CHARACTER_LIMIT } from "../constants.js";

interface XyzMm { x: number; y: number; z: number; }
interface LinkedElementDto {
  id: number; source: string; linkInstanceId: number; category: string;
  familyName: string; typeName: string; mark: string; levelName: string;
  bboxMin: XyzMm; bboxMax: XyzMm; start?: XyzMm; end?: XyzMm;
  parameters: Record<string, string>;
}
interface LinkedElementsResult {
  category: string; total: number; count: number; offset: number; hasMore: boolean;
  searchedSources: string[]; unloadedLinks: string[]; elements: LinkedElementDto[];
}

const f = (n: number) => n.toFixed(0);

export function registerLinkedTools(server: McpServer): void {

  // ── revit_list_linked_elements ───────────────────────────────────────────
  server.registerTool(
    "revit_list_linked_elements",
    {
      title: "List Elements in Linked Models",
      description: `List elements inside LINKED Revit models (structure ST, MEP, etc.). revit_list_elements
only sees the active document, so beams/columns that live in a structural link are invisible
to it — use this tool for those.

All coordinates are converted to the HOST model's coordinates, in mm, so they can be compared
directly with revit_list_elements / revit_list_levels output.
For beams: beam soffit (梁底) = bboxMin.z, beam top = bboxMax.z; start/end = location line.

Args:
  - category (string, optional): Beams (default) / Columns / Floors / Walls / Foundation, or a raw OST_ name
  - source (string, optional): Substring of the link file name, e.g. "ST01"
  - level (string, optional): Substring of the element's (reference) level name in the link, e.g. "B1"
  - family / type / mark (string, optional): Substring filters
  - minX/minY/minZ/maxX/maxY/maxZ (number, optional): Region filter in HOST mm; each bound is applied
    independently, an element is kept when its bounding box overlaps
  - params (string, optional): Comma-separated parameter names to read (instance first, then type).
    Default tries common section params (b, h, 寬度, 深度, 梁寬, 梁深…). Length values return in mm.
  - limit (number): default 50, offset (number): default 0
  - response_format: "markdown" or "json"`,
      inputSchema: z.object({
        category: z.string().optional().describe("Beams / Columns / Floors / Walls / Foundation or OST_ name"),
        source:   z.string().optional().describe("Link file name substring, e.g. ST01"),
        level:    z.string().optional().describe("Level name substring in the link"),
        family:   z.string().optional().describe("Family name substring"),
        type:     z.string().optional().describe("Type name substring"),
        mark:     z.string().optional().describe("Mark (標記) substring"),
        minX: z.number().optional(), minY: z.number().optional(), minZ: z.number().optional(),
        maxX: z.number().optional(), maxY: z.number().optional(), maxZ: z.number().optional(),
        params:   z.string().optional().describe("Comma-separated parameter names to read"),
        limit:    z.number().int().min(1).max(500).default(50),
        offset:   z.number().int().min(0).default(0),
        response_format: z.nativeEnum(ResponseFormat).default(ResponseFormat.MARKDOWN),
      }).strict(),
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ response_format, ...q }) => {
      try {
        const r = await revitRequest<LinkedElementsResult>("get", "/elements/linked", q);
        let text: string;
        if (response_format === ResponseFormat.JSON) {
          text = JSON.stringify(r, null, 2);
        } else {
          const lines = [
            `# Linked elements — ${r.category}`,
            `**Total:** ${r.total} | Showing ${r.count} (offset ${r.offset})` +
              (r.hasMore ? ` | next: offset=${q.offset + q.limit}` : ""),
            `**Searched:** ${r.searchedSources.join(", ") || "—"}` +
              (r.unloadedLinks.length ? `\n**Unloaded links:** ${r.unloadedLinks.join(", ")}` : ""),
            "",
            "| id | 來源 | 族群 / 類型 | 標記 | 樓層 | 底 z | 頂 z | 起點 (x,y) | 終點 (x,y) | 參數 |",
            "|---:|---|---|---|---|---:|---:|---|---|---|",
          ];
          for (const e of r.elements) {
            const s = e.start ? `${f(e.start.x)}, ${f(e.start.y)}` : "";
            const en = e.end ? `${f(e.end.x)}, ${f(e.end.y)}` : "";
            const ps = Object.entries(e.parameters).map(([k, v]) => `${k}=${v}`).join("; ");
            lines.push(`| ${e.id} | ${e.source} | ${e.familyName} / ${e.typeName} | ${e.mark} | ${e.levelName} | ` +
                       `${f(e.bboxMin.z)} | ${f(e.bboxMax.z)} | ${s} | ${en} | ${ps} |`);
          }
          text = lines.join("\n");
        }
        if (text.length > CHARACTER_LIMIT)
          text = text.slice(0, CHARACTER_LIMIT) + "\n\n> Response truncated. Narrow the region or use offset/limit.";
        return { content: [{ type: "text", text }], structuredContent: r as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_list_linked_rooms ──────────────────────────────────────────────
  // revit_list_rooms 只看作用中文件；房間常住在建築連結裡，要用這支才看得到。
  server.registerTool("revit_list_linked_rooms", {
    title: "List rooms across host + all Revit links",
    description: `跨主模型與所有 Revit 連結列出房間（含中心點座標，mm，已換算成主模型座標）。
Args:
  - name: 房間名稱關鍵字
  - level: 樓層名稱關鍵字`,
    inputSchema: z.object({
      name:  z.string().optional().describe("房間名稱關鍵字"),
      level: z.string().optional().describe("樓層名稱關鍵字"),
    }).strict(),
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  }, async (a) => {
    try {
      const r = await revitRequest<unknown>("get", "/rooms/linked", a);
      let text = "```json\n" + JSON.stringify(r, null, 2) + "\n```";
      if (text.length > CHARACTER_LIMIT)
        text = text.slice(0, CHARACTER_LIMIT) + "\n\n> 回應已截斷，請用 name / level 縮小範圍。";
      return { content: [{ type: "text", text }], structuredContent: r as Record<string, unknown> };
    } catch (e) {
      return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
    }
  });
}
