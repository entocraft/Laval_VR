using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR;

/// <summary>
/// Klaxon de train à air comprimé, façon TGV : deux trompes (ton grave 370 Hz, ton aigu 660 Hz).
/// C'est la seconde moitié de l'arme : il se branche sur un compresseur (AirCompressor.cs), posé ailleurs
/// dans la pièce, et n'a aucune réserve d'air à lui. Sans compresseur, ou réservoir vide, il ne fait rien.
///
/// Tout dépend de la pression chargée dans le compresseur :
///  - le son : à pleine pression les trompes sont puissantes et brillantes ; quand le réservoir se vide, elles
///    faiblissent, deviennent plus sourdes et baissent légèrement, puis bégaient et laissent place au souffle de l'air ;
///  - les dégâts : le souffle des trompes pousse, écrase et casse ce qui se trouve devant elles, avec une force
///    proportionnelle à la pression. À pleine charge il fait éclater des objets qu'il ne ferait que bousculer
///    à mi-charge.
/// Chaque coup de klaxon vide le réservoir : il faut laisser le compresseur le regonfler pour retrouver la puissance.
///
/// Le son est fabriqué en direct, échantillon par échantillon : il n'y a aucun fichier son à fournir.
///
/// Mise en place, sur la racine du klaxon (l'objet qui porte le Rigidbody et un collider) :
///  - WeaponGrab : pour le saisir. La gâchette de l'index joue le bi-ton (aigu puis grave), plus ou moins fort
///    selon l'appui. Le bouton principal (A / X) joue le ton grave seul, le bouton secondaire (B / Y) le ton aigu seul.
///  - TrainHorn  : ce script (il ajoute et règle lui-même l'Audio Source dont il a besoin).
///  - Glisser le compresseur de la scène dans Compressor (sinon le plus proche est pris au démarrage).
///  - Créer un objet vide à la sortie des trompes, axe bleu (Z) vers l'avant, et le glisser dans Horn Mouth.
///  - Ne pas mettre d'autre Audio Source sur le même objet : le son fabriqué passerait aussi par elle.
///  - Nécessite AirCompressor, WeaponGrab, WeaponHand, RuntimeFracture et RuntimeCrush dans le projet.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(AudioSource))]
public class TrainHorn : MonoBehaviour
{
    public enum HornMode
    {
        [InspectorName("Deux tons ensemble")] DeuxTons,
        [InspectorName("Ton grave seul")] Grave,
        [InspectorName("Ton aigu seul")] Aigu,
        [InspectorName("Bi-ton alterné")] Alterne
    }

    public enum HornButton
    {
        [InspectorName("Aucun")] Aucun,
        [InspectorName("Bouton principal (A / X)")] Principal,
        [InspectorName("Bouton secondaire (B / Y)")] Secondaire,
        [InspectorName("Clic du joystick")] Joystick
    }

    [Header("Alimentation en air")]
    [Tooltip("Compresseur sur lequel le klaxon est branché. Si vide, le plus proche dans la scène est pris au démarrage.")]
    public AirCompressor compressor;
    [Tooltip("Pression (bars) en dessous de laquelle les trompes ne chantent plus : il ne reste que le souffle de l'air.")]
    public float minHornPressure = 3f;
    [Tooltip("Durée approximative (secondes) d'un coup de klaxon continu, gâchette à fond, pour faire tomber un réservoir "
           + "plein jusqu'à cette pression minimale (compresseur à l'arrêt).")]
    public float blastTime = 5f;

    [Header("Tuyau")]
    [Tooltip("Dessine un tuyau entre le klaxon et le compresseur.")]
    public bool showHose = true;
    [Tooltip("Point du klaxon où le tuyau se branche (optionnel). Si vide, c'est l'origine de l'objet.")]
    public Transform hoseInlet;
    [Tooltip("Longueur du tuyau (mètres). Au-delà, il se débranche et le klaxon n'a plus d'air. 0 = longueur illimitée.")]
    [Min(0f)] public float hoseLength = 0f;
    [Tooltip("Épaisseur du tuyau (mètres).")]
    public float hoseWidth = 0.014f;
    [Tooltip("Couleur du tuyau.")]
    public Color hoseColor = new Color(0.08f, 0.08f, 0.08f);
    [Tooltip("Matériau du tuyau (optionnel). Si vide, un matériau uni est créé.")]
    public Material hoseMaterial;

