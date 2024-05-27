using BepInEx.Configuration;
using RiskOfOptions;
using RiskOfOptions.OptionConfigs;
using RiskOfOptions.Options;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DronemeldDevotionFix
{
    public static class PluginConfig
    {
        public static ConfigFile myConfig;

        public static ConfigEntry<bool> enabled;
        public static ConfigEntry<bool> randomizeElites;
        public static ConfigEntry<bool> allowT1Elites;
        public static ConfigEntry<bool> disableFallDamage;
        public static ConfigEntry<bool> showAllMinions;
        public static ConfigEntry<bool> shareItems;
        public static ConfigEntry<bool> enableDebugging;

        public const string GENERAL = "01 - General";
        public const string EXPERIMENTAL = "02 - Experimental";

        internal static void ReadConfig()
        {
            InitROO();
            enabled = BindAndOptions(GENERAL, "Enabled", true, "Set to false to disable all DronemeldDevotionFix changes. Does not affect the base Dronemeld plugin.", true);
            randomizeElites = BindAndOptions(GENERAL, "Reroll Elite Elder Lemurians", true, "If true, fully evolved Elite Elder Lemurians will reroll to a new elite type after each boss is defeated.");
            allowT1Elites = BindAndOptions(GENERAL, "Allow Any Randomized Elite Tier", false, "If true, fully evolved Elite Elder Lemurians will be able to reroll into any elite type." +
                " If false, only the initial elite type can be tier 1.\r\n\r\nRequires \"Randomize Evolved Elites\" to be enabled.");
            disableFallDamage = BindAndOptions(GENERAL, "Disable Fall Damage", true, "If true, prevents Lemurians from taking fall damage.");

            showAllMinions = BindAndOptions(EXPERIMENTAL, "Show All Minions on Scoreboard", false, "If true, the scoreboard will display all of the Lemurians and their inventories." +
                " Intended to be used with \"Share Lemurian Items\" set to false.");
            shareItems = BindAndOptions(EXPERIMENTAL, "Share Lemurian Items", true, "If true, Lemurians will use items from every other Lemurian that you currently control. " +
                "Items are still lost on death when their original owner dies. If false, Lemurians will only use items that have been given to them.");
            enableDebugging = BindAndOptions(EXPERIMENTAL, "Enable Debugging", false, "For dev use, enables console debug messages. Keep this off.");
        }

        public static void InitROO()
        {
            if (DronemeldFixPlugin.rooInstalled)
            {
                var sprite = LoadSprite();
                if (sprite != null)
                {
                    ModSettingsManager.SetModIcon(sprite);
                }
                ModSettingsManager.SetModDescription("Devotion Artifact but better.");
            }
        }

        public static Sprite LoadSprite()
        {
            var filePath = Path.Combine(Assembly.GetExecutingAssembly().Location, "icon.png");

            if (File.Exists(filePath))
            {
                // i hate this tbh
                Texture2D texture = new(1, 1);
                texture.LoadImage(File.ReadAllBytes(filePath));

                if (texture != null)
                {
                    var bounds = new Rect(0, 0, texture.width, texture.height);
                    return Sprite.Create(texture, bounds, new Vector2(bounds.width * 0.5f, bounds.height * 0.5f));
                }
            }

            return null;
        }

        #region Config
        public static ConfigEntry<T> BindAndOptions<T>(string section, string name, T defaultValue, string description = "", bool restartRequired = false)
        {
            if (string.IsNullOrEmpty(description))
            {
                description = name;
            }

            if (restartRequired)
            {
                description += " (restart required)";
            }

            ConfigEntry<T> configEntry = myConfig.Bind(section, name, defaultValue, description);

            if (DronemeldFixPlugin.rooInstalled)
            {
                TryRegisterOption(configEntry, restartRequired);
            }

            return configEntry;
        }

        public static ConfigEntry<float> BindAndOptionsSlider(string section, string name, float defaultValue, string description = "", float min = 0, float max = 20, bool restartRequired = false)
        {
            if (string.IsNullOrEmpty(description))
            {
                description = name;
            }

            description += " (Default: " + defaultValue + ")";

            if (restartRequired)
            {
                description += " (restart required)";
            }

            ConfigEntry<float> configEntry = myConfig.Bind(section, name, defaultValue, description);

            if (DronemeldFixPlugin.rooInstalled)
            {
                TryRegisterOptionSlider(configEntry, min, max, restartRequired);
            }

            return configEntry;
        }

        public static ConfigEntry<int> BindAndOptionsSlider(string section, string name, int defaultValue, string description = "", int min = 0, int max = 20, bool restartRequired = false)
        {
            if (string.IsNullOrEmpty(description))
            {
                description = name;
            }

            description += " (Default: " + defaultValue + ")";

            if (restartRequired)
            {
                description += " (restart required)";
            }

            ConfigEntry<int> configEntry = myConfig.Bind(section, name, defaultValue, description);

            if (DronemeldFixPlugin.rooInstalled)
            {
                TryRegisterOptionSlider(configEntry, min, max, restartRequired);
            }

            return configEntry;
        }
        #endregion

        #region RoO
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        private static void TryRegisterOption<T>(ConfigEntry<T> entry, bool restartRequired)
        {
            if (entry is ConfigEntry<string> stringEntry)
            {
                ModSettingsManager.AddOption(new StringInputFieldOption(stringEntry, restartRequired));
            }
            if (entry is ConfigEntry<float>)
            {
                ModSettingsManager.AddOption(new SliderOption(entry as ConfigEntry<float>, new SliderConfig()
                {
                    min = 0,
                    max = 20,
                    formatString = "{0:0.00}",
                    restartRequired = restartRequired
                }));
            }
            if (entry is ConfigEntry<int>)
            {
                ModSettingsManager.AddOption(new IntSliderOption(entry as ConfigEntry<int>, restartRequired));
            }
            if (entry is ConfigEntry<bool>)
            {
                ModSettingsManager.AddOption(new CheckBoxOption(entry as ConfigEntry<bool>, restartRequired));
            }
            if (entry is ConfigEntry<KeyboardShortcut>)
            {
                ModSettingsManager.AddOption(new KeyBindOption(entry as ConfigEntry<KeyboardShortcut>, restartRequired));
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        private static void TryRegisterOptionSlider(ConfigEntry<int> entry, int min, int max, bool restartRequired)
        {
            ModSettingsManager.AddOption(new IntSliderOption(entry as ConfigEntry<int>, new IntSliderConfig()
            {
                min = min,
                max = max,
                formatString = "{0:0.00}",
                restartRequired = restartRequired
            }));
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        private static void TryRegisterOptionSlider(ConfigEntry<float> entry, float min, float max, bool restartRequired)
        {
            ModSettingsManager.AddOption(new SliderOption(entry as ConfigEntry<float>, new SliderConfig()
            {
                min = min,
                max = max,
                formatString = "{0:0.00}",
                restartRequired = restartRequired
            }));
        }
        #endregion
    }
}