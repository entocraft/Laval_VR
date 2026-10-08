using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR;

/// <summary>
/// Main qui saisit les armes (objets portant WeaponGrab). À placer sur chaque manette.
/// Lit la gâchette latérale (grip) de la manette, cherche l'arme la plus proche et la cale dans la main.
///
/// Mise en place :
///  - À placer sur l'objet de la manette gauche et sur celui de la manette droite (ceux qui suivent le suivi VR).
///  - Régler Side sur chacune.
///  - Créer un objet vide enfant au creux de la paume et le glisser dans Hold Point : c'est là que vient
///    se placer le Grip Point de l'arme. Son orientation décide de la façon dont l'arme est tenue.
///  - Glisser la racine du joueur (XR Origin) dans Player Root pour que l'arme tenue ne se cogne pas au joueur.
/// </summary>
[DisallowMultipleComponent]
public class WeaponHand : MonoBehaviour
{
    public enum Side
    {
        [InspectorName("Gauche")] Gauche,
        [InspectorName("Droite")] Droite
    }

    public enum GrabInput
    {
        [InspectorName("Maintenir la gâchette")] Maintenir,
        [InspectorName("Bascule (un appui prend, un appui lâche)")] Bascule
    }

    [Header("Main")]
    public Side side = Side.Droite;
    [Tooltip("Point de tenue au creux de la paume. Le Grip Point de l'arme vient s'y aligner (position et rotation). "
           + "Si vide, c'est l'origine de la manette qui est utilisée.")]
    public Transform holdPoint;

    [Header("Saisie")]
    [Tooltip("Distance (mètres) autour de la main dans laquelle une arme peut être attrapée.")]
    public float grabRadius = 0.12f;
    [Tooltip("Layers dans lesquels chercher les armes.")]
    public LayerMask grabLayers = ~0;
    [Tooltip("Maintenir : l'arme tombe dès qu'on relâche la gâchette. Bascule : plus reposant pour une arme gardée longtemps.")]
    public GrabInput grabInput = GrabInput.Maintenir;
    [Tooltip("Lit la gâchette latérale (grip) de la manette. À décocher pour piloter la main uniquement "
           + "depuis un autre script ou des événements, avec Grab(), Release() et Toggle().")]
    public bool readControllerGrip = true;
    [Tooltip("Enfoncement de la gâchette (0 à 1) à partir duquel la main se ferme.")]
    [Range(0.1f, 1f)] public float gripThreshold = 0.6f;

    [Header("Joueur")]
    [Tooltip("Racine du joueur (XR Origin). L'arme tenue ignore tous les colliders situés dessous. "
           + "Si vide, seuls les colliders de cette manette sont ignorés.")]
    public Transform playerRoot;
    [Tooltip("Composants désactivés tant qu'une arme est tenue, par exemple l'interactor XR de cette main, "
           + "pour ne pas attraper autre chose en même temps.")]
    public Behaviour[] disableWhileHolding;

    [Header("Vibrations")]
    [Tooltip("Petite vibration au moment de la saisie. 0 = aucune.")]
    [Range(0f, 1f)] public float grabHaptic = 0.3f;

    [Header("Événements")]
    public UnityEvent<WeaponGrab> onGrab;
    public UnityEvent<WeaponGrab> onRelease;

    /// <summary>Arme actuellement tenue, ou null.</summary>
    public WeaponGrab HeldWeapon { get; private set; }
    public bool IsLeft => side == Side.Gauche;
    public Vector3 HoldPosition => holdPoint != null ? holdPoint.position : transform.position;
    public Quaternion HoldRotation => holdPoint != null ? holdPoint.rotation : transform.rotation;

    /// <summary>Vitesse de la main (m/s), moyennée sur les dernières images.</summary>
    public Vector3 Velocity { get; private set; }
    /// <summary>Vitesse de rotation de la main (rad/s).</summary>
    public Vector3 AngularVelocity { get; private set; }

    /// <summary>Colliders du joueur que l'arme tenue doit ignorer.</summary>
    public Collider[] PlayerColliders
    {
        get
        {
            if (playerColliders == null)
                playerColliders = (playerRoot != null ? playerRoot : transform).GetComponentsInChildren<Collider>(true);
            return playerColliders;
        }
    }

