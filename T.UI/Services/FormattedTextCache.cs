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
    /// (text, font, size, weight, color).
    /// 
    /// Characteristics:
    /// - Bounded capacity to avoid unbounded memory growth.
    /// - O(1) lookup via dictionary and O(1) update of recentness via a linked list.
    /// - Safe for simple concurrent access using an internal lock.
    /// 
    /// Tuning:
    /// - Increase capacity for workloads with many unique strings (more memory, fewer misses).
    /// - Decrease capacity to reduce memory footprint; accepts more cache churn.
    /// </summary>
    public sealed class FormattedTextCache
    {
        private readonly int _capacity;
        private readonly Dictionary<string, LinkedListNode<CacheEntry>> _map;
        private readonly LinkedList<CacheEntry> _lru;
        private readonly Lock _lock = new();

        /// <summary>
        /// Represents a cached entry: the lookup key and the cached <see cref="FormattedText"/>.
        /// </summary>
        /// <param name="key">Unique key identifying the text + render parameters.</param>
        /// <param name="value">The cached <see cref="FormattedText"/> instance.</param>
        private sealed class CacheEntry(string key, FormattedText value)
        {
            /// <summary>
            /// Cache lookup key (composed of text + font + size + weight + color).
            /// </summary>
            public string Key { get; } = key;

            /// <summary>
            /// Cached FormattedText instance.
            /// </summary>
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
            _map = new Dictionary<string, LinkedListNode<CacheEntry>>(capacity);
            _lru = new LinkedList<CacheEntry>();
        }

        /// <summary>
        /// Build a compact, stable cache key from the text and rendering parameters.
        /// The key includes the raw text, font family, font size, bold flag and color.
        /// Using a single string key keeps dictionary lookups efficient and deterministic.
        /// </summary>
        /// <param name="text">Rendered text.</param>
        /// <param name="fontFamily">Font family name.</param>
        /// <param name="fontSize">Font size.</param>
        /// <param name="bold">True if bold weight.</param>
        /// <param name="color">Foreground color value.</param>
        /// <returns>Concatenated string key suitable for dictionary lookup.</returns>
        private static string MakeKey(string text, string fontFamily, double fontSize, bool bold, Color color)
        {
            // Use a separator that is unlikely to appear in normal terminal text
            return string.Concat(text, '\u001F', fontFamily, '\u001F', fontSize.ToString(CultureInfo.InvariantCulture),
                                 '\u001F', bold ? "1" : "0", '\u001F', color.ToUInt32().ToString());
        }

        /// <summary>
        /// Retrieve a cached <see cref="FormattedText"/> for the given parameters, or create one
        /// if absent. Access refreshes the entry's recency (moves it to the head of LRU).
        /// 
        /// Thread-safety: method is protected by an internal lock to ensure map/list consistency.
        /// </summary>
        /// <param name="text">The string to render.</param>
        /// <param name="typeface">Typeface describing family/weight (bold detection is based on <see cref="Typeface.Weight"/>).</param>
        /// <param name="fontSize">Font size to use for <see cref="FormattedText"/> creation.</param>
        /// <param name="color">Foreground color for the text.</param>
        /// <returns>Existing or newly created <see cref="FormattedText"/> instance.</returns>
        public FormattedText GetOrCreate(string text, Typeface typeface, double fontSize, Color color)
        {
            if (string.IsNullOrEmpty(text))
            {
                // Return a minimal FormattedText for empty strings to keep behavior consistent.
                return new FormattedText(string.Empty, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.White);
            }

            var fontFamily = typeface.FontFamily?.ToString() ?? "default";
            var bold = typeface.Weight == FontWeight.Bold;
            var key = MakeKey(text, fontFamily, fontSize, bold, color);

            lock (_lock)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    // Move to front = mark as most recently used
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    Interlocked.Increment(ref _hits);
                    return node.Value.Value;
                }

                Interlocked.Increment(ref _misses);

                // Create a new FormattedText and cache it.
                var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, new SolidColorBrush(color));

                var entry = new CacheEntry(key, ft);
                var newNode = new LinkedListNode<CacheEntry>(entry);
                _lru.AddFirst(newNode);
                _map[key] = newNode;

                Interlocked.Increment(ref _inserts);

                if (_map.Count > _capacity)
                {
                    // Evict least-recently-used entry (tail of linked list).
                    var last = _lru.Last!;
                    _lru.RemoveLast();
                    _map.Remove(last.Value.Key);
                    Interlocked.Increment(ref _evictions);
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