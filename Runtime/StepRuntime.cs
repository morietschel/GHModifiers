using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Rhino.Geometry;

namespace RhinoModifiers.Runtime
{
    /// <summary>
    /// Contains the execution state, bindings, and cached geometry for a single modifier step.
    /// </summary>
    internal sealed class StepRuntime : IDisposable
    {
        public StepRuntime(
            string path,
            DateTime lastWriteUtc,
            GH_Document document,
            DefinitionContract contract,
            Param_Geometry? sceneInputSource,
            IGH_Param? sceneInputParam,
            List<RuntimeInputBinding> inputs,
            List<RuntimeOutputBinding> outputs
        )
        {
            Path = path;
            LastWriteUtc = lastWriteUtc;
            Document = document;
            Contract = contract;
            SceneInputSource = sceneInputSource;
            SceneInputParam = sceneInputParam;
            Inputs = inputs;
            Outputs = outputs;
            LastInputRevision = ulong.MaxValue;
        }

        public string Path { get; }

        public DateTime LastWriteUtc { get; }

        public GH_Document Document { get; }

        public DefinitionContract Contract { get; }

        public Param_Geometry? SceneInputSource { get; }

        public IGH_Param? SceneInputParam { get; }

        public List<RuntimeInputBinding> Inputs { get; }

        public List<RuntimeOutputBinding> Outputs { get; }

        public ulong LastInputRevision { get; set; }

        public ulong LastOutputRevision { get; set; }

        public List<GeometryBase> CachedOutput { get; set; } = new();

        public List<StepOutputValue> CachedPublishedOutputs { get; set; } = new();

        public void Dispose()
        {
            Document.Dispose();
            CachedOutput.Clear();
            CachedPublishedOutputs.Clear();
        }
    }
}
