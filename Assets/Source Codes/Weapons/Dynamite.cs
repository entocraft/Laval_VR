using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR;

/// <summary>
/// Bâton de dynamite : une arme qu'on saisit, dont on allume la mèche, et qui explose à la fin du compte à rebours,
/// qu'elle soit encore en main ou déjà lancée. Pendant que la mèche brûle : étincelles qui descendent le long
/// de la mèche, sifflement de plus en plus aigu et vibrations de plus en plus rapprochées dans la main.
///
/// Mise en place, sur la racine de la dynamite (l'objet qui porte le Rigidbody et un collider) :
///  - WeaponGrab      : pour la saisir et la lancer.
///  - RuntimeExplosion: pour l'explosion (laisser Fuse On Start à 0 et Explode On Impact décoché).
///  - Dynamite        : ce script.
///  - Créer un objet vide au bout de la mèche et le glisser dans Fuse Tip. Optionnel : un second objet vide
///    là où la mèche entre dans le bâton, dans Fuse Base, pour que les étincelles descendent le long de la mèche.
///  - Son de mèche : les clips Assets/Sounds/Fuse/SO_Fuse_1, _2... sont chargés automatiquement.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(RuntimeExplosion))]
public class Dynamite : MonoBehaviour
{
    public enum Ignition
    {
        [InspectorName("Gâchette, dynamite en main")] Gachette,
        [InspectorName("Dès la saisie")] Saisie,
        [InspectorName("Au lâcher")] Lacher,
        [InspectorName("Manuel (depuis un autre script)")] Manuel
    }

    [Header("Mèche")]
    [Tooltip("Ce qui allume la mèche. Gâchette : appuyer sur la gâchette de l'index en tenant la dynamite.")]
    public Ignition ignition = Ignition.Gachette;
    [Tooltip("Durée (secondes) entre l'allumage et l'explosion.")]
    [Min(0.1f)] public float fuseTime = 4f;
    [Tooltip("Bout de la mèche, là où elle s'allume. Si vide, les étincelles partent de l'origine de l'objet.")]
    public Transform fuseTip;
    [Tooltip("Base de la mèche, là où elle entre dans le bâton (optionnel). Les étincelles descendent du bout jusqu'ici.")]
    public Transform fuseBase;

    [Header("Étincelles")]
    [Tooltip("Étincelles au point où la mèche brûle.")]
    public bool sparks = true;
    [Tooltip("Taille des étincelles.")]
    [Min(0.1f)] public float sparkScale = 1f;
    [Tooltip("Matériau de particules personnalisé (optionnel). Si vide, celui des explosions est utilisé.")]
    public Material sparkMaterial;

    [Header("Son")]
    [Tooltip("Nom du dossier dans Assets/Sounds/. Les clips SO_[nom]_n de ce dossier sont chargés automatiquement.")]
    [Delayed] public string soundMaterial = "Fuse";
    [SerializeField, HideInInspector] string appliedSoundMaterial = "";
    [Tooltip("Sifflement de la mèche, joué en boucle (un son continu, sans début ni fin marqués).")]
    public AudioClip[] fuseSounds;
    [Range(0f, 1f)] public float soundVolume = 0.7f;

    [Header("Vibrations")]
    [Tooltip("Petites vibrations dans la main qui tient la dynamite, de plus en plus rapprochées. 0 = aucune.")]
    [Range(0f, 1f)] public float hapticStrength = 0.4f;

    [Header("Événement")]
    [Tooltip("Appelé au moment où la mèche s'allume.")]
    public UnityEvent onIgnite;

    /// <summary>Vrai une fois la mèche allumée.</summary>
    public bool IsLit => lit;
    /// <summary>Avancement de la mèche, de 0 (allumage) à 1 (explosion).</summary>
    public float Progress => lit ? Mathf.Clamp01(elapsed / fuseTime) : 0f;

    // ------------------------------------------------------------------ État interne

    RuntimeExplosion explosion;
    WeaponGrab grab;
    bool lit;
    float elapsed;
    float nextPulse;
    Transform sparkRoot;
    AudioSource hiss;

    // ------------------------------------------------------------------ Sons (éditeur)

    void OnValidate()
    {
        if (soundMaterial == appliedSoundMaterial) return;
        appliedSoundMaterial = soundMaterial;
        ReloadSounds();
    }

