// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { revitRequest } from "../services/revitClient.js";

/**
 * 建模、修改、載入族群。幾何一律 mm、專案內部座標。
 * 會改模型的工具都先 apply=false 預檢；群組內元件一律跳過、不解除群組。
 */

type Any = Record<string, unknown>;
const ok  = (d: unknown) => ({ content: [{ type: "text" as const, text: "```json\n" + JSON.stringify(d, null, 2) + "\n```" }], structuredContent: d as Any });
const err = (e: unknown) => ({ content: [{ type: "text" as const, text: `Error: ${e instanceof Error ? e.message : e}` }] });
const W = { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false };
const LONG = 5 * 60 * 1000;

const Pt2 = z.array(z.number()).length(2).describe("[x, y] mm");
const Pt3 = z.array(z.number()).min(2).max(3).describe("[x, y, z] mm；缺 z 時為 0");
const Line2 = z.object({ start: Pt2, end: Pt2 }).strict();
const call = (path: string) => async (a: Any) => {
  try { return ok(await revitRequest<Any>("post", path, undefined, a, LONG)); } catch (e) { return err(e); }
};

export function registerModelingTools(server: McpServer): void {

  server.registerTool("revit_create_grids", {
    title: "Create grids",
    description: `建立網格線（直線）。名稱已存在的會跳過。
apply=false（預設）只預檢；apply=true 單一交易建立，任何錯誤整批回滾。
Args:
  - grids: [{ name?, start:[x,y], end:[x,y] }]（mm）
  - apply: false = 預檢；true = 建立`,
    inputSchema: z.object({
      grids: z.array(z.object({ name: z.string().optional(), start: Pt2, end: Pt2 }).strict()).min(1).max(300),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, call("/grids"));

  server.registerTool("revit_create_structural_framing", {
    title: "Create structural framing (beams)",
    description: `建立結構梁：每支由 start、end 兩點（mm）定義。模型裡要先有結構框架族群（沒有就先用 revit_load_families）。
apply=false（預設）只預檢；apply=true 單一交易建立。
Args:
  - beams: [{ start:[x,y,z], end:[x,y,z], level? }]
  - familyType: 「族群: 類型」或類型名（省略取第一個；找不到時回傳可用清單）
  - level: 預設參考樓層名（各支可另外指定）
  - apply: false = 預檢；true = 建立`,
    inputSchema: z.object({
      beams: z.array(z.object({ start: Pt3, end: Pt3, level: z.string().optional() }).strict()).min(1).max(500),
      familyType: z.string().optional(),
      level: z.string().optional(),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, call("/structural-framing"));

  server.registerTool("revit_create_rooms", {
    title: "Create rooms",
    description: `在指定樓層的點位建立房間，可設名稱與編號。點不在封閉邊界內時會建出「未封閉」房間（面積 0），結果裡的 enclosed 會標出來。
apply=false（預設）只預檢；apply=true 單一交易建立。
Args:
  - rooms: [{ level?, point:[x,y], name?, number? }]
  - level: 預設樓層名（各房間可另外指定）
  - apply: false = 預檢；true = 建立`,
    inputSchema: z.object({
      rooms: z.array(z.object({ level: z.string().optional(), point: Pt2, name: z.string().optional(), number: z.string().optional() }).strict()).min(1).max(500),
      level: z.string().optional(),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, call("/rooms/create"));

  server.registerTool("revit_create_room_separation", {
    title: "Create room separation lines",
    description: `建立房間分隔線（畫在樓層高程上）。給 viewId 就畫在該平面視圖；只給 level 時用該樓層的第一個平面視圖。
apply=false（預設）只預檢；apply=true 建立。注意：分隔線與牆重疊會產生警告，可用 revit_resolve_warnings 處理。
Args:
  - lines: [{ start:[x,y], end:[x,y] }]（mm）
  - viewId: 平面視圖 id
  - level: 樓層名（沒給 viewId 時用）
  - apply: false = 預檢；true = 建立`,
    inputSchema: z.object({
      lines: z.array(Line2).min(1).max(1000),
      viewId: z.number().int().optional(),
      level: z.string().optional(),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, call("/rooms/separation"));

  server.registerTool("revit_create_detail_lines", {
    title: "Create detail lines",
    description: `在指定視圖畫詳圖線（視圖專屬）。
座標：平面、製圖、圖例視圖用專案平面座標 [x,y]；剖面、立面視圖的 [x,y] 是視圖平面上的 (右, 上) 座標，原點為視圖原點。
apply=false（預設）只預檢；apply=true 建立。
Args:
  - viewId: 視圖 id（不可為 3D 或樣板）
  - lines: [{ start:[x,y], end:[x,y] }]（mm）
  - lineStyle: 線型名稱（例如「<中線>」；找不到時回傳可用清單）
  - apply: false = 預檢；true = 建立`,
    inputSchema: z.object({
      viewId: z.number().int(),
      lines: z.array(Line2).min(1).max(2000),
      lineStyle: z.string().optional(),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, call("/detail-lines"));

  server.registerTool("revit_create_schedule", {
    title: "Create schedule",
    description: `建立明細表並加入欄位。欄位名稱用 Revit 介面上的顯示名（例如「族群與類型」「數量」「樓層」）。
apply=false（預設）只預檢：回傳這個品類所有可用欄位名稱，以及哪些要求的欄位不存在。有欄位不存在時 apply=true 不會建立。
建好後可用 revit_export_schedule 匯出。
Args:
  - category: 品類（OST_ 名稱、英文別名或品類顯示名）
  - name: 明細表名稱（不可與既有的重複）
  - fields: 欄位顯示名清單（依序加入）
  - apply: false = 預檢；true = 建立`,
    inputSchema: z.object({
      category: z.string().min(1),
      name: z.string().min(1),
      fields: z.array(z.string()).default([]),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, call("/schedules"));

  server.registerTool("revit_mirror_elements", {
    title: "Mirror elements",
    description: `鏡射元件（預設保留原件、產生鏡射複本）。群組內、已釘選、Revit 不允許鏡射的元件會跳過。
鏡射面：axis="y" 為通過 origin、平行 Y 軸的鉛直面（左右翻）；axis="x" 為平行 X 軸（上下翻）；也可直接給 plane。origin 省略 = 所選元件邊界框中心。
apply=false（預設）只預檢；apply=true 執行，回傳新元件 id。
Args:
  - ids: 元件 id
  - axis: "x"｜"y"（預設 y）
  - origin: [x,y]（mm）
  - plane: { origin:[x,y,z], normal:[x,y,z] }（給了就忽略 axis/origin）
  - copy: true = 保留原件（預設）；false = 只翻轉原件
  - apply: false = 預檢；true = 執行`,
    inputSchema: z.object({
      ids: z.array(z.number().int()).min(1),
      axis: z.enum(["x", "y"]).default("y"),
      origin: Pt2.optional(),
      plane: z.object({ origin: Pt3, normal: z.array(z.number()).length(3) }).strict().optional(),
      copy: z.boolean().default(true),
      apply: z.boolean().default(false),
    }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  }, call("/elements/mirror"));

  server.registerTool("revit_color_by_parameter", {
    title: "Color elements by parameter value",
    description: `依參數值在視圖中上色：同一個值同一個顏色（線色＋實心填滿），只改這個視圖的元件覆寫，不改元件本身。參數先找實例、再找類型。
apply=false（預設）只預檢，回傳每個值的數量與顏色（legend）。apply=true 套用。要還原用 revit_clear_view_overrides。
注意：視圖若套用的樣板鎖住 V/G，元件覆寫仍可設定，但品類層級設定不受影響。
Args:
  - viewId: 視圖 id（省略 = 作用中視圖）
  - category: 品類
  - parameter: 參數名稱（介面顯示名，例如「類型」「防火時效」）
  - apply: false = 預檢；true = 套用`,
    inputSchema: z.object({
      viewId: z.number().int().optional(),
      category: z.string().min(1),
      parameter: z.string().min(1),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, call("/views/color-by-parameter"));

  server.registerTool("revit_clear_view_overrides", {
    title: "Clear element overrides in a view",
    description: `清除視圖中元件的圖形覆寫（例如 revit_color_by_parameter 上的顏色）。只處理目前真的有覆寫的元件。
ids 與 category 都省略 = 清除這個視圖中所有元件的覆寫（包括使用者手動設定的），請先看預檢數量。
apply=false（預設）只預檢；apply=true 執行。
Args:
  - viewId: 視圖 id（省略 = 作用中視圖）
  - ids: 只清這些元件
  - category: 只清這個品類
  - apply: false = 預檢；true = 執行`,
    inputSchema: z.object({
      viewId: z.number().int().optional(),
      ids: z.array(z.number().int()).optional(),
      category: z.string().optional(),
      apply: z.boolean().default(false),
    }).strict(),
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: true, openWorldHint: false },
  }, call("/views/clear-overrides"));

  server.registerTool("revit_load_families", {
    title: "Load families from .rfa",
    description: `從 .rfa 檔載入族群到作用中專案。可給檔案清單或資料夾（只取該層，略過 *.0001.rfa 備份檔），一次最多 200 個。
overwrite=false（預設）：專案已有同名族群就跳過。overwrite=true 會重新載入並覆蓋既有族群的類型參數值。
apply=false（預設）只預檢：逐檔回報 load／reload／skip 與原因。apply=true 單一交易載入，任何錯誤整批回滾。
Args:
  - paths: .rfa 完整路徑清單
  - folder: 資料夾路徑
  - overwrite: 是否覆蓋同名族群（預設 false）
  - apply: false = 預檢；true = 載入`,
    inputSchema: z.object({
      paths: z.array(z.string()).optional(),
      folder: z.string().optional(),
      overwrite: z.boolean().default(false),
      apply: z.boolean().default(false),
    }).strict(), annotations: W,
  }, call("/families/load"));
}
