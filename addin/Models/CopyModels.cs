// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Newtonsoft.Json;

namespace RevitMcpAddin.Models
{
    /// <summary>矩形範圍（mm，專案座標）。只比對 XY。</summary>
    public class BBoxDto
    {
        [JsonProperty("minX")] public double MinX { get; set; }
        [JsonProperty("minY")] public double MinY { get; set; }
        [JsonProperty("maxX")] public double MaxX { get; set; }
        [JsonProperty("maxY")] public double MaxY { get; set; }
    }

    /// <summary>跨樓層／區域複製設備的請求。</summary>
    public class CopyElementsRequest
    {
        /// <summary>直接指定來源元件 Id。給了就忽略下面的篩選條件。</summary>
        [JsonProperty("sourceIds")]       public List<long>?   SourceIds       { get; set; }
        [JsonProperty("sourceLevelName")] public string?       SourceLevelName { get; set; }
        [JsonProperty("sourceViewId")]    public long?         SourceViewId    { get; set; }
        /// <summary>類別名稱（同 /elements 的 category 對照表）。</summary>
        [JsonProperty("categories")]      public List<string>? Categories      { get; set; }
        /// <summary>族群名稱白名單（可留空 = 不限）。</summary>
        [JsonProperty("familyNames")]     public List<string>? FamilyNames     { get; set; }
        /// <summary>來源區域（mm）。只取放置點落在框內的元件。</summary>
        [JsonProperty("bbox")]            public BBoxDto?      BBox            { get; set; }

        [JsonProperty("targetLevelName")] public string        TargetLevelName { get; set; } = "";
        /// <summary>水平位移（mm）。同一塔樓對位時通常 0。</summary>
        [JsonProperty("dx")]              public double        Dx              { get; set; }
        [JsonProperty("dy")]              public double        Dy              { get; set; }
        /// <summary>面掛族群是否重新掛到目標層天花底面。</summary>
        [JsonProperty("rehostToCeiling")] public bool          RehostToCeiling { get; set; } = true;
        /// <summary>只回報會做什麼，不改模型。</summary>
        [JsonProperty("dryRun")]          public bool          DryRun          { get; set; } = true;
    }

    public class CopyGroupDto
    {
        [JsonProperty("familyName")] public string FamilyName { get; set; } = "";
        [JsonProperty("typeName")]   public string TypeName   { get; set; } = "";
        [JsonProperty("category")]   public string Category   { get; set; } = "";
        [JsonProperty("count")]      public int    Count      { get; set; }
        /// <summary>面掛（有 HostFace）的數量，需要重新掛載。</summary>
        [JsonProperty("faceHosted")] public int    FaceHosted { get; set; }
    }

    public class CopyElementsResult
    {
        [JsonProperty("dryRun")]        public bool   DryRun        { get; set; }
        [JsonProperty("sourceCount")]   public int    SourceCount   { get; set; }
        [JsonProperty("createdCount")]  public int    CreatedCount  { get; set; }
        [JsonProperty("skippedCount")]  public int    SkippedCount  { get; set; }
        [JsonProperty("sourceLevel")]   public string SourceLevel   { get; set; } = "";
        [JsonProperty("targetLevel")]   public string TargetLevel   { get; set; } = "";
        [JsonProperty("sourceBBox")]    public BBoxDto? SourceBBox  { get; set; }
        [JsonProperty("targetCeilings")] public int   TargetCeilings { get; set; }
        [JsonProperty("groups")]        public List<CopyGroupDto> Groups { get; set; } = new();
        [JsonProperty("createdIds")]    public List<long>   CreatedIds { get; set; } = new();
        [JsonProperty("warnings")]      public List<string> Warnings   { get; set; } = new();
    }
}
