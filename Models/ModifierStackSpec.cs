using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RhinoModifiers.Models;

/// <summary>
/// Serializable specification of a modifier stack stored on a Rhino object.
/// The stack is a tree of <see cref="ModifierNodeSpec"/>: groups are pure visual
/// containers, leaf <see cref="ModifierSpec"/> nodes carry the actual modifier data.
/// The execution pipeline only ever consumes the flattened leaf list (see
/// <see cref="ModifierStack.Flatten"/>), so groups never affect evaluation order.
/// </summary>
internal sealed class ModifierStackSpec
{
    internal const string UserDictionaryKey = "Modifiers.ModifierStack.V1";

    public int Version { get; set; } = 1;

    public List<ModifierNodeSpec> Nodes { get; set; } = new();

    public ModifierStackSpec Clone()
    {
        var clone = new ModifierStackSpec { Version = Version };

        foreach (var node in Nodes)
        {
            clone.Nodes.Add(node.Clone());
        }

        return clone;
    }
}

/// <summary>
/// Base of the modifier stack tree. Every node has a stable unique id so links and
/// UI state can reference nodes regardless of ordering or nesting.
/// </summary>
[JsonDerivedType(typeof(ModifierGroupSpec), "group")]
[JsonDerivedType(typeof(GrasshopperModifierSpec), "grasshopper")]
[JsonDerivedType(typeof(NativeModifierSpec), "native")]
internal abstract class ModifierNodeSpec
{
    public Guid NodeId { get; set; } = Guid.NewGuid();

    /// <summary>Optional display name override (groups require one).</summary>
    public string Name { get; set; } = string.Empty;

    public abstract ModifierNodeSpec Clone();
}

/// <summary>
/// A nameable, collapsible container of other groups and modifiers. Groups are purely
/// organizational: they are never evaluated, never produce or consume geometry, and are
/// never valid link sources.
/// </summary>
internal sealed class ModifierGroupSpec : ModifierNodeSpec
{
    public List<ModifierNodeSpec> Children { get; set; } = new();

    public override ModifierNodeSpec Clone()
    {
        var clone = new ModifierGroupSpec { NodeId = NodeId, Name = Name };

        foreach (var child in Children)
        {
            clone.Children.Add(child.Clone());
        }

        return clone;
    }
}

/// <summary>
/// Base class for a single modifier instance in the stack: a leaf node carrying the
/// shared modifier state (enabled flag, input values, input links).
/// </summary>
internal abstract class ModifierSpec : ModifierNodeSpec
{
    public bool Enabled { get; set; } = true;

    public Dictionary<string, string> InputValues { get; set; } = new();

    public Dictionary<string, ModifierInputLinkSpec> InputLinks { get; set; } = new();

    protected void CopyModifierStateTo(ModifierSpec clone)
    {
        clone.NodeId = NodeId;
        clone.Name = Name;
        clone.Enabled = Enabled;

        foreach (var pair in InputValues)
        {
            clone.InputValues[pair.Key] = pair.Value;
        }

        foreach (var pair in InputLinks)
        {
            clone.InputLinks[pair.Key] = pair.Value.Clone();
        }
    }
}

/// <summary>
/// A modifier backed by a Grasshopper definition file. One class serves every
/// .gh/.ghx script: the script's contract (inputs, outputs, geometry piping) is
/// discovered at runtime rather than declared per script.
/// </summary>
internal sealed class GrasshopperModifierSpec : ModifierSpec
{
    public string Path { get; set; } = string.Empty;

    public override ModifierNodeSpec Clone()
    {
        var clone = new GrasshopperModifierSpec { Path = Path };
        CopyModifierStateTo(clone);
        return clone;
    }
}

/// <summary>
/// A modifier backed by a built-in Rhino operation (e.g. Extrude). The concrete
/// behavior is resolved from <see cref="NativeKind"/> via the native modifier registry.
/// </summary>
internal sealed class NativeModifierSpec : ModifierSpec
{
    public string NativeKind { get; set; } = string.Empty;

    public override ModifierNodeSpec Clone()
    {
        var clone = new NativeModifierSpec { NativeKind = NativeKind };
        CopyModifierStateTo(clone);
        return clone;
    }
}

/// <summary>
/// Links an input of one modifier to an output of an upstream modifier (or to the
/// preview geometry of another managed Rhino object).
/// </summary>
internal sealed class ModifierInputLinkSpec
{
    public ModifierInputLinkSourceKind SourceKind { get; set; } =
        ModifierInputLinkSourceKind.StepOutput;

    public Guid SourceNodeId { get; set; }

    public string SourceOutputId { get; set; } = string.Empty;

    public string SourceStepLabel { get; set; } = string.Empty;

    public string SourceOutputLabel { get; set; } = string.Empty;

    public Guid SourceObjectId { get; set; }

    public string SourceObjectLabel { get; set; } = string.Empty;

    public ModifierInputLinkSpec Clone()
    {
        return new ModifierInputLinkSpec
        {
            SourceKind = SourceKind,
            SourceNodeId = SourceNodeId,
            SourceOutputId = SourceOutputId,
            SourceStepLabel = SourceStepLabel,
            SourceOutputLabel = SourceOutputLabel,
            SourceObjectId = SourceObjectId,
            SourceObjectLabel = SourceObjectLabel,
        };
    }
}

internal enum ModifierInputLinkSourceKind
{
    StepOutput = 0,
    ObjectPreview = 1,
}
