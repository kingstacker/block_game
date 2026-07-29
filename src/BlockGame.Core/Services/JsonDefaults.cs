using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlockGame.Core.Services;

public static class JsonDefaults
{
    public static JsonSerializerOptions Create(bool indented = false)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = indented
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

