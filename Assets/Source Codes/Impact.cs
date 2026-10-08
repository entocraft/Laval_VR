using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Sons des objets qui ne cassent pas : chocs (chute, coup, objet reposé) et frottement quand l'objet glisse.
/// Le volume suit la force du choc ou la vitesse de glissement.
///
/// Mise en place :
///  - À placer sur l'objet qui porte le Rigidbody (la racine), avec ou sans RuntimeFracture / RuntimeCrush.
///  - Choisir un matériau : les sons sont chargés depuis Assets/Sounds/[matériau]/ selon leur nom :
///        SO_[matériau]_Impact_1, _2...   -> chocs
///        SO_[matériau]_Slide_1, _2...    -> glissement (sons continus, qui bouclent proprement)
///    Les fichiers SO_[matériau]_1, _2... restent réservés à la casse et à l'écrasement.
///  - Pour les objets tenus en main (kinematic) contre le décor :
///    Edit > Project Settings > Physics > Contact Pairs Mode = Enable All Contact Pairs.
///  - Nécessite RuntimeFracture.cs et RuntimeCrush.cs dans le projet.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody))]
public class RuntimeImpactSound : MonoBehaviour, IPointVelocity
{
    public enum Preset
    {
        [InspectorName("Personnalisé")] Personnalise,
        [InspectorName("Verre")] Verre,
        [InspectorName("Céramique")] Ceramique,
        [InspectorName("Plastique")] Plastique,
        [InspectorName("Bois")] Bois,
        [InspectorName("Pierre / béton")] Pierre,
        [InspectorName("Métal")] Metal,
        [InspectorName("Carton")] Carton,
        [InspectorName("Mousse")] Mousse,
        [InspectorName("Caoutchouc")] Caoutchouc
    }

    // Limites globales, tous objets confondus (évite la cacophonie et la surcharge audio).
    const int MaxVoices = 12;       // sons de choc joués en même temps
    const int MaxPerFrame = 3;      // sons de choc démarrés dans la même image
    const int MaxSlideVoices = 4;   // sons de glissement joués en même temps

    [Header("Preset")]
    [Tooltip("Choisir un matériau remplit les paramètres ci-dessous et charge ses sons. Les valeurs restent modifiables.")]
    public Preset preset = Preset.Personnalise;
    [SerializeField, HideInInspector] Preset appliedPreset = Preset.Personnalise;
    [Tooltip("Nom du dossier dans Assets/Sounds/ (rempli par le preset, modifiable). "
           + "Charge les clips SO_..._Impact_n et SO_..._Slide_n de ce dossier.")]
    [Delayed] public string soundMaterial = "";
    [SerializeField, HideInInspector] string appliedSoundMaterial = "";

    [Header("Chocs")]
    [Tooltip("Sons de choc. S'il y en a plusieurs, un clip est tiré au hasard à chaque fois.")]
    public AudioClip[] impactSounds;
    [Tooltip("Volume pour un choc à pleine force. Les petits chocs sont joués moins fort.")]
    [Range(0f, 1f)] public float impactVolume = 0.8f;
    [Tooltip("Vitesse d'impact (m/s) en dessous de laquelle aucun son n'est joué.")]
    public float minImpactSpeed = 0.4f;
    [Tooltip("Vitesse d'impact (m/s) à partir de laquelle le son est joué à plein volume.")]
    public float maxImpactSpeed = 4f;
    [Tooltip("Délai minimal (secondes) entre deux sons de choc du même objet.")]
    public float impactCooldown = 0.1f;

    [Header("Glissement")]
    [Tooltip("Sons de frottement, joués en boucle tant que l'objet glisse. Laisser vide pour désactiver.")]
    public AudioClip[] slideSounds;
    [Tooltip("Volume à pleine vitesse de glissement.")]
    [Range(0f, 1f)] public float slideVolume = 0.5f;
    [Tooltip("Vitesse de glissement (m/s) à partir de laquelle le frottement s'entend.")]
    public float slideMinSpeed = 0.25f;
    [Tooltip("Vitesse de glissement (m/s) à laquelle le frottement est à plein volume.")]
    public float slideMaxSpeed = 2.5f;

    [Header("Hauteur du son")]
    [Tooltip("Hauteur de base (1 = normale). Monter pour un petit objet, baisser pour un gros.")]
    [Range(0.5f, 2f)] public float pitch = 1f;
    [Tooltip("Variation aléatoire de hauteur (±) pour éviter la répétition.")]
    [Range(0f, 0.5f)] public float pitchVariation = 0.1f;

    [Header("Casse")]
    [Tooltip("Quand l'objet casse, nombre de gros fragments qui héritent des sons de choc (tintement des éclats qui retombent).")]
    [Range(0, 8)] public int fragmentSounds = 4;

