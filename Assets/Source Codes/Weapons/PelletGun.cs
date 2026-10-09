using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR;

/// <summary>
/// Pistolet à billes. Les billes partent à grande vitesse et ne peuvent pas traverser un objet sans le toucher :
/// tant qu'elles sont rapides, elles ne sont pas confiées au moteur physique. Le script balaie lui-même, à chaque
/// image, tout le trajet parcouru par la bille (avec son épaisseur) et traite le premier obstacle rencontré,
/// puis le reste du trajet après le rebond, dans la même image. Une bille tirée à bout portant, canon enfoncé
/// dans un objet, touche aussi.
///
/// À l'impact : casse les objets fragiles (script de casse), marque les objets déformables (script d'écrasement),
/// pousse ce qui a un Rigidbody, déclenche les explosifs, puis ricoche. Une fois ralentie, la bille devient
/// un vrai petit objet physique qui roule au sol avant de disparaître.
///
/// Mise en place, sur la racine du pistolet (l'objet qui porte le Rigidbody et un collider) :
///  - WeaponGrab : pour le saisir. Le tir se fait avec la gâchette de l'index, pistolet en main.
///  - PelletGun  : ce script.
///  - Créer un objet vide au bout du canon, son axe bleu (Z) pointant dans le sens du tir, et le glisser dans Muzzle.
///  - Sons : Assets/Sounds/Gun/SO_Gun_1, _2... pour le tir, SO_Gun_Impact_1, _2... pour les impacts.
///  - Nécessite WeaponGrab, WeaponHand, RuntimeFracture, RuntimeCrush, RuntimeExplosion et RuntimeImpactSound.
/// </summary>
[DisallowMultipleComponent]
public class PelletGun : MonoBehaviour
{
    [Header("Tir")]
    [Tooltip("Bout du canon. Les billes partent de ce point, dans la direction de son axe bleu (Z).")]
    public Transform muzzle;
    [Tooltip("Vitesse des billes à la sortie du canon (m/s). Une réplique d'airsoft tire autour de 90 à 120 m/s.")]
    public float muzzleVelocity = 90f;
    [Tooltip("Coché : tir en rafale tant que la gâchette est enfoncée. Décoché : une bille par pression.")]
    public bool automatic;
    [Tooltip("Cadence maximale (billes par seconde).")]
    public float fireRate = 8f;
    [Tooltip("Dispersion (degrés) : 0 = toutes les billes partent exactement dans l'axe du canon.")]
    [Range(0f, 10f)] public float spread = 0.6f;

    [Header("Bille")]
    [Tooltip("Diamètre de la bille (mètres). 0,006 = bille d'airsoft de 6 mm.")]
    public float pelletDiameter = 0.006f;
    [Tooltip("Masse de la bille (kg). Détermine la force avec laquelle elle pousse ce qu'elle touche.")]
    public float pelletMass = 0.001f;
    [Tooltip("Modèle de bille personnalisé (optionnel). Si vide, une petite sphère est créée.")]
    public GameObject pelletPrefab;
    [Tooltip("Matériau de la sphère créée automatiquement (optionnel).")]
    public Material pelletMaterial;
    [Tooltip("Traînée lumineuse derrière la bille, pour suivre le tir à l'œil.")]
    public bool tracer = true;
    [Tooltip("Couleur de la traînée.")]
    public Color tracerColor = new Color(1f, 0.9f, 0.6f, 0.8f);

    [Header("Trajectoire")]
    [Tooltip("Effet de la gravité sur les billes. 1 = normale, 0 = tir parfaitement droit.")]
    [Range(0f, 2f)] public float gravityScale = 1f;
    [Tooltip("Freinage de l'air : part de vitesse perdue par seconde de vol.")]
    [Range(0f, 2f)] public float airDrag = 0.3f;
    [Tooltip("Layers que les billes peuvent toucher.")]
    public LayerMask hitLayers = ~0;
    [Tooltip("Durée de vol maximale (secondes) d'une bille qui ne touche rien.")]
    public float maxFlightTime = 5f;

