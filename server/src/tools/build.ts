// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";

/** 幾何一律 mm、專案座標。Revit 內部英呎換算在 add-in 端處理。 */

const Pt = z.array(z.number()).length(2);

interface NewProjectResult {
  savedPath: string; central: boolean; lengthUnit: string;
  opened: boolean; title: string; note: string;
}
interface LevelDto { id: number; name: string; elevation: number }
interface CreateLevelsResult { created: number; skipped: string[]; levels: LevelDto[] }
interface BuildTypeDto { id: number; family: string; name: string; kind: string; widthMm?: number | null }
interface CreateWallsResult {
  created: number; wallTypeUsed: string; levelName: string; ids: number[]; skipped: string[];
}
interface CreateFloorsResult {
  created: number; openings: number; floorTypeUsed: string; ids: number[]; skipped: string[];
}
interface CreateColumnsResult { created: number; ids: number[]; skipped: string[] }

function skipBlock(skipped: string[], limit = 15): string {
  if (!skipped || skipped.length === 0) return "";
  const head = skipped.slice(0, limit).map(s => `  - ${s}`).join("\n");
  const more = skipped.length > limit ? `\n  - …另有 ${skipped.length - limit} 筆` : "";
  return `\n\n**略過 / 失敗（${skipped.length}）：**\n${head}${more}`;
}

