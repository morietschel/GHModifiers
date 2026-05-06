using System;
using Grasshopper.Kernel;

namespace RhinoModifiers.Runtime
{
    /// <summary>
    /// Represents a parsed Grasshopper document ready to be stamped into step instances.
    /// </summary>
    internal sealed class DefinitionTemplate : IDisposable
    {
        public DefinitionTemplate(string path, DateTime lastWriteUtc, GH_Document document, DefinitionContract contract)
        {
            Path = path;
            LastWriteUtc = lastWriteUtc;
            Document = document;
            Contract = contract;
        }

        public string Path { get; }

        public DateTime LastWriteUtc { get; }

        public GH_Document Document { get; }

        public DefinitionContract Contract { get; }

        public void Dispose()
        {
            Document.Dispose();
        }
    }
}