    [Header("Impact")]
    [Tooltip("Part de la vitesse de la bille qui compte comme force de choc pour casser un objet. "
           + "0,06 : une bille à 90 m/s frappe comme un choc à 5,4 m/s, à comparer au Break Velocity des objets.")]
    [Range(0.01f, 0.5f)] public float impactFactor = 0.06f;
    [Tooltip("Solidité de la bille : elle ne casse que les objets de solidité inférieure ou égale "
           + "(verre 1, céramique 2, plastique 3, bois 5).")]
    public float solidity = 3f;
    [Tooltip("Multiplie la poussée exercée sur les objets touchés. 1 = réaliste (une bille pousse très peu).")]
    public float knockback = 3f;
    [Tooltip("Profondeur (mètres) de la marque laissée sur un objet déformable, pour une bille à pleine vitesse. 0 = aucune.")]
    public float dentDepth = 0.006f;
    [Tooltip("Rayon (mètres) de cette marque.")]
    public float dentRadius = 0.015f;

    [Header("Ricochet")]
    [Tooltip("Part de la vitesse conservée dans le rebond, perpendiculairement à la surface.")]
    [Range(0f, 1f)] public float bounciness = 0.3f;
    [Tooltip("Part de la vitesse perdue le long de la surface à chaque impact.")]
    [Range(0f, 1f)] public float friction = 0.3f;
    [Tooltip("Part de la vitesse conservée par une bille qui traverse un objet qu'elle vient de casser.")]
    [Range(0f, 1f)] public float breakThrough = 0.5f;
    [Tooltip("Nombre de ricochets rapides avant que la bille ne devienne un simple objet qui roule.")]
    [Range(0, 6)] public int maxRicochets = 3;

    [Header("Billes au sol")]
    [Tooltip("Durée (secondes) pendant laquelle une bille ralentie reste au sol avant de disparaître.")]
    public float restLifetime = 6f;
    [Tooltip("Nombre maximal de billes présentes en même temps (en vol et au sol). Au-delà, les plus anciennes disparaissent.")]
    [Min(1)] public int maxPellets = 120;

    [Header("Son")]
    [Tooltip("Nom du dossier dans Assets/Sounds/. SO_[nom]_n : tir. SO_[nom]_Impact_n : impact des billes.")]
    [Delayed] public string soundMaterial = "Gun";
    [SerializeField, HideInInspector] string appliedSoundMaterial = "";
    [Tooltip("Sons de tir. S'il y en a plusieurs, un clip est tiré au hasard à chaque fois.")]
    public AudioClip[] shotSounds;
    [Tooltip("Sons d'impact par défaut. Si l'objet touché porte le script de sons, c'est son propre bruit de choc qui est joué.")]
    public AudioClip[] impactSounds;
    [Range(0f, 1f)] public float shotVolume = 0.8f;
    [Range(0f, 1f)] public float impactVolume = 0.5f;
    [Tooltip("Variation aléatoire de hauteur (±) des sons de tir.")]
    [Range(0f, 0.5f)] public float pitchVariation = 0.06f;

    [Header("Effets")]
    [Tooltip("Petite gerbe d'étincelles à chaque impact rapide.")]
    public bool impactSparks = true;
    [Tooltip("Vibration de la manette à chaque tir. 0 = aucune.")]
    [Range(0f, 1f)] public float shotHaptic = 0.5f;

    [Header("Événements")]
    [Tooltip("Appelé à chaque tir.")]
    public UnityEvent onFire;
    [Tooltip("Appelé à chaque impact rapide, avec le point touché (monde).")]
    public UnityEvent<Vector3> onImpact;

    [Header("Diagnostic")]
    [Tooltip("Affiche dans la console chaque impact : objet touché, vitesse de la bille et force de choc équivalente.")]
    public bool debugLog;

    // ------------------------------------------------------------------ État interne

