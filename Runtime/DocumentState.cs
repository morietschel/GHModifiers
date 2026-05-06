using System;
using System.Collections.Generic;

namespace RhinoModifiers.Runtime
{
    /// <summary>
    /// Represents the state of the document, maintaining open stacks and dependencies.
    /// </summary>
    internal sealed class DocumentState : IDisposable
    {
        public Dictionary<Guid, StackRuntime> Stacks { get; } = new();

        public Dictionary<Guid, HashSet<Guid>> DependenciesByObject { get; } = new();

        public Dictionary<Guid, HashSet<Guid>> DependentsByObject { get; } = new();

        public HashSet<Guid> DirtyObjects { get; } = new();

        public void Dispose()
        {
            foreach (var runtime in Stacks.Values)
            {
                runtime.Dispose();
            }

            Stacks.Clear();
            DependenciesByObject.Clear();
            DependentsByObject.Clear();
            DirtyObjects.Clear();
        }
    }
}
