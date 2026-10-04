using System.Collections;

namespace HandGestureRecognition.Utils;

/// <summary>
/// Equivalent of Python's collections.deque(maxlen=N): appending past capacity
/// silently drops the oldest element. Iteration is oldest → newest.
/// </summary>
public sealed class BoundedQueue<T> : IReadOnlyCollection<T>
{
    private readonly Queue<T> _queue;

    public int MaxLength { get; }

    public int Count => _queue.Count;

    public BoundedQueue(int maxLength)
    {
        if (maxLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        }
        MaxLength = maxLength;
        _queue = new Queue<T>(maxLength + 1);
    }

    /// <summary>Copy constructor (stands in for copy.deepcopy on value-type elements).</summary>
    public BoundedQueue(BoundedQueue<T> other)
    {
        MaxLength = other.MaxLength;
        _queue = new Queue<T>(other._queue);
    }

    public void Enqueue(T item)
    {
        _queue.Enqueue(item);
        while (_queue.Count > MaxLength)
        {
            _queue.Dequeue();
        }
    }

    public T First() => _queue.Peek();

    public IEnumerator<T> GetEnumerator() => _queue.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
