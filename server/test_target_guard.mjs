// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

/**
 * targetGuard 回歸測試：document 參數必須「同時」用來選 Revit 實例、並原樣送到 handler。
 *
 * 不需要真的 Revit：起兩個假 add-in HTTP server（含 token 檔），
 * 用 REVIT_PORT_RANGE_START 把真正的 dist/index.js 指到假的埠，
 * 走真實的 stdio MCP + axios + token 認證，檢查落到 handler 的 request body。
 *
 *   node test_target_guard.mjs
 */
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import crypto from "node:crypto";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const BASE = 49500;
const INSTANCES = [
  { port: BASE,     pid: 9001, activeDocument: "Tower.rvt",  documents: ["Tower.rvt", "Podium.rvt"] },
  { port: BASE + 1, pid: 9002, activeDocument: "Bridge.rvt", documents: ["Bridge.rvt"] },
];

const tokenDir = path.join(process.env.LOCALAPPDATA ?? path.join(os.homedir(), "AppData", "Local"), "revit-mcp");
const tokenFile = (port) => path.join(tokenDir, `port-${port}.token`);

/** 收到的 POST：{ port, path, body } */
const received = [];
const servers = [];

function startFake(inst) {
  const token = crypto.randomBytes(32).toString("hex");
  fs.mkdirSync(tokenDir, { recursive: true });
  fs.writeFileSync(tokenFile(inst.port), token, { encoding: "utf8" });

  const srv = http.createServer((req, res) => {
    const send = (status, obj) => {
      const b = Buffer.from(JSON.stringify(obj), "utf8");
      res.writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Content-Length": b.length });
      res.end(b);
    };
    if (req.headers["x-revit-mcp-token"] !== token)
      return send(401, { success: false, error: "Unauthorized" });

    const url = new URL(req.url, `http://127.0.0.1:${inst.port}`);
    const p = url.pathname.replace(/\/$/, "");

    if (req.method === "GET" && p === "/ping")
      return send(200, { success: true, data: { server: "revit-mcp-addin", port: inst.port, pid: inst.pid } });

    if (req.method === "GET" && p === "/info")
      return send(200, { success: true, data: {
        server: "revit-mcp-addin", port: inst.port, pid: inst.pid,
        revitVersion: "2024", revitBuild: "test", username: "tester",
        activeDocument: inst.activeDocument,
        activeDocumentPath: `C:\\models\\${inst.activeDocument}`,
        activeView: "Level 1", documents: inst.documents,
      }});

    let raw = "";
    req.on("data", (c) => (raw += c));
    req.on("end", () => {
      let body = null;
      if (raw) { try { body = JSON.parse(raw); } catch { body = raw; } }
      received.push({ port: inst.port, path: p, method: req.method, body });
      send(200, { success: true, data: { ok: true } });
    });
  });

  return new Promise((resolve) => {
    srv.listen(inst.port, "127.0.0.1", () => { servers.push(srv); resolve(); });
  });
}

function cleanup() {
  for (const s of servers) { try { s.close(); } catch {} }
  for (const i of INSTANCES) { try { fs.unlinkSync(tokenFile(i.port)); } catch {} }
}

// ── assertions ─────────────────────────────────────────────
let pass = 0, fail = 0;
const ok  = (name) => { pass++; console.log(`  PASS  ${name}`); };
const bad = (name, detail) => { fail++; console.log(`  FAIL  ${name}\n        ${detail}`); };
const check = (name, cond, detail) => (cond ? ok(name) : bad(name, detail));
const textOf = (r) => (r?.content ?? []).map((c) => c.text).join("\n");
const lastPost = () => received[received.length - 1];

// ── run ────────────────────────────────────────────────────
await Promise.all(INSTANCES.map(startFake));

const transport = new StdioClientTransport({
  command: process.execPath,
  args: ["dist/index.js"],
  cwd: import.meta.dirname,
  env: {
    ...process.env,
    REVIT_PORT_RANGE_START: String(BASE),
    REVIT_PORT_RANGE_COUNT: String(INSTANCES.length),
    REVIT_PORT: "",
  },
});
const client = new Client({ name: "targetGuard-test", version: "1" });
await client.connect(transport);

