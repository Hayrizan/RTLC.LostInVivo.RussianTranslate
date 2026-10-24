using System;
using System.Collections.Generic;
using UnityEngine;

namespace RTLC.LostInVivo
{
    // Compare ink bounds in the original and Cyrillic fonts. Only a single, uniform
    // characterSize factor changes; the original transform/colour animation owns its axes.
    public sealed class MemoryTextViewport : MonoBehaviour
    {
        private TextMesh text;
        private float originalSize;
        private Font measuredFont;
        private string measuredText;
        private int measuredSize;
        private FontStyle measuredStyle;
        private float measuredSpacing;
        private Vector2 glyphSize;
        private readonly Dictionary<string, Vector2> originalBounds = new Dictionary<string, Vector2>(StringComparer.Ordinal);

        private void Awake() { Initialize(); }

        private void Initialize()
        {
            if (text != null) return;
            text = GetComponent<TextMesh>();
            if (text != null) originalSize = text.characterSize;
        }

        public void RegisterReference(string english, string russian, Font originalFont)
        {
            Initialize();
            if (text == null || originalFont == null || string.IsNullOrEmpty(english) || string.IsNullOrEmpty(russian) || english == russian || originalBounds.ContainsKey(russian)) return;
            // OnTriggerStay sees the already translated fields after Start. Never use
            // those Cyrillic strings as a new reference for the English bitmap font.
            foreach (char c in english) if (c >= '\u0400' && c <= '\u052f') return;
            int size = text.fontSize > 0 ? text.fontSize : 32;
            Vector2 bounds = MeasureInk(originalFont, english, size, text.fontStyle, text.lineSpacing);
            if (bounds.x > 0f && bounds.y > 0f) originalBounds[russian] = bounds;
        }

        private static Vector2 MeasureInk(Font font, string value, int size, FontStyle style, float spacing)
        {
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
                    minX = Mathf.Min(minX, x + info.minX); maxX = Mathf.Max(maxX, x + info.maxX);
                    minY = Mathf.Min(minY, y + info.minY); maxY = Mathf.Max(maxY, y + info.maxY);
                }
                x += info.advance;
            }
            return minX == float.MaxValue ? Vector2.zero : new Vector2(maxX - minX, maxY - minY);
        }

        private void LateUpdate()
        {
            Camera camera = GetComponentInParent<Camera>();
            if (camera == null) camera = Camera.main;
            if (text == null || text.font == null || camera == null || string.IsNullOrEmpty(text.text)) return;
            int size = text.fontSize > 0 ? text.fontSize : 32;
            if (measuredFont != text.font || measuredText != text.text || measuredSize != size ||
                measuredStyle != text.fontStyle || measuredSpacing != text.lineSpacing)
            {
                glyphSize = MeasureInk(text.font, text.text, size, text.fontStyle, text.lineSpacing);
                measuredFont = text.font; measuredText = text.text; measuredSize = size;
                measuredStyle = text.fontStyle; measuredSpacing = text.lineSpacing;
            }
            if (glyphSize.x <= 0f || glyphSize.y <= 0f) return;
            float referenceFit = 1f;
            Vector2 target;
            if (originalBounds.TryGetValue(text.text, out target))
                referenceFit = Mathf.Min(target.x / glyphSize.x, target.y / glyphSize.y);
            float characterSize = originalSize * referenceFit;
            // Additional screen safety applies to the original animated scale, without
            // enlarging a short message to fill an otherwise mostly empty screen.
            Vector2 extent = glyphSize * (characterSize * 0.05f);
            Vector3 left = camera.WorldToViewportPoint(transform.TransformPoint(new Vector3(-extent.x, 0f, 0f)));
            Vector3 right = camera.WorldToViewportPoint(transform.TransformPoint(new Vector3(extent.x, 0f, 0f)));
            Vector3 bottom = camera.WorldToViewportPoint(transform.TransformPoint(new Vector3(0f, -extent.y, 0f)));
            Vector3 top = camera.WorldToViewportPoint(transform.TransformPoint(new Vector3(0f, extent.y, 0f)));
            float widthOnScreen = Mathf.Abs(right.x - left.x);
            float heightOnScreen = Mathf.Abs(top.y - bottom.y);
            float fit = 1f;
            if (widthOnScreen > 0.84f) fit = Mathf.Min(fit, 0.84f / widthOnScreen);
            if (heightOnScreen > 0.76f) fit = Mathf.Min(fit, 0.76f / heightOnScreen);
            text.characterSize = characterSize * fit;
        }
    }
}
