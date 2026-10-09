using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;

/// <summary>
/// Fragmentation d'un mesh à l'exécution, par coupes planes successives.
/// Les coupes sont concentrées autour du point d'impact : petits éclats près du choc,
/// gros morceaux plus loin.
///
/// Version optimisée pour casque autonome (Quest 2 / 3) :
///  - les découpes sont calculées à l'avance, sur des fils d'exécution secondaires, puis réutilisées :
///    aucun calcul lourd au moment où un objet casse ;
///  - les fragments sont de simples objets recyclés (jamais créés ni détruits en plein jeu), sans aucun
///    script dessus : pas de message de collision, pas de mise à jour par fragment ;
///  - les effets de particules et les sons passent par des systèmes partagés, créés une seule fois ;
///  - sur casque autonome, des limites s'appliquent d'elles-mêmes (nombre de fragments, durée de vie,
///    colliders simples, pas d'ombres), sans toucher aux réglages de chaque objet. Voir "Profile".
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
///  - Conseillé : créer un layer nommé "Debris" dans le projet. Les fragments y sont alors placés et ne se
///    heurtent plus entre eux, ce qui allège fortement la physique quand beaucoup d'objets sont cassés.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class RuntimeFracture : MonoBehaviour, IPointVelocity
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

    public enum Profile
    {
        [InspectorName("Automatique (selon l'appareil)")] Automatique,
        [InspectorName("PC (aucune limite)")] PC,
        [InspectorName("Casque autonome (limites Quest)")] Quest
    }

    // ------------------------------------------------------------------ Limites sur casque autonome
    // Communes à tous les objets. Modifiables depuis un script, avant le chargement de la scène de jeu.

    /// <summary>Nombre maximal de fragments par objet cassé.</summary>
    public static int QuestMaxFragments = 8;
    /// <summary>Nombre maximal de fragments présents en même temps dans la scène.</summary>
    public static int QuestFragmentBudget = 60;
    /// <summary>Durée de vie maximale des débris (secondes).</summary>
    public static float QuestMaxLifetime = 5f;
    /// <summary>Nombre maximal de découpes gardées en mémoire par modèle.</summary>
    public static int QuestCacheVariants = 2;
    /// <summary>Layer des fragments, s'il existe dans le projet : ils ne se heurtent alors plus entre eux.</summary>
    public static string DebrisLayerName = "Debris";

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
    [Tooltip("Puissance de frappe : multiplie la vitesse des coups que cet objet porte aux autres. "
           + "2 = un coup à 2,5 m/s compte comme 5 m/s. À monter sur les outils lourds (pied-de-biche, masse), 1 pour le reste.")]
    [Range(0.5f, 5f)] public float impactPower = 1f;

    [Header("Découpe")]
    [Tooltip("Nombre de fragments visé. Sur casque autonome, il est plafonné (8 par défaut).")]
    [Range(2, 64)] public int fragmentCount = 12;
    [Tooltip("0 = découpe uniforme, 1 = coupes très concentrées autour de l'impact.")]
    [Range(0f, 1f)] public float impactFocus = 0.7f;
    [Tooltip("Matériau des faces intérieures (la tranche). Si vide, elles prennent le matériau de l'objet "
           + "et chaque fragment se dessine en une seule passe : c'est le réglage le plus léger.")]
    public Material insideMaterial;
    [Tooltip("Objet creux (bouteille, verre, vase...) : les coupes ne sont pas rebouchées, "
           + "les fragments restent des éclats fins au lieu de blocs pleins.")]
    public bool hollow;

    [Header("Fragments")]
    [Tooltip("Vitesse (m/s) ajoutée aux fragments, en s'éloignant du point d'impact.")]
    public float scatterSpeed = 1.5f;
    [Tooltip("Durée de vie des débris en secondes (0 = permanents). Sur casque autonome, elle est plafonnée (5 s par défaut).")]
    public float debrisLifetime = 8f;
    [Tooltip("Variation aléatoire (± secondes) appliquée à la durée de vie de chaque fragment.")]
    [Min(0f)] public float debrisLifetimeVariance = 2f;
    [Tooltip("Les fragments plus petits que cette taille (en mètres) ne sont pas créés.")]
    public float minFragmentSize = 0.02f;
    [Tooltip("Vrai : MeshCollider convexe (précis). Faux : BoxCollider (bien plus léger). "
           + "Sur casque autonome, ce sont toujours des BoxCollider.")]
    public bool convexColliders = true;
    [Tooltip("Les fragments projettent et reçoivent des ombres. Toujours désactivé sur casque autonome.")]
    public bool fragmentShadows = true;
    [Tooltip("Recalcule les tangentes des fragments, nécessaires aux matériaux avec normal map. "
           + "Toujours désactivé sur casque autonome.")]
    public bool recalculateTangents = true;
    [Tooltip("Nombre de fois où les fragments peuvent eux-mêmes se recasser. Toujours 0 sur casque autonome.")]
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
    [Tooltip("Tes propres prefabs d'effets, créés au point d'impact puis supprimés au bout de 6 secondes (optionnel). "
           + "Contrairement aux effets intégrés, ils sont créés à chaque casse : à éviter sur casque autonome.")]
    public GameObject[] customEffects;

    [Header("Optimisation")]
    [Tooltip("Automatique : les limites du casque autonome s'appliquent sur Quest, pas sur PC. "
           + "Casque autonome : les applique partout, pour voir le rendu Quest dans l'éditeur.")]
    public Profile profile = Profile.Automatique;
    [Tooltip("Nombre de découpes différentes gardées en mémoire par modèle. Un objet qui casse réutilise celle dont le point "
           + "d'impact est le plus proche. 0 = recalculer à chaque casse (déconseillé).")]
    [Range(0, 8)] public int cacheVariants = 3;
    [Tooltip("Calcule ces découpes dès le chargement de la scène, en arrière-plan, pour qu'aucun calcul n'ait lieu pendant le jeu. "
           + "Toujours actif sur casque autonome.")]
    public bool prepareAtLoad = true;
    [Tooltip("Nombre maximal de fragments présents en même temps dans la scène, tous objets confondus. "
           + "Au-delà, les plus anciens disparaissent. 0 = pas de limite. Sur casque autonome, il est plafonné (60 par défaut).")]
    [Min(0)] public int fragmentBudget = 200;
    [Tooltip("Quand la scène se remplit de débris, seuls les plus gros morceaux de chaque objet sont créés, jusqu'à ce minimum.")]
    [Range(2, 16)] public int minFragmentCount = 3;

    [Header("Son")]
    [Tooltip("Nom du dossier dans Assets/Sounds/ (rempli par le preset, modifiable). "
           + "Les clips SO_[matériau]_n de ce dossier sont chargés automatiquement dans la liste ci-dessous "
           + "(ceux nommés _Impact_ ou _Slide_ sont réservés au script RuntimeImpactSound).")]
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

    // ------------------------------------------------------------------ État de l'objet

    Rigidbody rb;
    int ignoreMask;
    bool canBreak = true;
    bool broken;
    bool isFragment;        // fragment capable de se recasser (les autres fragments n'ont aucun script)
    bool fragmentMeshIsUnique;
    float armedAt;

    // Réglages effectifs, une fois les limites du casque autonome appliquées.
    bool quest;
    int effCount, effBudget, effVariants, effRefracture;
    float effLifetime;
    bool effConvex, effShadows, effTangents, effPrewarm;

    // Poses des derniers pas physiques : vitesse réelle de l'objet quand il est tenu en main (kinematic),
    // cas où le moteur physique ne la connaît pas. Elles ne sont relevées que dans ce cas.
    readonly Matrix4x4[] poses = new Matrix4x4[4];
    int poseHead;
    bool tracking;

    // Vitesse du coup reçu par Hit() : les fragments partent en partie dans son sens.
    Vector3 hitVelocity;
    float lastHitLog = -10f;

    /// <summary>Vrai dès que l'objet a cassé (il est supprimé à la fin de l'image).</summary>
    public bool IsBroken => broken;

    // Sons de choc transmis aux plus gros fragments (si l'objet porte RuntimeImpactSound).
    RuntimeImpactSound impactSoundSource;
    int soundFragmentsLeft;
    float soundShareThreshold;

    /// <summary>Mis à vrai par RuntimeCrush : le mesh de cet objet est déformé, donc propre à lui, et ne doit pas être mis en cache.</summary>
    [System.NonSerialized] public bool hasUniqueMesh;

    // ------------------------------------------------------------------ Données partagées entre tous les objets

    /// <summary>Un morceau prêt à l'emploi : son mesh, son encombrement et sa part du volume de l'objet.</summary>
    class FragmentData
    {
        public Mesh mesh;
        public Bounds bounds;
        public float volumeShare;
        public float span;       // plus grande dimension du morceau
        public float thickness;  // épaisseur dans sa direction la plus mince (0 : morceau parfaitement plat)
    }

    /// <summary>Une découpe complète d'un modèle, morceaux classés du plus gros au plus petit.</summary>
    class FractureResult
    {
        public readonly List<FragmentData> fragments = new List<FragmentData>();
        public int count;
        public int capSub;
        public bool hollow;
        public bool shared;          // vrai : meshes gardés en cache, à ne pas détruire avec les fragments
        public Vector3 localImpact;  // point d'impact pour lequel cette découpe a été calculée
    }

    /// <summary>Une découpe demandée à un fil secondaire : seul le calcul géométrique s'y fait, jamais d'appel à Unity.</summary>
    class SliceJob
    {
        public Mesh src;
        public Piece root;
        public bool hadNormals;
        public int capSub, count, variants, seed;
        public bool hollow, tangents, keepReadable;
        public float focus;
        public Vector3 localImpact;
        public List<Piece> pieces;   // résultat
        public bool failed;
    }

    /// <summary>Un fragment en scène. Les fragments ordinaires sont recyclés ; ceux qui portent un script sont détruits.</summary>
    class Frag
    {
        public GameObject go;
        public Transform tf;
        public MeshFilter mf;
        public MeshRenderer mr;
        public Rigidbody rb;
        public BoxCollider box;
        public MeshCollider mc;
        public float dieAt;
        public int kind;             // PlainFragment, SoundFragment ou ScriptedFragment
        public RuntimeImpactSound sound;
        public Mesh ownedMesh;       // mesh propre à ce fragment (hors cache), à libérer avec lui
        public float solidity;       // solidité de l'objet dont il provient
    }

    /// <summary>Compare les Rigidbody par référence : valable même pour un objet déjà détruit, et sans appel à Unity.</summary>
    sealed class BodyComparer : IEqualityComparer<Rigidbody>
    {
        public bool Equals(Rigidbody a, Rigidbody b) => ReferenceEquals(a, b);
        public int GetHashCode(Rigidbody body) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(body);
    }

    static readonly Dictionary<Mesh, List<FractureResult>> cache = new Dictionary<Mesh, List<FractureResult>>();
    static readonly List<SliceJob> pending = new List<SliceJob>();                       // demandées, pas encore rangées
    static readonly ConcurrentQueue<SliceJob> finished = new ConcurrentQueue<SliceJob>(); // calculées, meshes à créer

    static readonly List<Frag> live = new List<Frag>();   // du plus ancien au plus récent
    // Deux bassins de fragments recyclés : les ordinaires (aucun script) et ceux qui portent le script de sons de choc.
    // Les garder séparés évite qu'à la longue tous les fragments recyclés se retrouvent avec un script.
    const int PlainFragment = 0, SoundFragment = 1, ScriptedFragment = 2;
    static readonly Stack<Frag> pool = new Stack<Frag>();
    static readonly Stack<Frag> soundPool = new Stack<Frag>();
    // Retrouve un fragment d'après son Rigidbody : les fragments n'ont pas de script, mais gardent la solidité de leur objet.
    static readonly Dictionary<Rigidbody, Frag> fragmentOf = new Dictionary<Rigidbody, Frag>(new BodyComparer());
    static readonly List<MeshFilter> filterBuffer = new List<MeshFilter>();

    static readonly System.Random mainRandom = new System.Random(20261009);
    static int seedCounter;
    static int poolTarget;           // nombre de fragments à tenir prêts
    static bool poolWithBoxes;       // les fragments préparés reçoivent déjà leur BoxCollider
    static int lastTick = -1;
    static int debrisLayer = -2;     // -2 : pas encore cherché, -1 : layer absent du projet

    /// <summary>Nombre de fragments actuellement présents dans la scène.</summary>
    public static int LiveFragments => live.Count;
    /// <summary>Nombre de découpes encore en cours de calcul en arrière-plan (0 : tout est prêt).</summary>
    public static int PendingSlices => pending.Count;
    /// <summary>Nombre de fragments en attente de réutilisation.</summary>
    public static int PooledFragments => pool.Count + soundPool.Count;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        cache.Clear();
        pending.Clear();
        while (finished.TryDequeue(out SliceJob _)) { }
        live.Clear();
        pool.Clear();
        soundPool.Clear();
        fragmentOf.Clear();
        fxSystems.Clear();
        voices = null;
        particleMaterial = null;
        particleWarningShown = false;
        lastTick = -1;
        debrisLayer = -2;
        poolTarget = 0;
        poolWithBoxes = false;

        Application.onBeforeRender -= Tick;
        Application.onBeforeRender += Tick;
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
        pending.Clear();
    }

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
        if (Application.isPlaying) ResolveSettings(); // prise en compte immédiate d'un réglage changé en cours de jeu
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
                impactPower = 2f;
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
        impactPower = 1f;
    }

    // ------------------------------------------------------------------ Cycle de vie

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        ResolveSettings();
    }

    /// <summary>Calcule les réglages effectifs : ceux de l'inspecteur, plafonnés sur casque autonome.</summary>
    void ResolveSettings()
    {
        ignoreMask = ignoreLayers.value;
        quest = profile == Profile.Quest || (profile == Profile.Automatique && Application.isMobilePlatform);

        effCount = Mathf.Max(2, fragmentCount);
        effBudget = fragmentBudget;
        effVariants = cacheVariants;
        effLifetime = debrisLifetime;
        effConvex = convexColliders;
        effShadows = fragmentShadows;
        effTangents = recalculateTangents;
        effRefracture = refractureDepth;
        effPrewarm = prepareAtLoad;

        if (!quest) return;

        effCount = Mathf.Min(effCount, Mathf.Max(2, QuestMaxFragments));
        effBudget = effBudget > 0 ? Mathf.Min(effBudget, QuestFragmentBudget) : QuestFragmentBudget;
        effVariants = Mathf.Clamp(effVariants, 1, Mathf.Max(1, QuestCacheVariants)); // jamais 0 : pas de calcul à chaque casse
        effLifetime = effLifetime > 0f ? Mathf.Min(effLifetime, QuestMaxLifetime) : QuestMaxLifetime;
        effConvex = false;       // la préparation d'un collider convexe coûte cher à chaque nouveau mesh
        effShadows = false;
        effTangents = false;
        effRefracture = 0;
        effPrewarm = true;
    }

    void Start()
    {
        // Découpes préparées en arrière-plan dès le chargement : plus aucun calcul au moment de la casse.
        // (Dans Start et non Awake : RuntimeCrush a eu le temps de signaler un mesh déformable.)
        if (!effPrewarm || indestructible || !canBreak || isFragment) return;

        // Fragments tenus prêts (créés quelques-uns par image), systèmes d'effets et layer des débris :
        // tout ce qui serait sinon créé à la première casse.
        PurgeDestroyed();
        poolTarget = Mathf.Max(poolTarget, Mathf.Min(effBudget > 0 ? effBudget : 48, 96));
        if (!effConvex) poolWithBoxes = true;
        DebrisLayer();
        WarmUpEffects();

        if (effVariants <= 0 || hasUniqueMesh) return;

        GetComponentsInChildren(false, filterBuffer);
        for (int i = 0; i < filterBuffer.Count; i++)
        {
            Mesh src = filterBuffer[i].sharedMesh;
            if (src == null || !src.isReadable) continue;
            RequestVariants(src, CapSubmesh(src), effVariants);
        }
        filterBuffer.Clear();
    }

    /// <summary>
    /// Sous-mesh qui reçoit les faces intérieures. Sans matériau intérieur, elles rejoignent le dernier sous-mesh
    /// existant : le fragment se dessine alors en autant de passes que l'objet d'origine, pas une de plus.
    /// </summary>
    int CapSubmesh(Mesh src)
    {
        bool extra = insideMaterial != null && !isFragment;
        return extra ? src.subMeshCount : Mathf.Max(0, src.subMeshCount - 1);
    }

    // ------------------------------------------------------------------ Vitesse réelle de l'objet

    void FixedUpdate()
    {
        if (lastTick != Time.frameCount) Tick(); // filet de sécurité si le rappel d'avant-rendu n'arrive pas

        // Les poses ne sont relevées que pour un objet tenu en main : sinon le moteur physique connaît sa vitesse.
        if (!rb.isKinematic)
        {
            tracking = false;
            return;
        }

        Matrix4x4 m = transform.localToWorldMatrix;
        if (!tracking)
        {
            tracking = true;
            for (int i = 0; i < poses.Length; i++) poses[i] = m;
        }
        poseHead = (poseHead + 1) % poses.Length;
        poses[poseHead] = m;
    }

    /// <summary>
    /// Vitesse (monde) d'un point de l'objet, rotation comprise : le bout d'une bouteille qu'on balance
    /// va plus vite que sa base. Valable aussi quand l'objet est tenu en main.
    /// </summary>
    public Vector3 PointVelocity(Vector3 worldPoint)
    {
        if (!tracking) return rb != null ? rb.GetPointVelocity(worldPoint) : Vector3.zero;

        Matrix4x4 newest = poses[poseHead];
        Matrix4x4 oldest = poses[(poseHead + 1) % poses.Length];
        Vector3 local = newest.inverse.MultiplyPoint3x4(worldPoint);
        float span = (poses.Length - 1) * Time.fixedDeltaTime;
        return (worldPoint - oldest.MultiplyPoint3x4(local)) / span;
    }

    // ------------------------------------------------------------------ Déclenchement

    void OnCollisionEnter(Collision col)
    {
        // Les tests sont rangés du moins cher au plus cher : la plupart des contacts s'arrêtent dans les premières lignes.
        if (!canBreak || indestructible || broken) return;
        if (ignoreMask != 0 && (ignoreMask & (1 << col.collider.gameObject.layer)) != 0) return;
        if (Time.time < armedAt) return;

        Rigidbody otherRb = col.rigidbody;
        bool selfHeld = rb.isKinematic;
        bool otherHeld = otherRb != null && otherRb.isKinematic;

        // Solidité et puissance de ce qu'on a percuté : sans ce script, l'autre objet est "infiniment" solide.
        RuntimeFracture other = null;
        if (otherRb != null) otherRb.TryGetComponent(out other);
        float power = other != null ? other.impactPower : 1f;

        float physicsSpeed = col.relativeVelocity.magnitude;

        // Contact lent entre deux objets libres (objets qui se posent, roulent, se bousculent) : inutile d'aller plus loin.
        if (!selfHeld && !otherHeld && !debugLog && physicsSpeed * power < breakVelocity * 0.25f) return;
        if (col.contactCount == 0) return;

        ContactPoint contact = col.GetContact(0);
        Vector3 point = contact.point;

        // Normale orientée de l'obstacle vers cet objet.
        Vector3 n = contact.normal;
        if (Vector3.Dot(n, rb.worldCenterOfMass - point) < 0f) n = -n;

        // Vitesse d'approche au point de contact, rotation comprise. On ne compte que le rapprochement,
        // pour ne pas casser un objet qu'on soulève d'une table.
        Vector3 otherVel = Vector3.zero;
        if (otherRb != null)
        {
            if (other != null) otherVel = other.PointVelocity(point);
            else if (otherHeld && otherRb.TryGetComponent(out IPointVelocity tracker)) otherVel = tracker.PointVelocity(point);
            else otherVel = otherRb.GetPointVelocity(point);
        }
        float trackedSpeed = Mathf.Max(0f, -Vector3.Dot(PointVelocity(point) - otherVel, n));

        float speed = Mathf.Max(physicsSpeed, trackedSpeed) * power;
        // Un débris garde la solidité de l'objet dont il vient : un éclat de verre ne casse pas plus solide que lui.
        float otherSolidity = Mathf.Infinity;
        if (other != null) otherSolidity = other.solidity;
        else if (otherRb != null && fragmentOf.TryGetValue(otherRb, out Frag debris)) otherSolidity = debris.solidity;

        if (debugLog)
            Debug.Log($"[RuntimeFracture] '{name}' contre '{col.collider.name}' : {speed:F1} m/s "
                    + $"(physique {physicsSpeed:F1}, suivi {trackedSpeed:F1}, puissance x{power:F1}, seuil {breakVelocity:F1}) "
                    + $"— solidité {solidity} contre {otherSolidity}", this);

        if (speed < breakVelocity) return;      // choc trop faible
        if (otherSolidity < solidity) return;   // l'autre est plus fragile : c'est lui qui casse
        Break(point);
    }

    /// <summary>
    /// Coup porté par un autre script, par exemple une arme dont le bout a traversé l'objet entre deux pas physiques.
    /// velocity : vitesse du point qui frappe (monde). Casse l'objet si elle suffit et si l'attaquant est au moins
    /// aussi solide. Renvoie vrai si l'objet a cassé.
    /// </summary>
    public bool Hit(Vector3 worldPoint, Vector3 velocity, float attackerSolidity = Mathf.Infinity, float power = 1f)
    {
        if (indestructible || !canBreak || broken || Time.time < armedAt) return false;

        float speed = velocity.magnitude * power;
        bool breaks = speed >= breakVelocity && attackerSolidity >= solidity;

        // Une arme signale le coup à chaque pas physique tant qu'elle traverse l'objet : le journal est espacé.
        if (debugLog && (breaks || Time.time - lastHitLog >= 0.25f))
        {
            lastHitLog = Time.time;
            Debug.Log($"[RuntimeFracture] '{name}' frappé à {speed:F1} m/s "
                    + $"(vitesse réelle {velocity.magnitude:F1}, puissance x{power:F1}, seuil {breakVelocity:F1}) "
                    + $"— solidité {solidity} contre {attackerSolidity}", this);
        }

        if (!breaks) return false;

        hitVelocity = velocity;
        Break(worldPoint);
        return broken;
    }

    /// <summary>Clic droit sur le composant en mode Play : casse l'objet sans attendre de collision.</summary>
    [ContextMenu("Casser maintenant (test)")]
    void BreakNow()
    {
        if (Application.isPlaying) Break(transform.position);
    }

    // ------------------------------------------------------------------ Casse

    /// <summary>Casse l'objet à partir d'un point d'impact en coordonnées monde.</summary>
    public void Break(Vector3 worldImpact)
    {
        if (broken) return;
        if (rb == null) rb = GetComponent<Rigidbody>();

        // Le mesh peut être sur cet objet ou sur un enfant
        // (cas courant des prefabs XR : Rigidbody à la racine, visuel en enfant).
        GetComponentsInChildren(false, filterBuffer);
        int filterCount = filterBuffer.Count;
        float massPerMesh = rb.mass / Mathf.Max(1, filterCount);

        impactSoundSource = GetComponent<RuntimeImpactSound>();
        bool hasImpactSounds = impactSoundSource != null && impactSoundSource.impactSounds != null
                               && impactSoundSource.impactSounds.Length > 0;
        soundFragmentsLeft = hasImpactSounds ? impactSoundSource.fragmentSounds : 0;

        // Charge de la scène : plus il y a de débris, moins on crée de morceaux et moins ils durent.
        float load = effBudget > 0 ? Mathf.Clamp01((float)live.Count / effBudget) : 0f;

        bool hasBounds = false;
        Bounds worldBounds = default;
        int done = 0;

        for (int i = 0; i < filterCount; i++)
        {
            MeshFilter mf = filterBuffer[i];
            if (mf == null || !mf.TryGetComponent(out MeshRenderer mr)) continue;
            if (!FractureMesh(mf, mr, massPerMesh, worldImpact, load)) continue;
            done++;

            if (hasBounds) worldBounds.Encapsulate(mr.bounds);
            else { worldBounds = mr.bounds; hasBounds = true; }
        }
        filterBuffer.Clear();

        if (done == 0)
        {
            Debug.LogWarning($"[RuntimeFracture] Aucun mesh exploitable trouvé sur '{name}' ou ses enfants.", this);
            return;
        }

        broken = true;
        TrimFragments(effBudget);
        PlayAt(breakSounds, worldImpact, soundVolume, pitchVariation);
        SpawnEffects(worldImpact, hasBounds ? worldBounds.center : worldImpact, hasBounds ? worldBounds.size.magnitude : 0.2f, load);
        onBreak?.Invoke(worldImpact);
        Destroy(gameObject);
    }

    bool FractureMesh(MeshFilter mf, MeshRenderer mr, float mass, Vector3 worldImpact, float load)
    {
        Mesh src = mf.sharedMesh;
        if (src == null) return false;
        if (!src.isReadable)
        {
            Debug.LogWarning($"[RuntimeFracture] Le mesh '{src.name}' doit avoir Read/Write activé.", mf);
            return false;
        }

        Transform t = mf.transform;
        int capSub = CapSubmesh(src);
        Vector3 localImpact = t.InverseTransformPoint(worldImpact);

        // --- Découpe : prise dans le cache si possible, calculée sur place sinon.
        bool cacheable = effVariants > 0 && !fragmentMeshIsUnique && !hasUniqueMesh;
        int matching = 0;
        FractureResult result = cacheable ? FromCache(src, effCount, capSub, localImpact, out matching) : null;
        bool reused = result != null;

        string notReady = null;
        if (debugLog && !reused)
            notReady = effVariants <= 0 ? "cache désactivé (Cache Variants à 0)"
                     : !cacheable ? "mesh propre à cet objet"
                     : !effPrewarm ? "préparation au chargement désactivée"
                     : CountPending(src, effCount, capSub, hollow) > 0 ? "préparation pas encore terminée"
                     : "aucune découpe préparée pour ce modèle";

        if (reused)
        {
            // Cache incomplet : la variante manquante est calculée en arrière-plan, pour les prochains objets.
            if (matching + CountPending(src, effCount, capSub, hollow) < effVariants)
                RequestVariants(src, capSub, effVariants);
        }
        else
        {
            result = GenerateNow(src, capSub, localImpact);
            if (cacheable)
            {
                result.shared = true;
                Store(src, result);
            }
        }

        // --- Sous forte charge, seuls les plus gros morceaux sont créés (ils sont classés du plus gros au plus petit).
        int available = result.fragments.Count;
        int limit = SpawnLimit(available, load);

        if (debugLog)
            Debug.Log($"[RuntimeFracture] '{name}' : découpe {(reused ? "réutilisée" : "calculée sur place : " + notReady)}, "
                    + $"{limit} morceaux créés sur {available}, {live.Count} fragments déjà en scène (budget {effBudget})"
                    + (quest ? ", limites casque autonome actives" : ""), this);

        Material[] mats = BuildMaterials(mr.sharedMaterials, capSub);
        Vector3 scale = t.lossyScale;
        float lifeScale = Mathf.Lerp(1f, 0.5f, load);

        // Seuls les fragments plus gros que la moitié de la taille moyenne peuvent hériter des sons de choc.
        soundShareThreshold = 0.5f / Mathf.Max(1, available);

        for (int i = 0; i < available; i++)
        {
            FragmentData d = result.fragments[i];
            if (d.mesh == null) continue;
            bool spawn = i < limit;
            if (spawn)
            {
                Vector3 worldSize = Vector3.Scale(d.bounds.size, scale);
                float maxSize = Mathf.Max(Mathf.Abs(worldSize.x), Mathf.Max(Mathf.Abs(worldSize.y), Mathf.Abs(worldSize.z)));
                spawn = maxSize >= minFragmentSize;
            }

            if (spawn) SpawnFragment(d, result.shared, t, mats, mass * d.volumeShare, worldImpact, lifeScale);
            else if (!result.shared && d.mesh != null) Destroy(d.mesh); // mesh non utilisé et non partagé : on le libère
        }
        return true;
    }

    /// <summary>Nombre de morceaux créés selon la charge de la scène : tous, la moitié, ou le quart.</summary>
    int SpawnLimit(int available, float load)
    {
        if (load < 0.5f) return available;
        int floor = Mathf.Clamp(minFragmentCount, 1, Mathf.Max(1, available));
        if (load < 0.8f) return Mathf.Max(floor, available / 2);
        return Mathf.Max(floor, available / 4);
    }

    Material[] BuildMaterials(Material[] src, int capSub)
    {
        var mats = new Material[capSub + 1];
        for (int i = 0; i <= capSub; i++)
            mats[i] = src.Length > 0 ? src[Mathf.Min(i, src.Length - 1)] : null;
        if (insideMaterial != null && !isFragment && capSub >= src.Length) mats[capSub] = insideMaterial;
        return mats;
    }

    // ------------------------------------------------------------------ Fragments

    void SpawnFragment(FragmentData d, bool shared, Transform t, Material[] mats, float mass, Vector3 worldImpact, float lifeScale)
    {
        bool wantsSound = soundFragmentsLeft > 0 && d.volumeShare >= soundShareThreshold;
        bool refracture = effRefracture > 0;

        // Un fragment ordinaire n'a aucun script. Celui qui fait du bruit en retombant porte le script de sons.
        // Les deux sortes sont recyclées, chacune dans son bassin. Seul un fragment capable de se recasser
        // est un objet à part, détruit en fin de vie.
        Frag f = AcquireFragment(refracture ? ScriptedFragment : wantsSound ? SoundFragment : PlainFragment);
        GameObject go = f.go;

        go.layer = DebrisLayer() >= 0 ? debrisLayer : t.gameObject.layer;
        f.tf.SetPositionAndRotation(t.position, t.rotation);
        f.tf.localScale = t.lossyScale;
        f.solidity = solidity;

        // Collider réglé avant d'attribuer le mesh au MeshFilter. Un collider convexe a besoin d'un mesh
        // encore lisible : sinon (découpe partagée avec un objet réglé autrement), on se rabat sur une boîte.
        // Un morceau plat (éclat de vitre, panneau sans épaisseur) n'a pas d'enveloppe convexe : le moteur physique
        // refuserait de la construire et le fragment n'aurait aucune collision. Il reçoit une boîte lui aussi.
        Vector3 scale = t.lossyScale;
        bool hull = effConvex && d.mesh.isReadable && !IsFlat(d, scale);
        if (hull)
        {
            if (f.mc == null)
            {
                f.mc = go.AddComponent<MeshCollider>();
                f.mc.convex = true;
            }
            f.mc.sharedMesh = d.mesh;
            f.mc.enabled = true;
            if (f.box != null) f.box.enabled = false;
        }
        else
        {
            UseBox(f, d, scale);
        }

        f.mf.sharedMesh = d.mesh;
        f.mr.sharedMaterials = mats;
        f.mr.shadowCastingMode = effShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        f.mr.receiveShadows = effShadows;
        f.ownedMesh = shared ? null : d.mesh;

        // Physique allégée : les débris n'ont pas besoin de la précision d'un objet de jeu.
        Rigidbody frb = f.rb;
        frb.mass = Mathf.Max(0.01f, mass);
        frb.interpolation = quest ? RigidbodyInterpolation.None : rb.interpolation;
        frb.solverIterations = 3;
        frb.solverVelocityIterations = 1;
        frb.sleepThreshold = 0.03f;

        go.SetActive(true);

        // Filet de sécurité : si le moteur physique a tout de même refusé l'enveloppe convexe, le collider
        // est vide (encombrement nul). Le fragment passe alors sur une boîte au lieu de traverser le décor.
        if (hull && f.mc.bounds.size.sqrMagnitude <= 0f) UseBox(f, d, scale);

        Vector3 worldCenter = t.TransformPoint(d.bounds.center);
        Vector3 dir = (worldCenter - worldImpact).normalized;
        Vector3 baseVel = rb.isKinematic ? PointVelocity(worldCenter) : GetVelocity(rb);
        SetVelocity(frb, baseVel + hitVelocity * 0.5f + dir * scatterSpeed);
        frb.angularVelocity = rb.angularVelocity + RandomInSphere(mainRandom) * scatterSpeed;

        float life = float.PositiveInfinity;
        if (effLifetime > 0f)
        {
            float variance = Mathf.Min(debrisLifetimeVariance, effLifetime * 0.5f);
            life = Mathf.Max(0.1f, (effLifetime + Range(mainRandom, -variance, variance)) * lifeScale);
        }
        f.dieAt = Time.time + life;
        live.Add(f);

        // Fragment capable de se recasser : il reçoit ce même script, réglé comme l'objet d'origine.
        if (refracture)
        {
            var child = go.AddComponent<RuntimeFracture>();
            child.CopyFrom(this, !shared);
        }

        // Tintement des gros éclats qui retombent.
        if (wantsSound)
        {
            soundFragmentsLeft--;
            f.sound = impactSoundSource.CopyTo(go, 0.6f);
        }
    }

    /// <summary>
    /// Vrai si le morceau est trop plat pour une enveloppe convexe : épaisseur sous 2 % de sa longueur,
    /// ou sous 3 mm une fois l'échelle de l'objet appliquée.
    /// </summary>
    static bool IsFlat(FragmentData d, Vector3 scale)
    {
        if (d.thickness < d.span * 0.02f) return true;
        float smallest = Mathf.Min(Mathf.Abs(scale.x), Mathf.Min(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
        return d.thickness * smallest < 0.003f;
    }

    /// <summary>Donne au fragment une boîte à la taille du morceau, épaisse d'au moins 4 mm pour ne pas traverser le sol.</summary>
    static void UseBox(Frag f, FragmentData d, Vector3 scale)
    {
        if (f.box == null) f.box = f.go.AddComponent<BoxCollider>();

        Vector3 size = d.bounds.size;
        size.x = Mathf.Max(size.x, MinBoxThickness / Mathf.Max(Mathf.Abs(scale.x), 1e-5f));
        size.y = Mathf.Max(size.y, MinBoxThickness / Mathf.Max(Mathf.Abs(scale.y), 1e-5f));
        size.z = Mathf.Max(size.z, MinBoxThickness / Mathf.Max(Mathf.Abs(scale.z), 1e-5f));

        f.box.center = d.bounds.center;
        f.box.size = size;
        f.box.enabled = true;
        if (f.mc != null) f.mc.enabled = false;
    }

    const float MinBoxThickness = 0.004f;

    /// <summary>Règle un fragment capable de se recasser d'après l'objet dont il provient.</summary>
    void CopyFrom(RuntimeFracture source, bool uniqueMesh)
    {
        isFragment = true;
        fragmentMeshIsUnique = uniqueMesh;
        armedAt = Time.time + 0.25f; // évite la recasse immédiate entre fragments voisins

        profile = source.profile;
        breakVelocity = source.breakVelocity;
        ignoreLayers = source.ignoreLayers;
        solidity = source.solidity;
        fragmentCount = Mathf.Max(2, source.fragmentCount / 3);
        impactFocus = source.impactFocus;
        insideMaterial = source.insideMaterial;
        hollow = source.hollow;
        scatterSpeed = source.scatterSpeed;
        debrisLifetime = source.debrisLifetime;
        debrisLifetimeVariance = source.debrisLifetimeVariance;
        minFragmentSize = source.minFragmentSize;
        convexColliders = source.convexColliders;
        fragmentShadows = source.fragmentShadows;
        recalculateTangents = source.recalculateTangents;
        refractureDepth = Mathf.Max(0, source.refractureDepth - 1);
        cacheVariants = source.cacheVariants;
        prepareAtLoad = false;
        fragmentBudget = source.fragmentBudget;
        minFragmentCount = source.minFragmentCount;
        breakSounds = source.breakSounds;
        soundVolume = source.soundVolume * 0.6f; // un fragment qui recasse fait moins de bruit
        pitchVariation = source.pitchVariation;
        debugLog = source.debugLog;

        ResolveSettings();
    }

    /// <summary>Layer des fragments : "Debris" s'il existe, auquel cas les fragments ne se heurtent plus entre eux.</summary>
    static int DebrisLayer()
    {
        if (debrisLayer != -2) return debrisLayer;

        debrisLayer = string.IsNullOrEmpty(DebrisLayerName) ? -1 : LayerMask.NameToLayer(DebrisLayerName);
        if (debrisLayer >= 0) Physics.IgnoreLayerCollision(debrisLayer, debrisLayer, true);
        return debrisLayer;
    }

    static Frag AcquireFragment(int kind)
    {
        Frag f = null;
        if (kind != ScriptedFragment)
        {
            Stack<Frag> source = kind == SoundFragment ? soundPool : pool;
            while (source.Count > 0 && f == null)
            {
                Frag candidate = source.Pop();
                if (candidate.go != null) f = candidate; // un objet du bassin a pu disparaître avec sa scène
                else fragmentOf.Remove(candidate.rb);
            }
        }

        if (f == null) f = CreateFragment();

        f.kind = kind;
        return f;
    }

    static Frag CreateFragment()
    {
        var go = new GameObject("Fragment");
        go.SetActive(false);
        var f = new Frag { go = go, tf = go.transform };
        f.mf = go.AddComponent<MeshFilter>();
        f.mr = go.AddComponent<MeshRenderer>();
        f.rb = go.AddComponent<Rigidbody>();
        fragmentOf[f.rb] = f;
        return f;
    }

    static void ReleaseFragment(Frag f)
    {
        if (f.ownedMesh != null)
        {
            Destroy(f.ownedMesh);
            f.ownedMesh = null;
        }
        if (f.go == null)
        {
            fragmentOf.Remove(f.rb); // disparu avec sa scène
            return;
        }

        Stack<Frag> target = f.kind == SoundFragment ? soundPool : pool;
        if (f.kind == ScriptedFragment || target.Count >= 256)
        {
            fragmentOf.Remove(f.rb);
            Destroy(f.go);
            return;
        }

        if (f.sound != null) f.sound.impactSounds = null; // script de sons mis en sommeil jusqu'à la réutilisation
        f.go.SetActive(false);
        f.mf.sharedMesh = null;
        if (f.mc != null) f.mc.sharedMesh = null;
        target.Push(f);
    }

    /// <summary>
    /// Changement de scène : les fragments rangés ont disparu avec l'ancienne. On ne garde que ceux qui existent encore.
    /// </summary>
    static void PurgeDestroyed()
    {
        bool stale = (pool.Count > 0 && pool.Peek().go == null) || (soundPool.Count > 0 && soundPool.Peek().go == null);
        if (!stale) return;

        fragmentOf.Clear();
        KeepAlive(pool);
        KeepAlive(soundPool);
        for (int i = 0; i < live.Count; i++)
            if (live[i].go != null) fragmentOf[live[i].rb] = live[i];
    }

    static void KeepAlive(Stack<Frag> stack)
    {
        Frag[] all = stack.ToArray(); // du dessus vers le fond
        stack.Clear();
        for (int i = all.Length - 1; i >= 0; i--)
        {
            if (all[i].go == null) continue;
            stack.Push(all[i]);
            fragmentOf[all[i].rb] = all[i];
        }
    }

    /// <summary>Supprime les fragments les plus anciens quand le budget global est dépassé.</summary>
    static void TrimFragments(int budget)
    {
        if (budget <= 0) return;
        while (live.Count > budget)
        {
            Frag oldest = live[0];
            live.RemoveAt(0);
            ReleaseFragment(oldest);
        }
    }

    // ------------------------------------------------------------------ Entretien, une fois par image

    /// <summary>
    /// Appelé une fois par image, juste avant le rendu : retire les débris arrivés en fin de vie et range
    /// les découpes terminées en arrière-plan. C'est le seul travail récurrent du script, quel que soit
    /// le nombre de fragments.
    /// </summary>
    static void Tick()
    {
        if (!Application.isPlaying) return;

        int frame = Time.frameCount;
        if (frame == lastTick) return;
        lastTick = frame;

        float now = Time.time;
        for (int i = live.Count - 1; i >= 0; i--)
        {
            Frag f = live[i];
            if (f.go != null && now < f.dieAt) continue;
            live.RemoveAt(i);
            ReleaseFragment(f);
        }

        // Bassin de fragments rempli au fil des images, trois par image, jusqu'au nombre voulu.
        // (Le registre compte les fragments qui existent, en scène ou rangés.)
        PurgeDestroyed();
        for (int n = 0; n < 3 && fragmentOf.Count < poolTarget; n++)
        {
            Frag f = CreateFragment();
            if (poolWithBoxes) f.box = f.go.AddComponent<BoxCollider>();
            f.kind = PlainFragment;
            pool.Push(f);
        }

        // Une seule découpe rangée par image : la création de ses meshes est le seul coût sur le fil principal.
        if (finished.TryDequeue(out SliceJob job)) FinishJob(job);
    }

    // ------------------------------------------------------------------ Cache de découpes

    static int CountMatching(Mesh src, int count, int capSub, bool isHollow)
    {
        if (!cache.TryGetValue(src, out List<FractureResult> list)) return 0;
        int n = 0;
        for (int i = 0; i < list.Count; i++)
        {
            FractureResult r = list[i];
            if (r.count == count && r.capSub == capSub && r.hollow == isHollow) n++;
        }
        return n;
    }

    static int CountPending(Mesh src, int count, int capSub, bool isHollow)
    {
        int n = 0;
        for (int i = 0; i < pending.Count; i++)
        {
            SliceJob j = pending[i];
            if (j.src == src && j.count == count && j.capSub == capSub && j.hollow == isHollow) n++;
        }
        return n;
    }

    /// <summary>
    /// Renvoie la découpe en cache dont le point d'impact est le plus proche, ou null s'il n'y en a aucune.
    /// Dès qu'une découpe existe, elle est réutilisée : on ne calcule jamais sur place ce qui peut attendre.
    /// </summary>
    FractureResult FromCache(Mesh src, int count, int capSub, Vector3 localImpact, out int matching)
    {
        matching = 0;
        if (!cache.TryGetValue(src, out List<FractureResult> list)) return null;

        FractureResult best = null;
        float bestDist = float.MaxValue;

        for (int i = 0; i < list.Count; i++)
        {
            FractureResult r = list[i];
            if (r.count != count || r.capSub != capSub || r.hollow != hollow) continue;
            matching++;
            float d = (r.localImpact - localImpact).sqrMagnitude;
            if (d < bestDist) { bestDist = d; best = r; }
        }
        return best;
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

    // ------------------------------------------------------------------ Découpes en arrière-plan

    /// <summary>Demande les découpes qui manquent pour ce modèle. Le calcul se fait sur un fil secondaire.</summary>
    void RequestVariants(Mesh src, int capSub, int wanted)
    {
        int have = CountMatching(src, effCount, capSub, hollow) + CountPending(src, effCount, capSub, hollow);

        for (int v = have; v < wanted; v++)
        {
            // Point d'impact supposé : au hasard sur l'enveloppe de l'objet, différent pour chaque variante.
            Bounds b = src.bounds;
            Vector3 impact = b.center + Vector3.Scale(b.extents, RandomOnSphere(mainRandom));

            Piece root = Piece.FromMesh(src, capSub + 1, out bool hadNormals); // lecture du mesh : fil principal uniquement
            var job = new SliceJob
            {
                src = src,
                root = root,
                hadNormals = hadNormals,
                capSub = capSub,
                count = effCount,
                variants = wanted,
                seed = unchecked(++seedCounter * 7919 + 104729),
                hollow = hollow,
                tangents = effTangents,
                keepReadable = effConvex || effRefracture > 0,
                focus = impactFocus,
                localImpact = impact
            };
            pending.Add(job);

            Task.Run(() =>
            {
                try
                {
                    job.pieces = SliceAll(job.root, job.capSub, job.localImpact, job.count, job.focus, !job.hollow,
                                          new System.Random(job.seed));
                }
                catch (System.Exception)
                {
                    job.failed = true; // la découpe sera simplement recalculée sur place si l'objet casse
                }
                job.root = null;
                finished.Enqueue(job);
            });
        }
    }

    /// <summary>Fil principal : crée les meshes d'une découpe terminée et la range dans le cache.</summary>
    static void FinishJob(SliceJob job)
    {
        if (!pending.Remove(job)) return;              // cache vidé entre-temps
        if (job.failed || job.pieces == null || job.src == null) return;
        if (CountMatching(job.src, job.count, job.capSub, job.hollow) >= job.variants) return;

        FractureResult result = BuildResult(job.pieces, job.hadNormals, job.count, job.capSub, job.hollow, job.localImpact,
                                            job.tangents, job.keepReadable);
        result.shared = true;
        Store(job.src, result);
    }

    /// <summary>Découpe calculée sur place, quand un objet casse avant que sa découpe ne soit prête.</summary>
    FractureResult GenerateNow(Mesh src, int capSub, Vector3 localImpact)
    {
        Piece root = Piece.FromMesh(src, capSub + 1, out bool hadNormals);
        List<Piece> pieces = SliceAll(root, capSub, localImpact, effCount, impactFocus, !hollow, mainRandom);
        return BuildResult(pieces, hadNormals, effCount, capSub, hollow, localImpact, effTangents, effConvex || effRefracture > 0);
    }

    /// <summary>
    /// Géométrie pure, sans aucun appel à Unity : peut tourner sur n'importe quel fil.
    /// Recoupe en priorité les gros morceaux proches de l'impact, jusqu'au nombre voulu.
    /// </summary>
    static List<Piece> SliceAll(Piece root, int capSub, Vector3 localImpact, int count, float focus, bool closeCut,
                                System.Random rng)
    {
        var pieces = new List<Piece>(count) { root };
        float refSize = Mathf.Max(root.bounds.size.magnitude, 1e-6f);
        int attempts = count * 4;

        while (pieces.Count < count && attempts-- > 0)
        {
            int idx = PickPiece(pieces, localImpact, refSize, focus);
            Piece p = pieces[idx];

            Vector3 target = ClosestOnBounds(p.bounds, localImpact);
            Vector3 origin = Vector3.Lerp(p.bounds.center, target, focus * (float)rng.NextDouble());
            Vector3 normal = RandomOnSphere(rng);

            if (Slice(p, origin, normal, capSub, closeCut, out Piece a, out Piece b))
            {
                pieces[idx] = a;
                pieces.Add(b);
            }
            else
            {
                p.failures++;
            }
        }
        return pieces;
    }

    static int PickPiece(List<Piece> pieces, Vector3 localImpact, float refSize, float focus)
    {
        int best = 0;
        float bestScore = -1f;
        for (int i = 0; i < pieces.Count; i++)
        {
            Piece p = pieces[i];
            float d = Vector3.Distance(ClosestOnBounds(p.bounds, localImpact), localImpact) / refSize;
            float score = p.bounds.size.sqrMagnitude / (1f + focus * 10f * d);
            score *= Mathf.Pow(0.25f, p.failures); // pénalise les morceaux qu'on n'arrive pas à couper
            if (score > bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    /// <summary>Fil principal : transforme les morceaux en meshes, classés du plus gros au plus petit.</summary>
    static FractureResult BuildResult(List<Piece> pieces, bool hadNormals, int count, int capSub, bool isHollow,
                                      Vector3 localImpact, bool tangents, bool keepReadable)
    {
        float totalVolume = 0f;
        for (int i = 0; i < pieces.Count; i++) totalVolume += Volume(pieces[i].bounds);
        totalVolume = Mathf.Max(totalVolume, 1e-9f);

        pieces.Sort((x, y) => Volume(y.bounds).CompareTo(Volume(x.bounds)));

        var result = new FractureResult { count = count, capSub = capSub, hollow = isHollow, localImpact = localImpact };
        for (int i = 0; i < pieces.Count; i++)
        {
            Piece p = pieces[i];
            if (p.verts.Count < 4) continue;
            // Épaisseur : la plus petite des deux mesures, celle du plan du morceau et celle de sa boîte englobante.
            Vector3 size = p.bounds.size;
            float thickness = Thickness(p.verts, out float span);
            thickness = Mathf.Min(thickness, Mathf.Min(size.x, Mathf.Min(size.y, size.z)));
            result.fragments.Add(new FragmentData
            {
                mesh = BuildMesh(p, hadNormals, tangents, keepReadable),
                bounds = p.bounds,
                volumeShare = Volume(p.bounds) / totalVolume,
                span = span,
                thickness = thickness
            });
        }
        return result;
    }

    /// <summary>
    /// Épaisseur d'un nuage de points dans sa direction la plus mince, et sa plus grande dimension (span).
    /// On prend les deux points les plus éloignés, puis le point le plus loin de leur droite : ces trois points
    /// donnent le plan du morceau, et l'épaisseur est l'étendue des points de part et d'autre de ce plan.
    /// Renvoie 0 pour des points confondus, alignés ou tous dans un même plan.
    /// </summary>
    static float Thickness(List<Vector3> v, out float span)
    {
        span = 0f;
        int n = v.Count;
        if (n < 4) return 0f;

        int ia = Farthest(v, v[0]);
        int ib = Farthest(v, v[ia]);
        Vector3 a = v[ia];
        Vector3 ab = v[ib] - a;
        span = ab.magnitude;
        if (span < 1e-9f) return 0f;
        Vector3 dir = ab / span;

        Vector3 side = Vector3.zero;
        float best = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector3 r = v[i] - a;
            Vector3 off = r - dir * Vector3.Dot(r, dir);
            float d = off.sqrMagnitude;
            if (d > best) { best = d; side = off; }
        }
        if (best < span * span * 1e-10f) return 0f; // points alignés

        Vector3 normal = Vector3.Cross(dir, side / Mathf.Sqrt(best));
        float min = 0f, max = 0f;
        for (int i = 0; i < n; i++)
        {
            float d = Vector3.Dot(v[i] - a, normal);
            if (d < min) min = d;
            else if (d > max) max = d;
        }
        return max - min;
    }

    static int Farthest(List<Vector3> v, Vector3 from)
    {
        int index = 0;
        float best = -1f;
        for (int i = 0; i < v.Count; i++)
        {
            float d = (v[i] - from).sqrMagnitude;
            if (d > best) { best = d; index = i; }
        }
        return index;
    }

    static Mesh BuildMesh(Piece p, bool hadNormals, bool tangents, bool keepReadable)
    {
        var mesh = new Mesh { name = "FragmentMesh" };
        if (p.verts.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
        mesh.SetVertices(p.verts);
        mesh.SetNormals(p.normals);
        mesh.SetUVs(0, p.uvs);
        mesh.subMeshCount = p.tris.Length;
        for (int s = 0; s < p.tris.Length; s++) mesh.SetTriangles(p.tris[s], s, false);
        if (!hadNormals) mesh.RecalculateNormals();
        if (tangents) mesh.RecalculateTangents();
        mesh.bounds = p.bounds;

        // Le mesh part sur la carte graphique. Si rien n'a plus besoin de le relire (ni collider convexe,
        // ni nouvelle casse), sa copie en mémoire vive est libérée.
        mesh.UploadMeshData(!keepReadable);
        return mesh;
    }

    // ------------------------------------------------------------------ Hasard utilisable hors du fil principal

    static float Range(System.Random rng, float min, float max)
    {
        return min + (max - min) * (float)rng.NextDouble();
    }

    static Vector3 RandomOnSphere(System.Random rng)
    {
        // Tirage uniforme sur la sphère : hauteur uniforme, angle uniforme.
        float z = Range(rng, -1f, 1f);
        float angle = Range(rng, 0f, 2f * Mathf.PI);
        float r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
        return new Vector3(r * Mathf.Cos(angle), r * Mathf.Sin(angle), z);
    }

    static Vector3 RandomInSphere(System.Random rng)
    {
        return RandomOnSphere(rng) * Mathf.Pow((float)rng.NextDouble(), 1f / 3f);
    }

    static Vector3 ClosestOnBounds(Bounds b, Vector3 p)
    {
        Vector3 min = b.min, max = b.max;
        return new Vector3(Mathf.Clamp(p.x, min.x, max.x), Mathf.Clamp(p.y, min.y, max.y), Mathf.Clamp(p.z, min.z, max.z));
    }

    // ------------------------------------------------------------------ Effets visuels (systèmes partagés)

    const int FxExplosion = 0, FxSmoke = 1, FxDust = 2;
    static readonly Dictionary<Material, ParticleSystem[]> fxSystems = new Dictionary<Material, ParticleSystem[]>();
    static Material particleMaterial; // matériau de particules créé automatiquement
    static bool particleWarningShown;

    void SpawnEffects(Vector3 worldImpact, Vector3 center, float objectSize, float load)
    {
        if (customEffects != null)
            for (int i = 0; i < customEffects.Length; i++)
                if (customEffects[i] != null) Destroy(Instantiate(customEffects[i], worldImpact, Quaternion.identity), 6f);

        if (!explosionEffect && !smokeEffect && !dustEffect) return;

        Material mat = effectMaterial != null ? effectMaterial : DefaultParticleMaterial();
        if (mat == null) return;

        float s = Mathf.Max(0.02f, objectSize * effectScale);
        // Moins de particules quand la scène est chargée, et moitié moins sur casque autonome.
        float density = Mathf.Lerp(1f, 0.4f, load) * (quest ? 0.5f : 1f);

        if (explosionEffect)
            Emit(FxExplosion, mat, center, Count(24, density), Color.white, 0.15f * s,
                 new Vector2(3f, 8f) * s, new Vector2(0.6f, 1.4f) * s, new Vector2(0.25f, 0.5f));

        if (smokeEffect)
            Emit(FxSmoke, mat, center, Count(12, density), Color.white, 0.3f * s,
                 new Vector2(0.5f, 1.5f) * s, new Vector2(0.8f, 1.6f) * s, new Vector2(1.5f, 3f));

        if (dustEffect)
            Emit(FxDust, mat, worldImpact, Count(30, density), dustColor, 0.3f * s,
                 new Vector2(1f, 3f) * s, new Vector2(0.15f, 0.5f) * s, new Vector2(0.8f, 1.6f));
    }

    /// <summary>Crée dès le chargement les systèmes de particules dont cet objet aura besoin.</summary>
    void WarmUpEffects()
    {
        if (!explosionEffect && !smokeEffect && !dustEffect) return;
        Material mat = effectMaterial != null ? effectMaterial : DefaultParticleMaterial();
        if (mat == null) return;

        if (explosionEffect) FxSystem(FxExplosion, mat);
        if (smokeEffect) FxSystem(FxSmoke, mat);
        if (dustEffect) FxSystem(FxDust, mat);
    }

    static int Count(int baseCount, float density) => Mathf.Max(3, Mathf.RoundToInt(baseCount * density));

    /// <summary>
    /// Émet une bouffée de particules dans le système partagé de ce type d'effet. Rien n'est créé à la casse :
    /// le système existe une fois pour toutes et reçoit les particules de tous les objets.
    /// </summary>
    static void Emit(int kind, Material mat, Vector3 position, int count, Color tint, float radius,
                     Vector2 speed, Vector2 size, Vector2 life)
    {
        ParticleSystem ps = FxSystem(kind, mat);
        var p = new ParticleSystem.EmitParams();

        for (int i = 0; i < count; i++)
        {
            Vector3 dir = RandomOnSphere(mainRandom);
            p.position = position + dir * (radius * (float)mainRandom.NextDouble());
            p.velocity = dir * Range(mainRandom, speed.x, speed.y);
            p.startSize = Range(mainRandom, size.x, size.y);
            p.startLifetime = Range(mainRandom, life.x, life.y);
            p.rotation = Range(mainRandom, 0f, 360f);
            p.startColor = tint;
            ps.Emit(p, 1);
        }
    }

    static ParticleSystem FxSystem(int kind, Material mat)
    {
        if (!fxSystems.TryGetValue(mat, out ParticleSystem[] set))
        {
            set = new ParticleSystem[3];
            fxSystems[mat] = set;
        }
        if (set[kind] != null) return set[kind]; // (un système a pu disparaître avec sa scène : il est alors recréé)

        var go = new GameObject(kind == FxExplosion ? "FX_Explosion" : kind == FxSmoke ? "FX_Fumee" : "FX_Poussiere");
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); // à l'arrêt pendant le paramétrage

        float gravity = kind == FxExplosion ? 0f : kind == FxSmoke ? -0.03f : 0.1f;
        float damp = kind == FxExplosion ? 0.2f : kind == FxSmoke ? 0.05f : 0.1f;
        float endSize = kind == FxExplosion ? 1.6f : kind == FxSmoke ? 2.5f : 2f;

        var main = ps.main;
        main.playOnAwake = false;
        main.loop = true;
        main.startSpeed = 0f;
        main.gravityModifier = gravity;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 256;

        var emission = ps.emission;
        emission.rateOverTime = 0f; // rien en continu : les particules arrivent par Emit, à chaque casse

        var shape = ps.shape;
        shape.enabled = false;

        Gradient colors = kind == FxExplosion ? Fade(new Color(1f, 0.95f, 0.7f), new Color(1f, 0.5f, 0.1f), new Color(0.15f, 0.12f, 0.1f), 1f)
                        : kind == FxSmoke ? Fade(new Color(0.4f, 0.4f, 0.4f), new Color(0.3f, 0.3f, 0.3f), new Color(0.2f, 0.2f, 0.2f), 0.55f)
                        : Fade(Color.white, Color.white, new Color(0.85f, 0.85f, 0.85f), 0.6f); // teinté par la couleur de poussière de l'objet
        var colorOver = ps.colorOverLifetime;
        colorOver.enabled = true;
        colorOver.color = new ParticleSystem.MinMaxGradient(colors);

        // Les particules grossissent jusqu'à endSize fois leur taille de départ.
        var sizeOver = ps.sizeOverLifetime;
        sizeOver.enabled = true;
        sizeOver.size = new ParticleSystem.MinMaxCurve(endSize, AnimationCurve.EaseInOut(0f, 1f / endSize, 1f, 1f));

        // Freinage : les particules partent vite puis ralentissent.
        var limit = ps.limitVelocityOverLifetime;
        limit.enabled = true;
        limit.limit = 0f;
        limit.dampen = damp;

        var rend = go.GetComponent<ParticleSystemRenderer>();
        rend.sharedMaterial = mat;
        rend.shadowCastingMode = ShadowCastingMode.Off;
        rend.receiveShadows = false;

        ps.Play();
        set[kind] = ps;
        return ps;
    }

    /// <summary>Dégradé de trois couleurs, opaque au début puis qui s'efface.</summary>
    static Gradient Fade(Color start, Color middle, Color end, float alpha)
    {
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(start, 0f), new GradientColorKey(middle, 0.35f), new GradientColorKey(end, 1f) },
            new[] { new GradientAlphaKey(alpha, 0f), new GradientAlphaKey(alpha, 0.3f), new GradientAlphaKey(0f, 1f) });
        return g;
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

    // ------------------------------------------------------------------ Sons (sources partagées)

    const int VoiceCount = 8;
    static AudioSource[] voices;
    static int nextVoice;

    /// <summary>
    /// Joue un clip tiré au hasard à une position donnée, en son 3D, sur l'une des sources partagées :
    /// aucune source n'est créée ni détruite à la casse.
    /// </summary>
    static void PlayAt(AudioClip[] clips, Vector3 position, float volume, float pitchRange)
    {
        if (clips == null || clips.Length == 0) return;
        AudioClip clip = clips[mainRandom.Next(clips.Length)];
        if (clip == null) return;

        if (voices == null) voices = new AudioSource[VoiceCount];
        nextVoice = (nextVoice + 1) % VoiceCount;

        AudioSource src = voices[nextVoice];
        if (src == null) // première utilisation, ou source disparue avec sa scène
        {
            var go = new GameObject("FractureAudio");
            src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 1f;   // son entièrement spatialisé
            src.minDistance = 0.5f;
            src.maxDistance = 20f;
            voices[nextVoice] = src;
        }

        src.transform.position = position;
        src.clip = clip;
        src.volume = Mathf.Clamp01(volume);
        src.pitch = 1f + Range(mainRandom, -pitchRange, pitchRange);
        src.Play();
    }

#if UNITY_EDITOR
    /// <summary>Catégories reconnues dans les noms de fichiers : SO_[matériau]_[catégorie]_n.</summary>
    public static readonly string[] SoundCategories = { "Impact", "Slide" };

    /// <summary>Sons de casse ou d'écrasement : les clips SO_[matériau]_n, sans catégorie dans le nom.</summary>
    public static AudioClip[] FindMaterialSounds(string material, Object context)
    {
        return FindMaterialSounds(material, "", context);
    }

    /// <summary>
    /// Cherche dans Assets/Sounds/[matériau]/ (sous-dossiers compris) les clips nommés SO_..., triés par numéro.
    /// Avec une catégorie ("Impact", "Slide"), ne garde que les clips dont le nom contient _[catégorie].
    /// Sans catégorie, ne garde que ceux qui n'en contiennent aucune.
    /// Le nom du dossier est comparé sans tenir compte des majuscules ni des accents.
    /// Éditeur uniquement : les clips trouvés sont enregistrés sur le composant, donc présents dans le build.
    /// </summary>
    public static AudioClip[] FindMaterialSounds(string material, string category, Object context)
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

        bool hasCategory = !string.IsNullOrEmpty(category);
        string pattern = hasCategory ? $"SO_{material}_{category}_n" : $"SO_{material}_n";

        var clips = new List<AudioClip>();
        foreach (string guid in UnityEditor.AssetDatabase.FindAssets("t:AudioClip", new[] { folder }))
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            AudioClip clip = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip != null && MatchesCategory(clip.name, category)) clips.Add(clip);
        }
        if (clips.Count == 0)
        {
            // Une catégorie absente est un cas normal (pas de son de glissement pour ce matériau, par exemple).
            if (hasCategory) Debug.Log($"[Sons] Aucun clip '{pattern}' dans '{folder}'.", context);
            else Debug.LogWarning($"[Sons] Aucun clip '{pattern}' dans '{folder}'.", context);
            return null;
        }

        clips.Sort((a, b) =>
        {
            int c = TrailingNumber(a.name).CompareTo(TrailingNumber(b.name));
            return c != 0 ? c : string.CompareOrdinal(a.name, b.name);
        });
        Debug.Log($"[Sons] {clips.Count} clip(s) '{pattern}' chargé(s) depuis '{folder}'.", context);
        return clips.ToArray();
    }

    static bool MatchesCategory(string clipName, string category)
    {
        const System.StringComparison ignoreCase = System.StringComparison.OrdinalIgnoreCase;
        if (!clipName.StartsWith("SO_", ignoreCase)) return false;

        if (!string.IsNullOrEmpty(category))
            return clipName.IndexOf("_" + category, ignoreCase) >= 0;

        foreach (string c in SoundCategories)
            if (clipName.IndexOf("_" + c, ignoreCase) >= 0) return false;
        return true;
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

        // Tampons de travail réutilisés d'une coupe à l'autre (un jeu par fil d'exécution) : pas d'allocation par coupe.
        int vc = src.verts.Count;
        if (scratchDist == null || scratchDist.Length < vc)
        {
            int size = Mathf.Max(256, vc + vc / 2);
            scratchDist = new float[size];
            scratchMapA = new int[size];
            scratchMapB = new int[size];
        }
        float[] dist = scratchDist;
        int[] mapA = scratchMapA;
        int[] mapB = scratchMapB;
        for (int i = 0; i < vc; i++)
        {
            dist[i] = Vector3.Dot(normal, src.verts[i] - origin);
            mapA[i] = -1;
            mapB[i] = -1;
        }

        if (scratchCut == null) scratchCut = new List<Vector3>(64);
        List<Vector3> cut = scratchCut; // points du contour de la coupe
        cut.Clear();

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

    [System.ThreadStatic] static float[] scratchDist;
    [System.ThreadStatic] static int[] scratchMapA;
    [System.ThreadStatic] static int[] scratchMapB;
    [System.ThreadStatic] static List<Vector3> scratchCut;

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