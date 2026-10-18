// Revit MCP — © 2026 Rita (Rirychh)
// https://github.com/Rirychh/revit-mcp
// SPDX-License-Identifier: MIT

using Newtonsoft.Json;

namespace RevitMcpAddin.Models
{
    // ── 跨連結的構件查詢（結構梁、柱、樓板…）──────────────
    // 所有座標都已換成主模型座標，單位 mm。
    public class XyzMm
    {
        [JsonProperty("x")] public double X { get; set; }
        [JsonProperty("y")] public double Y { get; set; }
        [JsonProperty("z")] public double Z { get; set; }
    }

    public class LinkedElementDto
    {
        [JsonProperty("id")]           public long    Id           { get; set; }
        [JsonProperty("source")]       public string  Source       { get; set; } = "";
        [JsonProperty("linkInstanceId")] public long  LinkInstanceId { get; set; }
        [JsonProperty("category")]     public string  Category     { get; set; } = "";
        [JsonProperty("familyName")]   public string  FamilyName   { get; set; } = "";
        [JsonProperty("typeName")]     public string  TypeName     { get; set; } = "";
        [JsonProperty("mark")]         public string  Mark         { get; set; } = "";
        [JsonProperty("levelName")]    public string  LevelName    { get; set; } = "";
        /// <summary>外框（主模型座標，mm）。梁底 = bboxMin.z，梁頂 = bboxMax.z。</summary>
        [JsonProperty("bboxMin")]      public XyzMm   BboxMin      { get; set; } = new();
        [JsonProperty("bboxMax")]      public XyzMm   BboxMax      { get; set; } = new();
        /// <summary>線型構件（梁）的定位線起訖點；點型構件只有 start。</summary>
        [JsonProperty("start")]        public XyzMm?  Start        { get; set; }
        [JsonProperty("end")]          public XyzMm?  End          { get; set; }
        [JsonProperty("parameters")]   public Dictionary<string, string> Parameters { get; set; } = new();
    }

    public class LinkedElementsResult
    {
        [JsonProperty("category")]        public string                 Category        { get; set; } = "";
        [JsonProperty("total")]           public int                    Total           { get; set; }
        [JsonProperty("count")]           public int                    Count           { get; set; }
        [JsonProperty("offset")]          public int                    Offset          { get; set; }
        [JsonProperty("hasMore")]         public bool                   HasMore         { get; set; }
        [JsonProperty("searchedSources")] public List<string>           SearchedSources { get; set; } = new();
        [JsonProperty("unloadedLinks")]   public List<string>           UnloadedLinks   { get; set; } = new();
        [JsonProperty("elements")]        public List<LinkedElementDto> Elements        { get; set; } = new();
    }
}
