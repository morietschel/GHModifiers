using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GH_IO.Serialization;
using Grasshopper.GUI.Base;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoModifiers.Models;

namespace RhinoModifiers.Runtime;

/// <summary>
/// Executes modifier stacks by loading Grasshopper definitions, binding runtime inputs,
/// solving each step, and caching the resulting outputs.
/// </summary>
/// <remarks>
/// This class owns the expensive Grasshopper-facing work that used to live inside
/// <see cref="ModifierEngine"/>: definition loading, step runtime creation, input/output
/// binding, and stack evaluation. The engine now coordinates events and document state,
/// then hands the actual solve to this facade.
/// </remarks>
internal sealed class RuntimeExecutor : IDisposable
{
    private static readonly string[] InputAliases = { "GeomIn", "GeoIn" };
    private static readonly string[] OutputAliases = { "GeomOut", "GeoOut" };

    private readonly Dictionary<string, DefinitionTemplate> _definitionCache = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly Func<RhinoDoc, Guid, StackRuntime?> _tryGetStackRuntime;
    private ulong _revisionCounter = 1;

    public RuntimeExecutor(Func<RhinoDoc, Guid, StackRuntime?> tryGetStackRuntime)
    {
        _tryGetStackRuntime = tryGetStackRuntime;
    }

    /// <summary>
    /// Evaluates an entire modifier stack and updates the supplied runtime with the
    /// resulting cached geometry, outputs, and error state.
    /// </summary>
    public void EvaluateStack(
        RhinoDoc doc,
        Guid objectId,
        IReadOnlyList<GeometryBase> currentGeometry,
        ModifierStackSpec spec,
        StackRuntime runtime
    )
    {
        ulong upstreamRevision = runtime.RootRevision;
        var anyStepSucceeded = false;
        var failedAtFirstEnabledStep = false;
        var firstEnabledIndex = spec.Steps.FindIndex(step => step.Enabled);
        var publishedOutputsByStepId = new Dictionary<Guid, IReadOnlyList<StepOutputValue>>();
        var geometryState = CloneGeometry(currentGeometry);

        Log(
            $"Stack evaluation entering step loop. RootRevision={runtime.RootRevision}, FirstEnabledIndex={firstEnabledIndex}"
        );

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
                Log(
                    $"Step {i} runtime setup failed for '{Path.GetFileName(stepSpec.Path)}'. {ex.Message}"
                );
                failedAtFirstEnabledStep = i == firstEnabledIndex;
                break;
            }

            if (stepRuntime.LastInputRevision == upstreamRevision)
            {
                Log(
                    $"Step {i} cache hit. Modifier={Path.GetFileName(stepSpec.Path)}, InputRevision={upstreamRevision}, CachedOutputCount={stepRuntime.CachedOutput.Count}"
                );
                runtime.SetOutputs(i, stepRuntime.CachedPublishedOutputs);
                publishedOutputsByStepId[stepSpec.StepId] = stepRuntime.CachedPublishedOutputs;
                if (stepRuntime.HasGeometryOutputs)
                {
                    geometryState = CloneGeometry(stepRuntime.CachedOutput);
                }

                upstreamRevision = stepRuntime.LastOutputRevision;
                anyStepSucceeded = true;
                continue;
            }

            Log(
                $"Step {i} solving. Modifier={Path.GetFileName(stepSpec.Path)}, InputRevision={upstreamRevision}, InputCount={geometryState.Count}, InputSummary={DescribeGeometry(geometryState)}"
            );
            var result = EvaluateStep(
                doc,
                stepRuntime,
                stepSpec,
                i,
                spec.Steps,
                publishedOutputsByStepId,
                geometryState
            );
            if (!result.Success)
            {
                runtime.SetError(i, result.ErrorMessage);
                runtime.ClearOutputs(i);
                publishedOutputsByStepId.Remove(stepSpec.StepId);
                Log(
                    $"Step {i} solve failed for '{Path.GetFileName(stepSpec.Path)}'. {result.ErrorMessage}"
                );
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
                geometryState = CloneGeometry(stepRuntime.CachedOutput);
            }

