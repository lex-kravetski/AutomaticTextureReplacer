using Game;
using Game.Modding;
using Game.SceneFlow;
using Colossal.IO.AssetDatabase;
using Colossal.UI;
using System.IO;
using System.Reflection;

namespace AutomaticTextureReplacer
{
    /// <summary>
    /// Entry point for the Automatic Texture Replacer mod.
    /// Register systems and other game integrations in <see cref="OnLoad"/>.
    /// </summary>
    public sealed class Mod : IMod
    {
        public static AutomaticTextureReplacerSettings Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Settings = new AutomaticTextureReplacerSettings(this);
            Settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
            AssetDatabase.global.LoadSettings(
                "AutomaticTextureReplacer",
                Settings,
                new AutomaticTextureReplacerSettings(this));

            string modPath = GetAssemblyDirectory();
            if (modPath != null)
            {
                UIManager.defaultUISystem.AddHostLocation("automatictexturereplacer", modPath);
            }

            updateSystem.UpdateAt<AutomaticTextureReplacerUISystem>(SystemUpdatePhase.UIUpdate);
        }

        public void OnDispose()
        {
            if (Settings != null)
            {
                Settings.ApplyAndSave();
                Settings.UnregisterInOptionsUI();
                Settings = null;
            }
        }

        private static string GetAssemblyDirectory()
        {
            string assemblyName = Assembly.GetExecutingAssembly().FullName;
            ExecutableAsset modAsset = AssetDatabase.global.GetAsset(
                SearchFilter<ExecutableAsset>.ByCondition(x => x.definition?.FullName == assemblyName));
            return modAsset == null ? null : Path.GetDirectoryName(modAsset.GetMeta().path);
        }
    }
}
