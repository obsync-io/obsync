using System.Text.Json;
using System.Text.Json.Serialization;

namespace Obsync.Data;

/// <summary>Shared JSON settings for serializing complex columns (selection, schedule, etc.).</summary>
internal static class ObsyncJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) where T : new() =>
        string.IsNullOrWhiteSpace(json) ? new T() : JsonSerializer.Deserialize<T>(json, Options) ?? new T();
}
