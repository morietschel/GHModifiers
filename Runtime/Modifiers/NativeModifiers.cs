using System;
using RhinoModifiers.Models;

namespace RhinoModifiers.Runtime.Modifiers;

/// <summary>
/// Maps native modifier kind strings (stored on <see cref="NativeModifierSpec"/>)
/// to their implementing <see cref="Modifier"/> classes.
/// </summary>
internal static class NativeModifiers
{
    public const string ExtrudeKind = "extrude";

    public static bool TryCreate(string nativeKind, out Modifier modifier, out string error)
    {
        var created = Create(nativeKind);
        if (created is not null)
        {
            modifier = created;
            error = string.Empty;
            return true;
        }

        modifier = null!;
        error = $"Unknown native modifier kind '{nativeKind}'.";
        return false;
    }

    public static string GetDisplayName(string nativeKind)
    {
        return Create(nativeKind)?.DisplayName ?? nativeKind;
    }

    private static Modifier? Create(string nativeKind)
    {
        return nativeKind switch
        {
            ExtrudeKind => new ExtrudeModifier(),
            _ => null,
        };
    }
}
