using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmIntros.Options;
using StrmIntros.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static StrmIntros.Options.MediaInfoExtractOptions;
using static StrmIntros.Options.Utility;

namespace StrmIntros.Common
{
    public class FingerprintApi
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly IItemRepository _itemRepository;
        private readonly ILogger _logger;

        private readonly object _audioFingerprintManager;
        private readonly MethodInfo _createTitleFingerprint;
        private readonly FieldInfo _timeoutMs;
        private readonly ChromaprintMatcher _chromaprintMatcher;

        public static List<string> LibraryPathsInScope;

        public FingerprintApi(ILibraryManager libraryManager, IFileSystem fileSystem,
            IApplicationPaths applicationPaths, IFfmpegManager ffmpegManager, IMediaEncoder mediaEncoder,
            IMediaMountManager mediaMountManager, IJsonSerializer jsonSerializer, IItemRepository itemRepository,
            IServerApplicationHost serverApplicationHost)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _itemRepository = itemRepository;
            _chromaprintMatcher = new ChromaprintMatcher(_logger, itemRepository);

            UpdateLibraryPathsInScope();

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var audioFingerprintManager = embyProviders.GetType("Emby.Providers.Markers.AudioFingerprintManager");
                var audioFingerprintManagerConstructor = audioFingerprintManager.GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[]
                    {
                        typeof(IFileSystem), typeof(ILogger), typeof(IApplicationPaths), typeof(IFfmpegManager),
                        typeof(IMediaEncoder), typeof(IMediaMountManager), typeof(IJsonSerializer),
                        typeof(IServerApplicationHost)
                    }, null);
                _audioFingerprintManager = audioFingerprintManagerConstructor?.Invoke(new object[]
                {
                    fileSystem, _logger, applicationPaths, ffmpegManager, mediaEncoder, mediaMountManager,
                    jsonSerializer, serverApplicationHost
                });
                _createTitleFingerprint = audioFingerprintManager.GetMethod("CreateTitleFingerprint",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[]
                    {
                        typeof(Episode), typeof(LibraryOptions), typeof(IDirectoryService),
                        typeof(CancellationToken)
                    }, null);
                _timeoutMs = audioFingerprintManager.GetField("TimeoutMs",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                PatchTimeout(Plugin.Instance.GetPluginOptions().GeneralOptions.MaxConcurrentCount);
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    _logger.Debug(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            if (_audioFingerprintManager is null || _createTitleFingerprint is null || _timeoutMs is null)
            {
                _logger.Warn($"{nameof(FingerprintApi)} Init Failed");
            }
        }

        public int CalculateSeasonFingerprintLength(IEnumerable<Episode> episodes)
        {
            var maxMinutes = Plugin.Instance.GetPluginOptions()
                .IntroSkipOptions.IntroDetectionFingerprintMinutes;

            var shortestDurationMinutes = episodes
                .Where(e => e.RunTimeTicks.HasValue && e.RunTimeTicks.Value > 0)
                .Select(e => TimeSpan.FromTicks(e.RunTimeTicks.Value).TotalMinutes)
                .DefaultIfEmpty(maxMinutes * 2.0)
                .Min();

            var halfRuntime = (int)Math.Floor(shortestDurationMinutes / 2.0);
            return Math.Max(1, Math.Min(halfRuntime, maxMinutes));
        }

        public bool HasFingerprintFile(BaseItem item, int? expectedLength = null)
        {
            try
            {
                var metadataPath = item.GetInternalMetadataPath();
                if (Directory.Exists(metadataPath))
                {
                    var pattern = expectedLength.HasValue
                        ? $"title_{expectedLength.Value}_*.fp"
                        : "title_*.fp";

                    foreach (var fpFile in Directory.EnumerateFiles(metadataPath, pattern))
                    {
                        var fileInfo = new FileInfo(fpFile);
                        // Must have at least 4 bytes (one uint32 chromaprint point) to be usable
                        if (fileInfo.Length >= 4)
                        {
                            _logger.Debug($"HasFingerprintFile - Valid fingerprint ({fileInfo.Length} bytes): {fpFile}");
                            return true;
                        }

                        _logger.Warn($"HasFingerprintFile - Deleting unusable .fp file ({fileInfo.Length} bytes): {fpFile}");
                        try { File.Delete(fpFile); } catch { /* ignored */ }
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Debug("HasFingerprintFile check failed: " + e.Message);
            }

            return false;
        }

        public Task<Tuple<string, bool>> CreateTitleFingerprint(Episode item, IDirectoryService directoryService,
            CancellationToken cancellationToken, int? seasonFingerprintLength = null)
        {
            var libraryOptions = LibraryApi.CopyLibraryOptions(_libraryManager.GetLibraryOptions(item));

            if (seasonFingerprintLength.HasValue)
            {
                libraryOptions.IntroDetectionFingerprintLength = seasonFingerprintLength.Value;
            }

            return (Task<Tuple<string, bool>>)_createTitleFingerprint.Invoke(_audioFingerprintManager,
                new object[] { item, libraryOptions, directoryService, cancellationToken });
        }

        public Task<Tuple<string, bool>> CreateTitleFingerprint(Episode item, CancellationToken cancellationToken,
            int? seasonFingerprintLength = null)
        {
            var directoryService = new DirectoryService(_logger, _fileSystem);

            return CreateTitleFingerprint(item, directoryService, cancellationToken, seasonFingerprintLength);
        }

        public void PatchTimeout(int maxConcurrentCount)
        {
            var newTimeout = maxConcurrentCount * Convert.ToInt32(TimeSpan.FromMinutes(10.0).TotalMilliseconds);
            _timeoutMs.SetValue(_audioFingerprintManager, newTimeout);
        }

        public bool IsLibraryInScope(BaseItem item)
        {
            return !string.IsNullOrEmpty(item.Path) && LibraryPathsInScope.Any(l => item.Path.StartsWith(l));
        }

        public void UpdateLibraryPathsInScope()
        {
            var validLibraryIds = GetValidLibraryIds(Plugin.Instance.GetPluginOptions()
                .IntroSkipOptions.LibraryScope);

            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => f.LibraryOptions.EnableMarkerDetection &&
                            (f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null) &&
                            (!validLibraryIds.Any() || validLibraryIds.All(id => id == "-1") ||
                             validLibraryIds.Contains(f.Id)))
                .ToList();

            LibraryPathsInScope = libraries.SelectMany(l => l.Locations)
                .Select(ls => ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? ls
                    : ls + Path.DirectorySeparatorChar)
                .ToList();
        }

        public long[] GetAllFavoriteSeasons()
        {
            var favorites = LibraryApi.AllUsers.Select(e => e.Key)
                .SelectMany(u => _libraryManager.GetItemList(new InternalItemsQuery
                {
                    User = u,
                    IsFavorite = true,
                    IncludeItemTypes = new[] { nameof(Series), nameof(Episode) },
                    PathStartsWithAny = LibraryPathsInScope.ToArray()
                }))
                .GroupBy(i => i.InternalId)
                .Select(g => g.First())
                .ToList();

            var expanded = Plugin.LibraryApi.ExpandFavorites(favorites, false, null, false).OfType<Episode>();

            var result = expanded.GroupBy(e => e.ParentId).Select(g => g.Key).ToArray();

            return result;
        }

        public List<Episode> FetchFingerprintQueueItems(List<BaseItem> items)
        {
            var resultItems = new List<Episode>();
            var incomingItems = items.OfType<Episode>().ToList();

            if (IsCatchupTaskSelected(GeneralOptions.CatchupTask.Fingerprint) && LibraryPathsInScope.Any())
            {
                resultItems = incomingItems
                    .Where(i => LibraryPathsInScope.Any(p => i.ContainingFolderPath.StartsWith(p)))
                    .ToList();
            }

            var isModSupported = Plugin.Instance.IsModSupported;
            resultItems = resultItems.Where(i => isModSupported || !i.IsShortcut).GroupBy(i => i.InternalId)
                .Select(g => g.First()).ToList();

            var unprocessedItems = FilterUnprocessed(resultItems);

            return unprocessedItems;
        }

        private List<Episode> FilterUnprocessed(List<Episode> items)
        {
            var enableImageCapture = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.EnableImageCapture;

            var results = new List<Episode>();

            foreach (var item in items)
            {
                if (Plugin.LibraryApi.IsExtractNeeded(item, enableImageCapture))
                {
                    results.Add(item);
                }
                else if (IsExtractNeeded(item))
                {
                    results.Add(item);
                }
            }

            _logger.Info("IntroFingerprintExtract - Number of items: " + results.Count);

            return results;
        }

        public bool IsExtractNeeded(BaseItem item)
        {
            return !Plugin.ChapterApi.HasIntro(item) &&
                   string.IsNullOrEmpty(_itemRepository.GetIntroDetectionFailureResult(item.InternalId));
        }

        public List<Episode> FetchIntroPreExtractTaskItems()
        {
            var markerEnabledLibraryScope = Plugin.Instance.GetPluginOptions().IntroSkipOptions.LibraryScope;

            var itemsFingerprintQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                HasPath = true,
            };

            if (!string.IsNullOrEmpty(markerEnabledLibraryScope) && markerEnabledLibraryScope.Contains("-1"))
            {
                itemsFingerprintQuery.ParentIds = GetAllFavoriteSeasons().DefaultIfEmpty(-1).ToArray();
            }
            else
            {
                if (LibraryPathsInScope.Any())
                {
                    itemsFingerprintQuery.PathStartsWithAny = LibraryPathsInScope.ToArray();
                }
            }

            var items = _libraryManager.GetItemList(itemsFingerprintQuery).OfType<Episode>().ToList();

            return items;
        }

        public List<Episode> FetchIntroFingerprintTaskItems()
        {
            var libraryIds = Plugin.Instance.GetPluginOptions()
                .IntroSkipOptions.LibraryScope.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
            var librariesWithMarkerDetection = _libraryManager.GetVirtualFolders()
                .Where(f => (f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null) &&
                            f.LibraryOptions.EnableMarkerDetection)
                .ToList();
            var librariesSelected = librariesWithMarkerDetection.Where(f => libraryIds.Contains(f.Id)).ToList();

            _logger.Info("IntroFingerprintExtract - LibraryScope: " + (!librariesWithMarkerDetection.Any()
                ? "NONE"
                : string.Join(", ",
                    (libraryIds.Contains("-1")
                        ? new[] { Resources.Favorites }.Concat(librariesSelected.Select(l => l.Name))
                        : librariesSelected.Select(l => l.Name)).DefaultIfEmpty("ALL"))));

            var pathFilter = LibraryPathsInScope.Any() ? LibraryPathsInScope.ToArray() : null;

            if (Plugin.Instance.DebugMode)
            {
                // Diagnostic: episodes with just path filter
                var baseQuery = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    Recursive = true,
                    GroupByPresentationUniqueKey = false
                };
                if (pathFilter != null) baseQuery.PathStartsWithAny = pathFilter;
                var baseCount = _libraryManager.GetItemList(baseQuery).OfType<Episode>().Count();
                _logger.Info($"IntroFingerprintExtract - Diag: episodes with path filter only: {baseCount}");

                // Diagnostic: + WithoutChapterMarkers
                var markerQuery = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    Recursive = true,
                    GroupByPresentationUniqueKey = false,
                    WithoutChapterMarkers = new[] { MarkerType.IntroStart }
                };
                if (pathFilter != null) markerQuery.PathStartsWithAny = pathFilter;
                var markerCount = _libraryManager.GetItemList(markerQuery).OfType<Episode>().Count();
                _logger.Info($"IntroFingerprintExtract - Diag: + WithoutChapterMarkers: {markerCount}");

                // Diagnostic: + HasIntroDetectionFailure
                var failureQuery = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    Recursive = true,
                    GroupByPresentationUniqueKey = false,
                    WithoutChapterMarkers = new[] { MarkerType.IntroStart },
                    HasIntroDetectionFailure = false
                };
                if (pathFilter != null) failureQuery.PathStartsWithAny = pathFilter;
                var failureCount = _libraryManager.GetItemList(failureQuery).OfType<Episode>().Count();
                _logger.Info($"IntroFingerprintExtract - Diag: + HasIntroDetectionFailure=false: {failureCount}");
            }

            var itemsFingerprintQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                WithoutChapterMarkers = new[] { MarkerType.IntroStart },
                HasIntroDetectionFailure = false
            };

            if (libraryIds.Any() && libraryIds.All(i => i == "-1"))
            {
                itemsFingerprintQuery.ParentIds = GetAllFavoriteSeasons().DefaultIfEmpty(-1).ToArray();
            }
            else
            {
                if (LibraryPathsInScope.Any())
                {
                    itemsFingerprintQuery.PathStartsWithAny = LibraryPathsInScope.ToArray();
                }
            }

            var items = _libraryManager.GetItemList(itemsFingerprintQuery).OfType<Episode>().ToList();

            return items;
        }

        public void EnsureLibraryMarkerDetection()
        {
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null)
                .ToList();

            foreach (var library in libraries)
            {
                var options = library.LibraryOptions;

                if (!options.EnableMarkerDetection)
                {
                    options.EnableMarkerDetection = true;

                    if (long.TryParse(library.ItemId, out var itemId))
                    {
                        CollectionFolder.SaveLibraryOptions(itemId, options);
                    }
                }
            }
        }

