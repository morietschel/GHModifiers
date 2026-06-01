using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.UI;
using RhinoModifiers.Models;
using RhinoModifiers.Runtime;

namespace RhinoModifiers.UI;

[Guid("9D0B9A10-3A8E-46FA-B6C2-4E004A80A29B")]
public sealed class ModifierStackPanel : Panel
{
    private const int MaxSliderResolution = 10000;
    private const int LinkButtonWidth = 32;
    private const int IconButtonSize = 24;
    private const int ToolbarSpacing = 4;
    private const int PanelPadding = 12;
    private const int SectionSpacing = 8;
    private const int RowHeaderHorizontalPadding = 4;
    private const int RowHeaderVerticalPadding = 4;
    private const int StepDetailIndent = 20;
    private const int StepDetailSpacing = 8;
    private const int MaxOutputPreviewCharacters = 40;
    private const float DisclosureGlyphFontSize = 15f;

    private readonly Label _statusLabel;
    private readonly Scrollable _rowsScrollable;
    private readonly DropDown _definitionPicker;
    private readonly Button _addButton;
    private readonly Button _refreshButton;
    private readonly Button _editSelectedButton;
    private readonly Button _moveUpSelectedButton;
    private readonly Button _moveDownSelectedButton;
    private readonly Button _applySelectedButton;
    private readonly Button _deleteSelectedButton;
    private readonly Button _bakeButton;
    private readonly List<string> _importedDefinitionPaths = new();
    private readonly List<DefinitionChoice> _definitionChoices = new();
    private readonly HashSet<string> _expandedStepKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selectedStepKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedSectionKeys = new(StringComparer.Ordinal);
    private string? _lastPrimarySelectedStepKey;

    private bool _isUpdatingDefinitionPicker;

    private enum InputEditorKind
    {
        Text,
        FilePath,
        Number,
        Slider,
        Toggle,
        Point,
        Geometry,
        DropDown,
    }

    private readonly record struct NumericSliderConfiguration(
        double Minimum,
        double Maximum,
        int Steps,
        int DecimalPlaces
    )
    {
        public double GetValue(int sliderValue)
        {
            if (Steps <= 0 || Maximum <= Minimum)
            {
                return Minimum;
            }

#if NET48
            var clampedValue = sliderValue.Clamp(0, Steps);
#else
            var clampedValue = Math.Clamp(sliderValue, 0, Steps);
#endif
            var progress = (double)clampedValue / Steps;
            return RoundNumber(Minimum + ((Maximum - Minimum) * progress), DecimalPlaces);
        }

        public int GetSliderValue(double actualValue)
        {
            if (Steps <= 0 || Maximum <= Minimum)
            {
                return 0;
            }

#if NET48
            var clampedValue = actualValue.Clamp(Minimum, Maximum);
#else
            var clampedValue = Math.Clamp(actualValue, Minimum, Maximum);
#endif
            var progress = (clampedValue - Minimum) / (Maximum - Minimum);
            return (int)Math.Round(progress * Steps, MidpointRounding.AwayFromZero);
        }
    }

    private readonly record struct DefinitionChoice(string FullPath, string DisplayName);

    public ModifierStackPanel()
    {
        _definitionPicker = new DropDown
        {
            ToolTip = "Search modifiers by name, then pick one to add.",
        };
        _definitionPicker.SelectedIndexChanged += OnDefinitionSelected;

        // Set initial placeholder
        _definitionPicker.DataStore = new[] { "Add Modifier..." };
        _definitionPicker.SelectedIndex = 0;

        _addButton = CreateIconButton(
            LoadRhinoIcon("Rhino.UI.Resources.svg.File_Open.svg"),
            "Import modifier from file",
            OnAddClicked
        );

        _refreshButton = CreateIconButton(
            LoadRhinoIcon("Rhino.UI.Resources.Refresh.svg"),
            "Refresh the current modifier stack.",
            OnRefreshClicked
        );

        _editSelectedButton = CreateIconButton(
            LoadRhinoIcon("Rhino.UI.Resources.pencil.svg"),
            "Open selected modifier definitions in Grasshopper.",
            OnEditSelectedClicked
        );

        _moveUpSelectedButton = CreateIconButton(
            LoadRhinoIcon("Rhino.UI.Resources.plugin-sort-up.png"),
            "Move selected modifiers up.",
            (_, _) => MoveSelectedSteps(-1)
        );

        _moveDownSelectedButton = CreateIconButton(
            LoadRhinoIcon("Rhino.UI.Resources.plugin-sort-down.png"),
            "Move selected modifiers down.",
            (_, _) => MoveSelectedSteps(1)
        );

        _applySelectedButton = CreateIconButton(
            LoadRhinoIcon("Rhino.UI.Resources.checkmark.png"),
            "Apply selected modifier.",
            OnApplySelectedClicked
        );

        _deleteSelectedButton = CreateIconButton(
            LoadRhinoIcon("Rhino.UI.Resources.Delete.svg"),
            "Remove selected modifiers.",
            OnDeleteSelectedClicked
        );

        _bakeButton = new Button
        {
            Text = "Bake",
            ToolTip = "Create new Rhino geometry from the final stack result.",
        };
        _bakeButton.Click += OnBakeClicked;

        _statusLabel = new Label
        {
            Text = string.Empty,
            Wrap = WrapMode.Word,
            Visible = false,
        };

        _rowsScrollable = new Scrollable { ExpandContentWidth = true };

        var pickerRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Items = { new StackLayoutItem(_definitionPicker, true) },
        };