    /// <summary>Recharge la liste de sons depuis Assets/Sounds/[soundMaterial]/ (éditeur uniquement).</summary>
    [ContextMenu("Recharger les sons du dossier")]
    void ReloadSounds()
    {
#if UNITY_EDITOR
        AudioClip[] found = RuntimeFracture.FindMaterialSounds(soundMaterial, this);
        if (found == null) return;
        fuseSounds = found;
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    // ------------------------------------------------------------------ Allumage

    void Awake()
    {
        explosion = GetComponent<RuntimeExplosion>();
        grab = GetComponent<WeaponGrab>();

        if (grab != null)
        {
            grab.onGrab.AddListener(OnGrabbed);
            grab.onRelease.AddListener(OnReleased);
        }
        else if (ignition != Ignition.Manuel)
        {
            Debug.LogWarning($"[Dynamite] '{name}' n'a pas de WeaponGrab : la mèche ne peut être allumée que par Ignite().", this);
        }
    }

    void OnDestroy()
    {
        if (grab == null) return;
        grab.onGrab.RemoveListener(OnGrabbed);
        grab.onRelease.RemoveListener(OnReleased);
    }

    void OnGrabbed()
    {
        if (ignition == Ignition.Saisie) Ignite();
    }

    void OnReleased()
    {
        if (ignition == Ignition.Lacher) Ignite();
    }

    /// <summary>Allume la mèche. Sans effet si elle brûle déjà.</summary>
    [ContextMenu("Allumer la mèche (test)")]
    public void Ignite()
    {
        if (lit || !Application.isPlaying || explosion.HasExploded) return;
        lit = true;
        elapsed = 0f;
        nextPulse = 0f;

        if (sparks) CreateSparks();
        StartHiss();
        onIgnite?.Invoke();
    }

    void Update()
    {
        if (!lit)
        {
            if (ignition == Ignition.Gachette && grab != null && grab.IsHeld && TriggerPressed(grab.Holder)) Ignite();
            return;
        }

        elapsed += Time.deltaTime;
        float t = Mathf.Clamp01(elapsed / fuseTime);

        // Les étincelles suivent le point où la mèche brûle.
        if (sparkRoot != null) sparkRoot.position = BurnPoint(t);

        // Le sifflement monte dans les aigus à l'approche de l'explosion.
        if (hiss != null) hiss.pitch = Mathf.Lerp(1f, 1.35f, t);

        // Vibrations de plus en plus rapprochées dans la main qui tient la dynamite.
        if (hapticStrength > 0f && grab != null && grab.IsHeld && elapsed >= nextPulse)
        {
            grab.Holder.Vibrate(hapticStrength * Mathf.Lerp(0.5f, 1f, t), 0.04f);
            nextPulse = elapsed + Mathf.Lerp(0.5f, 0.07f, t);
        }

        if (elapsed >= fuseTime)
        {
            lit = false;
            explosion.Explode();
        }
    }

    static bool TriggerPressed(WeaponHand hand)
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(hand.IsLeft ? XRNode.LeftHand : XRNode.RightHand);
        if (!device.isValid) return false;

        if (device.TryGetFeatureValue(CommonUsages.trigger, out float value)) return value >= 0.5f;
        return device.TryGetFeatureValue(CommonUsages.triggerButton, out bool button) && button;
    }

    Vector3 BurnPoint(float t)
    {
        Vector3 tip = fuseTip != null ? fuseTip.position : transform.position;
        return fuseBase != null ? Vector3.Lerp(tip, fuseBase.position, t) : tip;
    }

    // ------------------------------------------------------------------ Effets de la mèche

    void StartHiss()
    {
        if (fuseSounds == null || fuseSounds.Length == 0) return;
        AudioClip clip = fuseSounds[Random.Range(0, fuseSounds.Length)];
        if (clip == null) return;

        hiss = gameObject.AddComponent<AudioSource>();
        hiss.clip = clip;
        hiss.loop = true;
        hiss.volume = soundVolume;
        hiss.spatialBlend = 1f;   // son spatialisé : on entend d'où vient la dynamite
        hiss.minDistance = 0.3f;
        hiss.maxDistance = 15f;
        hiss.Play();
    }

    /// <summary>Petit jet d'étincelles continu, enfant de la dynamite : il disparaît avec elle à l'explosion.</summary>
    void CreateSparks()
    {
        Material mat = sparkMaterial != null ? sparkMaterial : RuntimeExplosion.DefaultParticleMaterial();
        if (mat == null) return;

        var go = new GameObject("FX_Meche");
        sparkRoot = go.transform;
        sparkRoot.SetParent(transform, false);
        sparkRoot.position = BurnPoint(0f);

        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); // à l'arrêt pendant le paramétrage

        var main = ps.main;
        main.playOnAwake = false;
        main.loop = true;
        main.duration = 1f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.15f, 0.45f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 1.8f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.008f * sparkScale, 0.025f * sparkScale);
        main.startColor = Color.white;
        main.gravityModifier = 0.6f;
        main.simulationSpace = ParticleSystemSimulationSpace.World; // les étincelles restent en arrière quand on bouge
        main.maxParticles = 80;

        var emission = ps.emission;
        emission.rateOverTime = 70f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.004f;

        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(new Color(1f, 0.97f, 0.7f), 0f), new GradientColorKey(new Color(1f, 0.6f, 0.15f), 0.4f),
                    new GradientColorKey(new Color(0.8f, 0.2f, 0.05f), 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.5f), new GradientAlphaKey(0f, 1f) });
        var colorOver = ps.colorOverLifetime;
        colorOver.enabled = true;
        colorOver.color = new ParticleSystem.MinMaxGradient(gradient);

        var rend = go.GetComponent<ParticleSystemRenderer>();
        rend.sharedMaterial = mat;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows = false;

        ps.Play();
    }

    // ------------------------------------------------------------------ Aide au réglage dans l'éditeur

    void OnDrawGizmosSelected()
    {
        Vector3 tip = fuseTip != null ? fuseTip.position : transform.position;
        Gizmos.color = new Color(1f, 0.6f, 0.1f);
        Gizmos.DrawWireSphere(tip, 0.01f);
        if (fuseBase != null)
        {
            Gizmos.DrawLine(tip, fuseBase.position);
            Gizmos.DrawWireSphere(fuseBase.position, 0.006f);
        }
    }
}