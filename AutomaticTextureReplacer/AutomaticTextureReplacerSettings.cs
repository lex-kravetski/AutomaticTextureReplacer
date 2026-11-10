using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using System.Collections.Generic;

namespace AutomaticTextureReplacer
{
    [FileLocation("AutomaticTextureReplacer")]
    public sealed class AutomaticTextureReplacerSettings : ModSetting
    {
        public const string kMainSection = "Main";

        public AutomaticTextureReplacerSettings(IMod mod) : base(mod)
        {
        }

        [SettingsUISection(kMainSection)]
        [SettingsUISlider(min = 80, max = 200, step = 5)]
        public int DialogFontSize { get; set; } = 100;

        [SettingsUIHidden]
        public bool DialogOpen { get; set; } = true;

        // -1 means that the dialog has not been positioned by the player yet.
        [SettingsUIHidden]
        public int DialogX { get; set; } = -1;

        [SettingsUIHidden]
        public int DialogY { get; set; } = -1;

        [SettingsUIHidden]
        public int DialogWidth { get; set; } = 440;

        [SettingsUIHidden]
        public int DialogHeight { get; set; } = 300;

        [SettingsUIHidden]
        public int SurfacePickerX { get; set; } = -1;

        [SettingsUIHidden]
        public int SurfacePickerY { get; set; } = -1;

        [SettingsUIHidden]
        public int SurfacePickerWidth { get; set; } = 920;

        [SettingsUIHidden]
        public int SurfacePickerHeight { get; set; } = 680;

        [SettingsUIHidden]
        public bool SurfacePickerOpen { get; set; }

        public override void SetDefaults()
        {
            DialogOpen = true;
            DialogX = -1;
            DialogY = -1;
            DialogWidth = 440;
            DialogHeight = 300;
            SurfacePickerX = -1;
            SurfacePickerY = -1;
            SurfacePickerWidth = 920;
            SurfacePickerHeight = 680;
            SurfacePickerOpen = false;
            DialogFontSize = 100;
        }
    }

    public sealed class LocaleEN : IDictionarySource
    {
        private readonly AutomaticTextureReplacerSettings m_Settings;

        public LocaleEN(AutomaticTextureReplacerSettings settings)
        {
            m_Settings = settings;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Settings.GetSettingsLocaleID(), "Automatic Texture Replacer" },
                { m_Settings.GetOptionTabLocaleID(AutomaticTextureReplacerSettings.kMainSection), "Main" },
                { m_Settings.GetOptionLabelLocaleID(nameof(AutomaticTextureReplacerSettings.DialogFontSize)), "Dialog content font size (%)" },
                { m_Settings.GetOptionDescLocaleID(nameof(AutomaticTextureReplacerSettings.DialogFontSize)), "Text size inside the dialog. The dialog title is not affected." },
            };
        }

        public void Unload()
        {
        }
    }
}
