// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

import { readFile } from "node:fs/promises";
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
 */

export const TOKEN_HEADER = "X-Revit-Mcp-Token";

/** 快取：避免每個請求都打一次磁碟。401 時由呼叫端清掉。 */
const cache = new Map<number, string>();

/** token 檔路徑；必須跟 add-in 的 RevitHttpServer.TokenFilePath 一致。 */
export function tokenFilePath(port: number): string {
  const localAppData =
    process.env.LOCALAPPDATA ?? path.join(homedir(), "AppData", "Local");
  return path.join(localAppData, "revit-mcp", `port-${port}.token`);
}

/** 丟掉某個埠的快取，下次 readToken 會重新讀檔。 */
export function invalidateToken(port: number): void {
  cache.delete(port);
}

export function invalidateAllTokens(): void {
  cache.clear();
}

/**
 * 讀取某個埠的 token。讀不到就丟錯（fail closed —— 絕不以「沒有 token」繼續發請求）。
 */
export async function readToken(port: number): Promise<string> {
  const cached = cache.get(port);
  if (cached !== undefined) return cached;

  const file = tokenFilePath(port);
  let raw: string;
  try {
    raw = await readFile(file, "utf8");
  } catch (err) {
    const code = (err as NodeJS.ErrnoException)?.code;
    throw new Error(
      `讀不到 port ${port} 的認證 token（${file}${code ? `，${code}` : ""}）。` +
        `這個檔由 Revit add-in 啟動時產生、關閉時刪除。` +
        `請確認該 Revit 正在執行且 RevitMcpAddin 已載入；` +
        `若 Revit 是開著的，請重新啟動 Revit 讓 add-in 重新產生 token。`
    );
  }

  // 去掉 BOM 與換行；add-in 寫的是無 BOM UTF-8，但容錯一下比較保險。
  const token = raw.replace(/^﻿/, "").trim();
  if (!token) {
    throw new Error(`port ${port} 的 token 檔是空的（${file}）。請重新啟動 Revit。`);
  }

  cache.set(port, token);
  return token;
}

/** 組出帶認證的 headers。 */
export async function authHeaders(port: number): Promise<Record<string, string>> {
  return { [TOKEN_HEADER]: await readToken(port) };
}
