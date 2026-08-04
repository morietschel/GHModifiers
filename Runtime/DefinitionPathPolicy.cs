using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RhinoModifiers.Runtime;

/// <summary>
/// Decides whether a modifier definition path is safe to touch.
/// </summary>
/// <remarks>
/// <para>
/// Definition paths arrive from an opened document's user dictionary.
/// Two properties matter before any filesystem API sees the string:
/// </para>
/// <list type="bullet">
/// <item>
/// It must name a Grasshopper definition. The <c>.gh</c>/<c>.ghx</c> filter previously existed
/// only in the file dialogs, never on the load path.
/// </item>
/// <item>
/// It must be local, or point at a remote share the user has explicitly approved.
/// Connecting to the remote location happens on the very first <see cref="File.Exists"/>,
/// which is why this check runs before any I/O rather than at load time.
/// </item>
/// </list>
/// <para>
/// Approved share roots live in plug-in settings, machine wide. They are deliberately not stored
/// in the document: a document must never be able to carry its own permission to reach the
/// network.
/// </para>
/// </remarks>
internal static class DefinitionPathPolicy
{
    private const string ApprovedRemoteRootsKey = "Security.ApprovedRemoteRoots.V1";

    private static readonly string[] DefinitionExtensions = [".gh", ".ghx"];

    public enum PathVerdict
    {
        /// <summary>Safe to touch.</summary>
        Allowed,

        /// <summary>Malformed, empty, or not a file path we can reason about.</summary>
        Invalid,

        /// <summary>Not a Grasshopper definition.</summary>
        NotADefinition,

        /// <summary>Points at a remote share the user has not approved.</summary>
        RemoteNotApproved,
    }

    /// <summary>
    /// Evaluates <paramref name="rawPath"/> without performing any filesystem or network I/O.
    /// </summary>
    /// <param name="fullPath">The normalized path, valid only when the verdict is
    /// <see cref="PathVerdict.Allowed"/>.</param>
    /// <param name="remoteRoot">The share root (<c>\\host\share</c>) needing approval, set only
    /// when the verdict is <see cref="PathVerdict.RemoteNotApproved"/>.</param>
    /// <param name="error">A message safe to show the user..</param>
    public static PathVerdict Evaluate(
        string rawPath,
        out string fullPath,
        out string remoteRoot,
        out string error
    )
    {
        fullPath = string.Empty;
        remoteRoot = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = "Modifier path is empty.";
            return PathVerdict.Invalid;
        }

        // Reject URI-style paths before normalizing. Path.GetFullPath would otherwise turn
        // something like "http://host/a.gh" into a nonsense relative path under the working
        // directory rather than rejecting it outright.
        if (rawPath.Contains("://", StringComparison.Ordinal))
        {
            error = "Modifier path must be a file path.";
            return PathVerdict.Invalid;
        }

        // Detect UNC on the raw string. Normalizing first is not wrong, but keeping the remote
        // test ahead of every other operation makes the ordering guarantee obvious.
        var isRemote = IsUncPath(rawPath);

        string normalized;
        try
        {
            normalized = Path.GetFullPath(rawPath);
        }
        catch (Exception)
        {
            // ArgumentException, NotSupportedException, PathTooLongException, and friends all
            // mean the same thing here: we cannot reason about this path, so we refuse it.
            error = "Modifier path is not valid.";
            return PathVerdict.Invalid;
        }

        isRemote |= IsUncPath(normalized);

        var extension = Path.GetExtension(normalized);
        if (
            !DefinitionExtensions.Any(candidate =>
                string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            error = "Modifier path must point to a .gh or .ghx definition.";
            return PathVerdict.NotADefinition;
        }

        if (isRemote)
        {
            var root = GetShareRoot(normalized);
            if (string.IsNullOrEmpty(root))
            {
                error = "Modifier path is not valid.";
                return PathVerdict.Invalid;
            }

            if (!IsRemoteRootApproved(root))
            {
                remoteRoot = root;
                error =
                    $"This modifier is on a network location that has not been approved: {root}";
                return PathVerdict.RemoteNotApproved;
            }
        }

        fullPath = normalized;
        return PathVerdict.Allowed;
    }

    /// <summary>
    /// Convenience wrapper for callers that only care whether the path may be touched.
    /// </summary>
    public static bool TryResolve(string rawPath, out string fullPath, out string error)
    {
        return Evaluate(rawPath, out fullPath, out _, out error) == PathVerdict.Allowed;
    }

    public static bool IsUncPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 2)
        {
            return false;
        }

        // Windows accepts either separator for UNC, and \\?\UNC\ is the long-path form.
        var first = path[0];
        var second = path[1];
        var startsWithDoubleSeparator =
            (first == '\\' || first == '/') && (second == '\\' || second == '/');

        if (!startsWithDoubleSeparator)
        {
            return false;
        }

        return !path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reduces <c>\\host\share\folder\file.gh</c> to <c>\\host\share</c> so that approving a
    /// shared definition library is a single decision rather than one per file.
    /// </summary>
    public static string GetShareRoot(string uncPath)
    {
        if (!IsUncPath(uncPath))
        {
            return string.Empty;
        }

        var trimmed = uncPath.Replace('/', '\\');
        if (trimmed.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = @"\\" + trimmed.Substring(8);
        }

        var segments = trimmed
            .TrimStart('\\')
            .Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);

        // A share root needs both a host and a share name.
        return segments.Length < 2 ? string.Empty : $@"\\{segments[0]}\{segments[1]}";
    }

    public static bool IsRemoteRootApproved(string shareRoot)
    {
        if (string.IsNullOrWhiteSpace(shareRoot))
        {
            return false;
        }

        return LoadApprovedRemoteRoots()
            .Any(approved =>
                string.Equals(approved, shareRoot, StringComparison.OrdinalIgnoreCase)
            );
    }

    public static void ApproveRemoteRoot(string shareRoot)
    {
        if (string.IsNullOrWhiteSpace(shareRoot) || IsRemoteRootApproved(shareRoot))
        {
            return;
        }

        var roots = LoadApprovedRemoteRoots();
        roots.Add(shareRoot);
        SaveApprovedRemoteRoots(roots);
    }

    public static IReadOnlyList<string> GetApprovedRemoteRoots()
    {
        return LoadApprovedRemoteRoots();
    }

    public static void RevokeRemoteRoot(string shareRoot)
    {
        var roots = LoadApprovedRemoteRoots();
        if (
            roots.RemoveAll(approved =>
                string.Equals(approved, shareRoot, StringComparison.OrdinalIgnoreCase)
            ) > 0
        )
        {
            SaveApprovedRemoteRoots(roots);
        }
    }

    private static List<string> LoadApprovedRemoteRoots()
    {
        var raw = SecuritySettings.GetString(ApprovedRemoteRootsKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<string>();
        }

        return raw.Split('\n')
            .Select(entry => entry.Trim())
            .Where(entry => entry.Length > 0)
            .ToList();
    }

    private static void SaveApprovedRemoteRoots(IEnumerable<string> roots)
    {
        SecuritySettings.SetString(ApprovedRemoteRootsKey, string.Join("\n", roots));
    }
}
