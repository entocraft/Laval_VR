using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;

/// <summary>
/// Explosion avec dégâts de zone : casse les objets cassables, écrase les objets déformables et souffle
/// tout ce qui a un Rigidbody, de plus en plus faiblement en s'éloignant du centre.
///
/// Mise en place :
///  - À placer sur l'objet qui explose (bidon, bombonne, grenade...), sur la racine qui porte le Rigidbody.
///  - Le plus simple : donner aussi à l'objet le script de casse. Avec "Explode When Broken" coché,
///    il explose dès qu'il est cassé, par un coup d'arme, une chute ou une autre explosion.
///  - Sons : les clips Assets/Sounds/Explosion/SO_Explosion_1, _2... sont chargés automatiquement.
///  - Nécessite RuntimeFracture.cs, RuntimeCrush.cs, IPointVelocity.cs et WeaponHand.cs dans le projet.
/// </summary>
[DisallowMultipleComponent]
public class RuntimeExplosion : MonoBehaviour
{
    [Header("Déclenchement")]
    [Tooltip("Explose quand le script de casse du même objet le casse (coup d'arme, chute, autre explosion).")]
    public bool explodeWhenBroken = true;
    [Tooltip("Explose au premier choc assez rapide, sans avoir besoin du script de casse (grenade lancée, par exemple).")]
    public bool explodeOnImpact;
    [Tooltip("Vitesse de choc (m/s) qui déclenche l'explosion quand Explode On Impact est coché.")]
    public float impactVelocity = 4f;
    [Tooltip("Mèche : l'objet explose tout seul ce nombre de secondes après son apparition. 0 = pas de mèche.")]
    [Min(0f)] public float fuseOnStart = 0f;
    [Tooltip("Durée (secondes) après l'apparition pendant laquelle un choc ne déclenche rien, le temps que l'objet se pose.")]
    [Min(0f)] public float armDelay = 0.5f;
    [Tooltip("Les chocs avec ces layers ne déclenchent pas l'explosion (corps du joueur, mains...).")]
    public LayerMask ignoreLayers;

    [Header("Dégâts")]
    [Tooltip("Rayon (mètres) au-delà duquel l'explosion n'a plus aucun effet.")]
    public float radius = 3f;
    [Tooltip("Part du rayon, depuis le centre, où les dégâts sont à leur maximum. 0,25 = le premier quart du rayon.")]
    [Range(0f, 1f)] public float fullDamageZone = 0.25f;
    [Tooltip("Puissance au centre, exprimée comme une vitesse de choc (m/s) : un objet casse si cette valeur, "
           + "réduite par la distance, dépasse son Break Velocity.")]
    public float power = 12f;
    [Tooltip("Atténuation avec la distance. 1 = régulière, 2 = les dégâts chutent vite en s'éloignant du centre.")]
    [Range(0.5f, 3f)] public float falloff = 1.5f;
    [Tooltip("Les objets plus solides que cette valeur ne cassent pas, mais sont quand même soufflés.")]
    public float maxSolidity = 100f;
    [Tooltip("Écrasement des objets déformables au centre, en proportion de leur taille. 0 = aucun.")]
    [Range(0f, 1f)] public float crushAmount = 0.35f;
    [Tooltip("Layers des objets touchés par l'explosion.")]
    public LayerMask affectedLayers = ~0;

    [Header("Souffle")]
    [Tooltip("Vitesse (m/s) donnée aux objets au centre de l'explosion, quel que soit leur poids.")]
    public float pushSpeed = 7f;
    [Tooltip("Soulève les objets en plus de les repousser. 0 = poussée purement horizontale depuis le centre.")]
    [Range(0f, 2f)] public float upwardLift = 0.3f;

    [Header("Abri")]
    [Tooltip("Un mur ou un meuble fixe entre l'explosion et un objet protège cet objet.")]
    public bool blockedByWalls = true;
    [Tooltip("Part des dégâts et du souffle qui atteint quand même un objet abrité. 0 = totalement protégé.")]
    [Range(0f, 1f)] public float coverFactor = 0.25f;
    [Tooltip("Layers du décor qui peuvent servir d'abri.")]
    public LayerMask wallLayers = ~0;

