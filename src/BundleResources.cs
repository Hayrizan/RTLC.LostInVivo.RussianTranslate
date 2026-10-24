using System;
using System.IO;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace RTLC.LostInVivo
{
    // Keep the bundle alive while native Fonts and binary TextAssets are in use.
    internal sealed class BundleResources : IDisposable
    {
        internal const string FileName = "rtlc.bundle";
        private AssetBundle bundle;

        public BundleResources(string root, ManualLogSource logger)
        {
            string path = Path.Combine(root, FileName);
            if (!File.Exists(path)) throw new FileNotFoundException("Отсутствует бандл русского перевода", path);
            bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null) throw new InvalidOperationException("Не удалось загрузить Unity AssetBundle: " + path);
            logger.LogInfo("Бандл русского перевода загружен: " + bundle.GetAllAssetNames().Length + " ресурсов.");
        }

        private static string DataAddress(string path)
        {
            path = path.Replace('\\', '/');
            if (path.StartsWith("/", StringComparison.Ordinal) || path.Contains("..") || path.Contains(":"))
                throw new InvalidOperationException("Неверный путь ресурса: " + path);
            return "assets/rtlc/data/" + path.ToLowerInvariant() + ".bytes";
        }

        public bool Contains(string path) { return bundle.Contains(DataAddress(path)); }

        public byte[] ReadBytes(string path)
        {
            TextAsset asset = bundle.LoadAsset<TextAsset>(DataAddress(path));
            if (asset == null) throw new InvalidOperationException("В бандле отсутствует ресурс: " + path);
            return asset.bytes;
        }

        public string ReadText(string path)
        {
            byte[] bytes = ReadBytes(path);
            string value = Encoding.UTF8.GetString(bytes, 0, bytes.Length);
            return value.Length > 0 && value[0] == '\uFEFF' ? value.Substring(1) : value;
        }

        public Font LoadFont(string fileName)
        {
            if (Path.GetFileName(fileName) != fileName) throw new InvalidOperationException("Неверное имя шрифта: " + fileName);
            Font font = bundle.LoadAsset<Font>("assets/rtlc/fonts/native/" + fileName.ToLowerInvariant());
            if (font == null) throw new InvalidOperationException("В бандле отсутствует шрифт: " + fileName);
            return font;
        }

        public void Dispose()
        {
            if (bundle != null) bundle.Unload(false);
            bundle = null;
        }
    }
}
