using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Écrasement réaliste : l'objet prend la forme de ce qui le pousse et s'écrase aussi loin que l'autre
/// objet avance (marteau, sol, poids posé dessus, objet tenu en main qu'on enfonce dans une table...).
///
/// Principe, à chaque pas physique et pour chaque contact :
///  1. on mesure où se trouve la surface de l'autre objet (lancer de rayons sur son collider) ;
///  2. les sommets qui la dépassent sont repoussés derrière elle, et toute la matière située dessous
///     se tasse, gonfle sur les côtés et se froisse ;
///  3. le collider de l'objet est recalculé pour suivre la nouvelle forme, ce qui laisse l'autre objet
///     avancer davantage au pas suivant.
/// L'objet cède sur un choc rapide (Min Velocity) ou sous une pression forte (Resistance), et devient de
/// plus en plus dur à mesure qu'il s'aplatit.
///
/// Mise en place :
///  - À placer sur l'objet qui porte le Rigidbody (la racine). Le mesh peut être sur un enfant.
///  - Le mesh doit avoir "Read/Write Enabled" coché dans ses paramètres d'import.
///  - Le mesh doit être assez dense : on ne peut plier que là où il y a des sommets.
///  - Pour les objets tenus en main (kinematic) contre le décor :
///    Edit > Project Settings > Physics > Contact Pairs Mode = Enable All Contact Pairs.
///  - Nécessite RuntimeFracture.cs et RuntimeImpactSound.cs dans le projet.
/// </summary>
[DefaultExecutionOrder(-500)] // avant XR Interaction Toolkit, pour que le collider créé ici soit pris en compte
[RequireComponent(typeof(Rigidbody))]
public class RuntimeCrush : MonoBehaviour, IPointVelocity
{
    public enum Preset
    {
        [InspectorName("Personnalisé")] Personnalise,
        [InspectorName("Bouteille plastique")] BouteillePlastique,
        [InspectorName("Canette")] Canette,
        [InspectorName("Carton")] Carton,
        [InspectorName("Tôle / métal")] Tole,
        [InspectorName("Mousse")] Mousse,
        [InspectorName("Mousse à mémoire de forme")] MousseMemoire,
        [InspectorName("Caoutchouc")] Caoutchouc
    }

    public enum ColliderMode
    {
        [InspectorName("Automatique (suit la déformation)")] Automatique,
        [InspectorName("Ne pas toucher aux colliders")] Aucun
    }

    const float MinStep = 0.0003f;   // en dessous de 0,3 mm, on ne déforme pas
    const float SoundStep = 0.008f;  // un son tous les 8 mm d'écrasement
    const int MaxProxy = 96;         // nombre de points du collider simplifié

    [Header("Preset")]
    [Tooltip("Choisir un preset remplit les paramètres ci-dessous, qui restent modifiables ensuite.")]
    public Preset preset = Preset.Personnalise;
    [SerializeField, HideInInspector] Preset appliedPreset = Preset.Personnalise;

    [Header("Résistance")]
    [Tooltip("Choc : vitesse d'impact (m/s) à partir de laquelle l'objet se déforme (chute, lancer, coup).")]
    public float minVelocity = 1.5f;
    [Tooltip("Force des chocs libres (chute, lancer, coup en l'air), où l'objet peut rebondir au lieu de s'écraser. "
           + "1 = le choc écrase autant qu'une pression, 0,3 = l'objet rebondit surtout et marque peu.")]
    [Range(0f, 1f)] public float impactStrength = 0.5f;
    [Tooltip("Pression : force (newtons) à partir de laquelle l'objet cède quand il est coincé ou pressé "
           + "(entre un outil et le sol, sous un poids, tenu en main contre une table).")]
    public float resistance = 30f;
    [Tooltip("Force (newtons) prêtée au joueur quand l'objet ou l'outil est tenu en main en mode kinematic. "
           + "Si elle est inférieure à Resistance, l'objet ne peut pas être écrasé à la main, seulement par des chocs.")]
    public float heldForce = 250f;
    [Tooltip("Durcissement : l'objet résiste de plus en plus à mesure qu'il s'aplatit. 0 = résistance constante, 1 = normal.")]
    [Range(0f, 2f)] public float hardening = 1f;
    [Tooltip("Épaisseur minimale, en proportion de la taille d'origine. 0,15 = l'objet peut être aplati jusqu'à 15 %.")]
    [Range(0.05f, 0.9f)] public float minThickness = 0.15f;
    [Tooltip("Les collisions avec ces layers sont ignorées (corps du joueur, mains...).")]
    public LayerMask ignoreLayers;

    [Header("Forme de l'écrasement")]
    [Tooltip("Largeur (mètres) de la zone entraînée autour de la surface de contact.")]
    public float spread = 0.04f;
    [Tooltip("1 = toute l'épaisseur se tasse uniformément (mousse). 3 = seul le côté frappé s'enfonce (tôle).")]
    [Range(1f, 4f)] public float localization = 1.5f;
    [Tooltip("Gonflement sur les côtés : la matière écrasée s'élargit. 0 = aucun.")]
    [Range(0f, 1f)] public float bulge = 0.2f;
    [Tooltip("0 = écrasement lisse, 1 = surface très froissée.")]
    [Range(0f, 1f)] public float crumple = 0.35f;
    [Tooltip("Finesse des plis (plus grand = plis plus petits).")]
    public float crumpleScale = 60f;