    [Header("Filtre")]
    [Tooltip("Les collisions avec ces layers sont ignorées (corps du joueur, mains...).")]
    public LayerMask ignoreLayers;

    [Header("Diagnostic")]
    [Tooltip("Affiche dans la console la vitesse de chaque choc reçu.")]
    public bool debugLog;

    // ------------------------------------------------------------------ État interne

    Rigidbody rb;
    RuntimeFracture fracture;
    RuntimeCrush crush;
    float armedAt;
    float lastImpactTime = -10f;

    // Choc détecté pendant la physique, joué à l'image suivante une fois qu'on sait si l'objet a cassé.
    bool hasPending;
    float pendingSpeed;
    Vector3 pendingPoint;

    // Glissement
    AudioSource slideSource;
    bool sliding;
    float slideSpeed;
    float slideStamp = -1f;
    float lastSlideTime = -10f;

    // Vitesse de l'autre objet : composants gardés en mémoire tant qu'on touche le même Rigidbody.
    Rigidbody otherRb;
    IPointVelocity otherTracker;

    // Poses des derniers pas physiques, pour mesurer la vitesse réelle même tenu en main (kinematic).
    readonly Matrix4x4[] poses = new Matrix4x4[4];
    int poseHead;

    // Partagé entre tous les objets
    static readonly List<float> voiceEnds = new List<float>();
    static int stampFrame = -1;
    static int playedThisFrame;
    static int activeSlides;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        voiceEnds.Clear();
        stampFrame = -1;
        playedThisFrame = 0;
        activeSlides = 0;
    }

    // ------------------------------------------------------------------ Presets et chargement des sons

    void Reset()
    {
        // À l'ajout du composant : reprend le matériau déjà choisi sur le script de fracture ou d'écrasement.
        RuntimeFracture f = GetComponent<RuntimeFracture>();
        RuntimeCrush c = GetComponent<RuntimeCrush>();
        if (f != null && !string.IsNullOrWhiteSpace(f.soundMaterial)) soundMaterial = f.soundMaterial;
        else if (c != null && !string.IsNullOrWhiteSpace(c.soundMaterial)) soundMaterial = c.soundMaterial;

        appliedSoundMaterial = soundMaterial;
        ReloadSounds();
    }

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

    /// <summary>Recharge les sons de choc et de glissement depuis Assets/Sounds/[soundMaterial]/ (éditeur uniquement).</summary>
    [ContextMenu("Recharger les sons du dossier")]
    void ReloadSounds()
    {
#if UNITY_EDITOR
        if (string.IsNullOrWhiteSpace(soundMaterial)) return;
        // Dossier ou fichiers absents : listes vidées, pour ne pas garder les sons d'un autre matériau.
        impactSounds = RuntimeFracture.FindMaterialSounds(soundMaterial, "Impact", this) ?? new AudioClip[0];
        slideSounds = RuntimeFracture.FindMaterialSounds(soundMaterial, "Slide", this) ?? new AudioClip[0];
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    /// <summary>Remplit les paramètres selon un matériau type. "Personnalisé" ne change rien.</summary>
    public void ApplyPreset(Preset p)
    {
        switch (p)
        {
            //                                 dossier      volume  seuil  plein  variation  glissement
            case Preset.Verre: SetMaterial("Glass", 0.8f, 0.4f, 4f, 0.12f, 0.5f); break;
            case Preset.Ceramique: SetMaterial("Ceramic", 0.8f, 0.4f, 4f, 0.10f, 0.6f); break;
            case Preset.Plastique: SetMaterial("Plastic", 0.6f, 0.5f, 5f, 0.12f, 0.5f); break;
            case Preset.Bois: SetMaterial("Wood", 0.9f, 0.5f, 5f, 0.08f, 0.7f); break;
            case Preset.Pierre: SetMaterial("Stone", 1.0f, 0.5f, 5f, 0.06f, 0.8f); break;
            case Preset.Metal: SetMaterial("Metal", 1.0f, 0.4f, 5f, 0.08f, 0.8f); break;
            case Preset.Carton: SetMaterial("Cardboard", 0.5f, 0.6f, 5f, 0.10f, 0.5f); break;
            case Preset.Mousse: SetMaterial("Foam", 0.3f, 0.8f, 6f, 0.10f, 0.2f); break;
            case Preset.Caoutchouc: SetMaterial("Rubber", 0.5f, 0.6f, 6f, 0.10f, 0.4f); break;
        }
    }

    void SetMaterial(string sounds, float volume, float minSpeed, float maxSpeed, float variation, float slide)
    {
        soundMaterial = sounds;
        impactVolume = volume;
        minImpactSpeed = minSpeed;
        maxImpactSpeed = maxSpeed;
        pitchVariation = variation;
        slideVolume = slide;
    }

    // ------------------------------------------------------------------ Cycle de vie

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    void Start()
    {
        fracture = GetComponent<RuntimeFracture>();
        crush = GetComponent<RuntimeCrush>();
    }

    void OnEnable()
    {
        Matrix4x4 m = transform.localToWorldMatrix;
        for (int i = 0; i < poses.Length; i++) poses[i] = m;

        // Pas de son pendant que les objets se posent au chargement de la scène.
        armedAt = Mathf.Max(armedAt, Time.time + 0.5f);
    }

    void OnDisable()
    {
        hasPending = false;
        StopSlide();
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

    /// <summary>Vitesse de l'objet qu'on touche, au point de contact (nulle pour le décor).</summary>
    Vector3 OtherVelocity(Collision col, Vector3 point)
    {
        Rigidbody body = col.rigidbody;
        if (body == null) return Vector3.zero;

        if (body != otherRb)
        {
            otherRb = body;
            otherTracker = body.GetComponent<IPointVelocity>();
        }

        return otherTracker != null ? otherTracker.PointVelocity(point) : body.GetPointVelocity(point);
    }

    bool Ignored(Collision col)
    {
        return (ignoreLayers.value & (1 << col.collider.gameObject.layer)) != 0;
    }

    // ------------------------------------------------------------------ Chocs

    void OnCollisionEnter(Collision col)
    {
        if (Time.time < armedAt || col.contactCount == 0) return;
        if (impactSounds == null || impactSounds.Length == 0) return;
        if (Ignored(col)) return;

        ContactPoint contact = col.GetContact(0);
        Vector3 point = contact.point;

        // Normale orientée de l'obstacle vers cet objet.
        Vector3 n = contact.normal;
        if (Vector3.Dot(n, rb.worldCenterOfMass - point) < 0f) n = -n;

        // Vitesse d'approche mesurée par suivi de position, fiable aussi pour un objet tenu en main.
        float approach = Mathf.Max(0f, -Vector3.Dot(PointVelocity(point) - OtherVelocity(col, point), n));
        float speed = Mathf.Max(col.relativeVelocity.magnitude, approach);

        if (debugLog)
            Debug.Log($"[RuntimeImpactSound] '{name}' contre '{col.collider.name}' : {speed:F1} m/s "
                    + $"(seuil {minImpactSpeed:F1}, plein volume à {maxImpactSpeed:F1})", this);

        if (speed < minImpactSpeed) return;

        // Plusieurs chocs dans la même image : on ne garde que le plus fort.
        if (!hasPending || speed > pendingSpeed)
        {
            hasPending = true;
            pendingSpeed = speed;
            pendingPoint = point;
        }
    }

    void Update()
    {
        if (hasPending)
        {
            hasPending = false;

            // Si l'objet vient de casser ou de s'écraser, c'est ce son-là qui est joué, pas celui du choc.
            bool handledElsewhere = (fracture != null && fracture.IsBroken)
                                 || (crush != null && crush.lastCrushSoundFrame == Time.frameCount);
            if (!handledElsewhere) PlayImpact(pendingPoint, pendingSpeed);
        }

        UpdateSlide();
    }

    void PlayImpact(Vector3 point, float speed)
    {
        if (Time.time - lastImpactTime < impactCooldown) return;

        float force = Mathf.InverseLerp(minImpactSpeed, maxImpactSpeed, speed);
        float volume = impactVolume * Mathf.Lerp(0.15f, 1f, force);
        if (TryPlay(impactSounds, point, volume, pitch, pitchVariation)) lastImpactTime = Time.time;
    }

    /// <summary>Clic droit sur le composant en mode Play : joue un son de choc à pleine force.</summary>
    [ContextMenu("Jouer un son de choc (test)")]
    void TestImpact()
    {
        if (Application.isPlaying) TryPlay(impactSounds, transform.position, impactVolume, pitch, pitchVariation);
    }

    /// <summary>Joue un clip tiré au hasard en son 3D, si les limites globales le permettent.</summary>
    static bool TryPlay(AudioClip[] clips, Vector3 position, float volume, float basePitch, float pitchRange)
    {
        if (clips == null || clips.Length == 0) return false;

        if (stampFrame != Time.frameCount)
        {
            stampFrame = Time.frameCount;
            playedThisFrame = 0;
        }
        if (playedThisFrame >= MaxPerFrame) return false;

        float now = Time.time;
        for (int i = voiceEnds.Count - 1; i >= 0; i--)
            if (voiceEnds[i] <= now) voiceEnds.RemoveAt(i);
        if (voiceEnds.Count >= MaxVoices) return false;

        AudioClip clip = clips[Random.Range(0, clips.Length)];
        if (clip == null) return false;

        var go = new GameObject("ImpactAudio");
        go.transform.position = position;

        var src = go.AddComponent<AudioSource>();
        src.clip = clip;
        src.volume = Mathf.Clamp01(volume);
        src.pitch = Mathf.Max(0.1f, basePitch * (1f + Random.Range(-pitchRange, pitchRange)));
        src.spatialBlend = 1f;   // son entièrement spatialisé
        src.minDistance = 0.3f;
        src.maxDistance = 15f;
        src.Play();

        float duration = clip.length / src.pitch + 0.1f;
        Destroy(go, duration);
        voiceEnds.Add(now + duration);
        playedThisFrame++;
        return true;
    }

    // ------------------------------------------------------------------ Glissement

    void OnCollisionStay(Collision col)
    {
        if (slideSounds == null || slideSounds.Length == 0 || col.contactCount == 0) return;
        if (Ignored(col)) return;

        ContactPoint contact = col.GetContact(0);
        Vector3 point = contact.point;

        // Vitesse du point de contact le long de la surface. Un objet qui roule sans glisser donne ~0 :
        // pas de bruit de frottement dans ce cas.
        Vector3 v = PointVelocity(point) - OtherVelocity(col, point);
        Vector3 n = contact.normal;
        float speed = (v - n * Vector3.Dot(v, n)).magnitude;

        // Plusieurs contacts dans le même pas physique : on garde le plus rapide.
        if (Time.fixedTime != slideStamp || speed > slideSpeed) slideSpeed = speed;
        slideStamp = Time.fixedTime;
        lastSlideTime = Time.time;
    }

    void UpdateSlide()
    {
        bool hasClips = slideSounds != null && slideSounds.Length > 0;
        if (!sliding && !hasClips) return;

        bool touching = Time.time - lastSlideTime < 0.12f;
        float amount = touching && hasClips ? Mathf.InverseLerp(slideMinSpeed, slideMaxSpeed, slideSpeed) : 0f;
        float target = amount > 0f ? slideVolume * Mathf.Lerp(0.2f, 1f, amount) : 0f;

        if (!sliding)
        {
            if (target <= 0f || !StartSlide()) return;
        }

        // Volume lissé : le frottement monte et s'éteint progressivement au lieu de couper net.
        slideSource.volume = Mathf.MoveTowards(slideSource.volume, target, Time.deltaTime * 5f);
        if (amount > 0f) slideSource.pitch = pitch * Mathf.Lerp(0.9f, 1.15f, amount);

        if (target <= 0f && slideSource.volume <= 0.001f) StopSlide();
    }

    bool StartSlide()
    {
        if (activeSlides >= MaxSlideVoices) return false;

        AudioClip clip = slideSounds[Random.Range(0, slideSounds.Length)];
        if (clip == null) return false;

        if (slideSource == null)
        {
            slideSource = gameObject.AddComponent<AudioSource>();
            slideSource.playOnAwake = false;
            slideSource.loop = true;
            slideSource.spatialBlend = 1f;
            slideSource.minDistance = 0.3f;
            slideSource.maxDistance = 12f;
        }

        slideSource.clip = clip;
        slideSource.volume = 0f;
        slideSource.pitch = pitch;
        slideSource.Play();
        // Départ à un endroit aléatoire du clip, pour que deux objets ne sonnent pas à l'unisson.
        if (clip.length > 0.2f) slideSource.time = Random.value * (clip.length - 0.1f);

        sliding = true;
        activeSlides++;
        return true;
    }

    void StopSlide()
    {
        if (!sliding) return;
        sliding = false;
        activeSlides = Mathf.Max(0, activeSlides - 1);
        if (slideSource != null) slideSource.Stop();
    }

    // ------------------------------------------------------------------ Fragments

    /// <summary>
    /// Donne les sons de choc de cet objet à un fragment (appelé par RuntimeFracture à la casse).
    /// Version allégée : plus aigu, moins fort, sans glissement.
    /// </summary>
    public void CopyTo(GameObject fragment, float volumeScale)
    {
        if (impactSounds == null || impactSounds.Length == 0) return;

        var s = fragment.AddComponent<RuntimeImpactSound>();
        s.impactSounds = impactSounds;
        s.impactVolume = impactVolume * volumeScale;
        s.minImpactSpeed = minImpactSpeed;
        s.maxImpactSpeed = maxImpactSpeed;
        s.impactCooldown = impactCooldown * 2f;
        s.pitch = pitch * 1.25f;
        s.pitchVariation = pitchVariation + 0.05f;
        s.ignoreLayers = ignoreLayers;
        s.slideSounds = null;
        s.fragmentSounds = 0;
        s.armedAt = Time.time + 0.2f; // pas de tintement au moment où les éclats se séparent
    }
}