    const float HandoffSpeed = 3f;   // en dessous, la bille est confiée au moteur physique
    const float RestSpeed = 1.5f;    // vitesse maximale d'une bille devenue objet physique (inoffensive)
    const float SparkSpeed = 8f;     // en dessous, pas d'étincelles ni de son d'impact

    class Pellet
    {
        public GameObject go;
        public Transform tf;
        public Rigidbody rb;
        public Collider col;
        public TrailRenderer trail;

        public bool flying;
        public Vector3 pos;
        public Vector3 vel;
        public float bornAt;
        public float restUntil;
        public int ricochets;
        public Rigidbody skip;   // objet que la bille vient de casser et qu'elle traverse
        public int skipFrame;
    }

    readonly List<Pellet> pellets = new List<Pellet>();   // en vol ou au sol, de la plus ancienne à la plus récente
    readonly Stack<Pellet> pool = new Stack<Pellet>();
    readonly HashSet<Collider> ignored = new HashSet<Collider>();          // pistolet et joueur
    readonly HashSet<Collider> pelletColliders = new HashSet<Collider>();  // les billes ne se gênent pas entre elles
    readonly RaycastHit[] hits = new RaycastHit[24];

    WeaponGrab grab;
    WeaponHand ignoredFor;
    Collider[] ownColliders;
    AudioSource shotSource;
    ParticleSystem sparkSystem;
    Material fxMaterial;
    bool triggerDown;
    float nextShot;
    float lastImpactSound = -10f;

    // ------------------------------------------------------------------ Sons (éditeur)

    void OnValidate()
    {
        if (soundMaterial == appliedSoundMaterial) return;
        appliedSoundMaterial = soundMaterial;
        ReloadSounds();
    }

