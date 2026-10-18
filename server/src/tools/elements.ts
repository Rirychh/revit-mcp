// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";
import { ResponseFormat, CHARACTER_LIMIT } from "../constants.js";
import type { ElementDto, ElementListResult, LevelDto, RoomDto, CopyElementsResult } from "../types.js";

interface CloseLoopResult {
  applied: boolean;
  lineCount: number;
  skippedNonLine: number[];
  toleranceMm: number;
  movedLines: number;
  changedLines: number;
  isClosed: boolean;
  components: number;
  openEnds: number;
  branchPoints: number;
  degenerate: number[];
  crossings: number[][];
  failed: string[];
}

type Any = Record<string, unknown>;
const ok  = (d: unknown) => ({ content: [{ type: "text" as const, text: "```json\n" + JSON.stringify(d, null, 2) + "\n```" }], structuredContent: d as Any });
const err = (e: unknown) => ({ content: [{ type: "text" as const, text: `Error: ${e instanceof Error ? e.message : e}` }] });
const W = { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false };
const R = { readOnlyHint: true,  destructiveHint: false, idempotentHint: true,  openWorldHint: false };
const applyDesc = "false = 預檢（只回報將做什麼，不改模型）；確認後再用 true 執行";

export function registerElementTools(server: McpServer): void {

  // ── revit_list_elements ──────────────────────────────────────────────────
  server.registerTool(
    "revit_list_elements",
    {
      title: "List Revit Elements",
      description: `List building elements in the active Revit model, with optional filters.

Supported categories: Walls, Doors, Windows, Floors, Roofs, Columns, Stairs, Rooms, Furniture,
Ceilings, PlumbingFixtures, SpecialityEquipment, MechanicalEquipment, ElectricalFixtures,
LightingFixtures, GenericModel, Casework, CurtainWallPanels, DuctTerminal (= AirTerminal:
supply/return/smoke-exhaust grilles), DuctAccessory, DuctFitting, Ducts, Sprinklers,
FireAlarmDevices, ElectricalEquipment, CommunicationDevices, DataDevices, SecurityDevices,
LightingDevices, PipeAccessory, PipeFitting, Pipes. Any raw Revit enum name ("OST_DuctTerminal")
also works. Leave category empty to list all element types.

Pass summary=true to get family/type counts for the matched set instead of individual elements —
the cheap way to find out what equipment families actually exist in a region before pulling
hundreds of rows.

Pass viewId to list only what is actually visible in that view — the usual first step before
revit_tag_elements or revit_create_dimension.

Returns element id, name, category, family/type, level, and placement point (mm, project
coordinates) plus host id and rotation for each result — so an existing layout can be read
back and reproduced elsewhere.

Narrow big models with minX/minY/maxX/maxY (a rectangle in mm) instead of paging through
thousands of results.

Args:
  - category (string, optional): Filter by category name (e.g. "Walls")
  - categories (string, optional): Comma-separated categories, e.g. "LightingFixtures,GenericModel"
  - familyName (string, optional): Comma-separated family-name whitelist
  - levelName (string, optional): Filter by level/floor name (e.g. "Level 1")
  - viewId (number, optional): Only elements visible in this view
  - minX/minY/maxX/maxY (number, optional): Region filter in mm, all four required to apply
  - limit (number): Max results per page, default 50
  - offset (number): Pagination offset, default 0
  - response_format: "markdown" or "json"`,
      inputSchema: z.object({
        category:        z.string().optional().describe("Element category, e.g. Walls / Doors / PlumbingFixtures"),
        categories:      z.string().optional().describe("Comma-separated categories"),
        familyName:      z.string().optional().describe("Comma-separated family names to keep"),
        levelName:       z.string().optional().describe("Level name to filter by, e.g. 'Level 1'"),
        viewId:          z.number().int().optional().describe("Only elements visible in this view"),
        minX:            z.number().optional().describe("Region filter min X in mm"),
        minY:            z.number().optional().describe("Region filter min Y in mm"),
        maxX:            z.number().optional().describe("Region filter max X in mm"),
        maxY:            z.number().optional().describe("Region filter max Y in mm"),
        summary:         z.boolean().default(false).describe("Return family/type counts instead of elements"),
        limit:           z.number().int().min(1).max(200).default(50).describe("Max results"),
        offset:          z.number().int().min(0).default(0).describe("Pagination offset"),
        response_format: z.nativeEnum(ResponseFormat).default(ResponseFormat.MARKDOWN),
      }).strict(),
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ category, categories, familyName, levelName, viewId,
             minX, minY, maxX, maxY, summary, limit, offset, response_format }) => {
      try {
        const result = await revitRequest<ElementListResult>(
          "get", "/elements",
          { category, categories, familyName, levelName, viewId,
            minX, minY, maxX, maxY, summary: summary ? "true" : undefined, limit, offset }
        );

        let text: string;
        if (response_format === ResponseFormat.JSON) {
          text = JSON.stringify(result, null, 2);
        } else {
          const lines = [
            `# Revit Elements${category ? ` — ${category}` : ""}`,
            `**Total:** ${result.total}  |  Showing ${result.count} (offset ${result.offset})`,
            result.hasMore ? `\n> Page ${Math.floor(offset/limit)+1} — use offset=${offset+limit} for next page` : "",
            "",
          ];
          if (result.groups) {
            lines.push("| 族群 | 類型 | 類別 | 數量 | 面掛 |", "|---|---|---|---:|---:|");
            for (const g of result.groups)
              lines.push(`| ${g.familyName} | ${g.typeName} | ${g.category} | ${g.count} | ${g.faceHosted} |`);
            text = lines.join("\n");
            if (text.length > CHARACTER_LIMIT) text = text.slice(0, CHARACTER_LIMIT) + "\n\n> 回應已截斷。";
            return { content: [{ type: "text", text }], structuredContent: result as unknown as Record<string, unknown> };
          }
          for (const e of result.elements) {
            lines.push(`## [${e.id}] ${e.name}`);
            if (e.category)   lines.push(`- Category: ${e.category}`);
            if (e.familyName) lines.push(`- Family: ${e.familyName} / ${e.typeName}`);
            if (e.levelName)  lines.push(`- Level: ${e.levelName}`);
            if (e.location)   lines.push(`- Location (mm): ${e.location.x.toFixed(1)}, ${e.location.y.toFixed(1)}, ${e.location.z.toFixed(1)}`);
            if (e.hostId)     lines.push(`- Host: ${e.hostId}`);
            lines.push("");
          }
          text = lines.join("\n");
        }

        if (text.length > CHARACTER_LIMIT)
          text = text.slice(0, CHARACTER_LIMIT) + "\n\n> Response truncated. Use offset/limit to page.";

        return { content: [{ type: "text", text }], structuredContent: result as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_get_element ────────────────────────────────────────────────────
  server.registerTool(
    "revit_get_element",
    {
      title: "Get Revit Element Details",
      description: `Get full details and all parameters of a specific Revit element by its integer Element ID.

Returns all accessible parameters of the element (e.g. dimensions, materials, mark).

Args:
  - elementId (number): The Revit Element ID (integer)
  - response_format: "markdown" or "json"`,
      inputSchema: z.object({
        elementId:       z.number().int().describe("Revit Element ID"),
        response_format: z.nativeEnum(ResponseFormat).default(ResponseFormat.MARKDOWN),
      }).strict(),
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ elementId, response_format }) => {
      try {
        const elem = await revitRequest<ElementDto>("get", `/elements/${elementId}`);

        let text: string;
        if (response_format === ResponseFormat.JSON) {
          text = JSON.stringify(elem, null, 2);
        } else {
          const lines = [
            `# Element [${elem.id}]: ${elem.name}`,
            `- **Category:** ${elem.category}`,
            `- **Family:** ${elem.familyName} / ${elem.typeName}`,
            `- **Level:** ${elem.levelName || "—"}`,
            "", "## Parameters",
          ];
          for (const [k, v] of Object.entries(elem.parameters))
            lines.push(`- **${k}:** ${v}`);
          text = lines.join("\n");
        }

        return { content: [{ type: "text", text }], structuredContent: elem as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_create_element ─────────────────────────────────────────────────
  server.registerTool(
    "revit_create_element",
    {
      title: "Create Revit Element",
      description: `Place a new family instance into the active Revit model at the specified coordinates.

Coordinates are in millimetres (mm), relative to the Revit project origin.
The family and type must already exist in the project.

Args:
  - familyName (string): The Revit family name (e.g. "Single-Flush")
  - typeName (string): The type/size name within the family (e.g. "36\" x 84\"")
  - levelName (string, optional): Level to associate the element with
  - x, y, z (number): Placement coordinates in mm
  - parameters (object, optional): Key-value pairs of parameters to set on the new element`,
      inputSchema: z.object({
        familyName:  z.string().min(1).describe("Revit family name"),
        typeName:    z.string().min(1).describe("Revit type name within the family"),
        levelName:   z.string().optional().describe("Level name, e.g. 'Level 1'"),
        x:           z.number().default(0).describe("X coordinate in mm"),
        y:           z.number().default(0).describe("Y coordinate in mm"),
        z:           z.number().default(0).describe("Z coordinate in mm"),
        parameters:  z.record(z.string()).optional().describe("Parameter name → value pairs"),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async (params) => {
      try {
        const elem = await revitRequest<ElementDto>("post", "/elements", undefined, params);
        const text = `✅ Created element [${elem.id}]: **${elem.name}**\n- Family: ${elem.familyName} / ${elem.typeName}\n- Level: ${elem.levelName || "—"}`;
        return { content: [{ type: "text", text }], structuredContent: elem as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_modify_element ─────────────────────────────────────────────────
  server.registerTool(
    "revit_modify_element",
    {
      title: "Modify Revit Element Parameters",
      description: `Update one or more parameters of an existing Revit element.

Use revit_get_element first to see available parameter names.

Args:
  - elementId (number): The Element ID to modify
  - parameters (object): Parameter name → new value (as string) pairs`,
      inputSchema: z.object({
        elementId:  z.number().int().describe("Revit Element ID to modify"),
        parameters: z.record(z.string()).describe("Parameter name → value to set"),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async ({ elementId, parameters }) => {
      try {
        const elem = await revitRequest<ElementDto>("put", `/elements/${elementId}`, undefined, { parameters });
        const text = `✅ Modified element [${elem.id}]: **${elem.name}**\n${Object.entries(parameters).map(([k,v]) => `- ${k}: ${v}`).join("\n")}`;
        return { content: [{ type: "text", text }], structuredContent: elem as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_list_levels ────────────────────────────────────────────────────
  server.registerTool(
    "revit_list_levels",
    {
      title: "List Revit Levels",
      description: `List all levels (floors) defined in the active Revit model with their elevation in mm.`,
      inputSchema: z.object({
        response_format: z.nativeEnum(ResponseFormat).default(ResponseFormat.MARKDOWN),
      }).strict(),
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ response_format }) => {
      try {
        const levels = await revitRequest<LevelDto[]>("get", "/levels");
        let text: string;
        if (response_format === ResponseFormat.JSON) {
          text = JSON.stringify(levels, null, 2);
        } else {
          text = ["# Revit Levels", "",
            ...levels.map(l => `- **${l.name}** — elevation: ${l.elevation.toFixed(0)} mm (id: ${l.id})`)
          ].join("\n");
        }
        return { content: [{ type: "text", text }], structuredContent: { levels } as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_list_rooms ─────────────────────────────────────────────────────
  server.registerTool(
    "revit_list_rooms",
    {
      title: "List Revit Rooms",
      description: `List all rooms in the active Revit model with their area (m²) and level.

Args:
  - levelName (string, optional): Filter rooms by level name`,
      inputSchema: z.object({
        levelName:       z.string().optional().describe("Filter by level name"),
        response_format: z.nativeEnum(ResponseFormat).default(ResponseFormat.MARKDOWN),
      }).strict(),
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ levelName, response_format }) => {
      try {
        const rooms = await revitRequest<RoomDto[]>("get", "/rooms", { levelName });
        let text: string;
        if (response_format === ResponseFormat.JSON) {
          text = JSON.stringify(rooms, null, 2);
        } else {
          text = [
            `# Revit Rooms (${rooms.length})`, "",
            ...rooms.map(r =>
              `- **${r.number} ${r.name}** — ${r.area.toFixed(2)} m²  |  ${r.levelName}  (id: ${r.id})`)
          ].join("\n");
        }
        return { content: [{ type: "text", text }], structuredContent: { rooms } as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_close_lines ────────────────────────────────────────────────────
  server.registerTool(
    "revit_close_lines",
    {
      title: "Close Detail/Model Lines into a Loop",
      description: `Adjust a set of detail lines / model lines so their endpoints snap together into a closed, non-self-intersecting loop (for drawing/presentation).

Snaps endpoints within a tolerance to close small gaps, then reports whether the result is a single closed loop and lists any self-intersections. Self-intersections are only reported, never auto-resolved (to avoid changing the intended shape).

Run with apply=false first to preview; apply=true performs the edit inside one Revit transaction.

Args:
  - elementIds (number[]): Element IDs of the straight lines to process (>= 2)
  - toleranceMm (number): Endpoint snap tolerance in mm (default 3)
  - apply (boolean): false = preview only (default), true = perform the edit`,
      inputSchema: z.object({
        elementIds:  z.array(z.number().int()).min(2).describe("Element IDs of straight lines"),
        toleranceMm: z.number().positive().default(3).describe("Endpoint snap tolerance in mm"),
        apply:       z.boolean().default(false).describe("false = preview, true = apply"),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async ({ elementIds, toleranceMm, apply }) => {
      try {
        const r = await revitRequest<CloseLoopResult>(
          "post", "/lines/close-loop", undefined,
          { elementIds, toleranceMm, apply }
        );
        const lines = [
          `# 整理封閉線 — ${r.applied ? "已套用" : "預覽"}`,
          `- 直線段：${r.lineCount} 條${r.skippedNonLine.length ? `（略過非直線 ${r.skippedNonLine.length}）` : ""}`,
          `- 吸附容差：${r.toleranceMm} mm`,
          `- ${r.applied ? "已調整" : "將調整"}：${r.applied ? r.changedLines : r.movedLines} 條`,
          `- 封閉：${r.isClosed ? "單一封閉迴圈 ✔" : "尚未完全封閉 ✘"}（連通分量 ${r.components}、開放端 ${r.openEnds}、分岔 ${r.branchPoints}）`,
          `- 自相交：${r.crossings.length ? `${r.crossings.length} 組 ✘` : "無 ✔"}`,
        ];
        if (r.crossings.length)
          lines.push(`- 交叉線對：${r.crossings.map(c => `${c[0]}×${c[1]}`).join(", ")}`);
        if (r.degenerate.length)
          lines.push(`- 退化略過：${r.degenerate.join(", ")}`);
        if (r.failed.length)
          lines.push(`- 失敗：${r.failed.join("; ")}`);
        if (!r.applied)
          lines.push(`\n> 這是預覽。確認無誤後加 apply=true 才會實際調整端點。`);
        return { content: [{ type: "text", text: lines.join("\n") }], structuredContent: r as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_copy_elements ──────────────────────────────────────────────────
  server.registerTool(
    "revit_copy_elements",
    {
      title: "Copy Ceiling Equipment Between Levels",
      description: `Reproduce an existing equipment layout (lights, sprinklers, diffusers, detectors)
from one level onto another — the usual way to bring a coordinated ceiling plan onto a floor
whose reflected ceiling plan is still empty.

Face-hosted families cannot simply be translated: they would lose their host. This tool places
them fresh on the target level's ceiling bottom face at the same XY, then carries the rotation
and the project/shared parameters over. Level-based elements are translated instead.

Select the source either by explicit sourceIds, or by sourceLevelName + categories (+ optional
familyNames and a bbox rectangle in mm). Run with dryRun first: it reports the source count
grouped by family/type, how many are face-hosted, the source bounding box, and how many
ceilings exist on the target level — without touching the model.

Args:
  - sourceIds (number[], optional): explicit source elements; overrides the filters below
  - sourceLevelName (string), sourceViewId (number, optional)
  - categories (string[], optional): e.g. ["LightingFixtures","GenericModel"]
  - familyNames (string[], optional): family-name whitelist
  - bbox ({minX,minY,maxX,maxY} in mm, optional): source region
  - targetLevelName (string): level to place onto
  - dx, dy (number): horizontal offset in mm, default 0 (same tower = 0)
  - rehostToCeiling (boolean): re-host face-based families, default true
  - dryRun (boolean): report only, default true`,
      inputSchema: z.object({
        sourceIds:       z.array(z.number().int()).optional(),
        sourceLevelName: z.string().optional(),
        sourceViewId:    z.number().int().optional(),
        categories:      z.array(z.string()).optional(),
        familyNames:     z.array(z.string()).optional(),
        bbox:            z.object({
                           minX: z.number(), minY: z.number(),
                           maxX: z.number(), maxY: z.number(),
                         }).optional(),
        targetLevelName: z.string().describe("Level to place the copies on"),
        dx:              z.number().default(0).describe("Horizontal offset X in mm"),
        dy:              z.number().default(0).describe("Horizontal offset Y in mm"),
        rehostToCeiling: z.boolean().default(true),
        dryRun:          z.boolean().default(true),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async (args) => {
      try {
        const r = await revitRequest<CopyElementsResult>("post", "/elements/copy", undefined, args);
        const lines = [
          `# 複製天花設備 — ${r.dryRun ? "預覽" : "已執行"}`,
          `- 來源：${r.sourceLevel || "(未指定樓層)"}，${r.sourceCount} 個元件`,
          `- 目標：${r.targetLevel}，該層天花 ${r.targetCeilings} 片`,
        ];
        if (r.sourceBBox)
          lines.push(`- 來源範圍(mm)：X ${r.sourceBBox.minX.toFixed(0)}~${r.sourceBBox.maxX.toFixed(0)}, Y ${r.sourceBBox.minY.toFixed(0)}~${r.sourceBBox.maxY.toFixed(0)}`);
        if (!r.dryRun)
          lines.push(`- 建立 ${r.createdCount} 個，略過 ${r.skippedCount} 個`);
        if (r.groups.length) {
          lines.push("", "| 族群 | 類型 | 類別 | 數量 | 面掛 |", "|---|---|---|---:|---:|");
          for (const g of r.groups)
            lines.push(`| ${g.familyName} | ${g.typeName} | ${g.category} | ${g.count} | ${g.faceHosted} |`);
        }
        if (r.warnings.length) {
          lines.push("", `## 警告（${r.warnings.length}）`);
          for (const w of r.warnings.slice(0, 20)) lines.push(`- ${w}`);
          if (r.warnings.length > 20) lines.push(`- …其餘 ${r.warnings.length - 20} 筆略`);
        }
        if (r.dryRun) lines.push("", "> 這是預覽，模型未改動。確認後加 dryRun=false 才會實際放置。");
        let text = lines.join("\n");
        if (text.length > CHARACTER_LIMIT) text = text.slice(0, CHARACTER_LIMIT) + "\n\n> 回應已截斷。";
        return { content: [{ type: "text", text }], structuredContent: r as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_delete_elements ────────────────────────────────────────────────
  // 對應 add-in 既有路由 POST /drafting/delete（DraftingHandler.DeleteElements），
  // add-in 端不需重新編譯。單一 Transaction，逐一刪除，失敗的列在 failed。
  server.registerTool(
    "revit_delete_elements",
    {
      title: "Delete Revit Elements",
      description: `依 Element ID 刪除作用中模型裡的元素（任何類別：模型構件、註解、製圖線等）。

先用 revit_list_elements 取得 id，再傳進來。
刪除宿主會連帶刪除其上的依附元素（門窗、標籤），回報的 deleted 只計傳入的 id。

Args:
  - elementIds: 要刪的 id 清單（1–2000 筆）
  - apply: false = 預檢（只列出比對到的元素，不改模型）；確認後再用 true 執行`,
      inputSchema: z.object({
        elementIds: z.array(z.number().int()).min(1).max(2000).describe("Element IDs to delete"),
        apply:      z.boolean().default(false).describe("false = preview only"),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
    },
    async ({ elementIds, apply }) => {
      try {
        const r = await revitRequest<{ applied: boolean; matched: string[]; failed: string[]; deleted: number }>(
          "post", "/drafting/delete", undefined, { elementIds, apply });
        const matched = r.matched ?? [];
        const failed  = r.failed ?? [];
        const lines = [
          `# 刪除元素 — ${r.applied ? "已執行" : "預覽"}`,
          `- 傳入 ${elementIds.length} 筆，比對到 ${matched.length} 筆` + (r.applied ? `，已刪除 ${r.deleted} 筆` : ""),
        ];
        if (matched.length) {
          lines.push("", "## 比對到的元素");
          for (const m of matched.slice(0, 50)) lines.push(`- ${m}`);
          if (matched.length > 50) lines.push(`- …其餘 ${matched.length - 50} 筆略`);
        }
        if (failed.length) {
          lines.push("", `## 失敗（${failed.length}）`);
          for (const f of failed.slice(0, 30)) lines.push(`- ${f}`);
          if (failed.length > 30) lines.push(`- …其餘 ${failed.length - 30} 筆略`);
        }
        if (!r.applied) lines.push("", "> 這是預覽，模型未改動。確認後加 apply=true 才會實際刪除。");
        let text = lines.join("\n");
        if (text.length > CHARACTER_LIMIT) text = text.slice(0, CHARACTER_LIMIT) + "\n\n> 回應已截斷。";
        return { content: [{ type: "text", text }], structuredContent: r as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_rotate_elements ────────────────────────────────────────────────
  server.registerTool("revit_rotate_elements", {
    title: "Rotate elements",
    description: `繞任意軸旋轉指定元件。
Args:
  - ids: 要旋轉的元件 ElementId（至少一筆）
  - angleDeg: 旋轉角度（度，預設 90）
  - axis: auto（取 bounding box 最薄的軸，預設）｜x｜y｜z
  - origin: location（元件位置點，預設）｜bboxcenter
  - apply: ${applyDesc}`,
    inputSchema: z.object({
      ids:      z.array(z.number().int()).min(1).describe("要旋轉的元件 ElementId"),
      angleDeg: z.number().default(90).describe("旋轉角度（度）"),
      axis:     z.enum(["auto", "x", "y", "z"]).default("auto"),
      origin:   z.enum(["location", "bboxcenter"]).default("location"),
      apply:    z.boolean().default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/elements/rotate", undefined, a)); } catch (e) { return err(e); } });

  // ── revit_align_elements_upright ─────────────────────────────────────────
  server.registerTool("revit_align_elements_upright", {
    title: "Align wall-hosted family instances upright",
    description: `把工作平面基礎的牆面族實例統一轉正（指定族的某個軸對齊世界上方）。
familyName 與 ids 給其一（都給時 ids 優先）；兩個都沒給會直接擋下不送請求。
Args:
  - familyName: 族名
  - ids: 元件 ElementId
  - upAxis: 族的哪個軸要朝世界上方（X｜Y，預設 Y）
  - includeGrouped: 一併處理群組內的實例（預設 false）
  - tolDeg: 容許誤差（度），差距小於此值就跳過（預設 1）
  - apply: ${applyDesc}`,
    inputSchema: z.object({
      familyName:     z.string().optional().describe("族名；與 ids 二擇一"),
      ids:            z.array(z.number().int()).optional().describe("元件 ElementId；與 familyName 二擇一（優先）"),
      upAxis:         z.enum(["X", "Y"]).default("Y"),
      apply:          z.boolean().default(false),
      includeGrouped: z.boolean().default(false),
      tolDeg:         z.number().default(1),
    }).strict(), annotations: W,
  }, async (a) => {
    if (!a.familyName && !(a.ids && a.ids.length))
      return err("請提供 familyName 或 ids 其中之一（handler 兩者皆無時會丟錯）。");
    try { return ok(await revitRequest<Any>("post", "/elements/align-upright", undefined, a)); } catch (e) { return err(e); }
  });

  // ── revit_find_element_constraints ───────────────────────────────────────
  server.registerTool("revit_find_element_constraints", {
    title: "Find constraints referencing elements",
    description: `唯讀：找出哪些尺寸標註／對齊約束參考到指定元件。元件搬不動、改不了時用來查兇手。
Args:
  - ids: 要查約束的元件 ElementId`,
    inputSchema: z.object({
      ids: z.array(z.number().int()).min(1).describe("要查約束的元件 ElementId"),
    }).strict(), annotations: R,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/elements/constraints", undefined, a)); } catch (e) { return err(e); } });

  // ── revit_ungroup_groups ─────────────────────────────────────────────────
  server.registerTool("revit_ungroup_groups", {
    title: "Ungroup group instances",
    description: `解除指定的群組實例（只動被指名的 groupIds）。
⚠ apply=true 會直接寫入模型，解除群組後除了在 Revit 視窗按 Ctrl+Z 沒有其他還原方式 —— 執行前請先用 apply=false 預檢並向使用者確認。
Args:
  - groupIds: 群組實例 ElementId
  - apply: ${applyDesc}`,
    inputSchema: z.object({
      groupIds: z.array(z.number().int()).min(1).describe("群組實例 ElementId"),
      apply:    z.boolean().default(false),
    }).strict(), annotations: { ...W, destructiveHint: true },
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/groups/ungroup", undefined, a)); } catch (e) { return err(e); } });

  // ── revit_snap_elements_to_grid ──────────────────────────────────────────
  server.registerTool("revit_snap_elements_to_grid", {
    title: "Snap elements to a ceiling module grid",
    description: `把既有元件的平面位置吸附到天花模矩格線（只動 XY，不動高程）。
elementIds 若有給，levelName / categories / familyNames / hostIds / bbox 一律被忽略。
bbox 要給就得四個邊界都給（minX/minY/maxX/maxY），只給一部分的話範圍過濾形同沒設，會選到整個模型。
Args:
  - elementIds: 指定元件；給了就忽略其他過濾條件
  - levelName / categories / familyNames / hostIds: 過濾條件
  - bbox: XY 範圍過濾（mm，專案座標），四個邊界都必填
  - moduleMm: 格線模矩（mm，預設 600）
  - originX / originY: 格線原點（mm，預設 0）；未給時 handler 會用 sampleCount 取樣推估
  - target: center（格子中心，預設）｜line（格線交點）
  - sampleCount: 推估格線原點時的取樣數（預設 8）
  - dryRun: true = 預檢（預設）；false 才會寫入模型`,
    inputSchema: z.object({
      elementIds:  z.array(z.number().int()).optional(),
      levelName:   z.string().optional(),
      categories:  z.array(z.string()).optional(),
      familyNames: z.array(z.string()).optional(),
      hostIds:     z.array(z.number().int()).optional(),
      bbox: z.object({
        minX: z.number(), minY: z.number(), maxX: z.number(), maxY: z.number(),
      }).strict().optional().describe("XY 範圍過濾（mm）；四個邊界都要給"),
      moduleMm:    z.number().positive().default(600),
      originX:     z.number().optional(),
      originY:     z.number().optional(),
      target:      z.enum(["center", "line"]).default("center"),
      sampleCount: z.number().int().min(1).max(200).default(8),
      dryRun:      z.boolean().default(true),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/elements/snap", undefined, a)); } catch (e) { return err(e); } });
}
