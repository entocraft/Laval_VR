using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;

/// <summary>
/// Fragmentation d'un mesh à l'exécution, par coupes planes successives.
/// Les coupes sont concentrées autour du point d'impact : petits éclats près du choc,
/// gros morceaux plus loin.
///
/// Mise en place :
///  - À placer sur l'objet qui porte le Rigidbody (la racine du prefab).
///    Le mesh peut être sur cet objet ou sur un de ses enfants.
///  - Le mesh doit avoir "Read/Write Enabled" coché dans ses paramètres d'import.
///  - Pour casser un objet tenu en main (kinematic) contre le décor :
///    Edit > Project Settings > Physics > Contact Pairs Mode = Enable All Contact Pairs.
///  - L'objet ne doit pas être marqué "Static" (le static batching bloque l'accès au mesh).
///  - Donne de meilleurs résultats sur des formes fermées et plutôt convexes
///    (bouteille, assiette, caisse, écran...).
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class RuntimeFracture : MonoBehaviour
{
    public enum Preset
    {
        [InspectorName("Personnalisé")] Personnalise,
        [InspectorName("Bouteille en verre")] BouteilleVerre,
        [InspectorName("Vitre")] Vitre,
        [InspectorName("Céramique")] Ceramique,
        [InspectorName("Plastique dur")] PlastiqueDur,
        [InspectorName("Bois")] Bois,
        [InspectorName("Pierre / béton")] Pierre,
        [InspectorName("Outil (batte, marteau)")] Outil
    }

    [Header("Preset")]
    [Tooltip("Choisir un preset remplit les paramètres ci-dessous, qui restent modifiables ensuite.")]
    public Preset preset = Preset.Personnalise;
    [SerializeField, HideInInspector] Preset appliedPreset = Preset.Personnalise;

    [Header("Déclenchement")]
    [Tooltip("Vitesse relative d'impact (m/s) à partir de laquelle l'objet casse.")]
    public float breakVelocity = 3f;
    [Tooltip("Les collisions avec ces layers sont ignorées (corps du joueur, mains...).")]
    public LayerMask ignoreLayers;

    [Header("Solidité")]
    [Tooltip("Lors d'un choc entre deux objets, seul le moins solide casse (à égalité : les deux). "
           + "Les objets sans ce script (sol, murs...) sont considérés comme plus solides que tout.")]
    [Min(0f)] public float solidity = 1f;
    [Tooltip("Ne casse jamais : l'objet sert seulement d'outil ou d'obstacle avec sa solidité (batte, marteau, coussin...).")]
    public bool indestructible;

    [Header("Découpe")]
    [Tooltip("Nombre de fragments visé.")]
    [Range(2, 64)] public int fragmentCount = 12;
    [Tooltip("0 = découpe uniforme, 1 = coupes très concentrées autour de l'impact.")]
    [Range(0f, 1f)] public float impactFocus = 0.7f;
    [Tooltip("Matériau des faces intérieures (la tranche). Si vide, réutilise le matériau de l'objet.")]
    public Material insideMaterial;
    [Tooltip("Objet creux (bouteille, verre, vase...) : les coupes ne sont pas rebouchées, "
           + "les fragments restent des éclats fins au lieu de blocs pleins.")]
    public bool hollow;

    [Header("Fragments")]
    [Tooltip("Vitesse (m/s) ajoutée aux fragments, en s'éloignant du point d'impact.")]
    public float scatterSpeed = 1.5f;
    [Tooltip("Durée de vie des débris en secondes (0 = permanents).")]
    public float debrisLifetime = 8f;
    [Tooltip("Variation aléatoire (± secondes) appliquée à la durée de vie de chaque fragment.")]
    [Min(0f)] public float debrisLifetimeVariance = 2f;
    [Tooltip("Les fragments plus petits que cette taille (en mètres) ne sont pas créés.")]
    public float minFragmentSize = 0.02f;
    [Tooltip("Vrai : MeshCollider convexe (précis). Faux : BoxCollider (plus léger, conseillé sur Quest).")]
    public bool convexColliders = true;
    [Tooltip("Nombre de fois où les fragments peuvent eux-mêmes se recasser.")]
    [Range(0, 3)] public int refractureDepth = 0;

    [Header("Effets")]
    [Tooltip("Boule de feu brève à la casse.")]
    public bool explosionEffect;
    [Tooltip("Volutes de fumée qui montent et se dissipent.")]
    public bool smokeEffect;
    [Tooltip("Nuage de poussière qui s'étale puis retombe.")]
    public bool dustEffect;
    [Tooltip("Taille des effets par rapport à l'objet (1 = proportionnée à l'objet).")]
    [Min(0.1f)] public float effectScale = 1f;
    [Tooltip("Couleur de la poussière (beige pour du plâtre ou du bois, gris pour du béton, blanc pour de la céramique...).")]
    public Color dustColor = new Color(0.72f, 0.66f, 0.56f);
    [Tooltip("Matériau de particules personnalisé (optionnel). Si vide, un matériau simple est créé automatiquement.")]
    public Material effectMaterial;
    [Tooltip("Tes propres prefabs d'effets, créés au point d'impact puis supprimés au bout de 6 secondes (optionnel).")]
    public GameObject[] customEffects;

    [Header("Optimisation")]
    [Tooltip("Nombre de découpes différentes gardées en mémoire par modèle. Une fois ce nombre atteint, les objets suivants "
           + "réutilisent la découpe dont le point d'impact est le plus proche, au lieu d'en recalculer une. 0 = toujours recalculer.")]
    [Range(0, 8)] public int cacheVariants = 3;
    [Tooltip("Calcule ces découpes au chargement de la scène (une par image) pour éviter tout calcul pendant le jeu.")]
    public bool prewarmCache;
    [Tooltip("Nombre maximal de fragments présents en même temps dans la scène, tous objets confondus. "
           + "Au-delà, les plus anciens disparaissent. Mets la même valeur sur tous tes objets. 0 = pas de limite.")]
    [Min(0)] public int fragmentBudget = 200;
    [Tooltip("Quand la scène se remplit de débris, les objets cassent en moins de morceaux, plus gros, jusqu'à ce minimum.")]
    [Range(2, 16)] public int minFragmentCount = 3;

    [Header("Son")]
    [Tooltip("Nom du dossier dans Assets/Sounds/ (rempli par le preset, modifiable). "
           + "Tous les clips SO_... de ce dossier sont chargés automatiquement dans la liste ci-dessous.")]
    [Delayed] public string soundMaterial = "";
    [SerializeField, HideInInspector] string appliedSoundMaterial = "";
    [Tooltip("Sons de casse. S'il y en a plusieurs, un clip est tiré au hasard à chaque fois.")]
    public AudioClip[] breakSounds;
    [Range(0f, 1f)] public float soundVolume = 1f;
    [Tooltip("Variation aléatoire de hauteur (±) pour que deux casses ne sonnent jamais pareil.")]
    [Range(0f, 0.5f)] public float pitchVariation = 0.1f;

    [Header("Événement")]
    [Tooltip("Appelé à la casse avec le point d'impact (monde) : son, particules, haptique...")]
    public UnityEvent<Vector3> onBreak;

    [Header("Diagnostic")]
    [Tooltip("Affiche dans la console la vitesse de chaque impact reçu.")]
    public bool debugLog;

    // État interne (renseigné automatiquement sur les fragments générés)
    bool canBreak = true;
    bool broken;
    bool lastSubmeshIsInside;
    float armedAt;
    Mesh ownedMesh;

    // Poses des derniers pas physiques : sert à mesurer la vitesse réelle de l'objet,
    // y compris quand il est tenu en main (kinematic, donc sans vitesse côté moteur physique).
    readonly Matrix4x4[] poses = new Matrix4x4[4];
    int poseHead;

    // Fragment comptabilisé dans le budget global.
    bool countedFragment;

    /// <summary>Mis à vrai par RuntimeCrush : le mesh de cet objet est déformé, donc propre à lui, et ne doit pas être mis en cache.</summary>
    [System.NonSerialized] public bool hasUniqueMesh;

    // ------------------------------------------------------------------ Données partagées entre tous les objets

    class FragmentData
    {
        public Mesh mesh;
        public Bounds bounds;
        public float volumeShare;
    }

    class FractureResult
    {
        public readonly List<FragmentData> fragments = new List<FragmentData>();
        public int count;
        public int capSub;
        public bool hollow;
        public bool shared;          // vrai : meshes gardés en cache, à ne pas détruire avec les fragments
        public Vector3 localImpact;  // point d'impact pour lequel cette découpe a été calculée
    }

    static readonly Dictionary<Mesh, List<FractureResult>> cache = new Dictionary<Mesh, List<FractureResult>>();
    static readonly Queue<RuntimeFracture> fragmentQueue = new Queue<RuntimeFracture>();
    static int liveFragments;
    static int freshFrame = -1; // dernière image où une découpe a été calculée
    static Material particleMaterial; // matériau de particules créé automatiquement
    static bool particleWarningShown;

    /// <summary>Nombre de fragments actuellement présents dans la scène.</summary>
    public static int LiveFragments => liveFragments;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        cache.Clear();
        fragmentQueue.Clear();
        liveFragments = 0;
        freshFrame = -1;
        particleMaterial = null;
        particleWarningShown = false;
    }

    /// <summary>
    /// Libère toutes les découpes gardées en mémoire.
    /// À appeler lors d'un changement de scène, quand plus aucun fragment n'est affiché.
    /// </summary>
    public static void ClearCache()
    {
        foreach (List<FractureResult> list in cache.Values)
            foreach (FractureResult r in list)
                foreach (FragmentData d in r.fragments)
                    if (d.mesh != null) Destroy(d.mesh);
        cache.Clear();
    }

    // ------------------------------------------------------------------ Déclenchement

    // ------------------------------------------------------------------ Presets

    void OnValidate()
    {
        if (preset != appliedPreset)
        {
            appliedPreset = preset;
            ApplyPreset(preset);
        }
        if (soundMaterial != appliedSoundMaterial)
        {
            appliedSoundMaterial = soundMaterial;
            ReloadSounds();
        }
    }

    /// <summary>Recharge la liste de sons depuis Assets/Sounds/[soundMaterial]/ (éditeur uniquement).</summary>
    [ContextMenu("Recharger les sons du dossier")]
    void ReloadSounds()
    {
#if UNITY_EDITOR
        AudioClip[] found = RuntimeFracture.FindMaterialSounds(soundMaterial, this);
        if (found == null) return;
        breakSounds = found;
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    /// <summary>Remplit les paramètres selon un matériau type. "Personnalisé" ne change rien.</summary>
    public void ApplyPreset(Preset p)
    {
        switch (p)
        {
            //                                   vitesse solidité fragments focus  creux  dispersion
            case Preset.BouteilleVerre: SetMaterial(2.5f, 1f, 16, 0.8f, true, 2.0f, "Glass"); break;
            case Preset.Vitre: SetMaterial(2.0f, 1f, 24, 0.9f, false, 1.5f, "Glass"); break;
            case Preset.Ceramique: SetMaterial(3.0f, 2f, 10, 0.6f, false, 1.5f, "Ceramic"); break;
            case Preset.PlastiqueDur: SetMaterial(5.0f, 3f, 8, 0.5f, false, 1.2f, "Plastic"); break;
            case Preset.Bois: SetMaterial(6.0f, 5f, 6, 0.4f, false, 1.0f, "Wood"); break;
            case Preset.Pierre: SetMaterial(7.0f, 7f, 6, 0.3f, false, 0.8f, "Stone"); break;
            case Preset.Outil:
                solidity = 10f;
                indestructible = true;
                break;
        }
    }

    void SetMaterial(float velocity, float solid, int fragments, float focus, bool isHollow, float scatter, string sounds)
    {
        soundMaterial = sounds;
        breakVelocity = velocity;
        solidity = solid;
        fragmentCount = fragments;
        impactFocus = focus;
        hollow = isHollow;
        scatterSpeed = scatter;
        indestructible = false;
    }

    System.Collections.IEnumerator Start()
    {
        if (!prewarmCache || cacheVariants <= 0 || indestructible || !canBreak || hasUniqueMesh) yield break;

        foreach (MeshFilter mf in GetComponentsInChildren<MeshFilter>())
        {
            Mesh src = mf.sharedMesh;
            if (src == null || !src.isReadable || src == ownedMesh) continue;
            int capSub = lastSubmeshIsInside ? src.subMeshCount - 1 : src.subMeshCount;

            // Une seule découpe par image, tous objets confondus, pour ne pas figer le chargement.
            while (CountVariants(src, fragmentCount, capSub) < cacheVariants)
            {
                if (freshFrame != Time.frameCount)
                {
                    Bounds b = src.bounds;
                    Vector3 impact = b.center + Vector3.Scale(b.extents, Random.onUnitSphere);
                    FractureResult r = Generate(src, capSub, impact, fragmentCount);
                    r.shared = true;
                    Store(src, r);
                }
                yield return null;
            }
        }
    }

    void OnEnable()
    {
        Matrix4x4 m = transform.localToWorldMatrix;
        for (int i = 0; i < poses.Length; i++) poses[i] = m;
    }

    void FixedUpdate()
    {
        poseHead = (poseHead + 1) % poses.Length;
        poses[poseHead] = transform.localToWorldMatrix;
    }

    /// <summary>
    /// Vitesse (monde) d'un point de l'objet, moyennée sur les derniers pas physiques.
    /// Tient compte de la rotation : le bout d'une bouteille qu'on balance va plus vite que sa base.
    /// </summary>
    public Vector3 PointVelocity(Vector3 worldPoint)
    {
        Matrix4x4 newest = poses[poseHead];
        Matrix4x4 oldest = poses[(poseHead + 1) % poses.Length];
        Vector3 local = newest.inverse.MultiplyPoint3x4(worldPoint);
        float span = (poses.Length - 1) * Time.fixedDeltaTime;
        return (worldPoint - oldest.MultiplyPoint3x4(local)) / span;
    }

    void OnCollisionEnter(Collision col)
    {
        if (indestructible || !canBreak || broken || Time.time < armedAt) return;
        if (col.contactCount == 0) return;
        if ((ignoreLayers.value & (1 << col.collider.gameObject.layer)) != 0) return;

        // Solidité de ce qu'on a percuté : sans ce script, l'autre objet est "infiniment" solide.
        RuntimeFracture other = col.collider.GetComponentInParent<RuntimeFracture>();
        float otherSolidity = other != null ? other.solidity : Mathf.Infinity;

        ContactPoint contact = col.GetContact(0);
        Vector3 point = contact.point;

        // Normale orientée de l'obstacle vers cet objet.
        Vector3 n = contact.normal;
        if (Vector3.Dot(n, GetComponent<Rigidbody>().worldCenterOfMass - point) < 0f) n = -n;

        // Vitesse d'approche mesurée par suivi de position : la seule fiable quand l'objet est tenu en main.
        // On ne compte que le rapprochement, pour ne pas casser un objet qu'on soulève d'une table.
        Vector3 otherVel = other != null ? other.PointVelocity(point)
                         : col.rigidbody != null ? col.rigidbody.GetPointVelocity(point)
                         : Vector3.zero;
        float trackedSpeed = Mathf.Max(0f, -Vector3.Dot(PointVelocity(point) - otherVel, n));

        float physicsSpeed = col.relativeVelocity.magnitude;
        float speed = Mathf.Max(physicsSpeed, trackedSpeed);

        if (debugLog)
            Debug.Log($"[RuntimeFracture] '{name}' contre '{col.collider.name}' : {speed:F1} m/s "
                    + $"(physique {physicsSpeed:F1}, suivi {trackedSpeed:F1}, seuil {breakVelocity:F1}) "
                    + $"— solidité {solidity} contre {otherSolidity}", this);

        if (speed < breakVelocity) return;      // choc trop faible
        if (otherSolidity < solidity) return;   // l'autre est plus fragile : c'est lui qui casse
        Break(point);
    }

    /// <summary>Clic droit sur le composant en mode Play : casse l'objet sans attendre de collision.</summary>
    [ContextMenu("Casser maintenant (test)")]
    void BreakNow()
    {
        if (Application.isPlaying) Break(transform.position);
    }

    void OnDestroy()
    {
        if (countedFragment)
        {
            countedFragment = false;
            liveFragments--;
        }
        // Les meshes créés par script ne sont pas libérés automatiquement (ceux du cache sont partagés, on n'y touche pas).
        if (ownedMesh != null) Destroy(ownedMesh);
    }

    /// <summary>Casse l'objet à partir d'un point d'impact en coordonnées monde.</summary>
    public void Break(Vector3 worldImpact)
    {
        if (broken) return;

        // Le mesh peut être sur cet objet ou sur un enfant
        // (cas courant des prefabs XR : Rigidbody à la racine, visuel en enfant).
        MeshFilter[] filters = GetComponentsInChildren<MeshFilter>();
        Rigidbody rb = GetComponent<Rigidbody>();
        float massPerMesh = rb.mass / Mathf.Max(1, filters.Length);

        int done = 0;
        foreach (MeshFilter mf in filters)
            if (FractureMesh(mf, rb, massPerMesh, worldImpact)) done++;

        if (done == 0)
        {
            Debug.LogWarning($"[RuntimeFracture] Aucun mesh exploitable trouvé sur '{name}' ou ses enfants.", this);
            return;
        }

        broken = true;
        PlayAt(breakSounds, worldImpact, soundVolume, pitchVariation);
        SpawnEffects(worldImpact);
        onBreak?.Invoke(worldImpact);
        Destroy(gameObject);
    }

    bool FractureMesh(MeshFilter mf, Rigidbody rb, float mass, Vector3 worldImpact)
    {
        Mesh src = mf.sharedMesh;
        MeshRenderer mr = mf.GetComponent<MeshRenderer>();
        if (src == null || mr == null) return false;
        if (!src.isReadable)
        {
            Debug.LogWarning($"[RuntimeFracture] Le mesh '{src.name}' doit avoir Read/Write activé.", mf);
            return false;
        }

        Transform t = mf.transform;

        // Le dernier sous-mesh est réservé aux faces intérieures.
        int capSub = lastSubmeshIsInside ? src.subMeshCount - 1 : src.subMeshCount;
        Vector3 localImpact = t.InverseTransformPoint(worldImpact);

        // --- Charge de la scène : plus il y a de débris, moins on crée de morceaux (donc plus gros).
        float load = fragmentBudget > 0 ? Mathf.Clamp01((float)liveFragments / fragmentBudget) : 0f;
        int count = EffectiveCount(load);

        // --- Découpe : réutilisée depuis le cache si possible, calculée sinon.
        bool cacheable = cacheVariants > 0 && src != ownedMesh && !hasUniqueMesh;
        FractureResult result = cacheable ? FromCache(src, count, capSub, localImpact) : null;
        bool reused = result != null;
        if (!reused)
        {
            result = Generate(src, capSub, localImpact, count);
            if (cacheable)
            {
                result.shared = true;
                Store(src, result);
            }
        }

        if (debugLog)
            Debug.Log($"[RuntimeFracture] '{name}' : découpe {(reused ? "réutilisée" : "calculée")}, "
                    + $"{result.fragments.Count} morceaux, {liveFragments} fragments déjà en scène (budget {fragmentBudget})", this);

        // --- Création des fragments
        Material[] mats = BuildMaterials(mr.sharedMaterials, capSub);
        Vector3 scale = t.lossyScale;
        float lifeScale = Mathf.Lerp(1f, 0.5f, load); // scène chargée : les débris restent moins longtemps

        foreach (FragmentData d in result.fragments)
        {
            Vector3 worldSize = Vector3.Scale(d.bounds.size, scale);
            float maxSize = Mathf.Max(Mathf.Abs(worldSize.x), Mathf.Abs(worldSize.y), Mathf.Abs(worldSize.z));
            if (maxSize < minFragmentSize)
            {
                if (!result.shared) Destroy(d.mesh); // mesh non utilisé et non partagé : on le libère
                continue;
            }
            SpawnFragment(d, result.shared, t, mats, rb, mass * d.volumeShare, worldImpact, lifeScale);
        }

        TrimFragments(fragmentBudget);
        return true;
    }

    // ------------------------------------------------------------------ Niveau de détail et budget

    /// <summary>Nombre de morceaux visé selon la charge : 3 paliers pour que le cache reste efficace.</summary>
    int EffectiveCount(float load)
    {
        int floor = Mathf.Clamp(minFragmentCount, 2, fragmentCount);
        if (load < 0.5f) return fragmentCount;
        if (load < 0.8f) return Mathf.Max(floor, fragmentCount / 2);
        return Mathf.Max(floor, fragmentCount / 4);
    }

    /// <summary>Supprime les fragments les plus anciens quand le budget global est dépassé.</summary>
    static void TrimFragments(int budget)
    {
        // Nettoyage des entrées déjà détruites en tête de file.
        while (fragmentQueue.Count > 0 && (fragmentQueue.Peek() == null || !fragmentQueue.Peek().countedFragment))
            fragmentQueue.Dequeue();

        if (budget <= 0) return;

        while (liveFragments > budget && fragmentQueue.Count > 0)
        {
            RuntimeFracture old = fragmentQueue.Dequeue();
            if (old == null || !old.countedFragment) continue;
            old.countedFragment = false;
            liveFragments--;
            Destroy(old.gameObject);
        }
    }

    // ------------------------------------------------------------------ Cache de découpes

    int CountVariants(Mesh src, int count, int capSub)
    {
        if (!cache.TryGetValue(src, out List<FractureResult> list)) return 0;
        int n = 0;
        foreach (FractureResult r in list)
            if (r.count == count && r.capSub == capSub && r.hollow == hollow) n++;
        return n;
    }

    /// <summary>
    /// Renvoie la découpe en cache la plus proche du point d'impact, ou null s'il faut en calculer une nouvelle.
    /// </summary>
    FractureResult FromCache(Mesh src, int count, int capSub, Vector3 localImpact)
    {
        if (!cache.TryGetValue(src, out List<FractureResult> list)) return null;

        FractureResult best = null;
        float bestDist = float.MaxValue;
        int matching = 0;

        foreach (FractureResult r in list)
        {
            if (r.count != count || r.capSub != capSub || r.hollow != hollow) continue;
            matching++;
            float d = (r.localImpact - localImpact).sqrMagnitude;
            if (d < bestDist) { bestDist = d; best = r; }
        }
        if (best == null) return null;

        // Cache plein : on réutilise. Cache incomplet : on calcule une variante de plus,
        // sauf si une découpe a déjà été calculée pendant cette image (plusieurs objets cassés d'un coup).
        bool full = matching >= cacheVariants;
        bool busy = freshFrame == Time.frameCount;
        return full || busy ? best : null;
    }

    static void Store(Mesh src, FractureResult result)
    {
        if (!cache.TryGetValue(src, out List<FractureResult> list))
        {
            list = new List<FractureResult>();
            cache[src] = list;
        }
        list.Add(result);
    }

    // ------------------------------------------------------------------ Calcul d'une découpe

    FractureResult Generate(Mesh src, int capSub, Vector3 localImpact, int count)
    {
        freshFrame = Time.frameCount;

        Piece root = Piece.FromMesh(src, capSub + 1, out bool hadNormals);

        // On recoupe en priorité les gros morceaux proches de l'impact.
        var pieces = new List<Piece> { root };
        float refSize = Mathf.Max(root.bounds.size.magnitude, 1e-6f);
        int attempts = count * 4;

        while (pieces.Count < count && attempts-- > 0)
        {
            int idx = PickPiece(pieces, localImpact, refSize);
            Piece p = pieces[idx];

            Vector3 target = p.bounds.ClosestPoint(localImpact);
            Vector3 origin = Vector3.Lerp(p.bounds.center, target, impactFocus * Random.value);
            Vector3 normal = Random.onUnitSphere;

            if (Slice(p, origin, normal, capSub, !hollow, out Piece a, out Piece b))
            {
                pieces[idx] = a;
                pieces.Add(b);
            }
            else
            {
                p.failures++;
            }
        }

        float totalVolume = 0f;
        foreach (Piece p in pieces) totalVolume += Volume(p.bounds);
        totalVolume = Mathf.Max(totalVolume, 1e-9f);

        var result = new FractureResult { count = count, capSub = capSub, hollow = hollow, localImpact = localImpact };
        foreach (Piece p in pieces)
        {
            if (p.verts.Count < 4) continue;
            result.fragments.Add(new FragmentData
            {
                mesh = BuildMesh(p, hadNormals),
                bounds = p.bounds,
                volumeShare = Volume(p.bounds) / totalVolume
            });
        }
        return result;
    }

    static Mesh BuildMesh(Piece p, bool hadNormals)
    {
        var mesh = new Mesh { name = "FragmentMesh" };
        if (p.verts.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertices(p.verts);
        mesh.SetNormals(p.normals);
        mesh.SetUVs(0, p.uvs);
        mesh.subMeshCount = p.tris.Length;
        for (int s = 0; s < p.tris.Length; s++) mesh.SetTriangles(p.tris[s], s);
        if (!hadNormals) mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }

    // ------------------------------------------------------------------ Fragments

    void SpawnFragment(FragmentData d, bool shared, Transform t, Material[] mats, Rigidbody sourceRb, float mass,
                       Vector3 worldImpact, float lifeScale)
    {
        var go = new GameObject(name + "_frag") { layer = t.gameObject.layer };
        go.transform.SetPositionAndRotation(t.position, t.rotation);
        go.transform.localScale = t.lossyScale;

        // Collider ajouté avant le MeshFilter pour éviter une double préparation physique du mesh.
        // Avec un mesh du cache, cette préparation n'est faite qu'une fois puis réutilisée par Unity.
        if (convexColliders)
        {
            var mc = go.AddComponent<MeshCollider>();
            mc.convex = true;
            mc.sharedMesh = d.mesh;
        }
        else
        {
            var box = go.AddComponent<BoxCollider>();
            box.center = d.bounds.center;
            box.size = d.bounds.size;
        }

        go.AddComponent<MeshFilter>().sharedMesh = d.mesh;
        go.AddComponent<MeshRenderer>().sharedMaterials = mats;

        var frb = go.AddComponent<Rigidbody>();
        frb.mass = Mathf.Max(0.01f, mass);
        frb.interpolation = sourceRb.interpolation;

        Vector3 worldCenter = t.TransformPoint(d.bounds.center);
        Vector3 dir = (worldCenter - worldImpact).normalized;
        Vector3 baseVel = sourceRb.isKinematic ? PointVelocity(worldCenter) : GetVelocity(sourceRb);
        SetVelocity(frb, baseVel + dir * scatterSpeed);
        frb.angularVelocity = sourceRb.angularVelocity + Random.insideUnitSphere * scatterSpeed;

        // Le fragment porte le même composant : suivi du budget, libération du mesh, recasse éventuelle.
        var f = go.AddComponent<RuntimeFracture>();
        f.ownedMesh = shared ? null : d.mesh;
        f.lastSubmeshIsInside = true;
        f.canBreak = refractureDepth > 0;
        f.refractureDepth = Mathf.Max(0, refractureDepth - 1);
        f.armedAt = Time.time + 0.25f; // évite la recasse immédiate entre fragments voisins
        f.breakVelocity = breakVelocity;
        f.fragmentCount = Mathf.Max(2, fragmentCount / 3);
        f.impactFocus = impactFocus;
        f.insideMaterial = insideMaterial;
        f.hollow = hollow;
        f.scatterSpeed = scatterSpeed;
        f.debrisLifetime = debrisLifetime;
        f.debrisLifetimeVariance = debrisLifetimeVariance;
        f.solidity = solidity;
        f.breakSounds = breakSounds;
        f.soundVolume = soundVolume * 0.6f; // un fragment qui recasse fait moins de bruit
        f.pitchVariation = pitchVariation;
        f.ignoreLayers = ignoreLayers;
        f.minFragmentSize = minFragmentSize;
        f.convexColliders = convexColliders;
        f.cacheVariants = cacheVariants;
        f.fragmentBudget = fragmentBudget;
        f.minFragmentCount = minFragmentCount;
        f.debugLog = debugLog;

        f.countedFragment = true;
        liveFragments++;
        fragmentQueue.Enqueue(f);

        if (debrisLifetime > 0f)
        {
            float life = debrisLifetime + Random.Range(-debrisLifetimeVariance, debrisLifetimeVariance);
            Destroy(go, Mathf.Max(0.1f, life * lifeScale));
        }
    }

    Material[] BuildMaterials(Material[] src, int capSub)
    {
        var mats = new Material[capSub + 1];
        for (int i = 0; i <= capSub; i++)
            mats[i] = src.Length > 0 ? src[Mathf.Min(i, src.Length - 1)] : null;
        if (!lastSubmeshIsInside && insideMaterial != null) mats[capSub] = insideMaterial;
        return mats;
    }

    int PickPiece(List<Piece> pieces, Vector3 localImpact, float refSize)
    {
        int best = 0;
        float bestScore = -1f;
        for (int i = 0; i < pieces.Count; i++)
        {
            Piece p = pieces[i];
            float d = Vector3.Distance(p.bounds.ClosestPoint(localImpact), localImpact) / refSize;
            float score = p.bounds.size.sqrMagnitude / (1f + impactFocus * 10f * d);
            score *= Mathf.Pow(0.25f, p.failures); // pénalise les morceaux qu'on n'arrive pas à couper
            if (score > bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    // ------------------------------------------------------------------ Effets visuels

    void SpawnEffects(Vector3 worldImpact)
    {
        if (customEffects != null)
            foreach (GameObject prefab in customEffects)
                if (prefab != null) Destroy(Instantiate(prefab, worldImpact, Quaternion.identity), 6f);

        if (!explosionEffect && !smokeEffect && !dustEffect) return;

        // Centre et taille de l'objet, pour proportionner les effets.
        Vector3 center = worldImpact;
        float objectSize = 0.2f;
        Renderer[] rends = GetComponentsInChildren<Renderer>();
        if (rends.Length > 0)
        {
            Bounds b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            center = b.center;
            objectSize = b.size.magnitude;
        }
        float s = Mathf.Max(0.02f, objectSize * effectScale);

        Material mat = effectMaterial != null ? effectMaterial : DefaultParticleMaterial();
        if (mat == null) return;

        // Scène chargée en débris : moins de particules.
        float load = fragmentBudget > 0 ? Mathf.Clamp01((float)liveFragments / fragmentBudget) : 0f;
        float density = Mathf.Lerp(1f, 0.4f, load);

        if (explosionEffect)
        {
            Gradient fire = Fade(new Color(1f, 0.95f, 0.7f), new Color(1f, 0.5f, 0.1f), new Color(0.15f, 0.12f, 0.1f), 1f);
            EmitBurst("FX_Explosion", center, mat, Count(24, density),
                      life: new Vector2(0.25f, 0.5f), speed: new Vector2(3f, 8f) * s, size: new Vector2(0.6f, 1.4f) * s,
                      radius: 0.15f * s, gravity: 0f, damp: 0.2f, endSize: 1.6f, colors: fire);
        }

        if (smokeEffect)
        {
            Gradient grey = Fade(new Color(0.4f, 0.4f, 0.4f), new Color(0.3f, 0.3f, 0.3f), new Color(0.2f, 0.2f, 0.2f), 0.55f);
            EmitBurst("FX_Fumee", center, mat, Count(12, density),
                      life: new Vector2(1.5f, 3f), speed: new Vector2(0.5f, 1.5f) * s, size: new Vector2(0.8f, 1.6f) * s,
                      radius: 0.3f * s, gravity: -0.03f, damp: 0.05f, endSize: 2.5f, colors: grey);
        }

        if (dustEffect)
        {
            Gradient dust = Fade(dustColor, dustColor, dustColor * 0.85f, 0.6f);
            EmitBurst("FX_Poussiere", worldImpact, mat, Count(30, density),
                      life: new Vector2(0.8f, 1.6f), speed: new Vector2(1f, 3f) * s, size: new Vector2(0.15f, 0.5f) * s,
                      radius: 0.3f * s, gravity: 0.1f, damp: 0.1f, endSize: 2f, colors: dust);
        }
    }

    static int Count(int baseCount, float density) => Mathf.Max(4, Mathf.RoundToInt(baseCount * density));

    /// <summary>Dégradé de trois couleurs, opaque au début puis qui s'efface.</summary>
    static Gradient Fade(Color start, Color middle, Color end, float alpha)
    {
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(start, 0f), new GradientColorKey(middle, 0.35f), new GradientColorKey(end, 1f) },
            new[] { new GradientAlphaKey(alpha, 0f), new GradientAlphaKey(alpha, 0.3f), new GradientAlphaKey(0f, 1f) });
        return g;
    }

    /// <summary>Crée un système de particules qui émet une seule bouffée puis se supprime tout seul.</summary>
    static void EmitBurst(string label, Vector3 position, Material mat, int count, Vector2 life, Vector2 speed,
                          Vector2 size, float radius, float gravity, float damp, float endSize, Gradient colors)
    {
        var go = new GameObject(label);
        go.transform.position = position;

        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); // à l'arrêt pendant le paramétrage

        var main = ps.main;
        main.playOnAwake = false;
        main.loop = false;
        main.duration = life.y;
        main.startLifetime = new ParticleSystem.MinMaxCurve(life.x, life.y);
        main.startSpeed = new ParticleSystem.MinMaxCurve(speed.x, speed.y);
        main.startSize = new ParticleSystem.MinMaxCurve(size.x, size.y);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.startColor = Color.white;
        main.gravityModifier = gravity;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = count;
        main.stopAction = ParticleSystemStopAction.Destroy; // l'objet se supprime quand l'effet est terminé

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = radius;

        var colorOver = ps.colorOverLifetime;
        colorOver.enabled = true;
        colorOver.color = new ParticleSystem.MinMaxGradient(colors);

        // Les particules grossissent jusqu'à endSize fois leur taille de départ.
        var sizeOver = ps.sizeOverLifetime;
        sizeOver.enabled = true;
        sizeOver.size = new ParticleSystem.MinMaxCurve(endSize, AnimationCurve.EaseInOut(0f, 1f / endSize, 1f, 1f));

        // Freinage : les particules partent vite puis ralentissent.
        var limit = ps.limitVelocityOverLifetime;
        limit.enabled = damp > 0f;
        limit.limit = 0f;
        limit.dampen = damp;

        var rend = go.GetComponent<ParticleSystemRenderer>();
        rend.sharedMaterial = mat;
        rend.shadowCastingMode = ShadowCastingMode.Off;
        rend.receiveShadows = false;

        ps.Play();
    }

    /// <summary>Matériau de particules transparent avec une texture ronde et douce, créé une seule fois.</summary>
    static Material DefaultParticleMaterial()
    {
        if (particleMaterial != null) return particleMaterial;

        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
        {
            if (!particleWarningShown)
                Debug.LogWarning("[RuntimeFracture] Shader 'Sprites/Default' introuvable : assigne un matériau dans Effect Material pour voir les effets.");
            particleWarningShown = true;
            return null;
        }

        // Disque blanc aux bords fondus.
        const int res = 32;
        var tex = new Texture2D(res, res, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        float half = (res - 1) * 0.5f;
        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                float d = Mathf.Sqrt((x - half) * (x - half) + (y - half) * (y - half)) / half;
                float a = Mathf.Clamp01(1f - d);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
            }
        }
        tex.Apply();

        particleMaterial = new Material(shader) { name = "FractureParticles", mainTexture = tex };
        return particleMaterial;
    }

#if UNITY_EDITOR
    /// <summary>
    /// Cherche dans Assets/Sounds/[matériau]/ les clips nommés SO_..., triés par numéro.
    /// Le nom du dossier est comparé sans tenir compte des majuscules ni des accents.
    /// Éditeur uniquement : les clips trouvés sont enregistrés sur le composant, donc présents dans le build.
    /// </summary>
    public static AudioClip[] FindMaterialSounds(string material, Object context)
    {
        const string root = "Assets/Sounds";
        if (string.IsNullOrWhiteSpace(material)) return null;

        if (!UnityEditor.AssetDatabase.IsValidFolder(root))
        {
            Debug.LogWarning($"[Sons] Dossier '{root}' introuvable.", context);
            return null;
        }

        string wanted = Simplify(material);
        string folder = null;
        foreach (string sub in UnityEditor.AssetDatabase.GetSubFolders(root))
        {
            if (Simplify(System.IO.Path.GetFileName(sub)) != wanted) continue;
            folder = sub;
            break;
        }
        if (folder == null)
        {
            Debug.LogWarning($"[Sons] Aucun dossier '{root}/{material}'. Vérifie le champ Sound Material.", context);
            return null;
        }

        var clips = new List<AudioClip>();
        foreach (string guid in UnityEditor.AssetDatabase.FindAssets("t:AudioClip", new[] { folder }))
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            AudioClip clip = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip != null && clip.name.StartsWith("SO_", System.StringComparison.OrdinalIgnoreCase))
                clips.Add(clip);
        }
        if (clips.Count == 0)
        {
            Debug.LogWarning($"[Sons] Aucun clip 'SO_...' dans '{folder}'.", context);
            return null;
        }

        clips.Sort((a, b) =>
        {
            int c = TrailingNumber(a.name).CompareTo(TrailingNumber(b.name));
            return c != 0 ? c : string.CompareOrdinal(a.name, b.name);
        });
        Debug.Log($"[Sons] {clips.Count} clip(s) chargé(s) depuis '{folder}'.", context);
        return clips.ToArray();
    }

    // Minuscules, sans accents, sans espaces ni ponctuation : "Céramique" et "ceramique" deviennent identiques.
    static string Simplify(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in text.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    // Numéro en fin de nom : SO_Verre_12 -> 12 (pour trier 2 avant 10).
    static int TrailingNumber(string name)
    {
        int i = name.Length;
        while (i > 0 && char.IsDigit(name[i - 1])) i--;
        return i < name.Length && int.TryParse(name.Substring(i), out int n) ? n : int.MaxValue;
    }
#endif

    /// <summary>Joue un clip tiré au hasard à une position donnée, en son 3D, puis nettoie la source.</summary>
    static void PlayAt(AudioClip[] clips, Vector3 position, float volume, float pitchRange)
    {
        if (clips == null || clips.Length == 0) return;
        AudioClip clip = clips[Random.Range(0, clips.Length)];
        if (clip == null) return;

        var go = new GameObject("OneShotAudio");
        go.transform.position = position;

        var src = go.AddComponent<AudioSource>();
        src.clip = clip;
        src.volume = Mathf.Clamp01(volume) * RageRoom.GameSettings.FxVolume; // réglage « Volume des effets » du menu
        src.pitch = 1f + Random.Range(-pitchRange, pitchRange);
        src.spatialBlend = 1f;   // son entièrement spatialisé
        src.minDistance = 0.5f;
        src.maxDistance = 20f;
        src.Play();

        Destroy(go, clip.length / Mathf.Max(0.1f, src.pitch) + 0.1f);
    }

    static float Volume(Bounds b) => Mathf.Abs(b.size.x * b.size.y * b.size.z);

    static Vector3 GetVelocity(Rigidbody rb)
    {
#if UNITY_6000_0_OR_NEWER
        return rb.linearVelocity;
#else
        return rb.velocity;
#endif
    }

    static void SetVelocity(Rigidbody rb, Vector3 v)
    {
#if UNITY_6000_0_OR_NEWER
        rb.linearVelocity = v;
#else
        rb.velocity = v;
#endif
    }

    // ------------------------------------------------------------------ Découpe par un plan

    /// <summary>
    /// Coupe un morceau en deux selon le plan (origin, normal).
    /// a = côté de la normale, b = côté opposé. Renvoie false si le plan ne traverse pas le morceau.
    /// </summary>
    static bool Slice(Piece src, Vector3 origin, Vector3 normal, int capSub, bool closeCut, out Piece a, out Piece b)
    {
        int subCount = src.tris.Length;
        a = new Piece(subCount);
        b = new Piece(subCount);

        int vc = src.verts.Count;
        var dist = new float[vc];
        var mapA = new int[vc];
        var mapB = new int[vc];
        for (int i = 0; i < vc; i++)
        {
            dist[i] = Vector3.Dot(normal, src.verts[i] - origin);
            mapA[i] = -1;
            mapB[i] = -1;
        }

        var cut = new List<Vector3>(); // points du contour de la coupe

        for (int s = 0; s < subCount; s++)
        {
            List<int> t = src.tris[s];
            for (int i = 0; i < t.Count; i += 3)
            {
                int i0 = t[i], i1 = t[i + 1], i2 = t[i + 2];
                bool p0 = dist[i0] >= 0f, p1 = dist[i1] >= 0f, p2 = dist[i2] >= 0f;

                // Triangle entièrement d'un seul côté : simple copie.
                if (p0 == p1 && p1 == p2)
                {
                    Piece dst = p0 ? a : b;
                    int[] map = p0 ? mapA : mapB;
                    dst.tris[s].Add(Copy(src, dst, map, i0));
                    dst.tris[s].Add(Copy(src, dst, map, i1));
                    dst.tris[s].Add(Copy(src, dst, map, i2));
                    continue;
                }

                // Triangle traversé : on isole le sommet seul de son côté (en gardant le sens du triangle).
                int la, lb, lc;
                if (p0 != p1 && p0 != p2) { la = i0; lb = i1; lc = i2; }
                else if (p1 != p0 && p1 != p2) { la = i1; lb = i2; lc = i0; }
                else { la = i2; lb = i0; lc = i1; }

                bool loneInA = dist[la] >= 0f;
                Piece lone = loneInA ? a : b;
                Piece pair = loneInA ? b : a;
                int[] mapLone = loneInA ? mapA : mapB;
                int[] mapPair = loneInA ? mapB : mapA;

                float tab = dist[la] / (dist[la] - dist[lb]);
                float tac = dist[la] / (dist[la] - dist[lc]);

                Vector3 vab = Vector3.Lerp(src.verts[la], src.verts[lb], tab);
                Vector3 vac = Vector3.Lerp(src.verts[la], src.verts[lc], tac);
                Vector3 nab = Vector3.Lerp(src.normals[la], src.normals[lb], tab).normalized;
                Vector3 nac = Vector3.Lerp(src.normals[la], src.normals[lc], tac).normalized;
                Vector2 uab = Vector2.Lerp(src.uvs[la], src.uvs[lb], tab);
                Vector2 uac = Vector2.Lerp(src.uvs[la], src.uvs[lc], tac);

                // Côté du sommet seul : un triangle.
                int l0 = Copy(src, lone, mapLone, la);
                int l1 = lone.Add(vab, nab, uab);
                int l2 = lone.Add(vac, nac, uac);
                lone.tris[s].Add(l0); lone.tris[s].Add(l1); lone.tris[s].Add(l2);

                // Autre côté : un quadrilatère, soit deux triangles.
                int q0 = pair.Add(vab, nab, uab);
                int q1 = Copy(src, pair, mapPair, lb);
                int q2 = Copy(src, pair, mapPair, lc);
                int q3 = pair.Add(vac, nac, uac);
                pair.tris[s].Add(q0); pair.tris[s].Add(q1); pair.tris[s].Add(q2);
                pair.tris[s].Add(q0); pair.tris[s].Add(q2); pair.tris[s].Add(q3);

                cut.Add(vab);
                cut.Add(vac);
            }
        }

        if (!a.HasTriangles || !b.HasTriangles) return false;

        if (closeCut) BuildCap(a, b, cut, normal, capSub);
        a.RecalcBounds();
        b.RecalcBounds();
        return true;
    }

    static int Copy(Piece src, Piece dst, int[] map, int i)
    {
        if (map[i] < 0) map[i] = dst.Add(src.verts[i], src.normals[i], src.uvs[i]);
        return map[i];
    }

    /// <summary>Referme la coupe des deux côtés (éventail de triangles autour du centre du contour).</summary>
    static void BuildCap(Piece a, Piece b, List<Vector3> cut, Vector3 normal, int capSub)
    {
        if (cut.Count < 3) return;

        Vector3 center = Vector3.zero;
        for (int i = 0; i < cut.Count; i++) center += cut[i];
        center /= cut.Count;

        // Repère 2D dans le plan de coupe.
        Vector3 u = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        Vector3 v = Vector3.Cross(normal, u);

        // Tri des points par angle autour du centre.
        Vector3[] pts = cut.ToArray();
        var angles = new float[pts.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            Vector3 d = pts[i] - center;
            angles[i] = Mathf.Atan2(Vector3.Dot(d, v), Vector3.Dot(d, u));
        }
        System.Array.Sort(angles, pts);

        // Suppression des doublons (chaque point de coupe est partagé par deux triangles).
        var ring = new List<Vector3>(pts.Length);
        for (int i = 0; i < pts.Length; i++)
        {
            if (ring.Count > 0 && (pts[i] - ring[ring.Count - 1]).sqrMagnitude < 1e-10f) continue;
            ring.Add(pts[i]);
        }
        if (ring.Count > 1 && (ring[0] - ring[ring.Count - 1]).sqrMagnitude < 1e-10f)
            ring.RemoveAt(ring.Count - 1);
        if (ring.Count < 3) return;

        // Sens de parcours du contour par rapport à la normale du plan.
        float orient = 0f;
        for (int i = 0; i < ring.Count; i++)
        {
            Vector3 p0 = ring[i] - center;
            Vector3 p1 = ring[(i + 1) % ring.Count] - center;
            orient += Vector3.Dot(Vector3.Cross(p0, p1), normal);
        }

        // La face de coupe de "a" regarde vers -normal, celle de "b" vers +normal.
        AddFan(a, ring, center, -normal, u, v, capSub, orient > 0f);
        AddFan(b, ring, center, normal, u, v, capSub, orient <= 0f);
    }

    static void AddFan(Piece p, List<Vector3> ring, Vector3 center, Vector3 n,
                       Vector3 u, Vector3 v, int sub, bool flip)
    {
        int c = p.Add(center, n, new Vector2(Vector3.Dot(center, u), Vector3.Dot(center, v)));
        int first = p.verts.Count;
        for (int i = 0; i < ring.Count; i++)
            p.Add(ring[i], n, new Vector2(Vector3.Dot(ring[i], u), Vector3.Dot(ring[i], v)));

        for (int i = 0; i < ring.Count; i++)
        {
            int i0 = first + i;
            int i1 = first + (i + 1) % ring.Count;
            p.tris[sub].Add(c);
            p.tris[sub].Add(flip ? i1 : i0);
            p.tris[sub].Add(flip ? i0 : i1);
        }
    }

    // ------------------------------------------------------------------ Données d'un morceau

    class Piece
    {
        public readonly List<Vector3> verts = new List<Vector3>();
        public readonly List<Vector3> normals = new List<Vector3>();
        public readonly List<Vector2> uvs = new List<Vector2>();
        public readonly List<int>[] tris; // un tableau d'indices par sous-mesh
        public Bounds bounds;
        public int failures;

        public Piece(int subCount)
        {
            tris = new List<int>[subCount];
            for (int i = 0; i < subCount; i++) tris[i] = new List<int>();
        }

        public bool HasTriangles
        {
            get
            {
                for (int i = 0; i < tris.Length; i++)
                    if (tris[i].Count > 0) return true;
                return false;
            }
        }

        public int Add(Vector3 v, Vector3 n, Vector2 uv)
        {
            verts.Add(v);
            normals.Add(n);
            uvs.Add(uv);
            return verts.Count - 1;
        }

        public void RecalcBounds()
        {
            if (verts.Count == 0) { bounds = default; return; }
            var bb = new Bounds(verts[0], Vector3.zero);
            for (int i = 1; i < verts.Count; i++) bb.Encapsulate(verts[i]);
            bounds = bb;
        }

        public static Piece FromMesh(Mesh mesh, int subCount, out bool hadNormals)
        {
            var p = new Piece(subCount);
            mesh.GetVertices(p.verts);
            mesh.GetNormals(p.normals);
            mesh.GetUVs(0, p.uvs);

            hadNormals = p.normals.Count == p.verts.Count;
            if (!hadNormals)
            {
                p.normals.Clear();
                for (int i = 0; i < p.verts.Count; i++) p.normals.Add(Vector3.up);
            }
            if (p.uvs.Count != p.verts.Count)
            {
                p.uvs.Clear();
                for (int i = 0; i < p.verts.Count; i++) p.uvs.Add(Vector2.zero);
            }

            for (int s = 0; s < mesh.subMeshCount && s < subCount; s++)
                mesh.GetTriangles(p.tris[s], s);

            p.RecalcBounds();
            return p;
        }
    }
}