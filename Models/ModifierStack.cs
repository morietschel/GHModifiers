using System;
using System.Collections.Generic;

namespace RhinoModifiers.Models;

/// <summary>
/// Owns the tree structure of a modifier stack: ordering, grouping, and mutation.
/// The execution pipeline only ever consumes <see cref="Flatten"/>, the pre-order
/// list of leaf <see cref="ModifierSpec"/> nodes, so groups never leak into
/// evaluation, links, or the geometry pipeline.
/// </summary>
internal sealed class ModifierStack
{
    public ModifierStack(ModifierStackSpec spec)
    {
        Spec = spec ?? throw new ArgumentNullException(nameof(spec));
        Spec.Nodes ??= new List<ModifierNodeSpec>();
    }

    public ModifierStackSpec Spec { get; }

    /// <summary>
    /// Returns the leaf modifiers in pre-order (depth-first, left-to-right, groups
    /// skipped). This is the single view the runtime pipeline sees.
    /// </summary>
    public IReadOnlyList<ModifierSpec> Flatten()
    {
        var result = new List<ModifierSpec>();
        FlattenInto(Spec.Nodes, result);
        return result;
    }

    /// <summary>
    /// Returns every node (groups and modifiers) in pre-order. This is the order the
    /// panel renders; the runtime pipeline never sees groups.
    /// </summary>
    public IReadOnlyList<ModifierNodeSpec> AllNodesInPreOrder()
    {
        var result = new List<ModifierNodeSpec>();
        CollectPreOrder(Spec.Nodes, result);
        return result;
    }

    /// <summary>
    /// Returns the leaf modifiers under a node's subtree (the node itself when it is a
    /// leaf). Groups are never included. Returns an empty list when the node is missing.
    /// </summary>
    public IReadOnlyList<ModifierSpec> FlattenSubtree(Guid nodeId)
    {
        var result = new List<ModifierSpec>();
        var node = Find(nodeId);
        if (node is ModifierSpec modifier)
        {
            result.Add(modifier);
        }
        else if (node is ModifierGroupSpec group)
        {
            FlattenInto(GetChildren(group), result);
        }

        return result;
    }

