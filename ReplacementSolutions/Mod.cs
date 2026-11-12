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
    /// Entry point for the Replacement Solutions mod.
    /// Register systems and other game integrations in <see cref="OnLoad"/>.
    /// </summary>
    public sealed class Mod : IMod
    {
        public static ILog Log { get; } = LogManager.GetLogger("ReplacementSolutions").SetShowsErrorsInUI(false);
        public static ReplacementSolutionsSettings Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            ClearDiagnosticLog();
            Log.Info("=== Replacement Solutions session started ===");

            bool migratedSettings = MigrateLegacySettingsFile();
            Settings = new ReplacementSolutionsSettings(this);
            Settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
            AssetDatabase.global.LoadSettings(
                "ReplacementSolutions",
                Settings,
                new ReplacementSolutionsSettings(this));
            if (migratedSettings)
            {
                Settings.RulesJson = "[]";
                Settings.AreaRulesJson = "[]";
                Settings.ApplyAndSave();
            }

            string modPath = GetAssemblyDirectory();
            if (modPath != null)
            {
                UIManager.defaultUISystem.AddHostLocation("replacementsolutions", modPath);
            }

            updateSystem.UpdateAt<ReplacementSolutionsUISystem>(SystemUpdatePhase.UIUpdate);
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
                "ReplacementSolutions.log");

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

        private static bool MigrateLegacySettingsFile()
        {
            string settingsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "Colossal Order", "Cities Skylines II");
            string oldPath = Path.Combine(settingsDirectory, "AutomaticTextureReplacer.coc");
            string newPath = Path.Combine(settingsDirectory, "ReplacementSolutions.coc");
            if (!File.Exists(oldPath) || File.Exists(newPath))
            {
                return false;
            }

            try
            {
                string contents = File.ReadAllText(oldPath);
                int headerEnd = contents.IndexOfAny(new[] { '\r', '\n' });
                if (headerEnd < 0 ||
                    !string.Equals(contents.Substring(0, headerEnd), "AutomaticTextureReplacer", StringComparison.Ordinal))
                {
                    Log.Warn("Legacy settings file has an unexpected header; migration skipped");
                    return false;
                }

                File.WriteAllText(newPath, "ReplacementSolutions" + contents.Substring(headerEnd));
                Log.Info("Legacy settings copied to ReplacementSolutions.coc");
                return true;
            }
            catch (Exception exception)
            {
                Log.Warn(exception, "Could not migrate legacy mod settings");
                return false;
            }
        }
    }
}
