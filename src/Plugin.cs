using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace RTLC.LostInVivo
{
    [BepInPlugin("com.rtlc.lostinvivo.russian-translation", "Lost in Vivo — русский перевод", "0.2.2")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance;
        internal TranslationCatalog Catalog;
        internal FontManager Fonts;
        internal string Root;
        private string diagnosticRoot;
        private BundleResources bundle;
        private Harmony harmony;
        private TextureManager textures;
        private ConfigEntry<bool> auditMissing;
        private ConfigEntry<bool> snapshotOnScene;
        private ConfigEntry<bool> textureReplacementsEnabled;
        private float nextScan;
        private float sceneInspectAt;
        private Type statusType;
        private FieldInfo statusStage;
        private bool loggedTransitionPrompt;
        private readonly Dictionary<int, ManualLayoutState> manualLayouts = new Dictionary<int, ManualLayoutState>();
        private bool dirty = true;
        private bool dlcTapeMenuAdjusted;
        private bool tapeScreenAdjusted;
        private bool mainMenuCreditCreated;
        private readonly Dictionary<int, WorldTextState> worldText = new Dictionary<int, WorldTextState>();
        private readonly HashSet<string> missed = new HashSet<string>();
        private readonly Dictionary<int, string> fontNames = new Dictionary<int, string>();

        private void Awake()
        {
            Instance = this;
            Root = Path.GetDirectoryName(Info.Location);
            diagnosticRoot = Path.Combine(Paths.BepInExRootPath, "cache/RTLC.LostInVivo.RussianTranslate/diagnostics");
            auditMissing = Config.Bind("Diagnostics", "LogMissingTranslations", true, "Записывать непереведённые строки в BepInEx/cache/RTLC.LostInVivo.RussianTranslate/diagnostics/missing.jsonl.");
            snapshotOnScene = Config.Bind("Diagnostics", "SnapshotOnSceneLoad", false, "Сохранять диагностический снимок текстов после смены сцены.");
            textureReplacementsEnabled = Config.Bind("Textures", "Enabled", true,
                "Включить русские замены текстур. Изменение применяется после перезапуска игры.");
            try
            {
                bundle = new BundleResources(Root, Logger);
                Catalog = new TranslationCatalog(bundle, LogMissing);
                Fonts = new FontManager(bundle, Logger);
                textures = new TextureManager(bundle, Logger, textureReplacementsEnabled);
                harmony = new Harmony("com.rtlc.lostinvivo.russian-translation");
                harmony.Patch(AccessTools.Method(typeof(TextGenerator), "PopulateWithError"),
                    new HarmonyMethod(typeof(Plugin), "TranslateUnityGeneration"));
                harmony.Patch(AccessTools.Method(typeof(TMP_Text), "StringToCharArray"),
                    new HarmonyMethod(typeof(Plugin), "TranslateTmpGeneration"));
                harmony.Patch(AccessTools.Method(typeof(Text), "OnPopulateMesh", new Type[] { typeof(VertexHelper) }),
                    new HarmonyMethod(typeof(Plugin), "BeforeTextMesh"));
                harmony.Patch(AccessTools.PropertySetter(typeof(Text), "text"),
                    new HarmonyMethod(typeof(Plugin), "BeforeUnityTextValue"));
                harmony.Patch(AccessTools.PropertySetter(typeof(RawImage), "texture"),
                    new HarmonyMethod(typeof(Plugin), "BeforeRawTexture"));
                harmony.Patch(AccessTools.PropertySetter(typeof(Image), "sprite"),
                    new HarmonyMethod(typeof(Plugin), "BeforeSprite"));
                Type inventoryItem = AccessTools.TypeByName("Inv_Item");
                if (inventoryItem != null)
                    harmony.Patch(AccessTools.Method(inventoryItem, "Spawn"), postfix: new HarmonyMethod(typeof(Plugin), "AfterInventorySpawn"));
                Type status = AccessTools.TypeByName("Sys_scr");
                if (status != null)
                {
                    statusType = status;
                    statusStage = AccessTools.Field(status, "stage");
                    harmony.Patch(AccessTools.Method(status, "Change_Level"), new HarmonyMethod(typeof(Plugin), "BeforeLevelChange"));
                }
                // The mirror room's body and exit triggers overlap in the original game.
                // Do not let an examination prompt overwrite an already-started scene fade.
                Type examine = AccessTools.TypeByName("Examine_repeat_scr");
                if (examine != null)
                    foreach (string method in new string[] { "OnTriggerStay", "FixedUpdate" })
                        harmony.Patch(AccessTools.Method(examine, method), new HarmonyMethod(typeof(Plugin), "BeforeExamineDuringTransition"));
                // Components with array or string fields that display in game.
                Type typewriter = AccessTools.TypeByName("typewrite_scr");
                if (typewriter != null)
                {
                    var m = AccessTools.Method(typewriter, "Start");
                    if (m != null) harmony.Patch(m, new HarmonyMethod(typeof(Plugin), "BeforeTypewriter"));
                }
                Type subtitle = AccessTools.TypeByName("subtitle_scr");
                if (subtitle != null)
                {
                    var m = AccessTools.Method(subtitle, "Start");
                    if (m != null) harmony.Patch(m, new HarmonyMethod(typeof(Plugin), "BeforeSubtitle"));
                }
                Type note = AccessTools.TypeByName("Note_pickup_scr");
                if (note != null)
                {
                    var m = AccessTools.Method(note, "Start");
                    if (m != null) harmony.Patch(m, new HarmonyMethod(typeof(Plugin), "BeforeNote"), new HarmonyMethod(typeof(Plugin), "AfterNoteStarted"));
                }
                Type memory = AccessTools.TypeByName("DARK_voc_text");
                if (memory != null)
                {
                    harmony.Patch(AccessTools.Method(memory, "Start"), postfix: new HarmonyMethod(typeof(Plugin), "TranslateMemoryFields"));
                    harmony.Patch(AccessTools.Method(memory, "OnTriggerStay"), new HarmonyMethod(typeof(Plugin), "TranslateMemoryFields"));
                }
                Type coward = AccessTools.TypeByName("Coward_scr");
                if (coward != null)
                    harmony.Patch(AccessTools.Method(coward, "Update"), new HarmonyMethod(typeof(Plugin), "BeforeCowardUpdate"));
                Type inventory = AccessTools.TypeByName("Inv_Get_HUD_info_scr");
                if (inventory != null)
                    harmony.Patch(AccessTools.Method(inventory, "Update"), postfix: new HarmonyMethod(typeof(Plugin), "AfterInventoryTime"));
                Type pause = AccessTools.TypeByName("New_PAUSE");
                if (pause != null)
                    harmony.Patch(AccessTools.Method(pause, "OnEnable"), postfix: new HarmonyMethod(typeof(Plugin), "AfterPauseEnabled"));
                Type mainMenu = AccessTools.TypeByName("NEW_MENU_SCR");
                if (mainMenu != null)
                {
                    MethodInfo start = AccessTools.Method(mainMenu, "Start");
                    if (start != null)
                        harmony.Patch(start, postfix: new HarmonyMethod(typeof(Plugin), "AfterMainMenuStarted"));
                }
                SceneManager.sceneLoaded += OnSceneLoaded;
                Logger.LogInfo("Перевод загружен: " + Catalog.Count + " строк; игровые идентификаторы остаются исходными.");
            }
            catch (Exception exception)
            {
                Logger.LogError("Не удалось загрузить русификатор: " + exception);
                if (harmony != null) harmony.UnpatchSelf();
                enabled = false;
            }
        }

        private static void TranslateUnityGeneration(ref string str, ref TextGenerationSettings settings)
        {
            if (Instance == null) return;
            if (Instance.Catalog != null) str = Instance.Catalog.Translate(str);
            if (Instance.Fonts != null && settings.font != null)
            {
                Instance.RememberFont(settings.font, settings.font.name);
                settings.font = Instance.Fonts.UnityFont(settings.font);
            }
        }

        private static void TranslateTmpGeneration(TMP_Text __instance, ref string sourceText)
        {
            if (Instance == null || Instance.Catalog == null) return;
            Instance.RememberFont(__instance, __instance.font == null ? "" : __instance.font.name);
            sourceText = Instance.Catalog.Translate(sourceText);
            Instance.Fonts.Apply(__instance, sourceText);
        }

        private static void BeforeUnityTextValue(Text __instance, string __0)
        {
            if (Instance != null && Instance.Fonts != null) Instance.LayoutManual(__instance, __0);
        }

        private void LayoutManual(Text text, string source)
        {
            if (text == null) return;
            bool isManual = source != null && (source.StartsWith("Move - WASD\n", StringComparison.Ordinal) ||
                source.StartsWith("Move - Left Stick\n", StringComparison.Ordinal));
            int id = text.GetInstanceID();
            ManualLayoutState state;
            if (!isManual)
            {
                if (manualLayouts.TryGetValue(id, out state))
                {
                    if (state.Component == text)
                    {
                        text.fontSize = state.FontSize;
                        text.horizontalOverflow = state.Overflow;
                        text.resizeTextForBestFit = state.BestFit;
                    }
                    manualLayouts.Remove(id);
                }
                return;
            }
            if (!manualLayouts.TryGetValue(id, out state) || state.Component != text)
            {
                state = new ManualLayoutState { Component = text, FontSize = text.fontSize,
                    Overflow = text.horizontalOverflow, BestFit = text.resizeTextForBestFit };
                manualLayouts[id] = state;
            }
            Fonts.Apply(text);
            string translated = Catalog.Translate(source);
            var settings = text.GetGenerationSettings(new Vector2(100000f, 100000f));
            settings.fontSize = state.FontSize;
            settings.resizeTextForBestFit = false;
            settings.horizontalOverflow = HorizontalWrapMode.Overflow;
            settings.verticalOverflow = VerticalWrapMode.Overflow;
            float width;
            using (var generator = new TextGenerator())
                width = generator.GetPreferredWidth(translated, settings) / text.pixelsPerUnit;
            float available = text.rectTransform.rect.width - 4f;
            int size = state.FontSize;
            if (available > 0f && width > available)
                size = Math.Max(1, (int)Math.Floor(state.FontSize * available / width));
            text.resizeTextForBestFit = false;
            text.fontSize = size;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
        }

        private static void TranslateMemoryFields(ref string ___DIALOG, ref string ___DIALOG2, GameObject ___MOM)
        {
            if (Instance == null || Instance.Catalog == null) return;
            string english = ___DIALOG;
            string english2 = ___DIALOG2;
            string russian = Instance.Catalog.Translate(english);
            string russian2 = Instance.Catalog.Translate(english2);
            if (___MOM != null)
            {
                TextMesh text = ___MOM.GetComponent<TextMesh>();
                if (text != null)
                {
                    Font originalFont = Instance.Fonts.OriginalWorldFont(text);
                    var layout = text.GetComponent<MemoryTextViewport>();
                    if (layout == null) layout = text.gameObject.AddComponent<MemoryTextViewport>();
                    layout.RegisterReference(english, russian, originalFont);
                    layout.RegisterReference(english2, russian2, originalFont);
                    Instance.Fonts.Apply(text);
                }
            }
            // Keep the original line count. Added wrapping changes the size of these
            // camera-attached messages far more than the replacement font alone.
            ___DIALOG = russian;
            ___DIALOG2 = russian2;
        }

        private static void BeforeCowardUpdate(bool ___Killing, ref bool ___Seen)
        {
            // Once the scripted death has started, a disabled sight trigger must not
            // pause its countdown. Preserve the authored first/repeat death timings.
            if (___Killing) ___Seen = false;
        }

        private static void BeforeRawTexture(ref Texture __0)
        {
            if (Instance == null || Instance.textures == null) return;
            Texture2D replacement = Instance.textures.Replacement(__0);
            if (replacement != null) __0 = replacement;
        }

        private static void BeforeSprite(ref Sprite __0)
        {
            if (Instance == null || Instance.textures == null) return;
            Sprite replacement = Instance.textures.ReplacementSprite(__0);
            if (replacement != null) __0 = replacement;
        }

        private static void AfterInventorySpawn(Component __instance)
        {
            if (Instance != null && Instance.textures != null)
                Instance.textures.ApplyUnder(__instance.gameObject);
        }

        private static bool BeforeExamineDuringTransition(GameObject ___Status)
        {
            Plugin plugin = Instance;
            if (plugin == null || ___Status == null || plugin.statusType == null || plugin.statusStage == null ||
                SceneManager.GetActiveScene().name != "SUBWAY_03B") return true;
            Component status = ___Status.GetComponent(plugin.statusType);
            if (status == null) return true;
            int stage = (int)plugin.statusStage.GetValue(status);
            if (stage != 2 && stage != 4) return true;
            if (!plugin.loggedTransitionPrompt)
            {
                plugin.loggedTransitionPrompt = true;
                plugin.Logger.LogInfo("SUBWAY_03B: подсказка тела не прервёт переход через дверь.");
            }
            return false;
        }

        private static void BeforeLevelChange(string meh)
        {
            if (Instance != null) Instance.Logger.LogInfo("Переход сцены: " + SceneManager.GetActiveScene().name + " -> " + meh);
        }

        private static void BeforeTextMesh(Text __instance)
        {
            if (Instance == null || Instance.Fonts == null) return;
            Instance.RememberFont(__instance, __instance.font == null ? "" : __instance.font.name);
            Instance.Fonts.Apply(__instance);
        }

        private static void BeforeTypewriter(string[] ___goatText)
        {
            if (Instance == null || Instance.Catalog == null || ___goatText == null) return;
            for (int i = 0; i < ___goatText.Length; i++)
                ___goatText[i] = Instance.Catalog.Translate(___goatText[i]);
        }

        private static void BeforeSubtitle(string[] ___lines)
        {
            if (Instance == null || Instance.Catalog == null || ___lines == null) return;
            for (int i = 0; i < ___lines.Length; i++)
                ___lines[i] = Instance.Catalog.Translate(___lines[i]);
        }

        private static void AfterInventoryTime(GameObject ___text2)
        {
            if (___text2 == null) return;
            Text text = ___text2.GetComponent<Text>();
            if (text != null) text.text = "Время: " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        private static void AfterPauseEnabled(GameObject[] ___texts)
        {
            if (___texts == null) return;
            // All four objects are selected/unselected versions of the two labels.
            foreach (GameObject item in ___texts)
            {
                if (item == null) continue;
                Text text = item.GetComponent<Text>();
                if (text == null) continue;
                text.alignment = TextAnchor.MiddleCenter;
                RectTransform rect = text.rectTransform;
                // Stretch horizontally across the parent so different label widths share its centre.
                rect.anchorMin = new Vector2(0f, rect.anchorMin.y);
                rect.anchorMax = new Vector2(1f, rect.anchorMax.y);
                rect.pivot = new Vector2(0.5f, rect.pivot.y);
                rect.offsetMin = new Vector2(0f, rect.offsetMin.y);
                rect.offsetMax = new Vector2(0f, rect.offsetMax.y);
            }
        }

        private static void AfterMainMenuStarted(Component __instance)
        {
            if (Instance == null || __instance == null || Instance.mainMenuCreditCreated) return;
            Instance.mainMenuCreditCreated = Instance.CreateMainMenuCredit(__instance);
        }

        private bool CreateMainMenuCredit(Component menu)
        {
            // Use an authored menu label as the source of the exact TMP face and
            // material so the credit belongs visually to the game's existing UI.
            FieldInfo itemsField = AccessTools.Field(menu.GetType(), "State0_OBJ");
            GameObject[] items = itemsField == null ? null : itemsField.GetValue(menu) as GameObject[];
            TMP_Text reference = null;
            if (items != null)
            {
                foreach (GameObject item in items)
                {
                    if (item == null) continue;
                    reference = item.GetComponent<TMP_Text>();
                    if (reference != null) break;
                }
            }
            if (reference == null || reference.font == null)
            {
                Logger.LogWarning("Не удалось добавить подпись RTLC TEAM: не найден шрифт главного меню.");
                return false;
            }

            GameObject canvasObject = new GameObject("RTLC TEAM — перевод", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            canvasObject.transform.SetParent(null, false);
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            FieldInfo cameraField = AccessTools.Field(menu.GetType(), "cam");
            GameObject cameraObject = cameraField == null ? null : cameraField.GetValue(menu) as GameObject;
            Camera menuCamera = cameraObject == null ? null : cameraObject.GetComponent<Camera>();
            if (menuCamera != null)
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = menuCamera;
                canvas.planeDistance = Mathf.Clamp(10f, menuCamera.nearClipPlane + 0.1f, menuCamera.farClipPlane - 0.1f);
            }
            else canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            GameObject labelObject = new GameObject("RTLC TEAM", typeof(RectTransform), typeof(TextMeshProUGUI));
            labelObject.transform.SetParent(canvasObject.transform, false);
            RectTransform rect = labelObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-28f, -20f);
            rect.sizeDelta = new Vector2(420f, 56f);

            TextMeshProUGUI label = labelObject.GetComponent<TextMeshProUGUI>();
            label.text = "Перевод: RTLC TEAM";
            label.font = reference.font;
            label.fontSharedMaterial = reference.fontSharedMaterial;
            label.fontSize = 24f;
            label.fontStyle = reference.fontStyle;
            label.color = reference.color;
            label.alignment = TextAlignmentOptions.TopRight;
            label.enableWordWrapping = false;
            label.raycastTarget = false;
            label.margin = Vector4.zero;
            Fonts.Apply(label, label.text);
            Logger.LogInfo("В главное меню добавлена подпись «Перевод: RTLC TEAM».");
            return true;
        }

        private static void BeforeNote(Component __instance)
        {
            if (Instance == null) return;
            Transform child = __instance.transform.Find("text_real") ?? __instance.transform.Find("note_text");
            if (child != null) Instance.FitNote(child.GetComponent<TextMesh>());
        }

        private static void AfterNoteStarted(Component __instance, ref GameObject ___texty)
        {
            // Brain_07 has a visible full note and a separate hidden "illegible" placeholder.
            // Read and hide/show the same visible note instead of activating the placeholder.
            Transform real = __instance.transform.Find("text_real");
            if (real != null && real.GetComponent<TextMesh>() != null) ___texty = real.gameObject;
        }

        private void FitNote(TextMesh text)
        {
            if (text == null || text.GetComponent<WorldNoteLayout>() != null) return;
            // DARK_PRISON deliberately hides four world renderers while keeping their
            // text available in the reading UI. Do not reveal them with a new renderer.
            Renderer originalRenderer = text.GetComponent<Renderer>();
            if (originalRenderer == null || !originalRenderer.enabled || string.IsNullOrEmpty(text.text)) return;
            MeshFilter paper = null;
            for (Transform parent = text.transform.parent; parent != null; parent = parent.parent)
            {
                paper = parent.GetComponent<MeshFilter>();
                if (paper != null && paper.sharedMesh != null) break;
            }
            if (paper == null || paper.sharedMesh == null) return;
            // The game copies this backing text into HUD_text when the note is opened.
            // Keep its original reading layout and use a separate mesh on the paper.
            text.text = Catalog.Translate(text.text);
            Fonts.Apply(text);
            var layout = text.gameObject.AddComponent<WorldNoteLayout>();
            try { layout.Initialize(text, paper); }
            catch (Exception exception)
            {
                Logger.LogError("Не удалось разместить записку " + HierarchyPath(text.transform) + ": " + exception);
                Destroy(layout);
            }
        }

        private void RememberFont(UnityEngine.Object obj, string name)
        {
            if (obj == null) return;
            int id = obj.GetInstanceID();
            if (!fontNames.ContainsKey(id)) fontNames[id] = name;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Logger.LogInfo("Сцена загружена: " + scene.name);
            if (scene.name == "menu") mainMenuCreditCreated = false;
            sceneInspectAt = Time.unscaledTime + 5f;
            loggedTransitionPrompt = false;
            if (textures != null) textures.Apply();
            dirty = true;
            dlcTapeMenuAdjusted = false;
            tapeScreenAdjusted = false;
            worldText.Clear();
            fontNames.Clear();
            manualLayouts.Clear();
            nextScan = Time.unscaledTime + 0.2f;
        }

        private void LateUpdate()
        {
            if (Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 0.5f;
            try
            {
                ScanText(dirty);
                textures.Apply();
                if (sceneInspectAt > 0f && Time.unscaledTime >= sceneInspectAt)
                {
                    sceneInspectAt = 0f;
                    SaveSceneState();
                }
                if (dirty && snapshotOnScene.Value) Snapshot();
                dirty = false;
                string command = Path.Combine(diagnosticRoot, "command.txt");
                if (File.Exists(command))
                {
                    string value = File.ReadAllText(command).Trim();
                    File.Delete(command);
                    if (value == "snapshot") Snapshot();
                }
            }
            catch (Exception exception) { Logger.LogError("Ошибка обновления перевода: " + exception); }
        }

        private void ScanText(bool all)
        {
            var texts = all ? Resources.FindObjectsOfTypeAll<Text>() : FindObjectsOfType<Text>();
            foreach (Text text in texts)
            {
                if (text == null || !text.gameObject.scene.IsValid()) continue;
                RememberFont(text, text.font == null ? "" : text.font.name);
                Fonts.Apply(text);
                LayoutManual(text, text.text);
            }
            var tmpTexts = all ? Resources.FindObjectsOfTypeAll<TMP_Text>() : FindObjectsOfType<TMP_Text>();
            foreach (TMP_Text text in tmpTexts)
            {
                if (text == null || !text.gameObject.scene.IsValid()) continue;
                RememberFont(text, text.font == null ? "" : text.font.name);
                Fonts.Apply(text, Catalog.Translate(text.text));
            }
            var textMeshes = all ? Resources.FindObjectsOfTypeAll<TextMesh>() : FindObjectsOfType<TextMesh>();
            bool showroom = SceneManager.GetActiveScene().name == "Showroom";
            foreach (TextMesh text in textMeshes)
            {
                if (text == null || !text.gameObject.scene.IsValid()) continue;
                RememberFont(text, text.font == null ? "" : text.font.name);
                Font originalFont = Fonts.OriginalWorldFont(text);
                // Preserve the embedded default of each original Showroom font. An
                // unset TextMesh size would otherwise inherit the replacement's 32pt.
                if (showroom && text.fontSize <= 0 && originalFont != null && originalFont.fontSize > 0)
                    text.fontSize = originalFont.fontSize;
                if (text.name == "text_real" || (text.name == "note_text" &&
                    (text.transform.parent == null || text.transform.parent.Find("text_real") == null))) FitNote(text);
                Fonts.Apply(text);
                int id = text.GetInstanceID();
                WorldTextState state;
                if (!worldText.TryGetValue(id, out state) || state.Rendered != text.text)
                {
                    string source = text.text;
                    string translated = Catalog.Translate(source);
                    state = new WorldTextState { Source = source, Rendered = translated, Component = text };
                    worldText[id] = state;
                    if (showroom)
                    {
                        ShowroomTextFit fit = text.GetComponent<ShowroomTextFit>();
                        if (fit == null) fit = text.gameObject.AddComponent<ShowroomTextFit>();
                        fit.RegisterReference(source, translated, originalFont);
                    }
                    if (state.Rendered != text.text) text.text = state.Rendered;
                }
            }
            var guiTexts = all ? Resources.FindObjectsOfTypeAll<GUIText>() : FindObjectsOfType<GUIText>();
            foreach (GUIText text in guiTexts)
            {
                if (text == null || !text.gameObject.scene.IsValid()) continue;
                RememberFont(text, text.font == null ? "" : text.font.name);
                if (Fonts.UnityFallback != null) text.font = Fonts.UnityFallback;
                int id = text.GetInstanceID();
                WorldTextState state;
                if (!worldText.TryGetValue(id, out state) || state.Rendered != text.text)
                {
                    state = new WorldTextState { Source = text.text, Rendered = Catalog.Translate(text.text), Component = text };
                    worldText[id] = state;
                    if (state.Rendered != text.text) text.text = state.Rendered;
                }
            }
            var dead = new List<int>();
            foreach (var pair in worldText) if (pair.Value.Component == null) dead.Add(pair.Key);
            foreach (int id in dead) worldText.Remove(id);
            if (!dlcTapeMenuAdjusted) AdjustDlcTapeMenu();
            if (!tapeScreenAdjusted) AdjustTapeScreen();
        }

        private void AdjustDlcTapeMenu()
        {
            Type dlcMenuType = AccessTools.TypeByName("DLC_tape_menu_scr");
            if (dlcMenuType == null) return;
            var menu = UnityEngine.Object.FindObjectOfType(dlcMenuType);
            if (menu == null) return;
            var hitsField = AccessTools.Field(dlcMenuType, "Hits");
            if (hitsField == null) return;
            var hits = hitsField.GetValue(menu) as GameObject[];
            if (hits == null || hits.Length == 0) return;

            foreach (var hit in hits)
            {
                if (hit == null) continue;
                var rt = hit.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchoredPosition = new Vector2(rt.anchoredPosition.x + 60f, rt.anchoredPosition.y);
                    Logger.LogInfo("DLC Tape Menu: shifted " + hit.name + " to x=" + rt.anchoredPosition.x);
                }
            }
            dlcTapeMenuAdjusted = true;
        }

        private void AdjustTapeScreen()
        {
            Type tapeScreenType = AccessTools.TypeByName("Tape_screen");
            if (tapeScreenType == null) return;
            var screen = UnityEngine.Object.FindObjectOfType(tapeScreenType);
            if (screen == null) return;
            var selField = AccessTools.Field(tapeScreenType, "sel");
            if (selField == null) return;
            var sel = selField.GetValue(screen) as GameObject[];
            if (sel == null || sel.Length == 0) return;

            foreach (var s in sel)
            {
                if (s == null) continue;
                var rt = s.GetComponent<RectTransform>();
                if (rt != null)
                {
                    Logger.LogInfo("Tape_screen sel: " + s.name + " sizeDelta=" + rt.sizeDelta + " pos=" + rt.anchoredPosition);
                }
            }
            tapeScreenAdjusted = true;
        }

        private void LogMissing(string source)
        {
            if (!auditMissing.Value || string.IsNullOrEmpty(source) || !missed.Add(source)) return;
            Directory.CreateDirectory(diagnosticRoot);
            File.AppendAllText(Path.Combine(diagnosticRoot, "missing.jsonl"),
                JsonUtility.ToJson(new MissingRecord { scene = SceneManager.GetActiveScene().name, source = source }) + "\n", Encoding.UTF8);
        }

        private void SaveSceneState()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Scene=" + SceneManager.GetActiveScene().name + "; UTC=" + DateTime.UtcNow.ToString("o") + "; timeScale=" + Time.timeScale);
            Type status = AccessTools.TypeByName("Sys_scr");
            if (status != null)
                foreach (UnityEngine.Object obj in Resources.FindObjectsOfTypeAll(status))
                {
                    Component component = obj as Component;
                    if (component == null || !component.gameObject.scene.IsValid()) continue;
                    sb.Append("Sys=" + HierarchyPath(component.transform));
                    foreach (string fieldName in new string[] { "level", "stage", "real_stage", "t", "t2" })
                    {
                        FieldInfo field = AccessTools.Field(status, fieldName);
                        sb.Append("; " + fieldName + "=" + (field == null ? "?" : Convert.ToString(field.GetValue(obj), CultureInfo.InvariantCulture)));
                    }
                    FieldInfo overlayField = AccessTools.Field(status, "SYS_overlay");
                    GameObject overlay = overlayField == null ? null : overlayField.GetValue(obj) as GameObject;
                    if (overlay != null)
                    {
                        RawImage image = overlay.GetComponent<RawImage>();
                        sb.Append("; overlayActive=" + overlay.activeInHierarchy + "; overlayColor=" + (image == null ? "?" : image.color.ToString()));
                    }
                    sb.AppendLine();
                }
            foreach (Camera camera in Resources.FindObjectsOfTypeAll<Camera>())
                if (camera != null && camera.gameObject.scene.IsValid())
                    sb.AppendLine("Camera=" + HierarchyPath(camera.transform) + "; enabled=" + camera.enabled +
                        "; active=" + camera.gameObject.activeInHierarchy + "; depth=" + camera.depth + "; mask=" + camera.cullingMask +
                        "; target=" + (camera.targetTexture == null ? "screen" : camera.targetTexture.name));
            string directory = diagnosticRoot;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "scene-state.txt"), sb.ToString(), Encoding.UTF8);
            Logger.LogInfo("Состояние сцены сохранено: " + Path.Combine(diagnosticRoot, "scene-state.txt"));
        }

        private void Snapshot()
        {
            var sb = new StringBuilder();
            string sceneName = SceneManager.GetActiveScene().name;
            sb.AppendLine("=== SCENE TEXT SNAPSHOT: " + sceneName + " (" + DateTime.UtcNow.ToString("o") + ") ===");
            sb.AppendLine();

            sb.AppendLine("--- UI.Text ---");
            foreach (Text text in Resources.FindObjectsOfTypeAll<Text>())
            {
                if (text == null || !text.gameObject.scene.IsValid()) continue;
                string originalFont;
                fontNames.TryGetValue(text.GetInstanceID(), out originalFont);
                var rt = text.rectTransform;
                sb.AppendLine(string.Format("[{0}] path='{1}', text='{2}', font='{3}', origFont='{4}', size={5}, align={6}, pos=({7:F1},{8:F1}), sizeDelta=({9:F1},{10:F1})",
                    text.gameObject.activeInHierarchy ? "ACT" : "INA",
                    HierarchyPath(text.transform),
                    (text.text ?? "").Replace("\r", "").Replace("\n", "\\n"),
                    text.font != null ? text.font.name : "null",
                    originalFont ?? "",
                    text.fontSize,
                    text.alignment,
                    rt != null ? rt.anchoredPosition.x : 0f,
                    rt != null ? rt.anchoredPosition.y : 0f,
                    rt != null ? rt.sizeDelta.x : 0f,
                    rt != null ? rt.sizeDelta.y : 0f
                ));
            }

            sb.AppendLine();
            sb.AppendLine("--- TMP_Text ---");
            foreach (TMP_Text text in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (text == null || !text.gameObject.scene.IsValid()) continue;
                string originalFont;
                fontNames.TryGetValue(text.GetInstanceID(), out originalFont);
                var rt = text.rectTransform;
                sb.AppendLine(string.Format("[{0}] path='{1}', text='{2}', font='{3}', origFont='{4}', size={5:F1}, align={6}, pos=({7:F1},{8:F1}), sizeDelta=({9:F1},{10:F1})",
                    text.gameObject.activeInHierarchy ? "ACT" : "INA",
                    HierarchyPath(text.transform),
                    (text.text ?? "").Replace("\r", "").Replace("\n", "\\n"),
                    text.font != null ? text.font.name : "null",
                    originalFont ?? "",
                    text.fontSize,
                    text.alignment,
                    rt != null ? rt.anchoredPosition.x : 0f,
                    rt != null ? rt.anchoredPosition.y : 0f,
                    rt != null ? rt.sizeDelta.x : 0f,
                    rt != null ? rt.sizeDelta.y : 0f
                ));
            }

            sb.AppendLine();
            sb.AppendLine("--- TextMesh ---");
            foreach (TextMesh text in Resources.FindObjectsOfTypeAll<TextMesh>())
            {
                if (text == null || !text.gameObject.scene.IsValid()) continue;
                string originalFont;
                fontNames.TryGetValue(text.GetInstanceID(), out originalFont);
                var pos = text.transform.position;
                sb.AppendLine(string.Format("[{0}] path='{1}', text='{2}', font='{3}', origFont='{4}', size={5}, align={6}, worldPos=({7:F1},{8:F1},{9:F1})",
                    text.gameObject.activeInHierarchy ? "ACT" : "INA",
                    HierarchyPath(text.transform),
                    (text.text ?? "").Replace("\r", "").Replace("\n", "\\n"),
                    text.font != null ? text.font.name : "null",
                    originalFont ?? "",
                    text.fontSize,
                    text.alignment,
                    pos.x, pos.y, pos.z
                ));
            }

            sb.AppendLine();
            sb.AppendLine("--- GUIText ---");
            foreach (GUIText text in Resources.FindObjectsOfTypeAll<GUIText>())
            {
                if (text == null || !text.gameObject.scene.IsValid()) continue;
                string originalFont;
                fontNames.TryGetValue(text.GetInstanceID(), out originalFont);
                sb.AppendLine(string.Format("[{0}] path='{1}', text='{2}', font='{3}', origFont='{4}', size={5}, align={6}",
                    text.gameObject.activeInHierarchy ? "ACT" : "INA",
                    HierarchyPath(text.transform),
                    (text.text ?? "").Replace("\r", "").Replace("\n", "\\n"),
                    text.font != null ? text.font.name : "null",
                    originalFont ?? "",
                    text.fontSize,
                    text.alignment
                ));
            }

            string path = diagnosticRoot;
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "snapshot.txt"), sb.ToString(), Encoding.UTF8);
            Logger.LogInfo("Диагностический снимок сохранён: " + Path.Combine(diagnosticRoot, "snapshot.txt"));
        }

        private TextRecord Record(Component component, string type, string original, string translated, string font, string missing, int count)
        {
            string originalFont;
            fontNames.TryGetValue(component.GetInstanceID(), out originalFont);
            return new TextRecord { path = HierarchyPath(component.transform), type = type, original = original,
                translated = translated, active = component.gameObject.activeInHierarchy,
                font = font, originalFont = originalFont, missingGlyphs = missing, generatedCount = count };
        }

        private static string HierarchyPath(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null) { transform = transform.parent; path = transform.name + "/" + path; }
            return path;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (harmony != null) harmony.UnpatchSelf();
            if (Fonts != null) Fonts.Dispose();
            if (bundle != null) bundle.Dispose();
            if (Instance == this) Instance = null;
        }
    }

    internal sealed class ManualLayoutState
    {
        public Text Component; public int FontSize; public HorizontalWrapMode Overflow; public bool BestFit;
    }
    internal sealed class WorldTextState { public string Source; public string Rendered; public Component Component; }
    [Serializable] public sealed class MissingRecord { public string scene; public string source; }
    [Serializable] public sealed class TextSnapshot { public string scene; public string utc; public TextRecord[] texts; }
    [Serializable] public sealed class TextRecord
    {
        public string path; public string type; public string original; public string translated; public string parsed;
        public string font; public string originalFont; public string missingGlyphs; public bool active; public int generatedCount;
    }
}