    // ------------------------------------------------------------------ État interne

    const int Samples = 6;
    readonly Vector3[] posHistory = new Vector3[Samples];
    readonly Quaternion[] rotHistory = new Quaternion[Samples];
    readonly float[] timeHistory = new float[Samples];
    int head;

    readonly Collider[] overlap = new Collider[32];
    Collider[] playerColliders;
    bool gripDown;

    void OnEnable()
    {
        for (int i = 0; i < Samples; i++)
        {
            posHistory[i] = HoldPosition;
            rotHistory[i] = HoldRotation;
            timeHistory[i] = Time.time;
        }
    }

    void OnDisable()
    {
        Release(false);
    }

    void Update()
    {
        TrackMotion();
        if (readControllerGrip) ReadGrip();
    }

    // ------------------------------------------------------------------ Entrée manette

    void ReadGrip()
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(IsLeft ? XRNode.LeftHand : XRNode.RightHand);
        if (!device.isValid) return;

        float value;
        if (!device.TryGetFeatureValue(CommonUsages.grip, out value))
        {
            if (!device.TryGetFeatureValue(CommonUsages.gripButton, out bool button)) return;
            value = button ? 1f : 0f;
        }

        // Seuil de relâchement plus bas que le seuil de fermeture, pour éviter les va-et-vient à la limite.
        bool down = gripDown ? value > gripThreshold - 0.2f : value >= gripThreshold;
        if (down == gripDown) return;
        gripDown = down;

