using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using GH_IO.Serialization;
using Grasshopper.GUI;
using Grasshopper.GUI.Base;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using RhinoModifiers.Models;
using RhinoModifiers.UI;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhinoModifiers.Runtime;

internal sealed class ModifierEngine : IDisposable
{
    private const string LogPrefix = "GGH";
    private static readonly string[] InputAliases = { "GeomIn", "GeoIn" };
    private static readonly string[] OutputAliases = { "GeomOut", "GeoOut" };

    private readonly Dictionary<uint, DocumentState> _documents = new();
    private readonly Dictionary<string, DefinitionTemplate> _definitionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<QueuedStack> _queuedStacks = new();
    private readonly HashSet<string> _queuedKeys = new(StringComparer.Ordinal);
    private readonly StackPreviewConduit _previewConduit;
    private ulong _revisionCounter = 1;
    private bool _disposed;
    private bool _idleAttached;

    public ModifierEngine()
    {
        _previewConduit = new StackPreviewConduit(this);
        Log("ModifierEngine initialized.");

        RhinoDoc.ReplaceRhinoObject += OnReplaceRhinoObject;
        RhinoDoc.DeleteRhinoObject += OnDeleteRhinoObject;
        RhinoDoc.UndeleteRhinoObject += OnUndeleteRhinoObject;
        RhinoDoc.EndOpenDocument += OnEndOpenDocument;
        RhinoDoc.NewDocument += OnNewDocument;
        RhinoDoc.CloseDocument += OnCloseDocument;
        RhinoDoc.SelectObjects += OnSelectionChanged;
        RhinoDoc.DeselectObjects += OnSelectionChanged;
        RhinoDoc.DeselectAllObjects += OnDeselectAllObjects;
    }

    public event EventHandler? StateChanged;

    public ModifierPanelState GetPanelState(RhinoDoc? doc)
    {
        if (doc is null)
        {
            return new ModifierPanelState
            {
                StatusMessage = "No active Rhino document.",
            };
        }

        if (!TryGetSingleSelectedObject(doc, out var rhinoObject, out var statusMessage))
        {
            return new ModifierPanelState
            {
                StatusMessage = statusMessage,
            };
        }

        if (rhinoObject is null)
        {
            return new ModifierPanelState
            {
                StatusMessage = "Selected object is unavailable.",
            };
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        EnsureSavedStackRuntime(doc, rhinoObject.Id, spec);
        var runtime = TryGetStackRuntime(doc, rhinoObject.Id);
        var stepContexts = new List<PanelStepContext>(spec.Steps.Count);
        for (var i = 0; i < spec.Steps.Count; i++)
        {
            var step = spec.Steps[i];
            var displayName = Path.GetFileName(step.Path);
            if (TryGetDefinitionContract(step.Path, out var contract, out var contractError))
            {
                stepContexts.Add(new PanelStepContext(i, step, displayName, contract, string.Empty));
                continue;
            }

            stepContexts.Add(new PanelStepContext(i, step, displayName, null, contractError));
        }

        var steps = new List<ModifierStepPanelState>(spec.Steps.Count);
        foreach (var stepContext in stepContexts)
        {
            var stepError = runtime?.GetErrorForIndex(stepContext.Index) ?? string.Empty;
            var inputs = Array.Empty<ModifierStepInputPanelState>();
            var outputs = Array.Empty<ModifierStepOutputPanelState>();

            if (stepContext.Contract is not null)
            {
                inputs = BuildInputPanelState(doc, rhinoObject.Id, stepContexts, stepContext, runtime).ToArray();
                outputs = BuildOutputPanelState(runtime?.GetOutputsForIndex(stepContext.Index), stepContext.Contract).ToArray();

                if (string.IsNullOrWhiteSpace(stepError))
                {
                    var missingLabels = inputs
                        .Where(input => input.IsMissingRequiredValue)
                        .Select(input => input.Label)
                        .ToArray();

                    if (missingLabels.Length > 0)
                    {
                        stepError = FormatMissingRequiredInputs(missingLabels);
                    }
                }
            }
            else if (string.IsNullOrWhiteSpace(stepError))
            {
                stepError = stepContext.ContractError;
            }

            steps.Add(new ModifierStepPanelState
            {
                Index = stepContext.Index,
                StepId = stepContext.Step.StepId,
                Enabled = stepContext.Step.Enabled,
                FullPath = stepContext.Step.Path,
                DisplayName = stepContext.DisplayName,
                ErrorMessage = stepError,
                Inputs = inputs,
                Outputs = outputs,
            });
        }

        var selectionLabel = $"{rhinoObject.ObjectType}  {rhinoObject.Id}";
        var runtimeMessage = runtime?.ErrorMessage ?? string.Empty;

        return new ModifierPanelState
        {
            CanEdit = true,
            SelectedObjectId = rhinoObject.Id,
            SelectionLabel = selectionLabel,
            StatusMessage = runtimeMessage,
            Steps = steps,
        };
    }

    public IEnumerable<PreviewStack> GetPreviewStacks(RhinoDoc? doc)
    {
        if (doc is null)
        {
            yield break;
        }

        if (!_documents.TryGetValue(doc.RuntimeSerialNumber, out var documentState))
        {
            yield break;
        }

        foreach (var stack in documentState.Stacks)
        {
            if (stack.Value.PreviewGeometry.Count == 0)
            {
                continue;
            }

            yield return new PreviewStack(stack.Key, stack.Value.PreviewGeometry);
        }
    }

    public IEnumerable<Guid> GetManagedObjectIds(RhinoDoc? doc)
    {
        if (doc is null)
        {
            yield break;
        }

        if (!_documents.TryGetValue(doc.RuntimeSerialNumber, out var documentState))
        {
            yield break;
        }

        foreach (var objectId in documentState.Stacks.Keys)
        {
            yield return objectId;
        }
    }

    public bool AddStep(RhinoDoc doc, Guid objectId, string path, out string message)
    {
        message = string.Empty;
        Log($"AddStep requested. Object={objectId}, Path={path}");
        if (!File.Exists(path))
        {
            message = $"Modifier file not found: {path}";
            Log(message);
            return false;
        }

        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        if (!IsSupportedGeometryObject(rhinoObject))
        {
            message = $"Object type '{rhinoObject.ObjectType}' is not supported by the MVP.";
            Log(message);
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        spec.Steps.Add(new ModifierStepSpec
        {
            Enabled = true,
            Path = Path.GetFullPath(path),
        });

        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to store the modifier stack on the Rhino object.";
            Log(message);
            return false;
        }

        InvalidateStackFromStep(doc, objectId, spec, spec.Steps.Count - 1);
        message = $"Added modifier: {Path.GetFileName(path)}";
        Log($"{message} StackCount={spec.Steps.Count}");
        return true;
    }

    public bool OpenModifierDefinitionInGrasshopper(string path, out string message)
    {
        message = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            message = "Modifier path is empty.";
            Log(message);
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        Log($"OpenModifierDefinitionInGrasshopper requested. Path={fullPath}");
        if (!File.Exists(fullPath))
        {
            message = $"Modifier file not found: {fullPath}";
            Log(message);
            return false;
        }

        if (RhinoApp.GetPlugInObject("Grasshopper") is not Grasshopper.Plugin.GH_RhinoScriptInterface grasshopper)
        {
            message = "Grasshopper is not available for document editing.";
            Log(message);
            return false;
        }

        try
        {
            grasshopper.ShowEditor();
            var document = Grasshopper.Instances.DocumentServer.AddDocument(fullPath, true);
            if (document is null)
            {
                message = $"Failed to open modifier in Grasshopper: {Path.GetFileName(fullPath)}";
                Log(message);
                return false;
            }

            message = $"Opened modifier in Grasshopper: {Path.GetFileName(fullPath)}. Save the Grasshopper file, then refresh the stack to apply changes.";
            Log(message);
            return true;
        }
        catch (Exception ex)
        {
            message = $"Failed to open modifier in Grasshopper: {ex.Message}";
            Log($"{message} Path={fullPath}");
            return false;
        }
    }

    public bool RemoveStep(RhinoDoc doc, Guid objectId, int index, out string message)
    {
        message = string.Empty;
        Log($"RemoveStep requested. Object={objectId}, Index={index}");
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        if (index < 0 || index >= spec.Steps.Count)
        {
            message = "Step index is out of range.";
            Log(message);
            return false;
        }

        var runtime = TryGetStackRuntime(doc, objectId);
        if (runtime is not null && index < runtime.StepRuntimes.Count)
        {
            runtime.DisposeStep(index);
            runtime.StepRuntimes.RemoveAt(index);
        }

        spec.Steps.RemoveAt(index);
        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to update the modifier stack.";
            Log(message);
            return false;
        }

        InvalidateStackFromStep(doc, objectId, spec, index);
        message = "Removed modifier step.";
        Log($"{message} StackCount={spec.Steps.Count}");
        return true;
    }

    public bool MoveStep(RhinoDoc doc, Guid objectId, int index, int offset, out string message)
    {
        message = string.Empty;
        Log($"MoveStep requested. Object={objectId}, Index={index}, Offset={offset}");
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        var targetIndex = index + offset;
        var spec = ModifierStackStorage.Load(rhinoObject);
        if (index < 0 || index >= spec.Steps.Count || targetIndex < 0 || targetIndex >= spec.Steps.Count)
        {
            message = "Cannot move the modifier step further in that direction.";
            Log(message);
            return false;
        }

        (spec.Steps[index], spec.Steps[targetIndex]) = (spec.Steps[targetIndex], spec.Steps[index]);

        var runtime = TryGetStackRuntime(doc, objectId);
        if (runtime is not null && index < runtime.StepRuntimes.Count && targetIndex < runtime.StepRuntimes.Count)
        {
            (runtime.StepRuntimes[index], runtime.StepRuntimes[targetIndex]) = (runtime.StepRuntimes[targetIndex], runtime.StepRuntimes[index]);
        }

        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to update the modifier stack.";
            Log(message);
            return false;
        }

