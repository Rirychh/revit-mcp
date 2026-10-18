// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";

type Any = Record<string, unknown>;
const ok  = (d: unknown) => ({ content: [{ type: "text" as const, text: "```json\n" + JSON.stringify(d, null, 2) + "\n```" }], structuredContent: d as Any });
const err = (e: unknown) => ({ content: [{ type: "text" as const, text: `Error: ${e instanceof Error ? e.message : e}` }] });
const R = { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false };
const LONG = 10 * 60 * 1000;

export function registerQcTools(server: McpServer): void {

  server.registerTool("revit_model_health", {
    title: "Model health check",
    description: `作用中模型的健檢總覽（唯讀）：檔案大小、元素數、警告總數與前 15 類、可載入／內建族、匯入與連結 CAD、Revit 連結載入狀態、群組與未使用群組類型、未上圖紙的視圖與明細表、未使用視圖樣板、未放置／未封閉房間、設計選項、工作集、材料與填充樣式數、可清除元素數。
flags 依 high/medium/low 排序並附建議處理方式。
Args:
  - purgeable: 是否計算可清除元素數（大模型會多花一點時間，預設 true）`,
    inputSchema: z.object({ purgeable: z.boolean().default(true) }).strict(), annotations: R,
  }, async (a) => { try { return ok(await revitRequest<Any>("get", "/qc/health", a, undefined, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_performance_report", {
    title: "Model performance report",
    description: `找出拖慢模型的主因（唯讀）：各品類實例數、各族的實例數 × 幾何量（面數＋網格三角形，內建族為各實例總和）排序、單一類型最複雜的族、每個 CAD 的曲線／圖塊量、2D 雜物（詳圖線、模型線、填充區域、文字、尺寸、DirectShape、修訂雲形）、Fine 細緻度視圖數。
geometryLoad 是相對指標，用來排序找主因，不是記憶體數字。sampleElementId 可接 revit_select_elements 檢視。
Args:
  - topN: 每張排行的筆數（預設 20）
  - maxFamilies: 依實例數取前幾個族做幾何取樣（預設 400）
  - sampleGeometry: false = 只算數量不讀幾何（最快）`,
    inputSchema: z.object({
      topN: z.number().int().min(1).max(200).default(20),
      maxFamilies: z.number().int().min(1).max(5000).default(400),
      sampleGeometry: z.boolean().default(true),
    }).strict(), annotations: R,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/qc/performance", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_list_warnings", {
    title: "List warnings",
    description: `列出作用中模型的警告（唯讀）：依描述分組的統計（含 autoFix 標記可自動修正的類型），以及逐筆明細（相關元件 id、品類、族、類型、樓層、是否在群組內）。
可輸出 CSV（UTF-8 BOM，Excel 直接開）。要在 Revit 中檢視某筆警告的元件，用 revit_select_elements。
Args:
  - contains: 描述關鍵字篩選
  - kind: 只看某種可自動修正類型：duplicate-instances｜room-separation-on-wall｜room-tag-outside
  - limit / offset: 明細分頁（預設 50 / 0）
  - csvPath: 給完整路徑則把「篩選後全部明細」寫成 CSV`,
    inputSchema: z.object({
      contains: z.string().optional(),
      kind: z.enum(["duplicate-instances", "room-separation-on-wall", "room-tag-outside"]).optional(),
      limit: z.number().int().min(0).max(2000).default(50),
      offset: z.number().int().min(0).default(0),
      csvPath: z.string().optional(),
    }).strict(), annotations: R,
  }, async (a) => { try { return ok(await revitRequest<Any>("get", "/warnings", a, undefined, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_resolve_warnings", {
    title: "Auto-fix safe warnings",
    description: `自動修正可安全處理的警告類型（依 Revit 內建警告 id 判斷，不受介面語言影響）：
  - duplicate-instances：同位置重複實例 → 保留 id 最小者（或帶有門窗等寄宿元件者），刪除其餘；有寄宿元件或已釘選的不刪
  - room-separation-on-wall：房間分隔線與房間邊界牆重疊 → 線完全落在牆厚內且牆為房間邊界時才刪除分隔線
  - room-tag-outside：房間標籤在房間外 → 移到房間點（房間點本身在房間外則跳過）
群組內元素一律跳過，不解除群組。每個修正獨立交易，失敗只回復該筆、不彈對話框。
Args:
  - kinds: 要處理的類型（省略 = 全部三種）
  - apply: false = 預檢（列出將刪除／移動的元件，不改模型）；確認後再用 true 執行
  - maxFixes: 單次最多執行幾筆（預設 500）`,
    inputSchema: z.object({
      kinds: z.array(z.enum(["duplicate-instances", "room-separation-on-wall", "room-tag-outside"])).optional(),
      apply: z.boolean().default(false),
      maxFixes: z.number().int().min(1).max(5000).default(500),
    }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/warnings/resolve", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_purge_scan", {
    title: "Scan for unplaced families",
    description: `列出沒有被放置的族（唯讀，不改模型 —— 雖然是 POST）。預設只掃註解類品類。
回傳的族名清單可以直接餵給 revit_purge_delete 的 names。
Args:
  - categories: 品類名稱（可帶或不帶 OST_ 前綴，也可用中文品類名）；給了就忽略 annotationOnly
  - annotationOnly: 只掃註解類品類（categories 為空時才生效，預設 true）`,
    inputSchema: z.object({
      categories:     z.array(z.string()).optional(),
      annotationOnly: z.boolean().default(true),
    }).strict(), annotations: R,
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/purge/scan", undefined, a, LONG)); } catch (e) { return err(e); } });

  server.registerTool("revit_purge_delete", {
    title: "Delete unused families by name",
    description: `依名單刪除未被使用的族（仍在使用中的會自動跳過）。
dryRun 預設 true：不帶這個參數只會回傳 wouldDelete 預覽清單，不動模型。
要真的刪除必須明確帶 dryRun:false —— 請先讓使用者看過預覽清單再送。
（註：Revit 端 2026-10-03 前的版本會忽略 dryRun、照樣刪除。若面板或 addin 是舊版 DLL，
預覽不保證生效 —— 以回傳裡的 dryRun 欄位為準，它回報實際執行的模式。）
Args:
  - names: 要刪除的族名（建議直接取自 revit_purge_scan 的結果）
  - dryRun: 預設 true 只預覽；false 才真正刪除`,
    inputSchema: z.object({
      names: z.array(z.string().min(1)).min(1).describe("要刪除的族名；建議取自 revit_purge_scan"),
      dryRun: z.boolean().default(true).describe("預設 true 只回傳預覽清單；要真正刪除必須明確傳 false"),
    }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
  }, async (a) => { try { return ok(await revitRequest<Any>("post", "/purge/delete", undefined, a, LONG)); } catch (e) { return err(e); } });
}
