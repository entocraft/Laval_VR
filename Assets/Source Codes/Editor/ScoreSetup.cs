using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace RageRoom
{
    /// <summary>
    /// Installation du score, dans l'éditeur. Menu « Rage Room > Score » :
    ///
    ///  - « Installer ou mettre à jour le score » : crée le barème (ScoreTable.asset, dans un dossier Resources
    ///    à côté des scripts), crée le Panel Settings du HUD, relie ScoreHud.uxml et la police du shop,
    ///    ajoute le composant ScoreValue sur tous les prefabs cassables et une ligne par objet dans le barème.
    ///    À relancer après avoir créé un nouvel objet cassable : rien de ce qui existe n'est écrasé,
    ///    les points déjà réglés à la main sont conservés.
    ///  - « Ouvrir le barème » : sélectionne l'asset pour régler les points et les combos dans l'Inspector.
    ///
    /// À la première importation, une fenêtre propose de lancer l'installation.
    /// Ce fichier doit rester dans un dossier nommé « Editor ».
    /// </summary>
    [InitializeOnLoad]
    static class ScoreSetup
    {
        const string PanelSettingsFile = "ScoreHudPanelSettings.asset";
        const string HudUxmlName = "ScoreHud";
        const string HudFontName = "BarlowCondensed-ExtraBoldItalic";
        const string AskedKey = "RageRoom.ScoreSetup.Asked";

        /// <summary>1000 pixels par mètre : 1 pixel du HUD = 1 mm, comme les menus.</summary>
        const float PixelsPerUnit = 1000f;

        static ScoreSetup()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                if (FindTable() != null || SessionState.GetBool(AskedKey, false)) return;
                SessionState.SetBool(AskedKey, true);

                if (EditorUtility.DisplayDialog("Rage Room — Score",
                        "Installer le score maintenant ?\n\n"
                      + "• crée le barème (ScoreTable) et le Panel Settings du HUD ;\n"
                      + "• ajoute le composant ScoreValue sur les prefabs cassables.\n\n"
                      + "Tu peux aussi le faire plus tard : menu Rage Room > Score > Installer ou mettre à jour le score.",
                        "Installer", "Plus tard"))
                    Install();
            };
        }

        [MenuItem("Rage Room/Score/Installer ou mettre à jour le score")]
        static void Install()
        {
            string folder = ScriptFolder();
            if (folder == null)
            {
                Debug.LogError("[Score] ScoreManager.cs introuvable dans le projet : importe d'abord les scripts du score.");
                return;
            }

            // ---------- Barème ----------
            ScoreTable table = FindTable();
            if (table == null)
            {
                string resources = folder + "/Resources";
                EnsureFolder(resources);
                table = ScriptableObject.CreateInstance<ScoreTable>();
                AssetDatabase.CreateAsset(table, resources + "/" + ScoreTable.ResourceName + ".asset");
                Debug.Log($"[Score] Barème créé : {resources}/{ScoreTable.ResourceName}.asset", table);
            }
            else
            {
                string tablePath = AssetDatabase.GetAssetPath(table);
                if (!tablePath.Contains("/Resources/") || Path.GetFileNameWithoutExtension(tablePath) != ScoreTable.ResourceName)
                    Debug.LogWarning($"[Score] Le barème doit s'appeler « {ScoreTable.ResourceName} » et se trouver dans un dossier Resources "
                                   + $"pour être chargé en jeu. Il est ici : {tablePath}", table);
            }

            // ---------- HUD ----------
            if (table.hudUxml == null) table.hudUxml = FindAsset<VisualTreeAsset>(HudUxmlName);
            if (table.hudUxml == null)
                Debug.LogError("[Score] ScoreHud.uxml introuvable : importe ScoreHud.uxml et ScoreHud.uss dans un même dossier, "
                             + "puis relance « Rage Room > Score > Installer ou mettre à jour le score ».");

            if (table.hudFont == null) table.hudFont = FindAsset<Font>(HudFontName);
            if (table.hudPanelSettings == null) table.hudPanelSettings = EnsurePanelSettings(folder);

            // ---------- Objets cassables ----------
            int prefabsSeen = 0, componentsAdded = 0, rowsAdded = 0;
            var failed = new List<string>();

            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".prefab")) continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null || !IsTarget(prefab)) continue;
                prefabsSeen++;

                GameObject carrier = Carrier(prefab);
                if (carrier.GetComponent<ScoreValue>() == null)
                {
                    if (AddValueToPrefab(path, prefab.name)) componentsAdded++;
                    else failed.Add(path);
                }

                var value = carrier.GetComponent<ScoreValue>();
                string id = value != null && !string.IsNullOrEmpty(value.objectId) ? value.objectId : prefab.name;
                if (AddRow(table, id, MaterialOf(prefab), SizeOf(prefab))) rowsAdded++;
            }

            // Objets posés à la main dans la scène ouverte : ils reçoivent leur composant au lancement du jeu,
            // on leur prépare seulement une ligne dans le barème.
            foreach (RuntimeFracture f in Object.FindObjectsByType<RuntimeFracture>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (AddSceneRow(table, f.gameObject)) rowsAdded++;
            foreach (RuntimeCrush c in Object.FindObjectsByType<RuntimeCrush>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (AddSceneRow(table, c.gameObject)) rowsAdded++;

            EditorUtility.SetDirty(table);
            AssetDatabase.SaveAssets();

            Debug.Log($"[Score] Installation terminée : {prefabsSeen} prefab(s) cassable(s), {componentsAdded} composant(s) ScoreValue ajouté(s), "
                    + $"{rowsAdded} ligne(s) ajoutée(s) au barème ({table.objects.Count} au total). "
                    + "Règle les points dans « Rage Room > Score > Ouvrir le barème ».", table);
            foreach (string path in failed)
                Debug.LogWarning($"[Score] Impossible de modifier {path} : ajoute le composant ScoreValue à la main, à côté du script de casse.");
            if (table.hudFont == null)
                Debug.Log($"[Score] Police « {HudFontName} » introuvable : le HUD utilise la police par défaut. "
                        + "Tu peux glisser une police dans le champ Hud Font du barème.", table);
        }

        [MenuItem("Rage Room/Score/Ouvrir le barème")]
        static void OpenTable()
        {
            ScoreTable table = FindTable();
            if (table == null)
            {
                Debug.LogWarning("[Score] Aucun barème : lance d'abord « Rage Room > Score > Installer ou mettre à jour le score ».");
                return;
            }
            Selection.activeObject = table;
            EditorGUIUtility.PingObject(table);
        }

        // ------------------------------------------------------------------ Objets cassables

        /// <summary>Vrai pour un prefab qui se casse ou s'écrase et qui n'est ni une arme ni un outil.</summary>
        static bool IsTarget(GameObject prefab)
        {
            if (prefab.GetComponentInChildren<WeaponGrab>(true) != null) return false;
            var fracture = prefab.GetComponentInChildren<RuntimeFracture>(true);
            var crush = prefab.GetComponentInChildren<RuntimeCrush>(true);
            if (fracture == null && crush == null) return false;
            return crush != null || fracture.preset != RuntimeFracture.Preset.Outil;
        }

        /// <summary>Objet du prefab qui porte le script de casse (ou d'écrasement) : c'est là que va ScoreValue.</summary>
        static GameObject Carrier(GameObject root)
        {
            var fracture = root.GetComponentInChildren<RuntimeFracture>(true);
            if (fracture != null) return fracture.gameObject;
            var crush = root.GetComponentInChildren<RuntimeCrush>(true);
            return crush != null ? crush.gameObject : root;
        }

        static bool AddValueToPrefab(string path, string id)
        {
            GameObject contents = null;
            try
            {
                contents = PrefabUtility.LoadPrefabContents(path);
                GameObject carrier = Carrier(contents);
                var value = carrier.GetComponent<ScoreValue>();
                if (value == null) value = carrier.AddComponent<ScoreValue>();
                if (string.IsNullOrEmpty(value.objectId)) value.objectId = id;
                PrefabUtility.SaveAsPrefabAsset(contents, path);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                return false;
            }
            finally
            {
                if (contents != null) PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        static bool AddSceneRow(ScoreTable table, GameObject go)
        {
            if (go.GetComponent<WeaponGrab>() != null) return false;
            var fracture = go.GetComponent<RuntimeFracture>();
            if (fracture != null && fracture.preset == RuntimeFracture.Preset.Outil && go.GetComponent<RuntimeCrush>() == null) return false;

            var value = go.GetComponent<ScoreValue>();
            string id = value != null ? value.TableId : ScoreValue.CleanName(go.name);
            return AddRow(table, id, MaterialOf(go), SizeOf(go));
        }

        /// <summary>Ajoute une ligne au barème si l'objet n'y est pas encore, avec une valeur proposée.</summary>
        static bool AddRow(ScoreTable table, string id, string material, float size)
        {
            if (string.IsNullOrEmpty(id) || table.FindObject(id) != null) return false;

            // Valeur proposée : celle du matériau, augmentée pour les gros objets (référence : 30 cm).
            MaterialPoints m = table.FindMaterial(material);
            float basePoints = m != null ? m.points : table.defaultPoints;
            float factor = size > 0f ? Mathf.Clamp(size / 0.3f, 0.5f, 4f) : 1f;
            int points = Mathf.Max(10, Mathf.RoundToInt(basePoints * factor / 10f) * 10);

            table.objects.Add(new ObjectPoints(id, ObjectNames.NicifyVariableName(id.Replace('_', ' ')), points));
            return true;
        }

        static string MaterialOf(GameObject root)
        {
            var fracture = root.GetComponentInChildren<RuntimeFracture>(true);
            if (fracture != null && !string.IsNullOrEmpty(fracture.soundMaterial)) return fracture.soundMaterial;
            var crush = root.GetComponentInChildren<RuntimeCrush>(true);
            return crush != null ? crush.soundMaterial : "";
        }

        /// <summary>Plus grande dimension de l'objet, en mètres (0 si elle ne peut pas être mesurée).</summary>
        static float SizeOf(GameObject root)
        {
            bool any = false;
            var bounds = new Bounds();
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (any) bounds.Encapsulate(r.bounds);
                else bounds = r.bounds;
                any = true;
            }
            if (!any) return 0f;
            Vector3 s = bounds.size;
            return Mathf.Max(s.x, Mathf.Max(s.y, s.z));
        }

        // ------------------------------------------------------------------ Panel Settings du HUD

        /// <summary>
        /// Panel Settings réservé au HUD : affichage dans le monde, 1 pixel = 1 mm, et aucun collider,
        /// pour que le HUD ne gêne ni les rayons des manettes ni les objets. Il part d'une copie d'un
        /// Panel Settings du projet (celui des menus ou du shop) pour garder son thème.
        /// </summary>
        static PanelSettings EnsurePanelSettings(string folder)
        {
            string path = folder + "/" + PanelSettingsFile;
            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (settings == null)
            {
                string source = null;
                foreach (string guid in AssetDatabase.FindAssets("t:PanelSettings"))
                {
                    string candidatePath = AssetDatabase.GUIDToAssetPath(guid);
                    var candidate = candidatePath.StartsWith("Assets/") ? AssetDatabase.LoadAssetAtPath<PanelSettings>(candidatePath) : null;
                    if (candidate == null || candidate.themeStyleSheet == null) continue;

                    SerializedProperty renderMode = new SerializedObject(candidate).FindProperty("m_RenderMode");
                    bool worldSpace = renderMode != null && renderMode.intValue == 1;
                    if (source == null || worldSpace) source = candidatePath;
                    if (worldSpace) break;
                }

                if (source == null)
                {
                    Debug.LogError("[Score] Aucun Panel Settings avec un thème dans le projet, impossible de créer celui du HUD. "
                                 + "Crée-en un (Assets > Create > UI Toolkit > Panel Settings Asset), puis relance "
                                 + "« Rage Room > Score > Installer ou mettre à jour le score ».");
                    return null;
                }
                if (!AssetDatabase.CopyAsset(source, path))
                {
                    Debug.LogError($"[Score] Copie de {source} vers {path} impossible.");
                    return null;
                }
                settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
                if (settings == null) return null;
                Debug.Log($"[Score] Panel Settings du HUD créé : {path} (copie de {source}).", settings);
            }

            // Ces réglages n'ont pas d'accès public : on écrit les champs sérialisés.
            var so = new SerializedObject(settings);
            bool ok = true;
            ok &= Set(so, "m_RenderMode", p => p.intValue = 1);            // World Space
            ok &= Set(so, "m_PixelsPerUnit", p => p.floatValue = PixelsPerUnit);
            ok &= Set(so, "m_ColliderUpdateMode", p => p.intValue = 1);    // Keep existing colliders : aucun collider créé
            Set(so, "m_TargetTexture", p => p.objectReferenceValue = null);
            so.ApplyModifiedPropertiesWithoutUndo();
            if (!ok)
                Debug.LogWarning($"[Score] Réglages à vérifier à la main sur {path} : Render Mode « World Space », "
                               + $"Pixels Per Unit {PixelsPerUnit:0}, Collider Update Mode « Keep existing colliders ».", settings);
            return settings;
        }

        static bool Set(SerializedObject so, string propertyName, System.Action<SerializedProperty> assign)
        {
            SerializedProperty property = so.FindProperty(propertyName);
            if (property == null) return false;
            assign(property);
            return true;
        }

        // ------------------------------------------------------------------ Recherche dans le projet

        static ScoreTable FindTable()
        {
            ScoreTable fallback = null;
            foreach (string guid in AssetDatabase.FindAssets("t:ScoreTable"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var table = AssetDatabase.LoadAssetAtPath<ScoreTable>(path);
                if (table == null) continue;
                if (path.Contains("/Resources/") && Path.GetFileNameWithoutExtension(path) == ScoreTable.ResourceName) return table;
                if (fallback == null) fallback = table;
            }
            return fallback;
        }

        /// <summary>Asset d'un type donné dont le fichier porte exactement ce nom (sans extension), où qu'il soit rangé.</summary>
        static T FindAsset<T>(string fileName) where T : Object
        {
            foreach (string guid in AssetDatabase.FindAssets(fileName + " t:" + typeof(T).Name))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) != fileName) continue;
                var asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (asset != null) return asset;
            }
            return null;
        }

        /// <summary>Dossier qui contient ScoreManager.cs : le barème et le Panel Settings du HUD sont rangés à côté.</summary>
        static string ScriptFolder()
        {
            foreach (string guid in AssetDatabase.FindAssets("ScoreManager t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(path) != "ScoreManager.cs") continue;
                return Path.GetDirectoryName(path).Replace('\\', '/');
            }
            return null;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
