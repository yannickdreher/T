using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace T.UI.Services
{
    /// <summary>
    /// Thread-safe Least-Recently-Used (LRU) cache for <see cref="FormattedText"/>.
    /// Purpose: dramatically reduce allocations and GC pressure by reusing previously
    /// created <see cref="FormattedText"/> instances for identical render parameters
    /// (text, font, size, weight, style, color).
    /// 
    /// Characteristics:
    /// - Bounded capacity to avoid unbounded memory growth.
    /// - O(1) lookup via dictionary and O(1) update of recentness via a linked list.
    /// - Allocation-free lookups: the key is a value type (no string concatenation per query).
    /// - Safe for simple concurrent access using an internal lock.
    /// </summary>
    public sealed class FormattedTextCache
    {
        private readonly int _capacity;
        private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _map;
        private readonly LinkedList<CacheEntry> _lru;
        private readonly Lock _lock = new();

        /// <summary>
        /// Value-type cache key: avoids per-lookup string allocations entirely.
        /// Typeface is a struct wrapping interned font data, so equality is cheap.
        /// </summary>
        private readonly record struct CacheKey(string Text, Typeface Typeface, double FontSize, Color Color);

        /// <summary>
        /// Represents a cached entry: the lookup key and the cached <see cref="FormattedText"/>.
        /// </summary>
        private sealed class CacheEntry(CacheKey key, FormattedText value)
        {
            public CacheKey Key { get; } = key;
            public FormattedText Value { get; } = value;
        }

        /// <summary>
        /// Snapshot of cache statistics suitable for UI display / telemetry.
        /// </summary>
        public readonly struct Stats
        {
            public long Hits { get; init; }
            public long Misses { get; init; }
            public long Inserts { get; init; }
            public long Evictions { get; init; }
            public int CurrentCount { get; init; }
            public int Capacity { get; init; }
        }

        // statistics
        private long _hits;
        private long _misses;
        private long _inserts;
        private long _evictions;

        /// <summary>
        /// Create a new <see cref="FormattedTextCache"/>.
        /// </summary>
        /// <param name="capacity">Maximum number of entries the cache will hold. Must be > 0.</param>
        public FormattedTextCache(int capacity = 1024)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

            _capacity = capacity;
            _map = new Dictionary<CacheKey, LinkedListNode<CacheEntry>>(capacity);
            _lru = new LinkedList<CacheEntry>();
        }

        /// <summary>
        /// Retrieve a cached <see cref="FormattedText"/> for the given parameters, or create one
        /// if absent. Access refreshes the entry's recency (moves it to the head of LRU).
        /// Lookup itself performs no heap allocations.
        /// </summary>
        public FormattedText GetOrCreate(string text, Typeface typeface, double fontSize, Color color)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new FormattedText(string.Empty, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.White);
            }

            var key = new CacheKey(text, typeface, fontSize, color);

            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    // Move to front = mark as most recently used (skip if already first)
                    if (!ReferenceEquals(_lru.First, node))
                    {
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                    }
                    _hits++;
                    return node.Value.Value;
                }

                _misses++;

                // Create a new FormattedText and cache it.
                var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, new SolidColorBrush(color));

                var entry = new CacheEntry(key, ft);
                var newNode = new LinkedListNode<CacheEntry>(entry);
                _lru.AddFirst(newNode);
                _map[key] = newNode;

                _inserts++;

                if (_map.Count > _capacity)
                {
                    // Evict least-recently-used entry (tail of linked list).
                    var last = _lru.Last!;
                    _lru.RemoveLast();
                    _map.Remove(last.Value.Key);
                    _evictions++;
                    // Evicted FormattedText becomes eligible for GC.
                }

                return ft;
            }
        }

        /// <summary>
        /// Obtain current cache statistics. Snapshot is consistent at call moment.
        /// Suitable for UI display or telemetry.
        /// </summary>
        /// <returns>Stats snapshot.</returns>
        public Stats GetStats()
        {
            lock (_lock)
            {
                return new Stats
                {
                    Hits = _hits,
                    Misses = _misses,
                    Inserts = _inserts,
                    Evictions = _evictions,
                    CurrentCount = _map.Count,
                    Capacity = _capacity
                };
            }
        }

        /// <summary>
        /// Clear cache and reset statistics.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _lru.Clear();
                _map.Clear();
                _hits = _misses = _inserts = _evictions = 0;
            }
        }
    }
}