using System.Text.Json;

namespace OpenMono.Tools;

/// <summary>
/// Rewrites Unix-style absolute paths in tool input JSON to Windows paths.
/// Handles file_path and path properties that start with '/' followed by a non-drive segment.
/// </summary>
public static class PathNormalizer
{
    private static readonly string[] PathProperties = ["file_path", "path"];

    public static JsonElement NormalizeWindowsPaths(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
            return input;

        var modified = false;
        var dict = new Dictionary<string, JsonElement>();

        foreach (var prop in input.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String
                && Array.IndexOf(PathProperties, prop.Name) >= 0
                && prop.Value.GetString() is { } raw
                && TryConvertToWindows(raw) is { } converted)
            {
                dict[prop.Name] = JsonDocument.Parse($"\"{converted.Replace("\\", "\\\\")}\"").RootElement.Clone();
                modified = true;
            }
            else
            {
                dict[prop.Name] = prop.Value.Clone();
            }
        }

        if (!modified)
            return input;

        using var ms = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        foreach (var kv in dict)
        {
            writer.WritePropertyName(kv.Key);
            kv.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.Flush();
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// Converts a Unix absolute path like /home/user/foo or /c/users/foo or /foo/bar
    /// to a Windows path. Returns null if the path doesn't look like a Unix absolute path.
    /// </summary>
    private static string? TryConvertToWindows(string path)
    {
        if (!path.StartsWith('/'))
            return null;

        // Already Windows-style root on WSL: /c/Users/... → C:\Users\...
        if (path.Length >= 3 && path[2] == '/' && char.IsLetter(path[1]))
        {
            var drive = char.ToUpperInvariant(path[1]);
            var rest = path[3..].Replace('/', '\\');
            return $"{drive}:\\{rest}";
        }

        // Plain Unix absolute path: /foo/bar → treated as relative to working drive root
        // Replace leading / with current drive root
        var currentRoot = Path.GetPathRoot(Environment.CurrentDirectory) ?? "C:\\";
        var relativePart = path.TrimStart('/').Replace('/', '\\');
        return Path.Combine(currentRoot, relativePart);
    }
}
