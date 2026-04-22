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
    public class TheIntroDbApi
    {
        private readonly ILogger _logger;
        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IItemRepository _itemRepository;

        private const string ApiBaseUrl = "https://api.theintrodb.org/v2/media";
        private const string SubmitApiUrl = "https://api.theintrodb.org/v2/submit";
        private const string MarkerSuffix = "#SA";
        private const int RequestTimeoutMs = 10000;
        private const int MaxRetries = 3;
        private const int RetryDelayMs = 1000;

        public TheIntroDbApi(IHttpClient httpClient, IJsonSerializer jsonSerializer, IItemRepository itemRepository)
        {
            _logger = Plugin.Instance.Logger;
            _httpClient = httpClient;
            _jsonSerializer = jsonSerializer;
            _itemRepository = itemRepository;
        }

        public class TheIntroDbSegment
        {
            public long? start_ms { get; set; }
            public long? end_ms { get; set; }
        }

        public class TheIntroDbResponse
        {
            public int tmdb_id { get; set; }
            public string type { get; set; }
            public List<TheIntroDbSegment> intro { get; set; }
            public List<TheIntroDbSegment> recap { get; set; }
            public List<TheIntroDbSegment> credits { get; set; }
            public List<TheIntroDbSegment> preview { get; set; }
        }

        public class TheIntroDbSubmitRequest
        {
            public int tmdb_id { get; set; }
            public string type { get; set; }
            public string segment { get; set; }
            public double start_sec { get; set; }
            public double end_sec { get; set; }
            public int? season { get; set; }
            public int? episode { get; set; }
            public string imdb_id { get; set; }
        }

        /// <summary>
        /// Returns (intro segment in seconds, succeeded). succeeded=true means the API responded with valid JSON
        /// (even if intro is null). succeeded=false means a network/HTTP error occurred.
        /// </summary>
        public async Task<(IntroDbApi.IntroDbSegment intro, bool succeeded)> GetIntroAsync(string tmdbId, int? season,
            int? episode, CancellationToken cancellationToken)
        {
            var url = $"{ApiBaseUrl}?tmdb_id={tmdbId}";
            if (season.HasValue) url += $"&season={season.Value}";
            if (episode.HasValue) url += $"&episode={episode.Value}";

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
                    _logger.Debug("TheIntroDb - Failed to get response: " + response.StatusCode);
                    return (null, false);
                }

                await using var contentStream = response.Content;
                var result = _jsonSerializer.DeserializeFromStream<TheIntroDbResponse>(contentStream);

                var introSegment = result?.intro?.FirstOrDefault(s => s.start_ms.HasValue && s.end_ms.HasValue);
                if (introSegment == null) return (null, true);

                // Convert milliseconds to seconds
                return (new IntroDbApi.IntroDbSegment
                {
                    start_sec = introSegment.start_ms.Value / 1000.0,
                    end_sec = introSegment.end_ms.Value / 1000.0
                }, true);
            }
            catch (Exception e)
            {
                _logger.Debug("TheIntroDb - Failed to get response: " + e.Message);
                return (null, false);
            }
        }

        public async Task<IntroDbApi.IntroDbSegment> GetIntroWithRetryAsync(string tmdbId, int? season, int? episode,
            CancellationToken cancellationToken)
        {
            for (var attempt = 1; attempt <= MaxRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (intro, succeeded) = await GetIntroAsync(tmdbId, season, episode, cancellationToken)
                    .ConfigureAwait(false);

                if (succeeded) return intro;

                if (attempt < MaxRetries)
                {
                    _logger.Debug($"TheIntroDb - Retry {attempt}/{MaxRetries} for TMDB {tmdbId} S{season:D2}E{episode:D2}");

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

        public async Task<bool> TryApplyTheIntroDbForEpisode(Episode episode, CancellationToken cancellationToken)
        {
            var season = episode.Season;
            if (season?.Series == null) return false;

            var providerIds = season.Series.ProviderIds;
            if (providerIds == null || !providerIds.ContainsKey("Tmdb")) return false;

            var tmdbId = providerIds["Tmdb"];
            if (string.IsNullOrEmpty(tmdbId)) return false;

            var seasonNumber = episode.ParentIndexNumber;
            if (!seasonNumber.HasValue || !episode.IndexNumber.HasValue) return false;

            if (Plugin.ChapterApi.HasIntro(episode)) return true;

            var intro = await GetIntroWithRetryAsync(tmdbId, seasonNumber.Value, episode.IndexNumber.Value,
                cancellationToken).ConfigureAwait(false);

            if (intro == null) return false;

            // Validate duration bounds (15s - 120s)
            var introDurationSec = intro.end_sec - intro.start_sec;
            if (introDurationSec < 15 || introDurationSec > 120 || intro.start_sec < 0 ||
                intro.end_sec <= intro.start_sec)
            {
                _logger.Info(
                    $"TheIntroDb - Rejected invalid intro for {season.Series.Name} S{seasonNumber:D2}E{episode.IndexNumber:D2}: {intro.start_sec}s - {intro.end_sec}s (duration: {introDurationSec:F1}s)");
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
                $"TheIntroDb - Applied intro marker for {season.Series.Name} S{seasonNumber:D2}E{episode.IndexNumber:D2}: {intro.start_sec}s - {intro.end_sec}s");

            return true;
        }

        public async Task<bool> SubmitIntroAsync(string apiKey, int tmdbId, string imdbId, string type,
            int? season, int? episode, double startSec, double endSec, CancellationToken cancellationToken)
        {
            var request = new TheIntroDbSubmitRequest
            {
                tmdb_id = tmdbId,
                type = type,
                segment = "intro",
                start_sec = Math.Round(startSec, 1),
                end_sec = Math.Round(endSec, 1),
                season = season,
                episode = episode,
                imdb_id = imdbId
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
                    ["Authorization"] = $"Bearer {apiKey}"
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

                // 409 = duplicate submission, treat as success
                if ((int)response.StatusCode == 409)
                {
                    _logger.Debug(
                        $"TheIntroDb - Duplicate submission for TMDB {tmdbId} S{season:D2}E{episode:D2}, skipping");
                    return true;
                }

                _logger.Warn(
                    $"TheIntroDb - Submit failed for TMDB {tmdbId} S{season:D2}E{episode:D2}: HTTP {response.StatusCode}");
                return false;
            }
            catch (Exception e)
            {
                _logger.Warn(
                    $"TheIntroDb - Submit failed for TMDB {tmdbId} S{season:D2}E{episode:D2}: {e.Message}");
                return false;
            }
        }

        public async Task<bool> TryPublishIntroForEpisode(Episode episode, string apiKey,
            CancellationToken cancellationToken)
        {
            var season = episode.Season;
            if (season?.Series == null) return false;

            var providerIds = season.Series.ProviderIds;
            if (providerIds == null || !providerIds.ContainsKey("Tmdb")) return false;

            var tmdbIdStr = providerIds["Tmdb"];
            if (string.IsNullOrEmpty(tmdbIdStr) || !int.TryParse(tmdbIdStr, out var tmdbId)) return false;

            var seasonNumber = episode.ParentIndexNumber;
            if (!seasonNumber.HasValue || !episode.IndexNumber.HasValue) return false;

            var introStartTicks = Plugin.ChapterApi.GetIntroStart(episode);
            var introEndTicks = Plugin.ChapterApi.GetIntroEnd(episode);
            if (!introStartTicks.HasValue || !introEndTicks.HasValue) return false;

            var startSec = TimeSpan.FromTicks(introStartTicks.Value).TotalSeconds;
            var endSec = TimeSpan.FromTicks(introEndTicks.Value).TotalSeconds;

            var durationSec = endSec - startSec;
            if (durationSec < 15 || durationSec > 120) return false;

            // Get optional IMDB ID
            string imdbId = null;
            if (providerIds.ContainsKey("Imdb"))
                imdbId = providerIds["Imdb"];

            var result = await SubmitIntroAsync(apiKey, tmdbId, imdbId, "tv", seasonNumber.Value,
                episode.IndexNumber.Value, startSec, endSec, cancellationToken).ConfigureAwait(false);

            if (result)
            {
                _logger.Info(
                    $"TheIntroDb - Published intro for {season.Series.Name} S{seasonNumber:D2}E{episode.IndexNumber:D2}: {startSec:F1}s - {endSec:F1}s");
            }

            return result;
        }
    }
}
