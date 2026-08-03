using System;
using System.Collections.Generic;
using System.Globalization;
using Rhino.Geometry;
using RhinoModifiers.Models;
using RhinoModifiers.Runtime;

namespace RhinoModifiers.Runtime.Modifiers;

/// <summary>
/// Native modifier that extrudes planar curves into breps by a configurable distance.
/// Unsupported input geometry passes through unchanged.
/// </summary>
internal sealed class ExtrudeModifier : RhinoNativeModifier
{
    private static readonly Guid DistanceInputId = Guid.Parse(
        "e3100000-0000-4000-8000-000000000001"
    );
    private static readonly Guid GeometryOutputId = Guid.Parse(
        "e3100000-0000-4000-8000-000000000002"
    );

    public ExtrudeModifier()
    {
        DisplayName = "Extrude";
        Inputs = new[]
        {
            new ModifierInputDescriptor(
                DistanceInputId,
                "distance",
                "Distance",
                "Extrusion distance.",
                ModifierIoKind.Number,
                "1",
                true,
                false,
                false,
                null,
                null,
                3
            ),
        };
        Outputs = new[]
        {
            new ModifierOutputDescriptor(
                GeometryOutputId,
                "geometry",
                "Geometry",
                "Extruded geometry.",
                ModifierIoKind.Geometry
            ),
        };
    }

    protected override List<GeometryBase> Apply(
        IReadOnlyList<GeometryBase> inputGeometry,
        ModifierEvaluationContext context
    )
    {
        var distance = 1.0;
        if (
            context.InputValues.TryGetValue("distance", out var rawValue)
            && double.TryParse(
                rawValue,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsedDistance
            )
        )
        {
            distance = parsedDistance;
        }

        var output = new List<GeometryBase>();
        foreach (var geometry in inputGeometry)
        {
            switch (geometry)
            {
                case Curve curve:
                    try
                    {
                        var extrusion = Extrusion.Create(curve, distance, true);
                        output.Add(
                            extrusion is not null ? extrusion.ToBrep() : geometry.Duplicate()
                        );
                    }
                    catch
                    {
                        // Non-planar or degenerate curves cannot be extruded; pass them
                        // through unchanged so downstream modifiers still see them.
                        output.Add(geometry.Duplicate());
                    }

                    break;
                default:
                    // Pass unsupported types through so downstream modifiers still see them.
                    output.Add(geometry.Duplicate());
                    break;
            }
        }

        return output;
    }
}