    [Header("Retour à la forme")]
    [Tooltip("Part de la déformation qui disparaît une fois la pression relâchée. "
           + "0 = tout reste (canette), 0,25 = léger rebond (plastique), 1 = se regonfle entièrement (mousse).")]
    [Range(0f, 1f)] public float elasticity = 0f;
    [Tooltip("Rapidité du retour. 15 = quasi instantané (caoutchouc), 4 = environ une demi-seconde (mousse), 1 = lent.")]
    public float recoverySpeed = 4f;
    [Tooltip("Temps d'attente (secondes) avant que l'objet commence à reprendre sa forme.")]
    public float recoveryDelay = 0.1f;

    [Header("Collider")]
    [Tooltip("Automatique : remplace les colliders de l'objet par un collider convexe simplifié qui suit la déformation. "
           + "Indispensable pour que l'écrasement dépende de la distance parcourue par l'autre objet.")]
    public ColliderMode colliderMode = ColliderMode.Automatique;
    [Tooltip("Nombre de mises à jour du collider par seconde pendant une déformation. Baisser sur Quest si besoin.")]
    [Range(5f, 90f)] public float colliderRefreshRate = 60f;

    [Header("Son")]
    [Tooltip("Nom du dossier dans Assets/Sounds/ (rempli par le preset, modifiable). "
           + "Les clips SO_[matériau]_n de ce dossier sont chargés automatiquement dans la liste ci-dessous "
           + "(ceux nommés _Impact_ ou _Slide_ sont réservés au script RuntimeImpactSound).")]
    [Delayed] public string soundMaterial = "";
    [SerializeField, HideInInspector] string appliedSoundMaterial = "";
    [Tooltip("Sons d'écrasement. S'il y en a plusieurs, un clip est tiré au hasard à chaque fois.")]
    public AudioClip[] crushSounds;
    [Tooltip("Volume pour un écrasement rapide. Les petites déformations sont jouées moins fort.")]
    [Range(0f, 1f)] public float soundVolume = 1f;
    [Tooltip("Variation aléatoire de hauteur (±) pour éviter la répétition.")]
    [Range(0f, 0.5f)] public float pitchVariation = 0.1f;

    [Header("Événement")]
    [Tooltip("Appelé pendant l'écrasement (au rythme des sons) avec le point de contact (monde) : haptique, particules...")]
    public UnityEvent<Vector3> onCrush;

    [Header("Diagnostic")]
    [Tooltip("Affiche dans la console chaque écrasement : profondeur, vitesse et force mesurées.")]
    public bool debugLog;

    // ------------------------------------------------------------------ État interne

    class Target
    {
        public Transform t;
        public Mesh mesh;
        public Renderer renderer;
        public Vector3[] original; // forme d'origine
        public Vector3[] rest;     // forme vers laquelle l'objet revient (déformation permanente)
        public Vector3[] current;  // forme affichée
        public bool dirty;         // mesh à renvoyer à la carte graphique

        // Tableaux de travail, réutilisés à chaque pas
        public Vector3[] world;
        public float[] depth;      // position du sommet le long de la poussée (0 = surface de l'autre objet)
        public float[] lateral;    // distance à l'axe de la poussée
        public float[] push;       // déplacement minimal imposé (sommets qui dépassent dans l'autre objet)
        public readonly List<int> candidates = new List<int>();

        // Collider simplifié qui suit la déformation
        public MeshCollider collider;
        public Mesh proxy;
        public int[] proxyIndex;
        public Vector3[] proxyVerts;
    }

    readonly List<Target> targets = new List<Target>();
    Rigidbody rb;
    float worldSize = 0.2f;

    /// <summary>Image où le dernier son d'écrasement a été joué (lu par RuntimeImpactSound pour éviter un doublon).</summary>
    [System.NonSerialized] public int lastCrushSoundFrame = -1;

    // Retour à la forme
    bool recovering;
    float recoverAt;
    float lastPressureTime = -10f;
    readonly List<Collider> loaders = new List<Collider>(); // objets qui appuient encore sur celui-ci

    // Collider
    bool colliderDirty;
    float lastColliderRefresh = -10f;

    // Son
    float pendingSoundDepth;
    float lastSoundTime = -10f;

    // Derniers contacts, pour savoir si l'objet est pris en étau entre deux choses
    const int ContactSlots = 8;
    readonly Collider[] contactCollider = new Collider[ContactSlots];
    readonly Vector3[] contactNormal = new Vector3[ContactSlots];
    readonly float[] contactTime = new float[ContactSlots];

    // Vitesse de l'autre objet : composants gardés en mémoire tant qu'on touche le même Rigidbody.
    Rigidbody otherBody;
    IPointVelocity otherTracker;

    // Poses des derniers pas physiques, pour mesurer la vitesse réelle même tenu en main (kinematic).
    readonly Matrix4x4[] poses = new Matrix4x4[4];
    int poseHead;

