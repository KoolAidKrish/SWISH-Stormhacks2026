using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace HandGestureRecognition.Utils;

/// <summary>
/// Minimal insertion-ordered dictionary that mirrors Python 3.7+ dict semantics:
/// iteration follows insertion order, overwriting an existing key keeps its position,
/// and removing then re-adding a key moves it to the end.
/// (System.Collections.Generic.Dictionary does not guarantee order after removals,
/// and the original tracking logic depends on dict order.)
/// </summary>
public sealed class OrderedMap<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    private readonly List<TKey> _order = new();
    private readonly Dictionary<TKey, TValue> _map = new();

    public int Count => _order.Count;

    public TValue this[TKey key]
    {
        get => _map[key];
        set
        {
            if (!_map.ContainsKey(key))
            {
                _order.Add(key);
            }
            _map[key] = value;
        }
    }

    public IReadOnlyList<TKey> Keys => _order;

    public IEnumerable<TValue> Values => _order.Select(k => _map[k]);

    /// <summary>Equivalent of Python's next(iter(reversed(d))).</summary>
    public TKey LastKey => _order[_order.Count - 1];

    public bool ContainsKey(TKey key) => _map.ContainsKey(key);

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => _map.TryGetValue(key, out value);

    public bool Remove(TKey key)
    {
        if (_map.Remove(key))
        {
            _order.Remove(key);
            return true;
        }
        return false;
    }

    public void Clear()
    {
        _order.Clear();
        _map.Clear();
    }

    public KeyValuePair<TKey, TValue> ElementAt(int index)
    {
        var key = _order[index];
        return new KeyValuePair<TKey, TValue>(key, _map[key]);
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        foreach (var key in _order)
        {
            yield return new KeyValuePair<TKey, TValue>(key, _map[key]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
