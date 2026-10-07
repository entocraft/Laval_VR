using System.IO;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace RageRoom.EditorTools
{
    /// <summary>
    /// Installe le catalogue (panneau + spawner + presenter) dans la scène ouverte.
    /// S'exécute tout seul une fois après compilation si la scène a un XR Origin mais pas encore de panneau.
    /// </summary>
    [InitializeOnLoad]
    static class RageRoomSetup
    {
        const string CatalogPath = "Assets/RageRoom/Data/SpawnCatalog.asset";
        const string AutoInstallKey = "RageRoom.AutoInstallChecked";

        static RageRoomSetup()
        {
            EditorApplication.delayCall += AutoInstall;
        }

        const string AutoZoneKey = "RageRoom.AutoSecondZoneChecked";
        const string AutoFloorKey = "RageRoom.AutoFloorChecked";

        static void AutoInstall()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            // Joueur enfoncé dans le sol de la salle → capsule bloquée / caméra qui saute
            if (!SessionState.GetBool(AutoFloorKey, false))
            {
                SessionState.SetBool(AutoFloorKey, true);
                SnapPlayerToFloor(false);
            }

            // 2e zone d'apparition : ajoutée une fois si le spawner n'en a pas encore
            if (!SessionState.GetBool(AutoZoneKey, false))
            {
                SessionState.SetBool(AutoZoneKey, true);
                var existing = Object.FindFirstObjectByType<ObjectSpawner>();
                if (existing != null && existing.ExtraZoneCount == 0)
                {
                    Debug.Log("[Rage Room] Ajout automatique d'une 2e zone d'apparition. Pense à sauvegarder (Ctrl+S).");
                    AddSpawnZone();
                }
            }

            if (SessionState.GetBool(AutoInstallKey, false)) return;
            SessionState.SetBool(AutoInstallKey, true);

            if (Object.FindFirstObjectByType<OrderPanelPresenter>(FindObjectsInactive.Include) != null) return;
            if (Object.FindFirstObjectByType<XROrigin>() == null) return;

            Debug.Log("[Rage Room] Installation automatique du catalogue dans la scène. Pense à sauvegarder (Ctrl+S).");
            Install(false);
        }

        [MenuItem("Rage Room/Installer le catalogue dans la scène")]
        static void InstallFromMenu() => Install(true);

        [MenuItem("Rage Room/Reconstruire le panneau (après modif du catalogue)")]
        static void RebuildPanels()
        {
            var panels = Object.FindObjectsByType<OrderPanel>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var p in panels) p.BuildUI();
            Debug.Log($"[Rage Room] {panels.Length} panneau(x) reconstruit(s).");
        }

        static void Install(bool interactive)
        {
            var catalog = GetOrCreateCatalog();

            if (interactive && Object.FindFirstObjectByType<OrderPanelPresenter>(FindObjectsInactive.Include) != null &&
                !EditorUtility.DisplayDialog("Rage Room", "Un panneau de commande existe déjà dans la scène. En créer un autre ?", "Oui", "Annuler"))
                return;

            var origin = Object.FindFirstObjectByType<XROrigin>();
            Transform head = origin != null && origin.Camera != null ? origin.Camera.transform : Camera.main != null ? Camera.main.transform : null;
            Transform right = origin != null ? FindDeep(origin.transform, "Right Controller") : null;

            var root = new GameObject("Rage Room - Catalogue");
            Undo.RegisterCreatedObjectUndo(root, "Installer le catalogue Rage Room");
            SceneManager.MoveGameObjectToScene(root, SceneManager.GetActiveScene());

            Vector3 forward = origin != null ? Vector3.ProjectOnPlane(origin.transform.forward, Vector3.up).normalized : Vector3.forward;
            if (forward == Vector3.zero) forward = Vector3.forward;
            Vector3 basePos = origin != null ? origin.transform.position : Vector3.zero;

            // Zone de spawn : 2 m devant le joueur, 1.2 m au-dessus du sol
            var spawnGo = new GameObject("Spawn Zone");
            spawnGo.transform.SetParent(root.transform, false);
            spawnGo.transform.SetPositionAndRotation(basePos + forward * 2f + Vector3.up * 1.2f, Quaternion.LookRotation(forward, Vector3.up));
            var spawner = spawnGo.AddComponent<ObjectSpawner>();
            SnapAboveFloor(spawnGo.transform, basePos.y);

            // Panneau : visible dans la scène à 60 cm devant le joueur, à hauteur des yeux
            var panelGo = new GameObject("Order Panel", typeof(RectTransform));
            panelGo.transform.SetParent(root.transform, false);
            panelGo.transform.SetPositionAndRotation(basePos + forward * 0.6f + Vector3.up * 1.4f, Quaternion.LookRotation(forward, Vector3.up));
            var panel = panelGo.AddComponent<OrderPanel>();
            if (head != null) panelGo.GetComponent<Canvas>().worldCamera = head.GetComponent<Camera>();

            var panelSo = new SerializedObject(panel);
            panelSo.FindProperty("catalog").objectReferenceValue = catalog;
            panelSo.FindProperty("spawner").objectReferenceValue = spawner;
            panelSo.FindProperty("roundedSprite").objectReferenceValue =
                AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            panelSo.ApplyModifiedPropertiesWithoutUndo();
            panel.BuildUI();

            // Presenter (bouton B / paume)
            var presenterGo = new GameObject("Order Panel Presenter");
            presenterGo.transform.SetParent(root.transform, false);
            var presenter = presenterGo.AddComponent<OrderPanelPresenter>();
            var presSo = new SerializedObject(presenter);
            presSo.FindProperty("panel").objectReferenceValue = panelGo.transform;
            presSo.FindProperty("head").objectReferenceValue = head;
            presSo.FindProperty("pinnedController").objectReferenceValue = right;
            presSo.ApplyModifiedPropertiesWithoutUndo();

            Selection.activeGameObject = panelGo;
            EditorSceneManager.MarkSceneDirty(root.scene);

            var missing = "";
            if (origin == null) missing += "\n- XR Origin introuvable";
            if (right == null) missing += "\n- « Right Controller » introuvable";
            if (Object.FindFirstObjectByType<XRUIInputModule>() == null) missing += "\n- Aucun EventSystem avec XRUIInputModule (le panneau ne réagira pas)";

            Debug.Log("[Rage Room] Catalogue installé." + (missing.Length > 0 ? " À vérifier :" + missing : ""), root);
        }

        /// <summary>
        /// Crée une zone supplémentaire, de même taille, en miroir gauche/droite de la zone principale
        /// par rapport au joueur, posée sur la surface trouvée en dessous, et l'enregistre dans le spawner.
        /// </summary>
        [MenuItem("Rage Room/Ajouter une zone d'apparition")]
        static void AddSpawnZone()
        {
            var spawner = Object.FindFirstObjectByType<ObjectSpawner>();
            if (spawner == null) { Debug.LogWarning("[Rage Room] Aucun ObjectSpawner dans la scène : installe d'abord le catalogue."); return; }

            var main = spawner.transform;
            var origin = Object.FindFirstObjectByType<XROrigin>();
            Vector3 pos;
            Quaternion rot;
            if (origin != null)
            {
                // Miroir gauche/droite dans le repère du joueur
                var o = origin.transform;
                var local = o.InverseTransformPoint(main.position);
                local.x = -local.x;
                pos = o.TransformPoint(local);
                var fwd = o.InverseTransformDirection(main.forward);
                fwd.x = -fwd.x;
                rot = Quaternion.LookRotation(o.TransformDirection(fwd), Vector3.up);
            }
            else
            {
                pos = main.position + main.right * 2f;
                rot = main.rotation;
            }
            if ((pos - main.position).sqrMagnitude < 0.25f) pos = main.position + main.right * 2f; // zone principale centrée : on décale

            int number = spawner.ExtraZoneCount + 2;
            var go = new GameObject($"Spawn Zone {number}");
            Undo.RegisterCreatedObjectUndo(go, "Ajouter une zone d'apparition");
            go.transform.SetParent(main.parent, false);
            go.transform.SetPositionAndRotation(pos, rot);
            go.transform.localScale = main.localScale;
            var zone = go.AddComponent<SpawnZone>();
            zone.areaSize = spawner.AreaSize;

            // Pose le bas de la zone sur la surface en dessous (table ou sol)
            Physics.SyncTransforms();
            float halfHeight = zone.WorldSize.y * 0.5f;
            if (Physics.Raycast(pos + Vector3.up * halfHeight, Vector3.down, out var hit, 5f, ~0, QueryTriggerInteraction.Ignore))
            {
                go.transform.position = new Vector3(pos.x, hit.point.y + halfHeight + 0.01f, pos.z);
                Debug.Log($"[Rage Room] {go.name} posée sur « {hit.collider.name} ».", go);
            }

            Undo.RecordObject(spawner, "Ajouter une zone d'apparition");
            spawner.AddZone(zone);
            EditorUtility.SetDirty(spawner);
            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;
            Debug.Log($"[Rage Room] {go.name} ajoutée au spawner. Vérifie sa position sur la 2e table.", go);
        }

        [MenuItem("Rage Room/Poser le joueur (XR Origin) sur le sol")]
        static void SnapPlayerToFloorFromMenu() => SnapPlayerToFloor(true);

        /// <summary>
        /// Place l'XR Origin exactement sur la surface trouvée sous la tête du joueur.
        /// En automatique, ne corrige que les petits écarts (moins de 50 cm) pour ne rien casser.
        /// </summary>
        static void SnapPlayerToFloor(bool fromMenu)
        {
            var origin = Object.FindFirstObjectByType<XROrigin>();
            if (origin == null) { if (fromMenu) Debug.LogWarning("[Rage Room] Aucun XR Origin dans la scène."); return; }

            Physics.SyncTransforms();
            var t = origin.transform;
            var start = t.position + Vector3.up * 1.6f;
            // DefaultRaycastLayers exclut « Ignore Raycast », le calque du corps du joueur
            if (!Physics.Raycast(start, Vector3.down, out var hit, 4f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                Debug.LogWarning("[Rage Room] Aucun sol trouvé sous le joueur (la salle a-t-elle des colliders ?).", origin);
                return;
            }

            float delta = hit.point.y - t.position.y;
            if (Mathf.Abs(delta) < 0.01f)
            {
                if (fromMenu) Debug.Log($"[Rage Room] Le joueur est déjà sur le sol (« {hit.collider.name} »).", origin);
                return;
            }
            if (!fromMenu && Mathf.Abs(delta) > 0.5f)
            {
                Debug.LogWarning($"[Rage Room] Le sol sous le joueur (« {hit.collider.name} ») est à {delta:+0.00;-0.00} m. " +
                                 "Écart trop grand pour une correction auto : utilise Rage Room > Poser le joueur sur le sol si c'est voulu.", origin);
                return;
            }

            Undo.RecordObject(t, "Poser le joueur sur le sol");
            t.position = new Vector3(t.position.x, hit.point.y, t.position.z);
            EditorSceneManager.MarkSceneDirty(origin.gameObject.scene);
            Debug.Log($"[Rage Room] XR Origin déplacé de {delta:+0.00;-0.00} m pour être posé sur « {hit.collider.name} ». Pense à sauvegarder (Ctrl+S).", origin);
        }

        [MenuItem("Rage Room/Recaler la zone de spawn au-dessus du sol")]
        static void SnapSpawnZone()
        {
            var spawner = Object.FindFirstObjectByType<ObjectSpawner>();
            if (spawner == null) { Debug.LogWarning("[Rage Room] Aucun ObjectSpawner dans la scène."); return; }

            var origin = Object.FindFirstObjectByType<XROrigin>();
            Undo.RecordObject(spawner.transform, "Recaler la zone de spawn");
            if (SnapAboveFloor(spawner.transform, origin != null ? origin.transform.position.y : 0f))
                EditorSceneManager.MarkSceneDirty(spawner.gameObject.scene);
            Selection.activeGameObject = spawner.gameObject;
        }

        /// <summary>
        /// Lance un rayon vers le bas depuis hauteur de tête pour trouver le sol de la salle
        /// (nécessite des colliders sur le modèle) et place la zone 1.2 m au-dessus.
        /// </summary>
        static bool SnapAboveFloor(Transform zone, float playerFloorY)
        {
            Physics.SyncTransforms();
            var p = zone.position;
            var start = new Vector3(p.x, playerFloorY + 1.7f, p.z);
            if (Physics.Raycast(start, Vector3.down, out var hit, 10f, ~0, QueryTriggerInteraction.Ignore))
            {
                zone.position = new Vector3(p.x, hit.point.y + 1.2f, p.z);
                Debug.Log($"[Rage Room] Zone de spawn posée 1.2 m au-dessus de « {hit.collider.name} ».", zone);
                return true;
            }
            Debug.LogWarning("[Rage Room] Pas de sol trouvé sous la zone de spawn : le modèle de la salle a-t-il des colliders ? Place la zone à la main.", zone);
            return false;
        }

        static SpawnCatalog GetOrCreateCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<SpawnCatalog>(CatalogPath);
            if (catalog != null) return catalog;

            Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath));
            catalog = ScriptableObject.CreateInstance<SpawnCatalog>();
            catalog.FillDefaults();
            AssetDatabase.CreateAsset(catalog, CatalogPath);
            AssetDatabase.SaveAssets();
            return catalog;
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
