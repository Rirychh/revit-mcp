// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Autodesk.Revit.DB;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

namespace RevitMcpAddin.Handlers
{
    /// <summary>工作共用：與中央模型同步。</summary>
    public class SyncHandler
    {
        private readonly Document _doc;
        public SyncHandler(Document doc) => _doc = doc;

        private static string Ok(object data) => JsonConvert.SerializeObject(new { success = true, data, error = (string?)null });
        private static string Fail(string msg) => JsonConvert.SerializeObject(new { success = false, data = (object?)null, error = msg });

        // GET /sync/status
        public string Status()
        {
            if (!_doc.IsWorkshared) return Ok(new { workshared = false });
            return Ok(new
            {
                workshared = true,
                central = _doc.GetWorksharingCentralModelPath() is ModelPath mp
                          ? ModelPathUtils.ConvertModelPathToUserVisiblePath(mp) : "",
                title = _doc.Title,
                username = _doc.Application.Username,
                hasUnsavedChanges = _doc.IsModified
            });
        }

        // POST /sync
        // { comment?:"", relinquish?:true, compact?:false, saveLocalBefore?:true, saveLocalAfter?:true, apply:false }
        public string Sync(string body)
        {
            var r = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
            bool apply = (bool?)r["apply"] ?? false;
            string comment = (string?)r["comment"] ?? "";
            bool relinquish = (bool?)r["relinquish"] ?? true;
            bool compact = (bool?)r["compact"] ?? false;
            bool saveBefore = (bool?)r["saveLocalBefore"] ?? true;
            bool saveAfter = (bool?)r["saveLocalAfter"] ?? true;

            if (!_doc.IsWorkshared) return Fail("這份模型不是工作共用模型，無法同步。");

            string centralPath = _doc.GetWorksharingCentralModelPath() is ModelPath mp
                ? ModelPathUtils.ConvertModelPathToUserVisiblePath(mp) : "";

            if (!apply)
                return Ok(new
                {
                    preflight = true,
                    central = centralPath,
                    title = _doc.Title,
                    isModified = _doc.IsModified,
                    willRelinquish = relinquish,
                    comment
                });

            try
            {
                var two = new TransactWithCentralOptions();
                var swc = new SynchronizeWithCentralOptions();
                swc.Comment = comment;
                swc.Compact = compact;
                swc.SaveLocalBefore = saveBefore;
                swc.SaveLocalAfter = saveAfter;

                var rel = new RelinquishOptions(false);
                if (relinquish)
                {
                    rel.StandardWorksets = true;
                    rel.ViewWorksets = true;
                    rel.FamilyWorksets = true;
                    rel.UserWorksets = true;
                    rel.CheckedOutElements = true;
                }
                swc.SetRelinquishOptions(rel);

                _doc.SynchronizeWithCentral(two, swc);

                return Ok(new { applied = true, central = centralPath, comment, relinquished = relinquish });
            }
            catch (Exception ex)
            {
                return Fail("同步失敗：" + ex.Message);
            }
        }
    }
}