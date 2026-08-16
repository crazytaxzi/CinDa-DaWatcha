using System.Text.Json;
using System.Text.Json.Serialization;

namespace CinDa.DaWatcha.Core;

public static class StrictJson
{
    public static void RejectDuplicateProperties(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        var objectProperties = new Stack<HashSet<string>>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectProperties.Push(new HashSet<string>(
                        StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    objectProperties.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (objectProperties.Count == 0)
                        throw new JsonException("Property appeared outside an object.");
                    var property = reader.GetString() ?? "";
                    if (!objectProperties.Peek().Add(property))
                        throw new JsonException(
                            $"Duplicate JSON property '{property}' is forbidden.");
                    break;
            }
        }
    }
}

public sealed class StrictStringEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(
        ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"{typeof(TEnum).Name} must be a string.");
        var value = reader.GetString();
        var name = Enum.GetNames<TEnum>().FirstOrDefault(candidate =>
            candidate.Equals(value, StringComparison.Ordinal));
        if (name is null)
            throw new JsonException(
                $"'{value}' is not an exact {typeof(TEnum).Name} value.");
        return Enum.Parse<TEnum>(name, ignoreCase: false);
    }

    public override void Write(
        Utf8JsonWriter writer, TEnum value,
        JsonSerializerOptions options)
    {
        var name = Enum.GetName(value)
            ?? throw new JsonException(
                $"{value} is not a defined {typeof(TEnum).Name} value.");
        writer.WriteStringValue(name);
    }
}