    /// <summary>Recharge les sons de tir et d'impact depuis Assets/Sounds/[soundMaterial]/ (éditeur uniquement).</summary>
    [ContextMenu("Recharger les sons du dossier")]
    void ReloadSounds()
    {
#if UNITY_EDITOR
        if (string.IsNullOrWhiteSpace(soundMaterial)) return;
        AudioClip[] shots = RuntimeFracture.FindMaterialSounds(soundMaterial, this);
        if (shots != null) shotSounds = shots;
        AudioClip[] impacts = RuntimeFracture.FindMaterialSounds(soundMaterial, "Impact", this);
        if (impacts != null) impactSounds = impacts;
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    // ------------------------------------------------------------------ Cycle de vie

    void Awake()
    {
        grab = GetComponent<WeaponGrab>();
        ownColliders = GetComponentsInChildren<Collider>(true);
        RebuildIgnored(null);

        if (grab == null)
            Debug.LogWarning($"[PelletGun] '{name}' n'a pas de WeaponGrab : le tir ne peut être déclenché que par Fire().", this);
    }

    void OnDestroy()
    {
        foreach (Pellet p in pellets) if (p.go != null) Destroy(p.go);
        foreach (Pellet p in pool) if (p.go != null) Destroy(p.go);
        if (sparkSystem != null) Destroy(sparkSystem.gameObject);
    }

    /// <summary>Les billes ignorent le pistolet lui-même et le joueur qui le tient.</summary>
    void RebuildIgnored(WeaponHand hand)
    {
        ignoredFor = hand;
        ignored.Clear();
        foreach (Collider c in ownColliders) if (c != null) ignored.Add(c);
        if (hand == null) return;
        foreach (Collider c in hand.PlayerColliders) if (c != null) ignored.Add(c);
    }

    // ------------------------------------------------------------------ Gâchette et tir

    void Update()
    {
        ReadTrigger();
        SimulatePellets(Time.deltaTime);
    }

    void ReadTrigger()
    {
        WeaponHand hand = grab != null ? grab.Holder : null;
        if (hand != ignoredFor) RebuildIgnored(hand);
        if (hand == null) { triggerDown = false; return; }

        InputDevice device = InputDevices.GetDeviceAtXRNode(hand.IsLeft ? XRNode.LeftHand : XRNode.RightHand);
        if (!device.isValid) return;

        float value;
        if (!device.TryGetFeatureValue(CommonUsages.trigger, out value))
        {
            if (!device.TryGetFeatureValue(CommonUsages.triggerButton, out bool button)) return;
            value = button ? 1f : 0f;
        }

        // Seuil de relâchement plus bas que le seuil d'appui, pour éviter les tirs doubles à la limite.
        bool wasDown = triggerDown;
        triggerDown = triggerDown ? value > 0.4f : value >= 0.6f;

        if (triggerDown && (automatic || !wasDown)) Fire();
    }

    /// <summary>Tire une bille, si la cadence le permet. Renvoie vrai si la bille est partie.</summary>
    public bool Fire()
    {
        if (!Application.isPlaying || Time.time < nextShot) return false;
        nextShot = Time.time + 1f / Mathf.Max(0.5f, fireRate);

        Transform m = muzzle != null ? muzzle : transform;

        // Dispersion : direction tirée au hasard dans un petit cône autour de l'axe du canon.
        Vector3 dir = m.forward;
        if (spread > 0f)
        {
            Vector2 off = Random.insideUnitCircle * Mathf.Tan(spread * Mathf.Deg2Rad);
            dir = (m.forward + m.right * off.x + m.up * off.y).normalized;
        }

        Pellet p = Spawn();
        p.flying = true;
        p.pos = m.position;
        p.vel = dir * muzzleVelocity;
        p.bornAt = Time.time;
        p.ricochets = 0;
        p.skip = null;
        p.tf.SetPositionAndRotation(p.pos, Quaternion.identity);
        if (p.trail != null) p.trail.Clear();

        PlayShot();
        if (shotHaptic > 0f && grab != null && grab.Holder != null) grab.Holder.Vibrate(shotHaptic, 0.03f);
        onFire?.Invoke();
        return true;
    }

    [ContextMenu("Tirer (test)")]
    void TestFire()
    {
        nextShot = 0f;
        Fire();
    }

    void PlayShot()
    {
        if (shotSounds == null || shotSounds.Length == 0) return;
        AudioClip clip = shotSounds[Random.Range(0, shotSounds.Length)];
        if (clip == null) return;

        if (shotSource == null)
        {
            shotSource = gameObject.AddComponent<AudioSource>();
            shotSource.playOnAwake = false;
            shotSource.spatialBlend = 1f;
            shotSource.minDistance = 0.5f;
            shotSource.maxDistance = 30f;
        }
        shotSource.pitch = 1f + Random.Range(-pitchVariation, pitchVariation);
        shotSource.PlayOneShot(clip, shotVolume);
    }

    // ------------------------------------------------------------------ Billes : création et recyclage

    Pellet Spawn()
    {
        // Trop de billes : la plus ancienne disparaît.
        while (pellets.Count >= maxPellets) Recycle(0);

        Pellet p = pool.Count > 0 ? pool.Pop() : Create();
        p.go.SetActive(true);
        pellets.Add(p);
        return p;
    }

    Pellet Create()
    {
        GameObject go;
        if (pelletPrefab != null)
        {
            go = Instantiate(pelletPrefab);
        }
        else
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.transform.localScale = Vector3.one * pelletDiameter;
            Renderer rend = go.GetComponent<Renderer>();
            if (pelletMaterial != null) rend.sharedMaterial = pelletMaterial;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
        go.name = "Bille";

        Collider col = go.GetComponentInChildren<Collider>();
        if (col == null)
        {
            SphereCollider sphere = go.AddComponent<SphereCollider>();
            Vector3 ls = go.transform.lossyScale;
            float scale = Mathf.Max(1e-5f, Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
            sphere.radius = pelletDiameter * 0.5f / scale;
            col = sphere;
        }
        col.enabled = false; // pas de collision physique pendant le vol : c'est le script qui s'en charge
        pelletColliders.Add(col);

        Rigidbody rb = go.GetComponent<Rigidbody>();
        if (rb == null) rb = go.AddComponent<Rigidbody>();
        rb.mass = Mathf.Max(0.002f, pelletMass);
        rb.isKinematic = true;

        var p = new Pellet { go = go, tf = go.transform, rb = rb, col = col };

        if (tracer)
        {
            Material mat = FxMaterial();
            if (mat != null)
            {
                TrailRenderer trail = go.AddComponent<TrailRenderer>();
                trail.time = 0.05f;
                trail.startWidth = pelletDiameter * 1.2f;
                trail.endWidth = 0f;
                trail.minVertexDistance = 0.02f;
                trail.sharedMaterial = mat;
                trail.startColor = tracerColor;
                trail.endColor = new Color(tracerColor.r, tracerColor.g, tracerColor.b, 0f);
                trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                trail.receiveShadows = false;
                p.trail = trail;
            }
        }
        return p;
    }

    void Recycle(int index)
    {
        Pellet p = pellets[index];
        pellets.RemoveAt(index);
        if (p.go == null) return;

        p.flying = false;
        p.col.enabled = false;
        // Le mode de détection est remis à "Discrete" avant de repasser en kinematic (ContinuousDynamic n'y est pas permis).
        p.rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
        p.rb.isKinematic = true;
        if (p.trail != null) p.trail.emitting = true;
        p.go.SetActive(false);
        pool.Push(p);
    }

    /// <summary>La bille, ralentie, devient un vrai petit objet physique qui rebondit et roule au sol.</summary>
    void Handoff(Pellet p)
    {
        p.flying = false;
        p.restUntil = Time.time + restLifetime;
        p.tf.position = p.pos;
        if (p.trail != null) p.trail.emitting = false;

        p.col.enabled = true;
        p.rb.isKinematic = false;
        p.rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        p.rb.position = p.pos;
        // Vitesse plafonnée : une bille au sol ne doit plus rien casser en roulant contre un objet fragile.
        SetVelocity(p.rb, Vector3.ClampMagnitude(p.vel, RestSpeed));
    }

    // ------------------------------------------------------------------ Vol des billes

    void SimulatePellets(float dt)
    {
        if (dt <= 0f) return;
        Vector3 gravity = Physics.gravity * gravityScale;
        float radius = pelletDiameter * 0.5f;

        for (int i = pellets.Count - 1; i >= 0; i--)
        {
            Pellet p = pellets[i];
            if (p.go == null) { pellets.RemoveAt(i); continue; }

            if (!p.flying)
            {
                if (Time.time >= p.restUntil) Recycle(i);
                continue;
            }
            if (Time.time - p.bornAt > maxFlightTime) { Recycle(i); continue; }

            p.vel += gravity * dt;
            p.vel *= 1f / (1f + airDrag * dt);
            if (p.skipFrame != Time.frameCount) p.skip = null; // l'objet cassé a disparu à la fin de l'image précédente

            // Le trajet de cette image est parcouru tronçon par tronçon : jusqu'au premier obstacle,
            // puis, après le rebond, sur le temps qui reste. Rien de ce qui se trouve sur le trajet n'est sauté.
            float remaining = dt;
            for (int segment = 0; segment < 6 && p.flying && remaining > 1e-6f; segment++)
            {
                Vector3 step = p.vel * remaining;
                float dist = step.magnitude;
                if (dist < 1e-6f) break;
                Vector3 dir = step / dist;

                if (!NearestHit(p, dir, dist, radius, out RaycastHit hit))
                {
                    p.pos += step;
                    break;
                }

                p.pos += dir * hit.distance;
                remaining *= 1f - Mathf.Clamp01(hit.distance / dist);
                Impact(p, hit, dir);
            }

            if (p.flying) p.tf.position = p.pos;
        }
    }

    /// <summary>
    /// Premier obstacle sur le tronçon, en tenant compte de l'épaisseur de la bille.
    /// Un collider dans lequel la bille se trouve déjà (tir à bout portant) compte comme un impact immédiat.
    /// </summary>
    bool NearestHit(Pellet p, Vector3 dir, float dist, float radius, out RaycastHit nearest)
    {
        int count = Physics.SphereCastNonAlloc(p.pos, radius, dir, hits, dist, hitLayers, QueryTriggerInteraction.Ignore);

        nearest = default;
        float best = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            Collider c = hits[i].collider;
            if (c == null || ignored.Contains(c) || pelletColliders.Contains(c)) continue;
            if (p.skip != null && c.attachedRigidbody == p.skip) continue;
            if (hits[i].distance >= best) continue;

            best = hits[i].distance;
            nearest = hits[i];
        }
        if (float.IsPositiveInfinity(best)) return false;

        // Bille déjà dans le collider au départ : Unity ne donne ni point ni normale exploitables.
        if (nearest.distance <= 0f && nearest.point == Vector3.zero)
        {
            nearest.point = p.pos;
            nearest.normal = -dir;
        }
        return true;
    }

    // ------------------------------------------------------------------ Impact

    void Impact(Pellet p, RaycastHit hit, Vector3 dir)
    {
        Vector3 v = p.vel;
        float speed = v.magnitude;
        float shock = speed * impactFactor; // force de choc équivalente, comparable au Break Velocity des objets
        Vector3 n = hit.normal;
        Collider c = hit.collider;
        Rigidbody body = c.attachedRigidbody;

        bool broke = false;
        float absorb = 1f;

        if (body != null)
        {
            RuntimeFracture fracture = body.GetComponent<RuntimeFracture>();
            if (fracture == null) fracture = c.GetComponentInParent<RuntimeFracture>();
            if (fracture != null) broke = fracture.Hit(hit.point, dir * shock, solidity);

            if (!broke)
            {
                RuntimeExplosion explosive = body.GetComponent<RuntimeExplosion>();
                if (explosive != null && explosive.explodeOnImpact && !explosive.HasExploded && shock >= explosive.impactVelocity)
                    explosive.Explode();

                RuntimeCrush crush = body.GetComponent<RuntimeCrush>();
                if (crush != null)
                {
                    if (dentDepth > 0f && muzzleVelocity > 0f)
                        crush.Crush(hit.point, dir, dentDepth * Mathf.Clamp01(speed / muzzleVelocity), dentRadius);
                    absorb = 0.3f; // un objet qui se déforme amortit la bille
                }
            }
        }

        // Vitesse après l'impact.
        Vector3 after;
        if (broke)
        {
            // L'objet a éclaté : la bille le traverse, ralentie, sans changer de direction.
            after = v * breakThrough;
            p.skip = body;
            p.skipFrame = Time.frameCount;
        }
        else
        {
            float vn = Vector3.Dot(v, n);
            Vector3 tangent = v - n * vn;
            after = (tangent * (1f - friction) - n * (vn * bounciness)) * absorb;
            p.ricochets++;
            p.pos += n * 0.0005f; // décollée de la surface, pour ne pas la retoucher au tronçon suivant
        }

        // Poussée : la quantité de mouvement perdue par la bille est transmise à l'objet.
        if (body != null && !body.isKinematic && !broke)
            body.AddForceAtPosition((v - after) * (pelletMass * knockback), hit.point, ForceMode.Impulse);

        p.vel = after;

        if (speed >= SparkSpeed)
        {
            if (impactSparks) EmitSparks(hit.point, n, speed);
            PlayImpact(hit.point, body, speed);
            onImpact?.Invoke(hit.point);
        }

        if (debugLog)
            Debug.Log($"[PelletGun] bille sur '{c.name}' à {speed:F0} m/s (choc équivalent {shock:F1} m/s, solidité {solidity}) : "
                    + (broke ? "objet cassé, la bille le traverse" : $"ricochet à {after.magnitude:F0} m/s"), this);

        if (!broke && (p.ricochets > maxRicochets || after.magnitude < HandoffSpeed)) Handoff(p);
    }

    void PlayImpact(Vector3 point, Rigidbody body, float speed)
    {
        if (Time.time - lastImpactSound < 0.04f) return;

        // De préférence, le bruit de choc de l'objet touché (verre, bois, métal...).
        AudioClip[] clips = impactSounds;
        if (body != null)
        {
            RuntimeImpactSound material = body.GetComponent<RuntimeImpactSound>();
            if (material != null && material.impactSounds != null && material.impactSounds.Length > 0)
                clips = material.impactSounds;
        }
        if (clips == null || clips.Length == 0) return;

        AudioClip clip = clips[Random.Range(0, clips.Length)];
        if (clip == null) return;

        lastImpactSound = Time.time;
        float strength = muzzleVelocity > 0f ? Mathf.Clamp01(speed / muzzleVelocity) : 1f;
        AudioSource.PlayClipAtPoint(clip, point, impactVolume * Mathf.Lerp(0.4f, 1f, strength));
    }

    // ------------------------------------------------------------------ Effets

    Material FxMaterial()
    {
        if (fxMaterial == null) fxMaterial = RuntimeExplosion.DefaultParticleMaterial();
        return fxMaterial;
    }

    /// <summary>Un seul système de particules sert à tous les impacts : quelques étincelles émises au point touché.</summary>
    void EmitSparks(Vector3 point, Vector3 normal, float speed)
    {
        if (sparkSystem == null)
        {
            Material mat = FxMaterial();
            if (mat == null) { impactSparks = false; return; }

            var go = new GameObject("FX_ImpactsBilles");
            sparkSystem = go.AddComponent<ParticleSystem>();
            sparkSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = sparkSystem.main;
            main.playOnAwake = false;
            main.loop = true;
            main.startLifetime = 0.3f;
            main.startSpeed = 0f;
            main.startSize = 0.01f;
            main.gravityModifier = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 300;

            var emission = sparkSystem.emission;
            emission.rateOverTime = 0f; // rien en continu : les particules sont émises une à une, à chaque impact

            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.97f, 0.75f), 0f), new GradientColorKey(new Color(1f, 0.6f, 0.2f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.5f), new GradientAlphaKey(0f, 1f) });
            var colorOver = sparkSystem.colorOverLifetime;
            colorOver.enabled = true;
            colorOver.color = new ParticleSystem.MinMaxGradient(gradient);

