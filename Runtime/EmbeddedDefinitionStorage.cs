using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rhino;

namespace RhinoModifiers.Runtime;

/// <summary>
/// Stores Grasshopper definition files inside the Rhino document so that
/// modifier stacks can be shared without shipping external .gh / .ghx files.
/// </summary>
internal static class EmbeddedDefinitionStorage
{
    private const string Key = "Modifiers.EmbeddedDefinitions.V1";

    /// <summary>
    /// Reads each existing file in <paramref name="paths"/> and embeds its raw bytes
    /// (Base64-encoded) into the Rhino document's string table.
    /// Files that are already embedded with matching content are skipped.
    /// Returns counts of (added, updated, unchanged).
    /// </summary>
    public static (int Added, int Updated, int Unchanged) EmbedDefinitions(
        RhinoDoc doc,
        IEnumerable<string> paths
    )
    {
        var embedded = LoadEmbedded(doc);
        var added = 0;
        var updated = 0;
        var unchanged = 0;

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            var hash = ComputeHash(bytes);

            if (
                embedded.TryGetValue(path, out var existing)
                && string.Equals(existing.Hash, hash, StringComparison.Ordinal)
            )
            {
                unchanged++;
                continue;
            }

            if (embedded.ContainsKey(path))
            {
                updated++;
            }
            else
            {
                added++;
            }

            embedded[path] = new EmbeddedFileEntry(Convert.ToBase64String(bytes), hash);
        }

        SaveEmbedded(doc, embedded);
        return (added, updated, unchanged);
    }

    /// <summary>
    /// Attempts to extract an embedded definition to a temporary file.
    /// Returns <c>true</c> if a matching embedded file was found and written.
    /// </summary>
    public static bool TryExtract(RhinoDoc doc, string path, out string tempPath)
    {
        tempPath = string.Empty;
        var embedded = LoadEmbedded(doc);
        if (!embedded.TryGetValue(path, out var entry))
        {
            return false;
        }

        var bytes = Convert.FromBase64String(entry.Base64);
        var hash = ComputeHash(bytes);
        var tempDir = Path.Combine(Path.GetTempPath(), "Modifiers_Embedded", hash);
        Directory.CreateDirectory(tempDir);

        tempPath = Path.Combine(tempDir, Path.GetFileName(path));
        File.WriteAllBytes(tempPath, bytes);
        return true;
    }

    public static IReadOnlyList<string> GetEmbeddedPaths(RhinoDoc doc)
    {
        return LoadEmbedded(doc).Keys.ToList();
    }

    private static Dictionary<string, EmbeddedFileEntry> LoadEmbedded(RhinoDoc doc)
    {
        var json = doc.Strings.GetValue(Key);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, EmbeddedFileEntry>(StringComparer.OrdinalIgnoreCase);
        }

        var entries = JsonSerializer.Deserialize<Dictionary<string, EmbeddedFileEntry>>(json);
        return entries is not null
            ? new Dictionary<string, EmbeddedFileEntry>(entries, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, EmbeddedFileEntry>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveEmbedded(RhinoDoc doc, Dictionary<string, EmbeddedFileEntry> embedded)
    {
        var json = JsonSerializer.Serialize(embedded);
        doc.Strings.SetString(Key, json);
    }

    private static string ComputeHash(byte[] bytes)
    {
        // SHA256.HashData is .NET 5+ only; use the net48-compatible pattern (identical output).
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
    }

    private readonly record struct EmbeddedFileEntry(
        [property: JsonPropertyName("b")] string Base64,
        [property: JsonPropertyName("h")] string Hash
    );
}
