using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoModifiers.Models;
using RhinoModifiers.Runtime.Modifiers;
using RhinoModifiers.UI;

namespace RhinoModifiers.Runtime;

internal sealed class ModifierEngine : IDisposable
{
    private const string LogPrefix = "Modifiers";

    private readonly Dictionary<uint, DocumentState> _documents = new();
    private readonly Queue<QueuedStack> _queuedStacks = new();
    private readonly HashSet<string> _queuedKeys = new(StringComparer.Ordinal);
    private readonly StackPreviewConduit _previewConduit;
    private readonly RuntimeExecutor _executor;
    private ulong _revisionCounter = 1;
    private bool _disposed;
    private bool _idleAttached;

    public ModifierEngine()
    {
        _executor = new RuntimeExecutor(TryGetStackRuntime);
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
            return new ModifierPanelState { StatusMessage = "No active Rhino document." };
        }

        if (!TryGetSingleSelectedObject(doc, out var rhinoObject, out var statusMessage))
        {
            return new ModifierPanelState { StatusMessage = statusMessage };
        }

        if (rhinoObject is null)
        {
            return new ModifierPanelState { StatusMessage = "Selected object is unavailable." };
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        EnsureSavedStackRuntime(doc, rhinoObject.Id, spec);
        var runtime = TryGetStackRuntime(doc, rhinoObject.Id);
        var stack = new ModifierStack(spec);
        var flat = stack.Flatten();
        var stepContexts = new List<PanelStepContext>(flat.Count);
        for (var i = 0; i < flat.Count; i++)
        {
            var step = flat[i];
            var displayName = string.Empty;
            var kind = ModifierKind.Grasshopper;
            DefinitionContract? contract = null;
            var contractError = string.Empty;
            if (step is GrasshopperModifierSpec grasshopper)
            {
                if (string.IsNullOrWhiteSpace(grasshopper.Path))
                {
                    displayName = "No script selected";
                }
                else
                {
                    displayName = Path.GetFileName(grasshopper.Path);
                    if (
                        !_executor.TryGetDefinitionContract(
                            doc,
                            step,
                            out contract,
                            out contractError
                        )
                    )
                    {
                        contract = null;
                    }
                }
            }
            else if (step is NativeModifierSpec nativeSpec)
            {
                kind = ModifierKind.Native;
                displayName = ModifierDisplayNames.GetStepLabel(step);
                if (!_executor.TryGetDefinitionContract(doc, step, out contract, out contractError))
                {
                    contract = null;
                }
            }
            else
            {
                contractError = "This modifier kind is not supported yet.";
            }

            stepContexts.Add(
                new PanelStepContext(i, step, kind, displayName, contract, contractError)
            );
        }

        var steps = new List<ModifierStepPanelState>();
        var rowIndex = 0;
        var leafIndex = 0;
        BuildPanelRows(
            doc,
            rhinoObject.Id,
            stepContexts,
            stack.Spec.Nodes,
            0,
            Guid.Empty,
            runtime,
            ref rowIndex,
            ref leafIndex,
            steps
        );

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

    private void BuildPanelRows(
        RhinoDoc doc,
        Guid objectId,
        IReadOnlyList<PanelStepContext> stepContexts,
        List<ModifierNodeSpec> nodes,
        int depth,
        Guid parentNodeId,
        StackRuntime? runtime,
        ref int rowIndex,
        ref int leafIndex,
        List<ModifierStepPanelState> rows
    )
    {
        foreach (var node in nodes)
        {
            if (node is ModifierGroupSpec group)
            {
                var groupName = string.IsNullOrWhiteSpace(group.Name) ? "Group" : group.Name;
                rows.Add(
                    new ModifierStepPanelState
                    {
                        Index = rowIndex++,
                        StepId = group.NodeId,
                        IsGroup = true,
                        Depth = depth,
                        ParentNodeId = parentNodeId,
                        Kind = ModifierKind.Group,
                        Name = group.Name,
                        DisplayName = groupName,
                        Enabled = true,
                    }
                );

                BuildPanelRows(
                    doc,
                    objectId,
                    stepContexts,
                    group.Children,
                    depth + 1,
                    group.NodeId,
                    runtime,
                    ref rowIndex,
                    ref leafIndex,
                    rows
                );
                continue;
            }

            if (node is not ModifierSpec modifier)
            {
                continue;
            }

            var stepContext = stepContexts[leafIndex];
            leafIndex += 1;
            var stepError = runtime?.GetErrorForIndex(stepContext.Index) ?? string.Empty;
            var inputs = Array.Empty<ModifierStepInputPanelState>();
            var outputs = Array.Empty<ModifierStepOutputPanelState>();

            if (stepContext.Contract is not null)
            {
                inputs = BuildInputPanelState(doc, objectId, stepContexts, stepContext, runtime)
                    .ToArray();
                outputs = BuildOutputPanelState(
                        runtime?.GetOutputsForIndex(stepContext.Index),
                        stepContext.Contract
                    )
                    .ToArray();

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

            rows.Add(
                new ModifierStepPanelState
                {
                    Index = rowIndex++,
                    LeafIndex = stepContext.Index,
                    StepId = stepContext.Step.NodeId,
                    Enabled = stepContext.Step.Enabled,
                    FullPath = stepContext.Step is GrasshopperModifierSpec stepGh
                        ? stepGh.Path
                        : string.Empty,
                    DisplayName = stepContext.DisplayName,
                    ErrorMessage = stepError,
                    Inputs = inputs,
                    Outputs = outputs,
                    Depth = depth,
                    ParentNodeId = parentNodeId,
                    Kind = stepContext.Kind,
                }
            );
        }
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
        var stack = new ModifierStack(spec);
        if (
            !stack.TryAddNode(
                new GrasshopperModifierSpec { Enabled = true, Path = Path.GetFullPath(path) },
                null,
                null,
                out var addError
            )
        )
        {
            message = addError;
            Log(message);
            return false;
        }

        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to store the modifier stack on the Rhino object.";
            Log(message);
            return false;
        }

        var stepCount = stack.Flatten().Count;
        InvalidateStackFromStep(doc, objectId, spec, stepCount - 1);
        message = $"Added modifier: {Path.GetFileName(path)}";
        Log($"{message} StackCount={stepCount}");
        return true;
    }

    /// <summary>
    /// Adds any modifier node (modifier or group) to the stack, optionally inside the
    /// named parent group, and returns the new node's id.
    /// </summary>
    public bool AddNode(
        RhinoDoc doc,
        Guid objectId,
        ModifierNodeSpec node,
        Guid? parentNodeId,
        out Guid addedNodeId,
        out string message
    )
    {
        addedNodeId = Guid.Empty;
        message = string.Empty;
        Log(
            $"AddNode requested. Object={objectId}, Kind={node.GetType().Name}, Parent={parentNodeId}"
        );

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
        var stack = new ModifierStack(spec);
        if (!stack.TryAddNode(node, parentNodeId, null, out var addError))
        {
            message = addError;
            Log(message);
            return false;
        }

        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to store the modifier stack on the Rhino object.";
            Log(message);
            return false;
        }

        addedNodeId = node.NodeId;
        var start = stack.GetLeafRangeStart(node.NodeId, out _);
        InvalidateStackFromStep(doc, objectId, spec, start);
        message = node is ModifierGroupSpec ? "Added modifier group." : "Added modifier.";
        Log($"{message} Object={objectId}, Node={node.NodeId}");
        return true;
    }

