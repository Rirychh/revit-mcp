// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";

type Any = Record<string, unknown>;
const ok  = (d: unknown) => ({ content: [{ type: "text" as const, text: "```json\n" + JSON.stringify(d, null, 2) + "\n```" }], structuredContent: d as Any });
const err = (e: unknown) => ({ content: [{ type: "text" as const, text: `Error: ${e instanceof Error ? e.message : e}` }] });
const W = { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false };
const R = { readOnlyHint: true,  destructiveHint: false, idempotentHint: true,  openWorldHint: false };
const applyDesc = "false = 預檢（只回報將做什麼，不改模型）；確認後再用 true 執行";

export function registerSheetTools(server: McpServer): void {

  server.registerTool("revit_list_sheets", {
    title: "List sheets / title blocks",
    description: "列出所有圖紙（id、圖號、圖名）、可用圖框類型，以及目前的開啟畫面 viewId。",
    inputSchema: z.object({}).strict(), annotations: R,
  }, async () => { try { return ok(await revitRequest<Any>("get", "/sheets")); } catch (e) { return err(e); } });

  server.registerTool("revit_sheet_layout", {
    title: "Sheet layout (title block size + viewports)",
    description: `列出圖紙的圖框尺寸與已放的視埠（視圖名、中心、寬高，皆為圖紙 mm）。
Args:
  - numberContains: 圖號或圖名包含此字串才列（空白＝全部）`,
    inputSchema: z.object({ numberContains: z.string().optional() }).strict(), annotations: R,
  }, async (a) => { try { return ok(await revitRequest<Any>("get", "/sheets/layout", a.numberContains ? { number: a.numberContains } : undefined)); } catch (e) { return err(e); } });

  server.registerTool("revit_place_views", {
    title: "Place views on an existing sheet",
    description: `把視圖放進「既有」圖紙（新增視埠）。一次一張圖紙，可放多個視圖。
已在其他圖紙上的視圖（圖例除外）會略過並列在 warnings。
Args:
  - sheetNumber: 目標圖號
  - items: [{viewId 或 viewName, x, y}] 視埠中心，圖紙 mm（原點在圖框左下）
  - apply: ${applyDesc}`,
    inputSchema: z.object({
      sheetNumber: z.string(),
      items: z.array(z.object({
        viewId: z.number().int().optional(), viewName: z.string().optional(),
        x: z.number(), y: z.number(),
      })).min(1),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, async (a) => {
    try {
      const body = {
        sheetNumber: a.sheetNumber,
        items: a.items.map(i => ({ viewId: i.viewId ?? 0, viewName: i.viewName, xMm: i.x, yMm: i.y })),
        dryRun: !a.apply,
      };
      return ok(await revitRequest<Any>("post", "/sheets/place", undefined, body));
    } catch (e) { return err(e); }
  });

  server.registerTool("revit_create_sheet", {
    title: "Create sheet (+ place views, set starting view)",
    description: `建立新圖紙，可選擇放入視圖並設為開啟畫面。
Args:
  - number, name: 圖號、圖名（圖號重複會拒絕）
  - titleBlock: "族群:類型" 或類型名（空白＝無圖框；用 revit_list_sheets 查）
  - views: [{viewId, x, y}] 視埠中心，圖紙 mm
  - setStartingView: 設為開啟模型時的起始畫面
  - apply: ${applyDesc}`,
    inputSchema: z.object({
      number: z.string(), name: z.string(),
      titleBlock: z.string().optional(),
      views: z.array(z.object({ viewId: z.number().int(), x: z.number().optional(), y: z.number().optional() })).optional(),
      setStartingView: z.boolean().default(false),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/sheets/create", undefined, a)); } catch (e) { return err(e); } });

  server.registerTool("revit_set_starting_view", {
    title: "Set starting view",
    description: `設定模型開啟時的起始畫面。給 sheetNumber 或 viewId 其一。apply: ${applyDesc}`,
    inputSchema: z.object({
      sheetNumber: z.string().optional(), viewId: z.number().int().optional(),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/sheets/starting-view", undefined, a)); } catch (e) { return err(e); } });

  server.registerTool("revit_export_sheets_pdf", {
    title: "Batch export sheets to PDF",
    description: `依圖號批次輸出 PDF，每張一檔，檔名「圖號{separator}圖名.pdf」。
Args:
  - sheetNumbers: 圖號清單
  - folder: 輸出資料夾（可為網路路徑）
  - separator: 預設 "_"
  - overwrite: 已存在是否覆寫（預設 false＝略過）
  - apply: ${applyDesc}
使用 Revit 內建 PDF 匯出（2022+），套用各圖紙自己的尺寸。張數多時可能需要數分鐘。`,
    inputSchema: z.object({
      sheetNumbers: z.array(z.string()).min(1), folder: z.string(),
      separator: z.string().default("_"), overwrite: z.boolean().default(false),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/sheets/export-pdf", undefined, a, 30 * 60 * 1000)); } catch (e) { return err(e); } });

  server.registerTool("revit_write_revision_notes", {
    title: "Write revision notes into title blocks",
    description: `把修正說明寫進各圖紙圖框的「修訂內容」欄（新增 TextNote，自動在、，處換行）。
修訂欄已有文字的圖紙會略過。內建座標：範例圖框(執照圖)/(顧問) x=119 y=42.5 dy=9.8 每行18字 最多3行；
其他圖框用 titleBlocks 補（圖紙 mm，相對圖框原點）。
Args:
  - items: [{sheetNumber, text}]
  - textTypeName: 預設 "BAF 3mm 內文"
  - titleBlocks: {類型名: {x, y, dy, chars, maxLines}}
  - apply: ${applyDesc}`,
    inputSchema: z.object({
      items: z.array(z.object({ sheetNumber: z.string(), text: z.string() })).min(1),
      textTypeName: z.string().optional(),
      titleBlocks: z.record(z.object({ x: z.number(), y: z.number(), dy: z.number(), chars: z.number().int(), maxLines: z.number().int() })).optional(),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/sheets/revision-notes", undefined, a)); } catch (e) { return err(e); } });

  server.registerTool("revit_list_revisions", {
    title: "List revisions",
    description: "列出專案所有修訂（id、順序、描述、日期、雲形線數量）。",
    inputSchema: z.object({}).strict(), annotations: R,
  }, async () => { try { return ok(await revitRequest<Any>("get", "/revisions")); } catch (e) { return err(e); } });

  server.registerTool("revit_annotate_revision_clouds", {
    title: "Annotate revision clouds with change notes",
    description: `在指定修訂的每個雲形線右上角加文字＋直線引線，文字依雲形線所在圖紙的圖號取 notes。
雲形線「備註」已有內容者視為已標註而略過；標註後會把說明寫入備註。
Args:
  - revisionId: 用 revit_list_revisions 查
  - notes: {圖號: 說明}
  - textTypeName: 預設 "BAF 3mm 內文(紅)"
  - offsetMm: 文字離雲形線角點的圖面距離，預設 4
  - apply: ${applyDesc}`,
    inputSchema: z.object({
      revisionId: z.number().int(), notes: z.record(z.string()),
      textTypeName: z.string().optional(), offsetMm: z.number().default(4),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/revisions/cloud-notes", undefined, a, 10 * 60 * 1000)); } catch (e) { return err(e); } });

  server.registerTool("revit_batch_create_sheets", {
    title: "Batch create empty sheets",
    description: `批次建立空圖紙（自動沿用標題欄類型，已存在的圖號跳過）。
與 revit_create_sheet 的差別：這支只建空圖紙、可一次很多張，不放視圖、不設起始畫面。
Args:
  - sheets: [{number, name}]
  - titleblockFrom: 複製哪張圖紙（圖號）的標題欄類型；省略則取任一種
  - dryRun: true = 預檢（預設）；false 才會寫入模型`,
    inputSchema: z.object({
      sheets: z.array(z.object({
        number: z.string().min(1).describe("圖號"),
        name:   z.string().min(1).describe("圖名"),
      }).strict()).min(1),
      titleblockFrom: z.string().optional(),
      dryRun: z.boolean().default(true),
    }).strict(), annotations: W,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/sheets/batch-create", undefined, a, 10 * 60 * 1000)); } catch (e) { return err(e); } });
}