const { tools } = await client.listTools();
const tool = (n) => tools.find((t) => t.name === n);

console.log(`\n兩個假 Revit：port ${BASE} (Tower 作用中, Podium 也開著) / port ${BASE + 1} (Bridge 作用中)\n`);

// 1. schema：工具自己宣告的 document 不能被 guard 蓋成選填
{
  const s = tool("revit_activate_document")?.inputSchema;
  check("revit_activate_document.document 仍是必填（guard 沒蓋掉原 schema）",
    Array.isArray(s?.required) && s.required.includes("document"),
    `required = ${JSON.stringify(s?.required)}`);
}

// 2. 兩個 Revit 開著又沒指定 → 擋下。
//    必須在任何「有給 document」的呼叫之前跑：一旦指定過，那個實例就被 lock 住
//    （instances.ts 的 explicitPort/lockedDoc），後續沒給 document 會沿用它 —— 那是設計行為。
{
  received.length = 0;
  const r = await client.callTool({
    name: "revit_place_views",
    arguments: { sheetNumber: "A2-05", items: [{ viewId: 1, x: 0, y: 0 }] },
  }).catch((e) => ({ isError: true, content: [{ text: String(e) }] }));
  check("兩個 Revit 開著、沒給 document → 擋下不執行",
    r.isError === true && received.length === 0,
    `isError=${r.isError} posts=${received.length} / ${textOf(r).slice(0, 200)}`);
}

// 3. 核心：指名「有開著但不是作用中」的檔 → 選對實例 + document 原樣送達 handler
{
  received.length = 0;
  const r = await client.callTool({ name: "revit_activate_document", arguments: { document: "Podium" } });
  const p = lastPost();
  check("activate_document 路由到開著 Podium 的那個 Revit",
    p?.port === BASE, `實際 port = ${p?.port}；回應：${textOf(r).slice(0, 200)}`);
  check("activate_document 打 /documents/activate",
    p?.path === "/documents/activate", `實際 path = ${p?.path}`);
  check("★ handler 收到 document=\"Podium\"（修掉的 bug 本身）",
    p?.body?.document === "Podium", `實際 body = ${JSON.stringify(p?.body)}`);
}

// 4. 另一支 ownDoc 工具：document 同樣要送達，並路由到另一個實例
{
  received.length = 0;
  await client.callTool({ name: "revit_save_document", arguments: { document: "Bridge" } });
  const p = lastPost();
  check("save_document 路由到 Bridge 所在的 Revit",
    p?.port === BASE + 1, `實際 port = ${p?.port}`);
  check("save_document 的 handler 收到 document=\"Bridge\"",
    p?.body?.document === "Bridge", `實際 body = ${JSON.stringify(p?.body)}`);
}

// 5. 非 ownDoc 工具：document 只用來選實例，不應污染 request body
{
  received.length = 0;
  await client.callTool({
    name: "revit_place_views",
    arguments: { sheetNumber: "A2-05", items: [{ viewId: 1, x: 0, y: 0 }], document: "Tower" },
  });
  const p = lastPost();
  check("非 ownDoc 工具仍靠 document 選到正確實例",
    p?.port === BASE, `實際 port = ${p?.port}`);
  check("非 ownDoc 工具的 body 不含 document（只作用在作用中檔案）",
    p?.body !== null && !("document" in (p?.body ?? {})), `實際 body = ${JSON.stringify(p?.body)}`);
}

// 6. 防護沒有被放寬掉：對不到檔名時擋下，不發任何請求
{
  received.length = 0;
  const r = await client.callTool({ name: "revit_activate_document", arguments: { document: "NoSuchModel" } });
  check("對不到檔名時擋下且不發請求",
    r.isError === true && received.length === 0,
    `isError=${r.isError} posts=${received.length} / ${textOf(r).slice(0, 200)}`);
}

// 7. 檔名同時符合多個 Revit → 擋下，不要亂猜
{
  received.length = 0;
  const r = await client.callTool({ name: "revit_activate_document", arguments: { document: ".rvt" } });
  check("檔名對到多個 Revit 時擋下且不發請求",
    r.isError === true && received.length === 0,
    `isError=${r.isError} posts=${received.length} / ${textOf(r).slice(0, 200)}`);
}

await client.close();
cleanup();

console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
