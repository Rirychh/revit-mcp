// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { readFile, stat } from "node:fs/promises";
import { homedir } from "node:os";
import path from "node:path";

/**
 * Add-in 的認證 token。
 *
 * Revit add-in 每次啟動會產生一組隨機 token，寫到
 *   %LOCALAPPDATA%\revit-mcp\port-{port}.token
 * 並要求每個 HTTP 請求都帶 X-Revit-Mcp-Token。多個 Revit 實例各佔一個埠，
 * 各自一個 token 檔，所以這裡以 port 為 key 分開快取。
 *
 * Add-in 重啟後 token 會換，舊的快取就失效了 —— 呼叫端收到 401 時要
 * invalidateToken(port) 再重讀一次並重試（見 revitClient.ts）。
 *
 * 商店版（MSIX）Claude 的轉向：由商店版 Claude（例如它的 PowerShell 工具）開的 Revit，
 * 寫 %LOCALAPPDATA% 會被 Windows 轉到
 *   %LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\revit-mcp\port-{port}.token
 * 而這支 MCP server 不在套件裡，只看得到真實路徑。所以兩個位置都要找。
 *
 * 兩個位置都有同一個埠的檔時，挑「最新寫入」的那個：同一個埠同時只會有一個 Revit 在聽，
 * 活著的那個一定是最後啟動、最後寫檔的；另一個是先前關掉或當掉的 Revit 留下的。
 * 萬一挑錯（收到 401），invalidateToken 會把那組 token 降到最後，重試時改用另一個檔。
 */

export const TOKEN_HEADER = "X-Revit-Mcp-Token";

/** 商店版 Claude 的套件系列名稱（轉向資料夾的名字）。 */
export const CLAUDE_STORE_PACKAGE_FAMILY = "Claude_pzs8sxrjxfjjc";

/** 快取：避免每個請求都打一次磁碟。401 時由呼叫端清掉。 */
const cache = new Map<number, string>();

/** 被 invalidateToken 丟掉過的 token（多半是被 add-in 401 拒絕的），挑檔時排在最後。 */
const demoted = new Map<number, Set<string>>();

function localAppDataDir(): string {
  return process.env.LOCALAPPDATA ?? path.join(homedir(), "AppData", "Local");
}

/** token 檔路徑；必須跟 add-in 的 RevitHttpServer.TokenFilePath 一致。 */
export function tokenFilePath(port: number): string {
  return path.join(localAppDataDir(), "revit-mcp", `port-${port}.token`);
}

/** 商店版 Claude 開的 Revit 被轉向後實際寫入的位置。 */
export function virtualTokenFilePath(port: number): string {
  return path.join(
    localAppDataDir(),
    "Packages",
    CLAUDE_STORE_PACKAGE_FAMILY,
    "LocalCache",
    "Local",
    "revit-mcp",
    `port-${port}.token`
  );
}

/** 依序要找的 token 檔（真實位置在前）。 */
export function tokenFileCandidates(port: number): string[] {
  return [tokenFilePath(port), virtualTokenFilePath(port)];
}

/** 丟掉某個埠的快取，下次 readToken 會重新讀檔；丟掉的那組 token 之後挑檔時排在最後。 */
export function invalidateToken(port: number): void {
  const t = cache.get(port);
  if (t !== undefined) {
    let set = demoted.get(port);
    if (!set) {
      set = new Set<string>();
      demoted.set(port, set);
    }
    set.add(t);
    // 只留最近幾組，避免無限長大（token 每次啟動都是新的亂數，舊的不會再出現）
    while (set.size > 8) set.delete(set.values().next().value as string);
  }
  cache.delete(port);
}

export function invalidateAllTokens(): void {
  cache.clear();
}

export interface TokenCandidate {
  file: string;
  token: string;
  mtimeMs: number;
}

/**
 * 從多個候選檔挑一個：沒被降級的優先，再來是最新寫入的；都一樣時保留原本順序（真實位置優先）。
 * 匯出只為了測試。
 */
export function pickTokenCandidate(
  found: TokenCandidate[],
  demotedTokens?: ReadonlySet<string>
): TokenCandidate | undefined {
  const ranked = found
    .map((c, i) => ({ c, i, bad: demotedTokens?.has(c.token) ? 1 : 0 }))
    .sort((a, b) => a.bad - b.bad || b.c.mtimeMs - a.c.mtimeMs || a.i - b.i);
  return ranked[0]?.c;
}

async function readCandidate(file: string): Promise<TokenCandidate | { file: string; error: string }> {
  try {
    const [raw, st] = await Promise.all([readFile(file, "utf8"), stat(file)]);
    // 去掉 BOM 與換行；add-in 寫的是無 BOM UTF-8，但容錯一下比較保險。
    const token = raw.replace(/^\uFEFF/, "").trim();
    if (!token) return { file, error: "檔案是空的" };
    return { file, token, mtimeMs: st.mtimeMs };
  } catch (err) {
    const code = (err as NodeJS.ErrnoException)?.code;
    return { file, error: code ?? String(err) };
  }
}

/**
 * 讀取某個埠的 token。讀不到就丟錯（fail closed —— 絕不以「沒有 token」繼續發請求）。
 */
export async function readToken(port: number): Promise<string> {
  const cached = cache.get(port);
  if (cached !== undefined) return cached;

  const results = await Promise.all(tokenFileCandidates(port).map(readCandidate));
  const found = results.filter((r): r is TokenCandidate => "token" in r);
  const picked = pickTokenCandidate(found, demoted.get(port));
  if (!picked) {
    const detail = results.map((r) => `${r.file}${"error" in r ? `，${r.error}` : ""}`).join("；");
    throw new Error(
      `讀不到 port ${port} 的認證 token（${detail}）。` +
        `這個檔由 Revit add-in 啟動時產生、關閉時刪除。` +
        `請確認該 Revit 正在執行且 RevitMcpAddin 已載入；` +
        `若 Revit 是開著的，請重新啟動 Revit 讓 add-in 重新產生 token。`
    );
  }

  cache.set(port, picked.token);
  return picked.token;
}

/** 組出帶認證的 headers。 */
export async function authHeaders(port: number): Promise<Record<string, string>> {
  return { [TOKEN_HEADER]: await readToken(port) };
}
