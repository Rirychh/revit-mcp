// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

/**
 * 機械比對 tools-manifest.json 與 MCP server 實際註冊的工具。
 *
 *   node verify_tools.mjs            # 比對 dist/index.js（Claude Desktop 實際跑的）
 *   node verify_tools.mjs src        # 比對 src/index.ts（需要 tsx）
 *
 * 退出碼 0 = 完全對得上。非 0 = 有差異或讀不到 manifest。
 * 這支只做 tools/list，不會呼叫任何 Revit 路由，所以 Revit 沒開也能跑。
 */
import { readFileSync, existsSync } from "node:fs";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const MANIFEST = "tools-manifest.json";

// ── manifest：不存在／壞掉都要大聲說，不要當成「0 筆」靜默通過 ──────────────
if (!existsSync(MANIFEST)) {
  console.error(`FAIL: 找不到 ${MANIFEST}。這份檔由 revit-mcp-addin-src 的 build 複製過來，請先在 addin repo 跑一次 build。`);
  process.exit(2);
}
let manifest;
try {
  manifest = JSON.parse(readFileSync(MANIFEST, "utf8"));
} catch (e) {
  console.error(`FAIL: ${MANIFEST} 不是合法 JSON（${e.message}）。不要猜內容，請從 addin repo 重新複製。`);
  process.exit(2);
}
if (!Array.isArray(manifest.tools) || manifest.tools.length === 0) {
  console.error(`FAIL: ${MANIFEST} 沒有 tools[] 陣列，或是空的。`);
  process.exit(2);
}

// ── 啟動 server 問 tools/list ────────────────────────────────────────────────
const useSrc = process.argv[2] === "src";
const transport = useSrc
  ? new StdioClientTransport({ command: "npx", args: ["tsx", "src/index.ts"] })
  : new StdioClientTransport({ command: process.execPath, args: ["dist/index.js"] });

const client = new Client({ name: "verify-tools", version: "1" });
await client.connect(transport);
const { tools } = await client.listTools();
await client.close();

const live = new Map(tools.map((t) => [t.name, t]));
const declared = new Map(manifest.tools.map((t) => [t.name, t]));

// 這兩支是 MCP 端的多實例探測工具，刻意不對應任何 addin 路由，不在 manifest 內。
const EXPECTED_EXTRA = new Set(["revit_list_instances", "revit_use_instance"]);

const missing = [...declared.keys()].filter((n) => !live.has(n));
const extra = [...live.keys()].filter((n) => !declared.has(n) && !EXPECTED_EXTRA.has(n));
const extraExpected = [...live.keys()].filter((n) => EXPECTED_EXTRA.has(n));

// ── readOnly 旗標 vs targetGuard 注入的 document 參數 ────────────────────────
// 非唯讀工具必須有 document（自己宣告或 guard 注入）；唯讀工具不該被 guard 加上。
const guardWrong = [];
for (const [name, d] of declared) {
  const t = live.get(name);
  if (!t) continue;
  const hasDoc = Boolean(t.inputSchema?.properties?.document);
  if (d.readOnly === false && !hasDoc) guardWrong.push(`${name}: 非唯讀卻沒有 document 參數（沒過 targetGuard？）`);
  // 唯讀工具可以自己宣告 document（如 revit_document_info），只是 guard 不該插手，無法從外部分辨，故不檢查反向。
}

// ── pathParams：placeholder 必須對應到「必填」參數，缺值要在 MCP 層就被擋掉 ──
const pathParamWrong = [];
for (const [name, d] of declared) {
  const pp = d.pathParams ?? [];
  if (pp.length === 0) continue;
  const t = live.get(name);
  if (!t) continue;
  const req = new Set(t.inputSchema?.required ?? []);
  for (const p of pp) {
    if (!t.inputSchema?.properties?.[p]) pathParamWrong.push(`${name}: 路由 ${d.route} 需要 {${p}}，但工具沒有這個參數`);
    else if (!req.has(p)) pathParamWrong.push(`${name}: 路由 ${d.route} 需要 {${p}}，但參數不是必填 — 省略時會送出含字面 {${p}} 的壞 URL`);
  }
}

