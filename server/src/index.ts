#!/usr/bin/env node
// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

/**
 * revit-mcp-server
 *
 * MCP Server (stdio) that connects Claude to a running Autodesk Revit instance
 * via the RevitMcpAddin HTTP server (127.0.0.1:48884, token-authenticated —
 * see services/token.ts).
 *
 * 工具清單的單一事實源是 tools-manifest.json（由 revit-mcp-addin-src 的 build
 * 複製過來，與 add-in 那份 byte-identical）。每個 add-in 路由在 manifest 的
 * tools[] 裡剛好一筆；這裡註冊的工具名稱必須與 manifest 一致。
 * 比對方式：`node verify_tools.mjs`（跑 tools/list 後機械比對，不需要 Revit 開著）。
 * manifest 之外另有 revit_list_instances / revit_use_instance 兩支，是 MCP 端的
 * 多實例探測工具，刻意沒有對應路由。
 *
 * Multi-instance: each Revit binds the first free port from 48884 upwards, so
 * several Revits can run side by side. This server discovers them by probing
 * /info across that range, auto-selects when only one is running, and lets the
 * caller switch targets with revit_use_instance. Setting REVIT_PORT pins this
 * server to one instance and disables switching.
 */

import { McpServer }          from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { registerElementTools }  from "./tools/elements.js";
import { registerViewTools }     from "./tools/views.js";
import { registerExportTools }   from "./tools/export.js";
import { registerInstanceTools } from "./tools/instances.js";
import { registerAnnotationTools } from "./tools/annotations.js";
import { registerFamilyTools }   from "./tools/families.js";
import { registerSheetTools }    from "./tools/sheets.js";
import { registerBuildTools }    from "./tools/build.js";
import { registerLinkedTools }   from "./tools/linked.js";
import { registerSiteTools }     from "./tools/site.js";
import { registerDocTools }      from "./tools/docs.js";
import { installTargetGuard }    from "./services/targetGuard.js";
import { installCompactTools }   from "./services/compactTools.js";
import { registerViewSettingsTools } from "./tools/viewSettings.js";
import { registerQcTools }       from "./tools/qc.js";
import { registerDraftingTools } from "./tools/drafting.js";
import { registerMaterialTools } from "./tools/materials.js";
import { registerAnalysisTools } from "./tools/analysis.js";
import { registerMepTools }      from "./tools/mep.js";
import { registerModelingTools } from "./tools/modeling.js";
import { registerLayoutTools }   from "./tools/layout.js";
import { registerEditTools }     from "./tools/edit.js";
import { registerViewAnnotTools } from "./tools/viewAnnot.js";
import { registerProjectTools }  from "./tools/project.js";
import { registerStairsSweepTools } from "./tools/stairsSweep.js";
import { registerTypesAreasTools } from "./tools/typesAreas.js";

const INSTRUCTIONS = [
  "操作開著的 Autodesk Revit 模型。",
  "單位：長度一律 mm（座標也是 mm，模型座標或視圖平面 uv），面積 m²、體積 m³。",
  "會改模型的工具預設只預檢（apply=false 或 dryRun=true）：先看回報，確認後再用 apply=true 執行；一次呼叫在單一交易裡完成，任何錯誤整批回滾、模型不變。",
  "不知道名稱或 id 時先查：樓層 revit_list_levels、類型 revit_list_build_types、元件 revit_list_elements、視圖 revit_list_views、圖紙 revit_list_sheets。",
  "要確認結果長什麼樣：revit_screenshot_view，或 revit_labeled_snapshot（圖上標號對照元件 id）。",
  "多個 Revit 同時開著時，會改模型的工具要加 document（作用中檔名關鍵字）或先用 revit_use_instance 指定目標；沒指定會擋下不執行。",
  "刪除、清除、關檔、同步中央這類動作執行前先跟使用者確認。",
  "新建族（build_family、輪廓族、量體）若有 %LOCALAPPDATA%\\revit-mcp\\naming.json，會自動補上設定的前綴；最終名稱以回傳結果為準。",
].join("\n");

const server = new McpServer(
  { name: "revit-mcp-server", version: "1.10.2" },
  { instructions: INSTRUCTIONS },
);

// 必須在註冊工具前：會改模型的工具一律加 document 參數與多 Revit 防呆
installTargetGuard(server);
// 在 targetGuard 外層：先整理描述（Args 搬進參數說明、加相近工具），再交給 targetGuard
installCompactTools(server);

registerInstanceTools(server);
registerElementTools(server);
registerViewTools(server);
registerExportTools(server);
registerAnnotationTools(server);
registerFamilyTools(server);
registerSheetTools(server);
registerBuildTools(server);
registerLinkedTools(server);
registerSiteTools(server);
registerDocTools(server);
registerViewSettingsTools(server);
registerQcTools(server);
registerDraftingTools(server);
registerMaterialTools(server);
registerAnalysisTools(server);
registerMepTools(server);
registerModelingTools(server);
registerLayoutTools(server);
registerEditTools(server);
registerViewAnnotTools(server);
registerProjectTools(server);
registerStairsSweepTools(server);
registerTypesAreasTools(server);

async function main(): Promise<void> {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("[revit-mcp-server] Running via stdio — waiting for Claude...");
}

main().catch((err) => {
  console.error("[revit-mcp-server] Fatal error:", err);
  process.exit(1);
});
