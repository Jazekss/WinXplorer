using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WinXplorer.Services
{
    /// <summary>
    /// Localization. Built-in languages are embedded JSON files (Languages/xx.json, key → text).
    /// Users can add or override a language by dropping xx.json into %APPDATA%\WinXplorer\Languages
    /// (copy Languages\en.json from the repository as a template; "_name" is the language's own name).
    /// Missing keys fall back to English.
    /// </summary>
    public static class Loc
    {
        public sealed record Language(string Code, string Name, bool IsCustom);

        private static readonly string[] BuiltIn = { "en", "lv", "de", "et", "lt", "nb", "pl", "ru", "sv" };
        private static Dictionary<string, string> _en = new(), _cur = new();
        private static string _code = "en";

        public static string Code => _code;
        public static string CustomDir => Path.Combine(AppState.DataDir, "Languages");

        public static void Init(string setting)
        {
            _en = LoadBuiltIn("en") ?? new();
            var code = setting;
            if (string.IsNullOrEmpty(code) || code == "auto")
            {
                var sys = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                if (sys == "no" || sys == "nn") sys = "nb";
                code = Available().Any(l => l.Code == sys) ? sys : "en";
            }
            _code = code;
            _cur = LoadCustom(code) is { } custom
                ? Merge(LoadBuiltIn(code), custom)
                : LoadBuiltIn(code) ?? _en;
        }

        private static Dictionary<string, string> Merge(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            var r = a != null ? new Dictionary<string, string>(a) : new Dictionary<string, string>();
            foreach (var kv in b) r[kv.Key] = kv.Value;
            return r;
        }

        private static Dictionary<string, string> LoadBuiltIn(string code)
        {
            try
            {
                using var s = typeof(Loc).Assembly.GetManifestResourceStream("WinXplorer.Languages." + code + ".json");
                return s == null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(s);
            }
            catch { return null; }
        }

        private static Dictionary<string, string> LoadCustom(string code)
        {
            try
            {
                var f = Path.Combine(CustomDir, code + ".json");
                return File.Exists(f) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f)) : null;
            }
            catch { return null; }
        }

        /// <summary>Built-in languages plus any language files the user added.</summary>
        public static List<Language> Available()
        {
            var list = BuiltIn.Select(c => new Language(c, LoadBuiltIn(c)?.GetValueOrDefault("_name") ?? c, false)).ToList();
            try
            {
                if (Directory.Exists(CustomDir))
                    foreach (var f in Directory.EnumerateFiles(CustomDir, "*.json"))
                    {
                        var code = Path.GetFileNameWithoutExtension(f);
                        if (list.Any(l => l.Code == code)) continue;
                        string name = code;
                        try { name = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f))?.GetValueOrDefault("_name") ?? code; } catch { }
                        list.Add(new Language(code, name, true));
                    }
            }
            catch { }
            return list;
        }

        /// <summary>Writes the English template to the user's language folder so a translation can be started from it.</summary>
        public static string ExportTemplate()
        {
            Directory.CreateDirectory(CustomDir);
            var path = Path.Combine(CustomDir, "template.json.txt");
            var d = new SortedDictionary<string, string>(_en) { ["_name"] = "My language" };
            File.WriteAllText(path, JsonSerializer.Serialize(d, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            return path;
        }

        public static string T(string key)
        {
            if (key == null) return null;
            if (_cur.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) return v;
            if (_en.TryGetValue(key, out v)) return v;
            return key;
        }

        public static string F(string key, params object[] args)
        {
            try { return string.Format(CultureInfo.CurrentCulture, T(key), args); } catch { return T(key); }
        }

        // ---- plurals: keys <Key>One / <Key>Few / <Key>Many (Few only for lt, pl, ru) ----
        private static string PluralForm(long n)
        {
            long m10 = n % 10, m100 = n % 100;
            switch (_code)
            {
                case "lv": return m10 == 1 && m100 != 11 ? "One" : "Many";
                case "lt": return m100 is >= 11 and <= 19 ? "Many" : m10 == 1 ? "One" : m10 >= 2 ? "Few" : "Many";
                case "ru": return m10 == 1 && m100 != 11 ? "One" : m10 is >= 2 and <= 4 && m100 is < 12 or > 14 ? "Few" : "Many";
                case "pl": return n == 1 ? "One" : m10 is >= 2 and <= 4 && m100 is < 12 or > 14 ? "Few" : "Many";
                default: return n == 1 ? "One" : "Many";
            }
        }

        private static string Plural(string key, long n)
        {
            var form = PluralForm(n);
            string k = key + form;
            if (form == "Few" && !_cur.ContainsKey(k)) k = key + "Many";
            return string.Format(CultureInfo.CurrentCulture, T(k), n.ToString("N0", CultureInfo.CurrentCulture));
        }

        public static string Items(int n) => Plural("Items", n);
        public static string Selected(int n) => F("SelectedCount", n.ToString("N0", CultureInfo.CurrentCulture));
        public static string Free(string size) => F("FreeOf", size);
        public static string FilesFolders(int f, int d) => F("FilesFolders", f.ToString("N0"), d.ToString("N0"));
    }
}
