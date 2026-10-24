using System;
using System.Collections.Generic;
using UnityEngine;

namespace RTLC.LostInVivo
{
    // Shrink translated showroom plaques to the visible bounds of their original
    // English labels and neighboring slots. Keep the font face, alignment, and
    // transform; use Unity's depth-tested bitmap text shader for scene occlusion.
    public sealed class ShowroomTextFit : MonoBehaviour
    {
        private static readonly List<ShowroomTextFit> labels = new List<ShowroomTextFit>();
        private TextMesh text;
        private Renderer textRenderer;
        private Material depthMaterial;
        private Material originalMaterial;
        private Vector2 replacementBounds;
        private Font sourceFont;
        private string sourceText;
        private string translatedText;
        private float originalCharacterSize;
        private float fit = 1f;
        private bool initialized;
        private bool registered;

        private void Awake()
        {
            Initialize();
        }

        private void Initialize()
        {
            if (initialized) return;
            text = GetComponent<TextMesh>();
            if (text != null)
            {
                originalCharacterSize = text.characterSize;
                textRenderer = text.GetComponent<Renderer>();
                Shader shader = Shader.Find("GUI/3D Text Shader");
                if (textRenderer != null && shader != null)
                {
                    originalMaterial = textRenderer.sharedMaterial;
                    depthMaterial = new Material(shader);
                    depthMaterial.name = "RTLC depth-tested showroom text";
                    depthMaterial.renderQueue = 3000;
                    if (depthMaterial.HasProperty("_Color"))
                        depthMaterial.color = originalMaterial != null && originalMaterial.HasProperty("_Color") ? originalMaterial.color : Color.white;
                    depthMaterial.mainTexture = text.font.material.mainTexture;
                    textRenderer.sharedMaterial = depthMaterial;
                }
                labels.Add(this);
            }
            initialized = true;
        }

        public void RegisterReference(string english, string russian, Font originalFont)
        {
            Initialize();
            if (text == null || originalFont == null || string.IsNullOrEmpty(english) ||
                string.IsNullOrEmpty(russian)) return;
            if (registered && sourceText == english && translatedText == russian && sourceFont == originalFont) return;

            // A TextMesh can receive a new source string at runtime. Always start again
            // from the authored scale instead of compounding the previous fit.
            text.characterSize = originalCharacterSize;
            sourceText = english;
            translatedText = russian;
            sourceFont = originalFont;
            registered = false;

            int size = text.fontSize > 0 ? text.fontSize : (originalFont.fontSize > 0 ? originalFont.fontSize : 32);
            Vector2 original = MeasureInk(originalFont, english, size, text.fontStyle, text.lineSpacing);
            Vector2 replacement = MeasureInk(text.font, russian, size, text.fontStyle, text.lineSpacing);
            if (replacement.x <= 0f || replacement.y <= 0f) return;
            replacementBounds = replacement;

            // Uniform characterSize scaling preserves glyph proportions and prevents a
            // long Russian name from extending beyond the English plaque footprint.
            fit = original.x > 0f && original.y > 0f ? Mathf.Min(1f, original.x / replacement.x, original.y / replacement.y) : 1f;
            registered = true;
            ApplyLayout();
        }

        private static Vector2 MeasureInk(Font font, string value, int size, FontStyle style, float spacing)
        {
            if (font == null || string.IsNullOrEmpty(value)) return Vector2.zero;
            int requestSize = font.dynamic ? size : 0;
            if (font.dynamic) font.RequestCharactersInTexture(value, requestSize, style);
            float lineHeight = font.lineHeight;
            if (font.dynamic && font.fontSize > 0) lineHeight *= (float)size / font.fontSize;
            if (lineHeight <= 0f) lineHeight = size;
            lineHeight *= spacing;

            float x = 0f, y = 0f;
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            foreach (char c in value.Replace("\r", ""))
            {
                if (c == '\n') { x = 0f; y -= lineHeight; continue; }
                CharacterInfo info;
                if (!font.GetCharacterInfo(c, out info, requestSize, style)) continue;
                if (info.maxX > info.minX && info.maxY > info.minY)
                {
                    minX = Mathf.Min(minX, x + info.minX);
                    maxX = Mathf.Max(maxX, x + info.maxX);
                    minY = Mathf.Min(minY, y + info.minY);
                    maxY = Mathf.Max(maxY, y + info.maxY);
                }
                x += info.advance;
            }
            return minX == float.MaxValue ? Vector2.zero : new Vector2(maxX - minX, maxY - minY);
        }

        private void LateUpdate()
        {
            ApplyLayout();
        }

        private void ApplyLayout()
        {
            if (text == null) return;
            // A dynamic font can rebuild its atlas after any label requests glyphs.
            if (depthMaterial != null && text.font != null)
                depthMaterial.mainTexture = text.font.material.mainTexture;
            if (!registered) return;
            float factor = fit;
            Vector3 right = transform.right, up = transform.up, forward = transform.forward;
            float width = replacementBounds.x * originalCharacterSize * .1f * transform.TransformVector(Vector3.right).magnitude;
            foreach (ShowroomTextFit other in labels)
            {
                if (other == null || other == this || !other.gameObject.activeInHierarchy) continue;
                if (Vector3.Dot(right, other.transform.right) < .99f || Vector3.Dot(up, other.transform.up) < .99f) continue;
                Vector3 delta = other.transform.position - transform.position;
                // Opposite walls and different rows do not share a label slot.
                if (Mathf.Abs(Vector3.Dot(delta, forward)) > .25f || Mathf.Abs(Vector3.Dot(delta, up)) > .3f) continue;
                float distance = Mathf.Abs(Vector3.Dot(delta, right));
                if (distance > .001f && width > 0f) factor = Mathf.Min(factor, .88f * distance / width);
            }
            text.characterSize = originalCharacterSize * factor;
        }

        private void OnDestroy()
        {
            labels.Remove(this);
            if (textRenderer != null && textRenderer.sharedMaterial == depthMaterial)
                textRenderer.sharedMaterial = originalMaterial;
            if (depthMaterial != null) Destroy(depthMaterial);
        }

        private void OnDisable()
        {
            if (registered && text != null) text.characterSize = originalCharacterSize;
        }
    }
}