    /// <summary>
    /// Sets (or replaces) the Grasshopper definition backing a Grasshopper modifier.
    /// The stack is re-evaluated so the panel builds out the new script's inputs/outputs.
    /// </summary>
    public bool SetModifierPath(
        RhinoDoc doc,
        Guid objectId,
        Guid nodeId,
        string path,
        out string message
    )
    {
        message = string.Empty;
        Log($"SetModifierPath requested. Object={objectId}, Node={nodeId}, Path={path}");

        if (string.IsNullOrWhiteSpace(path))
        {
            message = "Modifier path is empty.";
            Log(message);
            return false;
        }

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

        var spec = ModifierStackStorage.Load(rhinoObject);
        var stack = new ModifierStack(spec);
        if (stack.Find(nodeId) is not GrasshopperModifierSpec grasshopper)
        {
            message = "The selected modifier cannot host a Grasshopper definition.";
            Log(message);
            return false;
        }

        grasshopper.Path = Path.GetFullPath(path);
        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to update the modifier.";
            Log(message);
            return false;
        }

        var start = stack.GetLeafRangeStart(nodeId, out _);
        InvalidateStackFromStep(doc, objectId, spec, start);
        message = $"Modifier script set to {Path.GetFileName(path)}";
        Log(message);
        return true;
    }

    /// <summary>
    /// Renames any node (group or modifier) in the stack.
    /// </summary>
    public bool RenameNode(
        RhinoDoc doc,
        Guid objectId,
        Guid nodeId,
        string name,
        out string message
    )
    {
        message = string.Empty;
        Log($"RenameNode requested. Object={objectId}, Node={nodeId}, Name='{name}'");

        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        var stack = new ModifierStack(spec);
        if (stack.Find(nodeId) is not { } node)
        {
            message = "Modifier node no longer exists.";
            Log(message);
            return false;
        }

        node.Name = name?.Trim() ?? string.Empty;
        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to update the modifier stack.";
            Log(message);
            return false;
        }

        RaiseStateChanged();
        message = "Renamed modifier.";
        Log(message);
        return true;
    }

    public static bool OpenModifierDefinitionInGrasshopper(string path, out string message)
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

        if (
            RhinoApp.GetPlugInObject("Grasshopper")
            is not Grasshopper.Plugin.GH_RhinoScriptInterface grasshopper
        )
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

            message =
                $"Opened modifier in Grasshopper: {Path.GetFileName(fullPath)}. Save the Grasshopper file, then refresh the stack to apply changes.";
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
        var stack = new ModifierStack(spec);
        var node = GetNodeAtRowIndex(stack, index);
        if (node is null)
        {
            message = "Step index is out of range.";
            Log(message);
            return false;
        }

        var start = stack.GetLeafRangeStart(node.NodeId, out var leafCount);
        var runtime = TryGetStackRuntime(doc, objectId);
        if (runtime is not null && leafCount > 0 && start + leafCount <= runtime.StepRuntimes.Count)
        {
            for (var i = 0; i < leafCount; i++)
            {
                runtime.DisposeStep(start + i);
            }

            runtime.StepRuntimes.RemoveRange(start, leafCount);
        }

        if (!stack.TryRemoveNode(node.NodeId, out _))
        {
            message = "Modifier node no longer exists.";
            Log(message);
            return false;
        }

        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to update the modifier stack.";
            Log(message);
            return false;
        }

        InvalidateStackFromStep(doc, objectId, spec, start);
        message = "Removed modifier step.";
        Log($"{message} StackCount={stack.Flatten().Count}");
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

        var spec = ModifierStackStorage.Load(rhinoObject);
        var stack = new ModifierStack(spec);
        var node = GetNodeAtRowIndex(stack, index);
        if (node is null)
        {
            message = "Cannot move the modifier step further in that direction.";
            Log(message);
            return false;
        }

        var oldStart = stack.GetLeafRangeStart(node.NodeId, out var oldCount);
        if (!stack.TryMoveRelative(node.NodeId, offset, out var moveError))
        {
            message = moveError;
            Log(message);
            return false;
        }

        var newStart = stack.GetLeafRangeStart(node.NodeId, out var newCount);
        var runtime = TryGetStackRuntime(doc, objectId);
        if (
            runtime is not null
            && oldCount > 0
            && oldStart + oldCount <= runtime.StepRuntimes.Count
        )
        {
            var block = runtime.StepRuntimes.GetRange(oldStart, oldCount);
            runtime.StepRuntimes.RemoveRange(oldStart, oldCount);
            runtime.StepRuntimes.InsertRange(Math.Min(newStart, runtime.StepRuntimes.Count), block);
        }

        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to update the modifier stack.";
            Log(message);
            return false;
        }

        InvalidateStackFromStep(doc, objectId, spec, Math.Min(oldStart, newStart));
        message = "Moved modifier step.";
        Log($"{message} NewIndex={newStart}");
        return true;
    }

    public bool SetStepEnabled(
        RhinoDoc doc,
        Guid objectId,
        int index,
        bool enabled,
        out string message
    )
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
        var stack = new ModifierStack(spec);
        var modifier = GetLeafAtRowIndex(stack, index, out var resolveError);
        if (modifier is null)
        {
            message = resolveError;
            Log(message);
            return false;
        }

        var leafIndex = stack.GetLeafIndex(modifier.NodeId);
        modifier.Enabled = enabled;
        if (
            enabled
            && !ModifierEngine.TryValidateObjectPreviewGraph(doc, objectId, spec, out message)
        )
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
            runtime.DisposeStep(leafIndex);
            runtime.ClearOutputs(leafIndex);
        }

        InvalidateStackFromStep(doc, objectId, spec, leafIndex);
        message = enabled ? "Modifier enabled." : "Modifier disabled.";
        Log(message);
        return true;
    }

    public bool SetStepInputValue(
        RhinoDoc doc,
        Guid objectId,
        int index,
        string inputId,
        string serializedValue,
        out string message
    )
    {
        message = string.Empty;
        Log(
            $"SetStepInputValue requested. Object={objectId}, Index={index}, Input={inputId}, Value='{serializedValue}'"
        );
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        var stack = new ModifierStack(spec);
        var modifier = GetLeafAtRowIndex(stack, index, out var resolveError);
        if (modifier is null)
        {
            message = resolveError;
            Log(message);
            return false;
        }

        var leafIndex = stack.GetLeafIndex(modifier.NodeId);
        modifier.InputValues[inputId] = serializedValue ?? string.Empty;
        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to update the modifier input value.";
            Log(message);
            return false;
        }

        InvalidateStackFromStep(doc, objectId, spec, leafIndex);
        message = "Updated modifier input.";
        Log(message);
        return true;
    }

    public bool SetStepInputLink(
        RhinoDoc doc,
        Guid objectId,
        int index,
        string inputId,
        Guid sourceNodeId,
        string sourceOutputId,
        out string message
    )
    {
        message = string.Empty;
        Log(
            $"SetStepInputLink requested. Object={objectId}, Index={index}, Input={inputId}, SourceNode={sourceNodeId}, SourceOutput={sourceOutputId}"
        );
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        var stack = new ModifierStack(spec);
        var modifier = GetLeafAtRowIndex(stack, index, out var resolveError);
        if (modifier is null)
        {
            message = resolveError;
            Log(message);
            return false;
        }

        var leafIndex = stack.GetLeafIndex(modifier.NodeId);
        if (
            !TryValidateInputLink(
                doc,
                spec,
                leafIndex,
                inputId,
                sourceNodeId,
                sourceOutputId,
                out var linkSpec,
                out message
            )
        )
        {
            Log(message);
            return false;
        }

        modifier.InputLinks[inputId] = linkSpec;
        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to update the modifier input link.";
            Log(message);
            return false;
        }

        InvalidateStackFromStep(doc, objectId, spec, leafIndex);
        message = "Updated modifier input link.";
        Log(message);
        return true;
    }

    public bool SetStepInputObjectPreviewLink(
        RhinoDoc doc,
        Guid objectId,
        int index,
        string inputId,
        Guid sourceObjectId,
        out string message
    )
    {
        message = string.Empty;
        Log(
            $"SetStepInputObjectPreviewLink requested. Object={objectId}, Index={index}, Input={inputId}, SourceObject={sourceObjectId}"
        );
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject is null)
        {
            message = "Selected object no longer exists.";
            Log(message);
            return false;
        }

        var spec = ModifierStackStorage.Load(rhinoObject);
        var stack = new ModifierStack(spec);
        var modifier = GetLeafAtRowIndex(stack, index, out var resolveError);
        if (modifier is null)
        {
            message = resolveError;
            Log(message);
            return false;
        }

        var leafIndex = stack.GetLeafIndex(modifier.NodeId);
        if (
            !TryValidateObjectPreviewInputLink(
                doc,
                objectId,
                spec,
                leafIndex,
                inputId,
                sourceObjectId,
                out var linkSpec,
                out message
            )
        )
        {
            Log(message);
            return false;
        }

        modifier.InputLinks[inputId] = linkSpec;
        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to update the modifier input link.";
            Log(message);
            return false;
        }

        InvalidateStackFromStep(doc, objectId, spec, leafIndex);
        message = "Updated modifier input link.";
        Log(message);
        return true;
    }

    public bool ClearStepInputLink(
        RhinoDoc doc,
        Guid objectId,
        int index,
        string inputId,
        out string message
    )
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
        var stack = new ModifierStack(spec);
        var modifier = GetLeafAtRowIndex(stack, index, out var resolveError);
        if (modifier is null)
        {
            message = resolveError;
            Log(message);
            return false;
        }

        var leafIndex = stack.GetLeafIndex(modifier.NodeId);
        modifier.InputLinks.Remove(inputId);
        if (!ModifierStackStorage.Save(doc, objectId, spec))
        {
            message = "Failed to clear the modifier input link.";
            Log(message);
            return false;
        }

        InvalidateStackFromStep(doc, objectId, spec, leafIndex);
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
        var stepCount = new ModifierStack(spec).Flatten().Count;
        if (stepCount == 0)
        {
            message = "Selected object does not have any modifier steps.";
            Log(message);
            return false;
        }

        ResetStackRuntime(doc, rhinoObject!.Id, spec);
        message = "Queued stack refresh.";
        Log($"{message} Object={rhinoObject.Id}, StepCount={stepCount}");
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
        var stack = new ModifierStack(spec);
        var node = GetNodeAtRowIndex(stack, stepIndex);
        if (node is null)
        {
            message = "Step index is out of range.";
            Log(message);
            return false;
        }

        // Applying through a group applies through its last modifier; applying through
        // a modifier applies through that modifier.
        var leafIndex = node is ModifierSpec leafModifier
            ? stack.GetLeafIndex(leafModifier.NodeId)
            : stack.GetLeafRangeStart(node.NodeId, out var leafCount) + leafCount - 1;
        if (leafIndex < 0)
        {
            message = "Step index is out of range.";
            Log(message);
            return false;
        }

        if (
            !TryEvaluateStackThroughStep(
                doc,
                objectId,
                rhinoObject,
                stack.Flatten(),
                leafIndex,
                out var evaluatedGeometry,
                out var evaluationError
            )
        )
        {
            message = evaluationError;
            Log(
                $"ApplyThroughStep evaluation failed. Object={objectId}, StepIndex={stepIndex}, Error={evaluationError}"
            );
            return false;
        }

        if (evaluatedGeometry.Count == 0)
        {
            message =
                "The selected modifier output is empty; apply was cancelled to avoid deleting geometry.";
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

        var newObjectAttributes = rhinoObject.Attributes.Duplicate();
        newObjectAttributes.UserDictionary.Remove(ModifierStackSpec.UserDictionaryKey);

        var undoRecord = doc.BeginUndoRecord("Apply Modifier Stack Through Step");

        try
        {
            if (
                !ReplaceManagedObjectGeometry(
                    doc,
                    objectId,
                    evaluatedGeometry[0],
                    out var replaceError
                )
            )
            {
                message = replaceError;
                Log(
                    $"ApplyThroughStep failed replacing object geometry. Object={objectId}, Error={replaceError}"
                );
                return false;
            }

            for (var i = 1; i < evaluatedGeometry.Count; i++)
            {
                if (
                    !TryAddGeometryObject(
                        doc,
                        evaluatedGeometry[i],
                        newObjectAttributes,
                        out Guid addedId,
                        out var addError
                    )
                )
                {
                    message =
                        $"Applied base object, but failed to add additional geometry item {i + 1}: {addError}";
                    Log(
                        $"ApplyThroughStep partially succeeded. Object={objectId}, Index={i}, Error={addError}"
                    );
                    return false;
                }
            }

            for (var i = 0; i <= leafIndex; i++)
            {
                stack.TryRemoveNode(stack.Flatten()[0].NodeId, out _);
            }

            if (!ModifierStackStorage.Save(doc, objectId, spec))
            {
                message = "Applied geometry, but failed to update remaining modifier stack.";
                Log(
                    $"ApplyThroughStep failed updating stack metadata after apply. Object={objectId}"
                );
                return false;
            }

            ResetStackRuntime(doc, objectId, spec);
        }
        finally
        {
            if (undoRecord != 0)
            {
                doc.EndUndoRecord(undoRecord);
            }
        }

        message =
            leafIndex == 0
                ? "Applied modifier and removed it from the stack."
                : $"Applied {leafIndex + 1} modifiers and removed them from the stack.";

        Log(
            $"ApplyThroughStep completed. Object={objectId}, AppliedCount={leafIndex + 1}, RemainingSteps={stack.Flatten().Count}"
        );

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
        var flat = new ModifierStack(spec).Flatten();
        if (flat.Count == 0)
        {
            message = "Selected object does not have any modifier steps.";
            Log(message);
            return false;
        }

        if (
            !TryEvaluateStackThroughStep(
                doc,
                objectId,
                rhinoObject,
                flat,
                flat.Count - 1,
                out var evaluatedGeometry,
                out var evaluationError
            )
        )
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
                message = unsupportedError.Replace(
                    "apply operation",
                    "bake operation",
                    StringComparison.Ordinal
                );
                Log($"BakeFinalResult geometry validation failed. {message}");
                return false;
            }
        }

        var undoRecord = doc.BeginUndoRecord("Bake Modifier Stack Result");
        var addedObjectIds = new List<Guid>();

        try
        {
            var newObjectAttributes = rhinoObject.Attributes.Duplicate();
            newObjectAttributes.UserDictionary.Remove(ModifierStackSpec.UserDictionaryKey);

            for (var i = 0; i < evaluatedGeometry.Count; i++)
            {
                if (
                    !TryAddGeometryObject(
                        doc,
                        evaluatedGeometry[i],
                        newObjectAttributes,
                        out Guid addedId,
                        out var addError
                    )
                )
                {
                    foreach (var id in addedObjectIds)
                    {
                        doc.Objects.Delete(id, true);
                    }

                    message = $"Failed to bake geometry item {i + 1}: {addError}";
                    Log(
                        $"BakeFinalResult add failed. Object={objectId}, Index={i}, Error={addError}"
                    );
                    return false;
                }

                addedObjectIds.Add(addedId);
            }
        }
        finally
        {
            if (undoRecord != 0)
            {
                doc.EndUndoRecord(undoRecord);
            }
        }

        message =
            evaluatedGeometry.Count == 1
                ? "Baked final stack result as a new object."
                : $"Baked final stack result as {evaluatedGeometry.Count} new objects.";
        Log($"BakeFinalResult completed. Object={objectId}, BakedCount={evaluatedGeometry.Count}");
        return true;
    }

    /// <summary>
    /// Embeds all Grasshopper definition files referenced by modifier stacks in the
    /// current document so the 3dm can be shared without external .gh / .ghx files.
    /// </summary>
    public bool EmbedDefinitions(RhinoDoc doc, out string message)
    {
        message = string.Empty;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rhinoObject in doc.Objects)
        {
            var spec = ModifierStackStorage.Load(rhinoObject);
            foreach (var modifier in new ModifierStack(spec).Flatten())
            {
                if (
                    modifier is GrasshopperModifierSpec grasshopper
                    && !string.IsNullOrWhiteSpace(grasshopper.Path)
                )
                {
                    paths.Add(grasshopper.Path);
                }
            }
        }

        if (paths.Count == 0)
        {
            message = "No modifier definitions to embed.";
            return false;
        }

        var (added, updated, unchanged) = EmbeddedDefinitionStorage.EmbedDefinitions(doc, paths);

        if (added == 0 && updated == 0)
        {
            message = $"All {unchanged} definition(s) are already up to date.";
        }
        else
        {
            var parts = new List<string>();
            if (added > 0)
                parts.Add($"{added} new");
            if (updated > 0)
                parts.Add($"{updated} updated");
            if (unchanged > 0)
                parts.Add($"{unchanged} unchanged");
            message = $"Embedded definitions: {string.Join(", ", parts)}.";
        }

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

        _documents.Clear();
        _queuedStacks.Clear();
        _queuedKeys.Clear();
        _previewConduit.Enabled = false;
        Log("ModifierEngine disposed.");
    }

    private void OnReplaceRhinoObject(object? sender, RhinoReplaceObjectEventArgs e)
    {
        var spec = ModifierStackStorage.Load(e.NewRhinoObject);
        var stepCount = new ModifierStack(spec).Flatten().Count;
        if (stepCount == 0)
        {
            return;
        }

        Log($"Rhino object replaced. Object={e.ObjectId}, Steps={stepCount}");
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
        var doc =
            e.TheObject?.Document
            ?? RhinoDoc.FromRuntimeSerialNumber(e.TheObject?.Document.RuntimeSerialNumber ?? 0);
        if (doc is null)
        {
            return;
        }

        var rhinoObject = doc.Objects.FindId(e.ObjectId);
        var spec = ModifierStackStorage.Load(rhinoObject);
        var stepCount = new ModifierStack(spec).Flatten().Count;
        if (stepCount == 0)
        {
            return;
        }

        Log($"Rhino object undeleted. Object={e.ObjectId}, Steps={stepCount}");
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
        Log(
            $"Selection changed. AffectedCount={count}, TotalSelected={e.Document.Objects.GetSelectedObjects(false, false).Count()}"
        );
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
            Log(
                $"Dequeued stack evaluation. Doc={queued.DocumentSerial}, Object={queued.ObjectId}, Remaining={_queuedStacks.Count}"
            );

            var doc = RhinoDoc.FromRuntimeSerialNumber(queued.DocumentSerial);
            if (doc is null)
            {
                Log(
                    $"Skipped queued stack because document {queued.DocumentSerial} is unavailable."
                );
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
        var flat = new ModifierStack(spec).Flatten();
        if (flat.Count == 0)
        {
            Log($"EvaluateStack aborted. Object={objectId} has no modifier steps.");
            RemoveStackRuntime(doc, objectId);
            return;
        }

        Log(
            $"Stack spec loaded. Object={DescribeRhinoObject(rhinoObject)}, StepCount={flat.Count}"
        );

        var runtime = GetOrCreateStackRuntime(doc, objectId);
        runtime.EnsureStepCapacity(flat.Count);
        runtime.ClearErrors(flat.Count);
        runtime.ClearAllOutputs(flat.Count);

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

        if (
            !GeometryConversion.TryGetSourceGeometry(
                rhinoObject.Geometry,
                out var currentGeometry,
                out var sourceError
            )
        )
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

        Log(
            $"Source geometry ready. Count={currentGeometry.Count}. {DescribeGeometry(currentGeometry)}"
        );

        _executor.EvaluateStack(doc, objectId, currentGeometry, flat, runtime);
        MarkObjectClean(doc, objectId);

        if (runtime.PreviewGeometry.Count == 0 && string.IsNullOrWhiteSpace(runtime.ErrorMessage))
        {
            Log($"Stack on {objectId} evaluated but produced no preview geometry.");
        }

        Log(
            $"EvaluateStack finished. Object={objectId}, PreviewCount={runtime.PreviewGeometry.Count}, PreviewSummary={DescribeGeometry(runtime.PreviewGeometry)}, Error='{runtime.ErrorMessage}'"
        );
        QueueDependentEvaluations(doc, objectId);
        UpdateConduitAndViews(doc);
        RaiseStateChanged();
    }

    private IEnumerable<ModifierStepInputPanelState> BuildInputPanelState(
        RhinoDoc doc,
        Guid objectId,
        IReadOnlyList<PanelStepContext> stepContexts,
        PanelStepContext currentStepContext,
        StackRuntime? runtime
    )
    {
        var contract = currentStepContext.Contract!;
        foreach (var input in contract.Inputs)
        {
            var serializedValue = GetDisplayedInputValue(currentStepContext.Step, input);
            var isMissingRequiredValue = IsMissingRequiredInput(currentStepContext.Step, input);
            var linkState = BuildLinkPresentationState(
                doc,
                objectId,
                stepContexts,
                currentStepContext,
                input,
                runtime
            );
            var (showModifiedGeometryToggle, useModifiedGeometry, modifiedGeometrySourceObjectId) =
                GetModifiedGeometryToggleState(doc, currentStepContext.Step, input);

            yield return new ModifierStepInputPanelState
            {
                Id = input.Id,
                Label = input.Label,
                Description = input.Kind switch
                {
                    ModifierIoKind.Geometry => AppendDescription(
                        input.Description,
                        "Blank uses the current stack geometry. Paste Rhino object IDs or `self` to override. When a single referenced object has its own modifiers, a checkbox lets you use its modified result instead of the base geometry."
                    ),
                    ModifierIoKind.Point => AppendDescription(
                        input.Description,
                        "Click Set point to use the selected point object or pick one in Rhino."
                    ),
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
                AvailableLinks = BuildAvailableLinkOptions(
                        stepContexts,
                        currentStepContext,
                        input,
                        runtime
                    )
                    .ToArray(),
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

    private static IEnumerable<ModifierStepOutputPanelState> BuildOutputPanelState(
        IReadOnlyList<StepOutputValue>? runtimeOutputs,
        DefinitionContract contract
    )
    {
        var displayById =
            runtimeOutputs?.ToDictionary(
                output => output.Id,
                output => output.DisplayValue,
                StringComparer.Ordinal
            ) ?? new Dictionary<string, string>(StringComparer.Ordinal);

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

    private static (
        bool ShowToggle,
        bool UseModifiedGeometry,
        Guid? SourceObjectId
    ) GetModifiedGeometryToggleState(
        RhinoDoc doc,
        ModifierSpec stepSpec,
        ModifierInputDescriptor input
    )
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

        if (
            !TryGetExplicitInputValue(stepSpec, input, out var serializedValue)
            || !TryGetSingleReferencedObjectId(serializedValue, out var sourceObjectId)
        )
        {
            return (false, false, null);
        }

        return DoesObjectHaveModifierStack(doc, sourceObjectId)
            ? (true, false, sourceObjectId)
            : (false, false, null);
    }

    /// <summary>
    /// Resolves a panel row index (position in the pre-order list of all nodes,
    /// including groups) to the underlying node.
    /// </summary>
    private static ModifierNodeSpec? GetNodeAtRowIndex(ModifierStack stack, int rowIndex)
    {
        var nodes = stack.AllNodesInPreOrder();
        return rowIndex >= 0 && rowIndex < nodes.Count ? nodes[rowIndex] : null;
    }

    /// <summary>
    /// Resolves a panel row index to a leaf modifier, rejecting group rows.
    /// </summary>
    private static ModifierSpec? GetLeafAtRowIndex(
        ModifierStack stack,
        int rowIndex,
        out string error
    )
    {
        error = string.Empty;
        var node = GetNodeAtRowIndex(stack, rowIndex);
        if (node is ModifierSpec modifier)
        {
            return modifier;
        }

        error = node is null
            ? "Step index is out of range."
            : "This action requires selecting a single modifier, not a group.";
        return null;
    }

    private bool TryValidateInputLink(
        RhinoDoc doc,
        ModifierStackSpec spec,
        int targetIndex,
        string inputId,
        Guid sourceNodeId,
        string sourceOutputId,
        out ModifierInputLinkSpec linkSpec,
        out string message
    )
    {
        linkSpec = null!;
        message = string.Empty;
        var steps = new ModifierStack(spec).Flatten();
        if (targetIndex < 0 || targetIndex >= steps.Count)
        {
            message = "Step index is out of range.";
            return false;
        }

        var sourceIndex = -1;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].NodeId == sourceNodeId)
            {
                sourceIndex = i;
                break;
            }
        }
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

        var targetStep = steps[targetIndex];
        if (
            !_executor.TryGetDefinitionContract(
                doc,
                targetStep,
                out var targetContract,
                out var targetError
            )
        )
        {
            message = targetError;
            return false;
        }

        var targetInput = targetContract.Inputs.FirstOrDefault(input =>
            input.Id.Equals(inputId, StringComparison.Ordinal)
        );
        if (targetInput is null)
        {
            message = "The selected input is no longer available on this modifier.";
            return false;
        }

        var sourceStep = steps[sourceIndex];
        if (!sourceStep.Enabled)
        {
            message = "The linked source modifier is disabled.";
            return false;
        }

        if (
            !_executor.TryGetDefinitionContract(
                doc,
                sourceStep,
                out var sourceContract,
                out var sourceError
            )
        )
        {
            message = sourceError;
            return false;
        }

        var sourceOutput = sourceContract.Outputs.FirstOrDefault(output =>
            output.Id.Equals(sourceOutputId, StringComparison.Ordinal)
        );
        if (sourceOutput is null)
        {
            message = "The selected output is no longer available on the source modifier.";
            return false;
        }

        if (!AreKindsLinkCompatible(targetInput.Kind, sourceOutput.Kind))
        {
            message =
                $"Output '{sourceOutput.Label}' is not compatible with input '{targetInput.Label}'.";
            return false;
        }

        linkSpec = new ModifierInputLinkSpec
        {
            SourceNodeId = sourceNodeId,
            SourceOutputId = sourceOutputId,
            SourceStepLabel = ModifierDisplayNames.GetStepLabel(sourceStep),
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
        out string message
    )
    {
        linkSpec = null!;
        message = string.Empty;
        var steps = new ModifierStack(spec).Flatten();
        if (targetIndex < 0 || targetIndex >= steps.Count)
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

        var targetStep = steps[targetIndex];
        if (
            !_executor.TryGetDefinitionContract(
                doc,
                targetStep,
                out var targetContract,
                out var targetError
            )
        )
        {
            message = targetError;
            return false;
        }

        var targetInput = targetContract.Inputs.FirstOrDefault(input =>
            input.Id.Equals(inputId, StringComparison.Ordinal)
        );
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
        if (new ModifierStack(sourceSpec).Flatten().Count == 0)
        {
            message = "The selected source object does not have any modifiers.";
            return false;
        }

        var candidateSpec = spec.Clone();
        new ModifierStack(candidateSpec).Flatten()[targetIndex].InputLinks[inputId] =
            new ModifierInputLinkSpec
            {
                SourceKind = ModifierInputLinkSourceKind.ObjectPreview,
                SourceObjectId = sourceObjectId,
                SourceObjectLabel = DescribeLinkedObject(sourceRhinoObject),
            };

        if (
            !ModifierEngine.TryValidateObjectPreviewGraph(
                doc,
                targetObjectId,
                candidateSpec,
                out message
            )
        )
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

    private static bool TryValidateObjectPreviewGraph(
        RhinoDoc doc,
        Guid objectId,
        ModifierStackSpec spec,
        out string message
    )
    {
        message = string.Empty;
        if (
            !TryGetObjectPreviewCyclePath(
                doc,
                objectId,
                GetActiveObjectPreviewDependencies(spec),
                out var cyclePath
            )
        )
        {
            return true;
        }

        message =
            $"Circular modified-geometry reference detected: {FormatObjectPreviewCycle(doc, cyclePath)}.";
        return false;
    }

    private static IEnumerable<ModifierInputLinkOptionPanelState> BuildAvailableLinkOptions(
        IReadOnlyList<PanelStepContext> stepContexts,
        PanelStepContext currentStepContext,
        ModifierInputDescriptor input,
        StackRuntime? runtime
    )
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

                var hasRuntimeValue = TryGetStepOutputValue(
                    runtimeOutputs,
                    output.Id,
                    out var runtimeOutput
                );
                yield return new ModifierInputLinkOptionPanelState
                {
                    SourceStepId = sourceStepContext.Step.NodeId,
                    SourceStepIndex = sourceStepContext.Index,
                    SourceStepLabel = sourceStepContext.DisplayName,
                    SourceOutputId = output.Id,
                    SourceOutputLabel = output.Label,
                    Kind = output.Kind,
                    HasRuntimeValue = hasRuntimeValue,
                    RuntimeDisplayValue = hasRuntimeValue
                        ? runtimeOutput.DisplayValue
                        : string.Empty,
                    IsSelected =
                        TryGetStepOutputInputLink(
                            currentStepContext.Step,
                            input.Id,
                            out var activeLink
                        )
                        && activeLink.SourceNodeId == sourceStepContext.Step.NodeId
                        && activeLink.SourceOutputId.Equals(output.Id, StringComparison.Ordinal),
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
        StackRuntime? runtime
    )
    {
        if (!TryGetInputLink(currentStepContext.Step, input.Id, out var activeLink))
        {
            return LinkPresentationState.None;
        }

        if (activeLink.SourceKind == ModifierInputLinkSourceKind.ObjectPreview)
        {
            return BuildObjectPreviewLinkPresentationState(
                doc,
                objectId,
                stepContexts.Select(context => context.Step),
                activeLink
            );
        }

        var sourceStepLabel = GetStoredStepLabel(activeLink);
        var sourceOutputLabel = GetStoredOutputLabel(activeLink);
        var sourceStepContext = stepContexts.FirstOrDefault(candidate =>
            candidate.Step.NodeId == activeLink.SourceNodeId
        );
        if (sourceStepContext is null)
        {
            return new LinkPresentationState(
                true,
                true,
                sourceStepLabel,
                sourceOutputLabel,
                $"Linked source '{sourceStepLabel}' was removed."
            );
        }

        sourceStepLabel = sourceStepContext.DisplayName;
        if (sourceStepContext.Index >= currentStepContext.Index)
        {
            return new LinkPresentationState(
                true,
                true,
                sourceStepLabel,
                sourceOutputLabel,
                $"Linked source '{sourceStepLabel}' must stay above this modifier."
            );
        }

        if (!sourceStepContext.Step.Enabled)
        {
            return new LinkPresentationState(
                true,
                true,
                sourceStepLabel,
                sourceOutputLabel,
                $"Linked source '{sourceStepLabel}' is disabled."
            );
        }

        if (sourceStepContext.Contract is null)
        {
            var message = string.IsNullOrWhiteSpace(sourceStepContext.ContractError)
                ? $"Linked source '{sourceStepLabel}' could not be loaded."
                : $"Linked source '{sourceStepLabel}' could not be loaded: {sourceStepContext.ContractError}";
            return new LinkPresentationState(
                true,
                true,
                sourceStepLabel,
                sourceOutputLabel,
                message
            );
        }

        var sourceOutput = sourceStepContext.Contract.Outputs.FirstOrDefault(output =>
            output.Id.Equals(activeLink.SourceOutputId, StringComparison.Ordinal)
        );
        if (sourceOutput is null)
        {
            return new LinkPresentationState(
                true,
                true,
                sourceStepLabel,
                sourceOutputLabel,
                $"Linked output '{sourceOutputLabel}' no longer exists on '{sourceStepLabel}'."
            );
        }

        sourceOutputLabel = sourceOutput.Label;
        if (!AreKindsLinkCompatible(input.Kind, sourceOutput.Kind))
        {
            return new LinkPresentationState(
                true,
                true,
                sourceStepLabel,
                sourceOutputLabel,
                $"Linked output '{sourceOutputLabel}' is no longer compatible with '{input.Label}'."
            );
        }

        var runtimeOutputs = runtime?.GetOutputsForIndex(sourceStepContext.Index);
        var hasRuntimeValue = TryGetStepOutputValue(
            runtimeOutputs,
            sourceOutput.Id,
            out var runtimeOutput
        );
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
        IEnumerable<ModifierSpec> steps,
        ModifierInputLinkSpec activeLink
    )
    {
        var sourceObjectLabel = GetStoredObjectLabel(activeLink);
        if (activeLink.SourceObjectId == Guid.Empty)
        {
            return new LinkPresentationState(
                true,
                true,
                sourceObjectLabel,
                string.Empty,
                "Modified source object is missing."
            );
        }

        if (
            TryGetObjectPreviewCyclePath(
                doc,
                objectId,
                GetActiveObjectPreviewDependencies(steps),
                out var cyclePath
            )
        )
        {
            return new LinkPresentationState(
                true,
                true,
                sourceObjectLabel,
                string.Empty,
                $"Circular modified-geometry reference detected: {FormatObjectPreviewCycle(doc, cyclePath)}."
            );
        }

        var sourceRhinoObject = doc.Objects.FindId(activeLink.SourceObjectId);
        if (sourceRhinoObject is null)
        {
            return new LinkPresentationState(
                true,
                true,
                sourceObjectLabel,
                string.Empty,
                $"Modified source '{sourceObjectLabel}' was removed."
            );
        }

        sourceObjectLabel = DescribeLinkedObject(sourceRhinoObject);
        var sourceSpec = ModifierStackStorage.Load(sourceRhinoObject);
        if (new ModifierStack(sourceSpec).Flatten().Count == 0)
        {
            return new LinkPresentationState(
                true,
                true,
                sourceObjectLabel,
                string.Empty,
                $"Modified source '{sourceObjectLabel}' no longer has any modifiers."
            );
        }

        var sourceRuntime = TryGetStackRuntime(doc, activeLink.SourceObjectId);
        if (sourceRuntime is null || !sourceRuntime.HasEvaluated)
        {
            return new LinkPresentationState(
                true,
                false,
                sourceObjectLabel,
                string.Empty,
                $"Using modified result of {sourceObjectLabel}. Waiting for preview evaluation."
            );
        }

        if (!string.IsNullOrWhiteSpace(sourceRuntime.ErrorMessage))
        {
            return new LinkPresentationState(
                true,
                true,
                sourceObjectLabel,
                string.Empty,
                $"Modified source '{sourceObjectLabel}' is unavailable: {sourceRuntime.ErrorMessage}"
            );
        }

        var geometrySummary =
            sourceRuntime.PreviewGeometry.Count == 0
                ? "none"
                : DescribeGeometry(sourceRuntime.PreviewGeometry);
        return new LinkPresentationState(
            true,
            false,
            sourceObjectLabel,
            string.Empty,
            $"Using modified result of {sourceObjectLabel}. {geometrySummary}"
        );
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

    private static bool TryGetInputLink(
        ModifierSpec stepSpec,
        string inputId,
        out ModifierInputLinkSpec linkSpec
    )
    {
        if (
            stepSpec.InputLinks.TryGetValue(inputId, out var storedLink)
            && IsStoredLinkValid(storedLink)
        )
        {
            linkSpec = storedLink!;
            return true;
        }

        linkSpec = null!;
        return false;
    }

    private static bool TryGetStepOutputInputLink(
        ModifierSpec stepSpec,
        string inputId,
        out ModifierInputLinkSpec linkSpec
    )
    {
        if (
            TryGetInputLink(stepSpec, inputId, out linkSpec)
            && linkSpec.SourceKind == ModifierInputLinkSourceKind.StepOutput
        )
        {
            return true;
        }

        linkSpec = null!;
        return false;
    }

    private static bool TryGetObjectPreviewInputLink(
        ModifierSpec stepSpec,
        string inputId,
        out ModifierInputLinkSpec linkSpec
    )
    {
        if (
            TryGetInputLink(stepSpec, inputId, out linkSpec)
            && linkSpec.SourceKind == ModifierInputLinkSourceKind.ObjectPreview
        )
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
            ModifierInputLinkSourceKind.StepOutput => storedLink.SourceNodeId != Guid.Empty
                && !string.IsNullOrWhiteSpace(storedLink.SourceOutputId),
            ModifierInputLinkSourceKind.ObjectPreview => storedLink.SourceObjectId != Guid.Empty,
            _ => false,
        };
    }

    private static bool TryGetExplicitInputValue(
        ModifierSpec stepSpec,
        ModifierInputDescriptor descriptor,
        out string serializedValue
    )
    {
        if (stepSpec.InputValues.TryGetValue(descriptor.Id, out var storedValue))
        {
            serializedValue = storedValue?.Trim() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(serializedValue);
        }

        serializedValue = string.Empty;
        return false;
    }

    private static string GetDisplayedInputValue(
        ModifierSpec stepSpec,
        ModifierInputDescriptor descriptor
    )
    {
        if (TryGetExplicitInputValue(stepSpec, descriptor, out var serializedValue))
        {
            return serializedValue;
        }

        return descriptor.HasDefaultValue ? descriptor.DefaultSerializedValue : string.Empty;
    }

    private static bool IsMissingRequiredInput(
        ModifierSpec stepSpec,
        ModifierInputDescriptor descriptor
    )
    {
        if (
            descriptor.IsOptional
            || descriptor.UsesSceneGeometryWhenBlank
            || TryGetInputLink(stepSpec, descriptor.Id, out _)
        )
        {
            return false;
        }

        if (TryGetExplicitInputValue(stepSpec, descriptor, out _))
        {
            return false;
        }

        return !descriptor.HasDefaultValue;
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
            .Split(new[] { ',', ';', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token => token.Length > 0)
            .ToArray();
    }

    private static bool TryGetStepOutputValue(
        IReadOnlyList<StepOutputValue>? outputs,
        string outputId,
        out StepOutputValue outputValue
    )
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

    private static string AppendDescription(string description, string note)
    {
        return string.IsNullOrWhiteSpace(description) ? note : $"{description} {note}";
    }

    private bool TryPrepareObjectPreviewDependencies(
        RhinoDoc doc,
        Guid objectId,
        ModifierStackSpec spec,
        out string reason
    )
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
            if (new ModifierStack(sourceSpec).Flatten().Count == 0)
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
                    reason =
                        $"Waiting for modified source {DescribeLinkedObject(sourceRhinoObject)}.";
                }
            }
        }

        return !waitingForUpstream;
    }

    private bool TryEvaluateStackThroughStep(
        RhinoDoc doc,
        Guid objectId,
        RhinoObject rhinoObject,
        IReadOnlyList<ModifierSpec> modifiers,
        int stepIndex,
        out List<GeometryBase> outputGeometry,
        out string error
    )
    {
        outputGeometry = new List<GeometryBase>();
        error = string.Empty;

        if (
            !GeometryConversion.TryGetSourceGeometry(
                rhinoObject.Geometry,
                out var currentGeometry,
                out var sourceError
            )
        )
        {
            error = sourceError;
            return false;
        }

        var runtime = GetOrCreateStackRuntime(doc, objectId);
        return _executor.TryEvaluateStackThroughStep(
            doc,
            runtime,
            modifiers,
            stepIndex,
            currentGeometry,
            out outputGeometry,
            out error
        );
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

    private static bool ReplaceManagedObjectGeometry(
        RhinoDoc doc,
        Guid objectId,
        GeometryBase geometry,
        out string error
    )
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

    private static bool TryAddGeometryObject(
        RhinoDoc doc,
        GeometryBase geometry,
        ObjectAttributes attributes,
        out Guid addedId,
        out string error
    )
    {
        error = string.Empty;

        var toAdd = geometry.Duplicate();

        addedId = toAdd switch
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
        var oldDependencies = documentState.DependenciesByObject.TryGetValue(
            objectId,
            out var existingDependencies
        )
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
                if (
                    documentState.DependentsByObject.TryGetValue(sourceObjectId, out var dependents)
                )
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
        if (
            !_documents.TryGetValue(doc.RuntimeSerialNumber, out var documentState)
            || !documentState.DependentsByObject.TryGetValue(objectId, out var dependents)
        )
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
        return GetActiveObjectPreviewDependencies(new ModifierStack(spec).Flatten());
    }

    private static IEnumerable<Guid> GetActiveObjectPreviewDependencies(
        IEnumerable<ModifierSpec> steps
    )
    {
        foreach (var step in steps)
        {
            if (!step.Enabled)
            {
                continue;
            }

            foreach (var inputLink in step.InputLinks.Values)
            {
                if (
                    inputLink is null
                    || inputLink.SourceKind != ModifierInputLinkSourceKind.ObjectPreview
                    || inputLink.SourceObjectId == Guid.Empty
                )
                {
                    continue;
                }

                yield return inputLink.SourceObjectId;
            }
        }
    }

    private static bool TryGetObjectPreviewCycleError(
        RhinoDoc doc,
        Guid objectId,
        ModifierStackSpec spec,
        out string message
    )
    {
        message = string.Empty;
        if (
            !TryGetObjectPreviewCyclePath(
                doc,
                objectId,
                GetActiveObjectPreviewDependencies(spec),
                out var cyclePath
            )
        )
        {
            return false;
        }

        message =
            $"Circular modified-geometry reference detected: {FormatObjectPreviewCycle(doc, cyclePath)}.";
        return true;
    }

    private static bool TryGetObjectPreviewCyclePath(
        RhinoDoc doc,
        Guid targetObjectId,
        IEnumerable<Guid> sourceObjectIds,
        out IReadOnlyList<Guid> cyclePath
    )
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
            if (
                TryReachTargetObject(doc, sourceObjectId, targetObjectId, new HashSet<Guid>(), path)
            )
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
        List<Guid> path
    )
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
            foreach (
                var dependencyObjectId in GetActiveObjectPreviewDependencies(currentSpec).Distinct()
            )
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
        return string.Join(
            " -> ",
            cyclePath.Select(objectId =>
                DescribeLinkedObject(doc.Objects.FindId(objectId), objectId)
            )
        );
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

        return new ModifierStack(ModifierStackStorage.Load(rhinoObject)).Flatten().Count > 0;
    }

    private static string DescribeLinkedObject(RhinoObject? rhinoObject, Guid objectId)
    {
        return rhinoObject is null
            ? objectId.ToString("D")
            : $"{rhinoObject.ObjectType} {rhinoObject.Id}";
    }

    private void InvalidateStackFromStep(
        RhinoDoc doc,
        Guid objectId,
        ModifierStackSpec spec,
        int fromStepIndex
    )
    {
        var stepCount = new ModifierStack(spec).Flatten().Count;
        if (stepCount == 0)
        {
            Log($"InvalidateStackFromStep removing empty stack. Object={objectId}");
            RemoveStackRuntime(doc, objectId);
            return;
        }

        UpdateObjectDependencies(doc, objectId, spec);
        var runtime = GetOrCreateStackRuntime(doc, objectId);
        runtime.EnsureStepCapacity(stepCount);
        runtime.InvalidateFromStep(fromStepIndex);
        Log(
            $"Stack invalidated from step {fromStepIndex}. Object={objectId}, StepCount={stepCount}"
        );
        QueueEvaluation(doc, objectId);
        RaiseStateChanged();
    }

    private void ResetStackRuntime(RhinoDoc doc, Guid objectId, ModifierStackSpec spec)
    {
        var stepCount = new ModifierStack(spec).Flatten().Count;
        if (stepCount == 0)
        {
            Log($"ResetStackRuntime removing empty stack. Object={objectId}");
            RemoveStackRuntime(doc, objectId);
            return;
        }

        UpdateObjectDependencies(doc, objectId, spec);
        var runtime = GetOrCreateStackRuntime(doc, objectId);
        runtime.Reset(stepCount);
        runtime.RootRevision = NextRevision();
        Log(
            $"Stack runtime reset. Object={objectId}, StepCount={stepCount}, RootRevision={runtime.RootRevision}"
        );
        QueueEvaluation(doc, objectId);
        RaiseStateChanged();
    }

    private void EnsureSavedStackRuntime(RhinoDoc doc, Guid objectId, ModifierStackSpec spec)
    {
        var stepCount = new ModifierStack(spec).Flatten().Count;
        if (stepCount == 0 || TryGetStackRuntime(doc, objectId) is not null)
        {
            return;
        }

        UpdateObjectDependencies(doc, objectId, spec);
        var runtime = GetOrCreateStackRuntime(doc, objectId);
        runtime.Reset(stepCount);
        runtime.RootRevision = NextRevision();
        Log(
            $"Saved stack runtime restored lazily. Object={objectId}, StepCount={stepCount}, RootRevision={runtime.RootRevision}"
        );
        QueueEvaluation(doc, objectId);
    }

    private void RestoreSavedStacks(RhinoDoc doc)
    {
        var restoredCount = 0;
        foreach (var rhinoObject in doc.Objects)
        {
            var spec = ModifierStackStorage.Load(rhinoObject);
            if (new ModifierStack(spec).Flatten().Count == 0)
            {
                continue;
            }

            EnsureSavedStackRuntime(doc, rhinoObject.Id, spec);
            restoredCount += 1;
        }

        Log(
            $"Saved stack restore scan complete. Doc={doc.RuntimeSerialNumber}, Restored={restoredCount}"
        );
        RaiseStateChanged();
    }

    private StackRuntime GetOrCreateStackRuntime(RhinoDoc doc, Guid objectId)
    {
        var documentState = GetOrCreateDocumentState(doc);
        if (!documentState.Stacks.TryGetValue(objectId, out var runtime))
        {
            runtime = new StackRuntime { RootRevision = NextRevision() };
            documentState.Stacks[objectId] = runtime;
            Log(
                $"Stack runtime created. Doc={doc.RuntimeSerialNumber}, Object={objectId}, RootRevision={runtime.RootRevision}"
            );
        }

        return runtime;
    }

    private StackRuntime? TryGetStackRuntime(RhinoDoc doc, Guid objectId)
    {
        return
            _documents.TryGetValue(doc.RuntimeSerialNumber, out var documentState)
            && documentState.Stacks.TryGetValue(objectId, out var runtime)
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

        if (
            _documents.TryGetValue(doc.RuntimeSerialNumber, out var documentState)
            && documentState.Stacks.Remove(objectId, out var runtime)
        )
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
            Log(
                $"Queued stack evaluation. Doc={doc.RuntimeSerialNumber}, Object={objectId}, QueueCount={_queuedStacks.Count}"
            );
        }
        else
        {
            Log(
                $"Skipped queueing duplicate stack evaluation. Doc={doc.RuntimeSerialNumber}, Object={objectId}"
            );
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
        Log(
            $"Requesting viewport redraw. Doc={doc.RuntimeSerialNumber}, PreviewObjectCount={GetPreviewStacks(doc).Count()}"
        );
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

    private static bool TryGetSingleSelectedObject(
        RhinoDoc doc,
        out RhinoObject? rhinoObject,
        out string message
    )
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
        return rhinoObject.Geometry
            is Rhino.Geometry.Point
                or Curve
                or Brep
                or Extrusion
                or Mesh
                or SubD;
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

        return string.Join(
            ", ",
            items.GroupBy(item => item.ObjectType).Select(group => $"{group.Key} x{group.Count()}")
        );
    }
}
