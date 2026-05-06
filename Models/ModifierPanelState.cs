using System;
using System.Collections.Generic;

namespace RhinoModifiers.Models;

internal sealed class ModifierPanelState
{
    public bool CanEdit { get; init; }

    public Guid? SelectedObjectId { get; init; }

    public string SelectionLabel { get; init; } = string.Empty;

    public string StatusMessage { get; init; } = string.Empty;

    public IReadOnlyList<ModifierStepPanelState> Steps { get; init; } = Array.Empty<ModifierStepPanelState>();
}

internal sealed class ModifierStepPanelState
{
    public int Index { get; init; }

    public Guid StepId { get; init; }

    public bool Enabled { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string FullPath { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;

    public IReadOnlyList<ModifierStepInputPanelState> Inputs { get; init; } = Array.Empty<ModifierStepInputPanelState>();

    public IReadOnlyList<ModifierStepOutputPanelState> Outputs { get; init; } = Array.Empty<ModifierStepOutputPanelState>();
}

internal enum ModifierIoKind
{
    Number,
    NumberSlider,
    Point,
    String,
    Boolean,
    Color,
    Geometry,
    ValueList,
}

internal sealed class ModifierStepInputPanelState
{
    public string Id { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public ModifierIoKind Kind { get; init; }

    public string SerializedValue { get; init; } = string.Empty;

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    public int DecimalPlaces { get; init; }

    public bool IsReadOnly { get; init; }

    public bool HasLink { get; init; }

    public bool IsLinkBroken { get; init; }

    public string LinkSourceStepLabel { get; init; } = string.Empty;

    public string LinkSourceOutputLabel { get; init; } = string.Empty;

    public string LinkStatusMessage { get; init; } = string.Empty;

    public IReadOnlyList<ModifierInputLinkOptionPanelState> AvailableLinks { get; init; } = Array.Empty<ModifierInputLinkOptionPanelState>();

    public IReadOnlyList<string> ValueListItems { get; init; } = Array.Empty<string>();

    public bool IsFilePath { get; init; }

    public bool IsMissingRequiredValue { get; init; }

    public string ValidationMessage { get; init; } = string.Empty;

    public bool ShowModifiedGeometryToggle { get; init; }

    public bool UseModifiedGeometry { get; init; }

    public Guid? ModifiedGeometrySourceObjectId { get; init; }
}

internal sealed class ModifierInputLinkOptionPanelState
{
    public Guid SourceStepId { get; init; }

    public int SourceStepIndex { get; init; }

    public string SourceStepLabel { get; init; } = string.Empty;

    public string SourceOutputId { get; init; } = string.Empty;

    public string SourceOutputLabel { get; init; } = string.Empty;

    public ModifierIoKind Kind { get; init; }

    public bool HasRuntimeValue { get; init; }

    public string RuntimeDisplayValue { get; init; } = string.Empty;

    public bool IsSelected { get; init; }
}

internal sealed class ModifierStepOutputPanelState
{
    public string Id { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public ModifierIoKind Kind { get; init; }

    public string DisplayValue { get; init; } = string.Empty;
}
