using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
// UnityEngine.UI et UnityEngine.UIElements ont des types de même nom (Image, Button…) : on ne prend que ceux utiles.
using PanelInputConfiguration = UnityEngine.UIElements.PanelInputConfiguration;
using PanelSettings = UnityEngine.UIElements.PanelSettings;
using UIDocument = UnityEngine.UIElements.UIDocument;
using VisualTreeAsset = UnityEngine.UIElements.VisualTreeAsset;

namespace RageRoom.EditorTools
{
    /// <summary>
    /// Crée la scène du menu principal (SC_Menu) et la place en premier dans les Build Settings.
    /// Se lance tout seul une fois si la scène n'existe pas encore ; sinon via
    /// « Rage Room > Menu > Recréer la scène du menu ». Ne modifie jamais la scène ouverte.
    /// Prépare aussi ce dont les menus en UI Toolkit ont besoin : leur Panel Settings et l'asset
    /// « MenuUiSkin » qui pointe vers MainMenu.uxml et InGameMenu.uxml.
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
        const string SkinPath = Folder + "/Resources/MenuUiSkin.asset";
        const string PanelSettingsPath = Folder + "/MenuPanelSettings.asset";
        const string RigPath = "Assets/Samples/XR Interaction Toolkit/3.5.1/Hands Interaction Demo/Prefabs/XR Origin Hands (XR Rig).prefab";
        const string HandModels = "Assets/Samples/XR Hands/1.8.1/HandVisualizer/";

        // Panneau du menu principal : mêmes dimensions que l'ancien canvas (560 x 620 pixels à 1,2 mm le pixel)
        static readonly Vector2 MenuSize = new Vector2(560f, 620f);
        const float MenuScale = 1.2f;

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

        [MenuItem("Rage Room/Menu/Relier les UXML des menus")]
        static void LinkUiAssets()
        {
            if (EnsureUiSkin(true) != null)
                Debug.Log($"[Rage Room] Menus reliés : {SkinPath} pointe vers MainMenu.uxml, InGameMenu.uxml et {PanelSettingsPath}.");
        }

        // ---------- Assets des menus en UI Toolkit ----------

        /// <summary>
        /// Crée ou complète l'asset « MenuUiSkin » (Panel Settings + les deux UXML, cherchés par leur nom dans le projet).
        /// Renvoie null s'il manque quelque chose ; <paramref name="log"/> décide si on l'écrit dans la console.
        /// </summary>
        static MenuUiSkin EnsureUiSkin(bool log)
        {
            var skin = AssetDatabase.LoadAssetAtPath<MenuUiSkin>(SkinPath);
            if (skin == null)
            {
                skin = ScriptableObject.CreateInstance<MenuUiSkin>();
                AssetDatabase.CreateAsset(skin, SkinPath);
            }

            var panelSettings = skin.panelSettings != null ? skin.panelSettings : EnsurePanelSettings(log);
            var mainMenu = skin.mainMenu != null ? skin.mainMenu : FindUxml("MainMenu", log);
            var inGameMenu = skin.inGameMenu != null ? skin.inGameMenu : FindUxml("InGameMenu", log);

            if (panelSettings != skin.panelSettings || mainMenu != skin.mainMenu || inGameMenu != skin.inGameMenu)
            {
                skin.panelSettings = panelSettings;
                skin.mainMenu = mainMenu;
                skin.inGameMenu = inGameMenu;
                EditorUtility.SetDirty(skin);
                AssetDatabase.SaveAssets();
            }

            return panelSettings != null && mainMenu != null && inGameMenu != null ? skin : null;
        }

        static VisualTreeAsset FindUxml(string fileName, bool log)
        {
            foreach (var guid in AssetDatabase.FindAssets(fileName + " t:VisualTreeAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == fileName)
                    return AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
            }
            if (log)
                Debug.LogError($"[Rage Room] {fileName}.uxml introuvable dans le projet. Importe MainMenu.uxml, InGameMenu.uxml, Settings.uxml et Menu.uss "
                             + "dans un même dossier, puis relance « Rage Room > Menu > Relier les UXML des menus ».");
            return null;
        }

        /// <summary>
        /// Panel Settings réservé aux menus : affichage dans le monde, 1 pixel = 1 mm, et un collider créé
        /// automatiquement à la taille du panneau pour que les rayons des manettes le touchent.
        /// Il part d'une copie d'un Panel Settings du projet (celui du shop de préférence) pour garder son thème.
        /// </summary>
        static PanelSettings EnsurePanelSettings(bool log)
        {
            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (settings == null)
            {
                string source = null;
                foreach (var guid in AssetDatabase.FindAssets("t:PanelSettings"))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var candidate = path.StartsWith("Assets/") ? AssetDatabase.LoadAssetAtPath<PanelSettings>(path) : null;
                    if (candidate == null || candidate.themeStyleSheet == null) continue;

                    var renderMode = new SerializedObject(candidate).FindProperty("m_RenderMode");
                    bool worldSpace = renderMode != null && renderMode.intValue == 1;
                    if (source == null || worldSpace) source = path;
                    if (worldSpace) break;
                }

                if (source == null)
                {
                    if (log)
                        Debug.LogError("[Rage Room] Aucun Panel Settings avec un thème dans le projet, impossible de créer celui des menus. "
                                     + "Crée-en un (Assets > Create > UI Toolkit > Panel Settings Asset), puis relance « Rage Room > Menu > Relier les UXML des menus ».");
                    return null;
                }

                if (!AssetDatabase.CopyAsset(source, PanelSettingsPath))
                {
                    if (log) Debug.LogError($"[Rage Room] Copie de {source} vers {PanelSettingsPath} impossible.");
                    return null;
                }
                settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
                if (settings == null) return null;
                Debug.Log($"[Rage Room] Panel Settings des menus créé : {PanelSettingsPath} (copie de {source}).");
            }

            // Ces réglages n'ont pas tous d'accès public selon la version de Unity : on écrit les champs sérialisés.
            var so = new SerializedObject(settings);
            bool ok = true;
            ok &= Set(so, "m_RenderMode", p => p.intValue = 1);                       // World Space
            ok &= Set(so, "m_PixelsPerUnit", p => p.floatValue = MenuUi.PixelsPerUnit);
            ok &= Set(so, "m_ColliderUpdateMode", p => p.intValue = 2);               // Match 2-D document rect
            ok &= Set(so, "m_ColliderIsTrigger", p => p.boolValue = true);            // les objets lancés traversent le menu
            so.ApplyModifiedPropertiesWithoutUndo();
            if (!ok)
                Debug.LogWarning($"[Rage Room] Réglages à vérifier à la main sur {PanelSettingsPath} : Render Mode « World Space », "
                               + $"Pixels Per Unit {MenuUi.PixelsPerUnit:0}, Collider Update Mode « Match 2-D document rect », Collider Is Trigger coché.", settings);
            AssetDatabase.SaveAssets();
            return settings;
        }

