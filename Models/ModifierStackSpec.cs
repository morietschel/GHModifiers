using System;
using System.Collections.Generic;

namespace RhinoModifiers.Models;

internal sealed class ModifierStackSpec
{
    internal const string UserDictionaryKey = "Modifiers.ModifierStack.V1";

    public int Version { get; set; } = 4;

    public List<ModifierStepSpec> Steps { get; set; } = new();

    public ModifierStackSpec Clone()
    {
        var clone = new ModifierStackSpec { Version = Version };

        foreach (var step in Steps)
        {
            clone.Steps.Add(step.Clone());
        }

        return clone;
    }
}

internal sealed class ModifierStepSpec
{
    public Guid StepId { get; set; } = Guid.NewGuid();

    public string Path { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public Dictionary<string, string> InputValues { get; set; } = new();

    public Dictionary<string, ModifierInputLinkSpec> InputLinks { get; set; } = new();

    public ModifierStepSpec Clone()
    {
        var clone = new ModifierStepSpec
        {
            StepId = StepId,
            Path = Path,
            Enabled = Enabled,
        };

        foreach (var pair in InputValues)
        {
            clone.InputValues[pair.Key] = pair.Value;
        }

        foreach (var pair in InputLinks)
        {
            clone.InputLinks[pair.Key] = pair.Value.Clone();
        }

        return clone;
    }
}

internal sealed class ModifierInputLinkSpec
{
    public ModifierInputLinkSourceKind SourceKind { get; set; } =
        ModifierInputLinkSourceKind.StepOutput;

    public Guid SourceStepId { get; set; }

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
            SourceStepId = SourceStepId,
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