    [Header("Trompes")]
    [Tooltip("Ce que joue la gâchette de l'index.")]
    public HornMode hornMode = HornMode.Alterne;
    [Tooltip("Bi-ton alterné : coché, il commence par le ton aigu (aigu puis grave). Décoché : grave puis aigu.")]
    public bool highToneFirst = true;
    [Tooltip("Bi-ton alterné : coché, les deux tons s'enchaînent en boucle tant que la gâchette est tenue. "
           + "Décoché : un seul enchaînement, puis le second ton est tenu.")]
    public bool repeatAlternation = true;
    [Tooltip("Bouton qui joue le ton grave seul, sans passer par la gâchette.")]
    public HornButton lowToneButton = HornButton.Principal;
    [Tooltip("Bouton qui joue le ton aigu seul, sans passer par la gâchette. Les deux boutons ensemble donnent les deux tons.")]
    public HornButton highToneButton = HornButton.Secondaire;
    [Tooltip("Fréquence du ton grave (Hz). 370 Hz sur les trains européens.")]
    public float lowTone = 370f;
    [Tooltip("Fréquence du ton aigu (Hz). 660 Hz sur les trains européens.")]
    public float highTone = 660f;
    [Tooltip("Durée (secondes) de chaque ton du bi-ton alterné.")]
    public float alternatePeriod = 0.6f;
    [Tooltip("Volume des trompes.")]
    [Range(0f, 1f)] public float hornVolume = 0.9f;
    [Tooltip("Sortie des trompes : le souffle part de ce point, dans la direction de son axe bleu (Z).")]
    public Transform hornMouth;

    [Header("Dégâts")]
    [Tooltip("Le souffle des trompes pousse, écrase et casse ce qui se trouve devant elles.")]
    public bool blastEnabled = true;
    [Tooltip("Force de choc (m/s) juste devant les trompes, réservoir à pleine pression et gâchette à fond. "
           + "Un objet casse si cette valeur, réduite par la charge et la distance, dépasse son Break Velocity.")]
    public float blastPower = 7f;
    [Tooltip("Influence de la pression chargée sur les dégâts. 0 = dégâts identiques quelle que soit la pression. "
           + "1 = proportionnels à la charge. 2 = il faut un réservoir presque plein pour faire mal.")]
    [Range(0f, 3f)] public float pressureInfluence = 1.5f;
    [Tooltip("Seuls les objets de solidité inférieure ou égale éclatent (verre 1, céramique 2, plastique 3, bois 5).")]
    public float maxSolidity = 3f;
    [Tooltip("Portée (mètres) du souffle.")]
    public float blastRange = 4f;
    [Tooltip("Demi-angle (degrés) du cône touché devant les trompes.")]
    [Range(5f, 90f)] public float blastAngle = 35f;
    [Tooltip("Poussée (m/s²) exercée sur les objets juste devant les trompes, à pleine charge.")]
    public float blastPush = 10f;
    [Tooltip("Vitesse d'écrasement (m/s) des objets déformables juste devant les trompes, à pleine charge. 0 = aucun.")]
    public float crushRate = 0.04f;
    [Tooltip("Layers des objets touchés par le souffle.")]
    public LayerMask affectedLayers = ~0;

    [Header("Vibrations")]
    [Tooltip("Vibration de la manette pendant que les trompes sonnent, plus forte à haute pression. 0 = aucune.")]
    [Range(0f, 1f)] public float hornHaptic = 1f;

    [Header("Audio")]
    [Tooltip("Règle automatiquement l'Audio Source (son 3D, portée longue). À décocher pour garder tes propres réglages.")]
    public bool configureAudioSource = true;

    [Header("Événements")]
    [Tooltip("Appelé quand les trompes se mettent à sonner.")]
    public UnityEvent onHornStart;
    [Tooltip("Appelé quand les trompes se taisent.")]
    public UnityEvent onHornStop;

