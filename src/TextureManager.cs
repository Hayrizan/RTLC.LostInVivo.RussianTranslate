using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace RTLC.LostInVivo
{
    [Serializable] public sealed class Manifest { public Entry[] entries; }
    [Serializable] public sealed class Entry
    {
        public string name; public int width; public int height; public string file; public bool pointFilter; public bool clamp;
        public bool overlay; public string mask; public bool linear;
    }

    internal sealed class TextureManager
    {
        private readonly Dictionary<string, TextureLayer> textures = new Dictionary<string, TextureLayer>(StringComparer.Ordinal);
        private readonly Dictionary<int, Texture2D> composed = new Dictionary<int, Texture2D>();
        private readonly Dictionary<int, Material> materials = new Dictionary<int, Material>();
        private readonly HashSet<int> ownMaterials = new HashSet<int>();
        private readonly Dictionary<int, Sprite> sprites = new Dictionary<int, Sprite>();
        private readonly ConfigEntry<bool> enabled;

        public TextureManager(BundleResources resources, ManualLogSource logger, ConfigEntry<bool> enabled)
        {
            this.enabled = enabled;
            string json = resources.ReadText("Textures/manifest.json");
            Entry[] list = null;
            try
            {
                var data = JsonUtility.FromJson<Manifest>(json);
                if (data != null && data.entries != null && data.entries.Length > 0)
                    list = data.entries;
            }
            catch { }

            if (list == null || list.Length == 0)
                list = ParseManifestFallback(json);

            if (list == null) return;

            foreach (Entry entry in list)
            {
                if (entry == null || string.IsNullOrEmpty(entry.name) || string.IsNullOrEmpty(entry.file)) continue;
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, entry.linear);
                if (!ImageConversion.LoadImage(texture, resources.ReadBytes("Textures/" + entry.file), !entry.overlay))
                    throw new InvalidOperationException("Неверная текстура в бандле: " + entry.file);
                if (texture.width != entry.width || texture.height != entry.height)
                    throw new InvalidOperationException("Размер замены не совпадает с manifest: " + entry.file);
                texture.name = "RTLC " + entry.name;
                texture.filterMode = entry.pointFilter ? FilterMode.Point : FilterMode.Bilinear;
                texture.wrapMode = entry.clamp ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
                Object.DontDestroyOnLoad(texture);
                Texture2D mask = null;
                if (!string.IsNullOrEmpty(entry.mask))
                {
                    mask = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                    if (!ImageConversion.LoadImage(mask, resources.ReadBytes("Textures/" + entry.mask), false) ||
                        mask.width != entry.width || mask.height != entry.height)
                        throw new InvalidOperationException("Неверная маска текстуры: " + entry.mask);
                    Object.DontDestroyOnLoad(mask);
                }
                textures[Key(entry.name, entry.width, entry.height)] = new TextureLayer { Texture = texture, Overlay = entry.overlay, Mask = mask, Linear = entry.linear };
            }
            logger.LogInfo("Замен текстур: " + textures.Count + (enabled.Value ? " (включены)" : " (отключены в конфиге)"));
        }

        private static string Key(string name, int width, int height) { return name + "|" + width + "x" + height; }

        internal Texture2D Replacement(Texture texture)
        {
            TextureLayer layer;
            if (!enabled.Value || texture == null || !textures.TryGetValue(Key(texture.name, texture.width, texture.height), out layer)) return null;
            if (!layer.Overlay) return layer.Texture;
            Texture2D replacement;
            if (composed.TryGetValue(texture.GetInstanceID(), out replacement)) return replacement;
            // Original game textures are often compressed and not CPU-readable. Read a GPU copy.
            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32,
                layer.Linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.Default);
            try
            {
                Graphics.Blit(texture, temporary);
                RenderTexture.active = temporary;
                replacement = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, layer.Linear);
                replacement.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                Color32[] background = replacement.GetPixels32();
                Color32[] foreground = layer.Texture.GetPixels32();
                Color32[] mask = layer.Mask == null ? null : layer.Mask.GetPixels32();
                for (int i = 0; i < background.Length; i++)
                {
                    int alpha = mask == null ? foreground[i].a : mask[i].r;
                    if (alpha == 0) continue;
                    int inverse = 255 - alpha;
                    background[i] = new Color32((byte)((foreground[i].r * alpha + background[i].r * inverse) / 255),
                        (byte)((foreground[i].g * alpha + background[i].g * inverse) / 255),
                        (byte)((foreground[i].b * alpha + background[i].b * inverse) / 255),
                        mask == null ? (byte)(alpha + background[i].a * inverse / 255) :
                            (byte)((foreground[i].a * alpha + background[i].a * inverse) / 255));
                }
                replacement.SetPixels32(background);
                replacement.Apply(false, true);
                replacement.name = "RTLC " + texture.name;
                replacement.filterMode = texture.filterMode;
                replacement.wrapMode = texture.wrapMode;
                replacement.anisoLevel = texture.anisoLevel;
                Object.DontDestroyOnLoad(replacement);
                composed[texture.GetInstanceID()] = replacement;
                return replacement;
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(temporary); }
        }

        public void Apply()
        {
            if (!enabled.Value || textures.Count == 0) return;
            foreach (RawImage image in Resources.FindObjectsOfTypeAll<RawImage>())
            {
                if (image == null || !image.gameObject.scene.IsValid()) continue;
                Texture2D replacement = Replacement(image.texture);
                if (replacement != null) image.texture = replacement;
            }
            foreach (Image image in Resources.FindObjectsOfTypeAll<Image>())
            {
                if (image == null || !image.gameObject.scene.IsValid()) continue;
                Sprite sprite = ReplacementSprite(image.sprite);
                if (sprite != null) image.sprite = sprite;
            }
            foreach (SpriteRenderer renderer in Resources.FindObjectsOfTypeAll<SpriteRenderer>())
            {
                if (renderer == null || !renderer.gameObject.scene.IsValid()) continue;
                Sprite sprite = ReplacementSprite(renderer.sprite);
                if (sprite != null) renderer.sprite = sprite;
            }
            foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (renderer == null || !renderer.gameObject.scene.IsValid()) continue;
                ApplyRenderer(renderer);
            }
        }

        internal void ApplyUnder(GameObject root)
        {
            if (!enabled.Value || root == null) return;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true)) ApplyRenderer(renderer);
            foreach (RawImage image in root.GetComponentsInChildren<RawImage>(true))
            {
                Texture2D replacement = Replacement(image.texture);
                if (replacement != null) image.texture = replacement;
            }
            foreach (Image image in root.GetComponentsInChildren<Image>(true))
            {
                Sprite replacement = ReplacementSprite(image.sprite);
                if (replacement != null) image.sprite = replacement;
            }
        }

        private void ApplyRenderer(Renderer renderer)
        {
            if (renderer == null) return;
            Material[] source = renderer.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < source.Length; i++)
            {
                Material original = source[i];
                if (original == null || ownMaterials.Contains(original.GetInstanceID())) continue;
                Material replacement;
                if (!materials.TryGetValue(original.GetInstanceID(), out replacement))
                {
                    replacement = null;
                    foreach (string property in original.GetTexturePropertyNames())
                    {
                        Texture2D texture = Replacement(original.GetTexture(property));
                        if (texture == null) continue;
                        if (replacement == null)
                        {
                            replacement = new Material(original);
                            replacement.name = "RTLC " + original.name;
                        }
                        replacement.SetTexture(property, texture);
                    }
                    if (replacement == null)
                    {
                        continue;
                    }
                    materials[original.GetInstanceID()] = replacement;
                    ownMaterials.Add(replacement.GetInstanceID());
                    Object.DontDestroyOnLoad(replacement);
                }
                source[i] = replacement;
                changed = true;
            }
            if (changed) renderer.sharedMaterials = source;
        }

        internal Sprite ReplacementSprite(Sprite original)
        {
            if (!enabled.Value || original == null) return null;
            Sprite replacement;
            if (sprites.TryGetValue(original.GetInstanceID(), out replacement)) return replacement;
            Texture2D texture = Replacement(original.texture);
            if (texture == null) return null;
            Rect rect = original.rect;
            replacement = Sprite.Create(texture, rect, new Vector2(original.pivot.x / rect.width, original.pivot.y / rect.height),
                original.pixelsPerUnit, 0, SpriteMeshType.FullRect, original.border);
            replacement.name = "RTLC " + original.name;
            Object.DontDestroyOnLoad(replacement);
            sprites[original.GetInstanceID()] = replacement;
            return replacement;
        }

        private static Entry[] ParseManifestFallback(string text)
        {
            var list = new List<Entry>();
            foreach (Match m in Regex.Matches(text, @"\{[^{}]*\}"))
            {
                string block = m.Value;
                var name = Regex.Match(block, "\"name\"\\s*:\\s*\"([^\"]*)\"");
                if (!name.Success) continue;
                var w = Regex.Match(block, "\"width\"\\s*:\\s*(\\d+)");
                var h = Regex.Match(block, "\"height\"\\s*:\\s*(\\d+)");
                var f = Regex.Match(block, "\"file\"\\s*:\\s*\"([^\"]*)\"");
                var mask = Regex.Match(block, "\"mask\"\\s*:\\s*\"([^\"]*)\"");
                list.Add(new Entry {
                    name = name.Groups[1].Value,
                    width = w.Success ? int.Parse(w.Groups[1].Value) : 0,
                    height = h.Success ? int.Parse(h.Groups[1].Value) : 0,
                    file = f.Success ? f.Groups[1].Value : "",
                    overlay = block.Contains("\"overlay\":true") || block.Contains("\"overlay\": true"),
                    pointFilter = block.Contains("\"pointFilter\":true") || block.Contains("\"pointFilter\": true"),
                    clamp = block.Contains("\"clamp\":true") || block.Contains("\"clamp\": true"),
                    linear = block.Contains("\"linear\":true") || block.Contains("\"linear\": true"),
                    mask = mask.Success ? mask.Groups[1].Value : null
                });
            }
            return list.ToArray();
        }

        private sealed class TextureLayer { public Texture2D Texture; public bool Overlay; public Texture2D Mask; public bool Linear; }
    }
}
