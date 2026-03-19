using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using static StrmIntros.Common.LanguageUtility;

namespace StrmIntros.Common
{
    public class MetadataApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly ILocalizationManager _localizationManager;
        private readonly IFileSystem _fileSystem;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IHttpClient _httpClient;

        private static readonly LruCache LruCache = new LruCache(20);
        private static long _lastRequestTicks;

        public const int RequestIntervalMs = 100;
        public static readonly TimeSpan DefaultCacheTime = TimeSpan.FromHours(6.0);

        public MetadataApi(ILibraryManager libraryManager, IFileSystem fileSystem,
            IServerConfigurationManager configurationManager, ILocalizationManager localizationManager,
            IJsonSerializer jsonSerializer, IHttpClient httpClient)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _configurationManager = configurationManager;
            _localizationManager = localizationManager;
            _fileSystem = fileSystem;
            _jsonSerializer = jsonSerializer;
            _httpClient = httpClient;
        }
        
        public MetadataRefreshOptions GetMetadataValidationRefreshOptions()
        {
            return new MetadataRefreshOptions(new DirectoryService(_logger, _fileSystem))
            {
                EnableRemoteContentProbe = false,
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllMetadata = false,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages = false,
                EnableThumbnailImageExtraction = false,
                EnableSubtitleDownloading = false
            };
        }

        public string GetPreferredMetadataLanguage(BaseItem item)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            var language = item.PreferredMetadataLanguage;
            if (string.IsNullOrEmpty(language))
            {
                language = item.GetParents().Select(i => i.PreferredMetadataLanguage).FirstOrDefault(i => !string.IsNullOrEmpty(i));
            }
            if (string.IsNullOrEmpty(language))
            {
                language = libraryOptions.PreferredMetadataLanguage;
            }
            if (string.IsNullOrEmpty(language))
            {
                language = _configurationManager.Configuration.PreferredMetadataLanguage;
            }

            return language;
        }

        public string GetServerPreferredMetadataLanguage()
        {
            return _configurationManager.Configuration.PreferredMetadataLanguage;
        }

        public string GetCollectionOriginalLanguage(BoxSet collection)
        {
            var children = _libraryManager.GetItemList(new InternalItemsQuery
            {
                CollectionIds = new[] { collection.InternalId }
            });

            var concatenatedTitles = string.Join("|", children.Select(c => c.OriginalTitle));

            return GetLanguageByTitle(concatenatedTitles);
        }

        public string ConvertToServerLanguage(string language)
        {
            if (string.Equals(language, "pt", StringComparison.OrdinalIgnoreCase))
                return "pt-br";
            if (string.Equals(language, "por", StringComparison.OrdinalIgnoreCase))
                return "pt";
            if (string.Equals(language, "zhtw", StringComparison.OrdinalIgnoreCase))
                return "zh-tw";
            if (string.Equals(language, "zho", StringComparison.OrdinalIgnoreCase))
                return "zh-hk";
            var languageInfo =
                _localizationManager.FindLanguageInfo(language.AsSpan());
            return languageInfo != null ? languageInfo.TwoLetterISOLanguageName : language;
        }

        public void UpdateSeriesPeople(Series series)
        {
            if (!series.ProviderIds.ContainsKey("Tmdb")) return;

            var seriesPeople = _libraryManager.GetItemPeople(series);

            var seasonQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Season) },
                ParentWithPresentationUniqueKeyFromItemId = series.InternalId,
                MinIndexNumber = 1,
                OrderBy = new (string, SortOrder)[] { (ItemSortBy.IndexNumber, SortOrder.Ascending) }
            };

            var seasons = _libraryManager.GetItemList(seasonQuery);
            var peopleLists = seasons
                .Select(s => _libraryManager.GetItemPeople(s))
                .ToList();

            peopleLists.Add(seriesPeople);

            var maxPeopleCount = peopleLists.Max(seasonPeople => seasonPeople.Count);

            var combinedPeople = new List<PersonInfo>();
            var uniqueNames = new HashSet<string>();

            for (var i = 0; i < maxPeopleCount; i++)
            {
                foreach (var seasonPeople in peopleLists)
                {
                    var person = i < seasonPeople.Count ? seasonPeople[i] : null;
                    if (person != null && uniqueNames.Add(person.Name))
                    {
                        combinedPeople.Add(person);
                    }
                }
            }

            _libraryManager.UpdatePeople(series, combinedPeople);
        }

        public async Task<T> GetMovieDbResponse<T>(string url, string cacheKey, string cachePath,
            CancellationToken cancellationToken) where T : class
        {
            var result = TryGetFromCache<T>(cacheKey, cachePath);

            if (result != null) return result;

            var num = Math.Min((RequestIntervalMs * 10000 - (DateTimeOffset.UtcNow.Ticks - _lastRequestTicks)) / 10000L,
                RequestIntervalMs);

            if (num > 0L)
            {
                _logger.Debug("Throttling Tmdb by {0} ms", num);
                await Task.Delay(Convert.ToInt32(num)).ConfigureAwait(false);
            }

            _lastRequestTicks = DateTimeOffset.UtcNow.Ticks;

            var options = new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken,
                AcceptHeader = "application/json",
                BufferContent = true,
                UserAgent = Plugin.Instance.UserAgent
            };

            try
            {
                using var response = await _httpClient.SendAsync(options, "GET").ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Debug("Failed to get MovieDb response - " + response.StatusCode);
                    return null;
                }

                await using var contentStream = response.Content;
                result = _jsonSerializer.DeserializeFromStream<T>(contentStream);

                if (result is null) return null;

                AddOrUpdateCache(result, cacheKey, cachePath);

                return result;
            }
            catch (Exception e)
            {
                _logger.Debug("Failed to get MovieDb response - " + e.Message);
                return null;
            }
        }

        public async Task<T> GetMovieDbResponse<T>(string url, CancellationToken cancellationToken) where T : class
        {
            return await GetMovieDbResponse<T>(url, null, null, cancellationToken);
        }

        public T TryGetFromCache<T>(string cacheKey, string cachePath) where T : class
        {
            if (string.IsNullOrEmpty(cacheKey) || string.IsNullOrEmpty(cachePath)) return null;

            if (LruCache.TryGetFromCache(cacheKey, out T result)) return result;

            var cacheFile = _fileSystem.GetFileSystemInfo(cachePath);

            if (cacheFile.Exists && DateTimeOffset.UtcNow - _fileSystem.GetLastWriteTimeUtc(cacheFile) <= DefaultCacheTime)
            {
                result = _jsonSerializer.DeserializeFromFile<T>(cachePath);
                LruCache.AddOrUpdateCache(cacheKey, result);

                return result;
            }

            return null;
        }

        public void AddOrUpdateCache<T>(T result, string cacheKey, string cachePath)
        {
            if (result is null || string.IsNullOrEmpty(cacheKey) || string.IsNullOrEmpty(cachePath)) return;

            _fileSystem.CreateDirectory(_fileSystem.GetDirectoryName(cachePath));
            _jsonSerializer.SerializeToFile(result, cachePath);
            LruCache.AddOrUpdateCache(cacheKey, result);
        }

        public Series GetSeriesByPath(string path)
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery { Path = path });

            foreach (var item in items)
            {
                if (item is Episode episode)
                {
                    return episode.Series;
                }

                if (item is Season season)
                {
                    return season.Series;
                }

                if (item is Series series)
                {
                    return series;
                }
            }

            return null;
        }
    }
}
