using System.Linq;
using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoModifiers.UI;

internal sealed class StackPreviewConduit : DisplayConduit
{
    private static readonly Color PreviewColor = Color.OrangeRed;
    private readonly RhinoModifiers.Runtime.ModifierEngine _engine;

    public StackPreviewConduit(RhinoModifiers.Runtime.ModifierEngine engine)
    {
        _engine = engine;
    }

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
        foreach (var stack in _engine.GetPreviewStacks(RhinoDoc.ActiveDoc))
        {
            foreach (var geometry in stack.Geometry)
            {
                e.IncludeBoundingBox(geometry.GetBoundingBox(true));
            }
        }
    }

    protected override void PreDrawObject(DrawObjectEventArgs e)
    {
        if (_engine.GetManagedObjectIds(e.RhinoObject.Document).Contains(e.RhinoObject.Id))
        {
            e.DrawObject = false;
        }
    }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
        foreach (var objectId in _engine.GetManagedObjectIds(e.RhinoDoc))
        {
            var sourceObject = e.RhinoDoc.Objects.FindId(objectId);
            var drawColor = GetPreviewColor(e.RhinoDoc, sourceObject);
            DrawSourceWireframe(e.Display, sourceObject, drawColor);
        }

        foreach (var stack in _engine.GetPreviewStacks(e.RhinoDoc))
        {
            var sourceObject = e.RhinoDoc.Objects.FindId(stack.SourceObjectId);
            var drawColor = GetPreviewColor(e.RhinoDoc, sourceObject);
            using var material = CreatePreviewMaterial(sourceObject, drawColor);

            foreach (var geometry in stack.Geometry)
            {
                DrawGeometry(e.Display, geometry, drawColor, material);
            }
        }
    }

    private static Color GetPreviewColor(RhinoDoc doc, RhinoObject? sourceObject)
    {
        return sourceObject?.Attributes.DrawColor(doc) ?? PreviewColor;
    }

    private static DisplayMaterial CreatePreviewMaterial(RhinoObject? sourceObject, Color fallbackColor)
    {
        var sourceMaterial = sourceObject?.GetMaterial(true);
        return sourceMaterial is null ? new DisplayMaterial(fallbackColor) : new DisplayMaterial(sourceMaterial);
    }

    private static void DrawSourceWireframe(DisplayPipeline display, RhinoObject? sourceObject, Color drawColor)
    {
        if (sourceObject is null ||
            !RhinoModifiers.Runtime.GeometryConversion.TryGetSourceGeometry(sourceObject.Geometry, out var geometry, out _))
        {
            return;
        }

        foreach (var item in geometry)
        {
            DrawWireframeGeometry(display, item, drawColor);
        }
    }

    private static void DrawGeometry(DisplayPipeline display, GeometryBase geometry, Color drawColor, DisplayMaterial material)
    {
        switch (geometry)
        {
            case Rhino.Geometry.Point point:
                display.DrawPoint(point.Location, PointStyle.Simple, 4, drawColor);
                break;
            case Curve curve:
                display.DrawCurve(curve, drawColor, 2);
                break;
            case Brep brep:
                display.DrawBrepShaded(brep, material);
                display.DrawBrepWires(brep, drawColor, 1);
                break;
            case Mesh mesh:
                if (mesh.VertexColors.Count == mesh.Vertices.Count)
                {
                    if (mesh.Normals.Count == 0)
                        mesh.Normals.ComputeNormals();
                    display.DrawMeshFalseColors(mesh);
                }
                else
                {
                    display.DrawMeshShaded(mesh, material);
                }
                display.DrawMeshWires(mesh, drawColor);
                break;
            case SubD subD:
                display.DrawSubDShaded(subD, material);
                display.DrawSubDWires(subD, drawColor, 1);
                break;
        }
    }

    private static void DrawWireframeGeometry(DisplayPipeline display, GeometryBase geometry, Color drawColor)
    {
        switch (geometry)
        {
            case Rhino.Geometry.Point point:
                display.DrawPoint(point.Location, PointStyle.Simple, 4, drawColor);
                break;
            case Curve curve:
                display.DrawCurve(curve, drawColor, 1);
                break;
            case Brep brep:
                display.DrawBrepWires(brep, drawColor, 1);
                break;
            case Mesh mesh:
                display.DrawMeshWires(mesh, drawColor);
                break;
            case SubD subD:
                display.DrawSubDWires(subD, drawColor, 1);
                break;
        }
    }
}