        InvalidateStackFromStep(doc, objectId, spec, Math.Min(index, targetIndex));
        message = "Moved modifier step.";
        Log($"{message} NewIndex={targetIndex}");
        return true;
    }

    public bool SetStepEnabled(RhinoDoc doc, Guid objectId, int index, bool enabled, out string message)
    {
        message = string.Empty;
        Log($"SetStepEnabled requested. Object={objectId}, Index={index}, Enabled={enabled}");
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        if (index < 0 || index >= spec.Steps.Count)
        {
            message = "Step index is out of range.";
            Log(message);
            return false;
        }

        spec.Steps[index].Enabled = enabled;
        if (enabled && !TryValidateObjectPreviewGraph(doc, objectId, spec, out message))
        {
            Log(message);
            return false;
        }

        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to update the modifier stack.";
            Log(message);
            return false;
        }

        if (!enabled && TryGetStackRuntime(doc, objectId) is { } runtime)
        {
            runtime.DisposeStep(index);
            runtime.ClearOutputs(index);
        }

        InvalidateStackFromStep(doc, objectId, spec, index);
        message = enabled ? "Modifier enabled." : "Modifier disabled.";
        Log(message);
        return true;
    }

    public bool SetStepInputValue(RhinoDoc doc, Guid objectId, int index, string inputId, string serializedValue, out string message)
    {
        message = string.Empty;
        Log($"SetStepInputValue requested. Object={objectId}, Index={index}, Input={inputId}, Value='{serializedValue}'");
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        if (index < 0 || index >= spec.Steps.Count)
        {
            message = "Step index is out of range.";
            Log(message);
            return false;
        }

        spec.Steps[index].InputValues[inputId] = serializedValue ?? string.Empty;
        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to update the modifier input value.";
            Log(message);
            return false;
        }

        InvalidateStackFromStep(doc, objectId, spec, index);
        message = "Updated modifier input.";
        Log(message);
        return true;
    }

    public bool SetStepInputLink(RhinoDoc doc, Guid objectId, int index, string inputId, Guid sourceStepId, string sourceOutputId, out string message)
    {
        message = string.Empty;
        Log($"SetStepInputLink requested. Object={objectId}, Index={index}, Input={inputId}, SourceStep={sourceStepId}, SourceOutput={sourceOutputId}");
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        if (!TryValidateInputLink(spec, index, inputId, sourceStepId, sourceOutputId, out var linkSpec, out message))
        {
            Log(message);
            return false;
        }

        spec.Steps[index].InputLinks[inputId] = linkSpec;
        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to update the modifier input link.";
            Log(message);
            return false;
        }

        InvalidateStackFromStep(doc, objectId, spec, index);
        message = "Updated modifier input link.";
        Log(message);
        return true;
    }

    public bool SetStepInputObjectPreviewLink(RhinoDoc doc, Guid objectId, int index, string inputId, Guid sourceObjectId, out string message)
    {
        message = string.Empty;
        Log($"SetStepInputObjectPreviewLink requested. Object={objectId}, Index={index}, Input={inputId}, SourceObject={sourceObjectId}");
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        if (!TryValidateObjectPreviewInputLink(doc, objectId, spec, index, inputId, sourceObjectId, out var linkSpec, out message))
        {
            Log(message);
            return false;
        }

        spec.Steps[index].InputLinks[inputId] = linkSpec;
        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to update the modifier input link.";
            Log(message);
            return false;
        }

        InvalidateStackFromStep(doc, objectId, spec, index);
        message = "Updated modifier input link.";
        Log(message);
        return true;
    }

    public bool ClearStepInputLink(RhinoDoc doc, Guid objectId, int index, string inputId, out string message)
    {
        message = string.Empty;
        Log($"ClearStepInputLink requested. Object={objectId}, Index={index}, Input={inputId}");
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        if (index < 0 || index >= spec.Steps.Count)
        {
            message = "Step index is out of range.";
            Log(message);
            return false;
        }

        spec.Steps[index].InputLinks.Remove(inputId);
        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to clear the modifier input link.";
            Log(message);
            return false;
        }

        InvalidateStackFromStep(doc, objectId, spec, index);
        message = "Cleared modifier input link.";
        Log(message);
        return true;
    }

    public bool RefreshSelectedObject(RhinoDoc doc, out string message)
    {
        if (!TryGetSingleSelectedObject(doc, out var rhinoObject, out message))
        {
            Log($"RefreshSelectedObject rejected. {message}");
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        if (spec.Steps.Count == 0)
        {
            message = "Selected object does not have any modifier steps.";
            Log(message);
            return false;
        }

        ResetStackRuntime(doc, rhinoObject!.Id, spec);
        message = "Queued stack refresh.";
        Log($"{message} Object={rhinoObject.Id}, StepCount={spec.Steps.Count}");
        return true;
    }

    public bool ApplyThroughStep(RhinoDoc doc, Guid objectId, int stepIndex, out string message)
    {
        message = string.Empty;
        Log($"ApplyThroughStep requested. Object={objectId}, StepIndex={stepIndex}");

        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        if (stepIndex < 0 || stepIndex >= spec.Steps.Count)
        {
            message = "Step index is out of range.";
            Log(message);
            return false;
        }

        if (!TryEvaluateStackThroughStep(doc, objectId, rhinoObject, spec, stepIndex, out var evaluatedGeometry, out var evaluationError))
        {
            message = evaluationError;
            Log($"ApplyThroughStep evaluation failed. Object={objectId}, StepIndex={stepIndex}, Error={evaluationError}");
            return false;
        }

        if (evaluatedGeometry.Count == 0)
        {
            message = "The selected modifier output is empty; apply was cancelled to avoid deleting geometry.";
            Log(message);
            return false;
        }

        foreach (var geometry in evaluatedGeometry)
        {
            if (!TryEnsureSupportedApplyGeometry(geometry, out var unsupportedError))
            {
                message = unsupportedError;
                Log($"ApplyThroughStep geometry validation failed. {unsupportedError}");
                return false;
            }
        }

        if (!ReplaceManagedObjectGeometry(doc, objectId, evaluatedGeometry[0], out var replaceError))
        {
            message = replaceError;
            Log($"ApplyThroughStep failed replacing object geometry. Object={objectId}, Error={replaceError}");
            return false;
        }

        var newObjectAttributes = rhinoObject.Attributes.Duplicate();
        newObjectAttributes.UserDictionary.Remove(ModifierStackSpec.UserDictionaryKey);

        for (var i = 1; i < evaluatedGeometry.Count; i++)
        {
            if (!TryAddGeometryObject(doc, evaluatedGeometry[i], newObjectAttributes, out var addError))
            {
                message = $"Applied base object, but failed to add additional geometry item {i + 1}: {addError}";
                Log($"ApplyThroughStep partially succeeded. Object={objectId}, Index={i}, Error={addError}");
                return false;
            }
        }

        spec.Steps.RemoveRange(0, stepIndex + 1);
        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Applied geometry, but failed to update remaining modifier stack.";
            Log($"ApplyThroughStep failed updating stack metadata after apply. Object={objectId}");
            return false;
        }

        ResetStackRuntime(doc, objectId, spec);
        message = stepIndex == 0
            ? "Applied modifier and removed it from the stack."
            : $"Applied {stepIndex + 1} modifiers and removed them from the stack.";
        Log($"ApplyThroughStep completed. Object={objectId}, AppliedCount={stepIndex + 1}, RemainingSteps={spec.Steps.Count}");
        return true;
    }

    public bool BakeFinalResult(RhinoDoc doc, Guid objectId, out string message)
    {
        message = string.Empty;
        Log($"BakeFinalResult requested. Object={objectId}");

        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        if (spec.Steps.Count == 0)
        {
            message = "Selected object does not have any modifier steps.";
            Log(message);
            return false;
        }

        if (!TryEvaluateStackThroughStep(doc, objectId, rhinoObject, spec, spec.Steps.Count - 1, out var evaluatedGeometry, out var evaluationError))
        {
            message = evaluationError;
            Log($"BakeFinalResult evaluation failed. Object={objectId}, Error={evaluationError}");
            return false;
        }

        if (evaluatedGeometry.Count == 0)
        {
            message = "The final modifier result is empty; bake was cancelled.";
            Log(message);
            return false;
        }

        foreach (var geometry in evaluatedGeometry)
        {
            if (!TryEnsureSupportedApplyGeometry(geometry, out var unsupportedError))
            {
                message = unsupportedError.Replace("apply operation", "bake operation", StringComparison.Ordinal);
                Log($"BakeFinalResult geometry validation failed. {message}");
                return false;
            }
        }

        var newObjectAttributes = rhinoObject.Attributes.Duplicate();
        newObjectAttributes.UserDictionary.Remove(ModifierStackSpec.UserDictionaryKey);

        for (var i = 0; i < evaluatedGeometry.Count; i++)
        {
            if (!TryAddGeometryObject(doc, evaluatedGeometry[i], newObjectAttributes, out var addError))
            {
                message = $"Failed to bake geometry item {i + 1}: {addError}";
                Log($"BakeFinalResult add failed. Object={objectId}, Index={i}, Error={addError}");
                return false;
            }
        }

        message = evaluatedGeometry.Count == 1
            ? "Baked final stack result as a new object."
            : $"Baked final stack result as {evaluatedGeometry.Count} new objects.";
        Log($"BakeFinalResult completed. Object={objectId}, BakedCount={evaluatedGeometry.Count}");
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Log("ModifierEngine disposing.");

        RhinoDoc.ReplaceRhinoObject -= OnReplaceRhinoObject;
        RhinoDoc.DeleteRhinoObject -= OnDeleteRhinoObject;
        RhinoDoc.UndeleteRhinoObject -= OnUndeleteRhinoObject;
        RhinoDoc.EndOpenDocument -= OnEndOpenDocument;
        RhinoDoc.NewDocument -= OnNewDocument;
        RhinoDoc.CloseDocument -= OnCloseDocument;
        RhinoDoc.SelectObjects -= OnSelectionChanged;
        RhinoDoc.DeselectObjects -= OnSelectionChanged;
        RhinoDoc.DeselectAllObjects -= OnDeselectAllObjects;

        if (_idleAttached)
        {
            RhinoApp.Idle -= OnIdle;
        }

        foreach (var documentState in _documents.Values)
        {
            documentState.Dispose();
        }

        foreach (var template in _definitionCache.Values)
        {
            template.Dispose();
        }

        _documents.Clear();
        _definitionCache.Clear();
        _queuedStacks.Clear();
        _queuedKeys.Clear();
        _previewConduit.Enabled = false;
        Log("ModifierEngine disposed.");
    }

    private void OnReplaceRhinoObject(object? sender, RhinoReplaceObjectEventArgs e)
    {
        var spec = ModifierStackStorage.Load(e.NewRhinoObject);
        if (spec.Steps.Count == 0)
        {
            return;
        }

        Log($"Rhino object replaced. Object={e.ObjectId}, Steps={spec.Steps.Count}");
        var runtime = GetOrCreateStackRuntime(e.Document, e.ObjectId);
        runtime.RootRevision = NextRevision();
        QueueEvaluation(e.Document, e.ObjectId);
    }

    private void OnDeleteRhinoObject(object? sender, RhinoObjectEventArgs e)
    {
        Log($"Rhino object deleted. Object={e.ObjectId}");
        RemoveStackRuntime(e.TheObject?.Document, e.ObjectId);
    }

    private void OnUndeleteRhinoObject(object? sender, RhinoObjectEventArgs e)
    {
        var doc = e.TheObject?.Document ?? RhinoDoc.FromRuntimeSerialNumber(e.TheObject?.Document.RuntimeSerialNumber ?? 0);
        if (doc is null)
        {
            return;
        }

        var rhinoObject = doc.Objects.FindId(e.ObjectId);
        var spec = ModifierStackStorage.Load(rhinoObject);
        if (spec.Steps.Count == 0)
        {
            return;
        }

        Log($"Rhino object undeleted. Object={e.ObjectId}, Steps={spec.Steps.Count}");
        var runtime = GetOrCreateStackRuntime(doc, e.ObjectId);
        runtime.RootRevision = NextRevision();
        QueueEvaluation(doc, e.ObjectId);
    }

    private void OnEndOpenDocument(object? sender, DocumentOpenEventArgs e)
    {
        Log($"Rhino document opened. Serial={e.Document.RuntimeSerialNumber}");
        RestoreSavedStacks(e.Document);
    }

    private void OnNewDocument(object? sender, DocumentEventArgs e)
    {
        Log($"Rhino new document created. Serial={e.Document.RuntimeSerialNumber}");
        RestoreSavedStacks(e.Document);
    }

    private void OnCloseDocument(object? sender, DocumentEventArgs e)
    {
        Log($"Rhino document closing. Serial={e.Document.RuntimeSerialNumber}");
        if (_documents.Remove(e.Document.RuntimeSerialNumber, out var state))
        {
            state.Dispose();
            UpdateConduitState();
        }

        RaiseStateChanged();
    }

    private void OnSelectionChanged(object? sender, RhinoObjectSelectionEventArgs e)
    {
        var count = e.RhinoObjects?.Length ?? 0;
        Log($"Selection changed. AffectedCount={count}, TotalSelected={e.Document.Objects.GetSelectedObjects(false, false).Count()}");
        RaiseStateChanged();
    }

    private void OnDeselectAllObjects(object? sender, RhinoDeselectAllObjectsEventArgs e)
    {
        Log("Selection cleared.");
        RaiseStateChanged();
    }

    private void OnIdle(object? sender, EventArgs e)
    {
        if (_queuedStacks.Count > 0)
        {
            Log($"Idle processing started. QueueCount={_queuedStacks.Count}");
        }

        while (_queuedStacks.Count > 0)
        {
            var queued = _queuedStacks.Dequeue();
            _queuedKeys.Remove(queued.Key);
            Log($"Dequeued stack evaluation. Doc={queued.DocumentSerial}, Object={queued.ObjectId}, Remaining={_queuedStacks.Count}");

            var doc = RhinoDoc.FromRuntimeSerialNumber(queued.DocumentSerial);
            if (doc is null)
            {
                Log($"Skipped queued stack because document {queued.DocumentSerial} is unavailable.");
                continue;
            }

            EvaluateStack(doc, queued.ObjectId);
        }

        if (_queuedStacks.Count == 0 && _idleAttached)
        {
            RhinoApp.Idle -= OnIdle;
            _idleAttached = false;
            Log("Idle processing finished. Queue empty.");
        }
    }

    private void EvaluateStack(RhinoDoc doc, Guid objectId)
    {
        Log($"EvaluateStack started. Doc={doc.RuntimeSerialNumber}, Object={objectId}");
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            Log($"EvaluateStack aborted. Object={objectId} no longer exists.");
            RemoveStackRuntime(doc, objectId);
            return;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        if (spec.Steps.Count == 0)
        {
            Log($"EvaluateStack aborted. Object={objectId} has no modifier steps.");
            RemoveStackRuntime(doc, objectId);
            return;
        }

        Log($"Stack spec loaded. Object={DescribeRhinoObject(rhinoObject)}, StepCount={spec.Steps.Count}");

        var runtime = GetOrCreateStackRuntime(doc, objectId);
        runtime.EnsureStepCapacity(spec.Steps.Count);
        runtime.ClearErrors(spec.Steps.Count);
        runtime.ClearAllOutputs(spec.Steps.Count);

        if (TryGetObjectPreviewCycleError(doc, objectId, spec, out var cycleError))
        {
            runtime.PreviewGeometry.Clear();
            runtime.SetError(-1, cycleError);
            runtime.HasEvaluated = true;
            MarkObjectClean(doc, objectId);
            Log($"EvaluateStack aborted. Object={objectId}. {cycleError}");
            QueueDependentEvaluations(doc, objectId);
            UpdateConduitAndViews(doc);
            RaiseStateChanged();
            return;
        }

        if (!TryPrepareObjectPreviewDependencies(doc, objectId, spec, out var waitingReason))
        {
            Log($"EvaluateStack deferred. Object={objectId}. {waitingReason}");
            QueueEvaluation(doc, objectId);
            return;
        }

        if (!GeometryConversion.TryGetSourceGeometry(rhinoObject.Geometry, out var currentGeometry, out var sourceError))
        {
            runtime.PreviewGeometry.Clear();
            runtime.SetError(-1, sourceError);
            runtime.HasEvaluated = true;
            MarkObjectClean(doc, objectId);
            Log($"Source geometry conversion failed. Object={objectId}. {sourceError}");
            QueueDependentEvaluations(doc, objectId);
            UpdateConduitAndViews(doc);
            RaiseStateChanged();
            return;
        }

        Log($"Source geometry ready. Count={currentGeometry.Count}. {DescribeGeometry(currentGeometry)}");

        ulong upstreamRevision = runtime.RootRevision;
        var anyStepSucceeded = false;
        var failedAtFirstEnabledStep = false;
        var firstEnabledIndex = spec.Steps.FindIndex(step => step.Enabled);
        var publishedOutputsByStepId = new Dictionary<Guid, IReadOnlyList<StepOutputValue>>();
        Log($"Stack evaluation entering step loop. RootRevision={runtime.RootRevision}, FirstEnabledIndex={firstEnabledIndex}");

        for (var i = 0; i < spec.Steps.Count; i++)
        {
            var stepSpec = spec.Steps[i];
            if (!stepSpec.Enabled)
            {
                Log($"Step {i} disabled. Disposing any existing runtime and skipping.");
                runtime.DisposeStep(i);
                runtime.ClearOutputs(i);
                publishedOutputsByStepId.Remove(stepSpec.StepId);
                continue;
            }

            StepRuntime stepRuntime;
            try
            {
                stepRuntime = EnsureStepRuntime(runtime, i, stepSpec);
            }
            catch (Exception ex)
            {
                runtime.SetError(i, ex.Message);
                Log($"Step {i} runtime setup failed for '{Path.GetFileName(stepSpec.Path)}'. {ex.Message}");
                failedAtFirstEnabledStep = i == firstEnabledIndex;
                break;
            }

            if (stepRuntime.LastInputRevision == upstreamRevision)
            {
                Log($"Step {i} cache hit. Modifier={Path.GetFileName(stepSpec.Path)}, InputRevision={upstreamRevision}, CachedOutputCount={stepRuntime.CachedOutput.Count}");
                runtime.SetOutputs(i, stepRuntime.CachedPublishedOutputs);
                publishedOutputsByStepId[stepSpec.StepId] = stepRuntime.CachedPublishedOutputs;
                if (stepRuntime.HasGeometryOutputs)
                {
                    currentGeometry = CloneGeometry(stepRuntime.CachedOutput);
                }

                upstreamRevision = stepRuntime.LastOutputRevision;
                anyStepSucceeded = true;
                continue;
            }

            Log($"Step {i} solving. Modifier={Path.GetFileName(stepSpec.Path)}, InputRevision={upstreamRevision}, InputCount={currentGeometry.Count}, InputSummary={DescribeGeometry(currentGeometry)}");
            var result = EvaluateStep(doc, stepRuntime, stepSpec, i, spec.Steps, publishedOutputsByStepId, currentGeometry);
            if (!result.Success)
            {
                runtime.SetError(i, result.ErrorMessage);
                runtime.ClearOutputs(i);
                publishedOutputsByStepId.Remove(stepSpec.StepId);
                Log($"Step {i} solve failed for '{Path.GetFileName(stepSpec.Path)}'. {result.ErrorMessage}");
                failedAtFirstEnabledStep = i == firstEnabledIndex;
                break;
            }

            stepRuntime.CachedOutput = result.OutputGeometry;
            stepRuntime.CachedPublishedOutputs = result.PublishedOutputs;
            stepRuntime.LastInputRevision = upstreamRevision;
            stepRuntime.LastOutputRevision = NextRevision();
            runtime.SetOutputs(i, result.PublishedOutputs);
            publishedOutputsByStepId[stepSpec.StepId] = result.PublishedOutputs;
            if (result.HasGeometryOutput)
            {
                currentGeometry = CloneGeometry(stepRuntime.CachedOutput);
            }

            upstreamRevision = stepRuntime.LastOutputRevision;
            anyStepSucceeded = true;
            Log($"Step {i} solve complete. Modifier={Path.GetFileName(stepSpec.Path)}, OutputRevision={stepRuntime.LastOutputRevision}, HasGeometryOutput={result.HasGeometryOutput}, RawOutputCount={result.RawGeometryOutputItemCount}, ConvertedOutputCount={stepRuntime.CachedOutput.Count}, OutputSummary={DescribeGeometry(result.HasGeometryOutput ? stepRuntime.CachedOutput : currentGeometry)}");
            if (result.SkippedGeometryOutputTypes.Count > 0)
            {
                Log($"Step {i} skipped output items. Modifier={Path.GetFileName(stepSpec.Path)}, Skipped={string.Join(", ", result.SkippedGeometryOutputTypes)}");
            }
        }

        runtime.PreviewGeometry = failedAtFirstEnabledStep && !anyStepSucceeded
            ? new List<GeometryBase>()
            : CloneGeometry(currentGeometry);
        runtime.HasEvaluated = true;
        MarkObjectClean(doc, objectId);

        if (runtime.PreviewGeometry.Count == 0 && string.IsNullOrWhiteSpace(runtime.ErrorMessage))
        {
            Log($"Stack on {objectId} evaluated but produced no preview geometry.");
        }

        Log($"EvaluateStack finished. Object={objectId}, PreviewCount={runtime.PreviewGeometry.Count}, PreviewSummary={DescribeGeometry(runtime.PreviewGeometry)}, Error='{runtime.ErrorMessage}'");
        QueueDependentEvaluations(doc, objectId);
        UpdateConduitAndViews(doc);
        RaiseStateChanged();
    }

    private StepRuntime EnsureStepRuntime(StackRuntime runtime, int index, ModifierStepSpec spec)
    {
        var fullPath = Path.GetFullPath(spec.Path);
        var lastWriteUtc = File.GetLastWriteTimeUtc(fullPath);

        var existing = runtime.StepRuntimes[index];
        if (existing is not null && existing.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase) && existing.LastWriteUtc == lastWriteUtc)
        {
            Log($"Step {index} runtime reused. Modifier={Path.GetFileName(fullPath)}, LastWriteUtc={lastWriteUtc:O}");
            return existing;
        }

        existing?.Dispose();
        if (existing is not null)
        {
            Log($"Step {index} runtime replaced. Modifier={Path.GetFileName(fullPath)}, LastWriteUtc={lastWriteUtc:O}");
        }

        var template = GetDefinitionTemplate(fullPath, lastWriteUtc);
        var document = GH_Document.DuplicateDocument(template.Document);
        Log($"Step {index} duplicated GH document. Modifier={Path.GetFileName(fullPath)}");

        Param_Geometry? sceneInputSource = null;
        IGH_Param? sceneInputParam = null;
        if (template.Contract.SceneInput is not null)
        {
            sceneInputParam = ResolveDocumentParam(document, template.Contract.SceneInput.ObjectId)
                ?? throw new InvalidOperationException($"Modifier '{Path.GetFileName(fullPath)}' is missing its scene geometry input in the duplicated document.");

            sceneInputSource = CreateRuntimeSourceParam(document, sceneInputParam);
        }

        var inputBindings = BindInputDescriptors(document, template.Contract.Inputs);
        var outputBindings = BindOutputDescriptors(document, template.Contract.Outputs);
        var geometryOutputBindings = BindOutputDescriptors(document, template.Contract.GeometryOutputs);

        Log($"Step {index} runtime created. Modifier={Path.GetFileName(fullPath)}, ExposedInputs={inputBindings.Count}, ExposedOutputs={outputBindings.Count}, GeometryOutputs={geometryOutputBindings.Count}");
        var stepRuntime = new StepRuntime(
            fullPath,
            lastWriteUtc,
            document,
            template.Contract,
            sceneInputSource,
            sceneInputParam,
            inputBindings,
            outputBindings,
            geometryOutputBindings);
        runtime.StepRuntimes[index] = stepRuntime;
        return stepRuntime;
    }

    private DefinitionTemplate GetDefinitionTemplate(string fullPath, DateTime lastWriteUtc)
    {
        if (_definitionCache.TryGetValue(fullPath, out var cached) && cached.LastWriteUtc == lastWriteUtc)
        {
            Log($"Definition template cache hit. Path={fullPath}, LastWriteUtc={lastWriteUtc:O}");
            return cached;
        }

        cached?.Dispose();
        if (cached is not null)
        {
            Log($"Definition template invalidated. Path={fullPath}, LastWriteUtc={lastWriteUtc:O}");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Modifier file not found.", fullPath);
        }

        Log($"Loading Grasshopper definition from disk. Path={fullPath}");
        var document = LoadDefinitionDocument(fullPath);

        var template = new DefinitionTemplate(fullPath, lastWriteUtc, document, CreateDefinitionContract(document));
        _definitionCache[fullPath] = template;
        Log($"Definition template cached. Path={fullPath}, LastWriteUtc={lastWriteUtc:O}");
        return template;
    }

    private static GH_Document LoadDefinitionDocument(string fullPath)
    {
        return WithMissingPluginDialogSuppressed(() =>
        {
            var archive = new GH_Archive();
            if (!archive.ReadFromFile(fullPath))
            {
                throw new InvalidOperationException($"Failed to load Grasshopper definition '{fullPath}'.");
            }

            var document = new GH_Document();
            if (!archive.ExtractObject(document, "Definition"))
            {
                document.Dispose();
                throw new InvalidOperationException($"Grasshopper definition '{fullPath}' did not produce a document.");
            }

            return document;
        });
    }

    private static T WithMissingPluginDialogSuppressed<T>(Func<T> action)
    {
        var original = Grasshopper.CentralSettings.TryDownloadMissingPlugins;
        Grasshopper.CentralSettings.TryDownloadMissingPlugins = false;
        try
        {
            return action();
        }
        finally
        {
            Grasshopper.CentralSettings.TryDownloadMissingPlugins = original;
        }
    }

    private StepEvaluationResult EvaluateStep(
        RhinoDoc doc,
        StepRuntime runtime,
        ModifierStepSpec stepSpec,
        int stepIndex,
        IReadOnlyList<ModifierStepSpec> allSteps,
        IReadOnlyDictionary<Guid, IReadOnlyList<StepOutputValue>> publishedOutputsByStepId,
        IReadOnlyList<GeometryBase> inputGeometry)
    {
        var missingInputs = GetMissingRequiredInputs(stepSpec, runtime.Contract.Inputs)
            .Select(input => input.Label)
            .ToArray();
        if (missingInputs.Length > 0)
        {
            return StepEvaluationResult.Fail(FormatMissingRequiredInputs(missingInputs));
        }

        if (runtime.SceneInputSource is not null)
        {
            if (!TryAppendGeometry(runtime.SceneInputSource, inputGeometry, out var error))
            {
                return StepEvaluationResult.Fail(error);
            }
        }

        foreach (var binding in runtime.Inputs)
        {
            if (!ApplyInputBinding(doc, binding, stepSpec, stepIndex, allSteps, publishedOutputsByStepId, inputGeometry, out var error))
            {
                return StepEvaluationResult.Fail(error);
            }
        }

        runtime.SceneInputSource?.ExpireSolution(false);
        runtime.SceneInputParam?.ExpireSolution(false);
        foreach (var binding in runtime.Inputs)
        {
            binding.Expire();
        }

        foreach (var binding in runtime.Outputs)
        {
            binding.Expire();
        }

        foreach (var binding in runtime.GeometryOutputs)
        {
            binding.Expire();
        }

        runtime.Document.NewSolution(true, GH_SolutionMode.Silent);

        var outputValues = new List<StepOutputValue>(runtime.Outputs.Count);
        foreach (var binding in runtime.Outputs)
        {
            binding.Param.CollectData();
            binding.Param.ComputeData();
            outputValues.Add(CapturePublishedOutput(binding.Descriptor, binding.Param));
        }

        if (runtime.GeometryOutputs.Count == 0)
        {
            return StepEvaluationResult.Successful(false, new List<GeometryBase>(), outputValues, 0, new List<string>());
        }

        var geometry = new List<GeometryBase>();
        var skipped = new List<string>();
        var totalRawItemCount = 0;
        foreach (var binding in runtime.GeometryOutputs)
        {
            binding.Param.CollectData();
            binding.Param.ComputeData();

            var output = GeometryConversion.ReadOutput(binding.Param);
            geometry.AddRange(CloneGeometry(output.Geometry));
            skipped.AddRange(output.SkippedTypes);
            totalRawItemCount += output.TotalItemCount;
        }

        return StepEvaluationResult.Successful(true, geometry, outputValues, totalRawItemCount, skipped);
    }

    private static Param_Geometry CreateRuntimeSourceParam(GH_Document document, IGH_Param inputParam)
    {
        var sourceParam = new Param_Geometry
        {
            Name = "GGH Runtime Input",
            NickName = "GGH Runtime Input",
            Description = "Injected runtime input for scene-bound modifier evaluation.",
            MutableNickName = false,
            Hidden = true,
            Optional = false,
        };

        document.AddObject(sourceParam, false);
        inputParam.AddSource(sourceParam);
        return sourceParam;
    }

    private bool TryGetDefinitionContract(string path, out DefinitionContract contract, out string error)
    {
        contract = null!;
        error = string.Empty;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var template = GetDefinitionTemplate(fullPath, File.GetLastWriteTimeUtc(fullPath));
            contract = template.Contract;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private IEnumerable<ModifierStepInputPanelState> BuildInputPanelState(
        RhinoDoc doc,
        Guid objectId,
        IReadOnlyList<PanelStepContext> stepContexts,
        PanelStepContext currentStepContext,
        StackRuntime? runtime)
    {
        var contract = currentStepContext.Contract!;
        foreach (var input in contract.Inputs)
        {
            var serializedValue = GetDisplayedInputValue(currentStepContext.Step, input);
            var isMissingRequiredValue = IsMissingRequiredInput(currentStepContext.Step, input);
            var linkState = BuildLinkPresentationState(doc, objectId, stepContexts, currentStepContext, input, runtime);
            var (showModifiedGeometryToggle, useModifiedGeometry, modifiedGeometrySourceObjectId) =
                GetModifiedGeometryToggleState(doc, currentStepContext.Step, input);

            yield return new ModifierStepInputPanelState
            {
                Id = input.Id,
                Label = input.Label,
                Description = input.Kind switch
                {
                    ModifierIoKind.Geometry => AppendDescription(input.Description, "Blank uses the current stack geometry. Paste Rhino object IDs or `self` to override. When a single referenced object has its own modifiers, a checkbox lets you use its modified result instead of the base geometry."),
                    ModifierIoKind.Point => AppendDescription(input.Description, "Click Set point to use the selected point object or pick one in Rhino."),
                    _ => input.Description,
                },
                Kind = input.Kind,
                SerializedValue = serializedValue,
                ValueListItems = input.ValueListItems,
                IsFilePath = input.IsFilePath,
                Minimum = input.Minimum,
                Maximum = input.Maximum,
                DecimalPlaces = input.DecimalPlaces,
                IsReadOnly = linkState.HasLink,
                HasLink = linkState.HasLink,
                IsLinkBroken = linkState.IsBroken,
                LinkSourceStepLabel = linkState.SourceStepLabel,
                LinkSourceOutputLabel = linkState.SourceOutputLabel,
                LinkStatusMessage = linkState.StatusMessage,
                AvailableLinks = BuildAvailableLinkOptions(stepContexts, currentStepContext, input, runtime).ToArray(),
                IsMissingRequiredValue = isMissingRequiredValue,
                ValidationMessage = isMissingRequiredValue
                    ? $"Set '{input.Label}' to run this modifier."
                    : string.Empty,
                ShowModifiedGeometryToggle = showModifiedGeometryToggle,
                UseModifiedGeometry = useModifiedGeometry,
                ModifiedGeometrySourceObjectId = modifiedGeometrySourceObjectId,
            };
        }
    }

    private static IEnumerable<ModifierStepOutputPanelState> BuildOutputPanelState(IReadOnlyList<StepOutputValue>? runtimeOutputs, DefinitionContract contract)
    {
        var displayById = runtimeOutputs?.ToDictionary(output => output.Id, output => output.DisplayValue, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var output in contract.Outputs)
        {
            displayById.TryGetValue(output.Id, out var displayValue);
            yield return new ModifierStepOutputPanelState
            {
                Id = output.Id,
                Label = output.Label,
                Description = output.Description,
                Kind = output.Kind,
                DisplayValue = displayValue ?? string.Empty,
            };
        }
    }

    private static (bool ShowToggle, bool UseModifiedGeometry, Guid? SourceObjectId) GetModifiedGeometryToggleState(
        RhinoDoc doc,
        ModifierStepSpec stepSpec,
        ModifierInputDescriptor input)
    {
        if (input.Kind != ModifierIoKind.Geometry)
        {
            return (false, false, null);
        }

        if (TryGetStepOutputInputLink(stepSpec, input.Id, out _))
        {
            return (false, false, null);
        }

        if (TryGetObjectPreviewInputLink(stepSpec, input.Id, out var objectPreviewLink))
        {
            return (true, true, objectPreviewLink.SourceObjectId);
        }

        if (!TryGetExplicitInputValue(stepSpec, input, out var serializedValue) ||
            !TryGetSingleReferencedObjectId(serializedValue, out var sourceObjectId))
        {
            return (false, false, null);
        }

        return DoesObjectHaveModifierStack(doc, sourceObjectId)
            ? (true, false, sourceObjectId)
            : (false, false, null);
    }

    private bool TryValidateInputLink(
        ModifierStackSpec spec,
        int targetIndex,
        string inputId,
        Guid sourceStepId,
        string sourceOutputId,
        out ModifierInputLinkSpec linkSpec,
        out string message)
    {
        linkSpec = null!;
        message = string.Empty;
        if (targetIndex < 0 || targetIndex >= spec.Steps.Count)
        {
            message = "Step index is out of range.";
            return false;
        }

        var sourceIndex = spec.Steps.FindIndex(step => step.StepId == sourceStepId);
        if (sourceIndex < 0)
        {
            message = "The linked source modifier no longer exists.";
            return false;
        }

        if (sourceIndex >= targetIndex)
        {
            message = "The linked source modifier must be earlier in the stack.";
            return false;
        }

        var targetStep = spec.Steps[targetIndex];
        if (!TryGetDefinitionContract(targetStep.Path, out var targetContract, out var targetError))
        {
            message = targetError;
            return false;
        }

        var targetInput = targetContract.Inputs.FirstOrDefault(input => input.Id.Equals(inputId, StringComparison.Ordinal));
        if (targetInput is null)
        {
            message = "The selected input is no longer available on this modifier.";
            return false;
        }

        var sourceStep = spec.Steps[sourceIndex];
        if (!sourceStep.Enabled)
        {
            message = "The linked source modifier is disabled.";
            return false;
        }

        if (!TryGetDefinitionContract(sourceStep.Path, out var sourceContract, out var sourceError))
        {
            message = sourceError;
            return false;
        }

        var sourceOutput = sourceContract.Outputs.FirstOrDefault(output => output.Id.Equals(sourceOutputId, StringComparison.Ordinal));
        if (sourceOutput is null)
        {
            message = "The selected output is no longer available on the source modifier.";
            return false;
        }

        if (!AreKindsLinkCompatible(targetInput.Kind, sourceOutput.Kind))
        {
            message = $"Output '{sourceOutput.Label}' is not compatible with input '{targetInput.Label}'.";
            return false;
        }

        linkSpec = new ModifierInputLinkSpec
        {
            SourceStepId = sourceStepId,
            SourceOutputId = sourceOutputId,
            SourceStepLabel = Path.GetFileName(sourceStep.Path),
            SourceOutputLabel = sourceOutput.Label,
        };
        return true;
    }

    private bool TryValidateObjectPreviewInputLink(
        RhinoDoc doc,
        Guid targetObjectId,
        ModifierStackSpec spec,
        int targetIndex,
        string inputId,
        Guid sourceObjectId,
        out ModifierInputLinkSpec linkSpec,
        out string message)
    {
        linkSpec = null!;
        message = string.Empty;
        if (targetIndex < 0 || targetIndex >= spec.Steps.Count)
        {
            message = "Step index is out of range.";
            return false;
        }

        if (sourceObjectId == Guid.Empty)
        {
            message = "Pick an object to use its modified result.";
            return false;
        }

        if (sourceObjectId == targetObjectId)
        {
            message = "A modifier input cannot reference the modified result of the same object.";
            return false;
        }

        var targetStep = spec.Steps[targetIndex];
        if (!TryGetDefinitionContract(targetStep.Path, out var targetContract, out var targetError))
        {
            message = targetError;
            return false;
        }

        var targetInput = targetContract.Inputs.FirstOrDefault(input => input.Id.Equals(inputId, StringComparison.Ordinal));
        if (targetInput is null)
        {
            message = "The selected input is no longer available on this modifier.";
            return false;
        }

        if (targetInput.Kind != ModifierIoKind.Geometry)
        {
            message = $"Input '{targetInput.Label}' does not support modified-geometry references.";
            return false;
        }

        var sourceRhinoObject = doc.Objects.FindId(sourceObjectId);
        if (sourceRhinoObject is null)
        {
            message = "The selected source object no longer exists.";
            return false;
        }

        var sourceSpec = ModifierStackStorage.Load(sourceRhinoObject);
        if (sourceSpec.Steps.Count == 0)
        {
            message = "The selected source object does not have any modifiers.";
            return false;
        }

        var candidateSpec = spec.Clone();
        candidateSpec.Steps[targetIndex].InputLinks[inputId] = new ModifierInputLinkSpec
        {
            SourceKind = ModifierInputLinkSourceKind.ObjectPreview,
            SourceObjectId = sourceObjectId,
            SourceObjectLabel = DescribeLinkedObject(sourceRhinoObject),
        };

        if (!TryValidateObjectPreviewGraph(doc, targetObjectId, candidateSpec, out message))
        {
            return false;
        }

        linkSpec = new ModifierInputLinkSpec
        {
            SourceKind = ModifierInputLinkSourceKind.ObjectPreview,
            SourceObjectId = sourceObjectId,
            SourceObjectLabel = DescribeLinkedObject(sourceRhinoObject),
        };
        return true;
    }

    private bool TryValidateObjectPreviewGraph(RhinoDoc doc, Guid objectId, ModifierStackSpec spec, out string message)
    {
        message = string.Empty;
        if (!TryGetObjectPreviewCyclePath(doc, objectId, GetActiveObjectPreviewDependencies(spec), out var cyclePath))
        {
            return true;
        }

        message = $"Circular modified-geometry reference detected: {FormatObjectPreviewCycle(doc, cyclePath)}.";
        return false;
    }

    private static IEnumerable<ModifierInputLinkOptionPanelState> BuildAvailableLinkOptions(
        IReadOnlyList<PanelStepContext> stepContexts,
        PanelStepContext currentStepContext,
        ModifierInputDescriptor input,
        StackRuntime? runtime)
    {
        for (var i = 0; i < currentStepContext.Index; i++)
        {
            var sourceStepContext = stepContexts[i];
            if (!sourceStepContext.Step.Enabled || sourceStepContext.Contract is null)
            {
                continue;
            }

            var runtimeOutputs = runtime?.GetOutputsForIndex(sourceStepContext.Index);
            foreach (var output in sourceStepContext.Contract.Outputs)
            {
                if (!AreKindsLinkCompatible(input.Kind, output.Kind))
                {
                    continue;
                }

                var hasRuntimeValue = TryGetStepOutputValue(runtimeOutputs, output.Id, out var runtimeOutput);
                yield return new ModifierInputLinkOptionPanelState
                {
                    SourceStepId = sourceStepContext.Step.StepId,
                    SourceStepIndex = sourceStepContext.Index,
                    SourceStepLabel = sourceStepContext.DisplayName,
                    SourceOutputId = output.Id,
                    SourceOutputLabel = output.Label,
                    Kind = output.Kind,
                    HasRuntimeValue = hasRuntimeValue,
                    RuntimeDisplayValue = hasRuntimeValue ? runtimeOutput.DisplayValue : string.Empty,
                    IsSelected = TryGetStepOutputInputLink(currentStepContext.Step, input.Id, out var activeLink) &&
                        activeLink.SourceStepId == sourceStepContext.Step.StepId &&
                        activeLink.SourceOutputId.Equals(output.Id, StringComparison.Ordinal),
                };
            }
        }
    }

    private LinkPresentationState BuildLinkPresentationState(
        RhinoDoc doc,
        Guid objectId,
        IReadOnlyList<PanelStepContext> stepContexts,
        PanelStepContext currentStepContext,
        ModifierInputDescriptor input,
        StackRuntime? runtime)
    {
        if (!TryGetInputLink(currentStepContext.Step, input.Id, out var activeLink))
        {
            return LinkPresentationState.None;
        }

        if (activeLink.SourceKind == ModifierInputLinkSourceKind.ObjectPreview)
        {
            return BuildObjectPreviewLinkPresentationState(doc, objectId, stepContexts.Select(context => context.Step), activeLink);
        }

        var sourceStepLabel = GetStoredStepLabel(activeLink);
        var sourceOutputLabel = GetStoredOutputLabel(activeLink);
        var sourceStepContext = stepContexts.FirstOrDefault(candidate => candidate.Step.StepId == activeLink.SourceStepId);
        if (sourceStepContext is null)
        {
            return new LinkPresentationState(true, true, sourceStepLabel, sourceOutputLabel, $"Linked source '{sourceStepLabel}' was removed.");
        }

        sourceStepLabel = sourceStepContext.DisplayName;
        if (sourceStepContext.Index >= currentStepContext.Index)
        {
            return new LinkPresentationState(true, true, sourceStepLabel, sourceOutputLabel, $"Linked source '{sourceStepLabel}' must stay above this modifier.");
        }

        if (!sourceStepContext.Step.Enabled)
        {
            return new LinkPresentationState(true, true, sourceStepLabel, sourceOutputLabel, $"Linked source '{sourceStepLabel}' is disabled.");
        }

        if (sourceStepContext.Contract is null)
        {
            var message = string.IsNullOrWhiteSpace(sourceStepContext.ContractError)
                ? $"Linked source '{sourceStepLabel}' could not be loaded."
                : $"Linked source '{sourceStepLabel}' could not be loaded: {sourceStepContext.ContractError}";
            return new LinkPresentationState(true, true, sourceStepLabel, sourceOutputLabel, message);
        }

        var sourceOutput = sourceStepContext.Contract.Outputs.FirstOrDefault(output => output.Id.Equals(activeLink.SourceOutputId, StringComparison.Ordinal));
        if (sourceOutput is null)
        {
            return new LinkPresentationState(true, true, sourceStepLabel, sourceOutputLabel, $"Linked output '{sourceOutputLabel}' no longer exists on '{sourceStepLabel}'.");
        }

        sourceOutputLabel = sourceOutput.Label;
        if (!AreKindsLinkCompatible(input.Kind, sourceOutput.Kind))
        {
            return new LinkPresentationState(true, true, sourceStepLabel, sourceOutputLabel, $"Linked output '{sourceOutputLabel}' is no longer compatible with '{input.Label}'.");
        }

        var runtimeOutputs = runtime?.GetOutputsForIndex(sourceStepContext.Index);
        var hasRuntimeValue = TryGetStepOutputValue(runtimeOutputs, sourceOutput.Id, out var runtimeOutput);
        var status = $"Linked from {sourceStepLabel} -> {sourceOutputLabel}.";
        if (hasRuntimeValue)
        {
            var displayValue = runtimeOutput.DisplayValue;
            if (displayValue.Length > 10)
            {
                displayValue = displayValue.Substring(0, 7) + "...";
            }

            status = $"{status} {displayValue}";
        }

        return new LinkPresentationState(true, false, sourceStepLabel, sourceOutputLabel, status);
    }

    private LinkPresentationState BuildObjectPreviewLinkPresentationState(
        RhinoDoc doc,
        Guid objectId,
        IEnumerable<ModifierStepSpec> steps,
        ModifierInputLinkSpec activeLink)
    {
        var sourceObjectLabel = GetStoredObjectLabel(activeLink);
        if (activeLink.SourceObjectId == Guid.Empty)
        {
            return new LinkPresentationState(true, true, sourceObjectLabel, string.Empty, "Modified source object is missing.");
        }

        if (TryGetObjectPreviewCyclePath(doc, objectId, GetActiveObjectPreviewDependencies(steps), out var cyclePath))
        {
            return new LinkPresentationState(true, true, sourceObjectLabel, string.Empty, $"Circular modified-geometry reference detected: {FormatObjectPreviewCycle(doc, cyclePath)}.");
        }

        var sourceRhinoObject = doc.Objects.FindId(activeLink.SourceObjectId);
        if (sourceRhinoObject is null)
        {
            return new LinkPresentationState(true, true, sourceObjectLabel, string.Empty, $"Modified source '{sourceObjectLabel}' was removed.");
        }

        sourceObjectLabel = DescribeLinkedObject(sourceRhinoObject);
        var sourceSpec = ModifierStackStorage.Load(sourceRhinoObject);
        if (sourceSpec.Steps.Count == 0)
        {
            return new LinkPresentationState(true, true, sourceObjectLabel, string.Empty, $"Modified source '{sourceObjectLabel}' no longer has any modifiers.");
        }

        var sourceRuntime = TryGetStackRuntime(doc, activeLink.SourceObjectId);
        if (sourceRuntime is null || !sourceRuntime.HasEvaluated)
        {
            return new LinkPresentationState(true, false, sourceObjectLabel, string.Empty, $"Using modified result of {sourceObjectLabel}. Waiting for preview evaluation.");
        }

        if (!string.IsNullOrWhiteSpace(sourceRuntime.ErrorMessage))
        {
            return new LinkPresentationState(true, true, sourceObjectLabel, string.Empty, $"Modified source '{sourceObjectLabel}' is unavailable: {sourceRuntime.ErrorMessage}");
        }

        var geometrySummary = sourceRuntime.PreviewGeometry.Count == 0
            ? "none"
            : DescribeGeometry(sourceRuntime.PreviewGeometry);
        return new LinkPresentationState(true, false, sourceObjectLabel, string.Empty, $"Using modified result of {sourceObjectLabel}. {geometrySummary}");
    }

    private static bool AreKindsLinkCompatible(ModifierIoKind inputKind, ModifierIoKind outputKind)
    {
        return IsNumericLinkKind(inputKind) && IsNumericLinkKind(outputKind)
            ? true
            : inputKind == outputKind;
    }

    private static bool IsNumericLinkKind(ModifierIoKind kind)
    {
        return kind is ModifierIoKind.Number or ModifierIoKind.NumberSlider;
    }

    private static string GetStoredStepLabel(ModifierInputLinkSpec inputLink)
    {
        return string.IsNullOrWhiteSpace(inputLink.SourceStepLabel)
            ? "previous modifier"
            : inputLink.SourceStepLabel;
    }

    private static string GetStoredOutputLabel(ModifierInputLinkSpec inputLink)
    {
        return string.IsNullOrWhiteSpace(inputLink.SourceOutputLabel)
            ? "output"
            : inputLink.SourceOutputLabel;
    }

    private static string GetStoredObjectLabel(ModifierInputLinkSpec inputLink)
    {
        return string.IsNullOrWhiteSpace(inputLink.SourceObjectLabel)
            ? "modifier object"
            : inputLink.SourceObjectLabel;
    }

    private static DefinitionContract CreateDefinitionContract(GH_Document document)
    {
        var inputGroupIds = GetGroupObjectIds(document, "Inputs");
        var outputGroupIds = GetGroupObjectIds(document, "Outputs");

        var inputs = new List<ModifierInputDescriptor>();
        foreach (var objectId in inputGroupIds)
        {
            var documentObject = ResolveDocumentObject(document, objectId);
            if (documentObject is null || documentObject is GH_Group)
            {
                continue;
            }

            if (TryCreateInputDescriptor(documentObject, out var descriptor, out var error) && descriptor is not null)
            {
                inputs.Add(descriptor);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException(error);
            }
        }

        var outputs = new List<ModifierOutputDescriptor>();
        var geometryOutputs = new List<ModifierOutputDescriptor>();
        foreach (var objectId in outputGroupIds)
        {
            var documentObject = ResolveDocumentObject(document, objectId);
            if (documentObject is null || documentObject is GH_Group)
            {
                continue;
            }

            if (TryCreateOutputDescriptor(documentObject, out var descriptor) && descriptor is not null)
            {
                outputs.Add(descriptor);
                if (documentObject is IGH_Param param &&
                    descriptor.Kind == ModifierIoKind.Geometry &&
                    IsGeometryPipeOutput(param))
                {
                    geometryOutputs.Add(descriptor);
                }
            }
        }

        var sceneInput = FindLegacySceneInput(document, inputGroupIds);

        if (geometryOutputs.Count == 0)
        {
            var legacyOutput = FindLegacyGeometryOutput(document, outputGroupIds);
            if (legacyOutput is not null)
            {
                geometryOutputs.Add(legacyOutput);
            }
        }

        return new DefinitionContract(sceneInput, inputs, outputs, geometryOutputs);
    }

    private static HashSet<Guid> GetGroupObjectIds(GH_Document document, string groupName)
    {
        var group = document.Objects
            .OfType<GH_Group>()
            .FirstOrDefault(candidate => candidate.NickName.Equals(groupName, StringComparison.OrdinalIgnoreCase));

        return group is null
            ? new HashSet<Guid>()
            : new HashSet<Guid>(group.ObjectIDs);
    }

    private static ModifierInputDescriptor? FindLegacySceneInput(GH_Document document, HashSet<Guid> groupedInputIds)
    {
        var param = document.Objects
            .OfType<IGH_Param>()
            .FirstOrDefault(candidate =>
                !groupedInputIds.Contains(candidate.InstanceGuid) &&
                InputAliases.Any(alias => candidate.NickName.Equals(alias, StringComparison.OrdinalIgnoreCase)));

        if (param is null)
        {
            return null;
        }

        if (param.Sources.Count > 0)
        {
            throw new InvalidOperationException("Legacy scene geometry input must be unwired.");
        }

        return CreateParamInputDescriptor(param, ModifierIoKind.Geometry, hasDefaultValue: false, defaultSerializedValue: string.Empty, usesSceneGeometryWhenBlank: true, isOptional: false, isFilePath: false, minimum: null, maximum: null, decimalPlaces: 0);
    }

    private static ModifierOutputDescriptor? FindLegacyGeometryOutput(GH_Document document, HashSet<Guid> groupedOutputIds)
    {
        var param = document.Objects
            .OfType<IGH_Param>()
            .FirstOrDefault(candidate =>
                !groupedOutputIds.Contains(candidate.InstanceGuid) &&
                OutputAliases.Any(alias => candidate.NickName.Equals(alias, StringComparison.OrdinalIgnoreCase)));

        return param is null
            ? null
            : CreateOutputDescriptor(param, ModifierIoKind.Geometry);
    }

    private static bool IsGeometryPipeOutput(IGH_Param param)
    {
        return OutputAliases.Any(alias => param.NickName.Equals(alias, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryCreateInputDescriptor(IGH_DocumentObject documentObject, out ModifierInputDescriptor? descriptor, out string error)
    {
        descriptor = null;
        error = string.Empty;

        if (documentObject is GH_ValueList valueList)
        {
            var selectedIndex = valueList.ListItems.FindIndex(item => item.Selected);
            if (selectedIndex < 0) selectedIndex = 0;
            descriptor = new ModifierInputDescriptor(
                valueList.InstanceGuid,
                valueList.InstanceGuid.ToString("D"),
                valueList.NickName,
                valueList.Description ?? string.Empty,
                ModifierIoKind.ValueList,
                selectedIndex.ToString(CultureInfo.InvariantCulture),
                true,
                false,
                true,
                null,
                null,
                0) { ValueListItems = valueList.ListItems.ConvertAll(item => item.Name) };
            return true;
        }

        if (documentObject is GH_NumberSlider slider)
        {
            descriptor = new ModifierInputDescriptor(
                slider.InstanceGuid,
                slider.InstanceGuid.ToString("D"),
                slider.NickName,
                slider.Description ?? string.Empty,
                ModifierIoKind.NumberSlider,
                SerializeNumber(slider.CurrentValue),
                true,
                false,
                false,
                (double)slider.Slider.Minimum,
                (double)slider.Slider.Maximum,
                slider.Slider.Type == GH_SliderAccuracy.Float ? slider.Slider.DecimalPlaces : 0);
            return true;
        }

        if (documentObject is not IGH_Param param || !TryGetSupportedParamKind(param, out var kind))
        {
            return false;
        }

        if (param.Sources.Count > 0)
        {
            error = $"Input '{GetDisplayLabel(param)}' must be unwired.";
            return false;
        }

        var hasDefaultValue = TryReadDefaultSerializedValue(param, kind, out var defaultSerializedValue);
        descriptor = CreateParamInputDescriptor(
            param,
            kind,
            hasDefaultValue,
            defaultSerializedValue,
            usesSceneGeometryWhenBlank: kind == ModifierIoKind.Geometry,
            isOptional: param.Optional,
            isFilePath: param is Param_FilePath,
            minimum: null,
            maximum: null,
            decimalPlaces: GetDefaultDecimalPlaces(param, kind));
        return true;
    }

    private static int GetDefaultDecimalPlaces(IGH_Param param, ModifierIoKind kind)
    {
        if (kind != ModifierIoKind.Number)
        {
            return 0;
        }

        return param is Param_Integer ? 0 : 3;
    }

    private static bool TryCreateOutputDescriptor(IGH_DocumentObject documentObject, out ModifierOutputDescriptor? descriptor)
    {
        descriptor = null;
        if (documentObject is not IGH_Param param || !TryGetSupportedParamKind(param, out var kind))
        {
            return false;
        }

        descriptor = CreateOutputDescriptor(param, kind);
        return true;
    }

    private static ModifierInputDescriptor CreateParamInputDescriptor(
        IGH_Param param,
        ModifierIoKind kind,
        bool hasDefaultValue,
        string defaultSerializedValue,
        bool usesSceneGeometryWhenBlank,
        bool isOptional,
        bool isFilePath,
        double? minimum,
        double? maximum,
        int decimalPlaces)
    {
        return new ModifierInputDescriptor(
            param.InstanceGuid,
            param.InstanceGuid.ToString("D"),
            GetDisplayLabel(param),
            param.Description ?? string.Empty,
            kind,
            defaultSerializedValue,
            hasDefaultValue,
            usesSceneGeometryWhenBlank,
            isOptional,
            minimum,
            maximum,
            decimalPlaces)
        {
            IsFilePath = isFilePath,
        };
    }

    private static ModifierOutputDescriptor CreateOutputDescriptor(IGH_Param param, ModifierIoKind kind)
    {
        return new ModifierOutputDescriptor(
            param.InstanceGuid,
            param.InstanceGuid.ToString("D"),
            GetDisplayLabel(param),
            param.Description ?? string.Empty,
            kind);
    }

    private static string GetDisplayLabel(IGH_DocumentObject documentObject)
    {
        return !string.IsNullOrWhiteSpace(documentObject.NickName)
            ? documentObject.NickName
            : documentObject.Name;
    }

    private static bool TryGetSupportedParamKind(IGH_Param param, out ModifierIoKind kind)
    {
        switch (param)
        {
            case Param_Number:
            case Param_Integer:
                kind = ModifierIoKind.Number;
                return true;
            case Param_Point:
                kind = ModifierIoKind.Point;
                return true;
            case Param_FilePath:
                kind = ModifierIoKind.String;
                return true;
            case Param_String:
                kind = ModifierIoKind.String;
                return true;
            case Param_Boolean:
                kind = ModifierIoKind.Boolean;
                return true;
            case Param_Colour:
                kind = ModifierIoKind.Color;
                return true;
            case Param_Geometry:
                kind = ModifierIoKind.Geometry;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static bool TryReadDefaultSerializedValue(IGH_Param param, ModifierIoKind kind, out string serializedValue)
    {
        serializedValue = string.Empty;
        var first = EnumeratePersistentData(param).FirstOrDefault();
        if (first is null)
        {
            return false;
        }

        return TryExtractPublishedOutputValue(first, kind, out _, out serializedValue);
    }

    private static List<RuntimeInputBinding> BindInputDescriptors(GH_Document document, IEnumerable<ModifierInputDescriptor> descriptors)
    {
        var bindings = new List<RuntimeInputBinding>();
        foreach (var descriptor in descriptors)
        {
            var documentObject = ResolveDocumentObject(document, descriptor.ObjectId)
                ?? throw new InvalidOperationException($"Modifier input '{descriptor.Label}' could not be found in the duplicated document.");

            if (documentObject is GH_NumberSlider slider)
            {
                bindings.Add(new RuntimeInputBinding(descriptor, slider));
                continue;
            }

            if (documentObject is GH_ValueList valueList)
            {
                bindings.Add(new RuntimeInputBinding(descriptor, valueList));
                continue;
            }

            if (documentObject is IGH_Param param)
            {
                bindings.Add(new RuntimeInputBinding(descriptor, param));
                continue;
            }

            throw new InvalidOperationException($"Modifier input '{descriptor.Label}' is not a supported Grasshopper input object.");
        }

        return bindings;
    }

    private static List<RuntimeOutputBinding> BindOutputDescriptors(GH_Document document, IEnumerable<ModifierOutputDescriptor> descriptors)
    {
        var bindings = new List<RuntimeOutputBinding>();
        foreach (var descriptor in descriptors)
        {
            var param = ResolveDocumentParam(document, descriptor.ObjectId)
                ?? throw new InvalidOperationException($"Modifier output '{descriptor.Label}' could not be found in the duplicated document.");
            bindings.Add(new RuntimeOutputBinding(descriptor, param));
        }

        return bindings;
    }

    private static IGH_DocumentObject? ResolveDocumentObject(GH_Document document, Guid instanceGuid)
    {
        return document.Objects.FirstOrDefault(candidate => candidate.InstanceGuid == instanceGuid);
    }

    private static IGH_Param? ResolveDocumentParam(GH_Document document, Guid instanceGuid)
    {
        return ResolveDocumentObject(document, instanceGuid) as IGH_Param;
    }

    private bool ApplyInputBinding(
        RhinoDoc doc,
        RuntimeInputBinding binding,
        ModifierStepSpec stepSpec,
        int stepIndex,
        IReadOnlyList<ModifierStepSpec> allSteps,
        IReadOnlyDictionary<Guid, IReadOnlyList<StepOutputValue>> publishedOutputsByStepId,
        IReadOnlyList<GeometryBase> currentGeometry,
        out string error)
    {
        error = string.Empty;

        if (TryGetInputLink(stepSpec, binding.Descriptor.Id, out var inputLink))
        {
            return TryApplyLinkedInput(doc, binding, stepIndex, allSteps, publishedOutputsByStepId, inputLink, out error);
        }

        var hasExplicitValue = TryGetExplicitInputValue(stepSpec, binding.Descriptor, out var serializedValue);

        if (binding.Slider is not null)
        {
            if (!hasExplicitValue)
            {
                return true;
            }

            if (!decimal.TryParse(serializedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var sliderValue))
            {
                error = $"Input '{binding.Descriptor.Label}' expects a number.";
                return false;
            }

            if (!binding.Slider.TrySetSliderValue(sliderValue))
            {
                binding.Slider.SetSliderValue(sliderValue);
            }

            return true;
        }

        if (binding.ValueList is not null)
        {
            if (hasExplicitValue &&
                int.TryParse(serializedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedIndex) &&
                selectedIndex >= 0 && selectedIndex < binding.ValueList.ListItems.Count)
            {
                binding.ValueList.SelectItem(selectedIndex);
            }

            return true;
        }

        if (binding.Param is null)
        {
            error = $"Input '{binding.Descriptor.Label}' is not bound to a Grasshopper parameter.";
            return false;
        }

        if (!hasExplicitValue)
        {
            if (binding.Descriptor.UsesSceneGeometryWhenBlank)
            {
                return TryAppendGeometry(binding.Param, currentGeometry, out error);
            }

            return true;
        }

        ClearParamData(binding.Param);
        switch (binding.Descriptor.Kind)
        {
            case ModifierIoKind.Number:
                if (!double.TryParse(serializedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var numberValue))
                {
                    error = $"Input '{binding.Descriptor.Label}' expects a number.";
                    return false;
                }

                AppendSingleValue(binding.Param, binding.Param is Param_Integer
                    ? (object)(int)Math.Round(numberValue, MidpointRounding.AwayFromZero)
                    : numberValue);
                return true;

            case ModifierIoKind.Point:
                if (string.IsNullOrWhiteSpace(serializedValue))
                {
                    return true;
                }

                if (!TryParsePoint(serializedValue, out var pointValue))
                {
                    error = $"Input '{binding.Descriptor.Label}' expects a point formatted like x,y,z.";
                    return false;
                }

                AppendSingleValue(binding.Param, pointValue);
                return true;

            case ModifierIoKind.String:
                AppendSingleValue(binding.Param, serializedValue);
                return true;

            case ModifierIoKind.Boolean:
                if (!bool.TryParse(serializedValue, out var boolValue))
                {
                    error = $"Input '{binding.Descriptor.Label}' expects true or false.";
                    return false;
                }

                AppendSingleValue(binding.Param, boolValue);
                return true;

            case ModifierIoKind.Color:
                if (string.IsNullOrWhiteSpace(serializedValue))
                {
                    return true;
                }

                if (!TryParseColor(serializedValue, out var colorValue))
                {
                    error = $"Input '{binding.Descriptor.Label}' expects a color like #RRGGBB or r,g,b.";
                    return false;
                }

                AppendSingleValue(binding.Param, colorValue);
                return true;

            case ModifierIoKind.Geometry:
                if (string.IsNullOrWhiteSpace(serializedValue))
                {
                    return binding.Descriptor.UsesSceneGeometryWhenBlank
                        ? TryAppendGeometry(binding.Param, currentGeometry, out error)
                        : true;
                }

                return TryAppendReferencedGeometry(doc, binding.Param, serializedValue, currentGeometry, out error);

            default:
                error = $"Input '{binding.Descriptor.Label}' uses an unsupported input type.";
                return false;
        }
    }

    private bool TryApplyLinkedInput(
        RhinoDoc doc,
        RuntimeInputBinding binding,
        int stepIndex,
        IReadOnlyList<ModifierStepSpec> allSteps,
        IReadOnlyDictionary<Guid, IReadOnlyList<StepOutputValue>> publishedOutputsByStepId,
        ModifierInputLinkSpec inputLink,
        out string error)
    {
        error = string.Empty;
        if (inputLink.SourceKind == ModifierInputLinkSourceKind.ObjectPreview)
        {
            return TryApplyObjectPreviewInput(doc, binding, inputLink, out error);
        }

        if (!TryResolveLinkedOutput(binding.Descriptor, stepIndex, allSteps, publishedOutputsByStepId, inputLink, out var sourceOutputValue, out var sourceStepLabel, out var sourceOutputLabel, out error))
        {
            return false;
        }

        if (binding.Slider is not null)
        {
            return TrySetLinkedSliderValue(binding, sourceOutputValue, sourceOutputLabel, out error);
        }

        if (binding.Param is null)
        {
            error = $"Input '{binding.Descriptor.Label}' is not bound to a Grasshopper parameter.";
            return false;
        }

        return TrySetLinkedParamValues(binding, sourceOutputValue, sourceStepLabel, sourceOutputLabel, out error);
    }

    private bool TryApplyObjectPreviewInput(RhinoDoc doc, RuntimeInputBinding binding, ModifierInputLinkSpec inputLink, out string error)
    {
        error = string.Empty;
        if (binding.Descriptor.Kind != ModifierIoKind.Geometry)
        {
            error = $"Input '{binding.Descriptor.Label}' does not support modified-geometry references.";
            return false;
        }

        if (binding.Param is null)
        {
            error = $"Input '{binding.Descriptor.Label}' is not bound to a Grasshopper parameter.";
            return false;
        }

        var sourceObjectLabel = GetStoredObjectLabel(inputLink);
        var sourceRhinoObject = doc.Objects.FindId(inputLink.SourceObjectId);
        if (sourceRhinoObject is null)
        {
            error = $"Modified source '{sourceObjectLabel}' could not be found.";
            return false;
        }

        sourceObjectLabel = DescribeLinkedObject(sourceRhinoObject);
        var sourceSpec = ModifierStackStorage.Load(sourceRhinoObject);
        if (sourceSpec.Steps.Count == 0)
        {
            error = $"Modified source '{sourceObjectLabel}' no longer has any modifiers.";
            return false;
        }

        var sourceRuntime = TryGetStackRuntime(doc, inputLink.SourceObjectId);
        if (sourceRuntime is null || !sourceRuntime.HasEvaluated)
        {
            error = $"Modified source '{sourceObjectLabel}' has no preview available yet.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(sourceRuntime.ErrorMessage))
        {
            error = $"Modified source '{sourceObjectLabel}' is unavailable: {sourceRuntime.ErrorMessage}";
            return false;
        }

        return TryAppendGeometry(binding.Param, sourceRuntime.PreviewGeometry, out error);
    }

    private bool TryResolveLinkedOutput(
        ModifierInputDescriptor targetInput,
        int targetStepIndex,
        IReadOnlyList<ModifierStepSpec> allSteps,
        IReadOnlyDictionary<Guid, IReadOnlyList<StepOutputValue>> publishedOutputsByStepId,
        ModifierInputLinkSpec inputLink,
        out StepOutputValue sourceOutputValue,
        out string sourceStepLabel,
        out string sourceOutputLabel,
        out string error)
    {
        sourceOutputValue = default;
        sourceStepLabel = GetStoredStepLabel(inputLink);
        sourceOutputLabel = GetStoredOutputLabel(inputLink);
        error = string.Empty;

        if (inputLink.SourceKind != ModifierInputLinkSourceKind.StepOutput)
        {
            error = $"Input '{targetInput.Label}' is not linked to an upstream modifier output.";
            return false;
        }

        var sourceStepIndex = -1;
        for (var i = 0; i < allSteps.Count; i++)
        {
            if (allSteps[i].StepId == inputLink.SourceStepId)
            {
                sourceStepIndex = i;
                break;
            }
        }
        if (sourceStepIndex < 0)
        {
            error = $"Linked source '{sourceStepLabel}' was removed.";
            return false;
        }

        if (sourceStepIndex >= targetStepIndex)
        {
            error = $"Linked source '{sourceStepLabel}' must stay above this modifier.";
            return false;
        }

        var sourceStep = allSteps[sourceStepIndex];
        sourceStepLabel = Path.GetFileName(sourceStep.Path);
        if (!sourceStep.Enabled)
        {
            error = $"Linked source '{sourceStepLabel}' is disabled.";
            return false;
        }

        if (!TryGetDefinitionContract(sourceStep.Path, out var sourceContract, out var contractError))
        {
            error = $"Linked source '{sourceStepLabel}' could not be loaded: {contractError}";
            return false;
        }

        var sourceOutput = sourceContract.Outputs.FirstOrDefault(output => output.Id.Equals(inputLink.SourceOutputId, StringComparison.Ordinal));
        if (sourceOutput is null)
        {
            error = $"Linked output '{sourceOutputLabel}' no longer exists on '{sourceStepLabel}'.";
            return false;
        }

        sourceOutputLabel = sourceOutput.Label;
        if (!AreKindsLinkCompatible(targetInput.Kind, sourceOutput.Kind))
        {
            error = $"Linked output '{sourceOutputLabel}' is no longer compatible with input '{targetInput.Label}'.";
            return false;
        }

        if (!publishedOutputsByStepId.TryGetValue(sourceStep.StepId, out var publishedOutputs) ||
            !TryGetStepOutputValue(publishedOutputs, sourceOutput.Id, out sourceOutputValue))
        {
            error = $"Linked source '{sourceStepLabel}' has no runtime output available for '{sourceOutputLabel}'.";
            return false;
        }

        return true;
    }

    private static bool TryGetInputLink(ModifierStepSpec stepSpec, string inputId, out ModifierInputLinkSpec linkSpec)
    {
        if (stepSpec.InputLinks.TryGetValue(inputId, out var storedLink) &&
            IsStoredLinkValid(storedLink))
        {
            linkSpec = storedLink!;
            return true;
        }

        linkSpec = null!;
        return false;
    }

    private static bool TryGetStepOutputInputLink(ModifierStepSpec stepSpec, string inputId, out ModifierInputLinkSpec linkSpec)
    {
        if (TryGetInputLink(stepSpec, inputId, out linkSpec) &&
            linkSpec.SourceKind == ModifierInputLinkSourceKind.StepOutput)
        {
            return true;
        }

        linkSpec = null!;
        return false;
    }

    private static bool TryGetObjectPreviewInputLink(ModifierStepSpec stepSpec, string inputId, out ModifierInputLinkSpec linkSpec)
    {
        if (TryGetInputLink(stepSpec, inputId, out linkSpec) &&
            linkSpec.SourceKind == ModifierInputLinkSourceKind.ObjectPreview)
        {
            return true;
        }

        linkSpec = null!;
        return false;
    }

    private static bool IsStoredLinkValid(ModifierInputLinkSpec? storedLink)
    {
        if (storedLink is null)
        {
            return false;
        }

        return storedLink.SourceKind switch
        {
            ModifierInputLinkSourceKind.StepOutput => storedLink.SourceStepId != Guid.Empty &&
                !string.IsNullOrWhiteSpace(storedLink.SourceOutputId),
            ModifierInputLinkSourceKind.ObjectPreview => storedLink.SourceObjectId != Guid.Empty,
            _ => false,
        };
    }

    private static bool TryGetExplicitInputValue(ModifierStepSpec stepSpec, ModifierInputDescriptor descriptor, out string serializedValue)
    {
        if (stepSpec.InputValues.TryGetValue(descriptor.Id, out var storedValue))
        {
            serializedValue = storedValue?.Trim() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(serializedValue);
        }

        serializedValue = string.Empty;
        return false;
    }

    private static string GetDisplayedInputValue(ModifierStepSpec stepSpec, ModifierInputDescriptor descriptor)
    {
        if (TryGetExplicitInputValue(stepSpec, descriptor, out var serializedValue))
        {
            return serializedValue;
        }

        return descriptor.HasDefaultValue
            ? descriptor.DefaultSerializedValue
            : string.Empty;
    }

    private static bool IsMissingRequiredInput(ModifierStepSpec stepSpec, ModifierInputDescriptor descriptor)
    {
        if (descriptor.IsOptional || descriptor.UsesSceneGeometryWhenBlank || TryGetInputLink(stepSpec, descriptor.Id, out _))
        {
            return false;
        }

        if (TryGetExplicitInputValue(stepSpec, descriptor, out _))
        {
            return false;
        }

        return !descriptor.HasDefaultValue;
    }

    private static IEnumerable<ModifierInputDescriptor> GetMissingRequiredInputs(ModifierStepSpec stepSpec, IEnumerable<ModifierInputDescriptor> descriptors)
    {
        return descriptors.Where(descriptor => IsMissingRequiredInput(stepSpec, descriptor));
    }

    private static string FormatMissingRequiredInputs(IEnumerable<string> labels)
    {
        var missingLabels = labels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (missingLabels.Length == 0)
        {
            return "Required inputs are missing.";
        }

        return $"Missing required inputs: {string.Join(", ", missingLabels)}.";
    }

    private static void ClearParamData(IGH_Param param)
    {
        param.ClearData();
        SetPersistentData(param, Array.Empty<object>());
    }

    private static void AppendSingleValue(IGH_Param param, object value)
    {
        SetPersistentData(param, new[] { value });
    }

    private static bool TryAppendGeometry(IGH_Param param, IEnumerable<GeometryBase> geometry, out string error)
    {
        error = string.Empty;
        ClearParamData(param);
        try
        {
            SetPersistentData(param, geometry.Cast<object>());
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryAppendReferencedGeometry(RhinoDoc doc, IGH_Param param, string serializedValue, IReadOnlyList<GeometryBase> currentGeometry, out string error)
    {
        error = string.Empty;
        var tokens = TokenizeGeometryReferenceValue(serializedValue);

        var geometry = new List<GeometryBase>();
        foreach (var token in tokens)
        {
            if (token.Equals("self", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("selected", StringComparison.OrdinalIgnoreCase))
            {
                geometry.AddRange(CloneGeometry(currentGeometry));
                continue;
            }

            if (!Guid.TryParse(token, out var objectId))
            {
                error = $"Geometry input token '{token}' is not a Rhino object id.";
                return false;
            }

            var rhinoObject = doc.Objects.FindId(objectId);
            if (rhinoObject is null)
            {
                error = $"Rhino object '{objectId}' could not be found for geometry input.";
                return false;
            }

            if (!GeometryConversion.TryGetSourceGeometry(rhinoObject.Geometry, out var converted, out var conversionError))
            {
                error = conversionError;
                return false;
            }

            geometry.AddRange(converted);
        }

        return TryAppendGeometry(param, geometry, out error);
    }

    private static bool TryGetSingleReferencedObjectId(string serializedValue, out Guid objectId)
    {
        objectId = Guid.Empty;
        var tokens = TokenizeGeometryReferenceValue(serializedValue);
        if (tokens.Length != 1)
        {
            return false;
        }

        return Guid.TryParse(tokens[0], out objectId);
    }

    private static string[] TokenizeGeometryReferenceValue(string serializedValue)
    {
        return serializedValue
            .Split(new[] { ',', ';', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool TrySetLinkedSliderValue(RuntimeInputBinding binding, StepOutputValue sourceOutputValue, string sourceOutputLabel, out string error)
    {
        error = string.Empty;
        if (binding.Slider is null)
        {
            error = $"Input '{binding.Descriptor.Label}' is not bound to a slider.";
            return false;
        }

        if (sourceOutputValue.Values.Count != 1)
        {
            error = $"Linked output '{sourceOutputLabel}' must provide exactly one numeric value for slider input '{binding.Descriptor.Label}'.";
            return false;
        }

        if (!TryConvertToDouble(sourceOutputValue.Values[0], out var sliderValue))
        {
            error = $"Linked output '{sourceOutputLabel}' must provide a numeric value for slider input '{binding.Descriptor.Label}'.";
            return false;
        }

        var decimalValue = (decimal)sliderValue;
        if (!binding.Slider.TrySetSliderValue(decimalValue))
        {
            binding.Slider.SetSliderValue(decimalValue);
        }

        return true;
    }

    private static bool TrySetLinkedParamValues(RuntimeInputBinding binding, StepOutputValue sourceOutputValue, string sourceStepLabel, string sourceOutputLabel, out string error)
    {
        error = string.Empty;
        if (binding.Param is null)
        {
            error = $"Input '{binding.Descriptor.Label}' is not bound to a Grasshopper parameter.";
            return false;
        }

        var values = new List<object>(sourceOutputValue.Values.Count);
        switch (binding.Descriptor.Kind)
        {
            case ModifierIoKind.Number:
                foreach (var publishedValue in sourceOutputValue.Values)
                {
                    if (!TryConvertToDouble(publishedValue, out var numberValue))
                    {
                        error = $"Linked output '{sourceOutputLabel}' from '{sourceStepLabel}' did not publish numeric values.";
                        return false;
                    }

                    values.Add(binding.Param is Param_Integer
                        ? (object)(int)Math.Round(numberValue, MidpointRounding.AwayFromZero)
                        : numberValue);
                }
                break;

            case ModifierIoKind.Point:
                foreach (var publishedValue in sourceOutputValue.Values)
                {
                    if (publishedValue is not Point3d pointValue)
                    {
                        error = $"Linked output '{sourceOutputLabel}' from '{sourceStepLabel}' did not publish point values.";
                        return false;
                    }

                    values.Add(pointValue);
                }
                break;

            case ModifierIoKind.String:
                foreach (var publishedValue in sourceOutputValue.Values)
                {
                    if (publishedValue is not string stringValue)
                    {
                        error = $"Linked output '{sourceOutputLabel}' from '{sourceStepLabel}' did not publish text values.";
                        return false;
                    }

                    values.Add(stringValue);
                }
                break;

            case ModifierIoKind.Boolean:
                foreach (var publishedValue in sourceOutputValue.Values)
                {
                    if (publishedValue is not bool boolValue)
                    {
                        error = $"Linked output '{sourceOutputLabel}' from '{sourceStepLabel}' did not publish boolean values.";
                        return false;
                    }

                    values.Add(boolValue);
                }
                break;

            case ModifierIoKind.Color:
                foreach (var publishedValue in sourceOutputValue.Values)
                {
                    if (publishedValue is not System.Drawing.Color colorValue)
                    {
                        error = $"Linked output '{sourceOutputLabel}' from '{sourceStepLabel}' did not publish color values.";
                        return false;
                    }

                    values.Add(colorValue);
                }
                break;

            case ModifierIoKind.Geometry:
                foreach (var publishedValue in sourceOutputValue.Values)
                {
                    if (publishedValue is not GeometryBase geometryValue)
                    {
                        error = $"Linked output '{sourceOutputLabel}' from '{sourceStepLabel}' did not publish geometry values.";
                        return false;
                    }

                    values.Add(geometryValue.Duplicate());
                }
                break;

            default:
                error = $"Input '{binding.Descriptor.Label}' uses an unsupported input type.";
                return false;
        }

        return TrySetParamValues(binding.Param, values, out error);
    }

    private static bool TrySetParamValues(IGH_Param param, IEnumerable<object> values, out string error)
    {
        error = string.Empty;
        ClearParamData(param);
        try
        {
            SetPersistentData(param, values);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryGetStepOutputValue(IReadOnlyList<StepOutputValue>? outputs, string outputId, out StepOutputValue outputValue)
    {
        if (outputs is not null)
        {
            foreach (var candidate in outputs)
            {
                if (candidate.Id.Equals(outputId, StringComparison.Ordinal))
                {
                    outputValue = candidate;
                    return true;
                }
            }
        }

        outputValue = default;
        return false;
    }

    private static StepOutputValue CapturePublishedOutput(ModifierOutputDescriptor descriptor, IGH_Param param)
    {
        if (descriptor.Kind == ModifierIoKind.Geometry)
        {
            var geometry = GeometryConversion.ReadOutput(param);
            return new StepOutputValue(
                descriptor.Id,
                geometry.Geometry.Count == 0 ? "none" : DescribeGeometry(geometry.Geometry),
                CloneGeometry(geometry.Geometry).Cast<object>().ToList());
        }

        var publishedValues = new List<object>();
        var displayValues = new List<string>();
        foreach (var goo in param.VolatileData.AllData(true))
        {
            if (TryExtractPublishedOutputValue(goo, descriptor.Kind, out var publishedValue, out var serializedValue))
            {
                publishedValues.Add(publishedValue);
                displayValues.Add(serializedValue);
            }
        }

        return new StepOutputValue(descriptor.Id, FormatPublishedOutputDisplay(displayValues), publishedValues);
    }

    private static string FormatPublishedOutputDisplay(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return "none";
        }

        const int maxItems = 4;
        if (values.Count <= maxItems)
        {
            return string.Join(", ", values);
        }

        return $"{string.Join(", ", values.Take(maxItems))} (+{values.Count - maxItems} more)";
    }

    private static bool TryExtractPublishedOutputValue(IGH_Goo goo, ModifierIoKind kind, out object publishedValue, out string serializedValue)
    {
        serializedValue = string.Empty;
        publishedValue = null!;
        var value = goo.ScriptVariable();
        if (value is null)
        {
            return false;
        }

        switch (kind)
        {
            case ModifierIoKind.Number:
            case ModifierIoKind.NumberSlider:
                if (TryConvertToDouble(value, out var numberValue))
                {
                    publishedValue = numberValue;
                    serializedValue = SerializeNumber(numberValue);
                    return true;
                }

                return false;

            case ModifierIoKind.Point:
                if (value is Point3d point)
                {
                    publishedValue = point;
                    serializedValue = SerializePoint(point);
                    return true;
                }

                return false;

            case ModifierIoKind.String:
                publishedValue = value.ToString() ?? string.Empty;
                serializedValue = (string)publishedValue;
                return true;

            case ModifierIoKind.Boolean:
                if (value is bool boolValue)
                {
                    publishedValue = boolValue;
                    serializedValue = boolValue ? bool.TrueString.ToLowerInvariant() : bool.FalseString.ToLowerInvariant();
                    return true;
                }

                return false;

            case ModifierIoKind.Color:
                if (value is System.Drawing.Color color)
                {
                    publishedValue = color;
                    serializedValue = SerializeColor(color);
                    return true;
                }

                return false;

            case ModifierIoKind.Geometry:
                if (value is GeometryBase geometry)
                {
                    publishedValue = geometry.Duplicate();
                    serializedValue = value.ToString() ?? string.Empty;
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    private static bool TryConvertToDouble(object value, out double numberValue)
    {
        switch (value)
        {
            case double doubleValue:
                numberValue = doubleValue;
                return true;
            case decimal decimalValue:
                numberValue = (double)decimalValue;
                return true;
            case int intValue:
                numberValue = intValue;
                return true;
            case long longValue:
                numberValue = longValue;
                return true;
            case float floatValue:
                numberValue = floatValue;
                return true;
            default:
                numberValue = 0;
                return false;
        }
    }

    private static string SerializeNumber(decimal value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string SerializeNumber(double value)
    {
        return value.ToString("0.###############", CultureInfo.InvariantCulture);
    }

    private static string SerializePoint(Point3d point)
    {
        return FormattableString.Invariant($"{point.X:0.###############},{point.Y:0.###############},{point.Z:0.###############}");
    }

    private static bool TryParsePoint(string serializedValue, out Point3d point)
    {
        var cleaned = serializedValue.Replace("(", string.Empty).Replace(")", string.Empty);
        var parts = cleaned
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length != 3 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            point = Point3d.Unset;
            return false;
        }

        point = new Point3d(x, y, z);
        return true;
    }

    private static string SerializeColor(System.Drawing.Color color)
    {
        return color.A == byte.MaxValue
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static bool TryParseColor(string serializedValue, out System.Drawing.Color color)
    {
        var trimmed = serializedValue.Trim();
        if (trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            var hex = trimmed[1..];
            if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            {
                color = System.Drawing.Color.FromArgb(
                    byte.MaxValue,
                    (rgb >> 16) & 0xFF,
                    (rgb >> 8) & 0xFF,
                    rgb & 0xFF);
                return true;
            }

            if (hex.Length == 8 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            {
                color = System.Drawing.Color.FromArgb(
                    (argb >> 24) & 0xFF,
                    (argb >> 16) & 0xFF,
                    (argb >> 8) & 0xFF,
                    argb & 0xFF);
                return true;
            }
        }

        var parts = trimmed
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if ((parts.Length == 3 || parts.Length == 4) &&
            byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) &&
            byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) &&
            byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
        {
            var a = byte.MaxValue;
            if (parts.Length == 4 &&
                !byte.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out a))
            {
                color = default;
                return false;
            }

            color = System.Drawing.Color.FromArgb(a, r, g, b);
            return true;
        }

        color = default;
        return false;
    }

    private static string AppendDescription(string description, string note)
    {
        return string.IsNullOrWhiteSpace(description)
            ? note
            : $"{description} {note}";
    }

    private static IEnumerable<IGH_Goo> EnumeratePersistentData(IGH_Param param)
    {
        var persistentData = param.GetType().GetProperty("PersistentData")?.GetValue(param);
        var allData = persistentData?.GetType().GetMethod("AllData", new[] { typeof(bool) });
        if (allData?.Invoke(persistentData, new object[] { true }) is not IEnumerable enumerable)
        {
            yield break;
        }

        foreach (var item in enumerable)
        {
            if (item is IGH_Goo goo)
            {
                yield return goo;
            }
        }
    }

    private static void SetPersistentData(IGH_Param param, IEnumerable<object> values)
    {
        var clearMethod = param.GetType().GetMethod("Script_ClearPersistentData", Type.EmptyTypes);
        var addMethod = param.GetType().GetMethod("Script_AddPersistentData", new[] { typeof(List<object>) });
        if (clearMethod is null || addMethod is null)
        {
            throw new InvalidOperationException($"Parameter '{GetDisplayLabel(param)}' does not expose persistent data scripting APIs.");
        }

        clearMethod.Invoke(param, Array.Empty<object>());
        addMethod.Invoke(param, new object[] { values.ToList() });
    }

    private bool TryPrepareObjectPreviewDependencies(RhinoDoc doc, Guid objectId, ModifierStackSpec spec, out string reason)
    {
        reason = string.Empty;
        var waitingForUpstream = false;
        foreach (var sourceObjectId in GetActiveObjectPreviewDependencies(spec))
        {
            var sourceRhinoObject = doc.Objects.FindId(sourceObjectId);
            if (sourceRhinoObject is null)
            {
                continue;
            }

            var sourceSpec = ModifierStackStorage.Load(sourceRhinoObject);
            if (sourceSpec.Steps.Count == 0)
            {
                continue;
            }

            UpdateObjectDependencies(doc, sourceObjectId, sourceSpec);
            var sourceRuntime = GetOrCreateStackRuntime(doc, sourceObjectId);
            var documentState = GetOrCreateDocumentState(doc);
            if (!sourceRuntime.HasEvaluated || documentState.DirtyObjects.Contains(sourceObjectId))
            {
                QueueEvaluation(doc, sourceObjectId);
                waitingForUpstream = true;
                if (string.IsNullOrWhiteSpace(reason))
                {
                    reason = $"Waiting for modified source {DescribeLinkedObject(sourceRhinoObject)}.";
                }
            }
        }

        return !waitingForUpstream;
    }

    private bool TryEvaluateStackThroughStep(
        RhinoDoc doc,
        Guid objectId,
        RhinoObject rhinoObject,
        ModifierStackSpec spec,
        int stepIndex,
        out List<GeometryBase> outputGeometry,
        out string error)
    {
        outputGeometry = new List<GeometryBase>();
        error = string.Empty;

        if (!GeometryConversion.TryGetSourceGeometry(rhinoObject.Geometry, out var currentGeometry, out var sourceError))
        {
            error = sourceError;
            return false;
        }

        var runtime = GetOrCreateStackRuntime(doc, objectId);
        runtime.EnsureStepCapacity(spec.Steps.Count);
        var publishedOutputsByStepId = new Dictionary<Guid, IReadOnlyList<StepOutputValue>>();

        for (var i = 0; i <= stepIndex; i++)
        {
            var stepSpec = spec.Steps[i];
            if (!stepSpec.Enabled)
            {
                runtime.DisposeStep(i);
                runtime.ClearOutputs(i);
                publishedOutputsByStepId.Remove(stepSpec.StepId);
                continue;
            }

            StepRuntime stepRuntime;
            try
            {
                stepRuntime = EnsureStepRuntime(runtime, i, stepSpec);
            }
            catch (Exception ex)
            {
                error = $"Modifier {i + 1} failed to initialize: {ex.Message}";
                return false;
            }

            var result = EvaluateStep(doc, stepRuntime, stepSpec, i, spec.Steps, publishedOutputsByStepId, currentGeometry);
            if (!result.Success)
            {
                error = $"Modifier {i + 1} '{Path.GetFileName(stepSpec.Path)}' failed: {result.ErrorMessage}";
                return false;
            }

            publishedOutputsByStepId[stepSpec.StepId] = result.PublishedOutputs;
            if (result.HasGeometryOutput)
            {
                currentGeometry = CloneGeometry(result.OutputGeometry);
            }
        }

        outputGeometry = CloneGeometry(currentGeometry);
        return true;
    }

    private static bool TryEnsureSupportedApplyGeometry(GeometryBase geometry, out string error)
    {
        switch (geometry)
        {
            case Rhino.Geometry.Point:
            case Curve:
            case Brep:
            case Mesh:
            case SubD:
                error = string.Empty;
                return true;
            default:
                error = $"Unsupported geometry type '{geometry.ObjectType}' for apply operation.";
                return false;
        }
    }

    private static bool ReplaceManagedObjectGeometry(RhinoDoc doc, Guid objectId, GeometryBase geometry, out string error)
    {
        error = string.Empty;
        var toReplaceWith = geometry.Duplicate();
        var replaced = toReplaceWith switch
        {
            Rhino.Geometry.Point point => doc.Objects.Replace(objectId, point.Location),
            Curve curve => doc.Objects.Replace(objectId, curve),
            Brep brep => doc.Objects.Replace(objectId, brep),
            Mesh mesh => doc.Objects.Replace(objectId, mesh),
            SubD subD => doc.Objects.Replace(objectId, subD),
            _ => false,
        };

        if (replaced)
        {
            return true;
        }

        error = "Rhino failed to replace object geometry.";
        return false;
    }

    private static bool TryAddGeometryObject(RhinoDoc doc, GeometryBase geometry, ObjectAttributes attributes, out string error)
    {
        error = string.Empty;
        var toAdd = geometry.Duplicate();
        var addedId = toAdd switch
        {
            Rhino.Geometry.Point point => doc.Objects.AddPoint(point.Location, attributes),
            Curve curve => doc.Objects.AddCurve(curve, attributes),
            Brep brep => doc.Objects.AddBrep(brep, attributes),
            Mesh mesh => doc.Objects.AddMesh(mesh, attributes),
            SubD subD => doc.Objects.AddSubD(subD, attributes),
            _ => Guid.Empty,
        };

        if (addedId != Guid.Empty)
        {
            return true;
        }

        error = "Rhino failed to add geometry produced by the applied stack.";
        return false;
    }

    private void UpdateObjectDependencies(RhinoDoc doc, Guid objectId, ModifierStackSpec spec)
    {
        var documentState = GetOrCreateDocumentState(doc);
        var newDependencies = GetActiveObjectPreviewDependencies(spec).ToHashSet();
        var oldDependencies = documentState.DependenciesByObject.TryGetValue(objectId, out var existingDependencies)
            ? existingDependencies.ToHashSet()
            : new HashSet<Guid>();

        foreach (var sourceObjectId in oldDependencies)
        {
            if (newDependencies.Contains(sourceObjectId))
            {
                continue;
            }

            if (documentState.DependentsByObject.TryGetValue(sourceObjectId, out var dependents))
            {
                dependents.Remove(objectId);
                if (dependents.Count == 0)
                {
                    documentState.DependentsByObject.Remove(sourceObjectId);
                }
            }
        }

        if (newDependencies.Count == 0)
        {
            documentState.DependenciesByObject.Remove(objectId);
            return;
        }

        documentState.DependenciesByObject[objectId] = newDependencies;
        foreach (var sourceObjectId in newDependencies)
        {
            if (!documentState.DependentsByObject.TryGetValue(sourceObjectId, out var dependents))
            {
                dependents = new HashSet<Guid>();
                documentState.DependentsByObject[sourceObjectId] = dependents;
            }

            dependents.Add(objectId);
        }
    }

    private void DetachObjectDependencies(RhinoDoc doc, Guid objectId)
    {
        if (!_documents.TryGetValue(doc.RuntimeSerialNumber, out var documentState))
        {
            return;
        }

        if (documentState.DependenciesByObject.TryGetValue(objectId, out var dependencies))
        {
            foreach (var sourceObjectId in dependencies)
            {
                if (documentState.DependentsByObject.TryGetValue(sourceObjectId, out var dependents))
                {
                    dependents.Remove(objectId);
                    if (dependents.Count == 0)
                    {
                        documentState.DependentsByObject.Remove(sourceObjectId);
                    }
                }
            }

            documentState.DependenciesByObject.Remove(objectId);
        }

        documentState.DirtyObjects.Remove(objectId);
    }

    private void QueueDependentEvaluations(RhinoDoc doc, Guid objectId)
    {
        if (!_documents.TryGetValue(doc.RuntimeSerialNumber, out var documentState) ||
            !documentState.DependentsByObject.TryGetValue(objectId, out var dependents))
        {
            return;
        }

        foreach (var dependentObjectId in dependents.ToArray())
        {
            QueueEvaluation(doc, dependentObjectId);
        }
    }

    private void MarkObjectClean(RhinoDoc doc, Guid objectId)
    {
        if (_documents.TryGetValue(doc.RuntimeSerialNumber, out var documentState))
        {
            documentState.DirtyObjects.Remove(objectId);
        }
    }

    private static IEnumerable<Guid> GetActiveObjectPreviewDependencies(ModifierStackSpec spec)
    {
        return GetActiveObjectPreviewDependencies(spec.Steps);
    }

    private static IEnumerable<Guid> GetActiveObjectPreviewDependencies(IEnumerable<ModifierStepSpec> steps)
    {
        foreach (var step in steps)
        {
            if (!step.Enabled)
            {
                continue;
            }

            foreach (var inputLink in step.InputLinks.Values)
            {
                if (inputLink is null || inputLink.SourceKind != ModifierInputLinkSourceKind.ObjectPreview || inputLink.SourceObjectId == Guid.Empty)
                {
                    continue;
                }

                yield return inputLink.SourceObjectId;
            }
        }
    }

    private static bool TryGetObjectPreviewCycleError(RhinoDoc doc, Guid objectId, ModifierStackSpec spec, out string message)
    {
        message = string.Empty;
        if (!TryGetObjectPreviewCyclePath(doc, objectId, GetActiveObjectPreviewDependencies(spec), out var cyclePath))
        {
            return false;
        }

        message = $"Circular modified-geometry reference detected: {FormatObjectPreviewCycle(doc, cyclePath)}.";
        return true;
    }

    private static bool TryGetObjectPreviewCyclePath(
        RhinoDoc doc,
        Guid targetObjectId,
        IEnumerable<Guid> sourceObjectIds,
        out IReadOnlyList<Guid> cyclePath)
    {
        cyclePath = Array.Empty<Guid>();
        foreach (var sourceObjectId in sourceObjectIds.Distinct())
        {
            if (sourceObjectId == targetObjectId)
            {
                cyclePath = new[] { targetObjectId, targetObjectId };
                return true;
            }

            var path = new List<Guid> { targetObjectId };
            if (TryReachTargetObject(doc, sourceObjectId, targetObjectId, new HashSet<Guid>(), path))
            {
                cyclePath = path;
                return true;
            }
        }

        return false;
    }

    private static bool TryReachTargetObject(
        RhinoDoc doc,
        Guid currentObjectId,
        Guid targetObjectId,
        HashSet<Guid> visited,
        List<Guid> path)
    {
        if (!visited.Add(currentObjectId))
        {
            return false;
        }

        path.Add(currentObjectId);
        if (currentObjectId == targetObjectId)
        {
            return true;
        }

        var currentObject = doc.Objects.FindId(currentObjectId);
        if (currentObject is not null)
        {
            var currentSpec = ModifierStackStorage.Load(currentObject);
            foreach (var dependencyObjectId in GetActiveObjectPreviewDependencies(currentSpec).Distinct())
            {
                if (TryReachTargetObject(doc, dependencyObjectId, targetObjectId, visited, path))
                {
                    return true;
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        return false;
    }

    private static string FormatObjectPreviewCycle(RhinoDoc doc, IReadOnlyList<Guid> cyclePath)
    {
        return string.Join(" -> ", cyclePath.Select(objectId => DescribeLinkedObject(doc.Objects.FindId(objectId), objectId)));
    }

    private static string DescribeLinkedObject(RhinoObject rhinoObject)
    {
        return DescribeLinkedObject(rhinoObject, rhinoObject.Id);
    }

    private static bool DoesObjectHaveModifierStack(RhinoDoc doc, Guid objectId)
    {
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            return false;
        }

        return ModifierStackStorage.Load(rhinoObject).Steps.Count > 0;
    }

    private static string DescribeLinkedObject(RhinoObject? rhinoObject, Guid objectId)
    {
        return rhinoObject is null
            ? objectId.ToString("D")
            : $"{rhinoObject.ObjectType} {rhinoObject.Id}";
    }

    private void InvalidateStackFromStep(RhinoDoc doc, Guid objectId, ModifierStackSpec spec, int fromStepIndex)
    {
        if (spec.Steps.Count == 0)
        {
            Log($"InvalidateStackFromStep removing empty stack. Object={objectId}");
            RemoveStackRuntime(doc, objectId);
            return;
        }

        UpdateObjectDependencies(doc, objectId, spec);
        var runtime = GetOrCreateStackRuntime(doc, objectId);
        runtime.EnsureStepCapacity(spec.Steps.Count);
        runtime.InvalidateFromStep(fromStepIndex);
        Log($"Stack invalidated from step {fromStepIndex}. Object={objectId}, StepCount={spec.Steps.Count}");
        QueueEvaluation(doc, objectId);
        RaiseStateChanged();
    }

    private void ResetStackRuntime(RhinoDoc doc, Guid objectId, ModifierStackSpec spec)
    {
        if (spec.Steps.Count == 0)
        {
            Log($"ResetStackRuntime removing empty stack. Object={objectId}");
            RemoveStackRuntime(doc, objectId);
            return;
        }

        UpdateObjectDependencies(doc, objectId, spec);
        var runtime = GetOrCreateStackRuntime(doc, objectId);
        runtime.Reset(spec.Steps.Count);
        runtime.RootRevision = NextRevision();
        Log($"Stack runtime reset. Object={objectId}, StepCount={spec.Steps.Count}, RootRevision={runtime.RootRevision}");
        QueueEvaluation(doc, objectId);
        RaiseStateChanged();
    }

    private void EnsureSavedStackRuntime(RhinoDoc doc, Guid objectId, ModifierStackSpec spec)
    {
        if (spec.Steps.Count == 0 || TryGetStackRuntime(doc, objectId) is not null)
        {
            return;
        }

        UpdateObjectDependencies(doc, objectId, spec);
        var runtime = GetOrCreateStackRuntime(doc, objectId);
        runtime.Reset(spec.Steps.Count);
        runtime.RootRevision = NextRevision();
        Log($"Saved stack runtime restored lazily. Object={objectId}, StepCount={spec.Steps.Count}, RootRevision={runtime.RootRevision}");
        QueueEvaluation(doc, objectId);
    }

    private void RestoreSavedStacks(RhinoDoc doc)
    {
        var restoredCount = 0;
        foreach (var rhinoObject in doc.Objects)
        {
            var spec = ModifierStackStorage.Load(rhinoObject);
            if (spec.Steps.Count == 0)
            {
                continue;
            }

            EnsureSavedStackRuntime(doc, rhinoObject.Id, spec);
            restoredCount += 1;
        }

        Log($"Saved stack restore scan complete. Doc={doc.RuntimeSerialNumber}, Restored={restoredCount}");
        RaiseStateChanged();
    }

    private StackRuntime GetOrCreateStackRuntime(RhinoDoc doc, Guid objectId)
    {
        var documentState = GetOrCreateDocumentState(doc);
        if (!documentState.Stacks.TryGetValue(objectId, out var runtime))
        {
            runtime = new StackRuntime
            {
                RootRevision = NextRevision(),
            };
            documentState.Stacks[objectId] = runtime;
            Log($"Stack runtime created. Doc={doc.RuntimeSerialNumber}, Object={objectId}, RootRevision={runtime.RootRevision}");
        }

        return runtime;
    }

    private StackRuntime? TryGetStackRuntime(RhinoDoc doc, Guid objectId)
    {
        return _documents.TryGetValue(doc.RuntimeSerialNumber, out var documentState) &&
               documentState.Stacks.TryGetValue(objectId, out var runtime)
            ? runtime
            : null;
    }

    private DocumentState GetOrCreateDocumentState(RhinoDoc doc)
    {
        if (!_documents.TryGetValue(doc.RuntimeSerialNumber, out var state))
        {
            state = new DocumentState();
            _documents[doc.RuntimeSerialNumber] = state;
            Log($"Document state created. Doc={doc.RuntimeSerialNumber}");
        }

        return state;
    }

    private void RemoveStackRuntime(RhinoDoc? doc, Guid objectId)
    {
        if (doc is null)
        {
            return;
        }

        DetachObjectDependencies(doc, objectId);

        if (_documents.TryGetValue(doc.RuntimeSerialNumber, out var documentState) &&
            documentState.Stacks.Remove(objectId, out var runtime))
        {
            Log($"Stack runtime removed. Doc={doc.RuntimeSerialNumber}, Object={objectId}");
            runtime.Dispose();
        }

        QueueDependentEvaluations(doc, objectId);
        UpdateConduitAndViews(doc);
        RaiseStateChanged();
    }

    private void QueueEvaluation(RhinoDoc doc, Guid objectId)
    {
        var documentState = GetOrCreateDocumentState(doc);
        documentState.DirtyObjects.Add(objectId);
        var key = $"{doc.RuntimeSerialNumber}:{objectId}";
        if (_queuedKeys.Add(key))
        {
            _queuedStacks.Enqueue(new QueuedStack(doc.RuntimeSerialNumber, objectId, key));
            Log($"Queued stack evaluation. Doc={doc.RuntimeSerialNumber}, Object={objectId}, QueueCount={_queuedStacks.Count}");
        }
        else
        {
            Log($"Skipped queueing duplicate stack evaluation. Doc={doc.RuntimeSerialNumber}, Object={objectId}");
        }

        if (_idleAttached)
        {
            return;
        }

        RhinoApp.Idle += OnIdle;
        _idleAttached = true;
        Log("Attached Rhino idle handler for queued stack processing.");
    }

    private void UpdateConduitAndViews(RhinoDoc doc)
    {
        UpdateConduitState();
        Log($"Requesting viewport redraw. Doc={doc.RuntimeSerialNumber}, PreviewObjectCount={GetPreviewStacks(doc).Count()}");
        doc.Views.Redraw();
    }

    private void UpdateConduitState()
    {
        var enabled = _documents.Values.Any(d => d.Stacks.Count > 0);
        if (_previewConduit.Enabled != enabled)
        {
            Log($"Preview conduit {(enabled ? "enabled" : "disabled")}.");
        }

        _previewConduit.Enabled = enabled;
    }

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool TryGetSingleSelectedObject(RhinoDoc doc, out RhinoObject? rhinoObject, out string message)
    {
        rhinoObject = null;
        message = string.Empty;

        var selected = doc.Objects.GetSelectedObjects(false, false).ToArray();
        if (selected is null || selected.Length == 0)
        {
            message = "Select one object to edit its modifier stack.";
            return false;
        }

        if (selected.Length > 1)
        {
            message = "Select a single object to edit.";
            return false;
        }

        rhinoObject = selected[0];
        if (rhinoObject is null)
        {
            message = "Selected object is unavailable.";
            return false;
        }

        if (!IsSupportedGeometryObject(rhinoObject))
        {
            message = $"Object type '{rhinoObject.ObjectType}' is not supported by the MVP.";
            return false;
        }

        return true;
    }

    private static bool IsSupportedGeometryObject(RhinoObject rhinoObject)
    {
        return rhinoObject.Geometry is Rhino.Geometry.Point or Curve or Brep or Extrusion or Mesh or SubD;
    }

    private static List<GeometryBase> CloneGeometry(IEnumerable<GeometryBase> geometry)
    {
        return geometry.Select(g => g.Duplicate()).ToList();
    }

    private ulong NextRevision()
    {
        _revisionCounter += 1;
        return _revisionCounter;
    }

    private static void Log(string message)
    {
        RhinoApp.WriteLine($"{LogPrefix}: {message}");
    }

    private static string DescribeRhinoObject(RhinoObject rhinoObject)
    {
        return $"{rhinoObject.ObjectType} {rhinoObject.Id}";
    }

    private static string DescribeGeometry(IEnumerable<GeometryBase> geometry)
    {
        var items = geometry.ToList();
        if (items.Count == 0)
        {
            return "none";
        }

        return string.Join(", ", items
            .GroupBy(item => item.ObjectType)
            .Select(group => $"{group.Key} x{group.Count()}"));
    }

}
