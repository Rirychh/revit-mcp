// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";
import { ResponseFormat } from "../constants.js";
import type { ViewDto, ScreenshotResult } from "../types.js";

type Any = Record<string, unknown>;
const ok  = (d: unknown) => ({ content: [{ type: "text" as const, text: "```json\n" + JSON.stringify(d, null, 2) + "\n```" }], structuredContent: d as Any });
const err = (e: unknown) => ({ content: [{ type: "text" as const, text: `Error: ${e instanceof Error ? e.message : e}` }] });
const W = { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false };
const applyDesc = "false = 預檢（只回報將做什麼，不改模型）；確認後再用 true 執行";
const LONG = 10 * 60 * 1000;

export function registerViewTools(server: McpServer): void {

  // ── revit_list_views ─────────────────────────────────────────────────────
  server.registerTool(
    "revit_list_views",
    {
      title: "List Revit Views",
      description: `List views in the active Revit model.

Supported viewType values: FloorPlan, Section, Elevation, ThreeD, CeilingPlan, AreaPlan, DraftingView, Legend.
Leave viewType empty to list all views.

Args:
  - viewType (string, optional): Filter by view type
  - response_format: "markdown" or "json"`,
      inputSchema: z.object({
        viewType:        z.string().optional().describe("Filter by view type, e.g. FloorPlan / Section / ThreeD"),
        response_format: z.nativeEnum(ResponseFormat).default(ResponseFormat.MARKDOWN),
      }).strict(),
      annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
    },
    async ({ viewType, response_format }) => {
      try {
        const views = await revitRequest<ViewDto[]>("get", "/views", { viewType });

        let text: string;
        if (response_format === ResponseFormat.JSON) {
          text = JSON.stringify(views, null, 2);
        } else {
          text = [
            `# Revit Views${viewType ? ` — ${viewType}` : ""} (${views.length})`, "",
            ...views.map(v =>
              `- **[${v.id}] ${v.name}** — ${v.viewType}${v.levelName ? `  |  ${v.levelName}` : ""}  |  1:${v.scale}`)
          ].join("\n");
        }
        return { content: [{ type: "text", text }], structuredContent: { views } as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_create_view ────────────────────────────────────────────────────
  server.registerTool(
    "revit_create_view",
    {
      title: "Create Revit View",
      description: `Create a new view in the active Revit model.

Supported viewType: FloorPlan, Section, 3D.

Args:
  - viewType (string): Type of view to create — "FloorPlan", "Section", or "3D"
  - levelName (string, optional): Required for FloorPlan — the level to create the plan for
  - name (string, optional): Custom name for the new view`,
      inputSchema: z.object({
        viewType:  z.enum(["FloorPlan", "Section", "3D"]).describe("Type of view"),
        levelName: z.string().optional().describe("Level name (required for FloorPlan)"),
        name:      z.string().optional().describe("Custom view name"),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async (params) => {
      try {
        const view = await revitRequest<ViewDto>("post", "/views", undefined, params);
        const text = `✅ Created view [${view.id}]: **${view.name}** (${view.viewType})`;
        return { content: [{ type: "text", text }], structuredContent: view as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_screenshot_view ────────────────────────────────────────────────
  server.registerTool(
    "revit_screenshot_view",
    {
      title: "Screenshot Revit View",
      description: `Export a PNG screenshot of a specific Revit view to a temp file.

Use revit_list_views first to get the view ID.
The screenshot is saved at 150 DPI / 2048 px and the file path is returned.

Args:
  - viewId (number): The Revit View Element ID`,
      inputSchema: z.object({
        viewId: z.number().int().describe("Revit View Element ID"),
      }).strict(),
      annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
    },
    async ({ viewId }) => {
      try {
        const result = await revitRequest<ScreenshotResult>("get", `/views/${viewId}/screenshot`);
        const text = [
          `✅ Screenshot saved: **${result.viewName}**`,
          `- Path: \`${result.filePath}\``,
          `- Size: ${result.width} × ${result.height} px`,
        ].join("\n");
        return { content: [{ type: "text", text }], structuredContent: result as unknown as Record<string, unknown> };
      } catch (e) {
        return { content: [{ type: "text", text: `Error: ${e instanceof Error ? e.message : e}` }] };
      }
    }
  );

  // ── revit_duplicate_views ────────────────────────────────────────────────
  server.registerTool("revit_duplicate_views", {
    title: "Duplicate views",
    description: `複製視圖（含詳圖／不含詳圖／從屬）。
Args:
  - views: [{ viewId, name? }]；name 省略＝「<原名> 複製」
  - option: withDetailing（含詳圖，預設）｜plain（不含詳圖）｜dependent（從屬視圖）
  - apply: ${applyDesc}`,
    inputSchema: z.object({
      views: z.array(z.object({
        viewId: z.number().int().describe("視圖 ElementId"),
        name:   z.string().optional().describe("新視圖名稱；省略＝「<原名> 複製」"),
      }).strict()).min(1),
      option: z.enum(["withDetailing", "plain", "dependent"]).default("withDetailing"),
      apply:  z.boolean().default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/views/duplicate", undefined, a, LONG)); } catch (e) { return err(e); } });

  // ── revit_rotate_view_crop ───────────────────────────────────────────────
  server.registerTool("revit_rotate_view_crop", {
    title: "Rotate a plan view's crop box",
    description: `旋轉平面視圖的裁剪範圍（把斜的量體轉成畫面上水平垂直）。
angleDeg、alignWallId、alignRoomId 要給其中一個（angleDeg 優先；都沒給會直接擋下不送請求）。
Args:
  - viewId: 平面視圖 ElementId
  - angleDeg: 旋轉角度（度，逆時針為正）
  - alignWallId: 改用某道直牆的方向推算角度
  - alignRoomId: 改用某元件 bounding box 長邊推算角度
  - apply: ${applyDesc}`,
    inputSchema: z.object({
      viewId:      z.number().int().describe("平面視圖 ElementId"),
      angleDeg:    z.number().optional(),
      alignWallId: z.number().int().optional(),
      alignRoomId: z.number().int().optional(),
      apply:       z.boolean().default(false),
    }).strict(), annotations: W,
  }, async (a) => {
    if (a.angleDeg === undefined && a.alignWallId === undefined && a.alignRoomId === undefined)
      return err("請給 angleDeg、alignWallId 或 alignRoomId 其中之一。");
    try { return ok(await revitRequest<Any>("post", "/views/rotate-crop", undefined, a)); } catch (e) { return err(e); }
  });

  // ── revit_create_room_views ──────────────────────────────────────────────
  server.registerTool("revit_create_room_views", {
    title: "Create plan + elevation views per room",
    description: `為房間批次建立平面放大圖與立面圖（含裁剪範圍與視圖樣板）。
roomNumbers 若有給，nameContains 會被忽略。planName / elevName 支援 {room}、{name}、{level} 代換，立面名稱後面會再加 a/b/c/d。
Args:
  - roomNumbers: 房間編號清單；給了就忽略 nameContains
  - nameContains: 房間名稱關鍵字
  - scale: 視圖比例分母（1:n，預設 50）
  - marginMm: 裁剪範圍外擴（mm，預設 500）
  - elevationCount: 每房間要幾張立面（0–4，預設 4）
  - depthMm / topMm / bottomMm: 立面視深與上下緣高程（mm，預設 5000 / 3000 / -300）
  - planTemplate / elevTemplate: 視圖樣板名稱
  - planName / elevName: 命名樣式（預設 {room}_plan / {room}_elev）
  - alignElevToRoom: 立面方向對齊房間長邊
  - elevAngleDeg: 直接指定立面角度（度），覆寫 alignElevToRoom
  - dryRun: true = 預檢（預設）；false 才會寫入模型`,
    inputSchema: z.object({
      roomNumbers:     z.array(z.string()).optional(),
      nameContains:    z.string().optional(),
      scale:           z.number().int().positive().default(50),
      marginMm:        z.number().default(500),
      elevationCount:  z.number().int().min(0).max(4).default(4),
      depthMm:         z.number().default(5000),
      topMm:           z.number().default(3000),
      bottomMm:        z.number().default(-300),
      planTemplate:    z.string().optional(),
      elevTemplate:    z.string().optional(),
      planName:        z.string().default("{room}_plan"),
      elevName:        z.string().default("{room}_elev"),
      alignElevToRoom: z.boolean().default(false),
      elevAngleDeg:    z.number().optional(),
      dryRun:          z.boolean().default(true),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/rooms/views", undefined, a, LONG)); } catch (e) { return err(e); } });
}
