using System;
using System.Collections.Generic;
using UnityEngine;

namespace RageRoom
{
    /// <summary>
    /// La radio du jeu : joue les morceaux de la radio choisie, l'un après l'autre, dans toutes les scènes.
    /// Créée automatiquement au lancement : rien à placer dans les scènes.
    ///
    /// Les radios sont les assets RadioStation du dossier « Resources/Radios » (un asset par radio).
    /// Le choix du joueur (radio, volume) est gardé d'une partie à l'autre.
    /// Le volume général du jeu s'applique aussi à la musique.
    ///
    /// Pour piloter la radio depuis un autre script : RadioPlayer.Instance.NextTrack(), .NextStation(), .Volume…
    /// </summary>
    public class RadioPlayer : MonoBehaviour
    {
        const string VolumeKey = "Radio.Volume";
        const string StationKey = "Radio.Station";
        /// <summary>Valeur enregistrée quand le joueur a coupé la radio.</summary>
        const string OffValue = "-";

        public static RadioPlayer Instance { get; private set; }

        /// <summary>Radio, morceau ou volume ont changé : le menu se met à jour là-dessus.</summary>
        public static event Action Changed;
        /// <summary>Un nouveau morceau démarre : le HUD l'annonce.</summary>
        public static event Action<RadioStation, RadioTrack> TrackStarted;

        readonly List<RadioStation> stations = new List<RadioStation>();
        readonly Dictionary<RadioStation, RadioPlaylist> playlists = new Dictionary<RadioStation, RadioPlaylist>();

        RadioConfig config;
        AudioSource source;
        int stationIndex = -1;          // -1 = radio coupée
        RadioTrack track;
        float volume;
        float startedAt;
        bool suspended;
        float resumeGuardUntil;

        // ------------------------------------------------------------------ Démarrage

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Utile si le rechargement de domaine est désactivé dans l'éditeur
            Instance = null;
            Changed = null;
            TrackStarted = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Instance != null) return;
            var go = new GameObject("Radio (auto)");
            DontDestroyOnLoad(go);
            go.AddComponent<RadioPlayer>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            config = Resources.Load<RadioConfig>(RadioConfig.ResourceName);
            bool hasConfig = config != null;
            if (!hasConfig) config = ScriptableObject.CreateInstance<RadioConfig>();

            // Musique « dans la tête » : même volume partout dans la salle
            source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.dopplerLevel = 0f;
            source.bypassReverbZones = true;
            source.priority = 0;

            foreach (RadioStation station in Resources.LoadAll<RadioStation>(RadioConfig.StationsFolder))
                if (station != null) stations.Add(station);
            stations.Sort((a, b) => a.order != b.order
                ? a.order.CompareTo(b.order)
                : string.Compare(a.stationName, b.stationName, StringComparison.OrdinalIgnoreCase));

            volume = Mathf.Clamp01(PlayerPrefs.GetFloat(VolumeKey, config.defaultVolume));

            if (stations.Count == 0)
            {
                Debug.Log("[Radio] Aucune radio : crée-en une avec « Rage Room > Radio > Créer une radio » "
                        + $"(un asset RadioStation dans un dossier Resources/{RadioConfig.StationsFolder}).");
            }
            else if (!hasConfig)
            {
                Debug.LogWarning("[Radio] Réglages introuvables : valeurs par défaut utilisées, sans annonce dans le HUD. "
                               + "Lance « Rage Room > Radio > Installer ou mettre à jour la radio ».");
            }