    // ------------------------------------------------------------------ Presets et sons

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
        crushSounds = found;
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    /// <summary>Remplit les paramètres selon un matériau type. "Personnalisé" ne change rien.</summary>
    public void ApplyPreset(Preset p)
    {
        switch (p)
        {
            //                                          choc   libre  pression  épaisseur  zone   localisé  gonfle  froissé  plis  élast.  retour  délai
            case Preset.BouteillePlastique: SetMaterial(1.5f, 0.4f, 25f, 0.15f, 0.04f, 1.5f, 0.20f, 0.35f, 60f, 0.25f, 6f, 0.05f, "Plastic"); break;
            case Preset.Canette: SetMaterial(2.0f, 0.5f, 40f, 0.12f, 0.02f, 1.3f, 0.15f, 0.30f, 80f, 0f, 0f, 0f, "Metal"); break;
            case Preset.Carton: SetMaterial(1.5f, 0.6f, 30f, 0.10f, 0.08f, 2.0f, 0.10f, 0.15f, 25f, 0.10f, 3f, 0.10f, "Cardboard"); break;
            case Preset.Tole: SetMaterial(3.0f, 0.6f, 300f, 0.30f, 0.08f, 3.0f, 0.05f, 0.08f, 20f, 0f, 0f, 0f, "Metal"); break;
            case Preset.Mousse: SetMaterial(0.5f, 1.0f, 4f, 0.20f, 0.08f, 1.0f, 0.25f, 0f, 30f, 1f, 4f, 0.15f, "Foam"); break;
            case Preset.MousseMemoire: SetMaterial(0.5f, 1.0f, 4f, 0.20f, 0.08f, 1.0f, 0.25f, 0f, 30f, 1f, 1f, 0.50f, "Foam"); break;
            case Preset.Caoutchouc: SetMaterial(1.0f, 0.8f, 60f, 0.50f, 0.06f, 1.0f, 0.30f, 0f, 30f, 1f, 15f, 0f, "Rubber"); break;
        }
    }

    void SetMaterial(float velocity, float impact, float pressure, float thickness, float zone, float local, float swell,
                     float crumpleAmount, float crumpleSize, float elastic, float recovery, float delay, string sounds)
    {
        minVelocity = velocity;
        impactStrength = impact;
        resistance = pressure;
        minThickness = thickness;
        spread = zone;
        localization = local;
        bulge = swell;
        crumple = crumpleAmount;
        crumpleScale = crumpleSize;
        elasticity = elastic;
        recoverySpeed = recovery;
        recoveryDelay = delay;
        soundMaterial = sounds;
    }

    // ------------------------------------------------------------------ Initialisation

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        for (int i = 0; i < ContactSlots; i++) contactTime[i] = -10f;

        // Le mesh de cet objet va être déformé, donc propre à lui : la fracture ne doit pas le mettre en cache.
        RuntimeFracture fracture = GetComponent<RuntimeFracture>();
        if (fracture != null) fracture.hasUniqueMesh = true;

        bool hasBounds = false;
        Bounds all = default;

        foreach (MeshFilter mf in GetComponentsInChildren<MeshFilter>())
        {
            Mesh shared = mf.sharedMesh;
            if (shared == null) continue;
            if (!shared.isReadable)
            {
                Debug.LogWarning($"[RuntimeCrush] Le mesh '{shared.name}' doit avoir Read/Write activé.", mf);
                continue;
            }

            // Copie propre à cet objet : les autres exemplaires du même modèle restent intacts.
            Mesh inst = Instantiate(shared);
            inst.name = shared.name + " (déformable)";
            inst.MarkDynamic();
            mf.sharedMesh = inst;

            Vector3[] verts = inst.vertices;
            var tg = new Target
            {
                t = mf.transform,
                mesh = inst,
                renderer = mf.GetComponent<Renderer>(),
                original = verts,
                rest = (Vector3[])verts.Clone(),
                current = (Vector3[])verts.Clone(),
                world = new Vector3[verts.Length],
                depth = new float[verts.Length],
                lateral = new float[verts.Length],
                push = new float[verts.Length]
            };
            targets.Add(tg);

            if (tg.renderer != null)
            {
                if (hasBounds) all.Encapsulate(tg.renderer.bounds);
                else { all = tg.renderer.bounds; hasBounds = true; }
            }
        }

