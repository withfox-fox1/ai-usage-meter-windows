using System.Globalization;
using System.Text.Json;

namespace AIUsageMeter.Core;

/// <summary>
/// 型が想定と異なる項目があっても例外にせず null を返す JSON 読み取りヘルパー。
/// </summary>
internal static class JsonUtil
{
    public static JsonDocument Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new UsageParseException("応答が空です");
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new UsageParseException("応答が JSON ではありません", ex);
        }
    }

    public static bool TryGetObject(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Object;
    }

    public static bool TryGetArray(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Array;
    }

    public static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static double? GetNumber(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value))
        {
            return null;
        }

        double number;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetDouble(out number):
                break;
            case JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number):
                break;
            default:
                return null;
        }

        return double.IsFinite(number) ? number : null;
    }

    /// <summary>0〜100 の使用率。範囲外は丸める。</summary>
    public static double? GetPercent(JsonElement obj, string name) =>
        GetNumber(obj, name) is double value ? Math.Clamp(value, 0, 100) : null;

    /// <summary>ISO 8601 文字列または epoch（秒・ミリ秒）を読み取る。</summary>
    public static DateTimeOffset? GetTimestamp(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var epoch))
        {
            return FromEpoch(epoch);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
            {
                return FromEpoch(numeric);
            }

            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static DateTimeOffset? FromEpoch(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            return null;
        }

        // 1e11 秒は西暦 5000 年超のため、それ以上はミリ秒とみなす。
        var seconds = value > 1e11 ? value / 1000 : value;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
