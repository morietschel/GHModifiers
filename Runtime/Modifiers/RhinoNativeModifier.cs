using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;
using RhinoModifiers.Models;
using RhinoModifiers.Runtime;

namespace RhinoModifiers.Runtime.Modifiers;

/// <summary>
/// Base class for modifiers implemented as built-in Rhino operations rather than
/// Grasshopper definitions. Subclasses declare their inputs/outputs in the constructor
/// and implement <see cref="Apply"/>. Geometry flows through the same pipe as
/// Grasshopper modifiers.
/// </summary>
internal abstract class RhinoNativeModifier : Modifier
{
    public sealed override ModifierEvaluationResult Evaluate(ModifierEvaluationContext context)
    {
        try
        {
            var output = Apply(context.InputGeometry, context);

            // Publish geometry outputs so native modifiers can act as link sources.
            var publishedOutputs = Outputs
                .Where(outputDescriptor => outputDescriptor.Kind == ModifierIoKind.Geometry)
                .Select(outputDescriptor => new StepOutputValue(
                    outputDescriptor.Id,
                    DescribeGeometry(output),
                    output.Cast<object>().ToList()
                ))
                .ToList();

            return ModifierEvaluationResult.Successful(output, publishedOutputs);
        }
        catch (Exception ex)
        {
            return ModifierEvaluationResult.Fail(ex.Message);
        }
    }

    protected abstract List<GeometryBase> Apply(
        IReadOnlyList<GeometryBase> inputGeometry,
        ModifierEvaluationContext context
    );

    private static string DescribeGeometry(IEnumerable<GeometryBase> geometry)
    {
        var counts = geometry
            .GroupBy(item => item.ObjectType)
            .Select(group => $"{group.Key} x{group.Count()}")
            .ToArray();
        return counts.Length == 0 ? "empty" : string.Join(", ", counts);
    }
}