// ── 形狀以 manifest 為準的工具（shapeAuthority=manifest）────────────────────
// 刻意比 manifest 更嚴格的地方列在這裡，其餘不一致都算 bug。
const INTENTIONAL = {
  // alwaysWrites 路由，materials 空著呼叫只會白開一個 Transaction，所以改成必填。
  revit_set_material_appearance: { requiredExtra: ["materials"] },
};
const shapeWrong = [];
for (const [name, d] of declared) {
  // 用 shapeAuthority，不要用 tsCoverage：工具補完後 tsCoverage 會翻成 covered，
  // 舊的過濾條件會讓這段退化成「檢查 0 支」的假綠燈。
  // 既有 55 支手寫先於 manifest，形狀不以 manifest 為準，不在本檢查範圍。
  if (d.shapeAuthority !== "manifest") continue;
  const t = live.get(name);
  if (!t) continue;

  // 參數名：manifest 的 properties ＋（非唯讀時）targetGuard 注入的 document
  const want = new Set(Object.keys(d.input_schema?.properties ?? {}));
  if (d.readOnly === false) want.add("document");
  const got = new Set(Object.keys(t.inputSchema?.properties ?? {}));
  for (const p of want) if (!got.has(p)) shapeWrong.push(`${name}: 少了參數 ${p}`);
  for (const p of got) if (!want.has(p)) shapeWrong.push(`${name}: 多了參數 ${p}`);

  // 必填欄位
  const allow = INTENTIONAL[name]?.requiredExtra ?? [];
  const wantReq = new Set([...(d.input_schema?.required ?? []), ...allow]);
  const gotReq = new Set(t.inputSchema?.required ?? []);
  for (const p of wantReq) if (!gotReq.has(p)) shapeWrong.push(`${name}: ${p} 應為必填`);
  for (const p of gotReq) if (!wantReq.has(p)) shapeWrong.push(`${name}: ${p} 不該是必填`);

  // 唯讀旗標
  if (Boolean(t.annotations?.readOnlyHint) !== Boolean(d.readOnly))
    shapeWrong.push(`${name}: readOnlyHint=${t.annotations?.readOnlyHint} 與 manifest readOnly=${d.readOnly} 不符`);
  if (d.destructive && !t.annotations?.destructiveHint)
    shapeWrong.push(`${name}: manifest destructive=true 但沒有標 destructiveHint`);
}

// ── targetGuard 迴歸：工具自己宣告的 document 不可被改寫成選填 ───────────────
// revit_activate_document 的 document 是必填（指名要啟用哪個已開啟的檔）。
// 舊版 guard 會 extend 掉它、變成選填，而且在呼叫 callback 前就把值剝掉。
const guardRegression = [];
{
  const t = live.get("revit_activate_document");
  if (!t) guardRegression.push("revit_activate_document 不存在");
  else if (!(t.inputSchema?.required ?? []).includes("document"))
    guardRegression.push("revit_activate_document 的 document 變成選填了 — targetGuard 又去覆寫工具自己的 schema");
}

// ── 報告 ────────────────────────────────────────────────────────────────────
const target = useSrc ? "src/index.ts" : "dist/index.js";
console.log(`# verify_tools — ${target}`);
console.log(`manifest tools[]      : ${declared.size}`);
console.log(`實際註冊 (tools/list) : ${live.size}`);
console.log(`  其中對應 manifest   : ${live.size - extraExpected.length - extra.length}`);
console.log(`  刻意多出的探測工具  : ${extraExpected.length} (${extraExpected.join(", ") || "—"})`);
console.log(`manifest 有、沒註冊   : ${missing.length}`);
missing.forEach((n) => console.log(`  - ${n}  [${declared.get(n).method} ${declared.get(n).route}] tsCoverage=${declared.get(n).tsCoverage}`));
console.log(`註冊了、manifest 沒有 : ${extra.length}`);
extra.forEach((n) => console.log(`  - ${n}`));
console.log(`targetGuard 異常      : ${guardWrong.length}`);
guardWrong.forEach((m) => console.log(`  - ${m}`));
console.log(`pathParams 異常       : ${pathParamWrong.length}`);
pathParamWrong.forEach((m) => console.log(`  - ${m}`));
console.log(`新工具形狀異常        : ${shapeWrong.length}  （形狀以 manifest 為準的 ${[...declared.values()].filter((d) => d.shapeAuthority === "manifest").length} 支）`);
shapeWrong.forEach((m) => console.log(`  - ${m}`));
console.log(`targetGuard 迴歸      : ${guardRegression.length}`);
guardRegression.forEach((m) => console.log(`  - ${m}`));

const bad = missing.length + extra.length + guardWrong.length + pathParamWrong.length + shapeWrong.length + guardRegression.length;
console.log(bad === 0 ? "\nOK — 完全對得上。" : `\nFAIL — ${bad} 項對不上。`);
process.exit(bad === 0 ? 0 : 1);
