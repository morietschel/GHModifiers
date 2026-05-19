using System;
using System.Collections.Generic;
using System.Text.Json;
using Rhino;
using Rhino.DocObjects;
using RhinoModifiers.Models;

namespace RhinoModifiers.Runtime;

internal static class ModifierStackStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static ModifierStackSpec Load(RhinoObject? rhinoObject)
    {
        if (rhinoObject is null)
        {
            return new ModifierStackSpec();
        }

        var dictionary = rhinoObject.Attributes.UserDictionary;
        if (!dictionary.ContainsKey(ModifierStackSpec.UserDictionaryKey))
        {
            return new ModifierStackSpec();
        }

        if (
            dictionary[ModifierStackSpec.UserDictionaryKey] is not string json
            || string.IsNullOrWhiteSpace(json)
        )
        {
            return new ModifierStackSpec();
        }

        try
        {
            var spec =
                JsonSerializer.Deserialize<ModifierStackSpec>(json, JsonOptions)
                ?? new ModifierStackSpec();
            Normalize(spec);
            return spec;
        }
        catch
        {
            return new ModifierStackSpec();
        }
    }

    public static bool Save(RhinoDoc doc, Guid objectId, ModifierStackSpec spec)
    {
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            return false;
        }

        var attributes = rhinoObject.Attributes.Duplicate();
        if (spec.Steps.Count == 0)
        {
            attributes.UserDictionary.Remove(ModifierStackSpec.UserDictionaryKey);
        }
        else
        {
            var json = JsonSerializer.Serialize(spec, JsonOptions);
            attributes.UserDictionary[ModifierStackSpec.UserDictionaryKey] = json;
        }

        return doc.Objects.ModifyAttributes(rhinoObject, attributes, true);
    }

    private static void Normalize(ModifierStackSpec spec)
    {
        spec.Version = Math.Max(spec.Version, 4);
        spec.Steps ??= new List<ModifierStepSpec>();

        var seenStepIds = new HashSet<Guid>();
        foreach (var step in spec.Steps)
        {
            step.InputValues ??= new Dictionary<string, string>();
            step.InputLinks ??= new Dictionary<string, ModifierInputLinkSpec>();

            if (step.StepId == Guid.Empty || !seenStepIds.Add(step.StepId))
            {
                step.StepId = Guid.NewGuid();
                seenStepIds.Add(step.StepId);
            }
        }
    }
}