            if (hasConfig && config.showHud && config.hudUxml != null && config.hudPanelSettings != null)
            {
                var hud = new GameObject("Radio HUD (auto)");
                hud.transform.SetParent(transform, false);
                hud.AddComponent<RadioHud>().Init(config);
            }
        }

        void Start()
        {
            if (Instance != this) return;

            // Dernière radio choisie par le joueur ; à la première partie, la première de la liste
            string saved = PlayerPrefs.GetString(StationKey, "");
            int index = -1;
            if (saved != OffValue)
            {
                index = stations.FindIndex(s => s.name == saved);
                if (index < 0 && stations.Count > 0) index = 0;
            }
            if (!config.playOnStart) index = -1;
            SetStation(index, false);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ État

        /// <summary>Toutes les radios du jeu, dans l'ordre du menu.</summary>
        public IList<RadioStation> Stations { get { return stations; } }

        /// <summary>Radio en cours, ou null si la radio est coupée.</summary>
        public RadioStation Station { get { return stationIndex >= 0 && stationIndex < stations.Count ? stations[stationIndex] : null; } }

        /// <summary>Morceau en cours, ou null.</summary>
        public RadioTrack Track { get { return track; } }

        public RadioConfig Config { get { return config; } }

        /// <summary>Volume de la musique (0 à 1), enregistré.</summary>
        public float Volume
        {
            get { return volume; }
            set
            {
                value = Mathf.Clamp01(value);
                if (Mathf.Approximately(value, volume)) return;
                volume = value;
                PlayerPrefs.SetFloat(VolumeKey, volume);
                ApplyVolume();
                if (Changed != null) Changed();
            }
        }

        // ------------------------------------------------------------------ Commandes

        /// <summary>Radio suivante. Après la dernière vient « radio coupée », puis la première.</summary>
        public void NextStation() { StepStation(1); }

        /// <summary>Radio précédente.</summary>
        public void PreviousStation() { StepStation(-1); }

        void StepStation(int step)
        {
            // Positions possibles : -1 (coupée), puis 0 à n - 1
            int slots = stations.Count + 1;
            int slot = ((stationIndex + 1 + step) % slots + slots) % slots;
            SetStation(slot - 1, true);
        }

        /// <summary>Choisit une radio par sa place dans la liste. -1 coupe la radio.</summary>
        public void SetStation(int index, bool save = true)
        {
            if (index < -1 || index >= stations.Count) index = -1;
            stationIndex = index;

            if (save)
            {
                PlayerPrefs.SetString(StationKey, Station != null ? Station.name : OffValue);
                PlayerPrefs.Save();
            }

            if (Station == null)
            {
                source.Stop();
                source.clip = null;
                track = null;
                if (Changed != null) Changed();
                return;
            }
            Advance(true);
        }

        /// <summary>Morceau suivant de la radio en cours.</summary>
        public void NextTrack()
        {
            if (Station != null) Advance(true);
        }

        /// <summary>
        /// Morceau précédent. Comme sur un lecteur : si le morceau joue depuis plus de 3 secondes,
        /// il reprend d'abord au début.
        /// </summary>
        public void PreviousTrack()
        {
            if (Station == null) return;
            if (track != null && track.clip != null && source.isPlaying && source.time > 3f)
            {
                source.time = 0f;
                startedAt = Time.unscaledTime;
                if (Changed != null) Changed();
                return;
            }
            Advance(false);
        }

        // ------------------------------------------------------------------ Lecture

        RadioPlaylist PlaylistOf(RadioStation station)
        {
            RadioPlaylist playlist;
            if (!playlists.TryGetValue(station, out playlist) || playlist.Count != station.tracks.Count)
            {
                playlist = new RadioPlaylist(station.tracks.Count, station.shuffle, Environment.TickCount ^ station.GetInstanceID());
                playlists[station] = playlist;
            }
            return playlist;
        }

        /// <summary>Lance le morceau suivant (ou précédent) de la radio en cours, en sautant ceux qui n'ont pas de son.</summary>
        void Advance(bool forward)
        {
            RadioStation station = Station;
            if (station == null) return;

            RadioPlaylist playlist = PlaylistOf(station);
            RadioTrack next = null;
            for (int tries = 0; tries < playlist.Count && next == null; tries++)
            {
                int i = forward ? playlist.Next() : playlist.Previous();
                if (i >= 0 && i < station.tracks.Count && station.tracks[i] != null && station.tracks[i].clip != null)
                    next = station.tracks[i];
            }

            track = next;
            if (track == null)
            {
                source.Stop();
                source.clip = null;
                Debug.LogWarning($"[Radio] « {station.stationName} » n'a aucun morceau jouable : ajoute des morceaux dans son asset.", station);
                if (Changed != null) Changed();
                return;
            }

            source.Stop();
            source.clip = track.clip;
            source.time = 0f;
            ApplyVolume();
            source.Play();
            startedAt = Time.unscaledTime;

            if (TrackStarted != null) TrackStarted(station, track);
            if (Changed != null) Changed();
        }

        void ApplyVolume()
        {
            source.volume = volume * (track != null ? Mathf.Clamp01(track.volume) : 1f);
        }

        void Update()
        {
            if (Instance != this || track == null || suspended) return;
            float now = Time.unscaledTime;
            if (now < resumeGuardUntil || now - startedAt < 0.5f) return;

            // Morceau terminé : le lecteur s'est arrêté tout seul, revenu au début (ou resté à la fin).
            // En pause (casque retiré), il est arrêté aussi, mais au milieu du morceau : on ne saute pas.
            if (source.isPlaying) return;
            AudioClip clip = source.clip;
            bool ended = clip == null || source.timeSamples <= 0 || source.timeSamples >= clip.samples - 1;
            if (ended) Advance(true);
        }

        void OnApplicationPause(bool paused) { Suspend(paused); }

        void OnApplicationFocus(bool focused) { Suspend(!focused); }

        void Suspend(bool value)
        {
            suspended = value;
            if (!value) resumeGuardUntil = Time.unscaledTime + 0.5f;
        }
    }
}