            upstreamRevision = stepRuntime.LastOutputRevision;
            anyStepSucceeded = true;
            Log(
                $"Step {i} solve complete. Modifier={Path.GetFileName(stepSpec.Path)}, OutputRevision={stepRuntime.LastOutputRevision}, HasGeometryOutput={result.HasGeometryOutput}, RawOutputCount={result.RawGeometryOutputItemCount}, ConvertedOutputCount={stepRuntime.CachedOutput.Count}, OutputSummary={DescribeGeometry(result.HasGeometryOutput ? stepRuntime.CachedOutput : geometryState)}"
            );
            if (result.SkippedGeometryOutputTypes.Count > 0)
            {
                Log(
                    $"Step {i} skipped output items. Modifier={Path.GetFileName(stepSpec.Path)}, Skipped={string.Join(", ", result.SkippedGeometryOutputTypes)}"
                );
            }
        }

        runtime.PreviewGeometry =
            failedAtFirstEnabledStep && !anyStepSucceeded
                ? new List<GeometryBase>()
                : CloneGeometry(geometryState);
        runtime.HasEvaluated = true;

        if (runtime.PreviewGeometry.Count == 0 && string.IsNullOrWhiteSpace(runtime.ErrorMessage))
        {
            Log($"Stack on {objectId} evaluated but produced no preview geometry.");
        }
    }

    /// <summary>
    /// Evaluates a modifier stack up to and including the specified step index,
    /// returning the resulting geometry without caching the evaluation state.
    /// </summary>
    public bool TryEvaluateStackThroughStep(
        RhinoDoc doc,
        StackRuntime runtime,
        ModifierStackSpec spec,
        int stepIndex,
        IReadOnlyList<GeometryBase> inputGeometry,
        out List<GeometryBase> outputGeometry,
        out string error
    )
    {
        error = string.Empty;
        outputGeometry = new List<GeometryBase>();

        runtime.EnsureStepCapacity(spec.Steps.Count);
        var publishedOutputsByStepId = new Dictionary<Guid, IReadOnlyList<StepOutputValue>>();
        var geometryState = CloneGeometry(inputGeometry);

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

            var result = EvaluateStep(
                doc,
                stepRuntime,
                stepSpec,
                i,
                spec.Steps,
                publishedOutputsByStepId,
                geometryState
            );
            if (!result.Success)
            {
                error =
                    $"Modifier {i + 1} '{Path.GetFileName(stepSpec.Path)}' failed: {result.ErrorMessage}";
                return false;
            }

            publishedOutputsByStepId[stepSpec.StepId] = result.PublishedOutputs;
            if (result.HasGeometryOutput)
            {
                geometryState = CloneGeometry(result.OutputGeometry);
            }
        }

        outputGeometry = CloneGeometry(geometryState);
        return true;
    }

    /// <summary>
    /// Ensures a step runtime exists for the requested modifier definition and is refreshed
    /// when the definition file changes.
    /// </summary>
    public StepRuntime EnsureStepRuntime(StackRuntime runtime, int index, ModifierStepSpec spec)
    {
        var fullPath = Path.GetFullPath(spec.Path);
        var lastWriteUtc = File.GetLastWriteTimeUtc(fullPath);

        var existing = runtime.StepRuntimes[index];
        if (
            existing is not null
            && existing.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase)
            && existing.LastWriteUtc == lastWriteUtc
        )
        {
            Log(
                $"Step {index} runtime reused. Modifier={Path.GetFileName(fullPath)}, LastWriteUtc={lastWriteUtc:O}"
            );
            return existing;
        }

        existing?.Dispose();
        if (existing is not null)
        {
            Log(
                $"Step {index} runtime replaced. Modifier={Path.GetFileName(fullPath)}, LastWriteUtc={lastWriteUtc:O}"
            );
        }

        var template = GetDefinitionTemplate(fullPath, lastWriteUtc);
        var document = GH_Document.DuplicateDocument(template.Document);
        Log($"Step {index} duplicated GH document. Modifier={Path.GetFileName(fullPath)}");

        Param_Geometry? sceneInputSource = null;
        IGH_Param? sceneInputParam = null;
        if (template.Contract.SceneInput is not null)
        {
            sceneInputParam =
                ResolveDocumentParam(document, template.Contract.SceneInput.ObjectId)
                ?? throw new InvalidOperationException(
                    $"Modifier '{Path.GetFileName(fullPath)}' is missing its scene geometry input in the duplicated document."
                );

            sceneInputSource = CreateRuntimeSourceParam(document, sceneInputParam);
        }

        var inputBindings = BindInputDescriptors(document, template.Contract.Inputs);
        var outputBindings = BindOutputDescriptors(document, template.Contract.Outputs);
        var geometryOutputBindings = BindOutputDescriptors(
            document,
            template.Contract.GeometryOutputs
        );

        Log(
            $"Step {index} runtime created. Modifier={Path.GetFileName(fullPath)}, ExposedInputs={inputBindings.Count}, ExposedOutputs={outputBindings.Count}, GeometryOutputs={geometryOutputBindings.Count}"
        );
        var stepRuntime = new StepRuntime(
            fullPath,
            lastWriteUtc,
            document,
            template.Contract,
            sceneInputSource,
            sceneInputParam,
            inputBindings,
            outputBindings,
            geometryOutputBindings
        );
        runtime.StepRuntimes[index] = stepRuntime;
        return stepRuntime;
    }

    /// <summary>
    /// Looks up or loads a Grasshopper definition template.
    /// </summary>
    public DefinitionTemplate GetDefinitionTemplate(string fullPath, DateTime lastWriteUtc)
    {
        if (
            _definitionCache.TryGetValue(fullPath, out var cached)
            && cached.LastWriteUtc == lastWriteUtc
        )
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
        var template = new DefinitionTemplate(
            fullPath,
            lastWriteUtc,
            document,
            CreateDefinitionContract(document)
        );
        _definitionCache[fullPath] = template;
        Log($"Definition template cached. Path={fullPath}, LastWriteUtc={lastWriteUtc:O}");
        return template;
    }

    /// <summary>
    /// Checks whether a modifier definition can be loaded and parsed.
    /// </summary>
    public bool TryGetDefinitionContract(
        string path,
        out DefinitionContract contract,
        out string error
    )
    {
        contract = null!;
        error = string.Empty;

        try
        {
            var fullPath = Path.GetFullPath(path);
            contract = GetDefinitionTemplate(fullPath, File.GetLastWriteTimeUtc(fullPath)).Contract;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var template in _definitionCache.Values)
        {
            template.Dispose();
        }

        _definitionCache.Clear();
    }

    private static GH_Document LoadDefinitionDocument(string fullPath)
    {
        return WithMissingPluginDialogSuppressed(() =>
        {
            var archive = new GH_Archive();
            if (!archive.ReadFromFile(fullPath))
            {
                throw new InvalidOperationException(
                    $"Failed to load Grasshopper definition '{fullPath}'."
                );
            }

            var document = new GH_Document();
            if (!archive.ExtractObject(document, "Definition"))
            {
                document.Dispose();
                throw new InvalidOperationException(
                    $"Grasshopper definition '{fullPath}' did not produce a document."
                );
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

            if (
                TryCreateInputDescriptor(documentObject, out var descriptor, out var error)
                && descriptor is not null
            )
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

            if (
                TryCreateOutputDescriptor(documentObject, out var descriptor)
                && descriptor is not null
            )
            {
                outputs.Add(descriptor);
                if (
                    documentObject is IGH_Param param
                    && descriptor.Kind == ModifierIoKind.Geometry
                    && IsGeometryPipeOutput(param)
                )
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
        var group = document
            .Objects.OfType<GH_Group>()
            .FirstOrDefault(candidate =>
                candidate.NickName.Equals(groupName, StringComparison.OrdinalIgnoreCase)
            );
        return group is null ? new HashSet<Guid>() : new HashSet<Guid>(group.ObjectIDs);
    }

    private static ModifierInputDescriptor? FindLegacySceneInput(
        GH_Document document,
        HashSet<Guid> groupedInputIds
    )
    {
        var param = document
            .Objects.OfType<IGH_Param>()
            .FirstOrDefault(candidate =>
                !groupedInputIds.Contains(candidate.InstanceGuid)
                && InputAliases.Any(alias =>
                    candidate.NickName.Equals(alias, StringComparison.OrdinalIgnoreCase)
                )
            );

        if (param is null)
        {
            return null;
        }

        if (param.Sources.Count > 0)
        {
            throw new InvalidOperationException("Legacy scene geometry input must be unwired.");
        }

        return CreateParamInputDescriptor(
            param,
            ModifierIoKind.Geometry,
            hasDefaultValue: false,
            defaultSerializedValue: string.Empty,
            usesSceneGeometryWhenBlank: true,
            isOptional: false,
            isFilePath: false,
            minimum: null,
            maximum: null,
            decimalPlaces: 0
        );
    }

    private static ModifierOutputDescriptor? FindLegacyGeometryOutput(
        GH_Document document,
        HashSet<Guid> groupedOutputIds
    )
    {
        var param = document
            .Objects.OfType<IGH_Param>()
            .FirstOrDefault(candidate =>
                !groupedOutputIds.Contains(candidate.InstanceGuid)
                && OutputAliases.Any(alias =>
                    candidate.NickName.Equals(alias, StringComparison.OrdinalIgnoreCase)
                )
            );

        return param is null ? null : CreateOutputDescriptor(param, ModifierIoKind.Geometry);
    }

    private static bool IsGeometryPipeOutput(IGH_Param param)
    {
        return OutputAliases.Any(alias =>
            param.NickName.Equals(alias, StringComparison.OrdinalIgnoreCase)
        );
    }

    private static bool TryCreateInputDescriptor(
        IGH_DocumentObject documentObject,
        out ModifierInputDescriptor? descriptor,
        out string error
    )
    {
        descriptor = null;
        error = string.Empty;

        if (documentObject is GH_ValueList valueList)
        {
            var selectedIndex = valueList.ListItems.FindIndex(item => item.Selected);
            if (selectedIndex < 0)
                selectedIndex = 0;
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
                0
            )
            {
                ValueListItems = valueList.ListItems.ConvertAll(item => item.Name),
            };
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
                slider.Slider.Type == GH_SliderAccuracy.Float ? slider.Slider.DecimalPlaces : 0
            );
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

        var hasDefaultValue = TryReadDefaultSerializedValue(
            param,
            kind,
            out var defaultSerializedValue
        );
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
            decimalPlaces: GetDefaultDecimalPlaces(param, kind)
        );
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

    private static bool TryCreateOutputDescriptor(
        IGH_DocumentObject documentObject,
        out ModifierOutputDescriptor? descriptor
    )
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
        int decimalPlaces
    )
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
            decimalPlaces
        )
        {
            IsFilePath = isFilePath,
        };
    }

    private static ModifierOutputDescriptor CreateOutputDescriptor(
        IGH_Param param,
        ModifierIoKind kind
    )
    {
        return new ModifierOutputDescriptor(
            param.InstanceGuid,
            param.InstanceGuid.ToString("D"),
            GetDisplayLabel(param),
            param.Description ?? string.Empty,
            kind
        );
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

    internal static bool AreKindsLinkCompatible(ModifierIoKind inputKind, ModifierIoKind outputKind)
    {
        return IsNumericLinkKind(inputKind) && IsNumericLinkKind(outputKind)
            ? true
            : inputKind == outputKind;
    }

    private static bool IsNumericLinkKind(ModifierIoKind kind)
    {
        return kind is ModifierIoKind.Number or ModifierIoKind.NumberSlider;
    }

    internal static bool IsMissingRequiredInput(
        ModifierStepSpec stepSpec,
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

    internal static IEnumerable<ModifierInputDescriptor> GetMissingRequiredInputs(
        ModifierStepSpec stepSpec,
        IEnumerable<ModifierInputDescriptor> descriptors
    )
    {
        return descriptors.Where(descriptor => IsMissingRequiredInput(stepSpec, descriptor));
    }

    internal static string FormatMissingRequiredInputs(IEnumerable<string> labels)
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

    private static bool TryReadDefaultSerializedValue(
        IGH_Param param,
        ModifierIoKind kind,
        out string serializedValue
    )
    {
        serializedValue = string.Empty;
        var first = EnumeratePersistentData(param).FirstOrDefault();
        if (first is null)
        {
            return false;
        }

        return TryExtractPublishedOutputValue(first, kind, out _, out serializedValue);
    }

    private static List<RuntimeInputBinding> BindInputDescriptors(
        GH_Document document,
        IEnumerable<ModifierInputDescriptor> descriptors
    )
    {
        var bindings = new List<RuntimeInputBinding>();
        foreach (var descriptor in descriptors)
        {
            var documentObject =
                ResolveDocumentObject(document, descriptor.ObjectId)
                ?? throw new InvalidOperationException(
                    $"Modifier input '{descriptor.Label}' could not be found in the duplicated document."
                );

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

            throw new InvalidOperationException(
                $"Modifier input '{descriptor.Label}' is not a supported Grasshopper input object."
            );
        }

        return bindings;
    }

    private static List<RuntimeOutputBinding> BindOutputDescriptors(
        GH_Document document,
        IEnumerable<ModifierOutputDescriptor> descriptors
    )
    {
        var bindings = new List<RuntimeOutputBinding>();
        foreach (var descriptor in descriptors)
        {
            var param =
                ResolveDocumentParam(document, descriptor.ObjectId)
                ?? throw new InvalidOperationException(
                    $"Modifier output '{descriptor.Label}' could not be found in the duplicated document."
                );
            bindings.Add(new RuntimeOutputBinding(descriptor, param));
        }

        return bindings;
    }

    private static IGH_DocumentObject? ResolveDocumentObject(
        GH_Document document,
        Guid instanceGuid
    )
    {
        return document.Objects.FirstOrDefault(candidate => candidate.InstanceGuid == instanceGuid);
    }

    private static IGH_Param? ResolveDocumentParam(GH_Document document, Guid instanceGuid)
    {
        return ResolveDocumentObject(document, instanceGuid) as IGH_Param;
    }

    private StepEvaluationResult EvaluateStep(
        RhinoDoc doc,
        StepRuntime runtime,
        ModifierStepSpec stepSpec,
        int stepIndex,
        IReadOnlyList<ModifierStepSpec> allSteps,
        IReadOnlyDictionary<Guid, IReadOnlyList<StepOutputValue>> publishedOutputsByStepId,
        IReadOnlyList<GeometryBase> inputGeometry
    )
    {
        var missingInputs = GetMissingRequiredInputs(stepSpec, runtime.Contract.Inputs)
            .Select(input => input.Label)
            .ToArray();
        if (missingInputs.Length > 0)
        {
            return StepEvaluationResult.Fail(FormatMissingRequiredInputs(missingInputs));
        }

        if (
            runtime.SceneInputSource is not null
            && !TryAppendGeometry(
                runtime.SceneInputSource,
                inputGeometry,
                out var sceneGeometryError
            )
        )
        {
            return StepEvaluationResult.Fail(sceneGeometryError);
        }

        foreach (var binding in runtime.Inputs)
        {
            if (
                !ApplyInputBinding(
                    doc,
                    binding,
                    stepSpec,
                    stepIndex,
                    allSteps,
                    publishedOutputsByStepId,
                    inputGeometry,
                    out var bindingError
                )
            )
            {
                return StepEvaluationResult.Fail(bindingError);
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
            return StepEvaluationResult.Successful(
                false,
                new List<GeometryBase>(),
                outputValues,
                0,
                new List<string>()
            );
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

        return StepEvaluationResult.Successful(
            true,
            geometry,
            outputValues,
            totalRawItemCount,
            skipped
        );
    }

    private static Param_Geometry CreateRuntimeSourceParam(
        GH_Document document,
        IGH_Param inputParam
    )
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

    private bool ApplyInputBinding(
        RhinoDoc doc,
        RuntimeInputBinding binding,
        ModifierStepSpec stepSpec,
        int stepIndex,
        IReadOnlyList<ModifierStepSpec> allSteps,
        IReadOnlyDictionary<Guid, IReadOnlyList<StepOutputValue>> publishedOutputsByStepId,
        IReadOnlyList<GeometryBase> currentGeometry,
        out string error
    )
    {
        error = string.Empty;

        if (TryGetInputLink(stepSpec, binding.Descriptor.Id, out var inputLink))
        {
            return TryApplyLinkedInput(
                doc,
                binding,
                stepIndex,
                allSteps,
                publishedOutputsByStepId,
                inputLink,
                out error
            );
        }

        var hasExplicitValue = TryGetExplicitInputValue(
            stepSpec,
            binding.Descriptor,
            out var serializedValue
        );

        if (binding.Slider is not null)
        {
            if (!hasExplicitValue)
            {
                return true;
            }

            if (
                !decimal.TryParse(
                    serializedValue,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var sliderValue
                )
            )
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
            if (
                hasExplicitValue
                && int.TryParse(
                    serializedValue,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var selectedIndex
                )
                && selectedIndex >= 0
                && selectedIndex < binding.ValueList.ListItems.Count
            )
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
                if (
                    !double.TryParse(
                        serializedValue,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var numberValue
                    )
                )
                {
                    error = $"Input '{binding.Descriptor.Label}' expects a number.";
                    return false;
                }

                AppendSingleValue(
                    binding.Param,
                    binding.Param is Param_Integer
                        ? (object)(int)Math.Round(numberValue, MidpointRounding.AwayFromZero)
                        : numberValue
                );
                return true;

            case ModifierIoKind.Point:
                if (string.IsNullOrWhiteSpace(serializedValue))
                {
                    return true;
                }

                if (!TryParsePoint(serializedValue, out var pointValue))
                {
                    error =
                        $"Input '{binding.Descriptor.Label}' expects a point formatted like x,y,z.";
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
                    error =
                        $"Input '{binding.Descriptor.Label}' expects a color like #RRGGBB or r,g,b.";
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

                return TryAppendReferencedGeometry(
                    doc,
                    binding.Param,
                    serializedValue,
                    currentGeometry,
                    out error
                );

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
        out string error
    )
    {
        error = string.Empty;
        if (inputLink.SourceKind == ModifierInputLinkSourceKind.ObjectPreview)
        {
            return TryApplyObjectPreviewInput(doc, binding, inputLink, out error);
        }

        if (
            !TryResolveLinkedOutput(
                binding.Descriptor,
                stepIndex,
                allSteps,
                publishedOutputsByStepId,
                inputLink,
                out var sourceOutputValue,
                out var sourceStepLabel,
                out var sourceOutputLabel,
                out error
            )
        )
        {
            return false;
        }

        if (binding.Slider is not null)
        {
            return TrySetLinkedSliderValue(
                binding,
                sourceOutputValue,
                sourceOutputLabel,
                out error
            );
        }

        if (binding.Param is null)
        {
            error = $"Input '{binding.Descriptor.Label}' is not bound to a Grasshopper parameter.";
            return false;
        }

        return TrySetLinkedParamValues(
            binding,
            sourceOutputValue,
            sourceStepLabel,
            sourceOutputLabel,
            out error
        );
    }

    private bool TryApplyObjectPreviewInput(
        RhinoDoc doc,
        RuntimeInputBinding binding,
        ModifierInputLinkSpec inputLink,
        out string error
    )
    {
        error = string.Empty;
        if (binding.Descriptor.Kind != ModifierIoKind.Geometry)
        {
            error =
                $"Input '{binding.Descriptor.Label}' does not support modified-geometry references.";
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

        var sourceRuntime = _tryGetStackRuntime(doc, inputLink.SourceObjectId);
        if (sourceRuntime is null || !sourceRuntime.HasEvaluated)
        {
            error = $"Modified source '{sourceObjectLabel}' has no preview available yet.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(sourceRuntime.ErrorMessage))
        {
            error =
                $"Modified source '{sourceObjectLabel}' is unavailable: {sourceRuntime.ErrorMessage}";
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
        out string error
    )
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

        if (
            !TryGetDefinitionContract(
                sourceStep.Path,
                out var sourceContract,
                out var contractError
            )
        )
        {
            error = $"Linked source '{sourceStepLabel}' could not be loaded: {contractError}";
            return false;
        }

        var sourceOutput = sourceContract.Outputs.FirstOrDefault(output =>
            output.Id.Equals(inputLink.SourceOutputId, StringComparison.Ordinal)
        );
        if (sourceOutput is null)
        {
            error = $"Linked output '{sourceOutputLabel}' no longer exists on '{sourceStepLabel}'.";
            return false;
        }

        sourceOutputLabel = sourceOutput.Label;
        if (!AreKindsLinkCompatible(targetInput.Kind, sourceOutput.Kind))
        {
            error =
                $"Linked output '{sourceOutputLabel}' is no longer compatible with input '{targetInput.Label}'.";
            return false;
        }

        if (
            !publishedOutputsByStepId.TryGetValue(sourceStep.StepId, out var publishedOutputs)
            || !TryGetStepOutputValue(publishedOutputs, sourceOutput.Id, out sourceOutputValue)
        )
        {
            error =
                $"Linked source '{sourceStepLabel}' has no runtime output available for '{sourceOutputLabel}'.";
            return false;
        }

        return true;
    }

    internal static bool TryGetInputLink(
        ModifierStepSpec stepSpec,
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

    internal static bool TryGetStepOutputInputLink(
        ModifierStepSpec stepSpec,
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

    internal static bool TryGetObjectPreviewInputLink(
        ModifierStepSpec stepSpec,
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

    internal static bool IsStoredLinkValid(ModifierInputLinkSpec? storedLink)
    {
        if (storedLink is null)
        {
            return false;
        }

        return storedLink.SourceKind switch
        {
            ModifierInputLinkSourceKind.StepOutput => storedLink.SourceStepId != Guid.Empty
                && !string.IsNullOrWhiteSpace(storedLink.SourceOutputId),
            ModifierInputLinkSourceKind.ObjectPreview => storedLink.SourceObjectId != Guid.Empty,
            _ => false,
        };
    }

    internal static bool TryGetExplicitInputValue(
        ModifierStepSpec stepSpec,
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

    private static void ClearParamData(IGH_Param param)
    {
        param.ClearData();
        SetPersistentData(param, Array.Empty<object>());
    }

    private static void AppendSingleValue(IGH_Param param, object value)
    {
        SetPersistentData(param, new[] { value });
    }

    private static bool TryAppendGeometry(
        IGH_Param param,
        IEnumerable<GeometryBase> geometry,
        out string error
    )
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

    private static bool TryAppendReferencedGeometry(
        RhinoDoc doc,
        IGH_Param param,
        string serializedValue,
        IReadOnlyList<GeometryBase> currentGeometry,
        out string error
    )
    {
        error = string.Empty;
        var tokens = TokenizeGeometryReferenceValue(serializedValue);

        var geometry = new List<GeometryBase>();
        foreach (var token in tokens)
        {
            if (
                token.Equals("self", StringComparison.OrdinalIgnoreCase)
                || token.Equals("selected", StringComparison.OrdinalIgnoreCase)
            )
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

            if (
                !GeometryConversion.TryGetSourceGeometry(
                    rhinoObject.Geometry,
                    out var converted,
                    out var conversionError
                )
            )
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
            .Split(new[] { ',', ';', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToArray();
    }

    private static bool TrySetLinkedSliderValue(
        RuntimeInputBinding binding,
        StepOutputValue sourceOutputValue,
        string sourceOutputLabel,
        out string error
    )
    {
        error = string.Empty;
        if (binding.Slider is null)
        {
            error = $"Input '{binding.Descriptor.Label}' is not bound to a slider.";
            return false;
        }

        if (sourceOutputValue.Values.Count != 1)
        {
            error =
                $"Linked output '{sourceOutputLabel}' must provide exactly one numeric value for slider input '{binding.Descriptor.Label}'.";
            return false;
        }

        if (!TryConvertToDouble(sourceOutputValue.Values[0], out var sliderValue))
        {
            error =
                $"Linked output '{sourceOutputLabel}' must provide a numeric value for slider input '{binding.Descriptor.Label}'.";
            return false;
        }

        var decimalValue = (decimal)sliderValue;
        if (!binding.Slider.TrySetSliderValue(decimalValue))
        {
            binding.Slider.SetSliderValue(decimalValue);
        }

        return true;
    }

    private static bool TrySetLinkedParamValues(
        RuntimeInputBinding binding,
        StepOutputValue sourceOutputValue,
        string sourceStepLabel,
        string sourceOutputLabel,
        out string error
    )
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
                        error =
                            $"Linked output '{sourceOutputLabel}' from '{sourceStepLabel}' did not publish numeric values.";
                        return false;
                    }

                    values.Add(
                        binding.Param is Param_Integer
                            ? (object)(int)Math.Round(numberValue, MidpointRounding.AwayFromZero)
                            : numberValue
                    );
                }
                break;
            case ModifierIoKind.Point:
                foreach (var publishedValue in sourceOutputValue.Values)
                {
                    if (publishedValue is not Point3d pointValue)
                    {
                        error =
                            $"Linked output '{sourceOutputLabel}' from '{sourceStepLabel}' did not publish point values.";
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
                        error =
                            $"Linked output '{sourceOutputLabel}' from '{sourceStepLabel}' did not publish text values.";
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
                        error =
                            $"Linked output '{sourceOutputLabel}' from '{sourceStepLabel}' did not publish boolean values.";
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
                        error =
                            $"Linked output '{sourceOutputLabel}' from '{sourceStepLabel}' did not publish color values.";
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
                        error =
                            $"Linked output '{sourceOutputLabel}' from '{sourceStepLabel}' did not publish geometry values.";
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

    private static bool TrySetParamValues(
        IGH_Param param,
        IEnumerable<object> values,
        out string error
    )
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

    internal static bool TryGetStepOutputValue(
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

    private static StepOutputValue CapturePublishedOutput(
        ModifierOutputDescriptor descriptor,
        IGH_Param param
    )
    {
        if (descriptor.Kind == ModifierIoKind.Geometry)
        {
            var geometry = GeometryConversion.ReadOutput(param);
            return new StepOutputValue(
                descriptor.Id,
                geometry.Geometry.Count == 0 ? "none" : DescribeGeometry(geometry.Geometry),
                CloneGeometry(geometry.Geometry).Cast<object>().ToList()
            );
        }

        var publishedValues = new List<object>();
        var displayValues = new List<string>();
        foreach (var goo in param.VolatileData.AllData(true))
        {
            if (
                TryExtractPublishedOutputValue(
                    goo,
                    descriptor.Kind,
                    out var publishedValue,
                    out var serializedValue
                )
            )
            {
                publishedValues.Add(publishedValue);
                displayValues.Add(serializedValue);
            }
        }

        return new StepOutputValue(
            descriptor.Id,
            FormatPublishedOutputDisplay(displayValues),
            publishedValues
        );
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

    private static bool TryExtractPublishedOutputValue(
        IGH_Goo goo,
        ModifierIoKind kind,
        out object publishedValue,
        out string serializedValue
    )
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
                    serializedValue = boolValue
                        ? bool.TrueString.ToLowerInvariant()
                        : bool.FalseString.ToLowerInvariant();
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
        return FormattableString.Invariant(
            $"{point.X:0.###############},{point.Y:0.###############},{point.Z:0.###############}"
        );
    }

    private static bool TryParsePoint(string serializedValue, out Point3d point)
    {
        var cleaned = serializedValue.Replace("(", string.Empty).Replace(")", string.Empty);
        var parts = cleaned
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToArray();

        if (
            parts.Length != 3
            || !double.TryParse(
                parts[0],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var x
            )
            || !double.TryParse(
                parts[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var y
            )
            || !double.TryParse(
                parts[2],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var z
            )
        )
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
            var hex = trimmed.Substring(1);
            if (
                hex.Length == 6
                && int.TryParse(
                    hex,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var rgb
                )
            )
            {
                color = System.Drawing.Color.FromArgb(
                    byte.MaxValue,
                    (rgb >> 16) & 0xFF,
                    (rgb >> 8) & 0xFF,
                    rgb & 0xFF
                );
                return true;
            }

            if (
                hex.Length == 8
                && int.TryParse(
                    hex,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var argb
                )
            )
            {
                color = System.Drawing.Color.FromArgb(
                    (argb >> 24) & 0xFF,
                    (argb >> 16) & 0xFF,
                    (argb >> 8) & 0xFF,
                    argb & 0xFF
                );
                return true;
            }
        }

        var parts = trimmed
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToArray();
        if (
            (parts.Length == 3 || parts.Length == 4)
            && byte.TryParse(
                parts[0],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var r
            )
            && byte.TryParse(
                parts[1],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var g
            )
            && byte.TryParse(
                parts[2],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var b
            )
        )
        {
            var a = byte.MaxValue;
            if (
                parts.Length == 4
                && !byte.TryParse(
                    parts[3],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out a
                )
            )
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

    internal static string AppendDescription(string description, string note)
    {
        return string.IsNullOrWhiteSpace(description) ? note : $"{description} {note}";
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
        var addMethod = param
            .GetType()
            .GetMethod("Script_AddPersistentData", new[] { typeof(List<object>) });
        if (clearMethod is null || addMethod is null)
        {
            throw new InvalidOperationException(
                $"Parameter '{GetDisplayLabel(param)}' does not expose persistent data scripting APIs."
            );
        }

        clearMethod.Invoke(param, Array.Empty<object>());
        addMethod.Invoke(param, new object[] { values.ToList() });
    }

    private static string DescribeGeometry(IEnumerable<GeometryBase> geometry)
    {
        var counts = geometry
            .GroupBy(item => item.ObjectType)
            .Select(group => $"{group.Key}:{group.Count()}")
            .ToArray();
        return counts.Length == 0 ? "none" : string.Join(", ", counts);
    }

    private static List<GeometryBase> CloneGeometry(IEnumerable<GeometryBase> geometry)
    {
        return geometry
            .Select(item =>
                (GeometryBase?)item?.Duplicate()
                ?? throw new InvalidOperationException("Encountered null geometry.")
            )
            .ToList();
    }

    internal static string DescribeLinkedObject(RhinoObject? rhinoObject)
    {
        return DescribeLinkedObject(rhinoObject, rhinoObject?.Id ?? Guid.Empty);
    }

    internal static string DescribeLinkedObject(RhinoObject? rhinoObject, Guid objectId)
    {
        return rhinoObject is null
            ? objectId.ToString("D")
            : $"{rhinoObject.ObjectType} {rhinoObject.Id}";
    }

    internal static string GetStoredStepLabel(ModifierInputLinkSpec inputLink)
    {
        return string.IsNullOrWhiteSpace(inputLink.SourceStepLabel)
            ? inputLink.SourceStepId.ToString("D")
            : inputLink.SourceStepLabel;
    }

    internal static string GetStoredOutputLabel(ModifierInputLinkSpec inputLink)
    {
        return string.IsNullOrWhiteSpace(inputLink.SourceOutputLabel)
            ? inputLink.SourceOutputId
            : inputLink.SourceOutputLabel;
    }

    internal static string GetStoredObjectLabel(ModifierInputLinkSpec inputLink)
    {
        return string.IsNullOrWhiteSpace(inputLink.SourceObjectLabel)
            ? inputLink.SourceObjectId.ToString("D")
            : inputLink.SourceObjectLabel;
    }

    private ulong NextRevision()
    {
        return _revisionCounter++;
    }

    private static void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine($"GGH: {message}");
    }
}
