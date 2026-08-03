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
            // Old beta serialization shapes (e.g. the flat "Steps" list) and any other
            // unreadable payloads simply produce an empty stack. The project is in beta
            // and does not migrate legacy data.
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
        if (spec.Nodes.Count == 0)
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
        spec.Version = 1;
        spec.Nodes ??= new List<ModifierNodeSpec>();

        var seenNodeIds = new HashSet<Guid>();
        NormalizeNodes(spec.Nodes, seenNodeIds);
    }

    private static void NormalizeNodes(List<ModifierNodeSpec> nodes, HashSet<Guid> seenNodeIds)
    {
        if (nodes is null)
        {
            return;
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node is null)
            {
                nodes.RemoveAt(i);
                i--;
                continue;
            }

            if (node.NodeId == Guid.Empty || !seenNodeIds.Add(node.NodeId))
            {
                node.NodeId = Guid.NewGuid();
                seenNodeIds.Add(node.NodeId);
            }

            if (node is ModifierGroupSpec group)
            {
                group.Children ??= new List<ModifierNodeSpec>();
                NormalizeNodes(group.Children, seenNodeIds);
            }
            else if (node is ModifierSpec modifier)
            {
                modifier.InputValues ??= new Dictionary<string, string>();
                modifier.InputLinks ??= new Dictionary<string, ModifierInputLinkSpec>();
            }
        }
    }
}
