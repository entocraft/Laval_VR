using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Écrasement d'un objet à l'impact : les sommets proches du choc sont enfoncés vers l'intérieur.
/// Selon l'élasticité, la déformation reste (canette), revient en partie (bouteille plastique)
/// ou disparaît complètement (mousse qui se regonfle).
///
/// Mise en place :
///  - À placer sur l'objet qui porte le Rigidbody (la racine). Le mesh peut être sur un enfant.
///  - Le mesh doit avoir "Read/Write Enabled" coché dans ses paramètres d'import.
///  - Le mesh doit être assez dense : on ne peut plier que là où il y a des sommets.
///  - Fonctionne avec RuntimeFracture.cs (présent dans le projet) pour connaître
///    la vitesse des objets tenus en main qui viennent frapper celui-ci.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class RuntimeCrush : MonoBehaviour
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

    [Header("Preset")]
    [Tooltip("Choisir un preset remplit les paramètres ci-dessous, qui restent modifiables ensuite.")]
    public Preset preset = Preset.Personnalise;
    [SerializeField, HideInInspector] Preset appliedPreset = Preset.Personnalise;

    [Header("Déclenchement")]
    [Tooltip("Vitesse d'impact (m/s) en dessous de laquelle rien ne se déforme.")]
    public float minVelocity = 1f;
    [Tooltip("Les collisions avec ces layers sont ignorées (corps du joueur, mains...).")]
    public LayerMask ignoreLayers;

    [Header("Déformation")]
    [Tooltip("Rayon de la zone enfoncée autour du point d'impact (mètres).")]
    public float dentRadius = 0.06f;
    [Tooltip("Profondeur d'enfoncement (mètres) par m/s au-dessus du seuil.")]
    public float depthPerSpeed = 0.008f;
    [Tooltip("Profondeur maximale d'un seul impact (mètres).")]
    public float maxDent = 0.02f;
    [Tooltip("Déformation maximale cumulée par rapport à la forme d'origine (mètres). "
           + "À garder proche du rayon de l'objet pour éviter que les parois se traversent trop.")]
    public float maxDeformation = 0.03f;
    [Tooltip("0 = enfoncement lisse, 1 = surface très froissée.")]
    [Range(0f, 1f)] public float crumple = 0.4f;
    [Tooltip("Finesse des plis (plus grand = plis plus petits).")]
    public float crumpleScale = 60f;

    [Header("Retour à la forme")]
    [Tooltip("Part de la déformation qui disparaît après le choc. "
           + "0 = tout reste (canette), 0,25 = léger rebond (plastique), 1 = se regonfle entièrement (mousse).")]
    [Range(0f, 1f)] public float elasticity = 0f;
    [Tooltip("Rapidité du retour. 15 = quasi instantané (caoutchouc), 4 = environ une demi-seconde (mousse), 1 = lent.")]
    public float recoverySpeed = 4f;
    [Tooltip("Temps d'attente (secondes) avant que l'objet commence à reprendre sa forme.")]
    public float recoveryDelay = 0.1f;

    [Header("Collider")]
    [Tooltip("Met à jour le MeshCollider après chaque impact (coûteux, à éviter sur Quest).")]
    public bool updateMeshCollider;

    [Header("Son")]
    [Tooltip("Nom du dossier dans Assets/Sounds/ (rempli par le preset, modifiable). "
           + "Les clips SO_[matériau]_n de ce dossier sont chargés automatiquement dans la liste ci-dessous "
           + "(ceux nommés _Impact_ ou _Slide_ sont réservés au script RuntimeImpactSound).")]
    [Delayed] public string soundMaterial = "";
    [SerializeField, HideInInspector] string appliedSoundMaterial = "";
    [Tooltip("Sons d'écrasement. S'il y en a plusieurs, un clip est tiré au hasard à chaque fois.")]
    public AudioClip[] crushSounds;
    [Tooltip("Volume pour un coup à pleine force. Les petits chocs sont joués moins fort.")]
    [Range(0f, 1f)] public float soundVolume = 1f;
    [Tooltip("Variation aléatoire de hauteur (±) pour éviter la répétition.")]
    [Range(0f, 0.5f)] public float pitchVariation = 0.1f;

    [Header("Événement")]
    [Tooltip("Appelé à chaque écrasement avec le point d'impact (monde) : son, haptique...")]
    public UnityEvent<Vector3> onCrush;

    [Header("Diagnostic")]
    public bool debugLog;

    class Target
    {
        public Transform t;
        public Mesh mesh;
        public Vector3[] original; // forme d'origine
        public Vector3[] rest;     // forme vers laquelle l'objet revient (déformation permanente)
        public Vector3[] current;  // forme affichée
        public MeshCollider collider;
    }

    readonly List<Target> targets = new List<Target>();
    readonly Matrix4x4[] poses = new Matrix4x4[4];
    int poseHead;
    Rigidbody rb;
    bool recovering;

    /// <summary>Image où le dernier son d'écrasement a été joué (lu par RuntimeImpactSound pour éviter un doublon).</summary>
    [System.NonSerialized] public int lastCrushSoundFrame = -1;
    float recoverAt;

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
        crushSounds = found;
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    /// <summary>Remplit les paramètres selon un matériau type. "Personnalisé" ne change rien.</summary>
    public void ApplyPreset(Preset p)
    {
        switch (p)
        {
            //                                         seuil  rayon  prof/vit  max coup  max total  froissé  plis  élast.  retour  délai
            case Preset.BouteillePlastique: SetMaterial(1.0f, 0.06f, 0.008f, 0.020f, 0.030f, 0.5f, 60f, 0.25f, 6f, 0.05f, "Plastic"); break;
            case Preset.Canette: SetMaterial(1.5f, 0.04f, 0.006f, 0.015f, 0.028f, 0.25f, 80f, 0f, 0f, 0f, "Metal"); break;
            case Preset.Carton: SetMaterial(1.0f, 0.12f, 0.010f, 0.040f, 0.080f, 0.2f, 25f, 0.1f, 3f, 0.1f, "Cardboard"); break;
            case Preset.Tole: SetMaterial(3.0f, 0.10f, 0.004f, 0.020f, 0.060f, 0.1f, 20f, 0f, 0f, 0f, "Metal"); break;
            case Preset.Mousse: SetMaterial(0.5f, 0.12f, 0.015f, 0.060f, 0.080f, 0f, 30f, 1f, 4f, 0.15f, "Foam"); break;
            case Preset.MousseMemoire: SetMaterial(0.5f, 0.12f, 0.015f, 0.060f, 0.080f, 0f, 30f, 1f, 1f, 0.5f, "Foam"); break;
            case Preset.Caoutchouc: SetMaterial(1.0f, 0.08f, 0.010f, 0.030f, 0.040f, 0f, 30f, 1f, 15f, 0f, "Rubber"); break;
        }
    }

    void SetMaterial(float velocity, float radius, float depthSpeed, float dent, float total,
                     float crumpleAmount, float crumpleSize, float elastic, float recovery, float delay,
                     string sounds)
    {
        soundMaterial = sounds;
        minVelocity = velocity;
        dentRadius = radius;
        depthPerSpeed = depthSpeed;
        maxDent = dent;
        maxDeformation = total;
        crumple = crumpleAmount;
        crumpleScale = crumpleSize;
        elasticity = elastic;
        recoverySpeed = recovery;
        recoveryDelay = delay;
    }

    // ------------------------------------------------------------------ Initialisation

    void Awake()
    {
        rb = GetComponent<Rigidbody>();

        // Le mesh de cet objet va être déformé, donc propre à lui : la fracture ne doit pas le mettre en cache.
        RuntimeFracture fracture = GetComponent<RuntimeFracture>();
        if (fracture != null) fracture.hasUniqueMesh = true;

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

            MeshCollider mc = mf.GetComponent<MeshCollider>();
            targets.Add(new Target
            {
                t = mf.transform,
                mesh = inst,
                original = inst.vertices,
                rest = inst.vertices,
                current = inst.vertices,
                collider = mc != null && mc.sharedMesh == shared ? mc : null
            });
        }
    }

    void OnEnable()
    {
        Matrix4x4 m = transform.localToWorldMatrix;
        for (int i = 0; i < poses.Length; i++) poses[i] = m;
    }

    void OnDestroy()
    {
        foreach (Target tg in targets)
            if (tg.mesh != null) Destroy(tg.mesh);
    }

    // ------------------------------------------------------------------ Suivi de vitesse

    void FixedUpdate()
    {
        poseHead = (poseHead + 1) % poses.Length;
        poses[poseHead] = transform.localToWorldMatrix;
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

    // ------------------------------------------------------------------ Impact

    void OnCollisionEnter(Collision col)
    {
        if (targets.Count == 0 || col.contactCount == 0) return;
        if ((ignoreLayers.value & (1 << col.collider.gameObject.layer)) != 0) return;

        ContactPoint contact = col.GetContact(0);
        Vector3 point = contact.point;

        // Normale orientée de l'obstacle vers cet objet : c'est le sens de l'enfoncement.
        Vector3 n = contact.normal;
        if (Vector3.Dot(n, rb.worldCenterOfMass - point) < 0f) n = -n;

        // Vitesse de ce qui nous a frappés (y compris un outil tenu en main).
        Vector3 otherVel = Vector3.zero;
        RuntimeFracture otherFracture = col.collider.GetComponentInParent<RuntimeFracture>();
        RuntimeCrush otherCrush = col.collider.GetComponentInParent<RuntimeCrush>();
        if (otherFracture != null) otherVel = otherFracture.PointVelocity(point);
        else if (otherCrush != null) otherVel = otherCrush.PointVelocity(point);
        else if (col.rigidbody != null) otherVel = col.rigidbody.GetPointVelocity(point);

        float trackedSpeed = Mathf.Max(0f, -Vector3.Dot(PointVelocity(point) - otherVel, n));
        float speed = Mathf.Max(col.relativeVelocity.magnitude, trackedSpeed);

        if (debugLog)
            Debug.Log($"[RuntimeCrush] '{name}' contre '{col.collider.name}' : {speed:F1} m/s (seuil {minVelocity:F1})", this);

        if (speed < minVelocity) return;

        float depth = Mathf.Min(maxDent, (speed - minVelocity) * depthPerSpeed);
        Crush(point, n, depth);
    }

    /// <summary>Clic droit sur le composant en mode Play : enfonce le côté de l'objet.</summary>
    [ContextMenu("Écraser maintenant (test)")]
    void CrushNow()
    {
        if (!Application.isPlaying) return;
        Vector3 dir = -transform.right;
        Crush(rb.worldCenterOfMass - dir * dentRadius * 0.5f, dir, maxDent);
    }

    // ------------------------------------------------------------------ Déformation

    /// <summary>
    /// Enfonce l'objet autour d'un point (monde), dans une direction (monde), d'une profondeur en mètres.
    /// Peut être appelé depuis un autre script (par exemple quand le joueur serre la gâchette).
    /// </summary>
    public void Crush(Vector3 worldPoint, Vector3 worldDirection, float depth)
    {
        if (depth <= 0f) return;
        bool any = false;

        foreach (Target tg in targets)
        {
            float scale = WorldScale(tg.t);

            // Passage en coordonnées locales du mesh.
            Vector3 lp = tg.t.InverseTransformPoint(worldPoint);
            Vector3 ld = tg.t.InverseTransformDirection(worldDirection.normalized);
            float r = dentRadius / scale;
            float d = depth / scale;
            float maxDef = maxDeformation / scale;
            float r2 = r * r;

            Vector3[] v = tg.current;
            Vector3[] o = tg.original;
            Vector3[] rest = tg.rest;
            bool changed = false;

            for (int i = 0; i < v.Length; i++)
            {
                float dist2 = (v[i] - lp).sqrMagnitude;
                if (dist2 > r2) continue;

                // Enfoncement maximal au centre, nul au bord de la zone.
                float falloff = 1f - Mathf.Sqrt(dist2) / r;
                falloff *= falloff;

                // Froissement : variation fixée par la position d'origine du sommet,
                // donc identique pour les sommets dupliqués (pas de déchirure aux coutures).
                Vector3 q = o[i] * (crumpleScale * scale);
                float noise = Mathf.PerlinNoise(q.x + q.z * 0.7f + 31.4f, q.y - q.z * 0.7f + 12.7f);
                float k = Mathf.Lerp(1f, noise * 2f, crumple);

                Vector3 moved = v[i] + ld * (d * falloff * k);

                // Limite cumulée par rapport à la forme d'origine.
                Vector3 offset = moved - o[i];
                if (offset.sqrMagnitude > maxDef * maxDef)
                    moved = o[i] + offset.normalized * maxDef;

                // Seule la part non élastique du coup devient permanente.
                rest[i] += (moved - v[i]) * (1f - elasticity);
                v[i] = moved;
                changed = true;
            }

            if (!changed) continue;
            any = true;
            Apply(tg, updateMeshCollider);
        }

        if (!any) return;

        if (elasticity > 0f && recoverySpeed > 0f)
        {
            recovering = true;
            recoverAt = Time.time + recoveryDelay;
        }
        float force = Mathf.Clamp01(depth / Mathf.Max(1e-6f, maxDent));
        PlayAt(crushSounds, worldPoint, soundVolume * Mathf.Lerp(0.4f, 1f, force), pitchVariation);
        if (crushSounds != null && crushSounds.Length > 0) lastCrushSoundFrame = Time.frameCount;
        onCrush?.Invoke(worldPoint);
    }

    // ------------------------------------------------------------------ Retour à la forme

    void Update()
    {
        if (!recovering || Time.time < recoverAt) return;

        float k = 1f - Mathf.Exp(-recoverySpeed * Time.deltaTime);
        bool stillMoving = false;

        foreach (Target tg in targets)
        {
            Vector3[] v = tg.current;
            Vector3[] rest = tg.rest;
            float eps = 0.0003f / WorldScale(tg.t); // en dessous de 0,3 mm, on considère le retour terminé
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

            bool finished = maxDiff < eps * eps;
            if (finished) System.Array.Copy(rest, v, v.Length);
            else stillMoving = true;

            // Le collider n'est remis à jour qu'une fois la forme stabilisée.
            Apply(tg, finished && updateMeshCollider);
        }

        recovering = stillMoving;
    }

    /// <summary>Rend immédiatement à l'objet sa forme d'origine.</summary>
    public void ResetShape()
    {
        recovering = false;
        foreach (Target tg in targets)
        {
            System.Array.Copy(tg.original, tg.rest, tg.original.Length);
            System.Array.Copy(tg.original, tg.current, tg.original.Length);
            Apply(tg, updateMeshCollider);
        }
    }

    // ------------------------------------------------------------------ Utilitaires

    static void Apply(Target tg, bool refreshCollider)
    {
        tg.mesh.vertices = tg.current;
        tg.mesh.RecalculateNormals();
        tg.mesh.RecalculateBounds();

        if (refreshCollider && tg.collider != null)
        {
            tg.collider.sharedMesh = null;
            tg.collider.sharedMesh = tg.mesh;
        }
    }

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

    static float WorldScale(Transform t)
    {
        Vector3 s = t.lossyScale;
        return Mathf.Max(1e-6f, (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f);
    }
}