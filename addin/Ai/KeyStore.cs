// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

namespace RevitMcpAddin.Ai
{
    /// <summary>
    /// AI 供應商 API 金鑰的儲存層（Claude／OpenAI／Gemini 各一筆）。
    /// 優先使用 Windows 認證管理員（Generic Credential，目標名稱見 AiProviders.CredTarget；Claude 沿用 revit-mcp）；
    /// 讀不到時回退到舊的純文字檔 ~/revit-mcp-apikey.txt，並自動遷移過去。
    /// 認證管理員若不可用（P/Invoke 失敗），整個流程退回純文字檔，功能不會中斷。
    /// </summary>
    internal static class KeyStore
    {
        private const uint CRED_TYPE_GENERIC = 1;
        private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

        /// <summary>舊版純文字檔，只有 Claude 金鑰用過。</summary>
        internal static readonly string LegacyFile =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         "revit-mcp-apikey.txt");

        private static readonly Dictionary<AiProvider, string> _labels = new();

        /// <summary>該供應商金鑰實際存放位置，供 UI 顯示。</summary>
        internal static string StorageLabel(AiProvider p) =>
            _labels.TryGetValue(p, out var l) ? l : "尚未載入";

        private static string LegacyPath(AiProvider p) =>
            p == AiProvider.Claude ? LegacyFile
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                           "revit-mcp-apikey-" + p.ToString().ToLowerInvariant() + ".txt");

        /// <summary>讀取金鑰。認證管理員優先，純文字檔為回退並自動遷移。</summary>
        internal static string Load(AiProvider p)
        {
            var target = AiProviders.CredTarget(p);
            try
            {
                var fromCred = CredRead(target);
                if (!string.IsNullOrEmpty(fromCred))
                {
                    _labels[p] = "Windows 認證管理員";
                    return fromCred;
                }
            }
            catch { /* 認證管理員不可用，往下走純文字檔 */ }

            string fromFile = "";
            try { if (File.Exists(LegacyPath(p))) fromFile = File.ReadAllText(LegacyPath(p)).Trim(); }
            catch { }

            if (fromFile == "")
            {
                _labels[p] = "尚未設定";
                return "";
            }

            try
            {
                if (CredWrite(p, fromFile))
                {
                    TryDeleteLegacyFile(p);
                    _labels[p] = "Windows 認證管理員（已從純文字檔遷移）";
                    return fromFile;
                }
            }
            catch { }

            _labels[p] = "純文字檔（認證管理員不可用）";
            return fromFile;
        }

        /// <summary>儲存金鑰。成功寫入認證管理員後會清掉純文字檔。失敗才退回寫檔。</summary>
        internal static void Save(AiProvider p, string key)
        {
            try
            {
                if (CredWrite(p, key))
                {
                    TryDeleteLegacyFile(p);
                    _labels[p] = "Windows 認證管理員";
                    return;
                }
            }
            catch { }

            File.WriteAllText(LegacyPath(p), key);   // 失敗就往外丟例外，由 UI 顯示
            _labels[p] = "純文字檔（認證管理員不可用）";
        }

        /// <summary>刪除金鑰：認證管理員與純文字檔都清掉。</summary>
        internal static void Delete(AiProvider p)
        {
            try { CredDelete(AiProviders.CredTarget(p), CRED_TYPE_GENERIC, 0); } catch { }
            TryDeleteLegacyFile(p);
            _labels[p] = "尚未設定";
        }

        private static void TryDeleteLegacyFile(AiProvider p)
        {
            try { if (File.Exists(LegacyPath(p))) File.Delete(LegacyPath(p)); } catch { }
        }
        // ── Windows Credential Manager P/Invoke ──────────────────────────

        private static string CredRead(string targetName)
        {
            if (!CredRead(targetName, CRED_TYPE_GENERIC, 0, out var ptr)) return "";
            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
                if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return "";
                var bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
                return Encoding.Unicode.GetString(bytes).Trim();
            }
            finally { CredFree(ptr); }
        }

        private static bool CredWrite(AiProvider p, string key)
        {
            var blob = Encoding.Unicode.GetBytes(key);
            var blobPtr = Marshal.AllocHGlobal(blob.Length);
            var targetPtr = Marshal.StringToHGlobalUni(AiProviders.CredTarget(p));
            var userPtr = Marshal.StringToHGlobalUni(p.ToString().ToUpperInvariant() + "_API_KEY");
            var commentPtr = Marshal.StringToHGlobalUni("Revit MCP Addin - " + AiProviders.KeyLabel(p));
            try
            {
                Marshal.Copy(blob, 0, blobPtr, blob.Length);
                var cred = new CREDENTIAL
                {
                    Flags = 0,
                    Type = CRED_TYPE_GENERIC,
                    TargetName = targetPtr,
                    Comment = commentPtr,
                    CredentialBlobSize = (uint)blob.Length,
                    CredentialBlob = blobPtr,
                    Persist = CRED_PERSIST_LOCAL_MACHINE,
                    AttributeCount = 0,
                    Attributes = IntPtr.Zero,
                    TargetAlias = IntPtr.Zero,
                    UserName = userPtr
                };
                return CredWrite(ref cred, 0);
            }
            finally
            {
                Array.Clear(blob, 0, blob.Length);
                Marshal.FreeHGlobal(blobPtr);
                Marshal.FreeHGlobal(targetPtr);
                Marshal.FreeHGlobal(userPtr);
                Marshal.FreeHGlobal(commentPtr);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredFree")]
        private static extern void CredFree(IntPtr buffer);
    }
}
