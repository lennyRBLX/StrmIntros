// Custom chromaprint matching adapted from intro-skipper-10.11's ChromaprintAnalyzer.
// Replaces Emby's broken reflected GetAllFingerprintFilesForSeason + UpdateSequencesForSeason
// which silently returns 0 fingerprints for strm files.
//
// Algorithm: inverted-index search + XOR bit-distance comparison on raw chromaprint uint[] data.
// Reference: https://github.com/intro-skipper (GPL-3.0)

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace StrmIntros.Common
{
    public class ChromaprintMatcher
    {
        /// <summary>
        /// Seconds of audio in one fingerprint point.
        /// This value is defined by the Chromaprint library and should not be changed.
        /// </summary>
        private const double SamplesToSeconds = 0.1238;

        /// <summary>
        /// Maximum number of bits (out of 32) that can differ between two fingerprint points
        /// for them to be considered a match. 6/32 = 81% similarity threshold.
        /// </summary>
        private const int MaxFingerprintPointDifferences = 6;

        /// <summary>
        /// Maximum gap in seconds between contiguous matching timestamps.
        /// </summary>
        private const double MaximumTimeSkip = 3.5;

        /// <summary>
        /// Tolerance for fuzzy matching in the inverted index search.
        /// </summary>
        private const int InvertedIndexShift = 2;

        /// <summary>
        /// Minimum intro duration in seconds.
        /// </summary>
        private const int MinimumIntroDuration = 15;

        /// <summary>
        /// Maximum intro duration in seconds.
        /// </summary>
        private const int MaximumIntroDuration = 120;

        /// <summary>
        /// If intro starts within this many seconds of the episode beginning, snap it to 0.
        /// </summary>
        private const double StartSnapThreshold = 5.0;

        /// <summary>
        /// Outward search window for chapter boundary snapping (seconds).
        /// </summary>
        private const double ChapterSnapOutward = 2.0;

        /// <summary>
        /// Inward search window for chapter boundary snapping (seconds).
        /// </summary>
        private const double ChapterSnapInward = 5.0;

        private readonly ILogger _logger;
        private readonly IItemRepository _itemRepository;

        public ChromaprintMatcher(ILogger logger, IItemRepository itemRepository)
        {
            _logger = logger;
            _itemRepository = itemRepository;
        }

        /// <summary>
        /// Matches fingerprints across all episodes in a season and returns detected intro timestamps.
        /// </summary>
        /// <param name="episodes">All episodes in the season.</param>
        /// <param name="fingerprintLength">Expected fingerprint length value in the filename pattern.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Dictionary mapping episode InternalId to (startSeconds, endSeconds).</returns>
        public Dictionary<long, (double start, double end)> MatchSeason(
            Episode[] episodes, int fingerprintLength, CancellationToken cancellationToken)
        {
            var results = new Dictionary<long, (double start, double end)>();

            // Deduplicate by episode number FIRST: group variants (e.g. mkv.strm + mp4.strm) and pick
            // one representative per unique episode. This avoids reading duplicate .fp files.
            var episodeGroups = episodes
                .GroupBy(e => (e.ParentIndexNumber ?? -1, e.IndexNumber ?? -1))
                .ToList();

            var representatives = new List<Episode>();
            var variantMap = new Dictionary<long, List<long>>(); // representative ID → all variant IDs

            foreach (var group in episodeGroups)
            {
                var variants = group.ToList();
                var representative = variants[0];
                representatives.Add(representative);
                variantMap[representative.InternalId] = variants.Select(e => e.InternalId).ToList();
            }

            // Read fingerprints only for representatives
            var fingerprintCache = new Dictionary<long, uint[]>();
            foreach (var episode in representatives)
            {
                var fp = ReadFingerprintFile(episode, fingerprintLength);
                if (fp != null && fp.Length > 0)
                {
                    fingerprintCache[episode.InternalId] = fp;
                }
            }

            // Remove representatives without valid fingerprints
            representatives = representatives.Where(e => fingerprintCache.ContainsKey(e.InternalId)).ToList();

            _logger.Info($"ChromaprintMatch - {representatives.Count} unique episodes with fingerprints " +
                         $"(from {episodes.Length} total variants)");

            if (representatives.Count < 2)
            {
                _logger.Info("ChromaprintMatch - Need at least 2 unique episodes for comparison, skipping");
                return results;
            }

            // Inverted index cache (computed once per episode, reused across pairs)
            var invertedIndexCache = new Dictionary<long, Dictionary<uint, int>>();

            // Best intro found per representative episode
            var bestIntros = new Dictionary<long, (double start, double end)>();

            // Cascading pairwise comparison on deduplicated representatives (from intro-skipper)
            var episodeQueue = new List<Episode>(representatives);

            while (episodeQueue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var current = episodeQueue[0];
                episodeQueue.RemoveAt(0);

                var currentFp = fingerprintCache[current.InternalId];

                foreach (var remaining in episodeQueue)
                {
                    var remainingFp = fingerprintCache[remaining.InternalId];

                    var (currentRange, remainingRange) = CompareEpisodes(
                        current.InternalId, currentFp,
                        remaining.InternalId, remainingFp,
                        invertedIndexCache);

                    // Validate the match
                    if (remainingRange.Duration <= 0 || remainingRange.Duration > MaximumIntroDuration)
                        continue;

                    if (currentRange.Duration < MinimumIntroDuration)
                        continue;

                    // Keep longest intro per episode
                    if (!bestIntros.TryGetValue(current.InternalId, out var savedCurrent) ||
                        currentRange.Duration > (savedCurrent.end - savedCurrent.start))
                    {
                        bestIntros[current.InternalId] = (currentRange.Start, currentRange.End);
                    }

                    if (!bestIntros.TryGetValue(remaining.InternalId, out var savedRemaining) ||
                        remainingRange.Duration > (savedRemaining.end - savedRemaining.start))
                    {
                        bestIntros[remaining.InternalId] = (remainingRange.Start, remainingRange.End);
                    }

                    // Break after first valid match for this episode (from intro-skipper)
                    break;
                }
            }

            // Build lookup for representative episodes
            var representativeMap = new Dictionary<long, Episode>();
            foreach (var ep in representatives)
                representativeMap[ep.InternalId] = ep;

            // Apply boundary refinement and propagate results to all variants
            foreach (var kvp in bestIntros)
            {
                if (!representativeMap.TryGetValue(kvp.Key, out var representative)) continue;

                var (start, end) = RefineBoundaries(representative, kvp.Value.start, kvp.Value.end);

                // Final duration validation
                var duration = end - start;
                if (duration < MinimumIntroDuration || duration > MaximumIntroDuration)
                    continue;

                // Apply to all variants of this episode
                var variantIds = variantMap.TryGetValue(kvp.Key, out var ids) ? ids : new List<long> { kvp.Key };
                foreach (var variantId in variantIds)
                {
                    results[variantId] = (start, end);
                }

                _logger.Info($"ChromaprintMatch - Detected intro for " +
                             $"{representative.FindSeriesName()} S{representative.ParentIndexNumber:D2}E{representative.IndexNumber:D2}: " +
                             $"{start:F1}s - {end:F1}s ({duration:F1}s) [{variantIds.Count} variant(s)]");
            }

            return results;
        }

        /// <summary>
        /// Reads a raw chromaprint fingerprint file (.fp) for an episode.
        /// Files are raw binary uint32 arrays (4 bytes per point, little-endian).
        /// </summary>
        private uint[] ReadFingerprintFile(Episode episode, int fingerprintLength)
        {
            try
            {
                var metadataPath = episode.GetInternalMetadataPath();
                if (!Directory.Exists(metadataPath))
                    return null;

                var pattern = $"title_{fingerprintLength}_*.fp";
                var fpFile = Directory.EnumerateFiles(metadataPath, pattern).FirstOrDefault();

                if (fpFile == null)
                {
                    // Try any title_*.fp file as fallback
                    fpFile = Directory.EnumerateFiles(metadataPath, "title_*.fp").FirstOrDefault();
                    if (fpFile != null)
                    {
                        _logger.Debug($"ChromaprintMatch - No title_{fingerprintLength}_*.fp found for " +
                                      $"{episode.Name}, using fallback: {Path.GetFileName(fpFile)}");
                    }
                }

                if (fpFile == null)
                    return null;

                var bytes = File.ReadAllBytes(fpFile);
                if (bytes.Length < 4)
                {
                    _logger.Warn($"ChromaprintMatch - Deleting unusable .fp file ({bytes.Length} bytes): {fpFile}");
                    try { File.Delete(fpFile); } catch { /* ignored */ }
                    return null;
                }

                // Truncate to nearest 4-byte boundary (trailing bytes from header/padding are ignored)
                var usableLength = bytes.Length - (bytes.Length % 4);
                if (usableLength != bytes.Length)
                {
                    _logger.Debug($"ChromaprintMatch - .fp file has {bytes.Length % 4} trailing bytes, " +
                                  $"truncating {bytes.Length} → {usableLength} bytes: {Path.GetFileName(fpFile)}");
                }

                var count = usableLength / 4;
                var result = new uint[count];
                Buffer.BlockCopy(bytes, 0, result, 0, usableLength);

                return result;
            }
            catch (Exception e)
            {
                _logger.Debug($"ChromaprintMatch - Failed to read fingerprint for {episode.Name}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Compares two episodes' fingerprints using inverted-index search.
        /// Returns the best matching time range for each episode.
        /// </summary>
        private (TimeRange Lhs, TimeRange Rhs) CompareEpisodes(
            long lhsId, uint[] lhsPoints,
            long rhsId, uint[] rhsPoints,
            Dictionary<long, Dictionary<uint, int>> indexCache)
        {
            var (lhsRanges, rhsRanges) = SearchInvertedIndex(
                lhsId, lhsPoints, rhsId, rhsPoints, indexCache);

            if (lhsRanges.Count > 0)
            {
                return GetLongestTimeRange(lhsRanges, rhsRanges);
            }

            return (new TimeRange(), new TimeRange());
        }

        /// <summary>
        /// Search for shared audio using inverted indexes.
        /// For each matching point found, calculate alignment shifts and find contiguous regions.
        /// </summary>
        private (List<TimeRange> Lhs, List<TimeRange> Rhs) SearchInvertedIndex(
            long lhsId, uint[] lhsPoints,
            long rhsId, uint[] rhsPoints,
            Dictionary<long, Dictionary<uint, int>> indexCache)
        {
            var lhsRanges = new List<TimeRange>();
            var rhsRanges = new List<TimeRange>();

            var lhsIndex = GetOrCreateInvertedIndex(lhsId, lhsPoints, indexCache);
            var rhsIndex = GetOrCreateInvertedIndex(rhsId, rhsPoints, indexCache);
            var indexShifts = new HashSet<int>();

            // For all audio points in LHS, check if RHS has a matching point (with shift tolerance)
            foreach (var kvp in lhsIndex)
            {
                var originalPoint = kvp.Key;

                for (var i = -InvertedIndexShift; i <= InvertedIndexShift; i++)
                {
                    var modifiedPoint = (uint)(originalPoint + i);

                    if (rhsIndex.TryGetValue(modifiedPoint, out var rhsPosition))
                    {
                        var lhsPosition = lhsIndex[originalPoint];
                        indexShifts.Add(rhsPosition - lhsPosition);
                    }
                }
            }

            // Use all discovered shifts to compare the episodes
            foreach (var shift in indexShifts)
            {
                var (lhsContiguous, rhsContiguous) = FindContiguous(lhsPoints, rhsPoints, shift);
                if (lhsContiguous.End > 0 && rhsContiguous.End > 0)
                {
                    lhsRanges.Add(lhsContiguous);
                    rhsRanges.Add(rhsContiguous);
                }
            }

            return (lhsRanges, rhsRanges);
        }

        /// <summary>
        /// XOR-compares aligned fingerprint arrays at a given shift and finds contiguous matching regions.
        /// </summary>
        private static (TimeRange Lhs, TimeRange Rhs) FindContiguous(
            uint[] lhs, uint[] rhs, int shiftAmount)
        {
            var leftOffset = 0;
            var rightOffset = 0;

            if (shiftAmount < 0)
            {
                leftOffset -= shiftAmount;
            }
            else
            {
                rightOffset += shiftAmount;
            }

            var lhsTimes = new List<double>();
            var rhsTimes = new List<double>();
            var upperLimit = Math.Min(lhs.Length, rhs.Length) - Math.Abs(shiftAmount);

            for (var i = 0; i < upperLimit; i++)
            {
                var lhsPosition = i + leftOffset;
                var rhsPosition = i + rightOffset;
                var diff = lhs[lhsPosition] ^ rhs[rhsPosition];

                if (CountBits(diff) > MaxFingerprintPointDifferences)
                    continue;

                lhsTimes.Add(lhsPosition * SamplesToSeconds);
                rhsTimes.Add(rhsPosition * SamplesToSeconds);
            }

            // Sentinel values to ensure the last range is captured
            lhsTimes.Add(double.MaxValue);
            rhsTimes.Add(double.MaxValue);

            var lContiguous = FindLongestContiguous(lhsTimes, MaximumTimeSkip);
            if (lContiguous == null || lContiguous.Duration < MinimumIntroDuration)
            {
                return (new TimeRange(), new TimeRange());
            }

            var rContiguous = FindLongestContiguous(rhsTimes, MaximumTimeSkip);
            if (rContiguous == null)
            {
                return (new TimeRange(), new TimeRange());
            }

            return (lContiguous, rContiguous);
        }

        /// <summary>
        /// Finds the longest contiguous time range from timestamps.
        /// Input is already sorted (populated in increasing index order) with a sentinel at the end.
        /// Ported from intro-skipper's TimeRangeHelpers.FindContiguous.
        /// </summary>
        private static TimeRange FindLongestContiguous(List<double> times, double maximumDistance)
        {
            if (times.Count == 0)
                return null;

            TimeRange longest = null;
            var currentStart = times[0];
            var currentEnd = times[0];

            for (var i = 0; i < times.Count - 1; i++)
            {
                var next = times[i + 1];

                if (next - times[i] <= maximumDistance)
                {
                    currentEnd = next;
                    continue;
                }

                var duration = currentEnd - currentStart;
                if (longest == null || duration > longest.Duration)
                {
                    longest = new TimeRange(currentStart, currentEnd);
                }

                currentStart = next;
                currentEnd = next;
            }

            return longest;
        }

        /// <summary>
        /// Selects the longest time range pair from the collected ranges.
        /// </summary>
        private static (TimeRange Lhs, TimeRange Rhs) GetLongestTimeRange(
            List<TimeRange> lhsRanges, List<TimeRange> rhsRanges)
        {
            // Find the index with the longest LHS duration
            var bestIndex = 0;
            for (var i = 1; i < lhsRanges.Count; i++)
            {
                if (lhsRanges[i].Duration > lhsRanges[bestIndex].Duration)
                    bestIndex = i;
            }

            var lhsIntro = lhsRanges[bestIndex];
            var rhsIntro = rhsRanges[bestIndex];

            // Snap start to 0 if near episode beginning
            if (lhsIntro.Start <= StartSnapThreshold)
                lhsIntro.Start = 0;

            if (rhsIntro.Start <= StartSnapThreshold)
                rhsIntro.Start = 0;

            return (lhsIntro, rhsIntro);
        }

        /// <summary>
        /// Gets or creates an inverted index for an episode's fingerprint.
        /// Maps each fingerprint point value to its last occurrence index.
        /// </summary>
        private static Dictionary<uint, int> GetOrCreateInvertedIndex(
            long episodeId, uint[] fingerprint,
            Dictionary<long, Dictionary<uint, int>> cache)
        {
            if (cache.TryGetValue(episodeId, out var cached))
                return cached;

            var invIndex = new Dictionary<uint, int>();
            for (int i = 0; i < fingerprint.Length; i++)
            {
                invIndex[fingerprint[i]] = i;
            }

            cache[episodeId] = invIndex;
            return invIndex;
        }

        /// <summary>
        /// Counts the number of set bits in a uint32 value.
        /// Uses the Hamming weight algorithm (Brian Kernighan's method).
        /// </summary>
        private static int CountBits(uint number)
        {
            // Parallel bit count (Hamming weight)
            number = number - ((number >> 1) & 0x55555555u);
            number = (number & 0x33333333u) + ((number >> 2) & 0x33333333u);
            number = (number + (number >> 4)) & 0x0F0F0F0Fu;
            return (int)((number * 0x01010101u) >> 24);
        }

        /// <summary>
        /// Refines intro boundaries using chapter alignment and start-snap.
        /// </summary>
        private (double start, double end) RefineBoundaries(
            Episode episode, double rawStart, double rawEnd)
        {
            var start = rawStart;
            var end = rawEnd;

            // Fetch chapters once for both boundary searches
            List<ChapterInfo> chapters = null;
            try
            {
                chapters = _itemRepository.GetChapters(episode);
            }
            catch { /* ignored */ }

            // Snap start to 0 if within threshold
            if (start <= StartSnapThreshold)
            {
                start = 0;
            }
            else
            {
                // Try to snap start to a chapter boundary
                var chapterStart = FindNearestChapterBoundary(
                    chapters, start,
                    start - ChapterSnapOutward,
                    start + ChapterSnapInward);

                if (chapterStart.HasValue)
                    start = chapterStart.Value;
            }

            // Try to snap end to a chapter boundary
            var chapterEnd = FindNearestChapterBoundary(
                chapters, end,
                end - ChapterSnapInward,
                end + ChapterSnapOutward);

            if (chapterEnd.HasValue)
                end = chapterEnd.Value;

            // Validation: if refinement broke the range, revert
            if (start >= end)
            {
                _logger.Debug($"ChromaprintMatch - Boundary refinement produced invalid range " +
                              $"({start:F1}s >= {end:F1}s), reverting to original ({rawStart:F1}s - {rawEnd:F1}s)");
                start = rawStart;
                end = rawEnd;

                // Still snap start to 0
                if (start <= StartSnapThreshold)
                    start = 0;
            }

            return (start, end);
        }

        /// <summary>
        /// Finds the nearest chapter boundary within a search window.
        /// Returns null if no chapter boundary is found in the window.
        /// </summary>
        private static double? FindNearestChapterBoundary(
            List<ChapterInfo> chapters, double referenceTime,
            double searchStart, double searchEnd)
        {
            if (chapters == null || chapters.Count == 0)
                return null;

            double? nearest = null;
            var smallestDistance = double.MaxValue;

            foreach (var chapter in chapters)
            {
                if (chapter.MarkerType != MarkerType.Chapter)
                    continue;

                var chapterTimeSec = TimeSpan.FromTicks(chapter.StartPositionTicks).TotalSeconds;

                if (chapterTimeSec < searchStart || chapterTimeSec > searchEnd)
                    continue;

                var distance = Math.Abs(chapterTimeSec - referenceTime);
                if (distance < smallestDistance)
                {
                    smallestDistance = distance;
                    nearest = chapterTimeSec;
                }
            }

            return nearest;
        }

        /// <summary>
        /// Simple time range with start and end in seconds.
        /// </summary>
        internal class TimeRange
        {
            public double Start { get; set; }
            public double End { get; set; }
            public double Duration => End - Start;

            public TimeRange()
            {
                Start = 0;
                End = 0;
            }

            public TimeRange(double start, double end)
            {
                Start = start;
                End = end;
            }

            public TimeRange(TimeRange other)
            {
                Start = other.Start;
                End = other.End;
            }
        }
    }
}