        static bool Set(SerializedObject so, string propertyName, System.Action<SerializedProperty> assign)
        {
            var property = so.FindProperty(propertyName);
            if (property == null) return false;
            assign(property);
            return true;
        }

        static void EnsureHandsConfig()
        {
            EnsureUiSkin(false);
            if (AssetDatabase.LoadAssetAtPath<ControllerHandsConfig>(ConfigPath) != null) return;
            var config = ScriptableObject.CreateInstance<ControllerHandsConfig>();
            config.leftHandModel = AssetDatabase.LoadAssetAtPath<GameObject>(HandModels + "Models/LeftHand.fbx");
            config.rightHandModel = AssetDatabase.LoadAssetAtPath<GameObject>(HandModels + "Models/RightHand.fbx");
            config.handMaterial = AssetDatabase.LoadAssetAtPath<Material>(HandModels + "Materials/HandsDefaultMaterial.mat");
            AssetDatabase.CreateAsset(config, ConfigPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Rage Room] Config des mains virtuelles créée : {ConfigPath}");
        }

        // ---------- Scène du menu ----------

        static void Build()
        {
            if (SceneManager.GetSceneByPath(ScenePath).isLoaded)
            {
                Debug.LogError("[Rage Room] Ferme SC_Menu avant de la recréer.");
                return;
            }

            var skin = EnsureUiSkin(true);
            if (skin == null)
            {
                Debug.LogError("[Rage Room] Scène du menu non créée : il manque les fichiers des menus (voir les messages ci-dessus).");
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

                // Entrées VR. Le menu est en UI Toolkit, le fond en canvas : avec les deux systèmes dans la scène,
                // XR Interaction Toolkit demande « Bypass UI Toolkit Events » décoché.
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<XRUIInputModule>().bypassUIToolkitEvents = false;

                // Ce qui permet aux rayons des manettes d'agir sur UI Toolkit
                new GameObject("XR UI Toolkit Manager").AddComponent<XRUIToolkitManager>();
                new GameObject("Panel Input Configuration").AddComponent<PanelInputConfiguration>()
                    .panelInputRedirection = PanelInputConfiguration.PanelInputRedirection.Never; // « No input redirection »

                // Fond 2D : la capture d'écran du jeu sur un grand panneau (simple image, sans interaction)
                var bgCanvas = MakeCanvas("Fond (capture du jeu)", new Vector3(0f, 1.6f, 3.2f), 0.0025f, new Vector2(1920f, 1920f * 438f / 782f), cam);
                var bgImage = new GameObject("Image", typeof(RectTransform)).AddComponent<Image>();
                bgImage.transform.SetParent(bgCanvas.transform, false);
                Stretch(bgImage.rectTransform);
                bgImage.sprite = background;
                bgImage.preserveAspect = true;
                bgImage.raycastTarget = false;

                // Menu interactif devant le fond : un UI Document qui affiche MainMenu.uxml.
                // Le pivot est le centre du panneau ; son collider est créé tout seul en Play.
                var menu = new GameObject("Menu");
                menu.transform.position = new Vector3(0f, 1.3f, 1.8f);
                menu.transform.localScale = Vector3.one * MenuScale;
                var document = menu.AddComponent<UIDocument>();
                document.panelSettings = skin.panelSettings;
                document.visualTreeAsset = skin.mainMenu;
                document.worldSpaceSizeMode = UIDocument.WorldSpaceSizeMode.Fixed;
                document.worldSpaceSize = MenuSize;
                document.pivotReferenceSize = UnityEngine.UIElements.PivotReferenceSize.Layout; // pivot fixe, même si un bouton grossit au survol
                menu.AddComponent<MainMenu>();

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

        // ---------- Helpers ----------

        /// <summary>Canvas dans le monde, sans interaction (sert au fond).</summary>
        static Canvas MakeCanvas(string name, Vector3 pos, float pixelSize, Vector2 size, Camera cam)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.position = pos;
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = cam;
            go.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 3f;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = size;
            rt.localScale = Vector3.one * pixelSize;
            return canvas;
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
