namespace MethodCache.Core.Runtime.Memory;

/// <summary>
/// Intrusive doubly linked list ordered from least (head) to most (tail) recently used.
/// Not thread-safe; guarded by the engine's eviction lock.
/// </summary>
internal sealed class AccessOrderDeque
{
    private Node? _head;
    private Node? _tail;

    public Node? First => _head;

    public bool IsEmpty => _head == null;

    public void AddLast(Node node)
    {
        node.Previous = _tail;
        node.Next = null;

        if (_tail == null)
        {
            _head = node;
        }
        else
        {
            _tail.Next = node;
        }

        _tail = node;
    }

    public void Remove(Node node)
    {
        var previous = node.Previous;
        var next = node.Next;

        if (previous == null)
        {
            _head = next;
        }
        else
        {
            previous.Next = next;
        }

        if (next == null)
        {
            _tail = previous;
        }
        else
        {
            next.Previous = previous;
        }

        node.Previous = null;
        node.Next = null;
    }

    public void MoveToEnd(Node node)
    {
        if (node == _tail)
        {
            return;
        }

        Remove(node);
        AddLast(node);
    }

    public void Clear()
    {
        var node = _head;
        while (node != null)
        {
            var next = node.Next;
            node.Previous = null;
            node.Next = null;
            node = next;
        }

        _head = null;
        _tail = null;
    }
}
