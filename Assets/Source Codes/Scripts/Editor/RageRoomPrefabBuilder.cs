using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace RageRoom.EditorTools
{
    /// <summary>
    /// Crée un prefab cassable/attrapable par modèle 3D (taille réaliste, colliders, Rigidbody,
    /// XRGrabInteractable) et les range dans les variantes du catalogue.
    /// Se lance tout seul une fois si Assets/RageRoom/Prefabs n'existe pas encore.
    /// </summary>
    [InitializeOnLoad]
    static class RageRoomPrefabBuilder
    {
        const string SourceFolder = "Assets/Scenes/3D/sources/";
        const string PrefabFolder = "Assets/RageRoom/Prefabs";
        const string CatalogPath = "Assets/RageRoom/Data/SpawnCatalog.asset";

        struct ModelSpec
        {
            public string entry;      // nom de l'article dans le catalogue
            public string file;       // fichier dans SourceFolder
            public string prefabName;
            public float size;        // plus grande dimension visée, en mètres
            public bool splitParts;   // un prefab par mesh (ex. set de vases)

            public ModelSpec(string entry, string file, string prefabName, float size, bool splitParts = false)
            {
                this.entry = entry; this.file = file; this.prefabName = prefabName; this.size = size; this.splitParts = splitParts;
            }
        }

        static readonly ModelSpec[] Specs =
        {
            new ModelSpec("Caisse en bois", "wood box.fbx",              "Caisse",        0.5f),
            new ModelSpec("Brique",         "bricklowpoly.obj",          "Brique",        0.22f),
            new ModelSpec("Télé",           "Retro TV.fbx",              "Tele Retro",    0.55f),
            new ModelSpec("Imprimante",     "LowPoly Printer.fbx",       "Imprimante",    0.45f),
            new ModelSpec("Miroir",         "Espejo Cuerpo Entero.fbx",  "Miroir",        1.6f),
            new ModelSpec("Vase",           "vases.fbx",                 "Vase",          0.4f, splitParts: true),
            new ModelSpec("Cadre photo",    "Lord Ganesha Frame.fbx",    "Cadre",         0.5f),
            new ModelSpec("Statue",         "moai-statue.fbx",           "Statue Moai",   1.2f),
            new ModelSpec("Statue",         "alts_Statue 02_lod_03.fbx", "Statue Grecque",1.2f),
            new ModelSpec("Vaisselle",      "plate.fbx",                 "Assiette",      0.26f),
            new ModelSpec("Vaisselle",      "cup.fbx",                   "Tasse",         0.1f),
            new ModelSpec("Vaisselle",      "Copo.fbx",                  "Verre",         0.14f),
            new ModelSpec("Vaisselle",      "bowl.fbx",                  "Bol",           0.16f),
            new ModelSpec("Tonneau",        "lowpolybarrel.fbx",         "Tonneau",       0.9f),
        };

        static RageRoomPrefabBuilder()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                CleanExistingPrefabs();
                if (AssetDatabase.IsValidFolder(PrefabFolder)) return;
                if (AssetDatabase.LoadAssetAtPath<SpawnCatalog>(CatalogPath) == null) return;
                Debug.Log("[Rage Room] Génération automatique des prefabs du catalogue…");
                Build();
            };
        }

        [MenuItem("Rage Room/Générer les prefabs depuis les modèles 3D")]
        static void Build()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<SpawnCatalog>(CatalogPath);
            if (catalog == null) { Debug.LogError($"[Rage Room] Catalogue introuvable : {CatalogPath}"); return; }

            Directory.CreateDirectory(PrefabFolder);
            AssetDatabase.Refresh();

            var variantsByEntry = new Dictionary<string, List<GameObject>>();
            int created = 0;

            foreach (var spec in Specs)
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(SourceFolder + spec.file);
                if (model == null) { Debug.LogWarning($"[Rage Room] Modèle introuvable : {SourceFolder}{spec.file}"); continue; }

                var entry = catalog.entries.Find(e => e.displayName == spec.entry);
                float mass = entry != null ? entry.mass : 1f;

                var parts = new List<GameObject>();
                if (spec.splitParts)
                {
                    foreach (var r in model.GetComponentsInChildren<MeshRenderer>(true))
                        parts.Add(r.gameObject);
                }
                if (parts.Count <= 1) parts = new List<GameObject> { model };

                for (int i = 0; i < parts.Count; i++)
                {
                    string name = parts.Count > 1 ? $"{spec.prefabName} {i + 1}" : spec.prefabName;
                    var prefab = CreatePrefab(parts[i], name, spec.size, mass);
                    if (prefab == null) continue;
                    if (!variantsByEntry.TryGetValue(spec.entry, out var list))
                        variantsByEntry[spec.entry] = list = new List<GameObject>();
                    list.Add(prefab);
                    created++;
                }
            }

            foreach (var e in catalog.entries)
                if (variantsByEntry.TryGetValue(e.displayName, out var list))
                    e.variants = list.ToArray();

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            string unlinked = string.Join(", ", catalog.entries.FindAll(e => !variantsByEntry.ContainsKey(e.displayName)).ConvertAll(e => e.displayName));
            Debug.Log($"[Rage Room] {created} prefab(s) créés dans {PrefabFolder} et liés au catalogue." +
                      (unlinked.Length > 0 ? $" Sans modèle (cube temporaire) : {unlinked}." : ""), catalog);
        }

        /// <summary>Retire caméras, lumières et AudioListeners des prefabs déjà générés (sans toucher au reste).</summary>
        [MenuItem("Rage Room/Nettoyer les prefabs (caméras, lumières)")]
        static void CleanExistingPrefabs()
        {
            if (!AssetDatabase.IsValidFolder(PrefabFolder)) return;
            int cleaned = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { PrefabFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset.GetComponentsInChildren<Camera>(true).Length == 0 &&
                    asset.GetComponentsInChildren<Light>(true).Length == 0 &&
                    asset.GetComponentsInChildren<AudioListener>(true).Length == 0)
                    continue;

                var contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    ObjectSpawner.StripSceneComponents(contents);
                    PrefabUtility.SaveAsPrefabAsset(contents, path);
                    cleaned++;
                    Debug.Log($"[Rage Room] Caméra/lumière parasite retirée de {path}");
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }
            if (cleaned > 0) AssetDatabase.SaveAssets();
        }

        static GameObject CreatePrefab(GameObject source, string name, float targetSize, float mass)
        {
            var root = new GameObject(name);
            try
            {
                // Copie du modèle (ou d'une seule pièce), sans garder la position d'origine dans le set
                var visual = Object.Instantiate(source);
                visual.name = "Visuel";
                visual.transform.SetParent(root.transform, false);
                visual.transform.localPosition = Vector3.zero;
                if (source.transform.parent != null)
                    visual.transform.localRotation = source.transform.rotation;

                ObjectSpawner.StripSceneComponents(visual);

                var renderers = visual.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0)
                {
                    Debug.LogWarning($"[Rage Room] « {name} » n'a aucun mesh, ignoré.");
                    return null;
                }

                // Mise à l'échelle : la plus grande dimension = targetSize
                var bounds = WorldBounds(renderers);
                float maxDim = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
                if (maxDim > 0.0001f)
                    visual.transform.localScale *= targetSize / maxDim;

                // Recentre le visuel sur l'origine du prefab
                bounds = WorldBounds(renderers);
                visual.transform.position -= bounds.center;

                // Colliders convexes (requis pour un Rigidbody dynamique)
                foreach (var col in visual.GetComponentsInChildren<Collider>(true))
                    Object.DestroyImmediate(col);
                foreach (var mf in visual.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.sharedMesh == null) continue;
                    var mc = mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                    mc.convex = true;
                }

                var rb = root.AddComponent<Rigidbody>();
                rb.mass = mass;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                root.AddComponent<XRGrabInteractable>().useDynamicAttach = true;

                string path = $"{PrefabFolder}/{name}.prefab";
                return PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        static Bounds WorldBounds(Renderer[] renderers)
        {
            var b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            return b;
        }
    }
}
