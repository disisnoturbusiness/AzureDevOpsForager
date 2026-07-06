using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AzureDevOpsForager.Core.Services.Utilities;
/// <summary>
/// A persistent, multi-level cache for query/answer pairs (typically expensive
/// language-model responses) that we would rather not pay to compute twice.
///
/// Lookups are attempted in order of decreasing precision:
///   1. Exact match on the raw query string.
///   2. Normalized match (case-insensitive, whitespace-trimmed) so that
///      cosmetically different phrasings of the same question still hit.
///
/// The whole cache is serialized to a single JSON file so it survives across
/// process restarts, and hit/miss counters let callers reason about how well
/// the cache is actually paying for itself.
/// </summary>
public class SmartCache
{
   #region Data Members

   /// <summary>
   /// Absolute path to the JSON file that backs this cache on disk. Resolved
   /// once in the constructor and treated as immutable for the object's life.
   /// </summary>
   private readonly string _cacheFile;

   /// <summary>
   /// Primary index keyed by the exact, unmodified query string. Backs the
   /// Level 1 (exact) lookup and is also the authoritative set of entries
   /// that gets persisted to disk.
   /// </summary>
   private Dictionary<string, CacheEntry> _cache;

   /// <summary>
   /// Secondary index keyed by the normalized form of each query. Backs the
   /// Level 2 (case-insensitive, trimmed) lookup. Entries are shared by
   /// reference with <see cref="_cache"/>, so hit-count updates apply to both.
   /// </summary>
   private Dictionary<string, CacheEntry> _normalizedCache;

   #endregion

   #region Constructor

   /// <summary>
   /// Creates the cache and immediately loads any previously persisted entries
   /// from disk so it is usable right away.
   /// </summary>
   /// <param name="cacheFile">
   /// Optional override for the backing file path. When null, the cache is
   /// stored as "smart_cache.json" under the application's local-app-data root.
   /// </param>
   public SmartCache(string cacheFile = null)
   {
      _cacheFile = cacheFile ?? Path.Combine( Config.LocalAppDataRoot, "smart_cache.json" );
      Load();
   }

   #endregion

   #region Public Methods

   /// <summary>
   /// Number of lookups that were satisfied from the cache (either level).
   /// </summary>
   public int HitCount { get; private set; }

   /// <summary>
   /// Number of lookups that found nothing and had to fall through to the caller.
   /// </summary>
   public int MissCount { get; private set; }

   /// <summary>
   /// Fraction of lookups that were hits, in the range 0..1. Returns 0 before
   /// any lookups have happened to avoid a divide-by-zero.
   /// </summary>
   public double HitRate => (HitCount + MissCount) > 0 ? (double)HitCount / (HitCount + MissCount) : 0;

   /// <summary>
   /// Attempts to retrieve a cached answer for the given query, trying an exact
   /// match first and then a normalized match. On any hit the entry's own hit
   /// count and last-accessed timestamp are bumped and the cache is re-saved so
   /// usage statistics survive a restart.
   /// </summary>
   /// <param name="query">The raw query to look up.</param>
   /// <param name="answer">The cached answer when found; otherwise null.</param>
   /// <returns>True if a cached answer was found, false on a miss.</returns>
   public bool TryGet(string query, out string answer)
   {
      // Level 1: exact match on the raw query.
      if( _cache.TryGetValue(query, out var entry) )
      {
         RecordHit( entry );
         answer = entry.Answer;
         return true;
      }

      // Level 2: normalized match (case-insensitive, trimmed) so minor
      // phrasing differences still resolve to the same cached answer.
      var normalizedQuery = Normalize(query);
      if( _normalizedCache.TryGetValue(normalizedQuery, out entry) )
      {
         RecordHit( entry );
         answer = entry.Answer;
         return true;
      }

      MissCount++;
      answer = null;
      return false;
   }

   /// <summary>
   /// Adds (or overwrites) a query/answer pair, registering it in both the
   /// exact and normalized indexes and persisting the result to disk. A fresh
   /// entry starts with a zero hit count.
   /// </summary>
   /// <param name="query">The query to key the answer under.</param>
   /// <param name="answer">The answer to cache.</param>
   public void Add(string query, string answer)
   {
      var entry = new CacheEntry
      {
         Query = query,
         Answer = answer,
         Created = DateTime.Now,
         LastAccessed = DateTime.Now,
         HitCount = 0
      };

      // Register under both keys so either lookup level can find it.
      _cache[query] = entry;
      _normalizedCache[Normalize(query)] = entry;

      Save();
   }

   /// <summary>
   /// Removes a query from both indexes (if present) and persists the change.
   /// The normalized key is derived from the same query so the two indexes
   /// stay in sync.
   /// </summary>
   /// <param name="query">The query to evict.</param>
   public void Remove(string query)
   {
      _cache.Remove(query);
      _normalizedCache.Remove(Normalize(query));
      Save();
   }

   /// <summary>
   /// Empties both indexes and persists the now-empty cache to disk.
   /// </summary>
   public void Clear()
   {
      _cache.Clear();
      _normalizedCache.Clear();
      Save();
   }

