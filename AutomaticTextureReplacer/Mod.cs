using Game;
using Game.Modding;
using Game.SceneFlow;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Colossal.UI;
using Game.Tools;
using Game.Common;
using System;
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
        public static ILog Log { get; } = LogManager.GetLogger("AutomaticTextureReplacer").SetShowsErrorsInUI(false);
        public static AutomaticTextureReplacerSettings Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            ClearDiagnosticLog();
            Log.Info("=== Automatic Texture Replacer session started ===");

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
            updateSystem.UpdateBefore<SurfaceReplacementSystem, ToolOutputBarrier>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateBefore<NewBuildingSurfaceReplacementSystem, GenerateAreasSystem>(SystemUpdatePhase.Modification1);
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

        private static void ClearDiagnosticLog()
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData",
                "LocalLow",
                "Colossal Order",
                "Cities Skylines II",
                "Logs",
                "AutomaticTextureReplacer.log");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                File.WriteAllText(logPath, string.Empty);
            }
            catch (Exception exception)
            {
                Log.Warn(exception, "Could not clear the mod diagnostic log at startup");
            }
        }
    }
}