    [Header("Réaction en chaîne")]
    [Tooltip("Fait exploser à leur tour les autres objets explosifs situés dans le rayon.")]
    public bool chainReaction = true;
    [Tooltip("Délai (secondes, mini et maxi) avant qu'un explosif voisin n'explose, pour un effet en cascade.")]
    public Vector2 chainDelay = new Vector2(0.08f, 0.25f);

    [Header("Effets")]
    [Tooltip("Boule de feu.")]
    public bool fireball = true;
    [Tooltip("Colonne de fumée qui reste quelques secondes.")]
    public bool smoke = true;
    [Tooltip("Étincelles projetées qui retombent.")]
    public bool sparks = true;
    [Tooltip("Éclair de lumière très bref. Coûteux sur casque autonome : à décocher sur Quest si besoin.")]
    public bool flash = true;
    [Tooltip("Taille des effets par rapport au rayon de l'explosion.")]
    [Min(0.1f)] public float effectScale = 1f;
    [Tooltip("Matériau de particules personnalisé (optionnel). Si vide, un matériau simple est créé automatiquement.")]
    public Material effectMaterial;
    [Tooltip("Tes propres prefabs d'effets, créés au centre puis supprimés au bout de 8 secondes (optionnel).")]
    public GameObject[] customEffects;

    [Header("Son")]
    [Tooltip("Nom du dossier dans Assets/Sounds/. Les clips SO_[nom]_n de ce dossier sont chargés automatiquement.")]
    [Delayed] public string soundMaterial = "Explosion";
    [SerializeField, HideInInspector] string appliedSoundMaterial = "";
    [Tooltip("Sons d'explosion. S'il y en a plusieurs, un clip est tiré au hasard à chaque fois.")]
    public AudioClip[] explosionSounds;
    [Range(0f, 1f)] public float soundVolume = 1f;
    [Tooltip("Variation aléatoire de hauteur (±) pour que deux explosions ne sonnent jamais pareil.")]
    [Range(0f, 0.5f)] public float pitchVariation = 0.08f;

    [Header("Vibrations")]
    [Tooltip("Vibration des manettes pour un joueur au centre de l'explosion. Elle diminue avec la distance. 0 = aucune.")]
    [Range(0f, 1f)] public float hapticStrength = 1f;

    [Header("Après l'explosion")]
    [Tooltip("Supprime l'objet après l'explosion. S'il porte le script de casse, il vole en éclats à la place.")]
    public bool destroySelf = true;

    [Header("Événement")]
    [Tooltip("Appelé à l'explosion avec la position du centre (monde).")]
    public UnityEvent<Vector3> onExplode;

    [Header("Diagnostic")]
    [Tooltip("Affiche dans la console le bilan de chaque explosion.")]
    public bool debugLog;

    /// <summary>Vrai dès que l'objet a explosé.</summary>
    public bool HasExploded => exploded;

    // ------------------------------------------------------------------ État interne

    struct Target
    {
        public float distance;   // du centre de l'explosion au point le plus proche de l'objet
        public Vector3 point;    // ce point le plus proche
        public Bounds bounds;    // volume occupé par l'objet
    }

    bool exploded;
    float armedAt;
    Coroutine fuse;
    Rigidbody rb;
    RuntimeFracture ownFracture;
    IPointVelocity tracker;

