using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace RageRoom.EditorTools
{
    /// <summary>
    /// Crée la scène du menu principal (SC_Menu) et la place en premier dans les Build Settings.
    /// Se lance tout seul une fois si la scène n'existe pas encore ; sinon via
    /// « Rage Room > Menu > Recréer la scène du menu ». Ne modifie jamais la scène ouverte.
    /// </summary>
    [InitializeOnLoad]
    static class MenuSceneBuilder
    {
        const string Folder = "Assets/Source Codes/Menu";
        const string DefaultScenePath = "Assets/Scenes/SC_Menu.unity";

        /// <summary>Chemin actuel de SC_Menu, où qu'elle ait été déplacée (sinon l'emplacement par défaut).</summary>
        static string ScenePath
        {
            get
            {
                foreach (var guid in AssetDatabase.FindAssets("SC_Menu t:Scene"))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (Path.GetFileNameWithoutExtension(path) == "SC_Menu") return path;
                }
                return DefaultScenePath;
            }
        }
        const string BackgroundPath = Folder + "/Textures/MenuBackground.png";
        const string ConfigPath = Folder + "/Resources/ControllerHandsConfig.asset";
        const string RigPath = "Assets/Samples/XR Interaction Toolkit/3.5.1/Hands Interaction Demo/Prefabs/XR Origin Hands (XR Rig).prefab";
        const string HandModels = "Assets/Samples/XR Hands/1.8.1/HandVisualizer/";

        static readonly Color PanelColor = new Color(0.05f, 0.05f, 0.06f, 0.85f);
        static readonly Color Red = new Color(0.86f, 0.16f, 0.16f);
        static readonly Color Grey = new Color(0.25f, 0.25f, 0.28f);

        static MenuSceneBuilder()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                EnsureHandsConfig();
                if (!File.Exists(ScenePath))
                {
                    Debug.Log("[Rage Room] Création automatique de la scène du menu…");
                    Build();
                }
            };
        }

        [MenuItem("Rage Room/Menu/Recréer la scène du menu")]
        static void Rebuild()
        {
            if (File.Exists(ScenePath) &&
                !EditorUtility.DisplayDialog("Rage Room", $"Recréer {ScenePath} ? Les modifications faites à la main dans cette scène seront perdues.", "Recréer", "Annuler"))
                return;
            Build();
        }

        static void EnsureUiSkin()
        {
            const string skinPath = Folder + "/Resources/MenuUiSkin.asset";
            if (AssetDatabase.LoadAssetAtPath<MenuUiSkin>(skinPath) != null) return;
            var skin = ScriptableObject.CreateInstance<MenuUiSkin>();
            skin.standard = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            skin.background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd");
            skin.knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");
            AssetDatabase.CreateAsset(skin, skinPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Rage Room] Skin des menus en jeu créé : {skinPath}");
        }

        static void EnsureHandsConfig()
        {
            EnsureUiSkin();
            if (AssetDatabase.LoadAssetAtPath<ControllerHandsConfig>(ConfigPath) != null) return;
            var config = ScriptableObject.CreateInstance<ControllerHandsConfig>();
            config.leftHandModel = AssetDatabase.LoadAssetAtPath<GameObject>(HandModels + "Models/LeftHand.fbx");
            config.rightHandModel = AssetDatabase.LoadAssetAtPath<GameObject>(HandModels + "Models/RightHand.fbx");
            config.handMaterial = AssetDatabase.LoadAssetAtPath<Material>(HandModels + "Materials/HandsDefaultMaterial.mat");
            AssetDatabase.CreateAsset(config, ConfigPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Rage Room] Config des mains virtuelles créée : {ConfigPath}");
        }

        static void Build()
        {
            if (SceneManager.GetSceneByPath(ScenePath).isLoaded)
            {
                Debug.LogError("[Rage Room] Ferme SC_Menu avant de la recréer.");
                return;
            }

            var background = ImportBackground();
            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigPath);
            if (rigPrefab == null) { Debug.LogError($"[Rage Room] Rig introuvable : {RigPath}"); return; }

            var previous = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene); // les nouveaux objets et l'éclairage vont dans cette scène
            try
            {
                // Éclairage : fond noir, pas de ciel
                RenderSettings.skybox = null;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.45f);
                var light = new GameObject("Directional Light").AddComponent<Light>();
                light.type = LightType.Directional;
                light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

                // Joueur : même rig que le jeu, sans déplacement
                var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab, scene);
                rig.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var locomotion = FindDeep(rig.transform, "Locomotion");
                if (locomotion != null) locomotion.gameObject.SetActive(false);
                var cam = rig.GetComponentInChildren<Camera>(true);
                if (cam != null)
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0.02f, 0.02f, 0.025f);
                }

                // UI VR
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<XRUIInputModule>();

                // Fond 2D : la capture d'écran du jeu sur un grand panneau
                var bgCanvas = MakeCanvas("Fond (capture du jeu)", new Vector3(0f, 1.6f, 3.2f), 0.0025f, new Vector2(1920f, 1920f * 438f / 782f), cam, false);
                var bgImage = new GameObject("Image", typeof(RectTransform)).AddComponent<Image>();
                bgImage.transform.SetParent(bgCanvas.transform, false);
                Stretch(bgImage.rectTransform);
                bgImage.sprite = background;
                bgImage.preserveAspect = true;
                bgImage.raycastTarget = false;

                // Menu interactif devant le fond
                var menuCanvas = MakeCanvas("Menu", new Vector3(0f, 1.3f, 1.8f), 0.0012f, new Vector2(560f, 620f), cam, true);
                var menu = menuCanvas.gameObject.AddComponent<MainMenu>();

                // Panneau principal
                var main = MakePanel(menuCanvas.transform, "Principal");
                var play = MakeButton(main, "JOUER", Red, 40, 96);
                var settings = MakeButton(main, "PARAMÈTRES", Grey, 34, 84);
                var quit = MakeButton(main, "QUITTER", Grey, 34, 84);

                // Panneau paramètres
                var opts = MakePanel(menuCanvas.transform, "Paramètres");
                MakeText(opts, "PARAMÈTRES", 38, FontStyles.Bold, TextAlignmentOptions.Center, 56, Color.white);
                var master = MakeSliderRow(opts, "Volume général", out var masterValue);
                var fx = MakeSliderRow(opts, "Volume des effets", out var fxValue);
                MakeText(opts, "Dans les mains", 24, FontStyles.Normal, TextAlignmentOptions.Left, 34, new Color(1f, 1f, 1f, 0.8f));
                var choice = MakeRow(opts, "Choix manettes / mains", 64);
                var controllers = MakeButton(choice, "Manettes", Red, 28, 64);
                var hands = MakeButton(choice, "Mains", Grey, 28, 64);
                var back = MakeButton(opts, "Retour", Grey, 28, 64);

                var so = new SerializedObject(menu);
                so.FindProperty("mainPanel").objectReferenceValue = main.gameObject;
                so.FindProperty("settingsPanel").objectReferenceValue = opts.gameObject;
                so.FindProperty("playButton").objectReferenceValue = play;
                so.FindProperty("settingsButton").objectReferenceValue = settings;
                so.FindProperty("quitButton").objectReferenceValue = quit;
                so.FindProperty("masterSlider").objectReferenceValue = master;
                so.FindProperty("masterValue").objectReferenceValue = masterValue;
                so.FindProperty("fxSlider").objectReferenceValue = fx;
                so.FindProperty("fxValue").objectReferenceValue = fxValue;
                so.FindProperty("controllersButton").objectReferenceValue = controllers;
                so.FindProperty("handsButton").objectReferenceValue = hands;
                so.FindProperty("backButton").objectReferenceValue = back;
                so.ApplyModifiedPropertiesWithoutUndo();
                opts.gameObject.SetActive(false);

                EditorSceneManager.SaveScene(scene, ScenePath);
            }
            finally
            {
                if (previous.IsValid()) SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(scene, true);
            }

            AddToBuildSettingsFirst();
            Debug.Log($"[Rage Room] Scène du menu créée : {ScenePath} (1re dans les Build Settings). Ouvre-la pour la voir.");
        }

        static Sprite ImportBackground()
        {
            var importer = AssetImporter.GetAtPath(BackgroundPath) as TextureImporter;
            if (importer == null) { Debug.LogWarning($"[Rage Room] Image de fond introuvable : {BackgroundPath}"); return null; }
            if (importer.textureType != TextureImporterType.Sprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Sprite>(BackgroundPath);
        }

        [MenuItem("Rage Room/Menu/Mettre le menu en 1re scène du build")]
        static void AddToBuildSettingsFirst()
        {
            var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            list.RemoveAll(s => s.path == ScenePath);
            list.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = list.ToArray();
            AssetDatabase.SaveAssets(); // écrit ProjectSettings/EditorBuildSettings.asset tout de suite
        }

        // ---------- Helpers UI ----------

        static Canvas MakeCanvas(string name, Vector3 pos, float pixelSize, Vector2 size, Camera cam, bool interactive)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.position = pos;
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = cam;
            go.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 3f;
            if (interactive) go.AddComponent<TrackedDeviceGraphicRaycaster>();
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = size;
            rt.localScale = Vector3.one * pixelSize;
            return canvas;
        }

        static RectTransform MakePanel(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            Stretch(rt);
            var img = go.AddComponent<Image>();
            img.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            img.type = Image.Type.Sliced;
            img.color = PanelColor;
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(40, 40, 40, 40);
            layout.spacing = 18f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return rt;
        }

        static RectTransform MakeRow(Transform parent, string name, float height)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var h = go.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 14f;
            h.childAlignment = TextAnchor.MiddleCenter;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = true;
            h.childForceExpandHeight = true;
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = le.minHeight = height;
            return (RectTransform)go.transform;
        }

        static TMP_Text MakeText(Transform parent, string text, float size, FontStyles style, TextAlignmentOptions align, float height, Color color)
        {
            var go = new GameObject("Texte", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = align;
            t.color = color;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            var le = go.AddComponent<LayoutElement>();
            if (height > 0f) le.preferredHeight = le.minHeight = height;
            return t;
        }

        static Button MakeButton(Transform parent, string label, Color color, float fontSize, float height)
        {
            var go = new GameObject("Bouton " + label, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            img.type = Image.Type.Sliced;
            var b = go.AddComponent<Button>();
            b.navigation = new Navigation { mode = Navigation.Mode.None };
            var cb = b.colors;
            cb.normalColor = color;
            cb.selectedColor = color;
            cb.highlightedColor = Color.Lerp(color, Color.white, 0.25f);
            cb.pressedColor = Color.Lerp(color, Color.black, 0.3f);
            cb.fadeDuration = 0.05f;
            b.colors = cb;
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = le.minHeight = height;
            le.flexibleWidth = 1f;

            var t = MakeText(go.transform, label, fontSize, FontStyles.Bold, TextAlignmentOptions.Center, 0f, Color.white);
            Stretch((RectTransform)t.transform);
            return b;
        }

        static Slider MakeSliderRow(Transform parent, string label, out TMP_Text value)
        {
            var header = MakeRow(parent, label, 34);
            var name = MakeText(header, label, 24, FontStyles.Normal, TextAlignmentOptions.Left, 0f, new Color(1f, 1f, 1f, 0.8f));
            name.GetComponent<LayoutElement>().flexibleWidth = 1f;
            value = MakeText(header, "100 %", 24, FontStyles.Bold, TextAlignmentOptions.Right, 0f, Color.white);
            value.GetComponent<LayoutElement>().preferredWidth = 100f;
            header.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = false;

            var res = new DefaultControls.Resources
            {
                standard = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"),
                background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd"),
                knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd"),
            };
            var go = DefaultControls.CreateSlider(res);
            go.name = "Slider " + label;
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = le.minHeight = 36f;
            var slider = go.GetComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 1f;
            slider.navigation = new Navigation { mode = Navigation.Mode.None };

            // Couleurs : remplissage rouge, poignée blanche agrandie
            var fill = go.transform.Find("Fill Area/Fill")?.GetComponent<Image>();
            if (fill != null) fill.color = Red;
            var handle = go.transform.Find("Handle Slide Area/Handle") as RectTransform;
            if (handle != null) handle.sizeDelta = new Vector2(40f, 0f);
            return slider;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        static Transform FindDeep(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name) return child;
                var found = FindDeep(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
