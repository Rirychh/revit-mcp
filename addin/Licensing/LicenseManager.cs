// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace RevitMcpAddin.Licensing
{
    public sealed class LicenseInfo
    {
        public string Id = "", Name = "", User = "", Host = "", Expires = "", RevokedPath = "";
    }

    /// <summary>
    /// 授權驗證。授權碼 = "RMCP1." + base64url(payload JSON) + "." + base64url(ECDSA P-256 簽章)，
    /// 由發行者私鑰簽出，這裡只內嵌公鑰。payload 綁 Windows 帳號、電腦（MachineGuid 雜湊）、到期日。
    /// 撤銷名單：payload.r 指向網路共用資料夾上的 revoked.json（同一把私鑰簽章），背景讀取並快取到本機。
    /// 檔案：%APPDATA%\Rirychh\RevitMCP\license.key、revoked.cache
    /// </summary>
    public static class LicenseManager
    {
        // 發行者公鑰（license-tool\public-key.txt）
        // 發佈版不含公鑰：請執行 license-tool\授權管理.bat 的 init，把產生的 public-key.txt 內容填入下面兩行。
        private const string PubX = "";
        private const string PubY = "";

        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rirychh", "RevitMCP");
        public static string LicenseFile => Path.Combine(Folder, "license.key");
        private static string RevCache => Path.Combine(Folder, "revoked.cache");

        private static readonly object Lock = new();
        private static DateTime _stamp = DateTime.MinValue, _checkedDay = DateTime.MinValue;
        private static bool _valid;
        private static string _msg = "尚未檢查授權。";
        private static LicenseInfo? _info;
        private static HashSet<string> _revoked = new();

        public static bool IsValid { get { Refresh(); return _valid; } }
        public static string Message { get { Refresh(); return _msg; } }
        public static LicenseInfo? Info { get { Refresh(); return _info; } }

        public static string CurrentUser => (Environment.UserDomainName + "\\" + Environment.UserName).ToLowerInvariant();

        public static string MachineHash
        {
            get
            {
                string guid = "";
                try
                {
                    using var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                    using var k = hk.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                    guid = (k?.GetValue("MachineGuid") as string ?? "").ToLowerInvariant();
                }
                catch { }
                using var sha = SHA256.Create();
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(guid));
                return BitConverter.ToString(h, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>授權檔有變動或跨日就重新驗證（檢查到期日）。</summary>
        public static void Refresh(bool force = false)
        {
            lock (Lock)
            {
                var stamp = File.Exists(LicenseFile) ? File.GetLastWriteTimeUtc(LicenseFile) : DateTime.MinValue;
                if (!force && stamp == _stamp && _checkedDay == DateTime.Today) return;
                _stamp = stamp; _checkedDay = DateTime.Today;
                LoadRevokedCache();
                Evaluate();
            }
        }

        private static void Fail(string m) { _valid = false; _msg = m; }

        private static void Evaluate()
        {
            _info = null;
            if (!File.Exists(LicenseFile)) { Fail("找不到授權碼，請執行 Revit MCP 安裝程式並輸入授權碼。"); return; }
            string code;
            try { code = File.ReadAllText(LicenseFile).Trim(); } catch (Exception ex) { Fail("讀不到授權檔：" + ex.Message); return; }
            if (!TryVerify(code, out var p, out var err)) { Fail(err); return; }

            var info = new LicenseInfo
            {
                Id = (string?)p["id"] ?? "", Name = (string?)p["n"] ?? "", User = ((string?)p["u"] ?? "").ToLowerInvariant(),
                Host = (string?)p["h"] ?? "", Expires = (string?)p["exp"] ?? "", RevokedPath = (string?)p["r"] ?? "",
            };
            _info = info;

            if (info.User != CurrentUser) { Fail($"這組授權碼屬於帳號 {info.User}，目前登入的是 {CurrentUser}。"); return; }
            if (((string?)p["m"] ?? "").ToLowerInvariant() != MachineHash) { Fail($"這組授權碼綁定的是電腦 {info.Host}，不是這台 {Environment.MachineName}。"); return; }
            if (!DateTime.TryParse(info.Expires, out var exp)) { Fail("授權碼到期日格式錯誤。"); return; }
            if (DateTime.Today > exp.Date) { Fail($"授權已於 {info.Expires} 到期，請向發行者申請新的授權碼。"); return; }
            if (_revoked.Contains(info.Id)) { Fail($"授權 {info.Id} 已被停用。"); return; }

            _valid = true;
            int days = (exp.Date - DateTime.Today).Days;
            _msg = $"已授權：{info.Name}，到期 {info.Expires}" + (days <= 14 ? $"（剩 {days} 天）" : "");
        }

        // ── 簽章 ──────────────────────────────────────────
        private static byte[] FromB64U(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
            return Convert.FromBase64String(s);
        }

        private static bool VerifySig(string signedPart, string sigB64U)
        {
            if (string.IsNullOrEmpty(PubX) || string.IsNullOrEmpty(PubY)) throw new InvalidOperationException("尚未設定發行者公鑰（見 LicenseManager.cs 的 PubX／PubY）。");
            var prm = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = Convert.FromBase64String(PubX), Y = Convert.FromBase64String(PubY) },
            };
            using var ec = ECDsa.Create(prm);
            return ec.VerifyData(Encoding.ASCII.GetBytes(signedPart), FromB64U(sigB64U), HashAlgorithmName.SHA256);
        }

        public static bool TryVerify(string code, out JObject payload, out string error)
        {
            payload = new JObject(); error = "";
            var parts = code.Trim().Split('.');
            if (parts.Length != 3 || parts[0] != "RMCP1") { error = "授權碼格式不正確。"; return false; }
            try
            {
                if (!VerifySig(parts[1], parts[2])) { error = "授權碼簽章不符（可能複製不完整或被修改）。"; return false; }
                payload = JObject.Parse(Encoding.UTF8.GetString(FromB64U(parts[1])));
                return true;
            }
            catch (Exception ex) { error = "授權碼無法解析：" + ex.Message; return false; }
        }

        // ── 撤銷名單 ───────────────────────────────────────
        private static bool TryParseRevoked(string doc, out HashSet<string> ids)
        {
            ids = new HashSet<string>();
            try
            {
                var o = JObject.Parse(doc);
                var p64 = (string?)o["payload"] ?? ""; var sig = (string?)o["sig"] ?? "";
                if (!VerifySig(p64, sig)) return false;
                var p = JObject.Parse(Encoding.UTF8.GetString(FromB64U(p64)));
                foreach (var t in (p["revoked"] as JArray) ?? new JArray()) ids.Add((string)t!);
                return true;
            }
            catch { return false; }
        }

        private static void LoadRevokedCache()
        {
            try
            {
                if (File.Exists(RevCache) && TryParseRevoked(File.ReadAllText(RevCache), out var ids)) _revoked = ids;
            }
            catch { }
        }

        /// <summary>背景讀 NAS 撤銷名單（最多等 8 秒，連不到就沿用快取），不阻塞 Revit 啟動。</summary>
        public static void StartRevocationCheck()
        {
            var path = Info?.RevokedPath;
            if (string.IsNullOrWhiteSpace(path)) return;
            Task.Run(() =>
            {
                try
                {
                    var read = Task.Run(() => File.ReadAllText(path!));
                    if (!read.Wait(8000) || read.IsFaulted) return;
                    if (!TryParseRevoked(read.Result, out var ids)) return;
                    Directory.CreateDirectory(Folder);
                    File.WriteAllText(RevCache, read.Result);
                    lock (Lock) { _revoked = ids; Evaluate(); }
                }
                catch { }
            });
        }
    }
}
