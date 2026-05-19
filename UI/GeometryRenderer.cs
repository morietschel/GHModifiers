using System.Drawing;
using Rhino.Display;
using Rhino.Geometry;
using Point = Rhino.Geometry.Point;

namespace RhinoModifiers.UI;

/// <summary>
/// Defines the visual style for rendering geometry.
/// </summary>
public enum RenderStyle
{
    Wireframe,
    Shaded,
}

/// <summary>
/// Utility class for centralizing geometry drawing logic across different preview styles.
/// </summary>
internal static class GeometryRenderer
{
    /// <summary>
    /// Draws various forms of geometry based on the specified rendering style.
    /// </summary>
    /// <param name="display">The display pipeline to use for drawing.</param>
    /// <param name="geometry">The geometry to draw (e.g., Point, Curve, Brep, Mesh, SubD).</param>
    /// <param name="drawColor">The primary color to use for wireframes or points.</param>
    /// <param name="style">The rendering style, specifying if it should be shaded or wireframe only.</param>
    /// <param name="material">The material to use for shaded rendering. Required for Shaded mode on Breps, Meshes, and SubDs.</param>
    public static void Draw(
        DisplayPipeline display,
        GeometryBase geometry,
        Color drawColor,
        RenderStyle style,
        DisplayMaterial? material = null
    )
    {
        switch (geometry)
        {
            case Point point:
                display.DrawPoint(point.Location, PointStyle.Simple, 4, drawColor);
                break;
            case Curve curve:
                display.DrawCurve(curve, drawColor, style == RenderStyle.Shaded ? 2 : 1);
                break;
            case Brep brep:
                if (style == RenderStyle.Shaded && material != null)
                {
                    display.DrawBrepShaded(brep, material);
                }
                display.DrawBrepWires(brep, drawColor, 1);
                break;
            case Mesh mesh:
                if (style == RenderStyle.Shaded)
                {
                    if (mesh.VertexColors.Count == mesh.Vertices.Count)
                    {
                        if (mesh.Normals.Count == 0)
                            mesh.Normals.ComputeNormals();
                        display.DrawMeshFalseColors(mesh);
                    }
                    else if (material != null)
                    {
                        display.DrawMeshShaded(mesh, material);
                    }
                }
                display.DrawMeshWires(mesh, drawColor);
                break;
            case SubD subD:
                if (style == RenderStyle.Shaded && material != null)
                {
                    display.DrawSubDShaded(subD, material);
                }
                display.DrawSubDWires(subD, drawColor, 1);
                break;
        }
    }
}
