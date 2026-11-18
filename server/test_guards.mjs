// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

/**
 * 逾時 header、早期建模工具的預檢、錯誤訊息、工具分組、停用工具的回歸測試。
 *
 * 不需要真的 Revit：起一個假 add-in HTTP server（含 token 檔），LOCALAPPDATA 指到暫存資料夾，
 * 走真實的 stdio MCP。
 *
 *   node test_guards.mjs
 */
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import crypto from "node:crypto";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const PORT = 49600;
const appData = fs.mkdtempSync(path.join(os.tmpdir(), "revit-mcp-test-"));
const cfgDir = path.join(appData, "revit-mcp");
fs.mkdirSync(cfgDir, { recursive: true });
const token = crypto.randomBytes(32).toString("hex");
fs.writeFileSync(path.join(cfgDir, `port-${PORT}.token`), token, "utf8");

const received = [];
const srv = http.createServer((req, res) => {
  let raw = "";
  req.on("data", (d) => (raw += d));
  req.on("end", () => {
    const send = (status, obj) => { res.writeHead(status, { "Content-Type": "application/json; charset=utf-8" }); res.end(JSON.stringify(obj)); };
    if (req.headers["x-revit-mcp-token"] !== token) return send(401, { success: false, error: "Unauthorized" });
    const url = req.url.split("?")[0];
    if (url === "/ping") return send(200, { success: true, data: { server: "revit-mcp-addin", port: PORT, pid: 1 } });
    if (url === "/info") return send(200, { success: true, data: {
      server: "revit-mcp-addin", port: PORT, pid: 1, revitVersion: "2024", revitBuild: "x", username: "t",
      activeDocument: "Tower.rvt", activeDocumentPath: "C:\\Tower.rvt", activeView: "1F", documents: ["C:\\Tower.rvt"] } });
    const body = raw ? JSON.parse(raw) : {};
    received.push({ method: req.method, url, body, timeout: req.headers["x-revit-mcp-timeout-ms"] });
    if (url === "/elements/move") return send(500, { success: false, error: "Revit 已開始執行這個請求，但 17 秒內沒有完成，可能仍在進行。" });
    if (body.apply !== true) return send(200, { success: true, data: { preflight: true, applied: false, request: body, counts: {}, checks: ["樓層「1F」存在。"] } });
    return send(200, { success: true, data: { id: 7, name: "X", familyName: "F", typeName: "T", levelName: "1F", created: 1, walls: [], ids: [], skipped: [] } });
  });
});
await new Promise((r) => srv.listen(PORT, "127.0.0.1", r));

async function connect(extraEnv = {}) {
  const t = new StdioClientTransport({
    command: process.execPath, args: ["dist/index.js"], stderr: "ignore",
    env: { ...process.env, LOCALAPPDATA: appData, REVIT_PORT_RANGE_START: String(PORT), REVIT_PORT_RANGE_COUNT: "1",
           REVIT_WRITE_TIMEOUT_MS: "20000", ...extraEnv },
  });
  const c = new Client({ name: "test", version: "1" });
  await c.connect(t);
  return c;
}
const textOf = (r) => (r.content ?? []).map((x) => x.text).join("\n");

let pass = 0, fail = 0;
const check = (name, ok, detail = "") => { ok ? pass++ : fail++; console.log(`  ${ok ? "PASS" : "FAIL"}  ${name}${ok || !detail ? "" : `\n        ${detail}`}`); };

// ── 預設：全部工具 ──
let c = await connect();
let names = (await c.listTools()).tools.map((t) => t.name);
check("預設載入全部工具，含 revit_toolsets", names.includes("revit_create_walls") && names.includes("revit_toolsets") && names.length > 160, `count=${names.length}`);

received.length = 0;
let r = await c.callTool({ name: "revit_create_levels", arguments: { levels: [{ name: "2F", elevationMm: 3600 }] } });
let last = received.at(-1);
check("沒帶 apply：送出 apply=false", last?.url === "/levels" && last.body.apply === false, JSON.stringify(last));
check("沒帶 apply：回應是預檢", textOf(r).includes("預檢") && textOf(r).includes("apply=true"), textOf(r).slice(0, 200));
check("帶逾時 header，且比 server 逾時短", Number(last?.timeout) === 17000, String(last?.timeout));

r = await c.callTool({ name: "revit_modify_element", arguments: { elementId: 7, parameters: { 備註: "a" }, apply: true } });
last = received.at(-1);
check("modify_element apply=true 送到 add-in", last?.method === "PUT" && last.url === "/elements/7" && last.body.apply === true, JSON.stringify(last));
check("modify_element apply=true 回應是執行結果", textOf(r).includes("Modified element"), textOf(r).slice(0, 200));

r = await c.callTool({ name: "revit_move_elements", arguments: { elementIds: [1], deltaMm: [100, 0, 0], apply: true } });
check("add-in 的錯誤訊息原樣回給 AI", textOf(r).includes("可能仍在進行") && !textOf(r).includes("Revit API error 500"), textOf(r).slice(0, 200));
await c.close();

// ── 只開 core，再用 revit_toolsets 開 modeling ──
c = await connect({ REVIT_MCP_TOOLSETS: "core" });
names = (await c.listTools()).tools.map((t) => t.name);
check("REVIT_MCP_TOOLSETS=core：不含建模工具", !names.includes("revit_create_walls") && names.includes("revit_list_elements"), `count=${names.length}`);
r = await c.callTool({ name: "revit_toolsets", arguments: { enable: ["modeling"] } });
names = (await c.listTools()).tools.map((t) => t.name);
check("revit_toolsets 開啟 modeling 後出現建模工具", names.includes("revit_create_walls") && !names.includes("revit_tag_elements"), textOf(r).slice(0, 120));
r = await c.callTool({ name: "revit_toolsets", arguments: { disable: ["core", "modeling"] } });
names = (await c.listTools()).tools.map((t) => t.name);
check("core 不能關；modeling 可以關回去", names.includes("revit_list_elements") && !names.includes("revit_create_walls"));
await c.close();

// ── rules.json 停用工具 ──
fs.writeFileSync(path.join(cfgDir, "rules.json"), "\uFEFF" + JSON.stringify({ disabledTools: ["revit_ungroup_groups"] }), "utf8");
c = await connect();
names = (await c.listTools()).tools.map((t) => t.name);
check("rules.json disabledTools：工具不在清單", !names.includes("revit_ungroup_groups") && names.includes("revit_groups"));
await c.close();

srv.close();
fs.rmSync(appData, { recursive: true, force: true });
console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
