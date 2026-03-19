using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using static StrmIntros.Options.MediaInfoExtractOptions;

namespace StrmIntros.Common
{
    public class IntroDbApi
    {
        private readonly ILogger _logger;
        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IItemRepository _itemRepository;

        private const string ApiBaseUrl = "https://api.introdb.app/segments";
        private const string SubmitApiUrl = "https://api.introdb.app/submit";
        private const string CoverageApiUrl = "https://api.introdb.app/trpc/stats.coverageWithStats";
        private const string MarkerSuffix = "#SA";
        private const int RequestTimeoutMs = 10000;
        private const int MaxRetries = 3;
        private const int RetryDelayMs = 1000;

        public IntroDbApi(IHttpClient httpClient, IJsonSerializer jsonSerializer, IItemRepository itemRepository)
        {
            _logger = Plugin.Instance.Logger;
            _httpClient = httpClient;
            _jsonSerializer = jsonSerializer;
            _itemRepository = itemRepository;
        }

        public class IntroDbSegment
        {
            public double start_sec { get; set; }
            public double end_sec { get; set; }
        }

        public class IntroDbResponse
        {
            public string imdb_id { get; set; }
            public int season { get; set; }
            public int episode { get; set; }
            public IntroDbSegment intro { get; set; }
        }

        /// <summary>
        /// Returns (intro, succeeded). succeeded=true means the API responded with valid JSON
        /// (even if intro is null, meaning no intro data exists). succeeded=false means a
        /// network/HTTP error occurred and the request should be retried.
        /// </summary>
        public async Task<(IntroDbSegment intro, bool succeeded)> GetIntroAsync(string imdbId, int season,
            int episode, CancellationToken cancellationToken)
        {
            var url = $"{ApiBaseUrl}?imdb_id={imdbId}&season={season}&episode={episode}&segment_type=intro";

            var options = new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken,
                AcceptHeader = "application/json",
                BufferContent = true,
                UserAgent = Plugin.Instance.UserAgent,
                TimeoutMs = RequestTimeoutMs
            };

            try
            {
                using var response = await _httpClient.SendAsync(options, "GET").ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Debug("IntroDb - Failed to get response: " + response.StatusCode);
                    return (null, false);
                }

                await using var contentStream = response.Content;
                var result = _jsonSerializer.DeserializeFromStream<IntroDbResponse>(contentStream);

