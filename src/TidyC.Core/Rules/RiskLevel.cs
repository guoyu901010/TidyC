using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyC.Core.Rules;

[JsonConverter(typeof(RiskLevelConverter))]
public enum RiskLevel
{
    /// <summary>GREEN — reproducible cache / logs / dumps, safe to move to quarantine.</summary>
    Green = 0,

    /// <summary>YELLOW — re-downloadable payloads and caches that cost time to rebuild.</summary>
    Yellow = 1,

    /// <summary>RED — user data or credentials. Hard locked, never selectable.</summary>
    Red = 2,

    /// <summary>GRAY — unrecognised. Default keep, hard locked.</summary>
    Gray = 3,
}

public static class RiskLevelExtensions
{
    public static bool IsSelectable(this RiskLevel level) => level is RiskLevel.Green or RiskLevel.Yellow;

    public static string ToContractString(this RiskLevel level) => level switch
    {
        RiskLevel.Green => "GREEN",
        RiskLevel.Yellow => "YELLOW",
        RiskLevel.Red => "RED",
        RiskLevel.Gray => "GRAY",
        _ => "GRAY",
    };

    public static string ToChineseLabel(this RiskLevel level) => level switch
    {
        RiskLevel.Green => "绿 · 可安全清理",
        RiskLevel.Yellow => "黄 · 需谨慎清理",
        RiskLevel.Red => "红 · 严禁清理",
        _ => "灰 · 未识别",
    };

    public static bool TryParseContract(string? value, out RiskLevel level)
    {
        level = RiskLevel.Gray;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToUpperInvariant())
        {
            case "GREEN":
            case "绿":
                level = RiskLevel.Green;
                return true;
            case "YELLOW":
            case "黄":
                level = RiskLevel.Yellow;
                return true;
            case "RED":
            case "红":
                level = RiskLevel.Red;
                return true;
            case "GRAY":
            case "GREY":
            case "灰":
                level = RiskLevel.Gray;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>Serialises the four-level risk as the contract strings GREEN/YELLOW/RED/GRAY.</summary>
public sealed class RiskLevelConverter : JsonConverter<RiskLevel>
{
    public override RiskLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int numeric))
        {
            return (RiskLevel)numeric;
        }

        string? text = reader.GetString();
        return RiskLevelExtensions.TryParseContract(text, out RiskLevel level) ? level : RiskLevel.Gray;
    }

    public override void Write(Utf8JsonWriter writer, RiskLevel value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToContractString());
    }
}
