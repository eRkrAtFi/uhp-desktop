using System.Collections.Generic;

namespace QemuManager.Services;

/// <summary>A fixed-capacity FIFO buffer - the oldest entry is silently
/// dropped once <see cref="Capacity"/> is reached, rather than growing
/// without bound. Used for per-VM CPU history (VmService) - a plain
/// List&lt;T&gt; would otherwise grow for as long as a VM keeps running,
/// which for a long-lived VM is exactly the unbounded-memory-growth
/// mistake a "last hour" view should never need to think about avoiding
/// itself.</summary>
public class RingBuffer<T>
{
    private readonly T[] _items;
    private int _start;
    private int _count;

    public int Capacity { get; }

    public RingBuffer(int capacity)
    {
        Capacity = capacity;
        _items = new T[capacity];
    }

    public void Add(T item)
    {
        var index = (_start + _count) % Capacity;
        _items[index] = item;
        if (_count < Capacity) _count++;
        else _start = (_start + 1) % Capacity; // full: overwritten slot's old owner is the new oldest
    }

    /// <summary>Oldest to newest.</summary>
    public IReadOnlyList<T> ToList()
    {
        var result = new List<T>(_count);
        for (var i = 0; i < _count; i++)
            result.Add(_items[(_start + i) % Capacity]);
        return result;
    }
}
