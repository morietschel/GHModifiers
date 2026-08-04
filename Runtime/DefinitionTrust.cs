using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace RhinoModifiers.Runtime;

/// <summary>
/// Where a definition's bytes came from. This drives which form of approval applies.
/// </summary>
internal enum DefinitionSource
{
    /// <summary>A file on local disk.</summary>
    LocalDisk,

    /// <summary>Bytes carried inside the Rhino document's string table.</summary>
    Embedded,

    /// <summary>A file on a network share the user has approved for access.</summary>
    Remote,
}

/// <summary>
/// Records which Grasshopper definitions the user has agreed to execute.
/// </summary>
/// <remarks>
/// <para>
/// Solving a Grasshopper definition runs any script components it contains, so loading one from an
/// untrusted document is arbitrary code execution. Definitions are therefore inert until approved.
/// </para>
/// <para>Approval is recorded in two forms, and keeping them apart is what makes this safe:</para>
/// <list type="bullet">
/// <item>
/// An <b>approved content hash</b> grants exactly those bytes, wherever they came from. Embedded
/// definitions require one, always.
/// </item>
/// <item>
/// An <b>approved path</b> grants that path as its contents change, so editing a definition does
/// not re-prompt on every save.
/// </item>
/// </list>
/// <para>
/// Path trust covers files on disk, local or on an approved network share, but never
/// <see cref="DefinitionSource.Embedded"/> bytes. That distinction is the point. A path on disk is
/// a real location the user chose, and whoever can write there could compromise them by other
/// means anyway. An embedded path is just a label the document author picked, and it is stored
/// next to bytes they also control -- so if path trust applied there, an attacker could ship a
/// document whose path matches one the user already approved plus a payload keyed to that path,
/// and the moment the real file was absent their bytes would inherit the approval and execute.
/// The source parameter threaded through these methods exists so that bypass cannot be reached by
/// accident.
/// </para>
/// <para>
/// Reaching a network share at all is gated separately, by
/// <see cref="DefinitionPathPolicy"/>'s approved share roots. Once the user has allowed a share,
/// definitions on it behave like local ones.
/// </para>
/// <para>
/// Approvals live in plug-in settings, machine wide, never in the document -- a hostile file must
/// not be able to carry its own permission.
/// </para>
/// </remarks>
internal static class DefinitionTrust
{
    private const string ApprovedHashesKey = "Security.ApprovedDefinitionHashes.V1";
    private const string ApprovedLocalPathsKey = "Security.ApprovedDefinitionPaths.V1";

    public static string ComputeHash(byte[] bytes)
    {
        // SHA256.HashData is .NET 5+ only; use the net48-compatible pattern (identical output).
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
    }

    /// <summary>A short, human-comparable form of a hash for display in the approval banner.</summary>
    public static string ShortHash(string hash)
    {
        return string.IsNullOrEmpty(hash) || hash.Length <= 12 ? hash : hash.Substring(0, 12);
    }

    /// <summary>
    /// True when a filesystem path is approved, meaning its current and future contents may run
    /// without re-prompting. Never consulted for embedded definitions.
    /// </summary>
    public static bool IsPathApproved(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return LoadList(ApprovedLocalPathsKey)
            .Any(approved => string.Equals(approved, path, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsHashApproved(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return false;
        }

        return LoadList(ApprovedHashesKey)
            .Any(approved => string.Equals(approved, hash, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Decides whether these bytes may be loaded and solved.
    /// </summary>
    public static bool IsApproved(string hash, string path, DefinitionSource source)
    {
        // Embedded definitions must match approved content, always. There is no path-based
        // shortcut for them by design -- see the remarks on this class.
        if (source == DefinitionSource.Embedded)
        {
            return IsHashApproved(hash);
        }

        return IsPathApproved(path) || IsHashApproved(hash);
    }

    /// <summary>
    /// Records the user's decision to run a definition.
    /// </summary>
    public static void Approve(string hash, string path, DefinitionSource source)
    {
        if (!string.IsNullOrWhiteSpace(hash))
        {
            AddTo(ApprovedHashesKey, hash);
        }

        // Only a real file gets ongoing path trust. Approving an embedded payload must never
        // grant its path, or the next document claiming that path would inherit the approval.
        if (source != DefinitionSource.Embedded && !string.IsNullOrWhiteSpace(path))
        {
            AddTo(ApprovedLocalPathsKey, path);
        }
    }

    /// <summary>
    /// Marks a definition the user picked themselves as trusted, without needing its bytes.
    /// Choosing a file through the file dialog is an explicit act, so the normal authoring flow
    /// never has to visit the approval banner.
    /// </summary>
    public static void ApprovePath(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            AddTo(ApprovedLocalPathsKey, path);
        }
    }

    public static void RevokePath(string path)
    {
        RemoveFrom(ApprovedLocalPathsKey, path);
    }

    public static IReadOnlyList<string> GetApprovedPaths()
    {
        return LoadList(ApprovedLocalPathsKey);
    }

    private static List<string> LoadList(string key)
    {
        var raw = SecuritySettings.GetString(key);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<string>();
        }

        return raw.Split('\n')
            .Select(entry => entry.Trim())
            .Where(entry => entry.Length > 0)
            .ToList();
    }

    private static void AddTo(string key, string value)
    {
        var entries = LoadList(key);
        if (entries.Any(entry => string.Equals(entry, value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        entries.Add(value);
        SecuritySettings.SetString(key, string.Join("\n", entries));
    }

    private static void RemoveFrom(string key, string value)
    {
        var entries = LoadList(key);
        if (
            entries.RemoveAll(entry =>
                string.Equals(entry, value, StringComparison.OrdinalIgnoreCase)
            ) > 0
        )
        {
            SecuritySettings.SetString(key, string.Join("\n", entries));
        }
    }
}
