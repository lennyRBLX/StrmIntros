using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmIntros.Common;
using StrmIntros.Properties;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static StrmIntros.Options.MediaInfoExtractOptions;

namespace StrmIntros.ScheduledTask
{
    public class ExtractIntroFingerprintTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;
        private readonly ITaskManager _taskManager;

        public ExtractIntroFingerprintTask(IFileSystem fileSystem, ITaskManager taskManager)
        {
            _logger = Plugin.Instance.Logger;
            _fileSystem = fileSystem;
            _taskManager = taskManager;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.Info("IntroFingerprintExtract - Scheduled Task Execute");
            
            var pluginOptions = Plugin.Instance.GetPluginOptions();

            var unlockIntroSkip = pluginOptions.IntroSkipOptions.UnlockIntroSkip;
            if (!unlockIntroSkip)
            {
                progress.Report(100.0);
                _ = Plugin.NotificationApi.SendMessageToAdmins(
                    $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] {Resources.IntroDetectionEnhancedNotEnabled}",
                    10000);
                _logger.Warn("Built-in Intro Detection Enhanced is not enabled.");
                _logger.Warn("IntroFingerprintExtract - Scheduled Task Aborted");
                return;
            }

            var maxConcurrentCount = pluginOptions.GeneralOptions.MaxConcurrentCount;
            _logger.Info("Master Max Concurrent Count: " + maxConcurrentCount);
            var cooldownSeconds = maxConcurrentCount == 1
                ? pluginOptions.GeneralOptions.CooldownDurationSeconds
                : (int?)null;
            if (cooldownSeconds.HasValue) _logger.Info("Cooldown Duration Seconds: " + cooldownSeconds.Value);

            var enableIntroDbPublish = pluginOptions.IntroSkipOptions.EnableIntroDbPublish;
            var introDbApiKey = pluginOptions.IntroSkipOptions.IntroDbApiKey;
            if (enableIntroDbPublish && !string.IsNullOrWhiteSpace(introDbApiKey))
                _logger.Info("IntroDb Publish: Enabled");

            var persistMediaInfoMode = pluginOptions.MediaInfoExtractOptions.PersistMediaInfoMode;
            _logger.Info("Persist MediaInfo Mode: " + persistMediaInfoMode);
            var persistMediaInfo = persistMediaInfoMode != PersistMediaInfoOption.None.ToString();
            var mediaInfoRestoreMode = persistMediaInfoMode == PersistMediaInfoOption.Restore.ToString();

            Plugin.FingerprintApi.EnsureLibraryMarkerDetection();
            Plugin.FingerprintApi.UpdateLibraryPathsInScope();

            var preExtractEpisodes = Plugin.FingerprintApi.FetchIntroPreExtractTaskItems();
            var postExtractEpisodes = Plugin.FingerprintApi.FetchIntroFingerprintTaskItems();

            var episodes = preExtractEpisodes.Concat(postExtractEpisodes)
                .GroupBy(e => e.InternalId)
                .Select(g => g.First())
                .ToList();

            _logger.Info($"IntroFingerprintExtract - Total episodes before pre-pass: {episodes.Count}");

            // Marker sync pre-pass: sync intro markers between SQLite DB and mediainfo JSON files
            if (persistMediaInfo && !mediaInfoRestoreMode)
            {
                var syncDirectoryService = new DirectoryService(_logger, _fileSystem);
                var jsonBackfillCount = 0;
                var jsonRestoredCount = 0;

                foreach (var episode in episodes)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.Info("IntroFingerprintExtract - Scheduled Task Cancelled");
                        return;
                    }