        var actionRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = ToolbarSpacing,
            Items =
            {
                _addButton,
                _refreshButton,
                _editSelectedButton,
                _moveUpSelectedButton,
                _moveDownSelectedButton,
                _applySelectedButton,
                _deleteSelectedButton,
                new StackLayoutItem(new Panel(), true),
            },
        };

        var footerRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = ToolbarSpacing,
            Items = { _bakeButton, new StackLayoutItem(new Panel(), true) },
        };

        Content = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Padding = PanelPadding,
            Spacing = SectionSpacing,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(actionRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(pickerRow, HorizontalAlignment.Stretch),
                _statusLabel,
                new StackLayoutItem(_rowsScrollable, expand: true)
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                },
                new StackLayoutItem(footerRow, HorizontalAlignment.Stretch),
            },
        };

        RhinoModifiersPlugin.Instance.Engine.StateChanged += OnEngineStateChanged;
        RefreshView();
    }

    public void RefreshNow()
    {
        RefreshView();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            RhinoModifiersPlugin.Instance.Engine.StateChanged -= OnEngineStateChanged;
        }

        base.Dispose(disposing);
    }

    private void OnEngineStateChanged(object? sender, EventArgs e)
    {
        Application.Instance?.AsyncInvoke(RefreshView);
    }

    private static Image? LoadRhinoIcon(string resourceName)
    {
        try
        {
            var assembly = typeof(Rhino.UI.RhinoEtoApp).Assembly;

            if (resourceName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var svg = Rhino.UI.ImageResources.SvgFromResourceId(resourceName, assembly);
                if (!string.IsNullOrEmpty(svg))
                {
                    return Rhino.UI.ImageResources.CreateEtoBitmap(
                        svg,
                        IconButtonSize,
                        IconButtonSize,
                        true
                    );
                }

                return null;
            }

            var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return null;
            }

            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static Button CreateIconButton(
        Image? icon,
        string toolTip,
        EventHandler<EventArgs> clickHandler
    )
    {
        var button = new Button
        {
            Image = icon,
            ImagePosition = ButtonImagePosition.Overlay,
            ToolTip = toolTip,
            Width = IconButtonSize,
            Height = IconButtonSize,
        };
        button.Click += clickHandler;
        return button;
    }

    private void OnAddClicked(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        var state = RhinoModifiersPlugin.Instance.Engine.GetPanelState(doc);
        if (!state.CanEdit || !state.SelectedObjectId.HasValue || doc is null)
        {
            return;
        }

        using var dialog = new Eto.Forms.OpenFileDialog
        {
            Title = "Add Grasshopper Modifier",
            MultiSelect = false,
        };
        dialog.Filters.Add(new FileFilter("Grasshopper Definitions", ".gh", ".ghx"));

        if (
            dialog.ShowDialog(RhinoEtoApp.MainWindow) != DialogResult.Ok
            || string.IsNullOrWhiteSpace(dialog.FileName)
        )
        {
            return;
        }

        TryAddStep(state, dialog.FileName);
    }

    private void OnRefreshClicked(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            return;
        }

        if (!RhinoModifiersPlugin.Instance.Engine.RefreshSelectedObject(doc, out var message))
        {
            MessageBox.Show(message, MessageBoxType.Warning);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }
    }

    private void OnBakeClicked(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        var state = RhinoModifiersPlugin.Instance.Engine.GetPanelState(doc);
        if (!state.CanEdit || !state.SelectedObjectId.HasValue || doc is null)
        {
            return;
        }

        if (
            !RhinoModifiersPlugin.Instance.Engine.BakeFinalResult(
                doc,
                state.SelectedObjectId.Value,
                out var message
            )
        )
        {
            MessageBox.Show(message, MessageBoxType.Error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }
    }

    private void OnDefinitionSelected(object? sender, EventArgs e)
    {
        if (_isUpdatingDefinitionPicker)
        {
            return;
        }

        var selectedIndex = _definitionPicker.SelectedIndex;

        // Placeholder is at index 0, ignore it
        if (selectedIndex <= 0)
        {
            ResetDefinitionPicker();
            return;
        }

        // Subtract 1 because placeholder is at index 0
        var definitionIndex = selectedIndex - 1;
        if (definitionIndex < 0 || definitionIndex >= _definitionChoices.Count)
        {
            ResetDefinitionPicker();
            return;
        }

        var state = RhinoModifiersPlugin.Instance.Engine.GetPanelState(RhinoDoc.ActiveDoc);
        if (!state.CanEdit || !state.SelectedObjectId.HasValue)
        {
            ResetDefinitionPicker();
            return;
        }

        TryAddStep(state, _definitionChoices[definitionIndex].FullPath);
        ResetDefinitionPicker();
    }

    private void RefreshView()
    {
        var state = RhinoModifiersPlugin.Instance.Engine.GetPanelState(RhinoDoc.ActiveDoc);

        SyncDefinitionChoices(state);
        NormalizeExpandedSteps(state);
        NormalizeSelectedSteps(state);
        NormalizeCollapsedSections(state);

        var canEdit = state.CanEdit && state.SelectedObjectId.HasValue;
        var canRefresh = canEdit && state.Steps.Count > 0;
        var canBake = canEdit && state.Steps.Count > 0;
        var selectedSteps = GetSelectedSteps(state);

        _definitionPicker.Enabled = canEdit && _definitionChoices.Count > 0;
        _addButton.Enabled = canEdit;
        _refreshButton.Enabled = canRefresh;
        _bakeButton.Enabled = canBake;
        _editSelectedButton.Enabled =
            canEdit && selectedSteps.Any(step => !string.IsNullOrWhiteSpace(step.FullPath));
        _moveUpSelectedButton.Enabled = canEdit && selectedSteps.Any(step => step.Index > 0);
        _moveDownSelectedButton.Enabled =
            canEdit && selectedSteps.Any(step => step.Index < state.Steps.Count - 1);
        _applySelectedButton.Enabled = canEdit && selectedSteps.Count == 1;
        _deleteSelectedButton.Enabled = canEdit && selectedSteps.Count > 0;

        _statusLabel.Visible = canEdit && !string.IsNullOrWhiteSpace(state.StatusMessage);
        _statusLabel.Text = state.StatusMessage;

        var rows = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        if (!state.CanEdit)
        {
            rows.Items.Add(
                new StackLayoutItem(
                    CreateCenteredMessage(
                        string.IsNullOrWhiteSpace(state.StatusMessage)
                            ? "Select one object to edit its modifier stack."
                            : state.StatusMessage
                    ),
                    HorizontalAlignment.Stretch
                )
            );
        }
        else if (state.Steps.Count == 0)
        {
            rows.Items.Add(
                new StackLayoutItem(
                    CreateCenteredMessage(
                        "No modifiers added yet.\n\nSearch above, or add a modifier\nfrom file to begin"
                    ),
                    HorizontalAlignment.Stretch
                )
            );
        }
        else if (state.SelectedObjectId.HasValue)
        {
            for (var i = 0; i < state.Steps.Count; i++)
            {
                var step = state.Steps[i];
                rows.Items.Add(
                    new StackLayoutItem(
                        CreateStepRow(state.SelectedObjectId.Value, step),
                        HorizontalAlignment.Stretch
                    )
                );
            }
        }

        // Add a clickable spacer so clicking empty list space deselects.
        var spacerPanel = new Panel();
        spacerPanel.MouseDown += (_, _) => ClearSelection();
        rows.Items.Add(
            new StackLayoutItem(spacerPanel, true)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
            }
        );

        _rowsScrollable.Content = rows;
    }

    private bool TryAddStep(ModifierPanelState state, string path)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            return false;
        }

        if (!state.SelectedObjectId.HasValue)
        {
            MessageBox.Show(
                "Select an object first to add a modifier step.",
                MessageBoxType.Warning
            );
            return false;
        }

        if (
            !RhinoModifiersPlugin.Instance.Engine.AddStep(
                doc,
                state.SelectedObjectId.Value,
                path,
                out var message
            )
        )
        {
            MessageBox.Show(message, MessageBoxType.Error);
            return false;
        }

        RememberImportedDefinitions(path);

        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }

        RefreshView();
        return true;
    }

    private void RememberImportedDefinitions(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var pathsToRemember = new List<string> { path };
        var folder = Path.GetDirectoryName(path);

        if (!string.IsNullOrWhiteSpace(folder))
        {
            try
            {
                pathsToRemember.AddRange(
                    Directory.EnumerateFiles(folder, "*.gh", SearchOption.AllDirectories)
                );
                pathsToRemember.AddRange(
                    Directory.EnumerateFiles(folder, "*.ghx", SearchOption.AllDirectories)
                );
            }
            catch
            {
                // Keep the chosen file even if scanning the folder fails.
            }
        }

        foreach (
            var candidate in pathsToRemember.Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate)
            )
        )
        {
            if (
                _importedDefinitionPaths.Any(existing =>
                    string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                continue;
            }

            _importedDefinitionPaths.Add(candidate);
        }

        _importedDefinitionPaths.Sort(StringComparer.OrdinalIgnoreCase);
    }

    private void SyncDefinitionChoices(ModifierPanelState state)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is not null)
        {
            foreach (var rhinoObject in doc.Objects)
            {
                var spec = ModifierStackStorage.Load(rhinoObject);
                foreach (
                    var path in spec
                        .Steps.Select(step => step.Path)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                )
                {
                    if (
                        _importedDefinitionPaths.Any(existing =>
                            string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)
                        )
                    )
                    {
                        continue;
                    }

                    _importedDefinitionPaths.Add(path);
                }
            }
        }

        foreach (
            var path in state
                .Steps.Select(step => step.FullPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
        )
        {
            if (
                _importedDefinitionPaths.Any(existing =>
                    string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                continue;
            }

            _importedDefinitionPaths.Add(path);
        }

        var uniquePaths = _importedDefinitionPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var duplicateNames = uniquePaths
            .GroupBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _definitionChoices.Clear();
        foreach (var path in uniquePaths)
        {
            _definitionChoices.Add(
                new DefinitionChoice(path, BuildDefinitionDisplayName(path, duplicateNames))
            );
        }

        UpdateDefinitionPickerFilter(string.Empty);
    }

    private static string BuildDefinitionDisplayName(string path, ISet<string> duplicateNames)
    {
        var fileName = Path.GetFileName(path);
        if (!duplicateNames.Contains(fileName))
        {
            return fileName;
        }

        var folderName = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
        return string.IsNullOrWhiteSpace(folderName) ? fileName : $"{fileName} ({folderName})";
    }

    private void ResetDefinitionPicker()
    {
        _isUpdatingDefinitionPicker = true;
        _definitionPicker.SelectedIndex = 0; // Select placeholder
        _isUpdatingDefinitionPicker = false;
    }

    private void UpdateDefinitionPickerFilter(string? searchText)
    {
        _isUpdatingDefinitionPicker = true;
        var items = new List<string> { "Add Modifier..." }; // Placeholder first
        items.AddRange(_definitionChoices.Select(choice => choice.DisplayName));
        _definitionPicker.DataStore = items;
        _definitionPicker.SelectedIndex = 0; // Select placeholder
        _isUpdatingDefinitionPicker = false;
    }

    private void NormalizeExpandedSteps(ModifierPanelState state)
    {
        var validKeys = state.Steps.Select(GetStepKey).ToHashSet(StringComparer.Ordinal);
        _expandedStepKeys.RemoveWhere(key => !validKeys.Contains(key));

        if (_expandedStepKeys.Count > 0)
        {
            return;
        }

        var firstProblemStep = state.Steps.FirstOrDefault(step =>
            !string.IsNullOrWhiteSpace(step.ErrorMessage)
        );
        if (firstProblemStep is not null)
        {
            _expandedStepKeys.Add(GetStepKey(firstProblemStep));
        }
    }

    private void NormalizeSelectedSteps(ModifierPanelState state)
    {
        var validKeys = state.Steps.Select(GetStepKey).ToHashSet(StringComparer.Ordinal);
        _selectedStepKeys.RemoveWhere(key => !validKeys.Contains(key));

        var primarySelectedKey = _lastPrimarySelectedStepKey;
        if (primarySelectedKey is not null && !validKeys.Contains(primarySelectedKey))
        {
            _lastPrimarySelectedStepKey = null;
        }
    }

    private List<ModifierStepPanelState> GetSelectedSteps(ModifierPanelState state)
    {
        if (_selectedStepKeys.Count == 0)
        {
            return new List<ModifierStepPanelState>();
        }

        return state
            .Steps.Where(step => _selectedStepKeys.Contains(GetStepKey(step)))
            .OrderBy(step => step.Index)
            .ToList();
    }

    private void SelectStep(ModifierStepPanelState step, bool additive, bool range)
    {
        var state = RhinoModifiersPlugin.Instance.Engine.GetPanelState(RhinoDoc.ActiveDoc);
        var stepKey = GetStepKey(step);

        if (range)
        {
            var anchorKey = _lastPrimarySelectedStepKey;
            if (string.IsNullOrWhiteSpace(anchorKey))
            {
                _selectedStepKeys.Clear();
                _selectedStepKeys.Add(stepKey);
                _lastPrimarySelectedStepKey = stepKey;
                RefreshView();
                return;
            }

            var anchorIndex =
                state
                    .Steps.Select((candidate, index) => new { Step = candidate, Index = index })
                    .FirstOrDefault(candidate =>
                        string.Equals(
                            GetStepKey(candidate.Step),
                            anchorKey,
                            StringComparison.Ordinal
                        )
                    )
                    ?.Index
                ?? -1;
            var clickedIndex =
                state
                    .Steps.Select((candidate, index) => new { Step = candidate, Index = index })
                    .FirstOrDefault(candidate =>
                        string.Equals(GetStepKey(candidate.Step), stepKey, StringComparison.Ordinal)
                    )
                    ?.Index
                ?? -1;

            if (anchorIndex < 0 || clickedIndex < 0)
            {
                _selectedStepKeys.Clear();
                _selectedStepKeys.Add(stepKey);
                _lastPrimarySelectedStepKey = stepKey;
                RefreshView();
                return;
            }

            if (!additive)
            {
                _selectedStepKeys.Clear();
            }

            var minIndex = Math.Min(anchorIndex, clickedIndex);
            var maxIndex = Math.Max(anchorIndex, clickedIndex);
            for (var i = minIndex; i <= maxIndex; i++)
            {
                _selectedStepKeys.Add(GetStepKey(state.Steps[i]));
            }

            RefreshView();
            return;
        }

        if (additive)
        {
            if (_selectedStepKeys.Contains(stepKey))
            {
                _selectedStepKeys.Remove(stepKey);
            }
            else
            {
                _selectedStepKeys.Add(stepKey);
            }
        }
        else
        {
            _selectedStepKeys.Clear();
            _selectedStepKeys.Add(stepKey);
        }

        _lastPrimarySelectedStepKey = stepKey;

        RefreshView();
    }

    private void ClearSelection()
    {
        if (_selectedStepKeys.Count == 0)
        {
            return;
        }

        _selectedStepKeys.Clear();
        _lastPrimarySelectedStepKey = null;
        RefreshView();
    }

    private static bool IsAdditiveSelection(MouseEventArgs e)
    {
        return (e.Modifiers & Keys.Application) == Keys.Application
            || (e.Modifiers & Keys.Control) == Keys.Control;
    }

    private static bool IsRangeSelection(MouseEventArgs e)
    {
        return (e.Modifiers & Keys.Shift) == Keys.Shift;
    }

    private static string GetStepKey(ModifierStepPanelState step)
    {
        return step.StepId != Guid.Empty
            ? step.StepId.ToString("N")
            : $"{step.Index}:{step.FullPath}";
    }

    private static string GetDisclosureGlyph(bool isExpanded)
    {
        return isExpanded ? "v" : ">";
    }

    private bool IsStepExpanded(ModifierStepPanelState step)
    {
        return _expandedStepKeys.Contains(GetStepKey(step));
    }

    private static string GetSectionKey(ModifierStepPanelState step, string sectionName)
    {
        return $"{GetStepKey(step)}:{sectionName}";
    }

    private bool IsSectionExpanded(ModifierStepPanelState step, string sectionName)
    {
        return !_collapsedSectionKeys.Contains(GetSectionKey(step, sectionName));
    }

    private void ToggleSectionExpanded(ModifierStepPanelState step, string sectionName)
    {
        var key = GetSectionKey(step, sectionName);
        if (_collapsedSectionKeys.Contains(key))
        {
            _collapsedSectionKeys.Remove(key);
        }
        else
        {
            _collapsedSectionKeys.Add(key);
        }

        RefreshView();
    }

    private void ToggleExpanded(ModifierStepPanelState step)
    {
        var stepKey = GetStepKey(step);
        if (_expandedStepKeys.Contains(stepKey))
        {
            _expandedStepKeys.Remove(stepKey);
        }
        else
        {
            _expandedStepKeys.Clear();
            _expandedStepKeys.Add(stepKey);
        }

        RefreshView();
    }

    private void NormalizeCollapsedSections(ModifierPanelState state)
    {
        var validKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in state.Steps)
        {
            if (step.Inputs.Count > 0)
            {
                validKeys.Add(GetSectionKey(step, "inputs"));
            }

            if (step.Outputs.Count > 0)
            {
                validKeys.Add(GetSectionKey(step, "outputs"));
            }
        }

        _collapsedSectionKeys.RemoveWhere(key => !validKeys.Contains(key));
    }

    private Control CreateSectionHeader(string title, bool isExpanded, Action toggle)
    {
        var disclosureButton = new Button
        {
            Text = GetDisclosureGlyph(isExpanded),
            ToolTip = isExpanded ? "Collapse" : "Expand",
            Font = new Font(SystemFont.Default, DisclosureGlyphFontSize),
            Width = 24,
            Height = 20,
        };
        disclosureButton.Click += (_, _) => toggle();

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                disclosureButton,
                new StackLayoutItem(
                    new Label
                    {
                        Text = title,
                        TextAlignment = TextAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    true
                ),
            },
        };
    }

    private Control CreateStepRow(Guid objectId, ModifierStepPanelState step)
    {
        var isExpanded = IsStepExpanded(step);
        var isSelected = _selectedStepKeys.Contains(GetStepKey(step));
        var headerBackground = isSelected ? SystemColors.Highlight : Colors.Transparent;
        var headerTextColor = isSelected ? SystemColors.HighlightText : SystemColors.ControlText;
        var container = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        // Header row: checkbox | name (fills width) | disclosure arrow
        var disclosureButton = new Button
        {
            Text = GetDisclosureGlyph(isExpanded),
            ToolTip = isExpanded ? "Collapse" : "Expand",
            Font = new Font(SystemFont.Default, DisclosureGlyphFontSize),
            Width = 28,
            Height = 20,
        };
        disclosureButton.Click += (_, _) => ToggleExpanded(step);

        var stepNameLabel = CreateStepNameLabel(step, headerTextColor);
        var enabledCheckBox = CreateStepEnabledCheckBox(objectId, step);
        var stepNameHost = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items = { stepNameLabel, new StackLayoutItem(new Panel(), true) },
        };

        var headerRow = new TableLayout
        {
            Padding = new Padding(RowHeaderHorizontalPadding, RowHeaderVerticalPadding),
            Spacing = new Size(4, 0),
            Rows =
            {
                new TableRow(
                    new TableCell(enabledCheckBox),
                    new TableCell(stepNameHost, scaleWidth: true),
                    new TableCell(disclosureButton)
                ),
            },
        };

        var headerContainer = new Panel { Content = headerRow, BackgroundColor = headerBackground };

        headerContainer.MouseDown += (_, e) =>
            SelectStep(step, IsAdditiveSelection(e), IsRangeSelection(e));
        headerRow.MouseDown += (_, e) =>
            SelectStep(step, IsAdditiveSelection(e), IsRangeSelection(e));

        container.Items.Add(new StackLayoutItem(headerContainer, HorizontalAlignment.Stretch));

        // Separator line
        container.Items.Add(
            new StackLayoutItem(
                new Panel { Height = 1, BackgroundColor = Colors.LightGrey },
                HorizontalAlignment.Stretch
            )
        );

        if (!string.IsNullOrWhiteSpace(step.ErrorMessage))
        {
            container.Items.Add(
                new StackLayout
                {
                    Padding = new Padding(StepDetailIndent, 2, 0, 0),
                    Items = { CreateMessageLabel(step.ErrorMessage, isError: true) },
                }
            );
        }

        // Expanded content: inputs indented like sub-layers
        if (isExpanded)
        {
            var childContent = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = StepDetailSpacing,
                Padding = new Padding(StepDetailIndent, 6, 0, 6),
            };

            if (step.Inputs.Count > 0)
            {
                var inputsExpanded = IsSectionExpanded(step, "inputs");
                childContent.Items.Add(
                    new StackLayoutItem(
                        CreateSectionHeader(
                            "Inputs",
                            inputsExpanded,
                            () => ToggleSectionExpanded(step, "inputs")
                        ),
                        HorizontalAlignment.Stretch
                    )
                );

                if (inputsExpanded)
                {
                    var inputsContent = new StackLayout
                    {
                        Orientation = Orientation.Vertical,
                        Spacing = StepDetailSpacing,
                        Padding = new Padding(20, 0, 0, 0),
                    };

                    foreach (var input in step.Inputs)
                    {
                        inputsContent.Items.Add(
                            new StackLayoutItem(
                                CreateInputRow(objectId, step, input),
                                HorizontalAlignment.Stretch
                            )
                        );
                    }

                    childContent.Items.Add(
                        new StackLayoutItem(inputsContent, HorizontalAlignment.Stretch)
                    );
                }
            }

            if (step.Outputs.Count > 0)
            {
                var outputsExpanded = IsSectionExpanded(step, "outputs");
                childContent.Items.Add(
                    new StackLayoutItem(
                        CreateSectionHeader(
                            "Outputs",
                            outputsExpanded,
                            () => ToggleSectionExpanded(step, "outputs")
                        ),
                        HorizontalAlignment.Stretch
                    )
                );

                if (outputsExpanded)
                {
                    var outputsContent = new StackLayout
                    {
                        Orientation = Orientation.Vertical,
                        Spacing = 0,
                        Padding = new Padding(20, 0, 0, 0),
                    };
                    outputsContent.Items.Add(
                        new StackLayoutItem(
                            CreateOutputSummaryRow(step.Outputs),
                            HorizontalAlignment.Stretch
                        )
                    );
                    childContent.Items.Add(
                        new StackLayoutItem(outputsContent, HorizontalAlignment.Stretch)
                    );
                }
            }

            container.Items.Add(new StackLayoutItem(childContent, HorizontalAlignment.Stretch));
        }

        return container;
    }

    private static CheckBox CreateStepEnabledCheckBox(Guid objectId, ModifierStepPanelState step)
    {
        var enabledCheckBox = new CheckBox { Checked = step.Enabled };

        enabledCheckBox.CheckedChanged += (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc is null)
            {
                return;
            }

            RhinoModifiersPlugin.Instance.Engine.SetStepEnabled(
                doc,
                objectId,
                step.Index,
                enabledCheckBox.Checked == true,
                out var message
            );

            if (!string.IsNullOrWhiteSpace(message))
            {
                RhinoApp.WriteLine(message);
            }
        };

        return enabledCheckBox;
    }

    private static Label CreateStepNameLabel(ModifierStepPanelState step, Color textColor)
    {
        return new Label
        {
            Text = step.DisplayName,
            ToolTip = step.FullPath,
            TextAlignment = TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Wrap = WrapMode.None,
            TextColor = textColor,
        };
    }

    private void OnEditSelectedClicked(object? sender, EventArgs e)
    {
        var state = RhinoModifiersPlugin.Instance.Engine.GetPanelState(RhinoDoc.ActiveDoc);
        foreach (var step in GetSelectedSteps(state))
        {
            if (!string.IsNullOrWhiteSpace(step.FullPath))
            {
                EditStepDefinition(step.FullPath);
            }
        }
    }

    private void OnApplySelectedClicked(object? sender, EventArgs e)
    {
        var state = RhinoModifiersPlugin.Instance.Engine.GetPanelState(RhinoDoc.ActiveDoc);
        var selectedSteps = GetSelectedSteps(state);
        if (!state.SelectedObjectId.HasValue || selectedSteps.Count != 1)
        {
            return;
        }

        ApplyStep(state.SelectedObjectId.Value, selectedSteps[0].Index);
    }

    private void OnDeleteSelectedClicked(object? sender, EventArgs e)
    {
        var state = RhinoModifiersPlugin.Instance.Engine.GetPanelState(RhinoDoc.ActiveDoc);
        if (!state.SelectedObjectId.HasValue)
        {
            return;
        }

        var selectedSteps = GetSelectedSteps(state).OrderByDescending(step => step.Index).ToList();

        foreach (var step in selectedSteps)
        {
            RemoveStep(state.SelectedObjectId.Value, step.Index);
        }

        _selectedStepKeys.Clear();
        RefreshView();
    }

    private void MoveSelectedSteps(int offset)
    {
        var state = RhinoModifiersPlugin.Instance.Engine.GetPanelState(RhinoDoc.ActiveDoc);
        if (!state.SelectedObjectId.HasValue || offset == 0)
        {
            return;
        }

        var orderedSteps =
            offset < 0
                ? GetSelectedSteps(state).OrderBy(step => step.Index)
                : GetSelectedSteps(state).OrderByDescending(step => step.Index);

        foreach (var step in orderedSteps)
        {
            if (offset < 0 && step.Index == 0)
            {
                continue;
            }

            if (offset > 0 && step.Index == state.Steps.Count - 1)
            {
                continue;
            }

            MoveStep(state.SelectedObjectId.Value, step.Index, offset);
        }

        RefreshView();
    }

    private static Label CreateMessageLabel(string text, bool isError)
    {
        return new Label
        {
            Text = text,
            Wrap = WrapMode.Word,
            TextColor = isError ? Eto.Drawing.Colors.OrangeRed : Eto.Drawing.Colors.Gray,
        };
    }

    private static Control CreateCenteredMessage(string text)
    {
        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Padding = new Padding(16, 56, 16, 0),
            Items =
            {
                new Label
                {
                    Text = text,
                    Wrap = WrapMode.Word,
                    TextAlignment = TextAlignment.Center,
                },
            },
        };
    }

    private Control CreateInputRow(
        Guid objectId,
        ModifierStepPanelState step,
        ModifierStepInputPanelState input
    )
    {
        var toolTip = BuildInputToolTip(input);
        var editorKind = GetEditorKind(input);

        return editorKind switch
        {
            InputEditorKind.Toggle => CreateToggleInputRow(objectId, step, input, toolTip),
            InputEditorKind.Slider => CreateInputBlock(
                objectId,
                step,
                input,
                CreateSliderEditor(objectId, step, input),
                toolTip
            ),
            InputEditorKind.Number => CreateInputBlock(
                objectId,
                step,
                input,
                CreateNumericEditor(objectId, step, input),
                toolTip
            ),
            InputEditorKind.Point => CreateInputBlock(
                objectId,
                step,
                input,
                CreatePointEditor(objectId, step, input),
                AppendToolTip(toolTip, GetPointDisplayText(input.SerializedValue))
            ),
            InputEditorKind.Geometry => CreateInputBlock(
                objectId,
                step,
                input,
                CreateGeometryEditor(objectId, step, input),
                AppendToolTip(toolTip, GetGeometryDisplayText(input))
            ),
            InputEditorKind.DropDown => CreateInputBlock(
                objectId,
                step,
                input,
                CreateDropDownEditor(objectId, step, input),
                toolTip
            ),
            InputEditorKind.FilePath => CreateInputBlock(
                objectId,
                step,
                input,
                CreateFilePathEditor(objectId, step, input),
                toolTip
            ),
            _ => CreateInputBlock(
                objectId,
                step,
                input,
                CreateTextEditor(objectId, step, input),
                toolTip
            ),
        };
    }

    private Control CreateToggleInputRow(
        Guid objectId,
        ModifierStepPanelState step,
        ModifierStepInputPanelState input,
        string? toolTip
    )
    {
        var toggle = new CheckBox
        {
            Checked = bool.TryParse(input.SerializedValue, out var boolValue) && boolValue,
            Enabled = IsInputEnabled(step, input),
            ToolTip = toolTip,
        };

        toggle.CheckedChanged += (_, _) =>
            CommitInput(objectId, step.Index, input, toggle.Checked == true ? "true" : "false");

        var block = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Items =
            {
                new StackLayoutItem(
                    CreateInputHeader(objectId, step, input, toolTip),
                    HorizontalAlignment.Stretch
                ),
                toggle,
            },
        };

        AddInputMessages(block, input);
        return block;
    }

    private Control CreateInputBlock(
        Guid objectId,
        ModifierStepPanelState step,
        ModifierStepInputPanelState input,
        Control editor,
        string? toolTip
    )
    {
        SetToolTip(editor, toolTip);

        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                new Label
                {
                    Text = $"{input.Label}:",
                    Wrap = WrapMode.Word,
                    ToolTip = toolTip,
                },
                new StackLayoutItem(editor, true),
                new StackLayoutItem(
                    CreateInputLinkButton(objectId, step, input),
                    HorizontalAlignment.Right
                ),
            },
        };

        var block = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Items = { new StackLayoutItem(row, HorizontalAlignment.Stretch) },
        };

        AddInputMessages(block, input);
        return block;
    }

    private Control CreateInputHeader(
        Guid objectId,
        ModifierStepPanelState step,
        ModifierStepInputPanelState input,
        string? toolTip
    )
    {
        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                new StackLayoutItem(
                    new Label
                    {
                        Text = input.Label,
                        Wrap = WrapMode.Word,
                        ToolTip = toolTip,
                    },
                    true
                ),
                new StackLayoutItem(
                    CreateInputLinkButton(objectId, step, input),
                    HorizontalAlignment.Right
                ),
            },
        };

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Items = { new StackLayoutItem(row, HorizontalAlignment.Stretch) },
        };
    }

    private static void AddInputMessages(StackLayout block, ModifierStepInputPanelState input)
    {
        if (!string.IsNullOrWhiteSpace(input.LinkStatusMessage))
        {
            block.Items.Add(
                CreateMessageLabel(input.LinkStatusMessage, isError: input.IsLinkBroken)
            );
        }

        if (input.IsMissingRequiredValue)
        {
            block.Items.Add(CreateMessageLabel(input.ValidationMessage, isError: true));
        }
    }

    private Button CreateInputLinkButton(
        Guid objectId,
        ModifierStepPanelState step,
        ModifierStepInputPanelState input
    )
    {
        var linkButton = new Button
        {
            Text = input.HasLink ? "●" : "○",
            Width = LinkButtonWidth,
            ToolTip = input.HasLink
                ? "Linked input. Click to change or clear the active link."
                : "Link this input to an upstream modifier output.",
        };

        linkButton.Click += (_, _) =>
        {
            var menu = ModifierStackPanel.BuildInputLinkMenu(objectId, step, input);
            menu.Show(linkButton);
        };

        return linkButton;
    }

    private static ContextMenu BuildInputLinkMenu(
        Guid objectId,
        ModifierStepPanelState step,
        ModifierStepInputPanelState input
    )
    {
        var menu = new ContextMenu();

        var clearItem = new ButtonMenuItem { Text = "Use manual value", Enabled = input.HasLink };
        clearItem.Click += (_, _) => ClearInputLink(objectId, step.Index, input.Id);
        menu.Items.Add(clearItem);

        menu.Items.Add(new SeparatorMenuItem());

        if (input.AvailableLinks.Count == 0)
        {
            menu.Items.Add(
                new ButtonMenuItem { Text = "No compatible upstream outputs", Enabled = false }
            );
            return menu;
        }

        foreach (
            var optionGroup in input
                .AvailableLinks.GroupBy(option => new
                {
                    option.SourceStepId,
                    option.SourceStepIndex,
                    option.SourceStepLabel,
                })
                .OrderBy(group => group.Key.SourceStepIndex)
        )
        {
            var groupItem = new ButtonMenuItem
            {
                Text = $"{optionGroup.Key.SourceStepIndex + 1}. {optionGroup.Key.SourceStepLabel}",
            };

            foreach (
                var option in optionGroup.OrderBy(
                    candidate => candidate.SourceOutputLabel,
                    StringComparer.OrdinalIgnoreCase
                )
            )
            {
                var optionItem = new ButtonMenuItem { Text = FormatInputLinkOption(option) };
                optionItem.Click += (_, _) => SetInputLink(objectId, step.Index, input.Id, option);
                groupItem.Items.Add(optionItem);
            }

            menu.Items.Add(groupItem);
        }

        return menu;
    }

    private static string FormatInputLinkOption(ModifierInputLinkOptionPanelState option)
    {
        var prefix = option.IsSelected ? "[current] " : string.Empty;
        if (option.HasRuntimeValue)
        {
            return $"{prefix}{option.SourceOutputLabel}  {option.RuntimeDisplayValue}";
        }

        return $"{prefix}{option.SourceOutputLabel}  (no runtime value yet)";
    }

    private static InputEditorKind GetEditorKind(ModifierStepInputPanelState input)
    {
        return input.Kind switch
        {
            ModifierIoKind.Boolean => InputEditorKind.Toggle,
            ModifierIoKind.NumberSlider => InputEditorKind.Slider,
            ModifierIoKind.Number when input.Minimum.HasValue && input.Maximum.HasValue =>
                InputEditorKind.Slider,
            ModifierIoKind.Number => InputEditorKind.Number,
            ModifierIoKind.Point => InputEditorKind.Point,
            ModifierIoKind.Geometry => InputEditorKind.Geometry,
            ModifierIoKind.ValueList => InputEditorKind.DropDown,
            ModifierIoKind.String when input.IsFilePath => InputEditorKind.FilePath,
            _ => InputEditorKind.Text,
        };
    }

    private static Control CreateSliderEditor(
        Guid objectId,
        ModifierStepPanelState step,
        ModifierStepInputPanelState input
    )
    {
        if (!TryCreateSliderConfiguration(input, out var configuration))
        {
            return CreateNumericEditor(objectId, step, input);
        }

        var initialValue = configuration.GetValue(
            configuration.GetSliderValue(GetInitialNumericValue(input))
        );
        var lastCommittedValue = SerializeNumber(initialValue, input.DecimalPlaces);
        var sliderToolTip = AppendToolTip(
            BuildInputToolTip(input),
            $"{FormatDisplayNumber(configuration.Minimum, input.DecimalPlaces)} to {FormatDisplayNumber(configuration.Maximum, input.DecimalPlaces)}"
        );

        var slider = new Slider
        {
            MinValue = 0,
            MaxValue = configuration.Steps,
            Value = configuration.GetSliderValue(initialValue),
            Enabled = IsInputEnabled(step, input),
            ToolTip = sliderToolTip,
        };

        void CommitSliderValue()
        {
            var actualValue = configuration.GetValue(slider.Value);
            var serializedValue = SerializeNumber(actualValue, input.DecimalPlaces);
            if (string.Equals(serializedValue, lastCommittedValue, StringComparison.Ordinal))
            {
                return;
            }

            lastCommittedValue = serializedValue;
            CommitInput(objectId, step.Index, input, serializedValue);
        }

        slider.MouseUp += (_, _) => CommitSliderValue();
        slider.LostFocus += (_, _) => CommitSliderValue();

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                new Label
                {
                    Text = FormatDisplayNumber(configuration.Minimum, input.DecimalPlaces),
                    ToolTip = sliderToolTip,
                },
                new StackLayoutItem(slider, true),
                new Label
                {
                    Text = FormatDisplayNumber(configuration.Maximum, input.DecimalPlaces),
                    ToolTip = sliderToolTip,
                },
            },
        };
    }

    private static Control CreateNumericEditor(
        Guid objectId,
        ModifierStepPanelState step,
        ModifierStepInputPanelState input
    )
    {
        var numericEditor = new NumericStepper
        {
            DecimalPlaces = input.DecimalPlaces,
            Increment = GetIncrement(input.DecimalPlaces),
            Enabled = IsInputEnabled(step, input),
            Width = 120,
        };

        if (input.Minimum.HasValue)
        {
            numericEditor.MinValue = input.Minimum.Value;
        }

        if (input.Maximum.HasValue)
        {
            numericEditor.MaxValue = input.Maximum.Value;
        }

        numericEditor.Value = GetInitialNumericValue(input);

        numericEditor.ValueChanged += (_, _) =>
            CommitInput(
                objectId,
                step.Index,
                input,
                SerializeNumber(numericEditor.Value, input.DecimalPlaces)
            );

        return numericEditor;
    }

    private static Control CreateTextEditor(
        Guid objectId,
        ModifierStepPanelState step,
        ModifierStepInputPanelState input
    )
    {
        var textEditor = new TextBox
        {
            Text = input.SerializedValue,
            ReadOnly = input.IsReadOnly,
            Enabled = IsInputEnabled(step, input),
        };

        textEditor.LostFocus += (_, _) =>
            CommitInput(objectId, step.Index, input, textEditor.Text ?? string.Empty);
        return textEditor;
    }

    private static Control CreateFilePathEditor(
        Guid objectId,
        ModifierStepPanelState step,
        ModifierStepInputPanelState input
    )
    {
        var textEditor = new TextBox
        {
            Text = input.SerializedValue,
            ReadOnly = input.IsReadOnly,
            Enabled = IsInputEnabled(step, input),
        };

        textEditor.LostFocus += (_, _) =>
            CommitInput(objectId, step.Index, input, textEditor.Text ?? string.Empty);

        void CommitPath(string path)
        {
            textEditor.Text = path;
            CommitInput(objectId, step.Index, input, path);
        }

        var openFileButton = new Button
        {
            Text = "F",
            Width = 28,
            ToolTip = "Pick existing file",
            Enabled = IsInputEnabled(step, input),
        };

        openFileButton.Click += (_, _) =>
        {
            using var dialog = new Eto.Forms.OpenFileDialog
            {
                Title = $"Select file for {input.Label}",
                MultiSelect = false,
                FileName = textEditor.Text,
            };

            if (
                dialog.ShowDialog(RhinoEtoApp.MainWindow) != DialogResult.Ok
                || string.IsNullOrWhiteSpace(dialog.FileName)
            )
            {
                return;
            }

            CommitPath(dialog.FileName);
        };

        var pickFolderButton = new Button
        {
            Text = "D",
            Width = 28,
            ToolTip = "Pick directory",
            Enabled = IsInputEnabled(step, input),
        };

        pickFolderButton.Click += (_, _) =>
        {
            using var dialog = new Eto.Forms.SelectFolderDialog
            {
                Title = $"Select folder for {input.Label}",
            };

            var currentPath = textEditor.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                if (Directory.Exists(currentPath))
                {
                    dialog.Directory = currentPath;
                }
                else if (File.Exists(currentPath))
                {
                    dialog.Directory = Path.GetDirectoryName(currentPath);
                }
            }

            if (
                dialog.ShowDialog(RhinoEtoApp.MainWindow) != DialogResult.Ok
                || string.IsNullOrWhiteSpace(dialog.Directory)
            )
            {
                return;
            }

            CommitPath(dialog.Directory);
        };

        var savePathButton = new Button
        {
            Text = "S",
            Width = 28,
            ToolTip = "Pick output path (can create new file)",
            Enabled = IsInputEnabled(step, input),
        };

        savePathButton.Click += (_, _) =>
        {
            var currentPath = textEditor.Text ?? string.Empty;
            using var dialog = new Eto.Forms.SaveFileDialog
            {
                Title = $"Select output path for {input.Label}",
            };

            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                if (File.Exists(currentPath))
                {
                    dialog.Directory = new Uri(Path.GetDirectoryName(currentPath)!);
                    dialog.FileName = Path.GetFileName(currentPath);
                }
                else if (Directory.Exists(currentPath))
                {
                    dialog.Directory = new Uri(currentPath);
                }
            }

            if (
                dialog.ShowDialog(RhinoEtoApp.MainWindow) != DialogResult.Ok
                || string.IsNullOrWhiteSpace(dialog.FileName)
            )
            {
                return;
            }

            CommitPath(dialog.FileName);
        };

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                new StackLayoutItem(textEditor, true),
                openFileButton,
                pickFolderButton,
                savePathButton,
            },
        };
    }

    private static Control CreateDropDownEditor(
        Guid objectId,
        ModifierStepPanelState step,
        ModifierStepInputPanelState input
    )
    {
        var dropDown = new DropDown { Enabled = IsInputEnabled(step, input) };

        foreach (var item in input.ValueListItems)
        {
            dropDown.Items.Add(item);
        }

        if (
            int.TryParse(input.SerializedValue, out var selectedIndex)
            && selectedIndex >= 0
            && selectedIndex < input.ValueListItems.Count
        )
        {
            dropDown.SelectedIndex = selectedIndex;
        }
        else if (input.ValueListItems.Count > 0)
        {
            dropDown.SelectedIndex = 0;
        }

        dropDown.SelectedIndexChanged += (_, _) =>
        {
            if (dropDown.SelectedIndex >= 0)
            {
                CommitInput(
                    objectId,
                    step.Index,
                    input,
                    dropDown.SelectedIndex.ToString(
                        System.Globalization.CultureInfo.InvariantCulture
                    )
                );
            }
        };

        return dropDown;
    }

    private static Control CreatePointEditor(
        Guid objectId,
        ModifierStepPanelState step,
        ModifierStepInputPanelState input
    )
    {
        var pickPointButton = new Button
        {
            Text = string.IsNullOrWhiteSpace(input.SerializedValue) ? "Set Point" : "Update Point",
            Enabled = IsInputEnabled(step, input),
        };

        pickPointButton.Click += (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc is null)
            {
                return;
            }

            Point3d point;
            if (!TryGetSelectedPoint(doc, objectId, out point))
            {
                var rc = RhinoGet.GetPoint("Set point input", false, out point);
                if (rc != Rhino.Commands.Result.Success)
                {
                    return;
                }
            }

            var serializedValue = SerializePoint(point);
            if (
                !RhinoModifiersPlugin.Instance.Engine.SetStepInputValue(
                    doc,
                    objectId,
                    step.Index,
                    input.Id,
                    serializedValue,
                    out var message
                )
            )
            {
                MessageBox.Show(message, MessageBoxType.Error);
                return;
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                RhinoApp.WriteLine(message);
            }
        };

        return pickPointButton;
    }

    private static Control CreateGeometryEditor(
        Guid objectId,
        ModifierStepPanelState step,
        ModifierStepInputPanelState input
    )
    {
        var pickGeometryButton = new Button
        {
            Text = string.IsNullOrWhiteSpace(input.SerializedValue)
                ? "Pick Geometry"
                : "Update Geometry",
            Enabled = IsInputEnabled(step, input),
            ToolTip = GetGeometryDisplayText(input),
        };

        pickGeometryButton.Click += (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc is null)
            {
                return;
            }

            var previousSelection = GetSelectedObjectIds(doc);
            try
            {
                doc.Objects.UnselectAll();
                var rc = RhinoGet.GetMultipleObjects(
                    "Select geometry for input",
                    false,
                    Rhino.DocObjects.ObjectType.AnyObject,
                    out var objRefs
                );
                if (rc != Rhino.Commands.Result.Success || objRefs is null || objRefs.Length == 0)
                {
                    return;
                }

                var ids = string.Join(" ", objRefs.Select(r => r.ObjectId.ToString()));
                if (
                    !RhinoModifiersPlugin.Instance.Engine.SetStepInputValue(
                        doc,
                        objectId,
                        step.Index,
                        input.Id,
                        ids,
                        out var message
                    )
                )
                {
                    MessageBox.Show(message, MessageBoxType.Error);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(message))
                {
                    RhinoApp.WriteLine(message);
                }
            }
            finally
            {
                RestoreSelection(doc, previousSelection);
            }
        };

        if (!input.ShowModifiedGeometryToggle)
        {
            return pickGeometryButton;
        }

        var modifiedGeometryToggle = new CheckBox
        {
            Text = "Use modified geometry",
            Checked = input.UseModifiedGeometry,
            Enabled = step.Enabled && input.ModifiedGeometrySourceObjectId.HasValue,
        };
        modifiedGeometryToggle.CheckedChanged += (_, _) =>
            ToggleModifiedGeometry(
                objectId,
                step.Index,
                input,
                modifiedGeometryToggle.Checked == true
            );

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Items = { pickGeometryButton, modifiedGeometryToggle },
        };
    }

    private static Control CreateOutputSummaryRow(
        IReadOnlyList<ModifierStepOutputPanelState> outputs
    )
    {
        var toolTip = BuildOutputToolTip(outputs);
        var outputLines = new StackLayout { Orientation = Orientation.Vertical, Spacing = 2 };

        foreach (var output in outputs)
        {
            var fullText = $"{output.Label}: {output.DisplayValue}";
            var line = new Label
            {
                Text = TruncateWithEllipsis(fullText, MaxOutputPreviewCharacters),
                Wrap = WrapMode.Word,
                TextAlignment = TextAlignment.Left,
            };
            SetToolTip(line, AppendToolTip(toolTip, fullText));
            outputLines.Items.Add(new StackLayoutItem(line, HorizontalAlignment.Stretch));
        }

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Items =
            {
                new StackLayoutItem(
                    new Label
                    {
                        Text = "Outputs",
                        TextAlignment = TextAlignment.Left,
                        ToolTip = toolTip,
                    },
                    HorizontalAlignment.Stretch
                ),
                new StackLayoutItem(outputLines, HorizontalAlignment.Stretch),
            },
        };
    }

    private static string TruncateWithEllipsis(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength || maxLength < 4)
        {
            return text;
        }

        return text.Substring(0, maxLength - 3) + "...";
    }

    private static void SetToolTip(Control control, string? toolTip)
    {
        if (!string.IsNullOrWhiteSpace(toolTip))
        {
            control.ToolTip = toolTip;
        }
    }

    private static string? BuildInputToolTip(ModifierStepInputPanelState input)
    {
        return string.IsNullOrWhiteSpace(input.Description) ? null : input.Description;
    }

    private static string? BuildOutputToolTip(IReadOnlyList<ModifierStepOutputPanelState> outputs)
    {
        var descriptions = outputs
            .Where(output => !string.IsNullOrWhiteSpace(output.Description))
            .Select(output => $"{output.Label}: {output.Description}")
            .ToArray();

        return descriptions.Length == 0 ? null : string.Join(Environment.NewLine, descriptions);
    }

    private static string? AppendToolTip(string? baseToolTip, string suffix)
    {
        if (string.IsNullOrWhiteSpace(baseToolTip))
        {
            return string.IsNullOrWhiteSpace(suffix) ? null : suffix;
        }

        return string.IsNullOrWhiteSpace(suffix)
            ? baseToolTip
            : $"{baseToolTip}{Environment.NewLine}{suffix}";
    }

    private static double GetIncrement(int decimalPlaces)
    {
        return decimalPlaces <= 0 ? 1d : Math.Pow(10d, -decimalPlaces);
    }

    private static bool IsInputEnabled(
        ModifierStepPanelState step,
        ModifierStepInputPanelState input
    )
    {
        return step.Enabled && !input.IsReadOnly;
    }

    private static Guid[] GetSelectedObjectIds(RhinoDoc doc)
    {
        return doc.Objects.GetSelectedObjects(false, false)
                ?.Select(candidate => candidate.Id)
                .ToArray()
            ?? Array.Empty<Guid>();
    }

    private static void RestoreSelection(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
        doc.Objects.UnselectAll();
        foreach (var objectId in objectIds)
        {
            doc.Objects.Select(objectId, true);
        }
    }

    private static bool TryGetSelectedPoint(RhinoDoc doc, Guid objectId, out Point3d point)
    {
        point = Point3d.Unset;
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject?.Geometry is not Rhino.Geometry.Point pointGeometry)
        {
            return false;
        }

        point = pointGeometry.Location;
        return true;
    }

    private static string GetPointDisplayText(string serializedValue)
    {
        return string.IsNullOrWhiteSpace(serializedValue)
            ? "No point set."
            : $"Current point: {serializedValue}";
    }

    private static string GetGeometryDisplayText(ModifierStepInputPanelState input)
    {
        if (input.HasLink)
        {
            return "Using linked geometry.";
        }

        return string.IsNullOrWhiteSpace(input.SerializedValue)
            ? "Using scene geometry by default."
            : $"Custom geometry: {input.SerializedValue}";
    }

    private static bool TryCreateSliderConfiguration(
        ModifierStepInputPanelState input,
        out NumericSliderConfiguration configuration
    )
    {
        configuration = default;
        if (!input.Minimum.HasValue || !input.Maximum.HasValue)
        {
            return false;
        }

        var minimum = input.Minimum.Value;
        var maximum = input.Maximum.Value;
        if (maximum < minimum)
        {
            (minimum, maximum) = (maximum, minimum);
        }

        if (Math.Abs(maximum - minimum) < double.Epsilon)
        {
            maximum = minimum + GetIncrement(input.DecimalPlaces);
        }

        var rawSteps = (maximum - minimum) / GetIncrement(input.DecimalPlaces);
        var steps = rawSteps > 0d ? (int)Math.Min(MaxSliderResolution, Math.Ceiling(rawSteps)) : 1;

        configuration = new NumericSliderConfiguration(
            minimum,
            maximum,
            Math.Max(1, steps),
            input.DecimalPlaces
        );
        return true;
    }

    private static double GetInitialNumericValue(ModifierStepInputPanelState input)
    {
        return TryParseNumber(input.SerializedValue, out var numericValue)
            ? numericValue
            : input.Minimum ?? 0d;
    }

    private static double RoundNumber(double value, int decimalPlaces)
    {
        return decimalPlaces <= 0
            ? Math.Round(value, 0, MidpointRounding.AwayFromZero)
            : Math.Round(value, decimalPlaces, MidpointRounding.AwayFromZero);
    }

    private static string SerializeNumber(double value, int decimalPlaces)
    {
        var roundedValue = RoundNumber(value, decimalPlaces);
        return decimalPlaces <= 0
            ? roundedValue.ToString("0", CultureInfo.InvariantCulture)
            : roundedValue.ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture);
    }

    private static string SerializePoint(Point3d point)
    {
        return FormattableString.Invariant(
            $"{point.X:0.###############},{point.Y:0.###############},{point.Z:0.###############}"
        );
    }

    private static string FormatDisplayNumber(double value, int decimalPlaces)
    {
        var serialized = SerializeNumber(value, decimalPlaces);
        return decimalPlaces <= 0 ? serialized : serialized.TrimEnd('0').TrimEnd('.');
    }

    private static bool TryParseNumber(string value, out double number)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static void CommitInput(
        Guid objectId,
        int stepIndex,
        ModifierStepInputPanelState input,
        string value
    )
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            return;
        }

        if (
            !RhinoModifiersPlugin.Instance.Engine.SetStepInputValue(
                doc,
                objectId,
                stepIndex,
                input.Id,
                value,
                out var message
            )
        )
        {
            MessageBox.Show(message, MessageBoxType.Error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }
    }

    private static void SetInputLink(
        Guid objectId,
        int stepIndex,
        string inputId,
        ModifierInputLinkOptionPanelState option
    )
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            return;
        }

        if (
            !RhinoModifiersPlugin.Instance.Engine.SetStepInputLink(
                doc,
                objectId,
                stepIndex,
                inputId,
                option.SourceStepId,
                option.SourceOutputId,
                out var message
            )
        )
        {
            MessageBox.Show(message, MessageBoxType.Error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }
    }

    private static void ToggleModifiedGeometry(
        Guid objectId,
        int stepIndex,
        ModifierStepInputPanelState input,
        bool useModifiedGeometry
    )
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            return;
        }

        if (useModifiedGeometry)
        {
            if (!input.ModifiedGeometrySourceObjectId.HasValue)
            {
                return;
            }

            if (
                !RhinoModifiersPlugin.Instance.Engine.SetStepInputObjectPreviewLink(
                    doc,
                    objectId,
                    stepIndex,
                    input.Id,
                    input.ModifiedGeometrySourceObjectId.Value,
                    out var message
                )
            )
            {
                MessageBox.Show(message, MessageBoxType.Error);
                return;
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                RhinoApp.WriteLine(message);
            }
        }
        else
        {
            ClearInputLink(objectId, stepIndex, input.Id);
        }
    }

    private static void ClearInputLink(Guid objectId, int stepIndex, string inputId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            return;
        }

        if (
            !RhinoModifiersPlugin.Instance.Engine.ClearStepInputLink(
                doc,
                objectId,
                stepIndex,
                inputId,
                out var message
            )
        )
        {
            MessageBox.Show(message, MessageBoxType.Error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }
    }

    private static void RemoveStep(Guid objectId, int index)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            return;
        }

        RhinoModifiersPlugin.Instance.Engine.RemoveStep(doc, objectId, index, out var message);
        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }
    }

    private static void MoveStep(Guid objectId, int index, int offset)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            return;
        }

        RhinoModifiersPlugin.Instance.Engine.MoveStep(
            doc,
            objectId,
            index,
            offset,
            out var message
        );
        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }
    }

    private static void ApplyStep(Guid objectId, int stepIndex)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            return;
        }

        if (stepIndex > 0)
        {
            var warning =
                $"Apply Stack will bake modifiers 1 through {stepIndex + 1} into the selected Rhino object and remove those modifiers from the stack.\n\n"
                + "This action is irreversible. Continue?";
            var result = MessageBox.Show(
                warning,
                "Apply Stack",
                MessageBoxButtons.YesNo,
                MessageBoxType.Warning,
                MessageBoxDefaultButton.No
            );
            if (result != DialogResult.Yes)
            {
                return;
            }
        }

        if (
            !RhinoModifiersPlugin.Instance.Engine.ApplyThroughStep(
                doc,
                objectId,
                stepIndex,
                out var message
            )
        )
        {
            MessageBox.Show(message, MessageBoxType.Error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }
    }

    private static void EditStepDefinition(string path)
    {
        if (!Runtime.ModifierEngine.OpenModifierDefinitionInGrasshopper(path, out var message))
        {
            MessageBox.Show(message, MessageBoxType.Error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            RhinoApp.WriteLine(message);
        }
    }
}
