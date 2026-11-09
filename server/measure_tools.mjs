// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

// 量測 tools/list 回傳給 AI 的大小（每個工具：名稱＋描述＋inputSchema）
import { spawn } from "node:child_process";
import { writeFileSync } from "node:fs";
const p = spawn(process.execPath, ["dist/index.js"], { stdio: ["pipe", "pipe", "ignore"] });
let buf = "";
const send = (o) => p.stdin.write(JSON.stringify(o) + "\n");
p.stdout.on("data", (d) => {
  buf += d.toString();
  let i;
  while ((i = buf.indexOf("\n")) >= 0) {
    const line = buf.slice(0, i); buf = buf.slice(i + 1);
    if (!line.trim()) continue;
    const msg = JSON.parse(line);
    if (msg.id === 1) { send({ jsonrpc: "2.0", method: "notifications/initialized" }); send({ jsonrpc: "2.0", id: 2, method: "tools/list" }); }
    if (msg.id === 2) {
      const rows = msg.result.tools.map((t) => ({ name: t.name, desc: (t.description || "").length, schema: JSON.stringify(t.inputSchema).length, total: JSON.stringify(t).length }));
      rows.sort((a, b) => b.total - a.total);
      const sum = rows.reduce((s, r) => s + r.total, 0);
      writeFileSync(process.argv[2] || "measure.json", JSON.stringify({ count: rows.length, totalChars: sum, rows, tools: msg.result.tools }, null, 1));
      p.kill(); process.exit(0);
    }
  }
});
send({ jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "measure", version: "1" } } });