                    if (Plugin.ChapterApi.HasIntro(episode))
                    {
                        // DB → JSON: persist existing DB markers to JSON
                        await Plugin.MediaInfoApi.UpdateIntroMarkerInJson(episode).ConfigureAwait(false);
                        jsonBackfillCount++;
                    }
                    else
                    {
                        // JSON → DB: restore markers from JSON if DB is missing them
                        var restored = await Plugin.MediaInfoApi
                            .DeserializeIntroMarker(episode, syncDirectoryService,
                                "IntroFingerprintExtract MarkerSync")
                            .ConfigureAwait(false);
                        if (restored)
                        {
                            jsonRestoredCount++;
                            // Re-serialize to ensure JSON is fully up to date
                            await Plugin.MediaInfoApi.UpdateIntroMarkerInJson(episode).ConfigureAwait(false);
                        }
                    }
                }

                if (jsonBackfillCount > 0 || jsonRestoredCount > 0)
                {
                    _logger.Info(
                        $"IntroFingerprintExtract - Marker sync pre-pass: {jsonBackfillCount} DB→JSON, {jsonRestoredCount} JSON→DB");
                }
            }

            // Pre-pass: skip episodes with missing .strm files or existing intro markers
            var alreadyCompleteCount = 0;
            var strmMissingCount = 0;
            var alreadyCompleteEpisodes = new List<Episode>();
            episodes = episodes.Where(e =>
            {
                if (e.Path != null && e.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                    && !File.Exists(e.Path))
                {
                    strmMissingCount++;
                    return false;
                }
                if (Plugin.ChapterApi.HasIntro(e))
                {
                    alreadyCompleteCount++;
                    alreadyCompleteEpisodes.Add(e);
                    return false;
                }
                return true;
            }).ToList();

            if (strmMissingCount > 0)
            {
                _logger.Info(
                    $"IntroFingerprintExtract - Pre-pass: {strmMissingCount} episodes skipped (missing .strm files)");
            }
            if (alreadyCompleteCount > 0)
            {
                _logger.Info(
                    $"IntroFingerprintExtract - Pre-pass: {alreadyCompleteCount} episodes already have intro markers, skipped");
            }

            // IntroDb publish pre-pass: use coverageWithStats to skip already-covered episodes
            var introDbCoveredKeys = new ConcurrentDictionary<string, bool>();
            var coverageCache = new Dictionary<string, Dictionary<string, IntroDbApi.CoverageEpisode>>();

            if (!mediaInfoRestoreMode && enableIntroDbPublish &&
                !string.IsNullOrWhiteSpace(introDbApiKey) && alreadyCompleteEpisodes.Count > 0)
            {
                var backfillPublishCount = 0;
                var backfillSkipCount = 0;
                var backfillFailCount = 0;

                var publishableEpisodes = alreadyCompleteEpisodes
                    .Where(e => e.Season?.Series?.ProviderIds != null &&
                                e.Season.Series.ProviderIds.ContainsKey("Imdb") &&
                                !string.IsNullOrEmpty(e.Season.Series.ProviderIds["Imdb"]) &&
                                e.ParentIndexNumber.HasValue && e.IndexNumber.HasValue)
                    .ToList();

                _logger.Info(
                    $"IntroFingerprintExtract - IntroDb publish pre-pass: {publishableEpisodes.Count} episodes to check");

                // Group by IMDB ID and fetch coverage per show
                var groupedByShow = publishableEpisodes
                    .GroupBy(e => e.Season.Series.ProviderIds["Imdb"])
                    .ToList();

                foreach (var showGroup in groupedByShow)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var imdbId = showGroup.Key;
                    var firstEp = showGroup.First();
                    int? tvdbId = firstEp.Season?.Series?.ProviderIds != null &&
                                  firstEp.Season.Series.ProviderIds.TryGetValue("Tvdb", out var tvdbStr) &&
                                  int.TryParse(tvdbStr, out var tvdbParsed)
                        ? tvdbParsed
                        : (int?)null;
                    var coverage = await Plugin.IntroDbApi
                        .GetCoverageAsync(imdbId, tvdbId, cancellationToken).ConfigureAwait(false);
                    coverageCache[imdbId] = coverage;

                    var publishSemaphore = new SemaphoreSlim(4, 4);

                    var publishTasks = showGroup.Select(episode => Task.Run(async () =>
                    {
                        try
                        {
                            await publishSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch
                        {
                            return;
                        }

                        try
                        {
                            var seasonNum = episode.ParentIndexNumber.Value;
                            var episodeNum = episode.IndexNumber.Value;
                            var cacheKey = $"{imdbId}|{seasonNum}|{episodeNum}";

                            // Skip if coverage shows already submitted or has segment
                            if (coverage.TryGetValue(cacheKey, out var covEp) &&
                                (covEp.has_segment || covEp.pending_count > 0))
                            {
                                introDbCoveredKeys.TryAdd(cacheKey, true);
                                Interlocked.Increment(ref backfillSkipCount);
                                return;
                            }

                            var published = await Plugin.IntroDbApi
                                .TryPublishIntroForEpisode(episode, introDbApiKey, cancellationToken)
                                .ConfigureAwait(false);

                            if (published)
                            {
                                introDbCoveredKeys.TryAdd(cacheKey, true);
                                Interlocked.Increment(ref backfillPublishCount);
                            }
                            else
                            {
                                Interlocked.Increment(ref backfillFailCount);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // handled via cancellationToken
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(
                                $"IntroDb - Publish failed for {episode.Name}: {e.Message}");
                            Interlocked.Increment(ref backfillFailCount);
                        }
                        finally
                        {
                            publishSemaphore.Release();
                        }
                    }, cancellationToken)).ToList();

                    try
                    {
                        await Task.WhenAll(publishTasks).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.Info("IntroFingerprintExtract - Scheduled Task Cancelled");
                        return;
                    }

                    publishSemaphore.Dispose();
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("IntroFingerprintExtract - Scheduled Task Cancelled");
                    return;
                }

                if (backfillPublishCount > 0 || backfillSkipCount > 0 || backfillFailCount > 0)
                {
                    _logger.Info(
                        $"IntroFingerprintExtract - IntroDb publish pre-pass: {backfillPublishCount} published, {backfillSkipCount} already in IntroDb, {backfillFailCount} failed");
                }
            }

            // Chapter-name pre-pass: detect intros from chapter names (e.g. "OP", "Opening", "Intro")
            if (!mediaInfoRestoreMode)
            {
                var chapterResolvedIds = new HashSet<long>();

                foreach (var episode in episodes)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.Info("IntroFingerprintExtract - Scheduled Task Cancelled");
                        return;
                    }

                    if (await Plugin.ChapterApi.TryDetectIntroFromChapterName(episode).ConfigureAwait(false))
                    {
                        chapterResolvedIds.Add(episode.InternalId);
                    }
                }

                if (chapterResolvedIds.Count > 0)
                {
                    _logger.Info(
                        $"IntroFingerprintExtract - Chapter-name pre-pass: {chapterResolvedIds.Count} episodes resolved from chapter names");
                    episodes = episodes.Where(e => !chapterResolvedIds.Contains(e.InternalId)).ToList();
                }
            }

            _logger.Info($"IntroFingerprintExtract - Total episodes before IntroDb pre-pass: {episodes.Count}");

            // IntroDb pre-pass: use coverageWithStats to find episodes with segments, then fetch only those
            if (!mediaInfoRestoreMode)
            {
                var withImdb = episodes.Where(e =>
                    e.Season?.Series?.ProviderIds != null &&
                    e.Season.Series.ProviderIds.ContainsKey("Imdb") &&
                    !string.IsNullOrEmpty(e.Season.Series.ProviderIds["Imdb"]) &&
                    e.ParentIndexNumber.HasValue && e.IndexNumber.HasValue).ToList();

                var skippedImdbCount = episodes.Count - withImdb.Count;
                if (skippedImdbCount > 0)
                {
                    _logger.Info(
                        $"IntroFingerprintExtract - IntroDb pre-pass: {skippedImdbCount} episodes skipped (no IMDB ID)");
                }

                var resolvedIds = new ConcurrentBag<long>();
                var introDbIndex = 0;
                var introDbTotal = episodes.Count;

                // Group by IMDB ID, fetch coverage per show, then only query episodes with segments
                var fetchGroupedByShow = withImdb
                    .GroupBy(e => e.Season.Series.ProviderIds["Imdb"])
                    .ToList();

                var introDbSemaphore = new SemaphoreSlim(4, 4);

                foreach (var showGroup in fetchGroupedByShow)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var imdbId = showGroup.Key;

                    // Reuse coverage from publish pass if available, otherwise fetch
                    if (!coverageCache.TryGetValue(imdbId, out var coverage))
                    {
                        var fetchFirstEp = showGroup.First();
                        int? fetchTvdbId = fetchFirstEp.Season?.Series?.ProviderIds != null &&
                                           fetchFirstEp.Season.Series.ProviderIds.TryGetValue("Tvdb",
                                               out var fetchTvdbStr) &&
                                           int.TryParse(fetchTvdbStr, out var fetchTvdbParsed)
                            ? fetchTvdbParsed
                            : (int?)null;
                        coverage = await Plugin.IntroDbApi
                            .GetCoverageAsync(imdbId, fetchTvdbId, cancellationToken).ConfigureAwait(false);
                        coverageCache[imdbId] = coverage;
                    }

                    var introDbTasks = showGroup.Select(episode => Task.Run(async () =>
                    {
                        try
                        {
                            await introDbSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch
                        {
                            return;
                        }

                        try
                        {
                            var epCacheKey =
                                $"{imdbId}|{episode.ParentIndexNumber.Value}|{episode.IndexNumber.Value}";

                            // Already covered by publish pre-pass
                            if (introDbCoveredKeys.ContainsKey(epCacheKey))
                            {
                                resolvedIds.Add(episode.InternalId);
                                return;
                            }

                            // Check coverage: only fetch if has_segment is true
                            if (!coverage.TryGetValue(epCacheKey, out var covEp) || !covEp.has_segment)
                            {
                                return;
                            }

                            var applied = await Plugin.IntroDbApi
                                .TryApplyIntroDbForEpisode(episode, cancellationToken)
                                .ConfigureAwait(false);

                            if (applied)
                            {
                                introDbCoveredKeys.TryAdd(epCacheKey, true);
                                resolvedIds.Add(episode.InternalId);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // handled via cancellationToken
                        }
                        catch (Exception e)
                        {
                            _logger.Debug($"IntroDb - Episode lookup failed: {e.Message}");
                        }
                        finally
                        {
                            introDbSemaphore.Release();
                            var current = Interlocked.Increment(ref introDbIndex);
                            progress.Report(5.0 * current / introDbTotal);
                        }
                    }, cancellationToken)).ToList();

                    try
                    {
                        await Task.WhenAll(introDbTasks).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.Info("IntroFingerprintExtract - Scheduled Task Cancelled");
                        return;
                    }
                }

                introDbSemaphore.Dispose();

                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("IntroFingerprintExtract - Scheduled Task Cancelled");
                    return;
                }

                _logger.Info(
                    $"IntroFingerprintExtract - IntroDb pre-pass: {resolvedIds.Count} of {withImdb.Count} episodes resolved");

                if (resolvedIds.Count > 0)
                {
                    var resolvedSet = new HashSet<long>(resolvedIds);
                    episodes = episodes.Where(e => !resolvedSet.Contains(e.InternalId)).ToList();
                }
            }

            var groupedBySeason = episodes.GroupBy(e => e.Season).ToList();

            double totalSeasons = groupedBySeason.Count;
            double totalEpisodes = episodes.Count;

            _logger.Info($"IntroFingerprintExtract - Number of seasons remaining: {totalSeasons}");
            _logger.Info($"IntroFingerprintExtract - Number of episodes remaining: {totalEpisodes}");

            if (totalEpisodes > 0) IsRunning = true;

            var directoryService = new DirectoryService(_logger, _fileSystem);

            var episodeIndex = 0;
            var processedEpisodes = 0;
            var processedSeasons = 0;
            var episodeSkipCount = 0;
            var seasonSkipCount = 0;
            var episodeWeight = !mediaInfoRestoreMode ? 0.76 : 0.95;
            var seasonWeight = !mediaInfoRestoreMode ? 0.19 : 0.0;
            var introDbWeight = 0.05;

            // Process each season sequentially: fingerprint all episodes → analyze → next season
            foreach (var season in groupedBySeason)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("IntroFingerprintExtract - Scheduled Task Cancelled");
                    return;
                }

                var taskSeason = season.Key;

                // Calculate fingerprint length from ALL episodes in the season (not just remaining)
                var allSeasonEpisodes = taskSeason.GetEpisodes(new InternalItemsQuery
                {
                    GroupByPresentationUniqueKey = false,
                    EnableTotalRecordCount = false
                }).Items.OfType<Episode>();
                var seasonFpLength = Plugin.FingerprintApi.CalculateSeasonFingerprintLength(allSeasonEpisodes);

                _logger.Info(
                    $"IntroFingerprintExtract - Processing season: {taskSeason.Path} (fp length: {seasonFpLength} min)");

                // Phase 1: Generate fingerprints for all episodes in this season (concurrent)
                var episodeTasks = new List<Task>();

                foreach (var episode in season)
                {
                    var taskEpisode = episode;

                    try
                    {
                        await QueueManager.MasterSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        QueueManager.MasterSemaphore.Release();
                        _logger.Info("IntroFingerprintExtract - Scheduled Task Cancelled");
                        return;
                    }

                    var taskEpisodeIndex = ++episodeIndex;
                    var task = Task.Run(async () =>
                    {
                        bool? result1 = null;
                        Tuple<string, bool> result2 = null;

                        try
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                _logger.Info("IntroFingerprintExtract - Scheduled Task Cancelled");
                                return;
                            }

                            var deserializeResult = false;

                            if (!Plugin.LibraryApi.HasMediaInfo(taskEpisode))
                            {
                                result1 = await Plugin.LibraryApi
                                    .OrchestrateMediaInfoProcessAsync(taskEpisode, "IntroFingerprintExtract Task",
                                        cancellationToken).ConfigureAwait(false);

                                if (result1 is null)
                                {
                                    if (!mediaInfoRestoreMode)
                                    {
                                        _logger.Info(
                                            $"IntroFingerprintExtract - Episode skipped or non-existent: {taskEpisode.Name} - {taskEpisode.Path}");
                                    }

                                    Interlocked.Increment(ref episodeSkipCount);
                                    return;
                                }
                            }
                            else if (persistMediaInfo)
                            {
                                deserializeResult = await Plugin.MediaInfoApi.DeserializeIntroMarker(taskEpisode,
                                    directoryService, "IntroFingerprintExtract Task").ConfigureAwait(false);
                            }

                            if (!deserializeResult && !Plugin.ChapterApi.HasIntro(taskEpisode))
                            {
                                if (!mediaInfoRestoreMode)
                                {
                                    if (Plugin.FingerprintApi.HasFingerprintFile(taskEpisode, seasonFpLength))
                                    {
                                        _logger.Info(
                                            $"IntroFingerprintExtract - Fingerprint exists, skipping creation: {taskEpisode.Name}");
                                    }
                                    else
                                    {
                                        result2 = await Plugin.FingerprintApi
                                            .CreateTitleFingerprint(taskEpisode, directoryService,
                                                cancellationToken, seasonFpLength)
                                            .ConfigureAwait(false);

                                        if (result2 != null)
                                        {
                                            _logger.Info(
                                                $"IntroFingerprintExtract - Fingerprint {(result2.Item2 ? "created" : "skipped (no new data)")}: {taskEpisode.Name} - {result2.Item1}");
                                        }
                                        else
                                        {
                                            _logger.Warn(
                                                $"IntroFingerprintExtract - Fingerprint creation returned null: {taskEpisode.Name} - {taskEpisode.Path}");
                                        }
                                    }
                                }
                                else
                                {
                                    Interlocked.Increment(ref episodeSkipCount);
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.Info(
                                $"IntroFingerprintExtract - Episode cancelled: {taskEpisode.Name} - {taskEpisode.Path}");
                        }
                        catch (Exception e)
                        {
                            _logger.Error(
                                $"IntroFingerprintExtract - Episode failed: {taskEpisode.Name} - {taskEpisode.Path}");
                            _logger.Error(e.Message);
                            _logger.Debug(e.StackTrace);
                        }
                        finally
                        {
                            if ((result1 is true || result2?.Item2 is true) && cooldownSeconds.HasValue)
                            {
                                try
                                {
                                    await Task.Delay(cooldownSeconds.Value * 1000, cancellationToken)
                                        .ConfigureAwait(false);
                                }
                                catch
                                {
                                    // ignored
                                }
                            }

                            QueueManager.MasterSemaphore.Release();

                            var currentCount = Interlocked.Increment(ref processedEpisodes);
                            var currentProgress = introDbWeight +
                                                  episodeWeight * currentCount / totalEpisodes +
                                                  seasonWeight * processedSeasons / totalSeasons;
                            progress.Report(currentProgress * 100);

                            if (!mediaInfoRestoreMode)
                            {
                                _logger.Info(
                                    $"IntroFingerprintExtract - Episode Progress {currentCount}/{totalEpisodes} - Task {taskEpisodeIndex}: {taskEpisode.Path}");
                            }
                        }
                    }, cancellationToken);
                    episodeTasks.Add(task);
                }

                // Wait for all fingerprints in this season to complete
                await Task.WhenAll(episodeTasks).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("IntroFingerprintExtract - Scheduled Task Cancelled");
                    return;
                }

                // Phase 1.5: Generate reference fingerprints for episodes with markers but no .fp files
                // Only when the season has unresolved episodes and needs more comparison data
                if (!mediaInfoRestoreMode)
                {
                    var allSeasonEps = taskSeason.GetEpisodes(new InternalItemsQuery
                    {
                        GroupByPresentationUniqueKey = false,
                        EnableTotalRecordCount = false,
                        HasIntroDetectionFailure = false
                    }).Items.OfType<Episode>().ToArray();

                    var unresolvedCount = allSeasonEps.Count(e => !Plugin.ChapterApi.HasIntro(e));

                    if (unresolvedCount > 0)
                    {
                        // Count fingerprints from episodes WITH markers (known-good references)
                        var referenceFpCount = allSeasonEps.Count(e =>
                            Plugin.ChapterApi.HasIntro(e) &&
                            Plugin.FingerprintApi.HasFingerprintFile(e, seasonFpLength));

                        if (referenceFpCount < 2)
                        {
                            var referenceEpisodes = allSeasonEps
                                .Where(e => Plugin.ChapterApi.HasIntro(e) &&
                                            !Plugin.FingerprintApi.HasFingerprintFile(e, seasonFpLength))
                                .Take(2 - referenceFpCount)
                                .ToList();

                            if (referenceEpisodes.Count > 0)
                            {
                                _logger.Info(
                                    $"IntroFingerprintExtract - Generating {referenceEpisodes.Count} reference fingerprint(s) for season: {taskSeason.Path}");

                                var refTasks = new List<Task>();
                                foreach (var refEpisode in referenceEpisodes)
                                {
                                    var ep = refEpisode;
                                    try
                                    {
                                        await QueueManager.MasterSemaphore.WaitAsync(cancellationToken)
                                            .ConfigureAwait(false);
                                    }
                                    catch
                                    {
                                        return;
                                    }

                                    var refTask = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            var result = await Plugin.FingerprintApi
                                                .CreateTitleFingerprint(ep, directoryService,
                                                    cancellationToken, seasonFpLength)
                                                .ConfigureAwait(false);

                                            if (result != null)
                                            {
                                                _logger.Info(
                                                    $"IntroFingerprintExtract - Reference fingerprint {(result.Item2 ? "created" : "skipped (no new data)")}: {ep.Name} - {result.Item1}");
                                            }
                                        }
                                        catch (OperationCanceledException)
                                        {
                                        }
                                        catch (Exception e)
                                        {
                                            _logger.Debug(
                                                $"IntroFingerprintExtract - Reference fingerprint failed: {ep.Name} - {e.Message}");
                                        }
                                        finally
                                        {
                                            QueueManager.MasterSemaphore.Release();
                                        }
                                    }, cancellationToken);
                                    refTasks.Add(refTask);
                                }

                                await Task.WhenAll(refTasks).ConfigureAwait(false);
                            }
                        }
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("IntroFingerprintExtract - Scheduled Task Cancelled");
                    return;
                }

                // Phase 2: Analyze the season (chromaprint matching)
                if (mediaInfoRestoreMode)
                {
                    seasonSkipCount++;
                }
                else
                {
                    try
                    {
                        await Plugin.FingerprintApi
                            .UpdateIntroMarkerForSeason(taskSeason, cancellationToken, null, seasonFpLength)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.Info("IntroFingerprintExtract - Season cancelled: " + taskSeason.Name + " - " +
                                     taskSeason.Path);
                    }
                    catch (Exception e)
                    {
                        _logger.Error("IntroFingerprintExtract - Season failed: " + taskSeason.Name + " - " +
                                      taskSeason.Path);
                        _logger.Error(e.Message);
                        _logger.Debug(e.StackTrace);
                    }
                }

                // Phase 3: Publish newly-detected intros to IntroDb
                if (!mediaInfoRestoreMode && enableIntroDbPublish &&
                    !string.IsNullOrWhiteSpace(introDbApiKey))
                {
                    var publishCount = 0;
                    var publishFailCount = 0;

                    foreach (var episode in season)
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        if (!Plugin.ChapterApi.HasIntro(episode)) continue;

                        // Skip if already covered (from /mine or publish pre-pass)
                        if (episode.Season?.Series?.ProviderIds != null &&
                            episode.Season.Series.ProviderIds.ContainsKey("Imdb") &&
                            episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue)
                        {
                            var epCacheKey =
                                $"{episode.Season.Series.ProviderIds["Imdb"]}|{episode.ParentIndexNumber.Value}|{episode.IndexNumber.Value}";
                            if (introDbCoveredKeys.ContainsKey(epCacheKey)) continue;
                        }

                        try
                        {
                            var published = await Plugin.IntroDbApi
                                .TryPublishIntroForEpisode(episode, introDbApiKey, cancellationToken)
                                .ConfigureAwait(false);

                            if (published)
                                publishCount++;
                            else
                                publishFailCount++;
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(
                                $"IntroDb - Publish failed for {episode.Name}: {e.Message}");
                            publishFailCount++;
                        }
                    }

                    if (publishCount > 0 || publishFailCount > 0)
                    {
                        _logger.Info(
                            $"IntroFingerprintExtract - IntroDb publish for {taskSeason.Path}: {publishCount} published, {publishFailCount} failed");
                    }
                }

                processedSeasons++;
                var seasonProgress = introDbWeight +
                                     episodeWeight * processedEpisodes / totalEpisodes +
                                     seasonWeight * processedSeasons / totalSeasons;
                progress.Report(seasonProgress * 100);

                _logger.Info(
                    $"IntroFingerprintExtract - Season Progress {processedSeasons}/{totalSeasons}: {taskSeason.Path}");
            }

            if (episodes.Count > 0) IsRunning = false;

            progress.Report(100.0);
            _logger.Info($"IntroFingerprintExtract - Number of seasons skipped: {seasonSkipCount}");
            _logger.Info($"IntroFingerprintExtract - Number of episodes skipped: {episodeSkipCount}");
            _logger.Info("IntroFingerprintExtract - Scheduled Task Complete");
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "IntroFingerprintExtractTask";

        public string Description => Resources.ResourceManager.GetString(
            "ExtractIntroFingerprintTask_Description_Extracts_intro_fingerprint_from_episodes",
            Plugin.Instance.DefaultUICulture);

        public string Name => "Extract Intro Fingerprint";
        //public string Name =>
        //    Resources.ResourceManager.GetString("ExtractIntroFingerprintTask_Name_Extract_Intro_Fingerprint",
        //        Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public static bool IsRunning { get; private set; }
    }
}
