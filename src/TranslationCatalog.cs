using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace RTLC.LostInVivo
{
    internal sealed class TranslationCatalog
    {
        private readonly Dictionary<string, string> exact = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<CompiledRule> rules = new List<CompiledRule>();
        private readonly Action<string> missing;
        private static readonly Regex WhiteSpace = new Regex(@"\s+", RegexOptions.CultureInvariant);
        private static readonly Regex Latin = new Regex("[A-Za-z]{2,}", RegexOptions.CultureInvariant);
        public int Count { get { return exact.Count; } }

        public TranslationCatalog(BundleResources resources, Action<string> missing)
        {
            this.missing = missing;
            string json = resources.ReadText("translations/ru.json");
            List<KeyValuePair<string, string>> items = null;
            try
            {
                var data = JsonUtility.FromJson<Data>(json);
                if (data != null && data.entries != null && data.entries.Length > 0)
                {
                    items = new List<KeyValuePair<string, string>>(data.entries.Length);
                    foreach (var e in data.entries)
                        if (e != null && !string.IsNullOrEmpty(e.source) && !string.IsNullOrEmpty(e.translation))
                            items.Add(new KeyValuePair<string, string>(e.source, e.translation));
                }
            }
            catch { }

            if (items == null || items.Count == 0)
                items = ParseEntriesFallback(json);

            if (items == null || items.Count == 0)
                throw new InvalidOperationException("Не удалось разобрать словарь ru.json");

            foreach (var pair in items)
            {
                exact[pair.Key] = pair.Value;
                string key = Normalize(pair.Key);
                if (!normalized.ContainsKey(key))
                    normalized[key] = pair.Value;
            }

            if (resources.Contains("translations/rules.json"))
            {
                string ruleJson = resources.ReadText("translations/rules.json");
                List<KeyValuePair<string, string>> rulePairs = null;
                try
                {
                    var ruleData = JsonUtility.FromJson<RuleData>(ruleJson);
                    if (ruleData != null && ruleData.rules != null)
                    {
                        rulePairs = new List<KeyValuePair<string, string>>(ruleData.rules.Length);
                        foreach (var r in ruleData.rules)
                            if (r != null && !string.IsNullOrEmpty(r.pattern))
                                rulePairs.Add(new KeyValuePair<string, string>(r.pattern, r.replacement ?? ""));
                    }
                }
                catch { }

                if (rulePairs == null || rulePairs.Count == 0)
                    rulePairs = ParseRulesFallback(ruleJson);

                if (rulePairs != null)
                {
                    foreach (var pair in rulePairs)
                    {
                        try
                        {
                            rules.Add(new CompiledRule { Regex = new Regex(pair.Key, RegexOptions.CultureInvariant), Replacement = pair.Value });
                        }
                        catch { }
                    }
                }
            }
        }

        public string Translate(string source)
        {
            if (string.IsNullOrEmpty(source)) return source;
            string result;
            if (cache.TryGetValue(source, out result)) return result;
            result = TranslateCore(source, true);
            if (result.IndexOf("<br", StringComparison.OrdinalIgnoreCase) >= 0)
                result = result.Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n").Replace("<BR>", "\n").Replace("<BR/>", "\n");
            // Bound the cache: dynamic counters and timer strings must not grow it forever.
            if (cache.Count >= 12000) cache.Clear();
            cache[source] = result;
            if (result == source && Latin.IsMatch(source)) missing(source);
            return result;
        }

        private string TranslateCore(string source, bool lines)
        {
            string result;
            if (exact.TryGetValue(source, out result)) return result;
            if (normalized.TryGetValue(Normalize(source), out result)) return result;
            string clean = source.Replace("\r\n", "\n").Replace("\r", "\n").Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n");
            if (lines && clean.IndexOf('\n') >= 0)
            {
                string[] pieces = clean.Split('\n');
                bool changed = false;
                for (int i = 0; i < pieces.Length; i++)
                {
                    string next = TranslateCore(pieces[i], false);
                    if (next != pieces[i]) changed = true;
                    pieces[i] = next;
                }
                if (changed) return string.Join("\n", pieces);
            }
            foreach (CompiledRule rule in rules)
                if (rule.Regex.IsMatch(clean)) return rule.Regex.Replace(clean, rule.Replacement);
            return source;
        }

        private static string Normalize(string source)
        {
            return WhiteSpace.Replace(source.Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n"), " ").Trim();
        }

        private static List<KeyValuePair<string, string>> ParseEntriesFallback(string text)
        {
            var entries = new List<KeyValuePair<string, string>>();
            int i = 0;
            while (i < text.Length)
            {
                int idx = text.IndexOf("\"source\"", i, StringComparison.Ordinal);
                if (idx < 0) break;
                int c1 = text.IndexOf(':', idx + 8);
                if (c1 < 0) break;
                string sVal = ReadJsonString(text, ref c1);
                int tIdx = text.IndexOf("\"translation\"", c1, StringComparison.Ordinal);
                if (tIdx < 0) break;
                int c2 = text.IndexOf(':', tIdx + 13);
                if (c2 < 0) break;
                string tVal = ReadJsonString(text, ref c2);
                if (sVal != null && tVal != null)
                    entries.Add(new KeyValuePair<string, string>(sVal, tVal));
                i = c2;
            }
            return entries;
        }

        private static List<KeyValuePair<string, string>> ParseRulesFallback(string text)
        {
            var list = new List<KeyValuePair<string, string>>();
            int i = 0;
            while (i < text.Length)
            {
                int idx = text.IndexOf("\"pattern\"", i, StringComparison.Ordinal);
                if (idx < 0) break;
                int c1 = text.IndexOf(':', idx + 9);
                if (c1 < 0) break;
                string pVal = ReadJsonString(text, ref c1);
                int rIdx = text.IndexOf("\"replacement\"", c1, StringComparison.Ordinal);
                if (rIdx < 0) break;
                int c2 = text.IndexOf(':', rIdx + 13);
                if (c2 < 0) break;
                string rVal = ReadJsonString(text, ref c2);
                if (pVal != null && rVal != null)
                    list.Add(new KeyValuePair<string, string>(pVal, rVal));
                i = c2;
            }
            return list;
        }

        private static string ReadJsonString(string text, ref int start)
        {
            int p = text.IndexOf('\"', start);
            if (p < 0) { start = text.Length; return null; }
            p++;
            var sb = new StringBuilder();
            while (p < text.Length)
            {
                char c = text[p];
                if (c == '\"') { start = p + 1; return sb.ToString(); }
                if (c == '\\')
                {
                    p++;
                    if (p >= text.Length) break;
                    char nxt = text[p];
                    if (nxt == '\"') sb.Append('\"');
                    else if (nxt == '\\') sb.Append('\\');
                    else if (nxt == '/') sb.Append('/');
                    else if (nxt == 'n') sb.Append('\n');
                    else if (nxt == 'r') sb.Append('\r');
                    else if (nxt == 't') sb.Append('\t');
                    else if (nxt == 'u' && p + 4 < text.Length)
                    {
                        try
                        {
                            int code = Convert.ToInt32(text.Substring(p + 1, 4), 16);
                            sb.Append((char)code);
                            p += 4;
                        }
                        catch { sb.Append(nxt); }
                    }
                    else sb.Append(nxt);
                }
                else sb.Append(c);
                p++;
            }
            start = text.Length;
            return null;
        }

        [Serializable] public sealed class Data { public Entry[] entries; }
        [Serializable] public sealed class Entry { public string source; public string translation; }
        [Serializable] public sealed class RuleData { public Rule[] rules; }
        [Serializable] public sealed class Rule { public string pattern; public string replacement; }
        private sealed class CompiledRule { public Regex Regex; public string Replacement; }
    }
}