    /// <summary>
    /// Returns the flat leaf index of a modifier, or -1 when the node is a group or
    /// does not exist.
    /// </summary>
    public int GetLeafIndex(Guid nodeId)
    {
        var flat = Flatten();
        for (var i = 0; i < flat.Count; i++)
        {
            if (flat[i].NodeId == nodeId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Returns the contiguous flat leaf range a node occupies (1 for a leaf, the
    /// subtree leaf count for a group). Pre-order guarantees subtree leaves are
    /// contiguous. Returns 0 when the node does not exist.
    /// </summary>
    public int GetLeafRangeStart(Guid nodeId, out int count)
    {
        count = 0;
        var node = Find(nodeId);
        if (node is null)
        {
            return 0;
        }

        var flat = Flatten();
        var subtree = FlattenSubtree(nodeId);
        count = subtree.Count;
        if (count == 0)
        {
            return 0;
        }

        var firstLeafId = subtree[0].NodeId;
        for (var i = 0; i < flat.Count; i++)
        {
            if (flat[i].NodeId == firstLeafId)
            {
                return i;
            }
        }

        return 0;
    }

    public ModifierNodeSpec? Find(Guid nodeId)
    {
        return FindInList(Spec.Nodes, nodeId);
    }

    /// <summary>
    /// Adds a node to the stack. When <paramref name="parentNodeId"/> is null the node
    /// is added at the root level; otherwise inside the named group.
    /// </summary>
    public bool TryAddNode(ModifierNodeSpec node, Guid? parentNodeId, int? index, out string error)
    {
        error = string.Empty;
        if (node is null)
        {
            error = "Modifier node is null.";
            return false;
        }

        List<ModifierNodeSpec> siblings;
        if (parentNodeId.HasValue)
        {
            if (Find(parentNodeId.Value) is not ModifierGroupSpec parent)
            {
                error = "Parent group no longer exists.";
                return false;
            }

            siblings = GetChildren(parent);
        }
        else
        {
            siblings = Spec.Nodes;
        }

        var insertIndex = index ?? siblings.Count;
        if (insertIndex < 0 || insertIndex > siblings.Count)
        {
            error = "Insert index is out of range.";
            return false;
        }

        siblings.Insert(insertIndex, node);
        return true;
    }

    /// <summary>
    /// Moves a node to a new parent (null = root level) at <paramref name="newIndex"/>.
    /// The index is interpreted against the destination sibling list after the node has
    /// been removed from its current list. Prevents moving a group into its own subtree.
    /// </summary>
    public bool TryMoveNode(Guid nodeId, Guid? newParentNodeId, int newIndex, out string error)
    {
        error = string.Empty;
        if (Find(nodeId) is not { } node)
        {
            error = "Modifier node no longer exists.";
            return false;
        }

        List<ModifierNodeSpec> newParentList;
        if (newParentNodeId.HasValue)
        {
            if (Find(newParentNodeId.Value) is not ModifierGroupSpec newParent)
            {
                error = "Target group no longer exists.";
                return false;
            }

            if (newParentNodeId.Value == nodeId || IsDescendantOf(newParentNodeId.Value, nodeId))
            {
                error = "A group cannot be moved into itself or one of its children.";
                return false;
            }

            newParentList = GetChildren(newParent);
        }
        else
        {
            newParentList = Spec.Nodes;
        }

        var oldParentList = FindParentList(nodeId);
        if (oldParentList is null)
        {
            error = "Modifier node no longer exists.";
            return false;
        }

        oldParentList.Remove(node);
        if (newIndex < 0)
        {
            newIndex = 0;
        }
        else if (newIndex > newParentList.Count)
        {
            newIndex = newParentList.Count;
        }

        newParentList.Insert(newIndex, node);
        return true;
    }

    /// <summary>
    /// Moves a node one step up (offset -1) or down (offset +1) in the stack, crossing
    /// group boundaries:
    /// <list type="bullet">
    /// <item>an item below a group moves up into the group as its last child,</item>
    /// <item>an item above a group moves down into the group as its first child,</item>
    /// <item>the last child of a group moves down to just after the group,</item>
    /// <item>the first child of a group moves up to just before the group.</item>
    /// </list>
    /// Within a sibling list the item swaps with its immediate neighbor. Every item
    /// except the very first (up) or very last (down) row in the stack can move.
    /// </summary>
    public bool TryMoveRelative(Guid nodeId, int offset, out string error)
    {
        error = string.Empty;
        if (offset == 0)
        {
            error = "Move offset must be -1 or +1.";
            return false;
        }

        if (Find(nodeId) is not { } node)
        {
            error = "Modifier node no longer exists.";
            return false;
        }

        TryGetParentNodeId(nodeId, out var parentNodeId);
        var siblings = GetSiblings(nodeId);
        var siblingIndex = FindSiblingIndex(siblings, nodeId);
        if (siblingIndex < 0)
        {
            error = "Modifier node no longer exists.";
            return false;
        }

        Guid? newParentNodeId;
        int newIndex;
        if (offset < 0)
        {
            if (siblingIndex > 0)
            {
                if (siblings[siblingIndex - 1] is ModifierGroupSpec previousGroup)
                {
                    // Up into the previous group, as its last child.
                    newParentNodeId = previousGroup.NodeId;
                    newIndex = GetChildren(previousGroup).Count;
                }
                else
                {
                    // Swap with the previous sibling.
                    newParentNodeId = parentNodeId;
                    newIndex = siblingIndex - 1;
                }
            }
            else if (parentNodeId.HasValue)
            {
                // First child of a group: exit to just before the group.
                if (!TryResolveGroupExit(parentNodeId.Value, out newParentNodeId, out newIndex))
                {
                    error = "Modifier node no longer exists.";
                    return false;
                }
            }
            else
            {
                error = "Cannot move the modifier step further in that direction.";
                return false;
            }
        }
        else
        {
            if (siblingIndex < siblings.Count - 1)
            {
                if (siblings[siblingIndex + 1] is ModifierGroupSpec nextGroup)
                {
                    // Down into the next group, as its first child.
                    newParentNodeId = nextGroup.NodeId;
                    newIndex = 0;
                }
                else
                {
                    // Swap with the next sibling.
                    newParentNodeId = parentNodeId;
                    newIndex = siblingIndex + 1;
                }
            }
            else if (parentNodeId.HasValue)
            {
                // Last child of a group: exit to just after the group.
                if (!TryResolveGroupExit(parentNodeId.Value, out newParentNodeId, out newIndex))
                {
                    error = "Modifier node no longer exists.";
                    return false;
                }

                newIndex += 1;
            }
            else
            {
                error = "Cannot move the modifier step further in that direction.";
                return false;
            }
        }

        return TryMoveNode(nodeId, newParentNodeId, newIndex, out error);
    }

    private bool TryResolveGroupExit(Guid groupNodeId, out Guid? newParentNodeId, out int newIndex)
    {
        newParentNodeId = null;
        newIndex = 0;
        if (!TryGetParentNodeId(groupNodeId, out var grandParentNodeId))
        {
            return false;
        }

        var parentSiblings = GetSiblings(groupNodeId);
        var groupIndex = FindSiblingIndex(parentSiblings, groupNodeId);
        if (groupIndex < 0)
        {
            return false;
        }

        newParentNodeId = grandParentNodeId;
        newIndex = groupIndex;
        return true;
    }

    private static int FindSiblingIndex(IReadOnlyList<ModifierNodeSpec> siblings, Guid nodeId)
    {
        for (var i = 0; i < siblings.Count; i++)
        {
            if (siblings[i].NodeId == nodeId)
            {
                return i;
            }
        }

        return -1;
    }

    public bool TryRemoveNode(Guid nodeId, out ModifierNodeSpec removed)
    {
        removed = null!;
        var parentList = FindParentList(nodeId);
        if (parentList is null)
        {
            return false;
        }

        for (var i = 0; i < parentList.Count; i++)
        {
            if (parentList[i].NodeId == nodeId)
            {
                removed = parentList[i];
                parentList.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the sibling list (root nodes or a group's children) that contains the
    /// given node, or an empty list if the node does not exist.
    /// </summary>
    public IReadOnlyList<ModifierNodeSpec> GetSiblings(Guid nodeId)
    {
        return FindParentList(nodeId)
            ?? (IReadOnlyList<ModifierNodeSpec>)Array.Empty<ModifierNodeSpec>();
    }

    /// <summary>
    /// Returns the id of the group that directly contains the node, or null when the
    /// node lives at the root level. Returns false when the node does not exist.
    /// </summary>
    public bool TryGetParentNodeId(Guid nodeId, out Guid? parentNodeId)
    {
        parentNodeId = null;
        if (Spec.Nodes.Exists(node => node.NodeId == nodeId))
        {
            return true;
        }

        parentNodeId = FindParentGroupIdIn(Spec.Nodes, nodeId);
        return parentNodeId.HasValue;
    }

    private static Guid? FindParentGroupIdIn(List<ModifierNodeSpec> nodes, Guid nodeId)
    {
        foreach (var node in nodes)
        {
            if (node is not ModifierGroupSpec group)
            {
                continue;
            }

            var children = GetChildren(group);
            if (children.Exists(child => child.NodeId == nodeId))
            {
                return group.NodeId;
            }

            if (FindParentGroupIdIn(children, nodeId) is { } nestedParent)
            {
                return nestedParent;
            }
        }

        return null;
    }

    private List<ModifierNodeSpec>? FindParentList(Guid nodeId)
    {
        return FindParentListIn(Spec.Nodes, nodeId);
    }

    private static List<ModifierNodeSpec>? FindParentListIn(
        List<ModifierNodeSpec> nodes,
        Guid nodeId
    )
    {
        foreach (var node in nodes)
        {
            if (node.NodeId == nodeId)
            {
                return nodes;
            }

            if (node is ModifierGroupSpec group)
            {
                var children = GetChildren(group);
                if (FindParentListIn(children, nodeId) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private bool IsDescendantOf(Guid nodeId, Guid potentialAncestorId)
    {
        // The node is a descendant of the potential ancestor if it is reachable from
        // within the ancestor's subtree.
        var ancestor = Find(potentialAncestorId);
        return ancestor is ModifierGroupSpec group
            && FindInList(GetChildren(group), nodeId) is not null;
    }

    private static void FlattenInto(IEnumerable<ModifierNodeSpec> nodes, List<ModifierSpec> result)
    {
        foreach (var node in nodes)
        {
            if (node is ModifierGroupSpec group)
            {
                FlattenInto(GetChildren(group), result);
            }
            else if (node is ModifierSpec modifier)
            {
                result.Add(modifier);
            }
        }
    }

    private static void CollectPreOrder(
        IEnumerable<ModifierNodeSpec> nodes,
        List<ModifierNodeSpec> result
    )
    {
        foreach (var node in nodes)
        {
            result.Add(node);
            if (node is ModifierGroupSpec group)
            {
                CollectPreOrder(GetChildren(group), result);
            }
        }
    }

    private static ModifierNodeSpec? FindInList(IEnumerable<ModifierNodeSpec> nodes, Guid nodeId)
    {
        foreach (var node in nodes)
        {
            if (node.NodeId == nodeId)
            {
                return node;
            }

            if (
                node is ModifierGroupSpec group
                && FindInList(GetChildren(group), nodeId) is { } child
            )
            {
                return child;
            }
        }

        return null;
    }

    private static List<ModifierNodeSpec> GetChildren(ModifierGroupSpec group)
    {
        return group.Children ??= new List<ModifierNodeSpec>();
    }
}
