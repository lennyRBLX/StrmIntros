using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmIntros.Common;
using StrmIntros.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static StrmIntros.Options.MediaInfoExtractOptions;

namespace StrmIntros.ScheduledTask
{
    public class ExtractMediaInfoTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;

        public ExtractMediaInfoTask(IFileSystem fileSystem)
        {
            _logger = Plugin.Instance.Logger;
            _fileSystem = fileSystem;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.Info("MediaInfoExtract - Scheduled Task Execute");

            var pluginOptions = Plugin.Instance.GetPluginOptions();

            var maxConcurrentCount = pluginOptions.GeneralOptions.MaxConcurrentCount;
            _logger.Info("Master Max Concurrent Count: " + maxConcurrentCount);
            var cooldownSeconds = maxConcurrentCount == 1
                ? pluginOptions.GeneralOptions.CooldownDurationSeconds
                : (int?)null;
            if (cooldownSeconds.HasValue) _logger.Info("Cooldown Duration Seconds: " + cooldownSeconds.Value);

            var persistMediaInfoMode = pluginOptions.MediaInfoExtractOptions.PersistMediaInfoMode;
            _logger.Info("Persist MediaInfo Mode: " + persistMediaInfoMode);
            var persistMediaInfo = persistMediaInfoMode != PersistMediaInfoOption.None.ToString();
            var mediaInfoRestoreMode = persistMediaInfoMode == PersistMediaInfoOption.Restore.ToString();

            var enableImageCapture = pluginOptions.MediaInfoExtractOptions.EnableImageCapture;
            _logger.Info("Enable Image Capture: " + enableImageCapture);

            var items = Plugin.LibraryApi.FetchPreExtractTaskItems();

            _logger.Info($"MediaInfoExtract - Total items before pre-pass: {items.Count}");

            // Media info sync pre-pass: restore from JSON first, then backfill DB → JSON
            if (persistMediaInfo)
            {
                var syncDirectoryService = new DirectoryService(_logger, _fileSystem);
                var jsonRestoredCount = 0;

                // JSON → DB: restore media info from JSON for items missing it
                foreach (var item in items)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.Info("MediaInfoExtract - Scheduled Task Cancelled");
                        return;
                    }

                    if (!Plugin.LibraryApi.HasMediaInfo(item))
                    {
                        var restored = await Plugin.MediaInfoApi
                            .DeserializeMediaInfo(item, syncDirectoryService,
                                "MediaInfoExtract MediaInfoSync", true)
                            .ConfigureAwait(false);
                        if (restored) jsonRestoredCount++;
                    }
                }

                if (jsonRestoredCount > 0)
                {
                    _logger.Info(
                        $"MediaInfoExtract - Media info sync pre-pass: {jsonRestoredCount} JSON→DB");
                }

                // DB → JSON: backfill existing DB media info to JSON (skip in Restore-only mode)
                if (!mediaInfoRestoreMode)
                {
                    var jsonBackfillCount = 0;

                    foreach (var item in items)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.Info("MediaInfoExtract - Scheduled Task Cancelled");
                            return;
                        }

                        if (Plugin.LibraryApi.HasMediaInfo(item))
                        {
                            await Plugin.MediaInfoApi
                                .SerializeMediaInfo(item.InternalId, syncDirectoryService, false,
                                    "MediaInfoExtract MediaInfoSync")
                                .ConfigureAwait(false);
                            jsonBackfillCount++;
                        }
                    }

                    if (jsonBackfillCount > 0)
                    {
                        _logger.Info(
                            $"MediaInfoExtract - Media info sync pre-pass: {jsonBackfillCount} DB→JSON");
                    }
                }
            }

            // Pre-pass: skip items that already have media info or missing .strm files
            var alreadyCompleteCount = 0;
            var strmMissingCount = 0;
            items = items.Where(item =>
            {
                if (item.Path != null && item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                    && !File.Exists(item.Path))
                {
                    strmMissingCount++;
                    return false;
                }
                if (Plugin.LibraryApi.HasMediaInfo(item) && !item.IsShortcut)
                {
                    alreadyCompleteCount++;
                    return false;
                }
                return true;
            }).ToList();

            if (strmMissingCount > 0)
            {
                _logger.Info(
                    $"MediaInfoExtract - Pre-pass: {strmMissingCount} items skipped (missing .strm files)");
            }
            if (alreadyCompleteCount > 0)
            {
                _logger.Info(
                    $"MediaInfoExtract - Pre-pass: {alreadyCompleteCount} items already have media info, skipped");
            }

            if (items.Count > 0) IsRunning = true;

            double total = items.Count;
            var index = 0;
            var current = 0;
            var skip = 0;

            var tasks = new List<Task>();

            foreach (var item in items)
            {
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
                    _logger.Info("MediaInfoExtract - Scheduled Task Cancelled");
                    return;
                }

                var taskIndex = ++index;
                var taskItem = item;
                var task = Task.Run(async () =>
                {
                    bool? result = null;

                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.Info("MediaInfoExtract - Scheduled Task Cancelled");
                            return;
                        }

                        result = await Plugin.LibraryApi
                            .OrchestrateMediaInfoProcessAsync(taskItem, "MediaInfoExtract Task", cancellationToken)
                            .ConfigureAwait(false);

                        if (result is null)
                        {
                            if (!mediaInfoRestoreMode)
                            {
                                _logger.Info(
                                    $"MediaInfoExtract - Item skipped or non-existent: {taskItem.Name} - {taskItem.Path}");
                            }

                            Interlocked.Increment(ref skip);
                            return;
                        }

                    }
                    catch (OperationCanceledException)
                    {
                        _logger.Info($"MediaInfoExtract - Item cancelled: {taskItem.Name} - {taskItem.Path}");
                    }
                    catch (Exception e)
                    {
                        _logger.Error($"MediaInfoExtract - Item failed: {taskItem.Name} - {taskItem.Path}");
                        _logger.Error(e.Message);
                        _logger.Debug(e.StackTrace);
                    }
                    finally
                    {
                        if (result is true && cooldownSeconds.HasValue)
                        {
                            try
                            {
                                await Task.Delay(cooldownSeconds.Value * 1000, cancellationToken).ConfigureAwait(false);
                            }
                            catch
                            {
                                // ignored
                            }
                        }

                        QueueManager.MasterSemaphore.Release();

                        var currentCount = Interlocked.Increment(ref current);
                        progress.Report(currentCount / total * 100);

                        if (!mediaInfoRestoreMode)
                        {
                            _logger.Info(
                                $"MediaInfoExtract - Progress {currentCount}/{total} - Task {taskIndex}: {taskItem.Path}");
                        }
                    }
                }, cancellationToken);
                tasks.Add(task);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);

            if (items.Count > 0) IsRunning = false;

            progress.Report(100.0);
            _logger.Info($"MediaInfoExtract - Number of items skipped: {skip}");
            _logger.Info("MediaInfoExtract - Scheduled Task Complete");
        }

        public string Category =>
            Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
                Plugin.Instance.DefaultUICulture);

        public string Key => "MediaInfoExtractTask";

        public string Description => Resources.ResourceManager.GetString(
            "ExtractMediaInfoTask_Description_Extracts_media_info_from_videos_and_audios",
            Plugin.Instance.DefaultUICulture);

        public string Name => "Extract MediaInfo";
        //public string Name => Resources.ResourceManager.GetString("ExtractMediaInfoTask_Name_Extract_MediaInfo",
        //    Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public static bool IsRunning { get; private set; }
    }
}
