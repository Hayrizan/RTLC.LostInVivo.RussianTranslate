using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace RTLC.LostInVivo
{
    // Original TextMesh remains the game's reading text; only its world renderer is hidden.
    public sealed class WorldNoteLayout : MonoBehaviour
    {
        private TextMesh source;
        private Renderer sourceRenderer;
        private MeshRenderer displayRenderer;
        private Transform display;
        private Mesh glyphMesh;
        private Material worldMaterial;
        private Bounds paperBounds;
        private string worldText;
        private string readingText;
        private int pointSize;
        private bool pending;
        private bool building;
        private bool ready;
        private bool originalRendererEnabled;

        public void Initialize(TextMesh original, MeshFilter sheet)
        {
            source = original;
            sourceRenderer = source.GetComponent<Renderer>();
            originalRendererEnabled = sourceRenderer != null && sourceRenderer.enabled;
            readingText = source.text;
            worldText = WrapWorldText(readingText);
            pointSize = source.fontSize > 0 ? source.fontSize : 32;
            // Project paper bounds once into the original text plane. Never read a cached
            // Renderer.bounds after changing transforms: Unity may still report the old AABB.
            bool first = true;
            Bounds bounds = PaperLocalBounds(sheet);
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 vertex = new Vector3(
                    (corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                    (corner & 4) == 0 ? bounds.min.z : bounds.max.z);
                Vector3 local = transform.InverseTransformPoint(sheet.transform.TransformPoint(vertex));
                if (first) { paperBounds = new Bounds(local, Vector3.zero); first = false; }
                else paperBounds.Encapsulate(local);
            }
            if (paperBounds.size.x <= 0f || paperBounds.size.y <= 0f)
                throw new InvalidOperationException("Пустая область бумаги");
            // Sprite shader respects scene depth and multiplies font alpha by vertex colour.
            // GUI/Text Shader always passes its depth test and shows glyphs through walls.
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Particles/Alpha Blended");
            if (shader == null) throw new InvalidOperationException("Не найден шейдер текста с проверкой глубины");
            worldMaterial = new Material(shader);
            worldMaterial.name = "RTLC depth-tested note";
            worldMaterial.renderQueue = 3000;
            if (worldMaterial.HasProperty("_Color")) worldMaterial.color = Color.white;
            if (worldMaterial.HasProperty("_ZTest")) worldMaterial.SetInt("_ZTest", 4); // LessEqual
            if (worldMaterial.HasProperty("_ZWrite")) worldMaterial.SetInt("_ZWrite", 0);
            var child = new GameObject("RTLC world note");
            child.layer = gameObject.layer;
            child.transform.SetParent(transform, false);
            display = child.transform;
            glyphMesh = new Mesh();
            glyphMesh.name = "RTLC note glyph geometry";
            child.AddComponent<MeshFilter>().sharedMesh = glyphMesh;
            displayRenderer = child.AddComponent<MeshRenderer>();
            displayRenderer.sharedMaterial = worldMaterial;
            displayRenderer.enabled = false;
            Font.textureRebuilt += OnFontTextureRebuilt;
            Rebuild();
        }

        private static Bounds PaperLocalBounds(MeshFilter sheet)
        {
            Renderer renderer = sheet.GetComponent<Renderer>();
            if (renderer == null || !renderer.isPartOfStaticBatch) return sheet.sharedMesh.bounds;
            // All 41 shipped papers are the built-in unit Quad. Static batching replaces
            // sharedMesh with a scene-wide mesh in batch space, not this sheet's local mesh.
            // Verify the individual renderer still matches that Quad before using it.
            Bounds quad = new Bounds(Vector3.zero, new Vector3(1f, 1f, 0f));
            Bounds expected = new Bounds(sheet.transform.TransformPoint(quad.min), Vector3.zero);
            for (int corner = 0; corner < 4; corner++)
                expected.Encapsulate(sheet.transform.TransformPoint(new Vector3(
                    (corner & 1) == 0 ? -.5f : .5f, (corner & 2) == 0 ? -.5f : .5f, 0f)));
            Bounds actual = renderer.bounds;
            float tolerance = Mathf.Max(expected.size.magnitude * .003f, .0005f);
            if ((actual.center - expected.center).magnitude > tolerance ||
                (actual.size - expected.size).magnitude > tolerance)
                throw new InvalidOperationException("Геометрия объединённой записки не соответствует исходному листу");
            return quad;
        }

        private void OnFontTextureRebuilt(Font font)
        {
            if (source != null && source.font == font) pending = true;
        }

        private void Rebuild()
        {
            if (building || source == null || source.font == null) return;
            building = true;
            try
            {
                Font font = source.font;
                font.RequestCharactersInTexture(worldText, pointSize, source.fontStyle);
                var vertices = new List<Vector3>();
                var uv = new List<Vector2>();
                var colours = new List<Color32>();
                var triangles = new List<int>();
                float x = 0f, y = 0f;
                float lineHeight = pointSize * source.lineSpacing;
                if (font.fontSize > 0 && font.lineHeight > 0)
                    lineHeight = font.lineHeight * ((float)pointSize / font.fontSize) * source.lineSpacing;
                foreach (char character in worldText)
                {
                    if (character == '\n') { x = 0f; y -= lineHeight; continue; }
                    CharacterInfo info;
                    if (!font.GetCharacterInfo(character, out info, pointSize, source.fontStyle)) continue;
                    if (character != ' ' && info.maxX > info.minX && info.maxY > info.minY)
                    {
                        int start = vertices.Count;
                        vertices.Add(new Vector3(x + info.minX, y + info.maxY, 0f));
                        vertices.Add(new Vector3(x + info.maxX, y + info.maxY, 0f));
                        vertices.Add(new Vector3(x + info.maxX, y + info.minY, 0f));
                        vertices.Add(new Vector3(x + info.minX, y + info.minY, 0f));
                        uv.Add(info.uvTopLeft); uv.Add(info.uvTopRight);
                        uv.Add(info.uvBottomRight); uv.Add(info.uvBottomLeft);
                        for (int i = 0; i < 4; i++) colours.Add(source.color);
                        triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
                        triangles.Add(start + 2); triangles.Add(start + 3); triangles.Add(start);
                    }
                    x += info.advance;
                }
                if (vertices.Count == 0)
                {
                    glyphMesh.Clear();
                    displayRenderer.enabled = false;
                    ready = false;
                    pending = false;
                    return;
                }
                glyphMesh.Clear();
                glyphMesh.vertices = vertices.ToArray();
                glyphMesh.uv = uv.ToArray();
                glyphMesh.colors32 = colours.ToArray();
                glyphMesh.triangles = triangles.ToArray();
                glyphMesh.RecalculateBounds();
                Bounds glyphs = glyphMesh.bounds;
                float insetX = paperBounds.size.x * 0.12f;
                float insetY = paperBounds.size.y * 0.12f;
                float width = paperBounds.size.x - 2f * insetX;
                float height = paperBounds.size.y - 2f * insetY;
                float fit = Mathf.Min(width / glyphs.size.x, height / glyphs.size.y);
                display.localScale = Vector3.one * fit;
                // Keep the text just in front of the actual sheet plane, even when its
                // original TextMesh pivot was offset from the paper.
                float side = paperBounds.center.z < 0f ? -1f : 1f;
                float depth = paperBounds.center.z - side * Mathf.Min(paperBounds.size.x, paperBounds.size.y) * 0.002f;
                display.localPosition = new Vector3(
                    paperBounds.min.x + insetX - glyphs.min.x * fit,
                    paperBounds.max.y - insetY - glyphs.max.y * fit, depth);
                worldMaterial.mainTexture = font.material.mainTexture;
                displayRenderer.enabled = true;
                if (sourceRenderer != null) sourceRenderer.enabled = false;
                ready = true;
                pending = false;
            }
            finally { building = false; }
        }

        private void LateUpdate()
        {
            // Final_school_note_scr replaces the train note with the ending text in Start.
            // It may run after Note_pickup_scr initialized this layout; the reading view
            // and the world mesh must follow the same current text.
            if (source != null && source.text != readingText)
            {
                string current = source.text;
                if (Plugin.Instance != null && Plugin.Instance.Catalog != null)
                    current = Plugin.Instance.Catalog.Translate(current);
                if (source.text != current) source.text = current;
                readingText = current;
                worldText = WrapWorldText(current);
                pending = true;
            }
            if (pending) Rebuild();
            if (ready && sourceRenderer != null) sourceRenderer.enabled = false;
        }

        private void OnDestroy()
        {
            Font.textureRebuilt -= OnFontTextureRebuilt;
            if (sourceRenderer != null) sourceRenderer.enabled = originalRendererEnabled;
            if (glyphMesh != null) Destroy(glyphMesh);
            if (worldMaterial != null) Destroy(worldMaterial);
            if (display != null) Destroy(display.gameObject);
        }

        internal static string WrapWorldText(string text) { return WrapDisplayText(text, 36); }

        internal static string WrapDisplayText(string text, int limit)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var output = new StringBuilder();
            string[] lines = text.Replace("\r", "").Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                int column = 0;
                foreach (string word in line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (column > 0 && column + 1 + word.Length > limit)
                    { output.Append('\n'); column = 0; }
                    if (column > 0) { output.Append(' '); column++; }
                    output.Append(word);
                    column += word.Length;
                }
                output.Append('\n');
            }
            return output.ToString().TrimEnd();
        }
    }
}
