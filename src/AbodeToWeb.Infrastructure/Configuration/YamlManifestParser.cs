using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AbodeToWeb.Core.Configuration;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace AbodeToWeb.Infrastructure.Configuration;

public sealed class YamlManifestParser : IManifestParser
{
    private readonly JsonManifestParser _jsonParser = new();

    public AbodeToWebManifest Parse(string content)
        => Expand(ParseUnexpanded(content));

    public AbodeToWebManifest ParseUnexpanded(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new ManifestFormatException("Manifest is empty.", "$");
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(content);
            stream.Load(reader);
            if (stream.Documents.Count != 1)
                throw new ManifestFormatException("Manifest must contain exactly one YAML document.", "$");

            var node = ConvertNode(stream.Documents[0].RootNode, "$", 0);
            if (node is not JsonObject root)
                throw new ManifestFormatException("Manifest must contain a YAML object at its root.", "$");

            return _jsonParser.Parse(root.ToJsonString(JsonManifestParser.CreateSerializerOptions()));
        }
        catch (ManifestFormatException) { throw; }
        catch (YamlException exception)
        {
            throw new ManifestFormatException(
                $"Manifest YAML is invalid near line {exception.Start.Line}, column {exception.Start.Column}: {exception.Message}",
                "$", exception.Start.Line, exception.Start.Column, exception);
        }
        catch (ArgumentException exception) when (exception.Message.Contains("same key", StringComparison.OrdinalIgnoreCase)
                                                  || exception.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            throw new ManifestFormatException("Manifest contains a duplicate YAML property.", "$", innerException: exception);
        }
    }

    public AbodeToWebManifest Expand(AbodeToWebManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var root = JsonSerializer.SerializeToNode(manifest, JsonManifestParser.CreateSerializerOptions()) as JsonObject
            ?? throw new ManifestFormatException("Manifest must contain a YAML object at its root.", "$");
        ExpandEnvironment(root);
        return _jsonParser.Parse(root.ToJsonString(JsonManifestParser.CreateSerializerOptions()));
    }

    private static JsonNode? ConvertNode(YamlNode node, string path, int depth)
    {
        if (depth > 64) throw new ManifestFormatException("Manifest nesting exceeds the maximum depth of 64.", path);
        return node switch
        {
            YamlMappingNode mapping => ConvertMapping(mapping, path, depth),
            YamlSequenceNode sequence => ConvertSequence(sequence, path, depth),
            YamlScalarNode scalar => ConvertScalar(scalar),
            _ => throw new ManifestFormatException($"Unsupported YAML node at {path}.", path)
        };
    }

    private static JsonObject ConvertMapping(YamlMappingNode mapping, string path, int depth)
    {
        var result = new JsonObject();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode key || string.IsNullOrWhiteSpace(key.Value))
                throw new ManifestFormatException($"Object keys must be non-empty strings at {path}.", path);
            var name = key.Value;
            if (!names.Add(name)) throw new ManifestFormatException($"Duplicate property '{name}' is not allowed at {path}.", path + "." + name);
            result[name] = ConvertNode(pair.Value, path + "." + name, depth + 1);
        }
        return result;
    }

    private static JsonArray ConvertSequence(YamlSequenceNode sequence, string path, int depth)
    {
        var result = new JsonArray();
        for (var index = 0; index < sequence.Children.Count; index++)
            result.Add(ConvertNode(sequence.Children[index], $"{path}[{index}]", depth + 1));
        return result;
    }

    private static JsonValue? ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (value is null) return null;
        if (scalar.Style != ScalarStyle.Plain) return JsonValue.Create(value);

        if (bool.TryParse(value, out var boolean)) return JsonValue.Create(boolean);
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return JsonValue.Create(integer);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatingPoint)
            && value.ContainsAny('.', 'e', 'E')) return JsonValue.Create(floatingPoint);
        if (value is "null" or "Null" or "NULL" or "~") return null;
        return JsonValue.Create(value);
    }

    private static void ExpandEnvironment(JsonObject root)
    {
        var variables = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(entry => entry.Key is string && entry.Value is string)
            .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        if (root["abodeToWeb"] is JsonObject abodeToWeb
            && abodeToWeb["projectsRoots"] is JsonArray roots
            && roots.FirstOrDefault() is JsonValue firstRoot
            && firstRoot.TryGetValue<string>(out var rootValue))
        {
            var expandedRoot = EnvironmentVariableExpander.Expand(rootValue, variables);
            if (expandedRoot.IsSuccess && expandedRoot.Value is not null)
                variables["PROJECTS_ROOT"] = expandedRoot.Value;

            ExpandStringArray(roots, variables, "$.abodeToWeb.projectsRoots");
        }

        if (root["apps"] is not JsonArray apps) return;
        for (var index = 0; index < apps.Count; index++)
        {
            if (apps[index] is not JsonObject app
                || app["repository"] is not JsonObject repository) continue;
            ExpandStringProperty(repository, "localPath", variables, $"$.apps[{index}].repository.localPath");
        }
    }

    private static void ExpandStringArray(JsonArray values, IReadOnlyDictionary<string, string> variables, string path)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;
            values[index] = Expand(text, variables, $"{path}[{index}]");
        }
    }

    private static void ExpandStringProperty(JsonObject objectNode, string propertyName,
        IReadOnlyDictionary<string, string> variables, string path)
    {
        if (objectNode[propertyName] is not JsonValue value || !value.TryGetValue<string>(out var text)) return;
        objectNode[propertyName] = Expand(text, variables, path);
    }

    private static string Expand(string text, IReadOnlyDictionary<string, string> variables, string path)
    {
        var expansion = EnvironmentVariableExpander.Expand(text, variables);
        if (!expansion.IsSuccess)
            throw new ManifestFormatException($"Unknown environment variable(s) {string.Join(", ", expansion.MissingVariables)} at {path}.", path);
        return expansion.Value!;
    }
}

internal static class StringCharacterExtensions
{
    public static bool ContainsAny(this string value, params char[] characters) => characters.Any(value.Contains);
}
