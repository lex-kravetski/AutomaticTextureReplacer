using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using System.Collections.Generic;

namespace AutomaticTextureReplacer
{
    [FileLocation("ReplacementSolutions")]
    public sealed class ReplacementSolutionsSettings : ModSetting
    {
        public const string kMainSection = "Main";

        public ReplacementSolutionsSettings(IMod mod) : base(mod)
        {
        }

        [SettingsUISection(kMainSection)]
        [SettingsUISlider(min = 80, max = 200, step = 5)]
        public int DialogFontSize { get; set; } = 100;

        [SettingsUISection(kMainSection)]
        public bool ShowInGameSurfacesAsTargets { get; set; }

        [SettingsUISection(kMainSection)]
        public bool ShowSpecialIndustrySurfacesAsTargets { get; set; }

        [SettingsUISection(kMainSection)]
        public bool ShowModSurfacesAsSources { get; set; }

        [SettingsUISection(kMainSection)]
        public bool ShowRevertAllButton { get; set; }

        [SettingsUIHidden]
        public bool DialogOpen { get; set; }

        [SettingsUIHidden]
        public string ReplacementScope { get; set; } = "assets";

        [SettingsUIHidden]
        public string ActiveMainTab { get; set; } = "surfaces";

        [SettingsUIHidden]
        public bool AlsoNewBuildings { get; set; }

        // -1 means that the dialog has not been positioned by the player yet.
        [SettingsUIHidden]
        public int DialogX { get; set; } = -1;

        [SettingsUIHidden]
        public int DialogY { get; set; } = -1;

        [SettingsUIHidden]
        public int DialogWidth { get; set; } = -1;

        [SettingsUIHidden]
        public int DialogHeight { get; set; } = -1;

        [SettingsUIHidden]
        public int SurfacePickerX { get; set; } = -1;

        [SettingsUIHidden]
        public int SurfacePickerY { get; set; } = -1;

        [SettingsUIHidden]
        public int SurfacePickerWidth { get; set; } = -1;

        [SettingsUIHidden]
        public int SurfacePickerHeight { get; set; } = -1;

        [SettingsUIHidden]
        public bool SurfacePickerOpen { get; set; }

        [SettingsUIHidden]
        public string SurfacePickerContextJson { get; set; } = "";

        [SettingsUIHidden]
        public string SurfacePickerCollapsedGroupsJson { get; set; } = "{}";

        [SettingsUIHidden]
        public string RulesJson { get; set; } = "[]";

        [SettingsUIHidden]
        public string AreaRulesJson { get; set; } = "[]";

        public override void SetDefaults()
        {
            DialogOpen = false;
            DialogX = -1;
            DialogY = -1;
            DialogWidth = -1;
            DialogHeight = -1;
            SurfacePickerX = -1;
            SurfacePickerY = -1;
            SurfacePickerWidth = -1;
            SurfacePickerHeight = -1;
            SurfacePickerOpen = false;
            SurfacePickerContextJson = "";
            SurfacePickerCollapsedGroupsJson = "{}";
            RulesJson = "[]";
            AreaRulesJson = "[]";
            DialogFontSize = 100;
            ShowInGameSurfacesAsTargets = false;
            ShowSpecialIndustrySurfacesAsTargets = false;
            ShowModSurfacesAsSources = false;
            ShowRevertAllButton = false;
            ReplacementScope = "assets";
            ActiveMainTab = "surfaces";
            AlsoNewBuildings = false;
        }
    }

    public sealed class LocaleEN : IDictionarySource
    {
        private readonly ReplacementSolutionsSettings m_Settings;

        public LocaleEN(ReplacementSolutionsSettings settings)
        {
            m_Settings = settings;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Settings.GetSettingsLocaleID(), "Replacement Solutions" },
                { m_Settings.GetOptionTabLocaleID(ReplacementSolutionsSettings.kMainSection), "Main" },
                { m_Settings.GetOptionLabelLocaleID(nameof(ReplacementSolutionsSettings.DialogFontSize)), "Dialog content font size (%)" },
                { m_Settings.GetOptionDescLocaleID(nameof(ReplacementSolutionsSettings.DialogFontSize)), "Text size inside the dialog. The dialog title is not affected." },
                { m_Settings.GetOptionLabelLocaleID(nameof(ReplacementSolutionsSettings.ShowInGameSurfacesAsTargets)), "Show in-game surfaces as replacement targets" },
                { m_Settings.GetOptionDescLocaleID(nameof(ReplacementSolutionsSettings.ShowInGameSurfacesAsTargets)), "Include built-in in-game surfaces in the target surface picker." },
                { m_Settings.GetOptionLabelLocaleID(nameof(ReplacementSolutionsSettings.ShowSpecialIndustrySurfacesAsTargets)), "Show special industry surfaces as replacement targets" },
                { m_Settings.GetOptionDescLocaleID(nameof(ReplacementSolutionsSettings.ShowSpecialIndustrySurfacesAsTargets)), "Include special industry surfaces in the target surface picker." },
                { m_Settings.GetOptionLabelLocaleID(nameof(ReplacementSolutionsSettings.ShowModSurfacesAsSources)), "Show mod surfaces as replacement sources" },
                { m_Settings.GetOptionDescLocaleID(nameof(ReplacementSolutionsSettings.ShowModSurfacesAsSources)), "Include mod-provided surfaces in the source surface picker." },
                { m_Settings.GetOptionLabelLocaleID(nameof(ReplacementSolutionsSettings.ShowRevertAllButton)), "Show Revert All button" },
                { m_Settings.GetOptionDescLocaleID(nameof(ReplacementSolutionsSettings.ShowRevertAllButton)), "Show the button that restores surface prefabs from their default assets." },
            };
        }

        public void Unload()
        {
        }
    }
}