    [Header("Diagnostic")]
    [Tooltip("Affiche dans la console, deux fois par seconde pendant un coup de klaxon, la pression, la charge et la force du souffle.")]
    public bool debugLog;

    /// <summary>Vrai tant que les trompes sonnent réellement (vanne ouverte et pression suffisante).</summary>
    public bool IsSounding => sounding;
    /// <summary>Vrai si le klaxon reçoit de l'air : compresseur présent et tuyau assez long.</summary>
    public bool IsConnected => connected;
    /// <summary>Charge utile du réservoir, de 0 (pression minimale des trompes) à 1 (pleine pression).</summary>
    public float Charge => charge;
    /// <summary>Force de choc (m/s) que le souffle aurait maintenant juste devant les trompes, gâchette à fond.</summary>
    public float CurrentBlastPower => blastPower * Mathf.Pow(charge, pressureInfluence);

    // ------------------------------------------------------------------ État (fil principal)

    WeaponGrab grab;
    WeaponHand ignoredFor;
    Rigidbody ownBody;
    AudioSource source;
    LineRenderer hose;

    float externalValve;
    float testUntil;
    bool connected;
    float charge;
    bool sounding;
    float nextBlast, nextHaptic, nextLog;

    readonly Collider[] overlap = new Collider[128];
    readonly HashSet<Rigidbody> seen = new HashSet<Rigidbody>();
    readonly HashSet<Collider> playerColliders = new HashSet<Collider>();

    // ------------------------------------------------------------------ Paramètres lus par le fil audio

    volatile float aValve;        // ouverture de la vanne, 0 à 1
    volatile float aTank;         // pression disponible, 0 à 1 (0 si débranché)
    volatile float aThreshold;    // pression minimale des trompes, 0 à 1
    volatile float aHornVolume;
    volatile float aAltPeriod = 0.6f;
    volatile int aMode;
    volatile int aButtons;        // tons demandés par les boutons : 1 = grave, 2 = aigu, 3 = les deux, 0 = aucun
    volatile bool aHighFirst = true;
    volatile bool aRepeat = true;
    volatile float aLevel;        // niveau réel des trompes, renvoyé par le fil audio
    volatile bool audioReady;

    // ------------------------------------------------------------------ État du synthétiseur (fil audio uniquement)

    const int TableSize = 2048;
    float[] lowSoft, lowBright, highSoft, highBright;
    float sampleRate = 48000f;
    float tableLow, tableHigh;    // fréquences pour lesquelles les tables ont été calculées

    float sValve, sHorn;          // vanne lissée, pression arrivant aux trompes
    float phaseLow, phaseHigh, ampLow, ampHigh;
    float altClock;
    uint rng = 0x2545F491u;
    float slowNoise, airLow;

    // ------------------------------------------------------------------ Initialisation

    void Awake()
    {
        grab = GetComponentInParent<WeaponGrab>();
        ownBody = GetComponentInParent<Rigidbody>();
        source = GetComponent<AudioSource>();

        if (compressor == null) compressor = FindNearestCompressor();
        if (compressor == null)
            Debug.LogWarning($"[TrainHorn] '{name}' n'est branché sur aucun compresseur : ajoute un objet avec AirCompressor "
                           + "dans la scène et glisse-le dans Compressor. Sans air, le klaxon reste muet.", this);

        sampleRate = Mathf.Max(8000, AudioSettings.outputSampleRate);
        BuildTables();

        // Le son est fabriqué dans OnAudioFilterRead, en multipliant un "clip" qui ne contient que des 1 :
        // Unity lui applique le volume, la distance et la position dans l'espace, et le résultat sert de gain.
        var ones = new float[1024];
        for (int i = 0; i < ones.Length; i++) ones[i] = 1f;
        AudioClip carrier = AudioClip.Create("HornCarrier", ones.Length, 1, (int)sampleRate, false);
        carrier.SetData(ones, 0);

        source.clip = carrier;
        source.loop = true;
        source.playOnAwake = true;
        source.dopplerLevel = 0f;
        source.pitch = 1f;
        if (configureAudioSource)
        {
            source.volume = 1f;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 2f;
            source.maxDistance = 100f;
        }

        PushParameters(0f, 0f);
        audioReady = true;
    }