            var rend = go.GetComponent<ParticleSystemRenderer>();
            rend.sharedMaterial = mat;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rend.receiveShadows = false;

            sparkSystem.Play();
        }

        float strength = muzzleVelocity > 0f ? Mathf.Clamp01(speed / muzzleVelocity) : 1f;
        int count = Mathf.RoundToInt(Mathf.Lerp(2f, 6f, strength));
        var emit = new ParticleSystem.EmitParams();
        for (int i = 0; i < count; i++)
        {
            // Projetées du côté d'où vient la bille, en éventail autour de la normale de la surface.
            Vector3 dir = (normal + Random.insideUnitSphere * 0.8f).normalized;
            emit.position = point + normal * 0.003f;
            emit.velocity = dir * Random.Range(0.8f, 3f) * Mathf.Lerp(0.5f, 1f, strength);
            emit.startSize = Random.Range(0.004f, 0.012f);
            emit.startLifetime = Random.Range(0.15f, 0.4f);
            sparkSystem.Emit(emit, 1);
        }
    }

    static void SetVelocity(Rigidbody body, Vector3 v)
    {
#if UNITY_6000_0_OR_NEWER
        body.linearVelocity = v;
#else
        body.velocity = v;
#endif
    }

    // ------------------------------------------------------------------ Aide au réglage dans l'éditeur

    void OnDrawGizmosSelected()
    {
        Transform m = muzzle != null ? muzzle : transform;
        Gizmos.color = new Color(1f, 0.4f, 0.1f);
        Gizmos.DrawWireSphere(m.position, 0.008f);
        Gizmos.DrawLine(m.position, m.position + m.forward * 0.5f); // direction du tir
    }
}