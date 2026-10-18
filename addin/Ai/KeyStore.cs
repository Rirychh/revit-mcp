// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RevitMcpAddin.Ai
{
    /// <summary>
    /// Anthropic API 金鑰的儲存層。
    /// 優先使用 Windows 認證管理員（Generic Credential，目標名稱 revit-mcp）；
    /// 讀不到時回退到舊的純文字檔 ~/revit-mcp-apikey.txt，並自動遷移過去。
    /// 認證管理員若不可用（P/Invoke 失敗），整個流程退回純文字檔，功能不會中斷。
    /// </summary>
    internal static class KeyStore
    {
        private const string TargetName = "revit-mcp";
        private const uint CRED_TYPE_GENERIC = 1;
        private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

        internal static readonly string LegacyFile =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         "revit-mcp-apikey.txt");

        /// <summary>目前金鑰實際存放位置，供 UI 顯示。</summary>
        internal static string StorageLabel { get; private set; } = "尚未載入";

        /// <summary>讀取金鑰。認證管理員優先，舊純文字檔為回退並自動遷移。</summary>
        internal static string Load()
        {
            try
            {
                var fromCred = CredRead();
                if (!string.IsNullOrEmpty(fromCred))
                {
                    StorageLabel = "Windows 認證管理員";
                    return fromCred;
                }
            }
            catch { /* 認證管理員不可用，往下走純文字檔 */ }

            string fromFile = "";
            try { if (File.Exists(LegacyFile)) fromFile = File.ReadAllText(LegacyFile).Trim(); }
            catch { }

            if (fromFile == "")
            {
                StorageLabel = "尚未設定";
                return "";
            }

            // 舊金鑰自動遷移到認證管理員；成功就刪掉純文字檔
            try
            {
                if (CredWrite(fromFile))
                {
                    TryDeleteLegacyFile();
                    StorageLabel = "Windows 認證管理員（已從純文字檔遷移）";
                    return fromFile;
                }
            }
            catch { }

            StorageLabel = "純文字檔（認證管理員不可用）";
            return fromFile;
        }

        /// <summary>儲存金鑰。成功寫入認證管理員後會清掉純文字檔。失敗才退回寫檔。</summary>
        internal static void Save(string key)
        {
            try
            {
                if (CredWrite(key))
                {
                    TryDeleteLegacyFile();
                    StorageLabel = "Windows 認證管理員";
                    return;
                }
            }
            catch { }

            File.WriteAllText(LegacyFile, key);   // 失敗就往外丟例外，由 UI 顯示
            StorageLabel = "純文字檔（認證管理員不可用）";
        }

        /// <summary>刪除金鑰：認證管理員與純文字檔都清掉。</summary>
        internal static void Delete()
        {
            try { CredDelete(TargetName, CRED_TYPE_GENERIC, 0); } catch { }
            TryDeleteLegacyFile();
            StorageLabel = "尚未設定";
        }

        private static void TryDeleteLegacyFile()
        {
            try { if (File.Exists(LegacyFile)) File.Delete(LegacyFile); } catch { }
        }

        // ── Windows Credential Manager P/Invoke ──────────────────────────

        private static string CredRead()
        {
            if (!CredRead(TargetName, CRED_TYPE_GENERIC, 0, out var ptr)) return "";
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

        private static bool CredWrite(string key)
        {
            var blob = Encoding.Unicode.GetBytes(key);
            var blobPtr = Marshal.AllocHGlobal(blob.Length);
            var targetPtr = Marshal.StringToHGlobalUni(TargetName);
            var userPtr = Marshal.StringToHGlobalUni("ANTHROPIC_API_KEY");
            var commentPtr = Marshal.StringToHGlobalUni("Revit MCP Addin - Anthropic API key");
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
