using System;
using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class BuildLocalizationBundle
{
    [Serializable] private sealed class Plan
    {
        public string[] dataPaths;
        public string[] fontPaths;
        public string outputDirectory;
    }

    public static void Build()
    {
        try
        {
            if (Application.unityVersion != "2018.2.21f1")
                throw new Exception("Bundle authoring requires Unity 2018.2.21f1; got " + Application.unityVersion);
            Plan plan = JsonUtility.FromJson<Plan>(File.ReadAllText("Assets/Editor/BundleBuildPlan.json"));
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            foreach (string path in plan.fontPaths)
            {
                TrueTypeFontImporter importer = AssetImporter.GetAtPath(path) as TrueTypeFontImporter;
                if (importer == null) throw new Exception("Missing native font importer: " + path);
                importer.fontTextureCase = FontTextureCase.Dynamic;
                importer.includeFontData = true;
                importer.fontSize = 32;
                importer.fontRenderingMode = FontRenderingMode.Smooth;
                importer.SaveAndReimport();
                Font font = AssetDatabase.LoadAssetAtPath<Font>(path);
                if (font == null || !font.dynamic || font.fontSize != 32)
                    throw new Exception("Invalid imported dynamic font: " + path);
                // Unity 2018's CreateDynamicFontFromOSFont leaves descent at zero.
                // Importers fill it from the TTF; TextMesh anchors then move despite
                // identical glyph metrics. Preserve the previous runtime's baseline.
                var serialized = new SerializedObject(font);
                SerializedProperty descent = serialized.FindProperty("m_Descent");
                if (descent == null) throw new Exception("Missing Unity 2018 Font descent: " + path);
                descent.floatValue = 0f;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(font);
            }
            AssetDatabase.SaveAssets();
            var names = new List<string>(plan.dataPaths);
            names.AddRange(plan.fontPaths);
            foreach (string path in plan.dataPaths)
            {
                TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                if (asset == null || !Equal(asset.bytes, File.ReadAllBytes(path)))
                    throw new Exception("Binary resource changed during Unity import: " + path);
            }
            Directory.CreateDirectory(plan.outputDirectory);
            var build = new AssetBundleBuild { assetBundleName = "rtlc.bundle", assetNames = names.ToArray() };
            var result = BuildPipeline.BuildAssetBundles(plan.outputDirectory, new[] { build },
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle,
                BuildTarget.StandaloneWindows64);
            if (result == null || !File.Exists(Path.Combine(plan.outputDirectory, "rtlc.bundle")))
                throw new Exception("AssetBundle build failed");
            Debug.Log("RTLC_BUNDLE_OK resources=" + names.Count + " output=" + plan.outputDirectory);
        }
        catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
    }

    private static bool Equal(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
