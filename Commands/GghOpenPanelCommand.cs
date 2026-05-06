using Rhino.Commands;
using Rhino.UI;

namespace RhinoModifiers.Commands;

public sealed class GghOpenPanelCommand : Command
{
    public override string EnglishName => "GghOpenPanel";

    protected override Result RunCommand(Rhino.RhinoDoc doc, RunMode mode)
    {
        Rhino.RhinoApp.WriteLine("GGH: Opening Object Properties panel.");
        Panels.OpenPanel(PanelIds.ObjectProperties);
        return Result.Success;
    }
}