        if (hasBounds) worldSize = Mathf.Max(0.02f, all.size.magnitude);
        SetupColliders();
    }

    void OnEnable()
    {
        Matrix4x4 m = transform.localToWorldMatrix;
        for (int i = 0; i < poses.Length; i++) poses[i] = m;
    }

    void OnDestroy()
    {
        foreach (Target tg in targets)
        {
            if (tg.mesh != null) Destroy(tg.mesh);
            if (tg.proxy != null) Destroy(tg.proxy);
        }
    }

    // ------------------------------------------------------------------ Collider qui suit la déformation

    void SetupColliders()
    {
        if (colliderMode != ColliderMode.Automatique || targets.Count == 0) return;

        Collider[] existing = GetComponentsInChildren<Collider>(true);
        var created = new List<Collider>();

        foreach (Target tg in targets)
        {
            if (!BuildProxy(tg)) continue;

            // Objet enfant dédié : un MeshCollider ajouté à côté d'un MeshFilter prendrait le mesh complet.
            var go = new GameObject("CrushCollider") { layer = tg.t.gameObject.layer };
            go.transform.SetParent(tg.t, false);

            MeshCollider mc = go.AddComponent<MeshCollider>();
            mc.convex = true;
            // Sans "CookForFasterSimulation" : le collider est recalculé souvent, autant que ce soit rapide.
            mc.cookingOptions = MeshColliderCookingOptions.EnableMeshCleaning
                              | MeshColliderCookingOptions.WeldColocatedVertices
                              | MeshColliderCookingOptions.UseFastMidphase;
            mc.sharedMesh = tg.proxy;
            tg.collider = mc;
            created.Add(mc);
        }
        if (created.Count == 0) return;

        // Les colliders d'origine sont désactivés : ils ne suivraient pas la forme écrasée.
        foreach (Collider c in existing)
        {
            if (c == null || c.isTrigger || !c.enabled) continue;
            if (c.GetComponentInParent<Rigidbody>() != rb) continue;

            var material = c.sharedMaterial;
            if (material != null)
                foreach (Collider mc in created)
                    if (mc.sharedMaterial == null) mc.sharedMaterial = material;

            c.enabled = false;
        }

        // Limite la vitesse d'éjection quand un outil tenu en main s'enfonce dans l'objet.
        rb.maxDepenetrationVelocity = Mathf.Min(rb.maxDepenetrationVelocity, 2f);

        RegisterWithInteractables(created);
    }

    /// <summary>Choisit jusqu'à 96 sommets représentatifs (les plus extérieurs dans 64 directions, puis un échantillon régulier).</summary>
    bool BuildProxy(Target tg)
    {
        Vector3[] v = tg.original;
        int count = v.Length;
        if (count < 4) return false;

        var picked = new HashSet<int>();
        var order = new List<int>();

        const int directions = 64;
        float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
        for (int d = 0; d < directions; d++)
        {
            float y = 1f - 2f * (d + 0.5f) / directions;
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
            float angle = golden * d;
            Vector3 dir = new Vector3(Mathf.Cos(angle) * r, y, Mathf.Sin(angle) * r);

            int best = 0;
            float bestDot = float.NegativeInfinity;
            for (int i = 0; i < count; i++)
            {
                float dot = Vector3.Dot(v[i], dir);
                if (dot > bestDot) { bestDot = dot; best = i; }
            }
            if (picked.Add(best)) order.Add(best);
        }

        int step = Mathf.Max(1, count / MaxProxy);
        for (int i = 0; i < count && order.Count < MaxProxy; i += step)
            if (picked.Add(i)) order.Add(i);

        if (order.Count < 4) return false;

        tg.proxyIndex = order.ToArray();
        tg.proxyVerts = new Vector3[tg.proxyIndex.Length];
        for (int k = 0; k < tg.proxyIndex.Length; k++) tg.proxyVerts[k] = v[tg.proxyIndex[k]];

        // Le collider convexe n'utilise que les points : les triangles ne servent qu'à rendre le mesh valide.
        int[] tris = new int[(tg.proxyIndex.Length - 2) * 3];
        for (int k = 0; k < tg.proxyIndex.Length - 2; k++)
        {
            tris[k * 3] = k;
            tris[k * 3 + 1] = k + 1;
            tris[k * 3 + 2] = k + 2;
        }

        tg.proxy = new Mesh { name = "CrushColliderMesh" };
        tg.proxy.MarkDynamic();
        tg.proxy.vertices = tg.proxyVerts;
        tg.proxy.triangles = tris;
        tg.proxy.RecalculateBounds();
        return true;
    }

    void RefreshCollider(Target tg)
    {
        if (tg.collider == null) return;
        for (int k = 0; k < tg.proxyIndex.Length; k++) tg.proxyVerts[k] = tg.current[tg.proxyIndex[k]];
        tg.proxy.vertices = tg.proxyVerts;
        tg.proxy.RecalculateBounds();
        tg.collider.sharedMesh = null; // force le recalcul
        tg.collider.sharedMesh = tg.proxy;
    }

    /// <summary>
    /// Ajoute les colliders créés à la liste "colliders" d'un éventuel XR Grab Interactable, pour que l'objet
    /// reste saisissable. Fait par réflexion : le script ne dépend pas du package XR Interaction Toolkit.
    /// </summary>
    void RegisterWithInteractables(List<Collider> created)
    {
        foreach (MonoBehaviour mb in GetComponents<MonoBehaviour>())
        {
            if (mb == null || mb == this) continue;
            try
            {
                System.Reflection.PropertyInfo prop = mb.GetType().GetProperty("colliders");
                if (prop == null || !typeof(List<Collider>).IsAssignableFrom(prop.PropertyType)) continue;

                var list = prop.GetValue(mb) as List<Collider>;
                if (list == null) continue;
                foreach (Collider c in created)
                    if (!list.Contains(c)) list.Add(c);
            }
            catch (System.Exception)
            {
                // Composant sans liste exploitable : on l'ignore.
            }
        }
    }

    // ------------------------------------------------------------------ Suivi de vitesse

    void FixedUpdate()
    {
        poseHead = (poseHead + 1) % poses.Length;
        poses[poseHead] = transform.localToWorldMatrix;

        // Collider remis à jour avant la simulation de ce pas, à la cadence choisie.
        if (colliderDirty && Time.time - lastColliderRefresh >= 1f / Mathf.Max(1f, colliderRefreshRate))
        {
            colliderDirty = false;
            lastColliderRefresh = Time.time;
            foreach (Target tg in targets) RefreshCollider(tg);
        }
    }

    /// <summary>Vitesse (monde) d'un point de l'objet, valable même tenu en main (kinematic).</summary>
    public Vector3 PointVelocity(Vector3 worldPoint)
    {
        Matrix4x4 newest = poses[poseHead];
        Matrix4x4 oldest = poses[(poseHead + 1) % poses.Length];
        Vector3 local = newest.inverse.MultiplyPoint3x4(worldPoint);
        float span = (poses.Length - 1) * Time.fixedDeltaTime;
        return (worldPoint - oldest.MultiplyPoint3x4(local)) / span;
    }

    Vector3 OtherVelocity(Rigidbody body, Vector3 point)
    {
        if (body == null) return Vector3.zero;

        if (body != otherBody)
        {
            otherBody = body;
            otherTracker = body.GetComponent<IPointVelocity>();
        }

        return otherTracker != null ? otherTracker.PointVelocity(point) : body.GetPointVelocity(point);
    }

    // ------------------------------------------------------------------ Contacts

    void OnCollisionEnter(Collision col) { ProcessContact(col); }
    void OnCollisionStay(Collision col) { ProcessContact(col); }
    void OnCollisionExit(Collision col) { loaders.Remove(col.collider); }

    void ProcessContact(Collision col)
    {
        if (targets.Count == 0 || col.contactCount == 0) return;

        Collider other = col.collider;
        if ((ignoreLayers.value & (1 << other.gameObject.layer)) != 0) return;

        // Contact le plus enfoncé de la paire.
        ContactPoint contact = col.GetContact(0);
        for (int i = 1; i < col.contactCount; i++)
        {
            ContactPoint c = col.GetContact(i);
            if (c.separation < contact.separation) contact = c;
        }
        Vector3 point = contact.point;

        // Sens de la poussée : de l'autre objet vers l'intérieur de celui-ci.
        Vector3 n = contact.normal;
        if (Vector3.Dot(n, rb.worldCenterOfMass - point) < 0f) n = -n;

        float dt = Time.fixedDeltaTime;
        Rigidbody body = col.rigidbody;
        bool selfHeld = rb.isKinematic;
        bool otherHeld = body != null && body.isKinematic;
        bool squeezed = RecordContact(other, n);

        // Vitesse d'approche. Pour un objet tenu en main, le moteur physique ne la connaît pas : on la mesure.
        float speed = Mathf.Abs(Vector3.Dot(col.relativeVelocity, n));
        if (selfHeld || otherHeld)
            speed = Mathf.Max(speed, -Vector3.Dot(PointVelocity(point) - OtherVelocity(body, point), n));

        // Force de pression : ne compte que si l'objet ne peut pas s'échapper (pris en étau, ou tenu en main).
        float force = 0f;
        if (selfHeld || squeezed)
        {
            force = col.impulse.magnitude / dt;
            if (selfHeld || otherHeld) force = Mathf.Max(force, heldForce);

            if (force > resistance * 0.5f)
            {
                lastPressureTime = Time.time;
                if (body != null && !loaders.Contains(other)) loaders.Add(other);
            }
        }

        // Sortie rapide : ni choc assez rapide, ni pression assez forte.
        // (Face à un outil tenu en main sans vitesse connue, la vitesse est déduite de son enfoncement.)
        bool measured = selfHeld || otherHeld;
        if (speed <= minVelocity && force <= resistance && !measured) return;

        float applied = Press(other, n, point, Mathf.Max(0f, speed) * dt, Mathf.Infinity, false, speed, force, measured);
        if (applied > 0f) Feedback(point, applied, speed, force);
    }

    /// <summary>Mémorise le contact et indique si un autre objet appuie en face (objet pris en étau).</summary>
    bool RecordContact(Collider other, Vector3 n)
    {
        float now = Time.time;
        float window = Time.fixedDeltaTime * 2.5f;

        bool squeezed = false;
        int slot = 0;
        float oldest = float.PositiveInfinity;
        bool found = false;

        for (int i = 0; i < ContactSlots; i++)
        {
            if (contactCollider[i] == other)
            {
                slot = i;
                found = true;
                continue;
            }
            if (now - contactTime[i] <= window && Vector3.Dot(contactNormal[i], n) < -0.3f) squeezed = true;
            if (!found && contactTime[i] < oldest) { oldest = contactTime[i]; slot = i; }
        }

        contactCollider[slot] = other;
        contactNormal[slot] = n;
        contactTime[slot] = now;
        return squeezed;
    }

    // ------------------------------------------------------------------ Écrasement

    /// <summary>
    /// Écrase l'objet avec un plan : tout ce qui se trouve devant le plan (passant par worldPoint, avancé de depth
    /// dans la direction worldDirection) est repoussé derrière lui. Utilisable depuis un autre script, par exemple
    /// pour écraser une canette quand le joueur serre la gâchette.
    /// </summary>
    public void Crush(Vector3 worldPoint, Vector3 worldDirection, float depth, float radius = Mathf.Infinity)
    {
        if (depth <= 0f || targets.Count == 0 || worldDirection.sqrMagnitude < 1e-12f) return;
        float applied = Press(null, worldDirection.normalized, worldPoint, depth, radius, true, 0f, 0f, false);
        if (applied > 0f) Feedback(worldPoint, applied, 0f, 0f);
    }

    /// <summary>Clic droit sur le composant en mode Play : aplatit l'objet de 40 % par le dessus.</summary>
    [ContextMenu("Écraser maintenant (test)")]
    void CrushNow()
    {
        if (!Application.isPlaying || targets.Count == 0) return;

        ApplyDirtyMeshes();
        bool has = false;
        Bounds b = default;
        foreach (Target tg in targets)
        {
            if (tg.renderer == null) continue;
            if (has) b.Encapsulate(tg.renderer.bounds);
            else { b = tg.renderer.bounds; has = true; }
        }
        if (!has) return;

        Crush(new Vector3(b.center.x, b.max.y, b.center.z), Vector3.down, b.size.y * 0.4f);
    }

    /// <summary>
    /// Cœur de l'écrasement. n : direction de la poussée (monde, vers l'intérieur de l'objet).
    /// advance : distance dont la surface de l'autre objet veut encore avancer pendant ce pas.
    /// Renvoie la profondeur réellement appliquée (0 si rien n'a bougé).
    /// </summary>
    float Press(Collider other, Vector3 n, Vector3 point, float advance, float radius, bool forced,
                float speed, float force, bool speedFromOverlap)
    {
        const float margin = 0.003f;
        float reach = worldSize * 2f + 0.1f;

        // 1. Surface de l'autre objet sur l'axe du contact (vue depuis l'intérieur de celui-ci).
        Vector3 q = point;
        if (other != null && other.Raycast(new Ray(point + n * reach, -n), out RaycastHit axisHit, reach * 2f))
            q = axisHit.point;

        // 2. Position de chaque sommet le long de la poussée, et taille d'origine de l'objet dans cette direction.
        float origMin = float.PositiveInfinity, origMax = float.NegativeInfinity;
        int candidateCount = 0;

        foreach (Target tg in targets)
        {
            Matrix4x4 m = tg.t.localToWorldMatrix;
            Vector3 c0 = m.GetColumn(0), c1 = m.GetColumn(1), c2 = m.GetColumn(2), c3 = m.GetColumn(3);
            Vector3 nLocal = new Vector3(Vector3.Dot(c0, n), Vector3.Dot(c1, n), Vector3.Dot(c2, n));
            float offset = Vector3.Dot(c3, n);

            Vector3[] cur = tg.current;
            Vector3[] orig = tg.original;
            tg.candidates.Clear();

            for (int i = 0; i < cur.Length; i++)
            {
                Vector3 wp = m.MultiplyPoint3x4(cur[i]);
                float h = Vector3.Dot(wp - q, n);
                tg.world[i] = wp;
                tg.depth[i] = h;
                tg.push[i] = 0f;

                float o = Vector3.Dot(orig[i], nLocal) + offset;
                if (o < origMin) origMin = o;
                if (o > origMax) origMax = o;

                if (h < advance + margin) tg.candidates.Add(i);
            }
            candidateCount += tg.candidates.Count;
        }
        if (candidateCount == 0) return 0f;

        // 3. Sommets proches de l'autre objet : de combien dépassent-ils dans sa surface ?
        //    (gap > 0 : le sommet est entré dans l'autre objet ; -infini : il n'est pas sous lui.)
        float gapMax = MeasureGaps(other, n, q, radius, reach, true);
        if (other != null && float.IsNegativeInfinity(gapMax))
        {
            // Aucun rayon n'a touché (collider non pris en charge) : on se rabat sur un plan de la taille de l'autre objet.
            gapMax = MeasureGaps(other, n, q, LateralRadius(other.bounds, n), reach, false);
        }
        if (float.IsNegativeInfinity(gapMax)) return 0f;

        // Avance virtuelle au-delà de ce qui est déjà enfoncé (cas d'un outil arrêté par le collider).
        float extra = Mathf.Max(0f, advance - Mathf.Max(0f, gapMax));
        float wanted = gapMax + extra;
        if (wanted < MinStep) return 0f;

        // 4. Zone de contact : centre et rayon des sommets à repousser.
        Vector3 center = q;
        float footprint = radius;
        if (other != null)
        {
            Vector3 sum = Vector3.zero;
            float weight = 0f;
            foreach (Target tg in targets)
            {
                foreach (int i in tg.candidates)
                {
                    float need = tg.push[i] + extra;
                    if (float.IsNegativeInfinity(tg.push[i]) || need <= 0f) { tg.push[i] = 0f; continue; }
                    tg.push[i] = need;
                    sum += tg.world[i] * need;
                    weight += need;
                }
            }
            if (weight <= 0f) return 0f;
            center = sum / weight;

            footprint = 0f;
            foreach (Target tg in targets)
                foreach (int i in tg.candidates)
                    if (tg.push[i] > 0f) footprint = Mathf.Max(footprint, LateralDistance(tg.world[i], center, n));
        }
        else
        {
            foreach (Target tg in targets)
            {
                foreach (int i in tg.candidates)
                {
                    float need = tg.push[i] + extra;
                    tg.push[i] = float.IsNegativeInfinity(tg.push[i]) || need <= 0f ? 0f : need;
                }
            }
        }

        // 5. Colonne de matière sous la zone de contact : du sommet le plus avancé jusqu'au fond de l'objet.
        float outer = footprint + Mathf.Max(0f, spread);
        float front = float.PositiveInfinity, far = float.NegativeInfinity;

        foreach (Target tg in targets)
        {
            for (int i = 0; i < tg.current.Length; i++)
            {
                float rho = LateralDistance(tg.world[i], center, n);
                tg.lateral[i] = rho;
                if (rho >= outer) continue;

                float h = tg.depth[i];
                if (h < front) front = h;
                if (h > far) far = h;
            }
        }

        float length = far - front;
        float origLength = origMax - origMin;
        if (length < 1e-5f || origLength < 1e-5f) { ClearPush(); return 0f; }

        // 6. L'objet cède-t-il ? Choc assez rapide ou pression assez forte, avec durcissement quand il s'aplatit.
        float compression = Mathf.Clamp(1f - length / origLength, 0f, 0.95f);
        float hard = 1f + hardening * (1f / ((1f - compression) * (1f - compression)) - 1f);

        float give = 1f;
        if (!forced)
        {
            if (speedFromOverlap && gapMax > 0f) speed = Mathf.Max(speed, gapMax / Time.fixedDeltaTime);
            float byImpact = speed > 0f ? impactStrength * Mathf.Clamp01((speed - minVelocity * hard) / speed) : 0f;
            float byPressure = force > 0f ? Mathf.Clamp01((force - resistance * hard) / force) : 0f;
            give = Mathf.Max(byImpact, byPressure);
        }

        float applied = Mathf.Min(wanted * give, length - minThickness * origLength);
        if (applied < MinStep) { ClearPush(); return 0f; }

        float pushScale = applied / wanted;
        // Exposant limité pour que les sommets ne se croisent jamais, même sur un gros écrasement d'un coup.
        float exponent = Mathf.Clamp(localization, 1f, Mathf.Max(1f, length / applied));

        // 7. Déplacement des sommets.
        float fold = Mathf.Max(0.0001f, crumpleScale);
        float keep = 1f - elasticity;

        foreach (Target tg in targets)
        {
            Matrix4x4 inv = tg.t.worldToLocalMatrix;
            Vector3 ls = tg.t.lossyScale;
            float scale = Mathf.Max(1e-6f, (Mathf.Abs(ls.x) + Mathf.Abs(ls.y) + Mathf.Abs(ls.z)) / 3f);

            Vector3[] cur = tg.current;
            Vector3[] rest = tg.rest;
            Vector3[] orig = tg.original;
            bool changed = false;

            for (int i = 0; i < cur.Length; i++)
            {
                float need = tg.push[i] * pushScale;
                tg.push[i] = 0f;

                float rho = tg.lateral[i];
                if (rho >= outer && need <= 0f) continue;

                // Tassement : maximal contre l'autre objet, nul au fond.
                float w = Falloff(rho, footprint, spread);
                float t = Mathf.Clamp01((far - tg.depth[i]) / length);
                float along = applied * Mathf.Pow(t, exponent) * w;
                if (need > along) along = need;
                if (along <= 1e-7f) continue;

                // Froissement : variation fixée par la position d'origine du sommet,
                // donc identique pour les sommets dupliqués (pas de déchirure aux coutures).
                Vector3 p = orig[i] * (fold * scale);
                float noiseA = Mathf.PerlinNoise(p.x + p.z * 0.7f + 31.4f, p.y - p.z * 0.7f + 12.7f);
                float noiseB = Mathf.PerlinNoise(p.y + p.x * 0.6f + 5.3f, p.z - p.x * 0.6f + 47.1f);

                // Les plis s'enfoncent un peu plus (jamais moins : le sommet repasserait dans l'autre objet).
                Vector3 move = n * (along * (1f + crumple * 0.3f * noiseB));

                // Sur les côtés : la matière gonfle vers l'extérieur et se plisse.
                if (rho > 1e-5f)
                {
                    Vector3 r = tg.world[i] - center;
                    Vector3 side = (r - n * Vector3.Dot(r, n)) / rho;
                    move += side * (along * (bulge + crumple * (2f * noiseA - 1f)));
                }

                Vector3 local = inv.MultiplyVector(move);
                cur[i] += local;
                rest[i] += local * keep; // seule la part non élastique devient permanente
                changed = true;
            }

            if (changed) tg.dirty = true;
        }

        colliderDirty = true;
        return applied;
    }

    /// <summary>
    /// Pour chaque sommet candidat, mesure de combien il dépasse dans l'autre objet et le range dans push.
    /// Avec rayons : la surface réelle de l'autre collider est lue sous chaque sommet (l'empreinte a sa forme).
    /// Sans rayons : l'autre objet est traité comme un plan de rayon donné.
    /// </summary>
    float MeasureGaps(Collider other, Vector3 n, Vector3 q, float radius, float reach, bool useRays)
    {
        float gapMax = float.NegativeInfinity;
        bool rays = useRays && other != null;

        foreach (Target tg in targets)
        {
            foreach (int i in tg.candidates)
            {
                Vector3 wp = tg.world[i];
                float gap = float.NegativeInfinity;

                if (rays)
                {
                    if (other.Raycast(new Ray(wp + n * reach, -n), out RaycastHit hit, reach * 2f))
                        gap = Vector3.Dot(hit.point - q, n) - tg.depth[i];
                }
                else if (float.IsPositiveInfinity(radius) || LateralDistance(wp, q, n) <= radius)
                {
                    gap = -tg.depth[i];
                }

                tg.push[i] = gap;
                if (gap > gapMax) gapMax = gap;
            }
        }
        return gapMax;
    }

    void ClearPush()
    {
        foreach (Target tg in targets)
            foreach (int i in tg.candidates)
                tg.push[i] = 0f;
    }

    static float LateralDistance(Vector3 p, Vector3 axisPoint, Vector3 n)
    {
        Vector3 r = p - axisPoint;
        return (r - n * Vector3.Dot(r, n)).magnitude;
    }

    /// <summary>Demi-largeur approximative d'un objet vu dans la direction n.</summary>
    static float LateralRadius(Bounds b, Vector3 n)
    {
        Vector3 e = b.extents;
        return (e - n * Vector3.Dot(e, n)).magnitude;
    }

    /// <summary>1 dans la zone de contact, puis décroissance douce jusqu'à 0 à la distance "spread".</summary>
    static float Falloff(float rho, float footprint, float width)
    {
        if (rho <= footprint) return 1f;
        if (width <= 0f) return 0f;
        float x = (rho - footprint) / width;
        if (x >= 1f) return 0f;
        return 1f - x * x * (3f - 2f * x);
    }

    // ------------------------------------------------------------------ Son, événement, retour à la forme

    void Feedback(Vector3 point, float applied, float speed, float force)
    {
        if (elasticity > 0f && recoverySpeed > 0f)
        {
            recovering = true;
            recoverAt = Time.time + recoveryDelay;
        }

        pendingSoundDepth += applied;
        if (pendingSoundDepth < SoundStep || Time.time - lastSoundTime < 0.1f) return;

        float strength = Mathf.Clamp01(pendingSoundDepth / 0.04f);
        if (debugLog)
            Debug.Log($"[RuntimeCrush] '{name}' écrasé de {pendingSoundDepth * 1000f:F0} mm "
                    + $"(vitesse {speed:F1} m/s, seuil {minVelocity:F1} — force {force:F0} N, seuil {resistance:F0})", this);

        pendingSoundDepth = 0f;
        lastSoundTime = Time.time;

        if (crushSounds != null && crushSounds.Length > 0)
        {
            PlayAt(crushSounds, point, soundVolume * Mathf.Lerp(0.4f, 1f, strength), pitchVariation);
            lastCrushSoundFrame = Time.frameCount;
        }
        onCrush?.Invoke(point);
    }

    void Update()
    {
        Recover();
        ApplyDirtyMeshes();
    }

    void Recover()
    {
        if (!recovering || Time.time < recoverAt) return;
        // Tant que quelque chose appuie encore dessus, l'objet reste écrasé.
        if (Time.time - lastPressureTime < recoveryDelay || StillLoaded()) return;

        float k = 1f - Mathf.Exp(-recoverySpeed * Time.deltaTime);
        bool stillMoving = false;

        foreach (Target tg in targets)
        {
            Vector3[] v = tg.current;
            Vector3[] rest = tg.rest;
            Vector3 ls = tg.t.lossyScale;
            float scale = Mathf.Max(1e-6f, (Mathf.Abs(ls.x) + Mathf.Abs(ls.y) + Mathf.Abs(ls.z)) / 3f);
            float eps = 0.0003f / scale; // en dessous de 0,3 mm, on considère le retour terminé
            float maxDiff = 0f;

            for (int i = 0; i < v.Length; i++)
            {
                Vector3 diff = rest[i] - v[i];
                float m = diff.sqrMagnitude;
                if (m == 0f) continue;
                v[i] += diff * k;
                if (m > maxDiff) maxDiff = m;
            }
            if (maxDiff == 0f) continue;

            if (maxDiff < eps * eps) System.Array.Copy(rest, v, v.Length);
            else stillMoving = true;

            tg.dirty = true;
            colliderDirty = true;
        }

        recovering = stillMoving;
    }

    bool StillLoaded()
    {
        for (int i = loaders.Count - 1; i >= 0; i--)
        {
            Collider c = loaders[i];
            if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) loaders.RemoveAt(i);
        }
        return loaders.Count > 0;
    }

    /// <summary>Rend immédiatement à l'objet sa forme d'origine.</summary>
    public void ResetShape()
    {
        recovering = false;
        foreach (Target tg in targets)
        {
            System.Array.Copy(tg.original, tg.rest, tg.original.Length);
            System.Array.Copy(tg.original, tg.current, tg.original.Length);
            tg.dirty = true;
        }
        colliderDirty = true;
    }

    /// <summary>Envoie les meshes modifiés à la carte graphique, une seule fois par image.</summary>
    void ApplyDirtyMeshes()
    {
        foreach (Target tg in targets)
        {
            if (!tg.dirty) continue;
            tg.dirty = false;
            tg.mesh.vertices = tg.current;
            tg.mesh.RecalculateNormals();
            tg.mesh.RecalculateBounds();
        }
    }

    // ------------------------------------------------------------------ Utilitaires

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
        src.volume = Mathf.Clamp01(volume);
        src.pitch = 1f + Random.Range(-pitchRange, pitchRange);
        src.spatialBlend = 1f;   // son entièrement spatialisé
        src.minDistance = 0.5f;
        src.maxDistance = 20f;
        src.Play();

        Destroy(go, clip.length / Mathf.Max(0.1f, src.pitch) + 0.1f);
    }
}