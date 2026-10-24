using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RTLC.LostInVivo
{
    [Serializable] public sealed class MappingData { public Mapping[] entries; }
    [Serializable] public sealed class Mapping { public string original; public string atlas; public string osFamily; }
    [Serializable] public sealed class AtlasData
    {
        public string name; public float size; public float padding; public float atlasWidth; public float atlasHeight;
        public float ascent; public float descent; public float lineHeight; public GlyphRecord[] glyphs;
    }
    [Serializable] public sealed class GlyphRecord
    {
        public int id; public float x; public float y; public float width; public float height;
        public float xOffset; public float yOffset; public float xAdvance;
    }

    internal sealed class FontManager
    {
        public Font UnityFallback { get; private set; }
        private readonly BundleResources resources;
        private readonly ManualLogSource logger;
        private readonly Dictionary<string, TMP_FontAsset> tmpFonts = new Dictionary<string, TMP_FontAsset>();
        private readonly Dictionary<string, string> atlasMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> familyMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Font> unityFonts = new Dictionary<string, Font>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, WorldFontSource> originalWorldFonts = new Dictionary<int, WorldFontSource>();
        private readonly Dictionary<int, Material> worldMaterials = new Dictionary<int, Material>();
        private readonly Dictionary<Material, Font> worldMaterialFonts = new Dictionary<Material, Font>();
        private readonly Dictionary<int, TmpTextFontState> tmpTextStates = new Dictionary<int, TmpTextFontState>();
        private readonly Dictionary<int, TmpFontSource> tmpFontSources = new Dictionary<int, TmpFontSource>();
        private int tmpApplyCount;

        private sealed class TmpFontSource
        {
            public TMP_FontAsset Font;
            public Material Material;
        }

        private sealed class TmpTextFontState
        {
            public TMP_Text Component;
            public TMP_FontAsset OriginalFont;
            public Material OriginalMaterial;
            public TMP_FontAsset ReplacementFont;
        }

        public FontManager(BundleResources resources, ManualLogSource logger)
        {
            this.resources = resources;
            this.logger = logger;

            // The native dynamic Font assets contain their TTF data. No installed
            // Windows font or process-wide font registration is needed.
            UnityFallback = LoadBundledFont("Ithaca");
            unityFonts["Ithaca"] = UnityFallback;
            Font.textureRebuilt += OnFontTextureRebuilt;

            string json = resources.ReadText("Fonts/mappings.json");
            Mapping[] list = null;
            try
            {
                var data = JsonUtility.FromJson<MappingData>(json);
                if (data != null && data.entries != null && data.entries.Length > 0)
                    list = data.entries;
            }
            catch { }

            if (list == null || list.Length == 0)
                list = ParseMappingsFallback(json);

            if (list != null)
            {
                foreach (Mapping mapping in list)
                {
                    if (mapping == null || string.IsNullOrEmpty(mapping.original)) continue;
                    if (!string.IsNullOrEmpty(mapping.atlas)) atlasMappings[mapping.original] = mapping.atlas;
                    // Retain the catalog schema: osFamily is now a bundled family key.
                    if (!string.IsNullOrEmpty(mapping.osFamily)) familyMappings[mapping.original] = mapping.osFamily;
                }
            }
        }

        private Font LoadBundledFont(string family)
        {
            string file;
            if (string.Equals(family, "Ithaca", StringComparison.OrdinalIgnoreCase)) file = "Ithaca.ttf";
            else if (string.Equals(family, "Fixedsys Excelsior", StringComparison.OrdinalIgnoreCase)) file = "FSEX302.ttf";
            else if (string.Equals(family, "MrPixel RTLC", StringComparison.OrdinalIgnoreCase)) file = "MrPixel-RTLC.ttf";
            else if (string.Equals(family, "MrPixel", StringComparison.OrdinalIgnoreCase)) file = "MrPixel.ttf";
            else if (string.Equals(family, "GothicRus Condenced", StringComparison.OrdinalIgnoreCase)) file = "gothicrus-condenced3.ttf";
            else if (string.Equals(family, "Liberation Serif", StringComparison.OrdinalIgnoreCase)) file = "LiberationSerif-Regular.ttf";
            else if (string.Equals(family, "Noto Sans", StringComparison.OrdinalIgnoreCase)) file = "NotoSans-Regular.ttf";
            else throw new InvalidOperationException("Неизвестная гарнитура в бандле: " + family);

            Font font = resources.LoadFont(file);
            if (font == null || !font.dynamic || font.fontSize != 32)
                throw new InvalidOperationException("Некорректный динамический шрифт в бандле: " + file);
            font.name = "RTLC " + family;
            Object.DontDestroyOnLoad(font);
            return font;
        }

        internal Font UnityFont(Font original)
        {
            if (original == null) return UnityFallback;
            if (original == UnityFallback || original.name.StartsWith("RTLC ", StringComparison.Ordinal)) return original;
            string family;
            if (!familyMappings.TryGetValue(original.name, out family))
            {
                string norm = original.name.Replace(" ", "").Replace("_", "");
                foreach (var pair in familyMappings)
                {
                    if (string.Equals(pair.Key.Replace(" ", "").Replace("_", ""), norm, StringComparison.OrdinalIgnoreCase))
                    {
                        family = pair.Value;
                        break;
                    }
                }
                if (family == null) return UnityFallback;
            }
            Font result;
            if (!unityFonts.TryGetValue(family, out result))
            {
                result = LoadBundledFont(family);
                logger.LogInfo("Unity шрифт: " + original.name + " → " + result.name);
                unityFonts[family] = result;
            }
            return result;
        }

        public void Apply(Text text)
        {
            Font font = UnityFont(text.font);
            if (text.font != font) text.font = font;
        }

        internal Font OriginalWorldFont(TextMesh text)
        {
            if (text == null) return null;
            int id = text.GetInstanceID();
            WorldFontSource source;
            if (originalWorldFonts.TryGetValue(id, out source) && source.Component == text && source.Font != null)
            {
                if (text.font == null || text.font == source.Font || text.font.name.StartsWith("RTLC ", StringComparison.Ordinal))
                    return source.Font;
            }
            if (text.font != null && !text.font.name.StartsWith("RTLC ", StringComparison.Ordinal))
                originalWorldFonts[id] = new WorldFontSource { Component = text, Font = text.font };
            return text.font;
        }

        private sealed class WorldFontSource { public TextMesh Component; public Font Font; }

        public void Apply(TextMesh text)
        {
            OriginalWorldFont(text);
            Font font = UnityFont(text.font);
            if (text.font == font) return;
            var renderer = text.GetComponent<Renderer>();
            Material previous = renderer == null ? null : renderer.sharedMaterial;
            text.font = font;
            if (renderer == null) return;
            if (previous == null) { renderer.sharedMaterial = font.material; return; }
            Material material;
            if (!worldMaterials.TryGetValue(previous.GetInstanceID(), out material))
            {
                material = new Material(font.material);
                material.name = "RTLC " + previous.name;
                if (previous.HasProperty("_Color") && material.HasProperty("_Color")) material.color = previous.color;
                material.renderQueue = previous.renderQueue;
                Object.DontDestroyOnLoad(material);
                worldMaterials[previous.GetInstanceID()] = material;
                worldMaterialFonts[material] = font;
            }
            renderer.sharedMaterial = material;
        }

        private void OnFontTextureRebuilt(Font font)
        {
            foreach (var pair in worldMaterialFonts)
                if (pair.Key != null && pair.Value == font && font.material != null)
                    pair.Key.mainTexture = font.material.mainTexture;
        }

        public void Dispose()
        {
            Font.textureRebuilt -= OnFontTextureRebuilt;
            originalWorldFonts.Clear();
            tmpTextStates.Clear();
            tmpFontSources.Clear();
        }

        public void Apply(TMP_Text text)
        {
            Apply(text, text == null ? null : text.text);
        }

        public void Apply(TMP_Text text, string renderedText)
        {
            if (text == null || text.font == null) return;
            if ((++tmpApplyCount & 127) == 0) PruneTmpTextStates();

            int id = text.GetInstanceID();
            TMP_FontAsset current = text.font;
            TmpTextFontState state;
            if (!tmpTextStates.TryGetValue(id, out state) || state.Component != text ||
                (current != state.OriginalFont && current != state.ReplacementFont))
            {
                TmpFontSource source;
                // The game clones its leaderboard prefab after scene scanning. A clone
                // can already carry our font, so recover its original font and material.
                bool inherited = tmpFontSources.TryGetValue(current.GetInstanceID(), out source);
                state = new TmpTextFontState {
                    Component = text,
                    OriginalFont = inherited ? source.Font : current,
                    OriginalMaterial = inherited ? source.Material : text.fontSharedMaterial,
                    ReplacementFont = inherited ? current : null
                };
                tmpTextStates[id] = state;
            }

            TMP_FontAsset original = state.OriginalFont;
            if (original == null) return;
            if (!HasCyrillic(renderedText))
            {
                if (text.font != original)
                {
                    text.font = original;
                    if (state.OriginalMaterial != null) text.fontSharedMaterial = state.OriginalMaterial;
                }
                return;
            }

            // Keep a component's material preset rather than sharing the first
            // material encountered on an unrelated font asset with the same name.
            Material sourceMaterial = state.OriginalMaterial != null ? state.OriginalMaterial : original.material;
            string atlas = "NotoSans-Regular";
            string mapped;
            if (atlasMappings.TryGetValue(original.name, out mapped)) atlas = mapped;
            else if (original.name.StartsWith("MotorolaScreentype", StringComparison.OrdinalIgnoreCase)) atlas = "Ithaca";
            else if (original.name.StartsWith("LiberationSans", StringComparison.OrdinalIgnoreCase)) atlas = "NotoSans-Regular";
            string key = original.GetInstanceID() + "|" + (sourceMaterial == null ? 0 : sourceMaterial.GetInstanceID()) + "|" + atlas;
            TMP_FontAsset replacement;
            if (!tmpFonts.TryGetValue(key, out replacement))
            {
                replacement = LoadTmpFont(atlas, sourceMaterial);
                tmpFonts[key] = replacement;
                tmpFontSources[replacement.GetInstanceID()] = new TmpFontSource { Font = original, Material = sourceMaterial };
                logger.LogInfo("TMP шрифт: " + original.name + " → " + replacement.name);
            }
            state.ReplacementFont = replacement;
            if (text.font != replacement) text.font = replacement;
            if (text.fontSharedMaterial != replacement.material) text.fontSharedMaterial = replacement.material;
        }

        private static bool HasCyrillic(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
                if ((c >= '\u0400' && c <= '\u052f') || (c >= '\u1c80' && c <= '\u1c8f') ||
                    (c >= '\ua640' && c <= '\ua69f')) return true;
            return false;
        }

        private void PruneTmpTextStates()
        {
            var deadWorld = new List<int>();
            foreach (var pair in originalWorldFonts)
                if (pair.Value.Component == null) deadWorld.Add(pair.Key);
            foreach (int id in deadWorld) originalWorldFonts.Remove(id);
            var dead = new List<int>();
            foreach (var pair in tmpTextStates)
                if (pair.Value.Component == null) dead.Add(pair.Key);
            foreach (int id in dead) tmpTextStates.Remove(id);
        }

        private TMP_FontAsset LoadTmpFont(string name, Material sourceMaterial)
        {
            string json = resources.ReadText("Fonts/" + name + ".json");

            AtlasData data = null;
            try
            {
                data = JsonUtility.FromJson<AtlasData>(json);
            }
            catch { }

            if (data == null || data.glyphs == null || data.glyphs.Length == 0)
                data = LoadAtlasFallback(json);

            if (data == null || data.glyphs == null || data.glyphs.Length == 0)
                throw new InvalidOperationException("Пустой атлас " + name);

            var atlas = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(atlas, resources.ReadBytes("Fonts/" + name + ".png"), true))
                throw new InvalidOperationException("Не читается PNG атласа " + name);
            atlas.name = "RTLC " + name + " atlas";
            atlas.filterMode = FilterMode.Bilinear;
            atlas.wrapMode = TextureWrapMode.Clamp;
            Object.DontDestroyOnLoad(atlas);

            var asset = ScriptableObject.CreateInstance<TMP_FontAsset>();
            asset.name = "RTLC " + name;
            asset.fontAssetType = TMP_FontAsset.FontAssetTypes.SDF;
            asset.atlas = atlas;
            Shader shader = sourceMaterial != null ? sourceMaterial.shader : Shader.Find("TextMeshPro/Distance Field");
            if (shader == null) shader = Shader.Find("TextMeshPro/Distance Field");
            if (shader == null) throw new InvalidOperationException("Не найден TMP SDF shader.");
            Material material = new Material(shader);
            if (sourceMaterial != null) material.CopyPropertiesFromMaterial(sourceMaterial);
            material.name = asset.name + " Material";
            material.mainTexture = atlas;
            SetFloat(material, "_TextureWidth", data.atlasWidth);
            SetFloat(material, "_TextureHeight", data.atlasHeight);
            SetFloat(material, "_GradientScale", data.padding + 1);
            SetFloat(material, "_WeightNormal", 0);
            SetFloat(material, "_WeightBold", 0.75f);
            SetFloat(material, "_ScaleX", 1);
            SetFloat(material, "_ScaleY", 1);
            asset.material = material;
            asset.AddFaceInfo(new FaceInfo { Name = data.name, PointSize = data.size, Scale = 1,
                CharacterCount = data.glyphs.Length, LineHeight = data.lineHeight, Baseline = 0,
                Ascender = data.ascent, Descender = data.descent, CapHeight = data.size * 0.72f,
                CenterLine = (data.ascent + data.descent) / 2, Underline = -data.size * 0.1f,
                UnderlineThickness = data.size * 0.06f, strikethrough = data.size * 0.3f,
                strikethroughThickness = data.size * 0.06f, SuperscriptOffset = data.ascent,
                SubscriptOffset = data.descent, SubSize = 0.5f, Padding = data.padding,
                AtlasWidth = data.atlasWidth, AtlasHeight = data.atlasHeight });
            var glyphs = new TMP_Glyph[data.glyphs.Length];
            for (int i = 0; i < glyphs.Length; i++)
            {
                GlyphRecord glyph = data.glyphs[i];
                glyphs[i] = new TMP_Glyph { id = glyph.id, x = glyph.x, y = glyph.y, width = glyph.width,
                    height = glyph.height, xOffset = glyph.xOffset, yOffset = glyph.yOffset, xAdvance = glyph.xAdvance, scale = 1 };
            }
            asset.AddGlyphInfo(glyphs);
            asset.AddKerningInfo(new KerningTable());
            asset.fallbackFontAssets = new List<TMP_FontAsset>();
            asset.ReadFontDefinition();
            Object.DontDestroyOnLoad(asset);
            Object.DontDestroyOnLoad(material);
            return asset;
        }

        private static void SetFloat(Material material, string name, float value)
        {
            if (material.HasProperty(name)) material.SetFloat(name, value);
        }

        private static Mapping[] ParseMappingsFallback(string text)
        {
            var list = new List<Mapping>();
            foreach (Match m in Regex.Matches(text, @"\{[^{}]*\}"))
            {
                string block = m.Value;
                var orig = Regex.Match(block, "\"original\"\\s*:\\s*\"([^\"]*)\"");
                if (!orig.Success) continue;
                var atlas = Regex.Match(block, "\"atlas\"\\s*:\\s*\"([^\"]*)\"");
                var osFam = Regex.Match(block, "\"osFamily\"\\s*:\\s*\"([^\"]*)\"");
                list.Add(new Mapping {
                    original = orig.Groups[1].Value,
                    atlas = atlas.Success ? atlas.Groups[1].Value : null,
                    osFamily = osFam.Success ? osFam.Groups[1].Value : null
                });
            }
            return list.ToArray();
        }

        private static AtlasData LoadAtlasFallback(string json)
        {
            var atlas = new AtlasData();
            atlas.name = ExtractStr(json, "\"name\"");
            atlas.size = ExtractFloat(json, "\"size\"");
            atlas.padding = ExtractFloat(json, "\"padding\"");
            atlas.atlasWidth = ExtractFloat(json, "\"atlasWidth\"");
            atlas.atlasHeight = ExtractFloat(json, "\"atlasHeight\"");
            atlas.ascent = ExtractFloat(json, "\"ascent\"");
            atlas.descent = ExtractFloat(json, "\"descent\"");
            atlas.lineHeight = ExtractFloat(json, "\"lineHeight\"");

            var glyphs = new List<GlyphRecord>();
            int gIdx = json.IndexOf("\"glyphs\"", StringComparison.Ordinal);
            if (gIdx >= 0)
            {
                int start = json.IndexOf('[', gIdx);
                int end = json.LastIndexOf(']');
                if (start >= 0 && end > start)
                {
                    int p = start;
                    while (p < end)
                    {
                        int objStart = json.IndexOf('{', p);
                        if (objStart < 0 || objStart >= end) break;
                        int objEnd = json.IndexOf('}', objStart);
                        if (objEnd < 0) break;
                        string block = json.Substring(objStart, objEnd - objStart + 1);
                        var g = new GlyphRecord();
                        g.id = (int)ExtractFloat(block, "\"id\"");
                        g.x = ExtractFloat(block, "\"x\"");
                        g.y = ExtractFloat(block, "\"y\"");
                        g.width = ExtractFloat(block, "\"width\"");
                        g.height = ExtractFloat(block, "\"height\"");
                        g.xOffset = ExtractFloat(block, "\"xOffset\"");
                        g.yOffset = ExtractFloat(block, "\"yOffset\"");
                        g.xAdvance = ExtractFloat(block, "\"xAdvance\"");
                        glyphs.Add(g);
                        p = objEnd + 1;
                    }
                }
            }
            atlas.glyphs = glyphs.ToArray();
            return atlas;
        }

        private static float ExtractFloat(string text, string key)
        {
            var m = Regex.Match(text, Regex.Escape(key) + @"\s*:\s*(-?[\d.]+)");
            if (m.Success)
            {
                float val;
                if (float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out val))
                    return val;
            }
            return 0f;
        }

        private static string ExtractStr(string text, string key)
        {
            var m = Regex.Match(text, Regex.Escape(key) + @"\s*:\s*""([^""]*)""");
            return m.Success ? m.Groups[1].Value : "";
        }
    }
}
