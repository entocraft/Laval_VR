using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RageRoom
{
    /// <summary>
    /// Score de la salle : écoute les objets cassables et les armes, et confie le calcul à ScoreEngine
    /// d'après le barème (asset ScoreTable).
    ///
    /// Rien à placer dans les scènes : il se crée tout seul dans chaque scène qui contient des objets cassables,
    /// avec son HUD (réglage « Auto Create » du barème). Aucun autre script du projet n'est modifié : il se branche
    /// sur leurs événements (onBreak, onCrush, onExplode, onImpact, onFire, onHornStart...).
    ///
    /// Depuis un autre script :
    ///   ScoreManager.Instance.Score               score en cours
    ///   ScoreManager.Instance.ResetScore()        remise à zéro (nouvelle partie)
    ///   ScoreManager.Instance.AddBonus("Défi", 500)
    ///   ScoreManager.Instance.Engine.Scored += ...  pour réagir à chaque casse (son, vibration...)
    /// </summary>
    [DefaultExecutionOrder(1000)]
    [DisallowMultipleComponent]
    public class ScoreManager : MonoBehaviour
    {
        const string BestScoreKey = "RageRoom.BestScore";
        const float ScanInterval = 0.25f;

        public static ScoreManager Instance { get; private set; }

        [Tooltip("Barème utilisé. Vide : l'asset « ScoreTable » d'un dossier Resources.")]
        public ScoreTable table;
        [Tooltip("Affiche dans la console chaque casse comptée et chaque bonus.")]
        public bool debugLog;

        public ScoreEngine Engine { get; private set; }
        public ScoreTable Table => table;
        public int Score => Engine != null ? Engine.Score : 0;
        /// <summary>Meilleur score enregistré sur cet appareil.</summary>
        public int BestScore { get; private set; }

        /// <summary>Appelé après une remise à zéro du score.</summary>
        public event System.Action ScoreReset;

        static int nextId;
        /// <summary>Numéro unique pour un objet ou une arme suivis par le score.</summary>
        public static int NextId() => ++nextId;

        // ------------------------------------------------------------------ Sources suivies

        sealed class WeaponTrack
        {
            public WeaponGrab grab;
            public Rigidbody body;
            public Collider[] colliders;
            public int id;
            public bool wasHeld;
            public float releasedAt = -1f;
            public Vector3 releasePos;
        }

        sealed class GunTrack
        {
            public ScoreManager owner;
            public PelletGun gun;
            public int id;

            public void OnFire()
            {
                owner.Engine.RegisterShot(id, Time.time);
            }

            public void OnImpact(Vector3 point)
            {
                Transform muzzle = gun.muzzle != null ? gun.muzzle : gun.transform;
                owner.Engine.RegisterPelletHit(id, muzzle.position, point, Time.time, Time.frameCount);
            }
        }

        sealed class ExplosiveTrack
        {
            public ScoreManager owner;
            public RuntimeExplosion explosive;

            public void OnExplode(Vector3 center)
            {
                owner.Engine.RegisterExplosion(center, explosive.radius, Time.time, Time.frameCount);
                if (owner.debugLog) Debug.Log($"[Score] Explosion de '{explosive.name}' (rayon {explosive.radius:0.0} m)", owner);
            }
        }

        sealed class HornTrack
        {
            public TrainHorn horn;
            public bool sounding;
            public float stoppedAt = -100f;

            public void OnStart() { sounding = true; }
            public void OnStop() { sounding = false; stoppedAt = Time.time; }
        }

        readonly List<WeaponTrack> weapons = new List<WeaponTrack>();
        readonly List<GunTrack> guns = new List<GunTrack>();
        readonly List<ExplosiveTrack> explosives = new List<ExplosiveTrack>();
        readonly List<HornTrack> horns = new List<HornTrack>();
        readonly HashSet<Object> known = new HashSet<Object>();
        readonly List<ScoreValue> held = new List<ScoreValue>();
        readonly List<ScoreValue> thrown = new List<ScoreValue>();

        float nextScan;
        bool recordAnnounced;
        ScoreHud hud;

        // ------------------------------------------------------------------ Création automatique

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Instance = null;
            nextId = 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureInScene(SceneManager.GetActiveScene());
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            EnsureInScene(scene);
        }

        /// <summary>Crée le score dans la scène si elle contient de quoi casser et qu'il n'y est pas déjà.</summary>
        static void EnsureInScene(Scene scene)
        {
            if (Instance != null) return;
            if (FindAnyObjectByType<ScoreManager>(FindObjectsInactive.Include) != null) return;

            var scoreTable = Resources.Load<ScoreTable>(ScoreTable.ResourceName);
            if (scoreTable == null || !scoreTable.autoCreate) return;

            if (scoreTable.excludedScenes != null)
                foreach (string excluded in scoreTable.excludedScenes)
                    if (!string.IsNullOrEmpty(excluded) && string.Equals(excluded, scene.name, System.StringComparison.OrdinalIgnoreCase)) return;

            bool somethingToBreak = FindAnyObjectByType<RuntimeFracture>(FindObjectsInactive.Include) != null
                                 || FindAnyObjectByType<RuntimeCrush>(FindObjectsInactive.Include) != null
                                 || FindAnyObjectByType<ObjectSpawner>(FindObjectsInactive.Include) != null;
            if (!somethingToBreak) return;

            new GameObject("Score (auto)").AddComponent<ScoreManager>();
        }

        // ------------------------------------------------------------------ Cycle de vie

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[Score] Deux ScoreManager dans la scène : celui-ci est ignoré.", this);
                enabled = false;
                return;
            }
            Instance = this;

            if (table == null) table = Resources.Load<ScoreTable>(ScoreTable.ResourceName);
            if (table == null)
            {
                table = ScriptableObject.CreateInstance<ScoreTable>();
                Debug.LogWarning("[Score] Aucun barème trouvé : valeurs par défaut utilisées, sans HUD. "
                               + "Lance « Rage Room > Score > Installer ou mettre à jour le score ».", this);
            }

            BestScore = PlayerPrefs.GetInt(BestScoreKey, 0);

            Engine = new ScoreEngine(table) { probe = Probe };
            Engine.Scored += OnScored;
            Engine.Bonus += OnBonus;
            Engine.ComboEnded += OnComboEnded;
        }

        void Start()
        {
            if (Instance != this) return;
            RegisterSceneObjects();
            Scan();
            CreateHud();
        }

        void OnDestroy()
        {
            if (Instance != this) return;
            SaveBestScore();
            Instance = null;

            foreach (GunTrack g in guns)
                if (g.gun != null)
                {
                    g.gun.onFire.RemoveListener(g.OnFire);
                    g.gun.onImpact.RemoveListener(g.OnImpact);
                }
            foreach (ExplosiveTrack e in explosives)
                if (e.explosive != null) e.explosive.onExplode.RemoveListener(e.OnExplode);
            foreach (HornTrack h in horns)
                if (h.horn != null)
                {
                    h.horn.onHornStart.RemoveListener(h.OnStart);
                    h.horn.onHornStop.RemoveListener(h.OnStop);
                }
        }

        void Update()
        {
            if (Instance != this) return;

            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + ScanInterval;
                Scan();
            }

            // Armes : on repère le moment où elles quittent la main, pour reconnaître une arme lancée.
            for (int i = weapons.Count - 1; i >= 0; i--)
            {
                WeaponTrack w = weapons[i];
                if (w.grab == null)
                {
                    weapons.RemoveAt(i);
                    continue;
                }
                bool heldNow = w.grab.IsHeld;
                if (w.wasHeld && !heldNow)
                {
                    w.releasedAt = Time.time;
                    w.releasePos = w.body != null ? w.body.worldCenterOfMass : w.grab.transform.position;
                }
                w.wasHeld = heldNow;
            }

            for (int i = held.Count - 1; i >= 0; i--)
                if (held[i] == null || !held[i].IsHeld) held.RemoveAt(i);

            for (int i = thrown.Count - 1; i >= 0; i--)
            {
                ScoreValue v = thrown[i];
                if (v == null || v.IsHeld || Time.time - v.ReleasedAt > table.throwWindow) thrown.RemoveAt(i);
            }
        }

        void LateUpdate()
        {
            if (Instance != this) return;
            Engine.Tick(Time.time, Time.frameCount);
        }

        // ------------------------------------------------------------------ Découverte des objets et des armes

        /// <summary>
        /// Objets cassables déjà présents dans la scène et sans ScoreValue : ils en reçoivent un.
        /// Fait une seule fois, au lancement, quand rien n'est encore cassé : les fragments créés ensuite
        /// ne sont donc jamais concernés.
        /// </summary>
        void RegisterSceneObjects()
        {
            int added = 0;
            foreach (RuntimeFracture f in FindObjectsByType<RuntimeFracture>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (f.preset == RuntimeFracture.Preset.Outil) continue;
                if (TryAddValue(f.gameObject)) added++;
            }
            foreach (RuntimeCrush c in FindObjectsByType<RuntimeCrush>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (TryAddValue(c.gameObject)) added++;

            if (debugLog && added > 0) Debug.Log($"[Score] {added} objet(s) de la scène ajouté(s) au score.", this);
        }

        static bool TryAddValue(GameObject go)
        {
            if (go.GetComponent<ScoreValue>() != null) return false;
            if (go.GetComponent<WeaponGrab>() != null) return false; // une arme n'est pas une cible
            go.AddComponent<ScoreValue>();
            return true;
        }

        /// <summary>Repère les armes et les explosifs apparus depuis le dernier passage (objets commandés, par exemple).</summary>
        void Scan()
        {
            known.RemoveWhere(o => o == null);

            foreach (WeaponGrab grab in FindObjectsByType<WeaponGrab>(FindObjectsSortMode.None))
            {
                if (!known.Add(grab)) continue;
                weapons.Add(new WeaponTrack
                {
                    grab = grab,
                    body = grab.GetComponent<Rigidbody>(),
                    colliders = grab.GetComponentsInChildren<Collider>(),
                    id = NextId(),
                    wasHeld = grab.IsHeld
                });
            }

            foreach (PelletGun gun in FindObjectsByType<PelletGun>(FindObjectsSortMode.None))
            {
                if (!known.Add(gun)) continue;
                var track = new GunTrack { owner = this, gun = gun, id = NextId() };
                gun.onFire.AddListener(track.OnFire);
                gun.onImpact.AddListener(track.OnImpact);
                guns.Add(track);
            }

            foreach (RuntimeExplosion explosive in FindObjectsByType<RuntimeExplosion>(FindObjectsSortMode.None))
            {
                if (!known.Add(explosive)) continue;
                var track = new ExplosiveTrack { owner = this, explosive = explosive };
                explosive.onExplode.AddListener(track.OnExplode);
                explosives.Add(track);
            }

            foreach (TrainHorn horn in FindObjectsByType<TrainHorn>(FindObjectsSortMode.None))
            {
                if (!known.Add(horn)) continue;
                var track = new HornTrack { horn = horn };
                horn.onHornStart.AddListener(track.OnStart);
                horn.onHornStop.AddListener(track.OnStop);
                horns.Add(track);
            }

            guns.RemoveAll(g => g.gun == null);
            explosives.RemoveAll(e => e.explosive == null);
            horns.RemoveAll(h => h.horn == null);
        }

        // ------------------------------------------------------------------ Signalements des objets

        /// <summary>Appelé par ScoreValue quand son objet casse ou est écrasé.</summary>
        public void Report(ScoreValue value, Vector3 point, bool crushed)
        {
            if (Engine == null || value == null) return;
            Engine.ReportBreak(new BreakReport
            {
                objectId = value.Id,
                label = value.Label(table),
                basePoints = value.Points(table),
                point = point,
                crushed = crushed,
                wasHeld = value.IsHeld,
                releasedAt = value.ReleasedAt,
                releasePos = value.ReleasePosition,
                time = Time.time,
                frame = Time.frameCount
            });
        }

        /// <summary>Appelé par ScoreValue quand le joueur prend son objet en main.</summary>
        public void NotifyGrabbed(ScoreValue value)
        {
            thrown.Remove(value);
            if (!held.Contains(value)) held.Add(value);
        }

        /// <summary>Appelé par ScoreValue quand le joueur lâche son objet.</summary>
        public void NotifyReleased(ScoreValue value)
        {
            held.Remove(value);
            if (!thrown.Contains(value)) thrown.Add(value);
        }

        // ------------------------------------------------------------------ Contexte d'une casse

        /// <summary>Regarde ce qui se trouve autour d'une casse : klaxon, arme tenue, objet lancé.</summary>
        LiveContext Probe(BreakReport r)
        {
            var ctx = new LiveContext();

            // Klaxon qui sonne (ou vient de se taire), objet dans son cône de souffle.
            foreach (HornTrack h in horns)
            {
                if (h.horn == null || !h.horn.blastEnabled) continue;
                if (!h.sounding && Time.time - h.stoppedAt > 0.2f) continue;

                Transform mouth = h.horn.hornMouth != null ? h.horn.hornMouth : h.horn.transform;
                Vector3 toPoint = r.point - mouth.position;
                float distance = toPoint.magnitude;
                if (distance > h.horn.blastRange * 1.1f + 0.2f) continue;
                if (distance > 0.3f && Vector3.Angle(mouth.forward, toPoint) > h.horn.blastAngle + 10f) continue;
                ctx.inHornBlast = true;
                break;
            }

            // Arme ou objet tenu en main, tout près du point d'impact.
            float best = table.reach;
            foreach (WeaponTrack w in weapons)
            {
                if (w.grab == null || !w.grab.IsHeld) continue;
                float d = DistanceTo(w.colliders, r.point);
                if (d > best) continue;
                best = d;
                ctx.heldToolId = w.id;
            }
            foreach (ScoreValue v in held)
            {
                if (v == null || v.Id == r.objectId || !v.IsHeld) continue;
                float d = DistanceTo(v.Colliders, r.point);
                if (d > best) continue;
                best = d;
                ctx.heldToolId = v.Id;
            }

            // Arme ou objet lancé, encore en mouvement, tout près du point d'impact.
            best = table.reach + 0.1f;
            foreach (WeaponTrack w in weapons)
            {
                if (w.grab == null || w.grab.IsHeld || w.releasedAt < 0f) continue;
                if (Time.time - w.releasedAt > table.throwWindow || !IsMoving(w.body)) continue;
                float d = DistanceTo(w.colliders, r.point);
                if (d > best) continue;
                best = d;
                ctx.thrownId = w.id;
                ctx.thrownFrom = w.releasePos;
            }
            foreach (ScoreValue v in thrown)
            {
                if (v == null || v.Id == r.objectId || v.IsHeld || !IsMoving(v.Body)) continue;
                float d = DistanceTo(v.Colliders, r.point);
                if (d > best) continue;
                best = d;
                ctx.thrownId = v.Id;
                ctx.thrownFrom = v.ReleasePosition;
            }

            return ctx;
        }

        static bool IsMoving(Rigidbody body)
        {
            return body != null && !body.isKinematic && body.linearVelocity.sqrMagnitude > 0.25f;
        }

        /// <summary>Distance entre un point et le plus proche des colliders donnés.</summary>
        static float DistanceTo(Collider[] colliders, Vector3 point)
        {
            float best = float.PositiveInfinity;
            if (colliders == null) return best;

            foreach (Collider c in colliders)
            {
                if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;

                // ClosestPoint n'accepte pas les MeshCollider non convexes : on se rabat sur leur boîte englobante.
                var mesh = c as MeshCollider;
                Vector3 closest = mesh != null && !mesh.convex ? c.bounds.ClosestPoint(point) : c.ClosestPoint(point);
                float d = Vector3.Distance(closest, point);
                if (d < best) best = d;
            }
            return best;
        }

        // ------------------------------------------------------------------ Score

        /// <summary>Remet le score et le combo à zéro (nouvelle partie). Le record est conservé.</summary>
        public void ResetScore()
        {
            if (Engine == null) return;
            SaveBestScore();
            Engine.Reset();
            recordAnnounced = false;
            ScoreReset?.Invoke();
        }

        /// <summary>Ajoute des points depuis un autre script, avec une ligne dans le HUD.</summary>
        public void AddBonus(string label, int points)
        {
            if (Engine != null) Engine.AddBonus(label, points);
        }

        void OnScored(ScoreEvent e)
        {
            if (debugLog)
                Debug.Log($"[Score] {e.label} — {e.causeLabel} : {e.basePoints} x{e.multiplier:0.##} = +{e.points} "
                        + $"(combo {e.comboCount}, total {Engine.Score})", this);
            CheckRecord();
        }

        void OnBonus(BonusEvent e)
        {
            if (debugLog) Debug.Log($"[Score] Bonus « {e.label} » +{e.points} (total {Engine.Score})", this);
            CheckRecord();
        }

        void OnComboEnded(int count, int points)
        {
            if (debugLog) Debug.Log($"[Score] Fin du combo : {count} objets, {points} points", this);
            SaveBestScore();
        }

        void CheckRecord()
        {
            if (Engine.Score <= BestScore) return;
            bool hadRecord = BestScore > 0;
            BestScore = Engine.Score;
            if (hadRecord && !recordAnnounced)
            {
                recordAnnounced = true;
                Engine.AddBonus("Nouveau record", 0);
            }
        }

        void SaveBestScore()
        {
            if (PlayerPrefs.GetInt(BestScoreKey, 0) >= BestScore) return;
            PlayerPrefs.SetInt(BestScoreKey, BestScore);
            PlayerPrefs.Save();
        }

        // ------------------------------------------------------------------ HUD

        void CreateHud()
        {
            if (!table.showHud || hud != null) return;
            if (table.hudUxml == null || table.hudPanelSettings == null)
            {
                Debug.LogWarning("[Score] HUD indisponible : le barème n'a pas son UXML (ScoreHud.uxml) ou son Panel Settings. "
                               + "Lance « Rage Room > Score > Installer ou mettre à jour le score ».", table);
                return;
            }
            hud = new GameObject("Score HUD (auto)").AddComponent<ScoreHud>();
            hud.Init(this);
        }

        // ------------------------------------------------------------------ Tests

        [ContextMenu("Simuler une casse (test)")]
        void TestBreak()
        {
            if (!Application.isPlaying || Engine == null) return;
            Engine.ReportBreak(new BreakReport
            {
                objectId = NextId(),
                label = "Objet de test",
                basePoints = 100,
                point = transform.position,
                releasedAt = -1f,
                time = Time.time,
                frame = Time.frameCount
            });
        }

        [ContextMenu("Remettre le score à zéro")]
        void TestReset()
        {
            if (Application.isPlaying) ResetScore();
        }
    }
}