    void OnEnable()
    {
        if (source != null && !source.isPlaying) source.Play();
    }

    void OnDestroy()
    {
        if (hose != null) Destroy(hose.gameObject);
    }

    AirCompressor FindNearestCompressor()
    {
#if UNITY_2023_1_OR_NEWER
        AirCompressor[] all = FindObjectsByType<AirCompressor>(FindObjectsSortMode.None);
#else
        AirCompressor[] all = FindObjectsOfType<AirCompressor>();
#endif
        AirCompressor best = null;
        float bestDist = float.PositiveInfinity;
        foreach (AirCompressor c in all)
        {
            float d = (c.transform.position - transform.position).sqrMagnitude;
            if (d < bestDist) { bestDist = d; best = c; }
        }
        return best;
    }

    /// <summary>Calcule une période du son de chaque trompe : une version sourde (basse pression) et une version brillante.</summary>
    void BuildTables()
    {
        tableLow = Mathf.Max(20f, lowTone);
        tableHigh = Mathf.Max(20f, highTone);
        lowSoft = BuildTable(tableLow, 1.7f);
        lowBright = BuildTable(tableLow, 0.85f);
        highSoft = BuildTable(tableHigh, 1.7f);
        highBright = BuildTable(tableHigh, 0.85f);
    }

    /// <summary>
    /// Une trompe à membrane produit un son très riche : la fondamentale et tous ses harmoniques, renforcés
    /// autour de 1,1 kHz et 2,6 kHz par le pavillon. "tilt" règle la vitesse à laquelle les harmoniques s'éteignent.
    /// </summary>
    float[] BuildTable(float frequency, float tilt)
    {
        var table = new float[TableSize + 1];
        float limit = Mathf.Min(sampleRate * 0.42f, 9000f); // pas d'harmonique au-delà : pas de repliement

        for (int k = 1; k * frequency < limit; k++)
        {
            float f = k * frequency;
            float amp = 1f / Mathf.Pow(k, tilt);
            amp *= 1f + 1.5f * Bump(f, 1150f, 400f) + 1.0f * Bump(f, 2600f, 700f);

            for (int i = 0; i <= TableSize; i++)
                table[i] += amp * Mathf.Sin(2f * Mathf.PI * k * i / TableSize);
        }

        float peak = 1e-6f;
        for (int i = 0; i <= TableSize; i++) peak = Mathf.Max(peak, Mathf.Abs(table[i]));
        for (int i = 0; i <= TableSize; i++) table[i] /= peak;
        return table;
    }

    static float Bump(float f, float center, float width)
    {
        float x = (f - center) / width;
        return Mathf.Exp(-x * x);
    }

    // ------------------------------------------------------------------ Commande

    /// <summary>
    /// Ouvre la vanne depuis un autre script, de 0 (fermée) à 1 (grande ouverte). Elle reste dans cet état
    /// jusqu'au prochain appel. La gâchette, si le klaxon est tenu, s'y ajoute.
    /// </summary>
    public void SetValve(float opening)
    {
        externalValve = Mathf.Clamp01(opening);
    }

    [ContextMenu("Klaxonner 2 secondes (test)")]
    void TestHorn()
    {
        if (Application.isPlaying) testUntil = Time.time + 2f;
    }

    static float ReadTrigger(WeaponHand hand)
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(hand.IsLeft ? XRNode.LeftHand : XRNode.RightHand);
        if (!device.isValid) return 0f;