        if (grabInput == GrabInput.Bascule)
        {
            if (down) Toggle();
        }
        else if (down) TryGrab();
        else Release();
    }

    void TrackMotion()
    {
        head = (head + 1) % Samples;
        posHistory[head] = HoldPosition;
        rotHistory[head] = HoldRotation;
        timeHistory[head] = Time.time;

        int oldest = (head + 1) % Samples;
        float span = timeHistory[head] - timeHistory[oldest];
        if (span < 1e-4f) return;

        Velocity = (posHistory[head] - posHistory[oldest]) / span;

        Quaternion delta = rotHistory[head] * Quaternion.Inverse(rotHistory[oldest]);
        delta.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f) angle -= 360f;
        bool valid = Mathf.Abs(angle) > 0.01f && !float.IsNaN(axis.x) && !float.IsInfinity(axis.x);
        AngularVelocity = valid ? axis.normalized * (angle * Mathf.Deg2Rad / span) : Vector3.zero;
    }

    // ------------------------------------------------------------------ Saisie

    /// <summary>Saisit l'arme la plus proche de la main, s'il y en a une à portée. Renvoie vrai si une arme a été prise.</summary>
    public bool TryGrab()
    {
        if (HeldWeapon != null) return true;

        Vector3 hand = HoldPosition;
        int count = Physics.OverlapSphereNonAlloc(hand, grabRadius, overlap, grabLayers, QueryTriggerInteraction.Collide);

        WeaponGrab best = null;
        float bestDist = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            WeaponGrab weapon = overlap[i].GetComponentInParent<WeaponGrab>();
            if (weapon == null || !weapon.isActiveAndEnabled) continue;

            float d = (weapon.GripPosition(hand, IsLeft) - hand).sqrMagnitude;
            if (d < bestDist) { bestDist = d; best = weapon; }
        }

        if (best == null) return false;
        Grab(best);
        return true;
    }

    /// <summary>
    /// Place directement cette arme dans la main, où qu'elle se trouve dans la scène
    /// (par exemple une arme choisie dans un menu).
    /// </summary>
    public void Grab(WeaponGrab weapon)
    {
        Grab(weapon, false);
    }

    /// <summary>Comme Grab. instant = vrai : l'arme apparaît déjà calée dans la main, sans mouvement d'approche.</summary>
    public void Grab(WeaponGrab weapon, bool instant)
    {
        if (weapon == null || weapon == HeldWeapon) return;
        if (HeldWeapon != null) Release(false);

        HeldWeapon = weapon;
        weapon.AttachTo(this, instant); // si l'autre main la tenait, elle la lâche

        SetBehaviours(false);
        if (grabHaptic > 0f) Vibrate(grabHaptic, 0.05f);
        onGrab?.Invoke(weapon);
    }

    /// <summary>Crée un exemplaire du prefab et le place directement dans la main. Renvoie l'arme créée.</summary>
    public WeaponGrab EquipPrefab(GameObject prefab)
    {
        if (prefab == null) return null;

        GameObject go = Instantiate(prefab, HoldPosition, HoldRotation);
        WeaponGrab weapon = go.GetComponentInChildren<WeaponGrab>();
        if (weapon == null)
        {
            Debug.LogWarning($"[WeaponHand] Le prefab '{prefab.name}' ne contient pas de composant WeaponGrab.", this);
            return null;
        }

        Grab(weapon, true);
        return weapon;
    }

    /// <summary>Lâche l'arme tenue, avec la vitesse de la main.</summary>
    public void Release()
    {
        Release(true);
    }

    /// <summary>Lâche l'arme tenue. withThrow = faux : elle tombe sur place, sans élan.</summary>
    public void Release(bool withThrow)
    {
        if (HeldWeapon == null) return;

        WeaponGrab weapon = HeldWeapon;
        HeldWeapon = null;
        weapon.Detach(withThrow ? Velocity : Vector3.zero, withThrow ? AngularVelocity : Vector3.zero, HoldPosition);

        SetBehaviours(true);
        onRelease?.Invoke(weapon);
    }

    /// <summary>Lâche l'arme si la main en tient une, sinon tente d'en saisir une.</summary>
    public void Toggle()
    {
        if (HeldWeapon != null) Release();
        else TryGrab();
    }

    void SetBehaviours(bool enabledState)
    {
        if (disableWhileHolding == null) return;
        foreach (Behaviour b in disableWhileHolding)
            if (b != null) b.enabled = enabledState;
    }

    // ------------------------------------------------------------------ Vibrations

    /// <summary>Fait vibrer la manette de cette main. amplitude de 0 à 1, duration en secondes.</summary>
    public void Vibrate(float amplitude, float duration)
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(IsLeft ? XRNode.LeftHand : XRNode.RightHand);
        if (device.isValid) device.SendHapticImpulse(0u, Mathf.Clamp01(amplitude), Mathf.Max(0.01f, duration));
    }

    // ------------------------------------------------------------------ Tests et aide au réglage

    [ContextMenu("Saisir l'arme la plus proche (test)")]
    void TestGrab()
    {
        if (!Application.isPlaying) return;

        // Sans limite de distance : pratique pour tester sans casque.
        WeaponGrab best = null;
        float bestDist = float.PositiveInfinity;
#if UNITY_2023_1_OR_NEWER
        WeaponGrab[] all = FindObjectsByType<WeaponGrab>(FindObjectsSortMode.None);
#else
        WeaponGrab[] all = FindObjectsOfType<WeaponGrab>();
#endif
        foreach (WeaponGrab weapon in all)
        {
            float d = (weapon.transform.position - HoldPosition).sqrMagnitude;
            if (d < bestDist) { bestDist = d; best = weapon; }
        }
        Grab(best);
    }

    [ContextMenu("Lâcher (test)")]
    void TestRelease()
    {
        if (Application.isPlaying) Release();
    }

    void OnDrawGizmosSelected()
    {
        Vector3 p = holdPoint != null ? holdPoint.position : transform.position;
        Quaternion r = holdPoint != null ? holdPoint.rotation : transform.rotation;

        Gizmos.color = new Color(1f, 0.8f, 0f, 0.6f);
        Gizmos.DrawWireSphere(p, grabRadius);

        // Repère du point de tenue : le Grip Point de l'arme s'aligne dessus.
        Gizmos.color = Color.red;   Gizmos.DrawLine(p, p + r * Vector3.right * 0.06f);
        Gizmos.color = Color.green; Gizmos.DrawLine(p, p + r * Vector3.up * 0.06f);
        Gizmos.color = Color.blue;  Gizmos.DrawLine(p, p + r * Vector3.forward * 0.06f);
    }
}
