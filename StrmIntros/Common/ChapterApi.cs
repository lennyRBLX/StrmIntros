using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static StrmIntros.Options.MediaInfoExtractOptions;

namespace StrmIntros.Common
{
    public class ChapterApi
    {
        private readonly ILogger _logger;
        private readonly IItemRepository _itemRepository;

        private const string MarkerSuffix = "#SA";

        public ChapterApi(IItemRepository itemRepository)
        {
            _logger = Plugin.Instance.Logger;
            _itemRepository = itemRepository;
        }

        public bool HasIntro(BaseItem item)
        {
            return _itemRepository.GetChapters(item.InternalId, new[] { MarkerType.IntroStart }).Any();
        }

        public long? GetIntroStart(BaseItem item)
        {
            var introStart = _itemRepository.GetChapters(item.InternalId, new[] { MarkerType.IntroStart })
                .FirstOrDefault();

            return introStart?.StartPositionTicks;
        }

        public long? GetIntroEnd(BaseItem item)
        {
            var introEnd = _itemRepository.GetChapters(item.InternalId, new[] { MarkerType.IntroEnd }).FirstOrDefault();

            return introEnd?.StartPositionTicks;
        }

        public bool HasCredits(BaseItem item)
        {
            return _itemRepository.GetChapters(item.InternalId, new[] { MarkerType.CreditsStart }).Any();
        }

        public long? GetCreditsStart(BaseItem item)
        {
            var creditsStart = _itemRepository.GetChapters(item.InternalId, new[] { MarkerType.CreditsStart })
                .FirstOrDefault();

            return creditsStart?.StartPositionTicks;
        }

        private static readonly Regex IntroChapterRegex =
            new Regex(@"(^|\s)(Intro|Introduction|OP|Opening)(?!\sEnd)(\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public async Task<bool> TryDetectIntroFromChapterName(Episode episode)
        {
            try
            {
                if (HasIntro(episode)) return true;

                var chapters = _itemRepository.GetChapters(episode);
                if (chapters == null || chapters.Count < 2) return false;

                for (var i = 0; i < chapters.Count; i++)
                {
                    var chapter = chapters[i];
                    if (string.IsNullOrEmpty(chapter.Name)) continue;
                    if (chapter.MarkerType != MarkerType.Chapter) continue;

                    if (!IntroChapterRegex.IsMatch(chapter.Name)) continue;

                    // Check adjacent chapter doesn't also match (prevent "Intro" + "Intro End" both matching)
                    if (i + 1 < chapters.Count && !string.IsNullOrEmpty(chapters[i + 1].Name) &&
                        IntroChapterRegex.IsMatch(chapters[i + 1].Name))
                        continue;

                    var introStartTicks = chapter.StartPositionTicks;
                    var introEndTicks = i + 1 < chapters.Count
                        ? chapters[i + 1].StartPositionTicks
                        : episode.RunTimeTicks ?? 0;

                    // Validate duration: 15s - 120s
                    var durationSec = TimeSpan.FromTicks(introEndTicks - introStartTicks).TotalSeconds;
                    if (durationSec < 15 || durationSec > 120) continue;

                    // Snap start to 0 if within 5 seconds of episode beginning
                    if (TimeSpan.FromTicks(introStartTicks).TotalSeconds <= 5.0)
                        introStartTicks = 0;

                    // Write markers
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
                        $"ChapterDetect - Applied intro marker from chapter \"{chapter.Name}\" for {episode.FindSeriesName()} S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2}: {TimeSpan.FromTicks(introStartTicks).TotalSeconds:F1}s - {TimeSpan.FromTicks(introEndTicks).TotalSeconds:F1}s");

                    return true;
                }
            }
            catch (Exception e)
            {
                _logger.Debug($"ChapterDetect - Failed for {episode.Name}: {e.Message}");
            }

            return false;
        }

    }
}
