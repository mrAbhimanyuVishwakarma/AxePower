using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxePower.Core.Models;

[JsonConverter(typeof(AppThemeJsonConverter))]
public enum AppTheme
{
    System,
    Dark,
    Light,
    Glass
}

public class AppThemeJsonConverter : JsonConverter<AppTheme>
{
    public override AppTheme Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (string.Equals(str, "Transparent", StringComparison.OrdinalIgnoreCase))
            {
                return AppTheme.Glass;
            }

            if (Enum.TryParse<AppTheme>(str, true, out var theme))
            {
                return theme;
            }
        }
        else if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var intVal))
        {
            if (Enum.IsDefined(typeof(AppTheme), intVal))
            {
                return (AppTheme)intVal;
            }
        }

        return AppTheme.System;
    }

    public override void Write(Utf8JsonWriter writer, AppTheme value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
