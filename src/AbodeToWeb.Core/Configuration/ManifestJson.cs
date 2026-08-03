using System.Text.Json;
using System.Text.Json.Serialization;

namespace AbodeToWeb.Core.Configuration;

public interface IManifestParser
{
    AbodeToWebManifest Parse(string content);
}

public sealed class ManifestFormatException : Exception
{
    public ManifestFormatException(string message, string? path = null, long? lineNumber = null, long? bytePosition = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Path = path;
        LineNumber = lineNumber;
        BytePosition = bytePosition;
    }

    public string? Path { get; }

    public long? LineNumber { get; }

    public long? BytePosition { get; }
}

public sealed class JsonManifestParser : IManifestParser
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public AbodeToWebManifest Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ManifestFormatException("Manifest is empty.", "$");
        }

        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            RejectDuplicateProperties(document.RootElement, "$");
            return JsonSerializer.Deserialize<AbodeToWebManifest>(content, Options)
                ?? throw new ManifestFormatException("Manifest must contain a JSON object.", "$");
        }
        catch (ManifestFormatException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            var path = string.IsNullOrEmpty(exception.Path) ? "$" : exception.Path;
            throw new ManifestFormatException(
                $"Manifest JSON is invalid at {path}: {exception.Message}",
                path,
                exception.LineNumber,
                exception.BytePositionInLine,
                exception);
        }
    }

    public static JsonSerializerOptions CreateSerializerOptions() => new(Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        return options;
    }

    private static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = path == "$" ? $"$.{property.Name}" : $"{path}.{property.Name}";
                if (!names.Add(property.Name))
                {
                    throw new ManifestFormatException($"Duplicate property '{property.Name}' is not allowed at {path}.", propertyPath);
                }

                RejectDuplicateProperties(property.Value, propertyPath);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index++}]");
            }
        }
    }

}
