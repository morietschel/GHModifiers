using System;
using System.IO;
using Rhino;

namespace RhinoModifiers.Runtime;

/// <summary>Why a definition is waiting on the user.</summary>
internal enum PendingApprovalKind
{
    /// <summary>These specific bytes have not been approved for execution.</summary>
    Content,

    /// <summary>The network share holding the definition has not been approved for access.</summary>
    RemoteLocation,
}

/// <summary>A definition that will not run until the user says so.</summary>
internal sealed class PendingApproval
{
    public PendingApprovalKind Kind { get; init; }

    /// <summary>The path as the document declared it.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Content hash, empty when the block is <see cref="PendingApprovalKind.RemoteLocation"/>.</summary>
    public string Hash { get; init; } = string.Empty;

    public DefinitionSource Source { get; init; }

    /// <summary>Share root awaiting approval, set only for <see cref="PendingApprovalKind.RemoteLocation"/>.</summary>
    public string RemoteRoot { get; init; } = string.Empty;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Path) ? "(unnamed)" : System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Thrown when a step's definition may not run. Carries the pending approval so the panel can
/// offer the user a way to allow it, rather than only reporting a dead end.
/// </summary>
internal sealed class DefinitionNotApprovedException : Exception
{
    public DefinitionNotApprovedException(string message, PendingApproval? pending)
        : base(message)
    {
        Pending = pending;
    }

    public PendingApproval? Pending { get; }
}

/// <summary>A definition cleared to load.</summary>
internal sealed class ResolvedDefinition
{
    /// <summary>Filesystem path to hand to the Grasshopper archive reader.</summary>
    public string LoadPath { get; init; } = string.Empty;

    public DateTime LastWriteUtc { get; init; }

    public string Hash { get; init; } = string.Empty;

    public DefinitionSource Source { get; init; }
}

/// <summary>
/// The single chokepoint through which every definition must pass before it can be loaded.
/// </summary>
/// <remarks>
/// Both the evaluation path and the panel's contract-inspection path resolve through here. Gating
/// only one would leave the other as an execution vector: inspecting a definition solves a copy of
/// it to discover default input values, which runs its script components just as evaluation does.
/// </remarks>
internal static class DefinitionResolver
{
    /// <summary>
    /// Validates the path, obtains the bytes, and checks them against
    /// <see cref="DefinitionTrust"/>. Nothing is written to disk or handed to Grasshopper unless
    /// the result is <c>true</c>.
    /// </summary>
    public static bool TryResolve(
        RhinoDoc? doc,
        string rawPath,
        out ResolvedDefinition resolved,
        out PendingApproval? pending,
        out string error
    )
    {
        resolved = new ResolvedDefinition();
        pending = null;

        var verdict = DefinitionPathPolicy.Evaluate(
            rawPath,
            out var fullPath,
            out var remoteRoot,
            out error
        );

        if (verdict == DefinitionPathPolicy.PathVerdict.RemoteNotApproved)
        {
            pending = new PendingApproval
            {
                Kind = PendingApprovalKind.RemoteLocation,
                Path = rawPath,
                Source = DefinitionSource.Remote,
                RemoteRoot = remoteRoot,
            };
            return false;
        }

        if (verdict != DefinitionPathPolicy.PathVerdict.Allowed)
        {
            return false;
        }

        var isRemote = DefinitionPathPolicy.IsUncPath(fullPath);
        var onDisk = File.Exists(fullPath);

        // A path the user already trusts short-circuits before we read anything. This is what
        // lets them edit a definition without re-approving on every save, and it keeps the hot
        // evaluation path from re-reading the file just to hash it.
        //
        // Network shares are included: reaching one at all required approving its share root
        // above, so by the time we get here the user has already allowed this location and a
        // definition on it should behave like a local one.
        if (onDisk && DefinitionTrust.IsPathApproved(fullPath))
        {
            resolved = new ResolvedDefinition
            {
                LoadPath = fullPath,
                LastWriteUtc = File.GetLastWriteTimeUtc(fullPath),
                Source = isRemote ? DefinitionSource.Remote : DefinitionSource.LocalDisk,
            };
            return true;
        }

        byte[] bytes;
        DefinitionSource source;

        if (onDisk)
        {
            source = isRemote ? DefinitionSource.Remote : DefinitionSource.LocalDisk;
            try
            {
                bytes = File.ReadAllBytes(fullPath);
            }
            catch (Exception ex)
            {
                error = $"Could not read modifier definition: {ex.Message}";
                return false;
            }
        }
        else if (doc is not null && TryGetEmbeddedBytes(doc, fullPath, out bytes))
        {
            source = DefinitionSource.Embedded;
        }
        else
        {
            error = $"Modifier file not found: {rawPath}";
            return false;
        }

        var hash = DefinitionTrust.ComputeHash(bytes);

        if (!DefinitionTrust.IsApproved(hash, fullPath, source))
        {
            pending = new PendingApproval
            {
                Kind = PendingApprovalKind.Content,
                Path = fullPath,
                Hash = hash,
                Source = source,
            };
            error = $"'{System.IO.Path.GetFileName(fullPath)}' has not been approved.";
            return false;
        }

        // Approved. Only now may embedded bytes touch the filesystem.
        if (source == DefinitionSource.Embedded)
        {
            string tempPath;
            try
            {
                tempPath = EmbeddedDefinitionStorage.WriteApprovedBytesToTemp(fullPath, bytes);
            }
            catch (Exception ex)
            {
                error = $"Could not stage embedded modifier definition: {ex.Message}";
                return false;
            }

            resolved = new ResolvedDefinition
            {
                LoadPath = tempPath,
                LastWriteUtc = File.GetLastWriteTimeUtc(tempPath),
                Hash = hash,
                Source = source,
            };
            return true;
        }

        resolved = new ResolvedDefinition
        {
            LoadPath = fullPath,
            LastWriteUtc = File.GetLastWriteTimeUtc(fullPath),
            Hash = hash,
            Source = source,
        };
        return true;
    }

    private static bool TryGetEmbeddedBytes(RhinoDoc doc, string fullPath, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        try
        {
            return EmbeddedDefinitionStorage.TryGetBytes(doc, fullPath, out bytes);
        }
        catch (Exception)
        {
            // Malformed embedded data is treated as absent rather than fatal; the caller reports
            // "not found" and the document still opens.
            return false;
        }
    }
}
