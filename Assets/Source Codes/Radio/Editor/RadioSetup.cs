using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace RageRoom.EditorTools
{
    /// <summary>
    /// Menu « Rage Room > Radio » :
    ///
    ///  - « Installer ou mettre à jour la radio » : crée les réglages (RadioConfig.asset, dans un dossier Resources
    ///    à côté des scripts), le Panel Settings de l'annonce du HUD, relie RadioHud.uxml et la police du shop,
    ///    et crée une première radio vide s'il n'y en a aucune. Rien de ce qui existe n'est écrasé.
    ///    Lancé tout seul à la première importation.
    ///  - « Créer une radio » : ajoute un asset RadioStation dans « Resources/Radios ». Un asset = une radio.
    ///  - « Ajouter un dossier de musiques à la radio… » : remplit la radio sélectionnée avec les fichiers audio
    ///    d'un dossier (et de ses sous-dossiers). Pour chaque fichier : artiste et titre lus dans le nom
    ///    (« Artiste - Titre »), album = nom du dossier, pochette = l'image du dossier.
    ///  - « Ouvrir les réglages » : sélectionne RadioConfig pour le régler dans l'Inspector.
    ///
    /// Ce fichier doit rester dans un dossier nommé « Editor ».
    /// </summary>
    [InitializeOnLoad]
    static class RadioSetup
    {
        const string PanelSettingsFile = "RadioHudPanelSettings.asset";
        const string HudUxmlName = "RadioHud";
        const string HudFontName = "BarlowCondensed-ExtraBoldItalic";
        const string AutoKey = "RageRoom.RadioSetup.Auto";

        /// <summary>1000 pixels par mètre : 1 pixel de l'annonce = 1 mm, comme le HUD de score et les menus.</summary>
        const float PixelsPerUnit = 1000f;

        static RadioSetup()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                if (FindConfig() != null || SessionState.GetBool(AutoKey, false)) return;
                SessionState.SetBool(AutoKey, true);
                Install();
            };
        }

        // ------------------------------------------------------------------ Installation

        [MenuItem("Rage Room/Radio/Installer ou mettre à jour la radio")]
        static void Install()
        {
            string folder = ScriptFolder();
            if (folder == null)
            {
                Debug.LogError("[Radio] RadioPlayer.cs introuvable dans le projet : importe d'abord les scripts de la radio.");
                return;
            }

            // ---------- Réglages ----------
            RadioConfig config = FindConfig();
            if (config == null)
            {
                string resources = folder + "/Resources";
                EnsureFolder(resources);
                config = ScriptableObject.CreateInstance<RadioConfig>();
                AssetDatabase.CreateAsset(config, resources + "/" + RadioConfig.ResourceName + ".asset");
                Debug.Log($"[Radio] Réglages créés : {resources}/{RadioConfig.ResourceName}.asset", config);
            }
            else
            {
                string configPath = AssetDatabase.GetAssetPath(config);
                if (!configPath.Contains("/Resources/") || Path.GetFileNameWithoutExtension(configPath) != RadioConfig.ResourceName)
                    Debug.LogWarning($"[Radio] Les réglages doivent s'appeler « {RadioConfig.ResourceName} » et se trouver dans un dossier Resources "
                                   + $"pour être chargés en jeu. Ils sont ici : {configPath}", config);
            }

            // ---------- Annonce du HUD ----------
            if (config.hudUxml == null) config.hudUxml = FindAsset<VisualTreeAsset>(HudUxmlName);
            if (config.hudUxml == null)
                Debug.LogError("[Radio] RadioHud.uxml introuvable : importe RadioHud.uxml et RadioHud.uss dans un même dossier, "
                             + "puis relance « Rage Room > Radio > Installer ou mettre à jour la radio ».");
            if (config.hudFont == null) config.hudFont = FindAsset<Font>(HudFontName);
            if (config.hudPanelSettings == null) config.hudPanelSettings = EnsurePanelSettings(folder);

            EditorUtility.SetDirty(config);

            // ---------- Radios ----------
            List<RadioStation> stations = FindStations();
            if (stations.Count == 0)
            {
                RadioStation first = NewStation(folder, "Radio 1");
                stations.Add(first);
                Debug.Log($"[Radio] Première radio créée : {AssetDatabase.GetAssetPath(first)}. Remplis-la avec "
                        + "« Rage Room > Radio > Ajouter un dossier de musiques à la radio… ».", first);
            }
            int tracks = 0;
            foreach (RadioStation station in stations)
            {
                tracks += station.tracks.Count;
                string path = AssetDatabase.GetAssetPath(station);
                if (!path.Contains("/Resources/" + RadioConfig.StationsFolder + "/"))
                    Debug.LogWarning($"[Radio] « {station.name} » n'est pas dans un dossier Resources/{RadioConfig.StationsFolder} : "
                                   + $"le jeu ne la verra pas. Elle est ici : {path}", station);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[Radio] Installation terminée : {stations.Count} radio(s), {tracks} morceau(x). "
                    + "Réglages dans « Rage Room > Radio > Ouvrir les réglages ».", config);
            if (config.hudFont == null)
                Debug.Log($"[Radio] Police « {HudFontName} » introuvable : l'annonce utilise la police par défaut. "
                        + "Tu peux glisser une police dans le champ Hud Font des réglages.", config);
        }

        [MenuItem("Rage Room/Radio/Ouvrir les réglages")]
        static void OpenConfig()
        {
            RadioConfig config = FindConfig();
            if (config == null)
            {
                Debug.LogWarning("[Radio] Aucun réglage : lance d'abord « Rage Room > Radio > Installer ou mettre à jour la radio ».");
                return;
            }
            Selection.activeObject = config;
            EditorGUIUtility.PingObject(config);
        }

        // ------------------------------------------------------------------ Radios

        [MenuItem("Rage Room/Radio/Créer une radio")]
        static void CreateStation()
        {
            string folder = ScriptFolder();
            if (folder == null)
            {
                Debug.LogError("[Radio] RadioPlayer.cs introuvable dans le projet : importe d'abord les scripts de la radio.");
                return;
            }
            RadioStation station = NewStation(folder, "Radio " + (FindStations().Count + 1));
            AssetDatabase.SaveAssets();
            Selection.activeObject = station;
            EditorGUIUtility.PingObject(station);
            Debug.Log($"[Radio] Radio créée : {AssetDatabase.GetAssetPath(station)}. Donne-lui un nom (Station Name), puis ajoute ses morceaux.", station);
        }

        static RadioStation NewStation(string folder, string assetName)
        {
            string stationsFolder = folder + "/Resources/" + RadioConfig.StationsFolder;
            EnsureFolder(stationsFolder);
            var station = ScriptableObject.CreateInstance<RadioStation>();
            station.stationName = assetName;
            station.order = FindStations().Count;
            AssetDatabase.CreateAsset(station, AssetDatabase.GenerateUniqueAssetPath(stationsFolder + "/" + assetName + ".asset"));
            return station;
        }

        [MenuItem("Rage Room/Radio/Ajouter un dossier de musiques à la radio…")]
        static void AddFolder()
        {
            // Radio visée : celle qui est sélectionnée, sinon la seule du projet
            RadioStation station = Selection.activeObject as RadioStation;
            if (station == null)
            {
                List<RadioStation> stations = FindStations();
                if (stations.Count == 1) station = stations[0];
                else
                {
                    EditorUtility.DisplayDialog("Rage Room — Radio",
                        stations.Count == 0
                            ? "Aucune radio dans le projet.\n\nLance d'abord Rage Room > Radio > Créer une radio."
                            : "Il y a plusieurs radios.\n\nSélectionne dans la fenêtre Project celle à remplir, puis relance ce menu.",
                        "OK");
                    return;
                }
            }

            string picked = EditorUtility.OpenFolderPanel($"Dossier de musiques pour « {station.stationName} »", Application.dataPath, "");
            if (string.IsNullOrEmpty(picked)) return;

            string musicFolder = ToProjectPath(picked);
            if (musicFolder == null)
            {
                EditorUtility.DisplayDialog("Rage Room — Radio",
                    "Ce dossier n'est pas dans le projet.\n\nCopie d'abord tes musiques dans le dossier Assets (par exemple Assets/Music/Nom de l'album), "
                  + "puis relance ce menu.", "OK");
                return;
            }

            var known = new HashSet<AudioClip>();
            foreach (RadioTrack t in station.tracks)
                if (t != null && t.clip != null) known.Add(t.clip);

            var paths = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { musicFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!paths.Contains(path)) paths.Add(path);
            }
            paths.Sort(StringComparer.OrdinalIgnoreCase);

            Undo.RecordObject(station, "Ajouter des morceaux à la radio");
            int added = 0, skipped = 0, streaming = 0, withoutCover = 0;
            var covers = new Dictionary<string, Texture2D>();
            foreach (string path in paths)
            {
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip == null) continue;
                if (known.Contains(clip)) { skipped++; continue; }

                // Un morceau entier chargé d'un coup en mémoire pèse lourd sur un casque autonome : lecture en continu.
                if (SetStreaming(path)) streaming++;

                string directory = Path.GetDirectoryName(path).Replace('\\', '/');
                Texture2D cover;
                if (!covers.TryGetValue(directory, out cover))
                {
                    cover = FindCover(directory);
                    covers[directory] = cover;
                }
                if (cover == null) withoutCover++;

                string artist, title;
                RadioTrackName.Parse(Path.GetFileNameWithoutExtension(path), out artist, out title);
                station.tracks.Add(new RadioTrack
                {
                    clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path), // rechargé : l'import en continu recrée l'objet
                    title = title,
                    artist = artist,
                    album = Path.GetFileName(directory),
                    cover = cover
                });
                added++;
            }

            EditorUtility.SetDirty(station);
            AssetDatabase.SaveAssets();
            Selection.activeObject = station;
            EditorGUIUtility.PingObject(station);

            Debug.Log($"[Radio] « {station.stationName} » : {added} morceau(x) ajouté(s) depuis {musicFolder}, {skipped} déjà présent(s), "
                    + $"{streaming} fichier(s) passé(s) en lecture continue (Streaming). La radio a maintenant {station.tracks.Count} morceau(x). "
                    + "Vérifie les titres, artistes et albums dans l'Inspector.", station);
            if (withoutCover > 0)
                Debug.Log($"[Radio] {withoutCover} morceau(x) sans pochette : mets une image dans le dossier de l'album et glisse-la dans le champ Cover, "
                        + "ou règle la pochette par défaut de la radio (Default Cover).", station);
        }

        /// <summary>Passe un fichier audio en lecture continue. Renvoie true s'il a fallu le changer.</summary>
        static bool SetStreaming(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as AudioImporter;
            if (importer == null) return false;
            AudioImporterSampleSettings settings = importer.defaultSampleSettings;
            if (settings.loadType == AudioClipLoadType.Streaming) return false;
            settings.loadType = AudioClipLoadType.Streaming;
            importer.defaultSampleSettings = settings;
            importer.SaveAndReimport();
            return true;
        }

        /// <summary>Image du dossier d'un album : de préférence celle dont le nom évoque une pochette.</summary>
        static Texture2D FindCover(string directory)
        {
            Texture2D first = null;
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { directory }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetDirectoryName(path).Replace('\\', '/') != directory) continue; // pas les sous-dossiers
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (texture == null) continue;

                string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                if (name.Contains("cover") || name.Contains("front") || name.Contains("folder") || name.Contains("pochette") || name.Contains("album"))
                    return texture;
                if (first == null) first = texture;
            }
            return first;
        }

        // ------------------------------------------------------------------ Panel Settings de l'annonce

        /// <summary>
        /// Panel Settings réservé à l'annonce : affichage dans le monde, 1 pixel = 1 mm, et aucun collider
        /// (elle ne doit pas arrêter les rayons des manettes). Il part d'une copie d'un Panel Settings du projet
        /// pour garder son thème.
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
                    Debug.LogError("[Radio] Aucun Panel Settings avec un thème dans le projet, impossible de créer celui de l'annonce du HUD. "
                                 + "Crée-en un (Assets > Create > UI Toolkit > Panel Settings Asset), puis relance "
                                 + "« Rage Room > Radio > Installer ou mettre à jour la radio ».");
                    return null;
                }
                if (!AssetDatabase.CopyAsset(source, path))
                {
                    Debug.LogError($"[Radio] Copie de {source} vers {path} impossible.");
                    return null;
                }
                settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
                if (settings == null) return null;
                Debug.Log($"[Radio] Panel Settings de l'annonce créé : {path} (copie de {source}).", settings);
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
                Debug.LogWarning($"[Radio] Réglages à vérifier à la main sur {path} : Render Mode « World Space », "
                               + $"Pixels Per Unit {PixelsPerUnit:0}, Collider Update Mode « Keep existing colliders ».", settings);
            return settings;
        }

        static bool Set(SerializedObject so, string propertyName, Action<SerializedProperty> assign)
        {
            SerializedProperty property = so.FindProperty(propertyName);
            if (property == null) return false;
            assign(property);
            return true;
        }

        // ------------------------------------------------------------------ Recherche d'assets

        static RadioConfig FindConfig()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:RadioConfig"))
            {
                var config = AssetDatabase.LoadAssetAtPath<RadioConfig>(AssetDatabase.GUIDToAssetPath(guid));
                if (config != null) return config;
            }
            return null;
        }

        static List<RadioStation> FindStations()
        {
            var stations = new List<RadioStation>();
            foreach (string guid in AssetDatabase.FindAssets("t:RadioStation"))
            {
                var station = AssetDatabase.LoadAssetAtPath<RadioStation>(AssetDatabase.GUIDToAssetPath(guid));
                if (station != null && !stations.Contains(station)) stations.Add(station);
            }
            return stations;
        }

        /// <summary>Premier asset du projet qui porte exactement ce nom de fichier (sans extension).</summary>
        static T FindAsset<T>(string fileName) where T : UnityEngine.Object
        {
            foreach (string guid in AssetDatabase.FindAssets(fileName + " t:" + typeof(T).Name))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == fileName)
                    return AssetDatabase.LoadAssetAtPath<T>(path);
            }
            return null;
        }

        /// <summary>Dossier qui contient RadioPlayer.cs, pour ranger les assets créés à côté des scripts.</summary>
        static string ScriptFolder()
        {
            foreach (string guid in AssetDatabase.FindAssets("RadioPlayer t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(path) == "RadioPlayer.cs")
                    return Path.GetDirectoryName(path).Replace('\\', '/');
            }
            return null;
        }

        /// <summary>Crée un dossier du projet et ses parents s'ils manquent.</summary>
        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        /// <summary>Chemin « Assets/… » d'un dossier choisi sur le disque, ou null s'il est hors du projet.</summary>
        static string ToProjectPath(string absolute)
        {
            string full = absolute.Replace('\\', '/').TrimEnd('/');
            string data = Application.dataPath.Replace('\\', '/').TrimEnd('/');
            if (full == data) return "Assets";
            if (!full.StartsWith(data + "/", StringComparison.OrdinalIgnoreCase)) return null;
            return "Assets" + full.Substring(data.Length);
        }
    }
}