    static readonly Collider[] overlap = new Collider[256];
    static Material particleMaterial;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        particleMaterial = null;
    }

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
        explosionSounds = found;
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    // ------------------------------------------------------------------ Déclenchement

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        ownFracture = GetComponent<RuntimeFracture>();
        tracker = GetComponent<IPointVelocity>();

        if (explodeWhenBroken && ownFracture != null) ownFracture.onBreak.AddListener(OnBroken);
    }

    void OnEnable()
    {
        armedAt = Time.time + armDelay;
    }

    void Start()
    {
        if (fuseOnStart > 0f) StartFuse(fuseOnStart);
    }

    void OnDestroy()
    {
        if (ownFracture != null) ownFracture.onBreak.RemoveListener(OnBroken);
    }

    void OnBroken(Vector3 impactPoint)
    {
        Explode();
    }

    void OnCollisionEnter(Collision col)
    {
        if (!explodeOnImpact || exploded || Time.time < armedAt || col.contactCount == 0) return;
        if ((ignoreLayers.value & (1 << col.collider.gameObject.layer)) != 0) return;

        float speed = col.relativeVelocity.magnitude;
        // Objet tenu en main : le moteur physique ne connaît pas sa vitesse, on la mesure.
        if (tracker != null) speed = Mathf.Max(speed, tracker.PointVelocity(col.GetContact(0).point).magnitude);

        if (speed >= impactVelocity) Explode();
    }

    /// <summary>Allume la mèche : l'objet explose après ce délai (secondes).</summary>
    public void StartFuse(float delay)
    {
        if (exploded || fuse != null || !isActiveAndEnabled) return;
        fuse = StartCoroutine(FuseRoutine(delay));
    }

    IEnumerator FuseRoutine(float delay)
    {
        yield return new WaitForSeconds(Mathf.Max(0f, delay));
        fuse = null;
        Explode();
    }

    /// <summary>Fait exploser l'objet maintenant, depuis son centre.</summary>
    [ContextMenu("Exploser maintenant (test)")]
    public void Explode()
    {
        ExplodeAt(rb != null ? rb.worldCenterOfMass : transform.position);
    }

    // ------------------------------------------------------------------ Explosion

    /// <summary>Fait exploser l'objet maintenant, avec le centre de l'explosion à cette position (monde).</summary>
    public void ExplodeAt(Vector3 center)
    {
        if (exploded || !Application.isPlaying) return;
        exploded = true;

        // 1. L'objet lui-même vole en éclats (s'il porte le script de casse et n'est pas déjà cassé).
        bool selfBroken = ownFracture != null && ownFracture.IsBroken;
        if (ownFracture != null && !selfBroken)
        {
            ownFracture.scatterSpeed = Mathf.Max(ownFracture.scatterSpeed, pushSpeed);
            float speed = Mathf.Max(power, ownFracture.breakVelocity + 1f);
            selfBroken = ownFracture.Hit(center, Vector3.up * speed);
        }

        // 2. Recensement de tout ce qui se trouve dans le rayon, avant de toucher à quoi que ce soit :
        //    casser un objet peut déclencher une autre explosion, qui réutilise le même tampon.
        var targets = new Dictionary<Rigidbody, Target>();
        int count = Physics.OverlapSphereNonAlloc(center, radius, overlap, affectedLayers, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < count; i++)
        {
            Collider c = overlap[i];
            if (c == null || c.transform.IsChildOf(transform)) continue;

            Rigidbody body = c.attachedRigidbody;
            if (body == null) continue; // décor fixe : rien à casser ni à pousser

            Vector3 point = c.bounds.ClosestPoint(center);
            float distance = Vector3.Distance(point, center);

            if (targets.TryGetValue(body, out Target t))
            {
                t.bounds.Encapsulate(c.bounds);
                if (distance < t.distance) { t.distance = distance; t.point = point; }
                targets[body] = t;
            }
            else
            {
                targets[body] = new Target { distance = distance, point = point, bounds = c.bounds };
            }
        }

        // 3. Dégâts et souffle, objet par objet.
        int broken = 0, crushed = 0, pushed = 0, chained = 0;

        foreach (KeyValuePair<Rigidbody, Target> pair in targets)
        {
            Rigidbody body = pair.Key;
            Target t = pair.Value;
            if (body == null) continue;

            float strength = Strength(t.distance);
            if (blockedByWalls && Sheltered(center, t.bounds.center, body)) strength *= coverFactor;
            if (strength <= 0f) continue;

            Vector3 dir = t.bounds.center - center;
            dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.up;

            RuntimeExplosion explosive = body.GetComponent<RuntimeExplosion>();
            if (explosive != null)
            {
                // Autre explosif : il n'est pas cassé directement, sa mèche est allumée pour une explosion en cascade.
                if (chainReaction && !explosive.exploded)
                {
                    explosive.StartFuse(Random.Range(Mathf.Min(chainDelay.x, chainDelay.y), Mathf.Max(chainDelay.x, chainDelay.y)));
                    chained++;
                }
            }
            else
            {
                RuntimeFracture fracture = body.GetComponent<RuntimeFracture>();
                if (fracture != null && fracture.Hit(t.point, dir * (power * strength), maxSolidity))
                {
                    broken++;
                    continue; // ses fragments partent déjà dans le sens du souffle
                }

                RuntimeCrush crush = body.GetComponent<RuntimeCrush>();
                if (crush != null && crushAmount > 0f)
                {
                    crush.Crush(t.point, dir, t.bounds.size.magnitude * crushAmount * strength);
                    crushed++;
                }
            }

            if (!body.isKinematic && pushSpeed > 0f)
            {
                body.WakeUp();
                body.AddForce(PushVelocity(center, t.bounds.center, strength), ForceMode.VelocityChange);
                pushed++;
            }
        }

        // 4. Effets, son, vibrations.
        SpawnEffects(center);
        PlaySound(center);
        ShakeHands(center);
        onExplode?.Invoke(center);

        if (debugLog)
            Debug.Log($"[RuntimeExplosion] '{name}' : {targets.Count} objets dans le rayon — {broken} cassés, "
                    + $"{crushed} écrasés, {pushed} soufflés, {chained} explosifs amorcés", this);

        // 5. L'objet disparaît (s'il a volé en éclats, le script de casse s'en charge déjà).
        if (destroySelf && !selfBroken) Destroy(gameObject);
    }

    /// <summary>Force de l'explosion à cette distance du centre : 1 dans la zone de dégâts maximum, 0 au bord du rayon.</summary>
    float Strength(float distance)
    {
        if (radius <= 0f) return 0f;
        float inner = radius * Mathf.Clamp01(fullDamageZone);
        if (distance <= inner) return 1f;
        if (distance >= radius) return 0f;
        return Mathf.Pow(1f - (distance - inner) / (radius - inner), falloff);
    }

    /// <summary>Vrai si un élément fixe du décor se trouve entre le centre de l'explosion et l'objet.</summary>
    bool Sheltered(Vector3 center, Vector3 targetCenter, Rigidbody body)
    {
        // Départ légèrement surélevé : un explosif posé au sol ne doit pas être "abrité" par le sol lui-même.
        Vector3 from = center + Vector3.up * 0.1f;
        Vector3 to = targetCenter;
        float length = Vector3.Distance(from, to);
        if (length < 0.05f) return false;

        if (!Physics.Linecast(from, to, out RaycastHit hit, wallLayers, QueryTriggerInteraction.Ignore)) return false;

        // Seul le décor fixe abrite : un autre objet mobile sur le trajet ne protège pas.
        return hit.collider.attachedRigidbody == null && hit.distance < length - 0.05f;
    }

    /// <summary>Vitesse donnée à un objet : il s'éloigne du centre, avec un peu de portance.</summary>
    Vector3 PushVelocity(Vector3 center, Vector3 targetCenter, float strength)
    {
        Vector3 dir = targetCenter - (center + Vector3.down * upwardLift);
        dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.up;
        return dir * (pushSpeed * strength);
    }

    // ------------------------------------------------------------------ Son et vibrations

    void PlaySound(Vector3 center)
    {
        if (explosionSounds == null || explosionSounds.Length == 0) return;
        AudioClip clip = explosionSounds[Random.Range(0, explosionSounds.Length)];
        if (clip == null) return;

        var go = new GameObject("ExplosionAudio");
        go.transform.position = center;

        var src = go.AddComponent<AudioSource>();
        src.clip = clip;
        src.volume = Mathf.Clamp01(soundVolume);
        src.pitch = 1f + Random.Range(-pitchVariation, pitchVariation);
        src.spatialBlend = 1f;   // son spatialisé, audible de loin
        src.minDistance = 2f;
        src.maxDistance = 60f;
        src.Play();

        Destroy(go, clip.length / Mathf.Max(0.1f, src.pitch) + 0.1f);
    }

    void ShakeHands(Vector3 center)
    {
        if (hapticStrength <= 0f) return;

#if UNITY_2023_1_OR_NEWER
        WeaponHand[] hands = FindObjectsByType<WeaponHand>(FindObjectsSortMode.None);
#else
        WeaponHand[] hands = FindObjectsOfType<WeaponHand>();
#endif
        foreach (WeaponHand hand in hands)
        {
            // Ressentie jusqu'à deux fois le rayon de dégâts.
            float t = 1f - Vector3.Distance(hand.HoldPosition, center) / (radius * 2f);
            if (t > 0f) hand.Vibrate(hapticStrength * t, Mathf.Lerp(0.1f, 0.4f, t));
        }
    }

    // ------------------------------------------------------------------ Effets visuels

    void SpawnEffects(Vector3 center)
    {
        if (customEffects != null)
            foreach (GameObject prefab in customEffects)
                if (prefab != null) Destroy(Instantiate(prefab, center, Quaternion.identity), 8f);

        float s = Mathf.Max(0.05f, radius * 0.3f * effectScale);

        if (flash)
        {
            var lightObject = new GameObject("FX_Eclair");
            lightObject.transform.position = center + Vector3.up * 0.2f;
            Light flashLight = lightObject.AddComponent<Light>();
            flashLight.type = LightType.Point;
            flashLight.color = new Color(1f, 0.75f, 0.4f);
            flashLight.intensity = 6f;
            flashLight.range = radius * 2.5f;
            flashLight.shadows = LightShadows.None;
            Destroy(lightObject, 0.1f);
        }

        if (!fireball && !smoke && !sparks) return;
        Material mat = effectMaterial != null ? effectMaterial : DefaultParticleMaterial();
        if (mat == null) return;

        if (fireball)
        {
            Gradient fire = Fade(new Color(1f, 0.95f, 0.7f), new Color(1f, 0.5f, 0.1f), new Color(0.15f, 0.12f, 0.1f), 1f);
            EmitBurst("FX_BouleDeFeu", center, mat, 30,
                      life: new Vector2(0.3f, 0.6f), speed: new Vector2(2f, 6f) * s, size: new Vector2(0.8f, 1.6f) * s,
                      radius: 0.2f * s, gravity: -0.05f, damp: 0.2f, endSize: 1.8f, colors: fire);
        }

        if (smoke)
        {
            Gradient grey = Fade(new Color(0.25f, 0.25f, 0.25f), new Color(0.2f, 0.2f, 0.2f), new Color(0.12f, 0.12f, 0.12f), 0.6f);
            EmitBurst("FX_Fumee", center, mat, 16,
                      life: new Vector2(2f, 4f), speed: new Vector2(0.4f, 1.2f) * s, size: new Vector2(1f, 2f) * s,
                      radius: 0.4f * s, gravity: -0.04f, damp: 0.05f, endSize: 2.5f, colors: grey);
        }

        if (sparks)
        {
            Gradient hot = Fade(new Color(1f, 0.95f, 0.6f), new Color(1f, 0.6f, 0.15f), new Color(0.8f, 0.2f, 0.05f), 1f);
            EmitBurst("FX_Etincelles", center, mat, 40,
                      life: new Vector2(0.5f, 1.2f), speed: new Vector2(5f, 12f) * Mathf.Sqrt(s), size: new Vector2(0.03f, 0.08f) * effectScale,
                      radius: 0.1f * s, gravity: 1f, damp: 0.02f, endSize: 1f, colors: hot);
        }
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
    public static Material DefaultParticleMaterial()
    {
        if (particleMaterial != null) return particleMaterial;

        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
        {
            Debug.LogWarning("[RuntimeExplosion] Shader 'Sprites/Default' introuvable : assigne un matériau dans Effect Material pour voir les effets.");
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

        particleMaterial = new Material(shader) { name = "ExplosionParticles", mainTexture = tex };
        return particleMaterial;
    }

    // ------------------------------------------------------------------ Aide au réglage dans l'éditeur

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.3f, 0.1f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, radius);
        Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, radius * Mathf.Clamp01(fullDamageZone));
    }
}