#nullable enable
        public async Task UpdateIntroMarkerForSeason(Season season, CancellationToken cancellationToken,
            IProgress<double>? progress = null, int? seasonFingerprintLength = null)
        {
            var episodeQuery = new InternalItemsQuery
            {
                GroupByPresentationUniqueKey = false,
                EnableTotalRecordCount = false,
                HasIntroDetectionFailure = false
            };
            var allEpisodes = season.GetEpisodes(episodeQuery).Items.OfType<Episode>().ToArray();

            var fpLength = seasonFingerprintLength ?? CalculateSeasonFingerprintLength(allEpisodes);

            _logger.Info(
                $"IntroFingerprintExtract - Season {season.Path}: using IntroDetectionFingerprintLength={fpLength}");

            var episodesWithoutMarkers = allEpisodes.Where(e => !Plugin.ChapterApi.HasIntro(e)).ToList();

            _logger.Info(
                $"IntroFingerprintExtract - Season {season.Path}: {allEpisodes.Length} total episodes, {episodesWithoutMarkers.Count} without intro markers");

            // Run custom chromaprint matching (replaces Emby's broken reflected methods)
            var matches = _chromaprintMatcher.MatchSeason(allEpisodes, fpLength, cancellationToken);

            _logger.Info(
                $"IntroFingerprintExtract - Season {season.Path}: {matches.Count} intros detected by chromaprint matching");

            double total = episodesWithoutMarkers.Count;
            var index = 0;

            foreach (var episode in episodesWithoutMarkers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (matches.TryGetValue(episode.InternalId, out var intro))
                {
                    WriteIntroMarkers(episode, intro.start, intro.end);

                    if (Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.PersistMediaInfoMode !=
                        PersistMediaInfoOption.None.ToString())
                    {
                        await Plugin.MediaInfoApi.UpdateIntroMarkerInJson(episode).ConfigureAwait(false);
                    }
                }

                index++;
                progress?.Report(index / total);
            }

            progress?.Report(1.0);
        }

        private void WriteIntroMarkers(Episode episode, double startSeconds, double endSeconds)
        {
            var chapters = _itemRepository.GetChapters(episode);
            chapters.RemoveAll(c =>
                c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd);

            var startTicks = TimeSpan.FromSeconds(startSeconds).Ticks;
            var endTicks = TimeSpan.FromSeconds(endSeconds).Ticks;

            chapters.Add(new ChapterInfo
            {
                Name = MarkerType.IntroStart + "#SA",
                MarkerType = MarkerType.IntroStart,
                StartPositionTicks = startTicks
            });
            chapters.Add(new ChapterInfo
            {
                Name = MarkerType.IntroEnd + "#SA",
                MarkerType = MarkerType.IntroEnd,
                StartPositionTicks = endTicks
            });

            chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));
            _itemRepository.SaveChapters(episode.InternalId, chapters);
        }
#nullable restore
    }
}
