using System.Collections.Concurrent;

namespace MethodCache.Core.Runtime.Memory;

// Sparse tags (notably HTTP URL slots) usually contain only a few entries. Immutable small
// snapshots avoid a bucket array and processor-sized lock array for each such tag. A busy
// tag promotes atomically to the same scalable concurrent dictionary used by the engine.
internal sealed class TagNodeSet
{
    private const int SmallLimit = 4;
    private object _state = Array.Empty<Node>();

    internal bool IsEmpty
    {
        get
        {
            var state = Volatile.Read(ref _state);
            return state is Node[] nodes ? nodes.Length == 0 : ((ConcurrentDictionary<Node, byte>)state).IsEmpty;
        }
    }

    internal ICollection<Node> Keys
    {
        get
        {
            var state = Volatile.Read(ref _state);
            return state is Node[] nodes ? nodes : ((ConcurrentDictionary<Node, byte>)state).Keys;
        }
    }

    internal bool Add(Node node)
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (state is ConcurrentDictionary<Node, byte> dictionary) return dictionary.TryAdd(node, 0);
            var nodes = (Node[])state;
            foreach (var existing in nodes)
                if (ReferenceEquals(existing, node)) return false;

            object updated;
            if (nodes.Length < SmallLimit)
            {
                var copy = new Node[nodes.Length + 1];
                nodes.CopyTo(copy, 0);
                copy[^1] = node;
                updated = copy;
            }
            else
            {
                var promoted = new ConcurrentDictionary<Node, byte>(ReferenceEqualityComparer.Instance);
                foreach (var existing in nodes) promoted.TryAdd(existing, 0);
                promoted.TryAdd(node, 0);
                updated = promoted;
            }
            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, updated, state), state)) return true;
        }
    }

    internal bool Remove(Node node)
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (state is ConcurrentDictionary<Node, byte> dictionary) return dictionary.TryRemove(node, out _);
            var nodes = (Node[])state;
            var index = 0;
            while (index < nodes.Length && !ReferenceEquals(nodes[index], node)) index++;
            if (index == nodes.Length) return false;
            var copy = nodes.Length == 1 ? Array.Empty<Node>() : new Node[nodes.Length - 1];
            nodes.AsSpan(0, index).CopyTo(copy);
            nodes.AsSpan(index + 1).CopyTo(copy.AsSpan(index));
            if (ReferenceEquals(Interlocked.CompareExchange(ref _state, copy, state), state)) return true;
        }
    }
}
