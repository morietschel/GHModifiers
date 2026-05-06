using Rhino;
using Rhino.Commands;

namespace RhinoModifiers.Commands;

public sealed class GghRefreshSelectedStackCommand : Command
{
    public override string EnglishName => "GghRefreshSelectedStack";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        RhinoApp.WriteLine("GGH: Manual refresh requested for selected stack.");
        if (!RhinoModifiersPlugin.Instance.Engine.RefreshSelectedObject(doc, out var message))
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                RhinoApp.WriteLine(message);
            }

            return Result.Nothing;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }

        return Result.Success;
    }
}
