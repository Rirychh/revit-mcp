// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

// token 檔 fallback 測試（不連 Revit、不改真實的 token 檔）：在暫存資料夾假造 LOCALAPPDATA。
// 用法：node test_token_fallback.mjs
import { mkdtempSync, mkdirSync, writeFileSync, utimesSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";

const root = mkdtempSync(path.join(tmpdir(), "rmcp-token-"));
process.env.LOCALAPPDATA = root;
const t = await import("./dist/services/token.js");
const real = path.join(root, "revit-mcp");
const virt = path.join(root, "Packages", t.CLAUDE_STORE_PACKAGE_FAMILY, "LocalCache", "Local", "revit-mcp");
mkdirSync(real, { recursive: true }); mkdirSync(virt, { recursive: true });
let fail = 0, n = 0;
const check = (label, got, exp) => { n++; if (got === exp) console.log("PASS", label); else { fail++; console.log("FAIL", label, "got", got, "exp", exp); } };
const put = (dir, port, token, secAgo) => { const f = path.join(dir, `port-${port}.token`); writeFileSync(f, token); const d = new Date(Date.now() - secAgo * 1000); utimesSync(f, d, d); };
const reset = () => t.invalidateAllTokens();

put(real, 1, "realonly", 10);                 check("real only", await t.readToken(1), "realonly");
put(virt, 2, "virtonly", 10);                 check("virtual only", await t.readToken(2), "virtonly");
put(real, 3, "old-real", 600); put(virt, 3, "new-virt", 5);   check("newer virtual wins", await t.readToken(3), "new-virt");
put(real, 4, "new-real", 5);   put(virt, 4, "old-virt", 600); check("newer real wins", await t.readToken(4), "new-real");
put(real, 5, "\uFEFFbom\r\n", 5);              check("bom trimmed", await t.readToken(5), "bom");
put(real, 6, "   ", 5); put(virt, 6, "good", 600);           check("empty file skipped", await t.readToken(6), "good");
let threw = false; try { await t.readToken(7); } catch (e) { threw = /port 7/.test(e.message) && e.message.includes("LocalCache"); }
check("none -> throws with both paths", threw, true);
// 401 降級：先挑到新的（死掉的那個），invalidate 後改挑另一個
put(real, 8, "live-old", 600); put(virt, 8, "stale-new", 5);
check("401 first pick", await t.readToken(8), "stale-new");
t.invalidateToken(8);
check("401 retry picks other", await t.readToken(8), "live-old");
// 快取
put(real, 9, "first", 5); check("cache 1", await t.readToken(9), "first");
put(real, 9, "second", 1); check("cache kept", await t.readToken(9), "first");
t.invalidateToken(9); check("after invalidate rereads", await t.readToken(9), "second");
check("tie keeps real first", t.pickTokenCandidate([{ file: "r", token: "a", mtimeMs: 1 }, { file: "v", token: "b", mtimeMs: 1 }]).token, "a");
check("headers", (await t.authHeaders(1))[t.TOKEN_HEADER], "realonly");
reset(); rmSync(root, { recursive: true, force: true });
console.log(`${n - fail}/${n} passed`); process.exit(fail ? 1 : 0);
