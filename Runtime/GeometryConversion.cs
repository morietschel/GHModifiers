using System.Collections.Generic;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace RhinoModifiers.Runtime;

internal static class GeometryConversion
{
    public static OutputReadResult ReadOutput(IGH_Param outputParam)
    {
        var output = new List<GeometryBase>();
        var skippedTypes = new List<string>();
        var totalItemCount = 0;

        foreach (var goo in outputParam.VolatileData.AllData(true))
        {
            totalItemCount += 1;
            if (TryFromGoo(goo, out var geometry))
            {
                output.Add(geometry);
                continue;
            }

            skippedTypes.Add(DescribeGoo(goo));
        }

        return new OutputReadResult(output, totalItemCount, skippedTypes);
    }

    public static bool TryGetSourceGeometry(GeometryBase geometry, out List<GeometryBase> converted, out string error)
    {
        converted = new List<GeometryBase>();
        error = string.Empty;

        switch (geometry)
        {
            case Rhino.Geometry.Point point:
                converted.Add(new Rhino.Geometry.Point(point.Location));
                return true;
            case Curve curve:
                converted.Add(curve.DuplicateCurve());
                return true;
            case Brep brep:
                converted.Add(brep.DuplicateBrep());
                return true;
            case Extrusion extrusion:
                converted.Add(extrusion.ToBrep());
                return true;
            case Mesh mesh:
                converted.Add(mesh.DuplicateMesh());
                return true;
            case SubD subD:
                converted.Add((GeometryBase)subD.Duplicate());
                return true;
            default:
                error = $"Unsupported Rhino object geometry type '{geometry.ObjectType}'.";
                return false;
        }
    }

    public static bool TryToGooList(IEnumerable<GeometryBase> geometry, out List<IGH_Goo> goos, out string error)
    {
        goos = new List<IGH_Goo>();
        error = string.Empty;

        foreach (var item in geometry)
        {
            var goo = ToGoo(item);
            if (goo is null)
            {
                error = $"Unsupported geometry type '{item.ObjectType}' for Grasshopper transport.";
                return false;
            }

            goos.Add(goo);
        }

        return true;
    }

    private static IGH_Goo? ToGoo(GeometryBase geometry)
    {
        return geometry switch
        {
            Rhino.Geometry.Point point => new GH_Point(point.Location),
            Curve curve => new GH_Curve(curve.DuplicateCurve()),
            Brep brep => new GH_Brep(brep.DuplicateBrep()),
            Extrusion extrusion => new GH_Brep(extrusion.ToBrep()),
            Mesh mesh => new GH_Mesh(mesh.DuplicateMesh()),
            SubD subD => new GH_SubD((SubD)subD.Duplicate()),
            _ => null,
        };
    }

    private static bool TryFromGoo(IGH_Goo goo, out GeometryBase geometry)
    {
        switch (goo)
        {
            case GH_Point point:
                geometry = new Rhino.Geometry.Point(point.Value);
                return true;
            case GH_Curve curve when curve.Value is not null:
                geometry = curve.Value.DuplicateCurve();
                return true;
            case GH_Brep brep when brep.Value is not null:
                geometry = brep.Value.DuplicateBrep();
                return true;
            case GH_Mesh mesh when mesh.Value is not null:
                geometry = mesh.Value.DuplicateMesh();
                return true;
            case GH_SubD subD when subD.Value is not null:
                geometry = (GeometryBase)subD.Value.Duplicate();
                return true;
            case GH_Surface surface when surface.Value is not null:
                geometry = surface.Value.DuplicateBrep();
                return true;
            case GH_Box box:
                geometry = Brep.CreateFromBox(box.Value);
                return true;
            default:
                var scriptValue = goo.ScriptVariable();
                switch (scriptValue)
                {
                    case GeometryBase geometryBase:
                        geometry = geometryBase.Duplicate();
                        return true;
                    case Point3d point3d:
                        geometry = new Rhino.Geometry.Point(point3d);
                        return true;
                    default:
                        geometry = null!;
                        return false;
                    }
        }
    }

    private static string DescribeGoo(IGH_Goo goo)
    {
        var scriptValue = goo.ScriptVariable();
        if (scriptValue is not null)
        {
            return $"{goo.GetType().Name} -> {scriptValue.GetType().Name}";
        }

        return goo.GetType().Name;
    }

    public readonly record struct OutputReadResult(List<GeometryBase> Geometry, int TotalItemCount, List<string> SkippedTypes);
}
