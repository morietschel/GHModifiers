using System;
using Rhino;

namespace RhinoModifiers.Runtime;

/// <summary>
/// Machine-wide persistence for the plug-in's security decisions.
/// </summary>
/// <remarks>
/// Backed by Rhino's per-plug-in <c>PersistentSettings</c>, which Rhino writes to the user's
/// profile. Approvals are stored here rather than in the 3dm on purpose: a document must never be
/// able to carry its own permission to execute code or reach the network.
/// </remarks>
internal static class SecuritySettings
{
    public static string GetString(string key)
    {
        try
        {
            var settings = RhinoModifiersPlugin.Instance?.Settings;
            return settings is null ? string.Empty : settings.GetString(key, string.Empty);
        }
        catch (Exception ex)
        {
            // Never let a settings failure become an implicit grant: callers treat an empty
            // value as "nothing approved".
            RhinoApp.WriteLine($"[Modifiers] Failed to read security setting '{key}': {ex.Message}");
            return string.Empty;
        }
    }

    public static void SetString(string key, string value)
    {
        try
        {
            var plugin = RhinoModifiersPlugin.Instance;
            if (plugin?.Settings is null)
            {
                return;
            }

            plugin.Settings.SetString(key, value);
            plugin.SaveSettings();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine(
                $"[Modifiers] Failed to persist security setting '{key}': {ex.Message}"
            );
        }
    }
}
