using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Compresseur à air avec son réservoir : la première moitié du klaxon de train (l'autre est TrainHorn.cs).
/// C'est lui qui stocke la puissance : plus la pression chargée est haute, plus le klaxon branché dessus
/// sonne fort et fait de dégâts.
///
/// Fonctionnement, comme un vrai compresseur d'atelier :
///  - sous la pression d'enclenchement, le moteur démarre et regonfle le réservoir ;
///  - il peine un peu plus à mesure que la pression monte ;
///  - à pleine pression, il s'arrête avec un "pschitt" de purge, puis attend que la pression retombe.
/// Le bruit (moteur électrique + battement des pistons + purge) est fabriqué en direct à partir de cet état :
/// il n'y a aucun fichier son à fournir.
///
/// Mise en place :
///  - À placer sur l'objet du compresseur. Le script ajoute et règle lui-même l'Audio Source dont il a besoin.
///  - Ne pas mettre d'autre Audio Source sur le même objet : le son fabriqué passerait aussi par elle.
///  - Optionnel : un objet vide là où le tuyau se branche (Hose Outlet), l'aiguille d'un manomètre (Gauge Needle),
///    et la partie du modèle qui doit trembler quand le moteur tourne (Shake Target).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(AudioSource))]
public class AirCompressor : MonoBehaviour
{
    [Header("Réservoir")]
    [Tooltip("Pression maximale du réservoir (bars). Le compresseur s'arrête quand elle est atteinte.")]
    public float maxPressure = 9f;
    [Tooltip("Le réservoir est plein au démarrage. Décoché : il part vide et le compresseur doit d'abord le remplir.")]
    public bool startFull = true;

    [Header("Compresseur")]
    [Tooltip("Compresseur sous tension. Décoché : le réservoir ne se regonfle plus, il ne reste que l'air déjà stocké.")]
    public bool powered = true;
    [Tooltip("Pression (bars) sous laquelle le moteur se remet en marche.")]
    public float cutInPressure = 7f;
    [Tooltip("Durée (secondes) pour remplir entièrement un réservoir vide.")]
    public float fillTime = 20f;
    [Tooltip("Volume du compresseur.")]
    [Range(0f, 1f)] public float volume = 0.7f;

    [Header("Tuyau")]
    [Tooltip("Point où le tuyau du klaxon se branche (optionnel). Si vide, c'est l'origine de l'objet.")]
    public Transform hoseOutlet;

    [Header("Manomètre")]
    [Tooltip("Aiguille d'un manomètre (optionnel) : elle tourne selon la pression du réservoir.")]
    public Transform gaugeNeedle;
    [Tooltip("Axe de rotation de l'aiguille, dans son propre repère.")]
    public Vector3 gaugeAxis = Vector3.forward;
    [Tooltip("Angle (degrés) de l'aiguille réservoir vide.")]
    public float gaugeAngleEmpty = 0f;
    [Tooltip("Angle (degrés) de l'aiguille réservoir plein.")]
    public float gaugeAngleFull = -240f;

    [Header("Tremblement")]
    [Tooltip("Partie du modèle qui tremble quand le moteur tourne (optionnel). Ne pas choisir l'objet qui porte le Rigidbody.")]
    public Transform shakeTarget;
    [Tooltip("Amplitude du tremblement (mètres).")]
    public float shakeAmount = 0.0015f;

    [Header("Audio")]
    [Tooltip("Règle automatiquement l'Audio Source (son 3D). À décocher pour garder tes propres réglages.")]
    public bool configureAudioSource = true;

    [Header("Événements")]
    [Tooltip("Appelé quand le moteur démarre.")]
    public UnityEvent onMotorStart;
    [Tooltip("Appelé quand le moteur s'arrête.")]
    public UnityEvent onMotorStop;

    /// <summary>Pression actuelle du réservoir, en bars.</summary>
    public float Pressure => pressure;
    /// <summary>Pression maximale du réservoir, en bars.</summary>
    public float MaxPressure => Mathf.Max(0.1f, maxPressure);
    /// <summary>Remplissage du réservoir, de 0 (vide) à 1 (pleine pression).</summary>
    public float Fill => Mathf.Clamp01(pressure / MaxPressure);
    /// <summary>Vrai tant que le moteur tourne.</summary>
    public bool MotorRunning => motorOn;
    /// <summary>Point de branchement du tuyau (monde).</summary>
    public Vector3 HosePoint => hoseOutlet != null ? hoseOutlet.position : transform.position;

    // ------------------------------------------------------------------ État de la machine (fil principal)

    AudioSource source;
    Quaternion gaugeRest = Quaternion.identity;
    Vector3 shakeRest;
    float pressure;
    bool motorOn;
    float spin; // montée en régime du moteur, 0 à 1

    // ------------------------------------------------------------------ Paramètres lus par le fil audio

    volatile float aTank;
    volatile float aVolume;
    volatile bool aMotor;
    volatile int aPurge; // incrémenté à chaque purge
    volatile bool audioReady;

    // ------------------------------------------------------------------ État du synthétiseur (fil audio uniquement)

    float sampleRate = 48000f;
    uint rng = 0x9E3779B9u;
    float airLow, rumble;
    float motor, motorPhase, pistonPhase, thumpPhase;
    float purgeEnv;
    int seenPurge;

    // ------------------------------------------------------------------ Initialisation

    void Awake()
    {
        source = GetComponent<AudioSource>();
        if (gaugeNeedle != null) gaugeRest = gaugeNeedle.localRotation;
        if (shakeTarget != null) shakeRest = shakeTarget.localPosition;

        pressure = startFull ? MaxPressure : 0f;
        sampleRate = Mathf.Max(8000, AudioSettings.outputSampleRate);

        // Le son est fabriqué dans OnAudioFilterRead, en multipliant un "clip" qui ne contient que des 1 :
        // Unity lui applique le volume, la distance et la position dans l'espace, et le résultat sert de gain.
        var ones = new float[1024];
        for (int i = 0; i < ones.Length; i++) ones[i] = 1f;
        AudioClip carrier = AudioClip.Create("CompressorCarrier", ones.Length, 1, (int)sampleRate, false);
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
            source.minDistance = 1f;
            source.maxDistance = 30f;
        }

        PushParameters();
        audioReady = true;
    }

    void OnEnable()
    {
        if (source != null && !source.isPlaying) source.Play();
    }

    // ------------------------------------------------------------------ Commande

    /// <summary>
    /// Prélève de l'air dans le réservoir (en bars) et renvoie la quantité réellement prélevée.
    /// Appelé par le klaxon à chaque image tant que sa vanne est ouverte.
    /// </summary>
    public float Draw(float bars)
    {
        float taken = Mathf.Clamp(bars, 0f, pressure);
        pressure -= taken;
        return taken;
    }

    /// <summary>Met le compresseur sous tension ou le coupe.</summary>
    public void SetPowered(bool on)
    {
        powered = on;
    }

    [ContextMenu("Marche / arrêt (test)")]
    public void TogglePower()
    {
        powered = !powered;
    }

    [ContextMenu("Vider le réservoir (test)")]
    void TestEmpty()
    {
        if (Application.isPlaying) pressure = 0f;
    }

    // ------------------------------------------------------------------ Simulation (fil principal)

    void Update()
    {
        float dt = Time.deltaTime;
        float max = MaxPressure;

        // Pressostat : démarrage sous la pression d'enclenchement, arrêt à pleine pression.
        if (powered)
        {
            if (!motorOn && pressure <= Mathf.Min(cutInPressure, max - 0.05f)) SetMotor(true, false);
            else if (motorOn && pressure >= max) SetMotor(false, true);
        }
        else if (motorOn)
        {
            SetMotor(false, false);
        }

        if (motorOn)
        {
            spin = Mathf.MoveTowards(spin, 1f, dt / 0.45f); // montée en régime
            pressure += max / Mathf.Max(0.1f, fillTime) * spin * dt;
        }
        else
        {
            spin = Mathf.MoveTowards(spin, 0f, dt / 0.6f);
        }
        pressure = Mathf.Clamp(pressure, 0f, max);

        PushParameters();
        UpdateGauge();
        UpdateShake();
    }

    void SetMotor(bool on, bool purge)
    {
        motorOn = on;
        if (purge) aPurge = aPurge + 1; // "pschitt" de mise à vide à l'arrêt
        if (on) onMotorStart?.Invoke();
        else onMotorStop?.Invoke();
    }

    void PushParameters()
    {
        aTank = Fill;
        aVolume = volume;
        aMotor = motorOn;
    }

    void UpdateGauge()
    {
        if (gaugeNeedle == null) return;
        Vector3 axis = gaugeAxis.sqrMagnitude > 1e-6f ? gaugeAxis.normalized : Vector3.forward;
        gaugeNeedle.localRotation = gaugeRest * Quaternion.AngleAxis(Mathf.Lerp(gaugeAngleEmpty, gaugeAngleFull, Fill), axis);
    }

    void UpdateShake()
    {
        if (shakeTarget == null) return;
        shakeTarget.localPosition = spin > 0.01f ? shakeRest + Random.insideUnitSphere * (shakeAmount * spin) : shakeRest;
    }

    // ------------------------------------------------------------------ Synthèse du son (fil audio)

    /// <summary>
    /// Appelé par Unity sur le fil audio. data contient déjà le gain calculé par l'Audio Source pour chaque canal
    /// (volume, distance, position) : on le multiplie par le son fabriqué.
    /// </summary>
    void OnAudioFilterRead(float[] data, int channels)
    {
        if (!audioReady || channels <= 0) return;

        float dt = 1f / sampleRate;
        int frames = data.Length / channels;

        float tank = aTank;
        float vol = aVolume;
        float motorTarget = aMotor ? 1f : 0f;

        int purge = aPurge;
        if (purge != seenPurge) { seenPurge = purge; purgeEnv = 1f; }

        float kMotor = 1f - Mathf.Exp(-dt / (motorTarget > 0.5f ? 0.3f : 0.5f)); // démarrage vif, arrêt en roue libre
        float kRumble = 2f * Mathf.PI * 220f * dt;
        float kPurge = Mathf.Exp(-dt / 0.12f);

        for (int n = 0; n < frames; n++)
        {
            // Bruit blanc entre -1 et 1, puis ses deux versions : souffle (sans les graves) et grondement (graves seuls).
            rng = rng * 1664525u + 1013904223u;
            float white = ((rng >> 8) & 0xFFFFFF) / 8388608f - 1f;
            airLow += (white - airLow) * 0.25f;
            float air = white - airLow;
            rumble += (white - rumble) * kRumble;

            float sample = 0f;

            motor += (motorTarget - motor) * kMotor;
            if (motor > 0.001f)
            {
                float speed = motor * (1f - 0.12f * tank); // le moteur peine un peu quand la pression monte

                // Moteur électrique : un bourdonnement et ses premiers harmoniques.
                motorPhase += 98f * speed * dt;
                if (motorPhase >= 1f) motorPhase -= 1f;
                float w = 2f * Mathf.PI * motorPhase;
                float hum = 0.5f * Mathf.Sin(w) + 0.3f * Mathf.Sin(2f * w) + 0.14f * Mathf.Sin(3f * w);

                // Pistons : environ 23 allers-retours par seconde, un coup sourd au refoulement, un souffle à l'aspiration.
                pistonPhase += 23f * speed * dt;
                if (pistonPhase >= 1f) pistonPhase -= 1f;
                float half = pistonPhase + 0.5f;
                if (half >= 1f) half -= 1f;
                float push = Mathf.Exp(-pistonPhase * 9f);
                float pull = Mathf.Exp(-half * 13f);

                thumpPhase += 64f * dt;
                if (thumpPhase >= 1f) thumpPhase -= 1f;
                float thump = Mathf.Sin(2f * Mathf.PI * thumpPhase);

                sample = motor * (0.8f + 0.2f * tank) * (hum * 0.35f + push * (thump * 0.6f + rumble * 2.2f) + pull * air * 0.5f) * 0.9f;
            }

            // Purge à l'arrêt : un jet d'air bref qui s'éteint.
            if (purgeEnv > 0.0005f)
            {
                sample += (air * 0.8f + rumble * 0.6f) * purgeEnv * 0.6f;
                purgeEnv *= kPurge;
            }

            sample = Mathf.Clamp(sample * vol, -1f, 1f);

            int offset = n * channels;
            for (int c = 0; c < channels; c++) data[offset + c] *= sample;
        }
    }
}
