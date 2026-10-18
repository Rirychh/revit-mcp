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
import { registerViewSettingsTools } from "./tools/viewSettings.js";
import { registerQcTools }       from "./tools/qc.js";
import { registerDraftingTools } from "./tools/drafting.js";
import { registerMaterialTools } from "./tools/materials.js";

const server = new McpServer({
  name:    "revit-mcp-server",
  version: "1.0.0",
});

// 必須在註冊工具前：會改模型的工具一律加 document 參數與多 Revit 防呆
installTargetGuard(server);

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

async function main(): Promise<void> {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("[revit-mcp-server] Running via stdio — waiting for Claude...");
}

main().catch((err) => {
  console.error("[revit-mcp-server] Fatal error:", err);
  process.exit(1);
});