        if (device.TryGetFeatureValue(CommonUsages.trigger, out float value)) return value;
        return device.TryGetFeatureValue(CommonUsages.triggerButton, out bool button) && button ? 1f : 0f;
    }

    static bool ReadButton(WeaponHand hand, HornButton button)
    {
        if (button == HornButton.Aucun) return false;

        InputDevice device = InputDevices.GetDeviceAtXRNode(hand.IsLeft ? XRNode.LeftHand : XRNode.RightHand);
        if (!device.isValid) return false;

        InputFeatureUsage<bool> usage = button == HornButton.Principal ? CommonUsages.primaryButton
                                      : button == HornButton.Secondaire ? CommonUsages.secondaryButton
                                      : CommonUsages.primary2DAxisClick;
        return device.TryGetFeatureValue(usage, out bool pressed) && pressed;
    }

    // ------------------------------------------------------------------ Simulation (fil principal)

    void Update()
    {
        float dt = Time.deltaTime;
        WeaponHand hand = grab != null ? grab.Holder : null;
        if (hand != ignoredFor) RebuildPlayerColliders(hand);

        // --- Vanne : gâchette (analogique), commande externe ou test
        float valve = externalValve;
        if (hand != null) valve = Mathf.Max(valve, ReadTrigger(hand));
        if (Time.time < testUntil) valve = 1f;
        valve = valve < 0.08f ? 0f : Mathf.Clamp01(valve);

        // --- Boutons : un ton seul, vanne grande ouverte. Ils prennent le pas sur ce que joue la gâchette.
        int buttons = 0;
        if (hand != null)
        {
            if (ReadButton(hand, lowToneButton)) buttons |= 1;
            if (ReadButton(hand, highToneButton)) buttons |= 2;
        }
        if (buttons != 0) valve = 1f;
        aButtons = buttons;

        // --- Air disponible : celui du compresseur, tant que le tuyau y arrive
        Vector3 inlet = hoseInlet != null ? hoseInlet.position : transform.position;
        connected = compressor != null && compressor.isActiveAndEnabled
                    && (hoseLength <= 0f || Vector3.Distance(inlet, compressor.HosePoint) <= hoseLength);

        float max = connected ? compressor.MaxPressure : 1f;
        float pressure = connected ? compressor.Pressure : 0f;
        float fill = Mathf.Clamp01(pressure / max);
        float minimum = Mathf.Min(minHornPressure, max - 0.05f);

        // Le klaxon vide le réservoir, d'autant plus vite que la vanne est ouverte et la pression élevée.
        if (connected && valve > 0f)
        {
            float rate = (max - minimum) / Mathf.Max(0.1f, blastTime);
            compressor.Draw(rate * valve * Mathf.Sqrt(fill) * dt);
        }

        // Charge utile : 0 quand les trompes s'éteignent, 1 à pleine pression. C'est elle qui décide des dégâts.
        charge = Mathf.InverseLerp(minimum, max, pressure);

        PushParameters(valve, connected ? fill : 0f);
        aThreshold = Mathf.Clamp(minimum / max, 0.05f, 0.9f);

        // --- Retour du synthétiseur : les trompes sonnent-elles vraiment ?
        float level = aLevel;
        bool now = sounding ? level > 0.04f : level > 0.1f;
        if (now != sounding)
        {
            sounding = now;
            if (sounding) onHornStart?.Invoke();
            else onHornStop?.Invoke();
        }

        // --- Dégâts : seulement quand les trompes chantent, avec une force qui dépend de la pression chargée
        float strength = sounding ? valve * Mathf.Pow(charge, pressureInfluence) : 0f;

        if (blastEnabled && strength > 0.02f && Time.time >= nextBlast)
        {
            const float interval = 0.12f;
            nextBlast = Time.time + interval;
            ApplyBlast(strength, interval);
        }

        if (debugLog && sounding && Time.time >= nextLog)
        {
            nextLog = Time.time + 0.5f;
            Debug.Log($"[TrainHorn] pression {pressure:F1} bars, charge {charge * 100f:F0} %, gâchette {valve * 100f:F0} % "
                    + $"→ force du souffle {blastPower * strength:F1} m/s devant les trompes", this);
        }

        if (hand != null && hornHaptic > 0f && level > 0.05f && Time.time >= nextHaptic)
        {
            hand.Vibrate(hornHaptic * Mathf.Lerp(0.3f, 1f, level), 0.07f);
            nextHaptic = Time.time + 0.05f;
        }
    }

    void LateUpdate()
    {
        UpdateHose();
    }

    void PushParameters(float valve, float fill)
    {
        aValve = valve;
        aTank = fill;
        aHornVolume = hornVolume;
        aAltPeriod = Mathf.Max(0.1f, alternatePeriod);
        aMode = (int)hornMode;
        aHighFirst = highToneFirst;
        aRepeat = repeatAlternation;
    }

    void RebuildPlayerColliders(WeaponHand hand)
    {
        ignoredFor = hand;
        playerColliders.Clear();
        if (hand == null) return;
        foreach (Collider c in hand.PlayerColliders) if (c != null) playerColliders.Add(c);
    }

    // ------------------------------------------------------------------ Tuyau

    /// <summary>Tuyau souple entre le klaxon et le compresseur : une courbe qui pend d'autant plus qu'il a du mou.</summary>
    void UpdateHose()
    {
        bool visible = showHose && connected;
        if (!visible)
        {
            if (hose != null) hose.enabled = false;
            return;
        }

        if (hose == null && !CreateHose()) return;
        hose.enabled = true;
        hose.widthMultiplier = hoseWidth;

        Vector3 a = hoseInlet != null ? hoseInlet.position : transform.position;
        Vector3 b = compressor.HosePoint;
        float distance = Vector3.Distance(a, b);

        // Mou du tuyau : sa longueur en trop s'il en a une, sinon une légère courbe.
        float slack = hoseLength > 0f ? Mathf.Max(0f, hoseLength - distance) : distance * 0.15f;
        float sag = Mathf.Min(0.6f, slack * 0.5f);

        int count = hose.positionCount;
        for (int i = 0; i < count; i++)
        {
            float t = i / (count - 1f);
            hose.SetPosition(i, Vector3.Lerp(a, b, t) + Vector3.down * (sag * 4f * t * (1f - t)));
        }
    }

    bool CreateHose()
    {
        Material mat = hoseMaterial;
        if (mat == null)
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                Debug.LogWarning("[TrainHorn] Shader 'Sprites/Default' introuvable : assigne un matériau dans Hose Material pour voir le tuyau.", this);
                showHose = false;
                return false;
            }
            mat = new Material(shader) { name = "HoseMaterial" };
        }

        var go = new GameObject("Tuyau");
        hose = go.AddComponent<LineRenderer>();
        hose.useWorldSpace = true;
        hose.positionCount = 20;
        hose.sharedMaterial = mat;
        hose.startColor = hoseColor;
        hose.endColor = hoseColor;
        hose.numCapVertices = 3;
        hose.numCornerVertices = 2;
        hose.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        hose.receiveShadows = false;
        return true;
    }

    // ------------------------------------------------------------------ Dégâts du souffle

    /// <summary>
    /// Pousse, écrase et casse ce qui se trouve dans le cône devant les trompes.
    /// strength : 0 à 1, produit de l'ouverture de la vanne et de la charge du réservoir.
    /// </summary>
    void ApplyBlast(float strength, float interval)
    {
        Transform mouth = hornMouth != null ? hornMouth : transform;
        Vector3 origin = mouth.position;
        Vector3 forward = mouth.forward;
        float cosLimit = Mathf.Cos(blastAngle * Mathf.Deg2Rad);

        int count = Physics.OverlapSphereNonAlloc(origin, blastRange, overlap, affectedLayers, QueryTriggerInteraction.Ignore);
        seen.Clear();

        for (int i = 0; i < count; i++)
        {
            Collider c = overlap[i];
            if (c == null || playerColliders.Contains(c)) continue;

            Rigidbody body = c.attachedRigidbody;
            if (body == null || body == ownBody || !seen.Add(body)) continue;

            Vector3 point = c.bounds.ClosestPoint(origin);
            Vector3 to = point - origin;
            float distance = to.magnitude;
            Vector3 dir = distance > 1e-4f ? to / distance : forward;

            // Dans le cône ? (tout ce qui est collé aux trompes est touché, quelle que soit la direction)
            float cos = Vector3.Dot(forward, dir);
            if (distance > 0.25f && cos < cosLimit) continue;
            float centered = distance <= 0.25f ? 1f : Mathf.InverseLerp(cosLimit, 1f, cos);

            // Force reçue par cet objet : charge du réservoir, puis distance, puis écart par rapport à l'axe.
            float local = strength * (1f - distance / Mathf.Max(0.01f, blastRange)) * Mathf.Lerp(0.4f, 1f, centered);
            if (local <= 0f) continue;

            RuntimeFracture fracture = body.GetComponent<RuntimeFracture>();
            if (fracture != null && fracture.Hit(point, dir * (blastPower * local), maxSolidity)) continue;

            if (crushRate > 0f)
            {
                RuntimeCrush crush = body.GetComponent<RuntimeCrush>();
                if (crush != null) crush.Crush(point, dir, crushRate * local * interval);
            }

            if (!body.isKinematic && blastPush > 0f)
                body.AddForce(dir * (blastPush * local * interval), ForceMode.VelocityChange);
        }
    }

    // ------------------------------------------------------------------ Synthèse du son (fil audio)

    /// <summary>
    /// Appelé par Unity sur le fil audio, plusieurs dizaines de fois par seconde. data contient déjà le gain
    /// calculé par l'Audio Source pour chaque canal (volume, distance, position) : on le multiplie par le son fabriqué.
    /// </summary>
    void OnAudioFilterRead(float[] data, int channels)
    {
        if (!audioReady || channels <= 0) return;

        float dt = 1f / sampleRate;
        int frames = data.Length / channels;

        // Paramètres lus une fois par bloc.
        float valveTarget = aValve;
        float tank = aTank;
        float threshold = aThreshold;
        float hornVol = aHornVolume;
        float altPeriod = aAltPeriod;
        int mode = aMode;
        int buttons = aButtons;
        bool highFirst = aHighFirst;
        bool repeat = aRepeat;

        // Constantes de lissage (exponentielles) pour ce taux d'échantillonnage.
        float kValve = 1f - Mathf.Exp(-dt / 0.012f);   // la vanne s'ouvre en ~12 ms
        float kRise = 1f - Mathf.Exp(-dt / 0.06f);     // l'air met ~60 ms à remplir les trompes
        float kFall = 1f - Mathf.Exp(-dt / 0.13f);     // ... et ~130 ms à s'en échapper
        float kAmp = 1f - Mathf.Exp(-dt / 0.02f);
        float kSlow = 2f * Mathf.PI * 14f * dt;        // bruit lent (~14 Hz) : instabilité des membranes

        float peakLevel = 0f;

        for (int n = 0; n < frames; n++)
        {
            // --- Bruit blanc, entre -1 et 1
            rng = rng * 1664525u + 1013904223u;
            float white = ((rng >> 8) & 0xFFFFFF) / 8388608f - 1f;

            slowNoise += (white - slowNoise) * kSlow;
            float wobble = Mathf.Clamp(slowNoise * 22f, -1f, 1f);
            airLow += (white - airLow) * 0.25f;
            float air = white - airLow;                // souffle : bruit sans ses graves

            // --- Air : la vanne, puis la pression qui arrive réellement aux trompes
            sValve += (valveTarget - sValve) * kValve;
            float feed = sValve * tank;
            sHorn += (feed - sHorn) * (feed > sHorn ? kRise : kFall);

            // --- Quelle(s) trompe(s) ?
            float gateLow = 1f, gateHigh = 1f;
            if (buttons != 0)
            {
                // Bouton : seulement le ou les tons demandés. Le bi-ton repartira de son premier ton.
                gateLow = (buttons & 1) != 0 ? 1f : 0f;
                gateHigh = (buttons & 2) != 0 ? 1f : 0f;
                altClock = 0f;
            }
            else if (mode == 1) gateHigh = 0f;
            else if (mode == 2) gateLow = 0f;
            else if (mode == 3)
            {
                if (sValve > 0.05f) altClock += dt / altPeriod; else altClock = 0f;

                int step = (int)altClock;
                if (!repeat && step > 1) step = 1;       // un seul enchaînement : on reste sur le second ton
                bool firstOfPair = (step & 1) == 0;
                if (firstOfPair == highFirst) gateLow = 0f; else gateHigh = 0f;
            }

            // La trompe aiguë demande un peu plus de pression : elle démarre après la grave et s'éteint avant.
            float low = Tone(ref phaseLow, ref ampLow, tableLow, lowSoft, lowBright, sHorn, threshold, gateLow, wobble, kAmp, dt);
            float high = Tone(ref phaseHigh, ref ampHigh, tableHigh, highSoft, highBright, sHorn, threshold + 0.04f, gateHigh, -wobble, kAmp, dt);

            float horn = (low + high * 0.85f) * 0.72f;
            horn = Mathf.Clamp(horn, -1.2f, 1.2f);
            horn *= 1f - 0.18f * horn * horn;          // légère saturation, comme une membrane poussée à fond

            float sing = Mathf.Max(ampLow, ampHigh);
            if (sing > peakLevel) peakLevel = sing;

            // --- Souffle de l'air : discret quand les trompes chantent, seul audible quand la pression manque
            float flow = sValve * Mathf.Sqrt(tank);
            float hiss = air * flow * (0.05f + 0.22f * (1f - sing));

            float sample = Mathf.Clamp((horn + hiss) * hornVol, -1f, 1f);

            int offset = n * channels;
            for (int c = 0; c < channels; c++) data[offset + c] *= sample;
        }

        aLevel = peakLevel;
    }

    /// <summary>Une trompe : son amplitude, sa hauteur et son timbre dépendent de la pression d'air qu'elle reçoit.</summary>
    static float Tone(ref float phase, ref float amp, float frequency, float[] soft, float[] bright,
                      float drive, float threshold, float gate, float wobble, float kAmp, float dt)
    {
        // Sous le seuil, la membrane ne vibre pas ; au-dessus, elle chante de plus en plus fort.
        float target = gate * Smooth(threshold - 0.04f, threshold + 0.16f, drive) * Mathf.Pow(Mathf.Max(drive, 0f), 0.6f);
        amp += (target - amp) * kAmp;
        if (amp < 0.0005f) return 0f;

        // Près du seuil, elle bégaie : son amplitude tremble de façon irrégulière.
        float stutter = 1f - Smooth(threshold, threshold + 0.22f, drive);
        float a = amp * (1f - stutter * 0.7f * (0.5f + 0.5f * wobble));

        // La note est un peu basse quand la pression manque (jusqu'à -7 %), et jamais parfaitement stable.
        float span = Mathf.Max(0.05f, 1f - threshold * 0.8f);
        float pitch = 0.93f + 0.07f * Mathf.Clamp01((drive - threshold * 0.8f) / span);
        phase += frequency * pitch * (1f + 0.004f * wobble) * dt;
        if (phase >= 1f) phase -= 1f;

        // Timbre : sourd à basse pression, brillant et agressif à pleine pression.
        float pos = phase * TableSize;
        int i = (int)pos;
        if (i >= TableSize) i = TableSize - 1;
        float frac = pos - i;
        float s = soft[i] + (soft[i + 1] - soft[i]) * frac;
        float b = bright[i] + (bright[i + 1] - bright[i]) * frac;
        float mix = Mathf.Clamp01((drive - threshold) / Mathf.Max(0.05f, 1f - threshold));

        return (s + (b - s) * mix * mix) * a;
    }

    static float Smooth(float edge0, float edge1, float x)
    {
        float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }

    // ------------------------------------------------------------------ Aide au réglage dans l'éditeur

    void OnDrawGizmosSelected()
    {
        if (!blastEnabled) return;
        Transform mouth = hornMouth != null ? hornMouth : transform;
        Vector3 o = mouth.position;
        float r = blastRange * Mathf.Tan(blastAngle * Mathf.Deg2Rad);
        Vector3 end = o + mouth.forward * blastRange;

        // Cône du souffle.
        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.8f);
        Gizmos.DrawLine(o, end + mouth.up * r);
        Gizmos.DrawLine(o, end - mouth.up * r);
        Gizmos.DrawLine(o, end + mouth.right * r);
        Gizmos.DrawLine(o, end - mouth.right * r);
        Gizmos.DrawLine(o, end);

        // Tuyau : sa longueur maximale autour du klaxon.
        if (hoseLength > 0f)
        {
            Gizmos.color = new Color(0.2f, 0.2f, 0.2f, 0.6f);
            Gizmos.DrawWireSphere(hoseInlet != null ? hoseInlet.position : transform.position, hoseLength);
        }
    }
}