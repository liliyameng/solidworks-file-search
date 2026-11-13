using System.Globalization;

namespace SwSearch.WebUi
{
    /// <summary>
    /// UI language resolution shared by every host process (desktop app, SOLIDWORKS
    /// add-in, property viewer). This file is also compiled by the add-in's C# 5
    /// csc.exe build, so it must avoid C# 6+ syntax and nullable annotations.
    /// </summary>
    public static class UiLanguage
    {
        /// <summary>Follows the operating system UI language (default).</summary>
        public const string Auto = "auto";

        /// <summary>Used when the system language matches none of the supported languages.</summary>
        public const string Fallback = "en";

        /// <summary>Simplified Chinese, the product's native language.</summary>
        public const string Chinese = "zh-CN";

        /// <summary>Configured values in settings.json and the settings dropdown order.</summary>
        public static readonly string[] ConfiguredValues = new string[]
        {
            Auto,
            Chinese,
            "en",
            "ja",
            "de",
            "fr",
            "es"
        };

        /// <summary>Returns true when the value is a valid persisted "language" setting.</summary>
        public static bool IsValidConfiguredValue(string value)
        {
            if (value == null || value.Length == 0)
            {
                return false;
            }
            for (int i = 0; i < ConfiguredValues.Length; i++)
            {
                if (string.Equals(ConfiguredValues[i], value, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Returns the value if valid, otherwise "auto".</summary>
        public static string SanitizeConfigured(string value)
        {
            if (IsValidConfiguredValue(value))
            {
                return value;
            }
            return Auto;
        }

        /// <summary>
        /// Resolves the configured setting into the effective UI language:
        /// "auto" (or anything invalid) follows the system UI language, with
        /// English as the fallback when the system language is unsupported.
        /// </summary>
        public static string ResolveEffective(string configuredValue)
        {
            string configured = SanitizeConfigured(configuredValue);
            if (!string.Equals(configured, Auto, System.StringComparison.Ordinal))
            {
                return configured;
            }
            return ResolveSystem();
        }

        /// <summary>Maps the current system UI culture onto a supported language.</summary>
        public static string ResolveSystem()
        {
            string result = FromCulture(CultureInfo.CurrentUICulture);
            if (result.Length > 0)
            {
                return result;
            }
            return Fallback;
        }

        /// <summary>Returns the supported language for a culture, or an empty string.</summary>
        public static string FromCulture(CultureInfo culture)
        {
            if (culture == null)
            {
                return string.Empty;
            }
            string name = culture.Name;
            if (!string.IsNullOrEmpty(name))
            {
                string byName = FromTwoLetter(TwoLetterPrefix(name));
                if (byName.Length > 0)
                {
                    return byName;
                }
            }
            return FromTwoLetter(culture.TwoLetterISOLanguageName);
        }

        private static string TwoLetterPrefix(string cultureName)
        {
            string lowered = cultureName.ToLowerInvariant();
            int dash = lowered.IndexOf('-');
            if (dash > 0)
            {
                return lowered.Substring(0, dash);
            }
            return lowered;
        }

        private static string FromTwoLetter(string twoLetter)
        {
            if (string.IsNullOrEmpty(twoLetter))
            {
                return string.Empty;
            }
            if (twoLetter == "zh")
            {
                return Chinese;
            }
            if (twoLetter == "en" || twoLetter == "de" || twoLetter == "fr" || twoLetter == "ja" || twoLetter == "es")
            {
                return twoLetter;
            }
            return string.Empty;
        }
    }
}
