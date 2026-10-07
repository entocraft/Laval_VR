using System.Collections;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace RageRoom
{
    /// <summary>
    /// Fait apparaître les objets commandés dans une boîte centrée sur ce transform.
    /// S'il y a plusieurs ObjectSpawner dans la scène, chacun est une zone : une commande passée
    /// à l'un d'eux est répartie à tour de rôle sur toutes les zones.
    /// Chaque objet est placé entièrement dans la boîte, sans chevaucher le décor, les autres
    /// objets ni le joueur. Les collisions avec le joueur sont désactivées pour ne pas bloquer
    /// sa locomotion.
    /// </summary>
    public class ObjectSpawner : MonoBehaviour
    {
        public readonly struct OrderLine
        {
            public readonly SpawnCatalog.Entry entry;
            public readonly int count;
            public OrderLine(SpawnCatalog.Entry entry, int count) { this.entry = entry; this.count = count; }
        }

        [Header("Zone de spawn (le bas de la boîte = surface où poser les objets)")]
        [Tooltip("Taille de la boîte. L'échelle du transform est aussi prise en compte.")]
        [SerializeField] Vector3 areaSize = new Vector3(1.5f, 1f, 0.8f);
        [Tooltip("Marge ajoutée autour de chaque objet pour le test de place libre.")]
        [SerializeField] float margin = 0.02f;
        [SerializeField] int placementAttempts = 15;
        [SerializeField] LayerMask overlapMask = ~0;
        [Header("Zones utilisées")]
        [Tooltip("Coché : une commande passée à ce spawner est répartie à tour de rôle sur les zones de TOUS les ObjectSpawner "
               + "actifs de la scène, celui-ci compris. Décoché : uniquement sur sa propre zone.")]
        [SerializeField] bool useAllSpawners = true;
        [Tooltip("Optionnel : zones sans spawner (composant SpawnZone) à ajouter à la répartition.")]
        [SerializeField] List<SpawnZone> extraZones = new List<SpawnZone>();

        [Header("Rythme / limites")]
        [SerializeField] float spawnInterval = 0.15f;
        [Tooltip("Si la zone est pleine, on réessaie plus tard, au maximum ce nombre de fois.")]
        [SerializeField] int retriesWhenFull = 20;
        [SerializeField] int maxObjectsInRoom = 80;
        [Tooltip("Quand une zone est pleine, supprime les débris qui l'encombrent (ceux enregistrés via Register) avant de réessayer.")]
        [SerializeField] bool clearDebrisWhenFull = true;

        [Header("Physique")]
        [Tooltip("Vitesse max de séparation quand deux objets se chevauchent (évite les objets qui bondissent).")]
        [SerializeField] float maxDepenetrationVelocity = 1.5f;
        [Tooltip("Désactive les collisions entre les objets et le corps du joueur (CharacterController).")]
        [SerializeField] bool ignorePlayerCollisions = true;

        [Header("Configuration auto des prefabs")]
        [SerializeField] bool autoAddPhysics = true;
        [SerializeField] bool autoMakeGrabbable = true;
        [SerializeField] bool placeholderIfNoPrefab = true;

        [Header("Feedback")]
        [SerializeField] AudioClip spawnClip;

        readonly List<GameObject> spawned = new List<GameObject>();
        readonly List<SpawnCatalog.Entry> queue = new List<SpawnCatalog.Entry>();
        readonly Dictionary<GameObject, Bounds> boundsCache = new Dictionary<GameObject, Bounds>();
        readonly HashSet<GameObject> debrisSet = new HashSet<GameObject>();
        string lastRefused = "";
        readonly System.Text.StringBuilder placementReport = new System.Text.StringBuilder();
        static readonly Bounds PlaceholderBounds = new Bounds(Vector3.zero, Vector3.one * 0.3f);
        Collider[] playerColliders = new Collider[0];
        Coroutine routine;
        int nextZone;

        // Tous les ObjectSpawner actifs de la scène : chacun sert de zone aux autres.
        static readonly List<ObjectSpawner> all = new List<ObjectSpawner>();

        struct Zone { public Transform t; public Vector3 size; }
        readonly List<Zone> zones = new List<Zone>();

        void OnEnable() { if (!all.Contains(this)) all.Add(this); }
        void OnDisable() { all.Remove(this); }

        int OwnObjects
        {
            get
            {
                spawned.RemoveAll(o => o == null);
                debrisSet.RemoveWhere(o => o == null);
                return spawned.Count - debrisSet.Count;
            }
        }

        /// <summary>Objets entiers présents dans la salle, tous spawners confondus. Les débris ne comptent pas dans la limite.</summary>
        public int ObjectsInRoom
        {
            get
            {
                int n = OwnObjects;
                if (useAllSpawners)
                    foreach (var s in all)
                        if (s != null && s != this) n += s.OwnObjects;
                return n;
            }
        }

        int QueuedInRoom
        {
            get
            {
                int n = queue.Count;
                if (useAllSpawners)
                    foreach (var s in all)
                        if (s != null && s != this) n += s.queue.Count;
                return n;
            }
        }

        /// <summary>Zones où cette commande peut être livrée : la sienne, celles des autres spawners, puis les SpawnZone listées.</summary>
        void CollectZones()
        {
            zones.Clear();
            zones.Add(new Zone { t = transform, size = WorldSize });
            if (useAllSpawners)
                foreach (var s in all)
                    if (s != null && s != this) zones.Add(new Zone { t = s.transform, size = s.WorldSize });
            foreach (var z in extraZones)
                if (z != null && z.isActiveAndEnabled) zones.Add(new Zone { t = z.transform, size = z.WorldSize });
        }

        Vector3 WorldSize => Vector3.Scale(areaSize, transform.lossyScale);

        void Start()
        {
            var origin = FindFirstObjectByType<XROrigin>();
            if (origin != null)
                playerColliders = origin.GetComponentsInChildren<CharacterController>(true);
        }

        /// <summary>Nombre d'objets qu'on peut encore commander : limite de la salle, moins les objets présents et ceux en attente de livraison.</summary>
        public int FreeSlots => Mathf.Max(0, maxObjectsInRoom - ObjectsInRoom - QueuedInRoom);

        /// <summary>Ajoute la commande à la file. Renvoie le nombre d'objets acceptés (limité par maxObjectsInRoom).</summary>
        public int PlaceOrder(IEnumerable<OrderLine> order)
        {
            int free = FreeSlots;
            int accepted = 0;
            foreach (var line in order)
                for (int i = 0; i < line.count && accepted < free; i++, accepted++)
                    queue.Add(line.entry);

            // Mélange pour que les objets arrivent dans le désordre
            for (int i = queue.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (queue[i], queue[j]) = (queue[j], queue[i]);
            }

            if (accepted > 0 && routine == null)
                routine = StartCoroutine(SpawnQueue());
            return accepted;
        }

        /// <summary>Supprime les objets et les débris de toute la salle (tous les spawners).</summary>
        public void ClearRoom()
        {
            ClearOwn();
            if (useAllSpawners)
                foreach (var s in all)
                    if (s != null && s != this) s.ClearOwn();
        }

        void ClearOwn()
        {
            queue.Clear();
            foreach (var o in spawned)
                if (o != null) Destroy(o);
            spawned.Clear();
            debrisSet.Clear();
        }

        /// <summary>À appeler par le script de casse pour que « Vider la salle » supprime aussi les débris.</summary>
        public void Register(GameObject debris)
        {
            if (debris == null || !debrisSet.Add(debris)) return;
            if (!spawned.Contains(debris)) spawned.Add(debris);
            IgnorePlayer(debris);
        }

        IEnumerator SpawnQueue()
        {
            var wait = new WaitForSeconds(spawnInterval);
            var waitFull = new WaitForSeconds(0.5f);
            int retries = 0;

            while (queue.Count > 0)
            {
                var entry = queue[queue.Count - 1];
                if (TrySpawn(entry))
                {
                    queue.RemoveAt(queue.Count - 1);
                    retries = 0;
                    yield return wait;
                }
                else if (clearDebrisWhenFull && ClearDebrisInZones() > 0)
                {
                    yield return null; // des débris occupaient la zone : on réessaie tout de suite
                }
                else if (++retries > retriesWhenFull)
                {
                    Debug.LogWarning($"[ObjectSpawner] Pas de place libre : {queue.Count} objet(s) annulé(s). Vide la salle, agrandis la zone, " +
                                     "ou vérifie que le bas de la zone (rectangle vert) est bien posé SUR la surface et pas dedans.\n" +
                                     $"Objet refusé : {lastRefused}\nZones essayées : {placementReport}\n" +
                                     $"Colliders présents dans les zones : {DescribeBlockers()}", this);
                    queue.Clear();
                }
                else
                {
                    yield return waitFull; // on attend que les objets tombent / se dégagent
                }
            }
            routine = null;
        }

        bool TrySpawn(SpawnCatalog.Entry entry)
        {
            var prefab = entry.PickVariant();
            if (prefab == null && !placeholderIfNoPrefab)
            {
                Debug.LogWarning($"[ObjectSpawner] Aucun prefab pour « {entry.displayName} ».", this);
                return true; // rien à faire, on passe au suivant
            }

            var localBounds = prefab != null ? GetLocalBounds(prefab) : PlaceholderBounds;
            if (!FindFreePose(localBounds, out var pos, out var rot))
            {
                lastRefused = $"« {entry.displayName} » ({Dim(localBounds.size)} m"
                            + (prefab != null ? $", échelle du prefab {Dim(prefab.transform.localScale)}" : "")
                            + $"), {zones.Count} zone(s) essayée(s)";
                return false;
            }

            GameObject go;
            if (prefab != null)
            {
                go = Instantiate(prefab, pos, rot);
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.transform.SetPositionAndRotation(pos, rot);
                go.transform.localScale = PlaceholderBounds.size;
                go.name = $"[Placeholder] {entry.displayName}";
            }

            if (StripSceneComponents(go) > 0)
                Debug.LogWarning($"[ObjectSpawner] Caméra/lumière parasite supprimée dans « {go.name} » (nettoie le prefab).", go);
            if (autoAddPhysics) EnsurePhysics(go, entry.mass);
            if (go.TryGetComponent<Rigidbody>(out var rb))
            {
                rb.maxDepenetrationVelocity = maxDepenetrationVelocity;
                // Moins coûteux que ContinuousDynamic contre les gros mesh colliders de la salle
                if (rb.collisionDetectionMode == CollisionDetectionMode.ContinuousDynamic)
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
            IgnorePlayer(go);

            if (spawnClip != null) AudioSource.PlayClipAtPoint(spawnClip, pos);
            spawned.Add(go);
            return true;
        }

        /// <summary>
        /// Essaie les zones à tour de rôle en commençant par la suivante dans la rotation, pour répartir
        /// les objets ; si une zone est pleine, essaie les autres.
        /// </summary>
        bool FindFreePose(Bounds local, out Vector3 pos, out Quaternion rot)
        {
            CollectZones();
            placementReport.Length = 0;
            for (int k = 0; k < zones.Count; k++)
            {
                int index = (nextZone + k) % zones.Count;
                Zone z = zones[index];
                if (SpawnZone.FindFreePose(z.t, z.size, local, margin, placementAttempts, overlapMask, out pos, out rot))
                {
                    nextZone = (index + 1) % zones.Count;
                    return true;
                }
                placementReport.Append("\n  - ").Append(z.t.name).Append(" (").Append(Dim(z.size)).Append(" m) : ")
                               .Append(SpawnZone.LastFailure);
            }

            pos = default;
            rot = default;
            return false;
        }

        // ---------- Débris et diagnostic ----------

        /// <summary>Supprime les débris enregistrés qui se trouvent dans une zone de spawn. Renvoie leur nombre.</summary>
        int ClearDebrisInZones()
        {
            CollectZones();
            int removed = 0;
            foreach (var z in zones)
            {
                removed += ClearDebrisIn(z.t, z.size);
                if (useAllSpawners)
                    foreach (var s in all)
                        if (s != null && s != this) removed += s.ClearDebrisIn(z.t, z.size);
            }
            return removed;
        }

        int ClearDebrisIn(Transform zone, Vector3 size)
        {
            debrisSet.RemoveWhere(o => o == null);
            if (debrisSet.Count == 0) return 0;

            int removed = 0;
            Vector3 half = size * 0.5f + Vector3.one * (margin + 0.05f); // un peu plus large : les éclats au bord gênent aussi
            foreach (var col in Physics.OverlapBox(zone.position, half, zone.rotation, overlapMask, QueryTriggerInteraction.Ignore))
            {
                GameObject piece = null;
                for (Transform t = col.transform; t != null && piece == null; t = t.parent)
                    if (debrisSet.Contains(t.gameObject)) piece = t.gameObject;
                if (piece == null) continue;

                debrisSet.Remove(piece);
                spawned.Remove(piece);
                piece.SetActive(false); // retire ses colliders tout de suite (Destroy n'agit qu'en fin d'image)
                Destroy(piece);
                removed++;
            }
            return removed;
        }

        struct Blocker { public int count; public Vector3 size; }

        /// <summary>Liste ce qui a un collider dans les zones, du plus gros au plus petit, pour comprendre ce qui empêche de poser un objet.</summary>
        string DescribeBlockers()
        {
            var found = new Dictionary<string, Blocker>();
            CollectZones();
            foreach (var z in zones) CountBlockers(z.t, z.size, found);

            if (found.Count == 0)
                return "aucun. Les zones sont donc vides : le blocage vient du placement lui-même (voir « Zones essayées »).";

            var list = new List<KeyValuePair<string, Blocker>>(found);
            list.Sort((x, y) => y.Value.size.sqrMagnitude.CompareTo(x.Value.size.sqrMagnitude));
            var sb = new System.Text.StringBuilder();
            sb.Append(list.Count).Append(" (du plus gros au plus petit)");
            for (int i = 0; i < list.Count && i < 25; i++)
            {
                sb.Append("\n  - ").Append(list[i].Key).Append(" : ").Append(Dim(list[i].Value.size)).Append(" m");
                if (list[i].Value.count > 1) sb.Append(" (x").Append(list[i].Value.count).Append(")");
            }
            if (list.Count > 25) sb.Append("\n  - ... et ").Append(list.Count - 25).Append(" autre(s)");
            return sb.ToString();
        }

        void CountBlockers(Transform zone, Vector3 size, Dictionary<string, Blocker> found)
        {
            // Boîte légèrement relevée pour ne pas compter la surface sur laquelle la zone est posée.
            Vector3 half = size * 0.5f;
            half.y = Mathf.Max(0.01f, half.y - 0.01f);
            Vector3 center = zone.position + zone.up * 0.01f;
            foreach (var col in Physics.OverlapBox(center, half, zone.rotation, overlapMask))
            {
                GameObject owner = col.attachedRigidbody != null ? col.attachedRigidbody.gameObject : col.gameObject;
                string key = owner.name.Replace("(Clone)", "").Trim()
                           + " [" + col.GetType().Name + (col.isTrigger ? ", trigger" : "")
                           + ", layer " + LayerMask.LayerToName(owner.layer) + "]";
                found.TryGetValue(key, out Blocker blocker);
                blocker.count++;
                Vector3 colSize = col.bounds.size;
                if (colSize.sqrMagnitude > blocker.size.sqrMagnitude) blocker.size = colSize;
                found[key] = blocker;
            }
        }

        static string Dim(Vector3 v) => $"{v.x:0.00} x {v.y:0.00} x {v.z:0.00}";

        /// <summary>
        /// Boîte englobante du prefab, orientée comme lui et à sa taille réelle : l'échelle de sa racine
        /// est comprise (un modèle importé puis réduit à 0.1 doit être testé à sa taille réduite).
        /// Calculée sur les meshes, mise en cache.
        /// </summary>
        Bounds GetLocalBounds(GameObject prefab)
        {
            if (boundsCache.TryGetValue(prefab, out var cached)) return cached;

            var root = Matrix4x4.Scale(prefab.transform.localScale) * prefab.transform.worldToLocalMatrix;
            bool has = false;
            var b = new Bounds();
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var m = root * mf.transform.localToWorldMatrix;
                var mb = mf.sharedMesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    var corner = mb.center + Vector3.Scale(mb.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                    var p = m.MultiplyPoint3x4(corner);
                    if (!has) { b = new Bounds(p, Vector3.zero); has = true; }
                    else b.Encapsulate(p);
                }
            }
            if (!has) b = PlaceholderBounds;
            boundsCache[prefab] = b;
            return b;
        }

        /// <summary>
        /// Les modèles exportés (Sketchfab, Blender…) contiennent parfois une caméra ou une lumière :
        /// une caméra en trop prend le rendu du casque (le joueur se voit « de loin »).
        /// </summary>
        public static int StripSceneComponents(GameObject go)
        {
            int removed = 0;
            foreach (var c in go.GetComponentsInChildren<AudioListener>(true)) { DestroyWithDependents(c); removed++; }
            foreach (var c in go.GetComponentsInChildren<Camera>(true)) { DestroyWithDependents(c); removed++; }
            foreach (var c in go.GetComponentsInChildren<Light>(true)) { DestroyWithDependents(c); removed++; }
            return removed;
        }

        /// <summary>Détruit d'abord les composants qui exigent celui-ci (ex. UniversalAdditionalCameraData), puis lui.</summary>
        static void DestroyWithDependents(Component target)
        {
            var type = target.GetType();
            foreach (var other in target.GetComponents<Component>())
            {
                if (other == target || other is Transform) continue;
                foreach (RequireComponent req in other.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                {
                    if (Requires(req.m_Type0, type) || Requires(req.m_Type1, type) || Requires(req.m_Type2, type))
                    {
                        DestroyImmediate(other);
                        break;
                    }
                }
            }
            DestroyImmediate(target);
        }

        static bool Requires(System.Type required, System.Type type) => required != null && required.IsAssignableFrom(type);

        void IgnorePlayer(GameObject go)
        {
            if (!ignorePlayerCollisions || playerColliders.Length == 0) return;
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
                foreach (var player in playerColliders)
                    if (player != null) Physics.IgnoreCollision(col, player, true);
        }

        void EnsurePhysics(GameObject go, float mass)
        {
            if (go.GetComponentInChildren<Collider>() == null)
            {
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
                {
                    if (mf.sharedMesh == null) continue;
                    var mc = mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                    mc.convex = true;
                }
            }

            if (!go.TryGetComponent<Rigidbody>(out var rb))
            {
                rb = go.AddComponent<Rigidbody>();
                rb.mass = mass;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }

            if (autoMakeGrabbable && !go.TryGetComponent<XRGrabInteractable>(out _))
                go.AddComponent<XRGrabInteractable>().useDynamicAttach = true;
        }

        void OnDrawGizmos() => SpawnZone.DrawGizmo(transform, WorldSize);

        void OnDrawGizmosSelected()
        {
            // Relie la zone principale aux zones supplémentaires
            Gizmos.matrix = Matrix4x4.identity;
            Gizmos.color = Color.yellow;
            foreach (var zone in extraZones)
                if (zone != null) Gizmos.DrawLine(transform.position, zone.transform.position);
        }

#if UNITY_EDITOR
        /// <summary>Utilisé par le menu d'édition pour enregistrer une nouvelle zone.</summary>
        public void AddZone(SpawnZone zone)
        {
            if (zone != null && !extraZones.Contains(zone)) extraZones.Add(zone);
        }

        public int ExtraZoneCount => extraZones.Count;
        public Vector3 AreaSize => areaSize;
#endif
    }
}