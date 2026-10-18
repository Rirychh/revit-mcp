// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
const t = new StdioClientTransport({ command: process.execPath, args: ["dist/index.js"] });
const c = new Client({ name: "t", version: "1" });
await c.connect(t);
const { tools } = await c.listTools();
const w = tools.filter(x => x.inputSchema?.properties?.document);
console.log("tools", tools.length, "with document", w.length);
console.log("place_views props:", Object.keys(tools.find(x => x.name === "revit_place_views").inputSchema.properties).join(","));
console.log("list_sheets has document:", !!tools.find(x => x.name === "revit_list_sheets").inputSchema.properties?.document);
const show = r => console.log((r.content || []).map(x => x.text).join("\n").slice(0, 600), "\n---");
show(await c.callTool({ name: "revit_place_views", arguments: { sheetNumber: "A2-05", items: [{ viewId: 5229038, x: 423, y: 365 }] } }));
show(await c.callTool({ name: "revit_place_views", arguments: { sheetNumber: "A2-05", items: [{ viewId: 5229038, x: 423, y: 365 }], document: "不存在的檔" } }));
show(await c.callTool({ name: "revit_place_views", arguments: { sheetNumber: "A2-05", items: [{ viewId: 5229038, x: 423, y: 365 }], document: "側置塔樓" } }));
await c.close();
