using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace RhinoModifiers.Runtime
{
    /// <summary>
    /// Holds the runtime state and outputs for an entire modifier stack.
    /// </summary>
    internal sealed class StackRuntime : IDisposable
    {
        public ulong RootRevision { get; set; }

        public bool HasEvaluated { get; set; }

        public List<StepRuntime?> StepRuntimes { get; } = new();

        public List<GeometryBase> PreviewGeometry { get; set; } = new();

        public string ErrorMessage { get; private set; } = string.Empty;

        private List<string> _stepErrors = new();
        private List<List<StepOutputValue>> _stepOutputs = new();

        public void EnsureStepCapacity(int count)
        {
            while (StepRuntimes.Count < count)
            {
                StepRuntimes.Add(null);
            }

            if (StepRuntimes.Count > count)
            {
                for (var i = count; i < StepRuntimes.Count; i++)
                {
                    StepRuntimes[i]?.Dispose();
                }

                StepRuntimes.RemoveRange(count, StepRuntimes.Count - count);
            }

            if (_stepErrors.Count != count)
            {
                _stepErrors = Enumerable.Repeat(string.Empty, count).ToList();
            }

            if (_stepOutputs.Count < count)
            {
                while (_stepOutputs.Count < count)
                {
                    _stepOutputs.Add(new List<StepOutputValue>());
                }
            }
            else if (_stepOutputs.Count > count)
            {
                _stepOutputs.RemoveRange(count, _stepOutputs.Count - count);
            }
        }

        public void Reset(int stepCount)
        {
            foreach (var stepRuntime in StepRuntimes)
            {
                stepRuntime?.Dispose();
            }

            StepRuntimes.Clear();
            PreviewGeometry.Clear();
            ErrorMessage = string.Empty;
            HasEvaluated = false;
            _stepErrors = Enumerable.Repeat(string.Empty, stepCount).ToList();
            _stepOutputs = new List<List<StepOutputValue>>(stepCount);

            for (var i = 0; i < stepCount; i++)
            {
                StepRuntimes.Add(null);
                _stepOutputs.Add(new List<StepOutputValue>());
            }
        }

        public void InvalidateFromStep(int fromIndex)
        {
            for (var i = fromIndex; i < StepRuntimes.Count; i++)
            {
                var step = StepRuntimes[i];
                if (step is not null)
                {
                    step.LastInputRevision = ulong.MaxValue;
                }
            }
        }

        public void DisposeStep(int index)
        {
            if (index < 0 || index >= StepRuntimes.Count)
            {
                return;
            }

            StepRuntimes[index]?.Dispose();
            StepRuntimes[index] = null;
            ClearOutputs(index);
        }

        public void ClearErrors(int stepCount)
        {
            ErrorMessage = string.Empty;
            if (_stepErrors.Count != stepCount)
            {
                _stepErrors = Enumerable.Repeat(string.Empty, stepCount).ToList();
                return;
            }

            for (var i = 0; i < _stepErrors.Count; i++)
            {
                _stepErrors[i] = string.Empty;
            }
        }

        public void SetError(int index, string message)
        {
            ErrorMessage = message;
            if (index >= 0 && index < _stepErrors.Count)
            {
                _stepErrors[index] = message;
            }
        }

        public string GetErrorForIndex(int index)
        {
            return index >= 0 && index < _stepErrors.Count ? _stepErrors[index] : string.Empty;
        }

        public IReadOnlyList<StepOutputValue> GetOutputsForIndex(int index)
        {
            return index >= 0 && index < _stepOutputs.Count
                ? _stepOutputs[index]
                : Array.Empty<StepOutputValue>();
        }

        public void ClearAllOutputs(int stepCount)
        {
            if (_stepOutputs.Count != stepCount)
            {
                _stepOutputs = Enumerable
                    .Range(0, stepCount)
                    .Select(_ => new List<StepOutputValue>())
                    .ToList();
                return;
            }

            foreach (var outputs in _stepOutputs)
            {
                outputs.Clear();
            }
        }

        public void ClearOutputs(int index)
        {
            if (index >= 0 && index < _stepOutputs.Count)
            {
                _stepOutputs[index].Clear();
            }
        }

        public void SetOutputs(int index, IEnumerable<StepOutputValue> outputs)
        {
            if (index < 0 || index >= _stepOutputs.Count)
            {
                return;
            }

            _stepOutputs[index] = outputs.ToList();
        }

        public void Dispose()
        {
            foreach (var stepRuntime in StepRuntimes)
            {
                stepRuntime?.Dispose();
            }

            StepRuntimes.Clear();
            PreviewGeometry.Clear();
            HasEvaluated = false;
            _stepErrors.Clear();
            _stepOutputs.Clear();
        }
    }
}