   /// <summary>
   /// Produces a point-in-time snapshot of cache health: total entries, the
   /// running hit/miss counters and hit rate, and the ten most-hit queries.
   /// The top-query list is useful for spotting which answers are earning
   /// their keep.
   /// </summary>
   /// <returns>A populated <see cref="CacheStats"/> instance.</returns>
   public CacheStats GetStats()
   {
      return new CacheStats
      {
         TotalEntries = _cache.Count,
         HitCount = HitCount,
         MissCount = MissCount,
         HitRate = HitRate,
         TopQueries = _cache.Values
            .OrderByDescending(e => e.HitCount)
            .Take(10)
            .Select(e => new TopQuery { Query = e.Query, Hits = e.HitCount })
            .ToList()
      };
   }

   #endregion

   #region Private Methods

   /// <summary>
   /// Records a cache hit against both the aggregate counters and the specific
   /// entry, then persists so the updated usage stats are not lost on restart.
   /// Centralizing this keeps the two lookup levels in <see cref="TryGet"/>
   /// behaving identically.
   /// </summary>
   /// <param name="entry">The entry that satisfied the lookup.</param>
   private void RecordHit(CacheEntry entry)
   {
      HitCount++;
      entry.HitCount++;
      entry.LastAccessed = DateTime.Now;
      Save(); // Persist updated hit counts.
   }

   /// <summary>
   /// Reduces a query to its normalized form (lower-cased, trimmed) used as the
   /// Level 2 key. A null query collapses to an empty string so it can still be
   /// used as a dictionary key without throwing.
   /// </summary>
   /// <param name="query">The query to normalize; may be null.</param>
   /// <returns>The normalized key, never null.</returns>
   private string Normalize(string query)
   {
      return query?.ToLowerInvariant().Trim() ?? "";
   }

   /// <summary>
   /// Loads persisted entries from the backing file and rebuilds both indexes.
   /// When the file is missing or empty, starts with empty indexes so the cache
   /// is always in a usable state after construction.
   /// </summary>
   private void Load()
   {
      _cache = new Dictionary<string, CacheEntry>();
      _normalizedCache = new Dictionary<string, CacheEntry>();

      var entries = FileHelper.ReadJson<List<CacheEntry>>( _cacheFile, "SmartCache" );
      if( entries == null || entries.Count == 0 )
         return;

      // Populate both indexes with overwrite-safe indexer assignment (last-wins), mirroring Add.
      // Using ToDictionary here would throw ArgumentException the moment two persisted entries
      // share an exact query or normalize to the same key, and this runs in the constructor.
      foreach( var entry in entries )
      {
         _cache[entry.Query] = entry;
         _normalizedCache[Normalize( entry.Query )] = entry;
      }
   }

   /// <summary>
   /// Persists the authoritative set of entries (the exact-match index's
   /// values) to the backing JSON file. The normalized index is not written
   /// separately because it is rebuilt from the same entries on load.
   /// </summary>
   private void Save()
   {
      FileHelper.WriteJson( _cacheFile, _cache.Values, true, "SmartCache" );
   }

   #endregion
}

/// <summary>
/// A single cached query/answer pair plus the bookkeeping used to rank and
/// age entries: when it was created, when it was last read, and how often.
/// </summary>
public class CacheEntry
{
   /// <summary>The original query text this entry answers.</summary>
   public string Query { get; set; }

   /// <summary>The cached answer returned for the query.</summary>
   public string Answer { get; set; }

   /// <summary>UTC-agnostic local timestamp of when the entry was first added.</summary>
   public DateTime Created { get; set; }

   /// <summary>Local timestamp of the most recent successful lookup.</summary>
   public DateTime LastAccessed { get; set; }

   /// <summary>How many times this specific entry has satisfied a lookup.</summary>
   public int HitCount { get; set; }
}

/// <summary>
/// A read-only snapshot of cache performance, returned by
/// <see cref="SmartCache.GetStats"/> for reporting and diagnostics.
/// </summary>
public class CacheStats
{
   /// <summary>Total number of distinct entries currently cached.</summary>
   public int TotalEntries { get; set; }

   /// <summary>Aggregate number of hits across all lookups.</summary>
   public int HitCount { get; set; }

   /// <summary>Aggregate number of misses across all lookups.</summary>
   public int MissCount { get; set; }

   /// <summary>Hit rate in the range 0..1 at the moment the snapshot was taken.</summary>
   public double HitRate { get; set; }

   /// <summary>The most-frequently-hit queries, ordered by hit count.</summary>
   public List<TopQuery> TopQueries { get; set; }
}

/// <summary>
/// A lightweight (query, hit-count) pair used in the top-queries leaderboard
/// within <see cref="CacheStats"/>.
/// </summary>
public class TopQuery
{
   /// <summary>The query text.</summary>
   public string Query { get; set; }

   /// <summary>Number of times this query has been served from the cache.</summary>
   public int Hits { get; set; }
}
