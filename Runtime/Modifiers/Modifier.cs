using System;
using System.Collections.Generic;
using System.IO;
using Rhino.Geometry;
using RhinoModifiers.Models;
using RhinoModifiers.Runtime;

namespace RhinoModifiers.Runtime.Modifiers;

/// <summary>
/// Display-name resolution shared by the engine and executor for any modifier kind.
/// </summary>
internal static class ModifierDisplayNames
{
    public static string GetStepLabel(ModifierSpec step)
    {
        if (step is GrasshopperModifierSpec grasshopper)
        {
            return Path.GetFileName(grasshopper.Path);
        }

        if (step is NativeModifierSpec native)
        {
            return string.IsNullOrWhiteSpace(native.Name)
                ? NativeModifiers.GetDisplayName(native.NativeKind)
                : native.Name;
        }

        return step.Name;
    }
}

/// <summary>
/// Base class for every evaluable modifier. Declares the modifier's inputs and outputs
/// and produces geometry/output values on demand. Grasshopper modifiers adapt to their
/// script; native modifiers implement built-in Rhino operations.
/// </summary>
internal abstract class Modifier : IDisposable
{
    public Guid NodeId { get; init; }

    public string DisplayName { get; protected set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public IReadOnlyList<ModifierInputDescriptor> Inputs { get; protected set; } =
        Array.Empty<ModifierInputDescriptor>();

    public IReadOnlyList<ModifierOutputDescriptor> Outputs { get; protected set; } =
        Array.Empty<ModifierOutputDescriptor>();

    public abstract ModifierEvaluationResult Evaluate(ModifierEvaluationContext context);

    /// <summary>Invalidates any cached state (e.g. cloned Grasshopper documents).</summary>
    public virtual void Reset() { }

    public virtual void Dispose() { }
}

/// <summary>
/// Everything a modifier needs to produce its result: the geometry flowing in from the
/// upstream stack and the serialized input values the user configured.
/// </summary>
internal sealed class ModifierEvaluationContext
{
    public IReadOnlyList<GeometryBase> InputGeometry { get; init; } = Array.Empty<GeometryBase>();

    public IReadOnlyDictionary<string, string> InputValues { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// The outcome of evaluating a single modifier: resulting geometry, published output
/// values (usable as link sources), and an optional error.
/// </summary>
internal sealed record ModifierEvaluationResult(
    bool Success,
    bool HasGeometryOutput,
    List<GeometryBase> OutputGeometry,
    List<StepOutputValue> PublishedOutputs,
    string ErrorMessage
)
{
    public static ModifierEvaluationResult Successful(
        List<GeometryBase> outputGeometry,
        List<StepOutputValue> publishedOutputs
    )
    {
        return new ModifierEvaluationResult(
            true,
            true,
            outputGeometry,
            publishedOutputs,
            string.Empty
        );
    }

    public static ModifierEvaluationResult Fail(string message)
    {
        return new ModifierEvaluationResult(
            false,
            false,
            new List<GeometryBase>(),
            new List<StepOutputValue>(),
            message
        );
    }
}