export function registerBuildTools(server: McpServer): void {

  // ── revit_new_project ────────────────────────────────────────────────────
  server.registerTool(
    "revit_new_project",
    {
      title: "New Revit Project (units + central file)",
      description: `Create a NEW Revit project from a template, set its length unit, optionally
save it as a worksharing CENTRAL file, and open it as the active document.

This does not touch any document that is already open — it creates a separate file.
Once opened, every other revit_* tool acts on this new document.

Args:
  - templatePath (string): full path to a .rte project template
  - savePath (string): full path of the .rvt to write
  - lengthUnit (string, optional): "cm" | "mm" | "m" — project length unit
  - central (bool, optional): true enables worksharing and saves as a central model
  - overwrite (bool, optional, default true)
  - openAfterSave (bool, optional, default true)`,
      inputSchema: z.object({
        templatePath:       z.string().min(1),
        savePath:           z.string().min(1),
        lengthUnit:         z.enum(["cm", "mm", "m"]).optional(),
        central:            z.boolean().default(false),
        levelsGridsWorkset: z.string().optional(),
        defaultWorkset:     z.string().optional(),
        overwrite:          z.boolean().default(true),
        openAfterSave:      z.boolean().default(true),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async (params) => {
      try {
        const r = await revitRequest<NewProjectResult>("post", "/project/new", undefined, params);
        const text = [
          `✅ 已建立新專案：**${r.title}**`,
          `- 路徑：\`${r.savedPath}\``,
          `- 中央檔：${r.central ? "是（已啟用工作共用）" : "否"}`,
          `- 長度單位：${r.lengthUnit}`,
          `- ${r.note}`,
        ].join("\n");
        return { content: [{ type: "text", text }], structuredContent: r as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_create_levels ──────────────────────────────────────────────────
  server.registerTool(
    "revit_create_levels",
    {
      title: "Create Revit Levels",
      description: `Create levels in the active Revit model, in one batch.

Elevations are in mm from the project base point. A level whose elevation already
exists (±0.5 mm) is skipped when skipExisting is true, so re-running is safe.

Args:
  - levels (array): [{ name?: string, elevationMm: number }, ...]
  - skipExisting (bool, default true)`,
      inputSchema: z.object({
        levels: z.array(z.object({
          name:        z.string().optional(),
          elevationMm: z.number(),
        })).min(1).max(400),
        skipExisting: z.boolean().default(true),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async (params) => {
      try {
        const r = await revitRequest<CreateLevelsResult>("post", "/levels", undefined, params);
        const sample = r.levels.slice(0, 8)
          .map(l => `  - [${l.id}] ${l.name} @ ${l.elevation.toFixed(0)} mm`).join("\n");
        const text = `✅ 建立標高 **${r.created}** 個`
          + (r.levels.length ? `\n${sample}${r.levels.length > 8 ? `\n  - …共 ${r.levels.length} 個` : ""}` : "")
          + skipBlock(r.skipped);
        return { content: [{ type: "text", text }], structuredContent: r as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_list_build_types ───────────────────────────────────────────────
  server.registerTool(
    "revit_list_build_types",
    {
      title: "List Wall / Floor Types",
      description: `List the wall types or floor types available in the active model.

Use this before revit_create_walls to find the exact curtain-wall type name
("kind": "Curtain"), or before revit_create_floors to pick a base floor type.

Args:
  - category (string): "walls" or "floors"`,
      inputSchema: z.object({
        category: z.enum(["walls", "floors"]).default("walls"),
      }).strict(),
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ category }) => {
      try {
        const types = await revitRequest<BuildTypeDto[]>("get", "/buildtypes", { category });
        const text = [
          `# ${category === "walls" ? "牆型別" : "樓板型別"}（${types.length}）`, "",
          ...types.map(t => `- **${t.name}** — ${t.kind}`
            + (t.widthMm != null ? `  |  厚 ${t.widthMm} mm` : "")
            + `  |  ${t.family}  |  id ${t.id}`),
        ].join("\n");
        return { content: [{ type: "text", text }], structuredContent: { types } as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_create_walls ───────────────────────────────────────────────────
  server.registerTool(
    "revit_create_walls",
    {
      title: "Create Revit Walls (incl. curtain walls)",
      description: `Create walls along one or more polylines on a single level.

Each loop is turned into one wall per segment. Pass a curtain-wall type name to
build a curtain wall (its curtain grid then divides the panels); pass a basic
glass wall type for a simple glazed skin. Points are mm, project coordinates.
Segments shorter than minSegmentMm are merged away.

Args:
  - wallTypeName (string, optional): exact or partial type name; omit for the model default
  - levelName (string): base level
  - heightMm (number): unconnected height
  - baseOffsetMm (number, optional): offset from the level
  - loops (array): [{ points: [[x,y], ...], closed?: bool }, ...]`,
      inputSchema: z.object({
        wallTypeName: z.string().optional(),
        levelName:    z.string().min(1),
        heightMm:     z.number().positive(),
        baseOffsetMm: z.number().default(0),
        loops: z.array(z.object({
          points: z.array(Pt).min(2),
          closed: z.boolean().default(true),
        })).min(1),
        minSegmentMm: z.number().default(2),
        structural:   z.boolean().default(false),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async (params) => {
      try {
        const r = await revitRequest<CreateWallsResult>("post", "/walls", undefined, params);
        const text = `✅ 在 **${r.levelName}** 建立牆 **${r.created}** 道（型別：${r.wallTypeUsed}）`
          + skipBlock(r.skipped);
        return { content: [{ type: "text", text }], structuredContent: r as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_create_floors ──────────────────────────────────────────────────
  server.registerTool(
    "revit_create_floors",
    {
      title: "Create Revit Floors (with openings)",
      description: `Create floors on one or more levels, in one batch.

Each floor takes an outer boundary plus any number of opening boundaries
(inner loops are cut out of the slab — use them for atria / voids).
Points are mm, project coordinates.

Setting thicknessMm duplicates the base floor type once and sets its core layer
width, so all floors in the call share one clean type. Re-running with the same
thickness reuses the type it made before instead of duplicating again.

Args:
  - floorTypeName (string, optional): base type; omit to use the first available
  - thicknessMm (number, optional): core layer thickness for the duplicated type
  - newTypeName (string, optional): name for that duplicated type
  - floors (array): [{ levelName, outer: [[x,y],...], openings?: [[[x,y],...],...], heightOffsetMm? }, ...]`,
      inputSchema: z.object({
        floorTypeName: z.string().optional(),
        newTypeName:   z.string().optional(),
        thicknessMm:   z.number().positive().optional(),
        minSegmentMm:  z.number().default(2),
        floors: z.array(z.object({
          levelName:      z.string().min(1),
          outer:          z.array(Pt).min(3),
          openings:       z.array(z.array(Pt).min(3)).optional(),
          heightOffsetMm: z.number().default(0),
        })).min(1),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async (params) => {
      try {
        const r = await revitRequest<CreateFloorsResult>("post", "/floors", undefined, params);
        const text = `✅ 建立樓板 **${r.created}** 片，挑空開口 **${r.openings}** 處`
          + `（型別：${r.floorTypeUsed}）` + skipBlock(r.skipped);
        return { content: [{ type: "text", text }], structuredContent: r as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_create_columns ─────────────────────────────────────────────────
  server.registerTool(
    "revit_create_columns",
    {
      title: "Create Round Columns (DirectShape)",
      description: `Create round columns as DirectShape solids on the Structural Columns category,
each running from baseMm to topMm.

These are geometry only — no structural analysis, no rebar, and schedules see
volume rather than a column family type. Use this when a quick massing-accurate
column grid matters more than family behaviour.

Args:
  - columns (array): [{ x, y, radiusMm? }, ...] — mm, project coordinates
  - diameterMm (number, optional): shared diameter when radiusMm is omitted
  - baseMm / topMm (number): bottom and top elevation in mm`,
      inputSchema: z.object({
        columns: z.array(z.object({
          x:        z.number(),
          y:        z.number(),
          radiusMm: z.number().positive().optional(),
        })).min(1).max(500),
        diameterMm: z.number().positive().optional(),
        baseMm:     z.number().default(0),
        topMm:      z.number(),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async (params) => {
      try {
        const r = await revitRequest<CreateColumnsResult>("post", "/columns", undefined, params);
        const text = `✅ 建立圓柱 **${r.created}** 支` + skipBlock(r.skipped);
        return { content: [{ type: "text", text }], structuredContent: r as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );
}
