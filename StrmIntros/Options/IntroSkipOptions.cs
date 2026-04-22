using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmIntros.Properties;
using System.Collections.Generic;
using System.ComponentModel;

namespace StrmIntros.Options
{
    public class IntroSkipOptions : EditableOptionsBase
    {
        [DisplayNameL("PluginOptions_IntroSkipOptions_Intro_Credits_Detection", typeof(Resources))]
        public override string EditorTitle => Resources.PluginOptions_IntroSkipOptions_Intro_Credits_Detection;
        
        [DisplayNameL("IntroSkipOptions_UnlockIntroSkip_Built_in_Intro_Skip_Enhanced", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_UnlockIntroSkip_Unlock_Strm_support_for_built_in_intro_skip_detection", typeof(Resources))]
        [Required]
        public bool UnlockIntroSkip { get; set; } = false;

        [Browsable(false)]
        public int IntroDetectionFingerprintMinutes { get; set; } = 10;

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> LibraryList { get; set; }

        [DisplayNameL("IntroSkipOptions_LibraryScope_Library_Scope", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_LibraryScope_TV_shows_library_scope_to_detect__Blank_includes_all_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        public string LibraryScope { get; set; } = string.Empty;

        [DisplayNameL("IntroSkipOptions_IntroDbApiKey_IntroDb_API_Key", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_IntroDbApiKey_API_key_for_IntroDb_publishing", typeof(Resources))]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        public string IntroDbApiKey { get; set; } = string.Empty;

        [DisplayNameL("IntroSkipOptions_EnableIntroDbPublish_Enable_IntroDb_Publish", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_EnableIntroDbPublish_Publish_locally_detected_intros_to_IntroDb", typeof(Resources))]
        [Required]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        public bool EnableIntroDbPublish { get; set; } = false;

        [DisplayNameL("IntroSkipOptions_TheIntroDbApiKey_TheIntroDb_API_Key", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_TheIntroDbApiKey_API_key_for_TheIntroDb", typeof(Resources))]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        public string TheIntroDbApiKey { get; set; } = string.Empty;

        [DisplayNameL("IntroSkipOptions_EnableTheIntroDbPublish_Enable_TheIntroDb_Publish", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_EnableTheIntroDbPublish_Publish_locally_detected_intros_to_TheIntroDb", typeof(Resources))]
        [Required]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        public bool EnableTheIntroDbPublish { get; set; } = false;

        [DisplayNameL("IntroSkipOptions_PublicMetaDbApiKey_PublicMetaDb_API_Key", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_PublicMetaDbApiKey_API_key_for_PublicMetaDb", typeof(Resources))]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        public string PublicMetaDbApiKey { get; set; } = string.Empty;

        [DisplayNameL("IntroSkipOptions_EnablePublicMetaDbPublish_Enable_PublicMetaDb_Publish", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_EnablePublicMetaDbPublish_Publish_locally_detected_intros_to_PublicMetaDb", typeof(Resources))]
        [Required]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        public bool EnablePublicMetaDbPublish { get; set; } = false;

    }
}
