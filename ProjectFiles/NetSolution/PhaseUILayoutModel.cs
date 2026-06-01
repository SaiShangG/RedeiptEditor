#region Using directives
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FTOptix.WebUI;
using FTOptix.EventLogger;
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
    public string UiPath { get; set; }
    public string UiProperty { get; set; }
    public string SourceTagPath { get; set; }
}

public class PhaseUILayoutItem
{
    public string Id { get; set; }
    public string WidgetType { get; set; }
    public PhaseUILayoutBindSpec Bind { get; set; }
    public List<PhaseUILayoutBindSpec> Binds { get; set; }
    public int? Width { get; set; }
    public string label { get; set; }
    public Dictionary<string, PhaseUILayoutBindSpec> Bindings { get; set; }
    public PhaseUILayoutItemConfig Config { get; set; }
}

public class PhaseUILayoutItemConfig
{
    public PhaseUILayoutConditionSelectorConfig ConditionSelector { get; set; }
}

public class PhaseUILayoutConditionSelectorConfig
{
    public List<PhaseUILayoutConditionSelectorItem> Items { get; set; }
}

public class PhaseUILayoutConditionSelectorItem
{
    public string Label { get; set; }

    [JsonConverter(typeof(FlexibleInt32JsonConverter))]
    public int Value { get; set; }

    public string Unit { get; set; }
    public Dictionary<string, PhaseUILayoutBindSpec> Bindings { get; set; }
}

/// <summary>兼容 JSON 中 value 为数字或数字字符串（前端编辑器可能写出 "4"）。</summary>
public sealed class FlexibleInt32JsonConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.GetInt32();
            case JsonTokenType.String:
                string text = reader.GetString();
                if (int.TryParse(text, out int parsed))
                    return parsed;
                throw new JsonException($"无法将字符串 \"{text}\" 转换为 Int32。");
            default:
                throw new JsonException($"无法将 {reader.TokenType} 转换为 Int32。");
        }
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
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
