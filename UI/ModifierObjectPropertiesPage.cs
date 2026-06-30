using System;
using Eto.Forms;
using Rhino;
using Rhino.UI;

namespace RhinoModifiers.UI;

public sealed class ModifierObjectPropertiesPage : ObjectPropertiesPage
{
    private readonly Panel _host = new();
    private ModifierStackPanel? _panel;

    public override string EnglishPageTitle => "Object Modifiers";

    public override string PageIconEmbeddedResourceString => "RhinoModifiers.wrench.png";

    public override int Index => int.MaxValue;

    public override object PageControl
    {
        get
        {
            EnsurePanel();
            return _host;
        }
    }

    public override bool ShouldDisplay(ObjectPropertiesPageEventArgs e)
    {
        return true;
    }

    public override void UpdatePage(ObjectPropertiesPageEventArgs e)
    {
        EnsurePanel();
        if (_panel is null)
        {
            return;
        }

        try
        {
            _panel.RefreshNow();
        }
        catch (Exception ex)
        {
            ShowError("Failed to update modifier panel.", ex);
        }
    }

    private void EnsurePanel()
    {
        if (_panel is not null)
        {
            return;
        }

        try
        {
            _panel = new ModifierStackPanel();
            _host.Content = _panel;
        }
        catch (Exception ex)
        {
            ShowError("Failed to initialize modifier panel.", ex);
        }
    }

    private void ShowError(string prefix, Exception ex)
    {
        _panel = null;
        RhinoApp.WriteLine($"Modifiers: {prefix} {ex}");
        _host.Content = new Label { Text = $"{prefix} {ex.Message}", Wrap = WrapMode.Word };
    }
}
