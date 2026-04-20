#region Using directives
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
#endregion

#region JSON 配置（与 phase_ui_layout.sample.json 一致）
public class PhaseUILayoutRoot
{
    public int Version { get; set; }
    public string Description { get; set; }
    public List<PhaseUILayoutSection> Sections { get; set; }
}

public class PhaseUILayoutSection
{
    public string Id { get; set; }
    public string PanelType { get; set; }
    public string Title { get; set; }
    public string RowLayoutPath { get; set; }
    public float? RowLayoutHorizontalGap { get; set; }
    [JsonPropertyName("rowHorizontalGap")]
    public float? RowHorizontalGap { get; set; }
    public int? SectionVerticalGap { get; set; }
    public List<PhaseUILayoutItem> Items { get; set; }
}

/// <summary>与 UDT_Phase 实例上字段的绑定说明；一维数组需指定 <see cref="Index"/>。</summary>
public class PhaseUILayoutBindSpec
{
    public string BufferField { get; set; }
    public string ValueRank { get; set; }
    public int? Index { get; set; }
    public string UiProperty { get; set; }
    public string SourceTagPath { get; set; }
}

public class PhaseUILayoutItem
{
    public string Id { get; set; }
    public string WidgetType { get; set; }
    public PhaseUILayoutBindSpec Bind { get; set; }
    public int? Width { get; set; }
    public string label { get; set; }
}

/// <summary>相位 UI 布局 JSON 的加载与按文件名查找（与 ResourceUri 解析无关）。</summary>
public static class PhaseUILayoutModel
{
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static PhaseUILayoutRoot TryLoadFromFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<PhaseUILayoutRoot>(json, Options);
    }

    /// <summary>与 NetLogic 程序集同目录、当前目录按文件名查找 JSON。</summary>
    public static string ResolveLayoutPath(string fileName = "phase_ui_layout.sample.json")
    {
        try
        {
            string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(dir))
            {
                string p = Path.Combine(dir, fileName);
                if (File.Exists(p)) return p;
            }
        }
        catch { }
        try
        {
            string p2 = Path.Combine(Directory.GetCurrentDirectory(), fileName);
            if (File.Exists(p2)) return p2;
        }
        catch { }
        return null;
    }
}
#endregion