                return (result?.intro, true);
            }
            catch (Exception e)
            {
                _logger.Debug("IntroDb - Failed to get response: " + e.Message);
                return (null, false);
            }
        }

        public async Task<IntroDbSegment> GetIntroWithRetryAsync(string imdbId, int season, int episode,
            CancellationToken cancellationToken)
        {
            for (var attempt = 1; attempt <= MaxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (intro, succeeded) = await GetIntroAsync(imdbId, season, episode, cancellationToken)
                    .ConfigureAwait(false);

                if (succeeded) return intro;

                if (attempt < MaxRetries)
                {
                    _logger.Debug($"IntroDb - Retry {attempt}/{MaxRetries} for {imdbId} S{season:D2}E{episode:D2}");

                    try
                    {
                        await Task.Delay(RetryDelayMs, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        return null;
                    }
                }
            }

            return null;
        }

        public async Task<bool> TryApplyIntroDbForEpisode(Episode episode, CancellationToken cancellationToken)
        {
            var season = episode.Season;
            if (season?.Series == null) return false;

            var providerIds = season.Series.ProviderIds;
            if (providerIds == null || !providerIds.ContainsKey("Imdb")) return false;

            var imdbId = providerIds["Imdb"];
            if (string.IsNullOrEmpty(imdbId)) return false;

            var seasonNumber = episode.ParentIndexNumber;
            if (!seasonNumber.HasValue || !episode.IndexNumber.HasValue) return false;

            if (Plugin.ChapterApi.HasIntro(episode)) return true;

            var intro = await GetIntroWithRetryAsync(imdbId, seasonNumber.Value, episode.IndexNumber.Value,
                cancellationToken).ConfigureAwait(false);

            if (intro == null) return false;

            // Validate duration bounds (15s - 120s)
            var introDurationSec = intro.end_sec - intro.start_sec;
            if (introDurationSec < 15 || introDurationSec > 120 || intro.start_sec < 0 ||
                intro.end_sec <= intro.start_sec)
            {
                _logger.Info(
                    $"IntroDb - Rejected invalid intro for {season.Series.Name} S{seasonNumber:D2}E{episode.IndexNumber:D2}: {intro.start_sec}s - {intro.end_sec}s (duration: {introDurationSec:F1}s)");
                return false;
            }

            var introStartTicks = TimeSpan.FromSeconds(intro.start_sec).Ticks;
            var introEndTicks = TimeSpan.FromSeconds(intro.end_sec).Ticks;

            // Snap start to 0 if within 5 seconds of episode beginning
            if (intro.start_sec <= 5.0)
                introStartTicks = 0;

            var chapters = _itemRepository.GetChapters(episode);
            chapters.RemoveAll(c =>
                c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd);

            chapters.Add(new ChapterInfo
            {
                Name = MarkerType.IntroStart + MarkerSuffix,
                MarkerType = MarkerType.IntroStart,
                StartPositionTicks = introStartTicks
            });
            chapters.Add(new ChapterInfo
            {
                Name = MarkerType.IntroEnd + MarkerSuffix,
                MarkerType = MarkerType.IntroEnd,
                StartPositionTicks = introEndTicks
            });

            chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));
            _itemRepository.SaveChapters(episode.InternalId, chapters);

            if (Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.PersistMediaInfoMode !=
                PersistMediaInfoOption.None.ToString())
            {
                await Plugin.MediaInfoApi.UpdateIntroMarkerInJson(episode).ConfigureAwait(false);
            }

            _logger.Info(
                $"IntroDb - Applied intro marker for {season.Series.Name} S{seasonNumber:D2}E{episode.IndexNumber:D2}: {intro.start_sec}s - {intro.end_sec}s");

            return true;
        }

        public class IntroDbSubmitRequest
        {
            public string imdb_id { get; set; }
            public string segment_type { get; set; }
            public int season { get; set; }
            public int episode { get; set; }
            public double start_sec { get; set; }
            public double end_sec { get; set; }
        }

        public class CoverageEpisode
        {
            public int episode { get; set; }
            public bool has_intro { get; set; }
            public bool has_segment { get; set; }
            public int pending_count { get; set; }
        }

        public class CoverageSeason
        {
            public int season { get; set; }
            public List<CoverageEpisode> episodes { get; set; }
        }

        public class CoverageData
        {
            public string imdb_id { get; set; }
            public List<CoverageSeason> seasons { get; set; }
        }

        public class CoverageResult
        {
            public CoverageData data { get; set; }
        }

        public class CoverageResultWrapper
        {
            public CoverageResult result { get; set; }
        }

        /// <summary>
        /// Fetches show coverage from coverageWithStats tRPC endpoint (no auth required).
        /// Returns a dictionary keyed by "imdbId|season|episode" for O(1) lookup.
        /// Returns empty dictionary on failure (non-blocking).
        /// </summary>
        public async Task<Dictionary<string, CoverageEpisode>> GetCoverageAsync(
            string imdbId, int? tvdbId, CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, CoverageEpisode>();

            var tvdbValue = tvdbId.HasValue ? tvdbId.Value.ToString() : "0";
            var input = Uri.EscapeDataString(
                $"{{\"0\":{{\"imdbId\":\"{imdbId}\",\"tvdbId\":{tvdbValue},\"segmentType\":\"intro\"}}}}");
            var url = $"{CoverageApiUrl}?batch=1&input={input}";

            var options = new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken,
                AcceptHeader = "application/json",
                BufferContent = true,
                UserAgent = Plugin.Instance.UserAgent,
                TimeoutMs = RequestTimeoutMs
            };

            try
            {
                using var response = await _httpClient.SendAsync(options, "GET").ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Debug($"IntroDb - Coverage request failed for {imdbId}: HTTP {response.StatusCode}");
                    return result;
                }

                await using var contentStream = response.Content;
                var wrappers = _jsonSerializer.DeserializeFromStream<List<CoverageResultWrapper>>(contentStream);

                var data = wrappers?.FirstOrDefault()?.result?.data;
                if (data?.seasons == null) return result;

                foreach (var season in data.seasons)
                {
                    if (season.episodes == null) continue;
                    foreach (var ep in season.episodes)
                    {
                        var key = $"{imdbId}|{season.season}|{ep.episode}";
                        result[key] = ep;
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Debug($"IntroDb - Coverage request failed for {imdbId}: {e.Message}");
            }

            return result;
        }

        public async Task<bool> SubmitIntroAsync(string apiKey, string imdbId, int season, int episode,
            double startSec, double endSec, CancellationToken cancellationToken)
        {
            var request = new IntroDbSubmitRequest
            {
                imdb_id = imdbId,
                segment_type = "intro",
                season = season,
                episode = episode,
                start_sec = Math.Round(startSec, 1),
                end_sec = Math.Round(endSec, 1)
            };

            var jsonContent = _jsonSerializer.SerializeToString(request);

            var options = new HttpRequestOptions
            {
                Url = SubmitApiUrl,
                CancellationToken = cancellationToken,
                RequestContent = jsonContent.AsMemory(),
                RequestContentType = "application/json",
                BufferContent = true,
                UserAgent = Plugin.Instance.UserAgent,
                TimeoutMs = RequestTimeoutMs,
                RequestHeaders =
                {
                    ["X-API-Key"] = apiKey
                }
            };

            try
            {
                using var response = await _httpClient.SendAsync(options, "POST").ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK ||
                    response.StatusCode == HttpStatusCode.Created)
                {
                    return true;
                }

                _logger.Warn(
                    $"IntroDb - Submit failed for {imdbId} S{season:D2}E{episode:D2}: HTTP {response.StatusCode}");
                return false;
            }
            catch (Exception e)
            {
                _logger.Warn(
                    $"IntroDb - Submit failed for {imdbId} S{season:D2}E{episode:D2}: {e.Message}");
                return false;
            }
        }

        public async Task<bool> TryPublishIntroForEpisode(Episode episode, string apiKey,
            CancellationToken cancellationToken)
        {
            var season = episode.Season;
            if (season?.Series == null) return false;

            var providerIds = season.Series.ProviderIds;
            if (providerIds == null || !providerIds.ContainsKey("Imdb")) return false;

            var imdbId = providerIds["Imdb"];
            if (string.IsNullOrEmpty(imdbId)) return false;

            var seasonNumber = episode.ParentIndexNumber;
            if (!seasonNumber.HasValue || !episode.IndexNumber.HasValue) return false;

            var introStartTicks = Plugin.ChapterApi.GetIntroStart(episode);
            var introEndTicks = Plugin.ChapterApi.GetIntroEnd(episode);
            if (!introStartTicks.HasValue || !introEndTicks.HasValue) return false;

            var startSec = TimeSpan.FromTicks(introStartTicks.Value).TotalSeconds;
            var endSec = TimeSpan.FromTicks(introEndTicks.Value).TotalSeconds;

            var durationSec = endSec - startSec;
            if (durationSec < 15 || durationSec > 120) return false;

            var result = await SubmitIntroAsync(apiKey, imdbId, seasonNumber.Value,
                episode.IndexNumber.Value, startSec, endSec, cancellationToken).ConfigureAwait(false);

            if (result)
            {
                _logger.Info(
                    $"IntroDb - Published intro for {season.Series.Name} S{seasonNumber:D2}E{episode.IndexNumber:D2}: {startSec:F1}s - {endSec:F1}s");
            }

            return result;
        }

        public async Task<bool> TryApplyIntroDbForSeason(Season season, List<Episode> episodes,
            CancellationToken cancellationToken)
        {
            if (season?.Series == null) return false;

            var providerIds = season.Series.ProviderIds;
            if (providerIds == null || !providerIds.ContainsKey("Imdb")) return false;

            var imdbId = providerIds["Imdb"];
            if (string.IsNullOrEmpty(imdbId)) return false;

            var seasonNumber = episodes.FirstOrDefault()?.ParentIndexNumber;
            if (!seasonNumber.HasValue) return false;

            var episodesWithIntro = new List<(Episode episode, IntroDbSegment intro)>();

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!episode.IndexNumber.HasValue) return false;

                var (intro, succeeded) = await GetIntroAsync(imdbId, seasonNumber.Value, episode.IndexNumber.Value,
                    cancellationToken).ConfigureAwait(false);

                if (!succeeded || intro == null)
                {
                    _logger.Info(
                        $"IntroDb - No entry for {season.Series.Name} S{seasonNumber:D2}E{episode.IndexNumber:D2}, falling back to fingerprinting for season");
                    return false;
                }

                episodesWithIntro.Add((episode, intro));
            }

            foreach (var (episode, intro) in episodesWithIntro)
            {
                if (Plugin.ChapterApi.HasIntro(episode)) continue;

                var introStartTicks = TimeSpan.FromSeconds(intro.start_sec).Ticks;
                var introEndTicks = TimeSpan.FromSeconds(intro.end_sec).Ticks;

                var chapters = _itemRepository.GetChapters(episode);
                chapters.RemoveAll(c =>
                    c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd);

                chapters.Add(new ChapterInfo
                {
                    Name = MarkerType.IntroStart + MarkerSuffix,
                    MarkerType = MarkerType.IntroStart,
                    StartPositionTicks = introStartTicks
                });
                chapters.Add(new ChapterInfo
                {
                    Name = MarkerType.IntroEnd + MarkerSuffix,
                    MarkerType = MarkerType.IntroEnd,
                    StartPositionTicks = introEndTicks
                });

                chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));
                _itemRepository.SaveChapters(episode.InternalId, chapters);

                if (Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.PersistMediaInfoMode !=
                    PersistMediaInfoOption.None.ToString())
                {
                    await Plugin.MediaInfoApi.UpdateIntroMarkerInJson(episode).ConfigureAwait(false);
                }

                _logger.Info(
                    $"IntroDb - Applied intro marker for {season.Series.Name} S{seasonNumber:D2}E{episode.IndexNumber:D2}: {intro.start_sec}s - {intro.end_sec}s");
            }

            _logger.Info(
                $"IntroDb - All {episodes.Count} episodes in {season.Series.Name} S{seasonNumber:D2} covered by IntroDB");
            return true;
        }
    }
}
