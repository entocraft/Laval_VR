using System.Collections;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Arme saisissable qui vient se placer directement dans la main : où qu'on l'attrape, elle se cale
/// sur son point de prise (le manche), toujours dans la même position et la même orientation.
/// Fonctionne avec WeaponHand.cs, à placer sur chaque manette.
///
/// Mise en place :
///  - À placer sur la racine de l'arme (l'objet qui porte le Rigidbody), avec au moins un collider.
///  - Créer un objet vide enfant sur le manche et le glisser dans Grip Point : l'arme est positionnée
///    pour que ce point coïncide exactement (position et rotation) avec le Hold Point de la main.
///  - Retirer le XR Grab Interactable de l'arme : les deux systèmes se disputeraient l'objet.
///  - Nécessite RuntimeFracture.cs dans le projet.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody))]
public class WeaponGrab : MonoBehaviour, IPointVelocity
{
    public enum HoldMode
    {
        [InspectorName("Rigide (suit la main exactement)")] Rigide,
        [InspectorName("Physique (bute contre le décor)")] Physique
    }

    public enum ReleasePhysics
    {
        [InspectorName("Tombe toujours (gravité activée)")] Tombe,
        [InspectorName("Comme avant la saisie")] CommeAvant
    }

    [Header("Prise")]
    [Tooltip("Point de prise sur le manche. L'arme est placée pour que ce point coïncide avec le Hold Point de la main. "
           + "Si vide, c'est l'origine de l'arme qui est utilisée.")]
    public Transform gripPoint;
    [Tooltip("Point de prise différent pour la main gauche (optionnel). Si vide, Grip Point sert aux deux mains.")]
    public Transform gripPointLeft;
    [Tooltip("Autre extrémité du manche (optionnel). Si renseigné, la main se place là où elle attrape le manche, "
           + "entre Grip Point et ce point, au lieu de toujours revenir au même endroit.")]
    public Transform handleEnd;

    [Header("Tenue")]
    [Tooltip("Rigide : l'arme suit la main sans jamais se décaler, et traverse le décor fixe. "
           + "Physique : l'arme est tirée vers la main et bute contre les murs et les tables.")]
    public HoldMode holdMode = HoldMode.Rigide;
    [Tooltip("Durée (secondes) du mouvement de l'arme vers la main au moment de la saisie. 0 = instantané.")]
    [Range(0f, 0.3f)] public float snapTime = 0.06f;

    [Header("Coups rapides")]
    [Tooltip("Vérifie à chaque pas physique tout le trajet parcouru par l'arme, pour que le bout, très rapide, "
           + "ne passe pas à travers un objet cassable sans le toucher.")]
    public bool sweepHits = true;
    [Tooltip("Layers des objets cassables à détecter sur le trajet de l'arme.")]
    public LayerMask strikeLayers = ~0;
    [Tooltip("Vitesse (m/s) en dessous de laquelle le trajet n'est pas vérifié : les contacts normaux suffisent.")]
    public float sweepMinSpeed = 1.5f;

    [Header("Lâcher")]
    [Tooltip("Tombe toujours : une fois lâchée, l'arme est soumise à la gravité, même si elle était figée (kinematic) "
           + "ou sans gravité quand on l'a prise, par exemple posée sur un présentoir ou livrée figée. "
           + "Comme avant la saisie : l'arme retrouve exactement les réglages de Rigidbody qu'elle avait avant d'être prise.")]
    public ReleasePhysics releasePhysics = ReleasePhysics.Tombe;
    [Tooltip("Vitesse donnée à l'arme quand on la lâche, par rapport à celle de la main. 0 = elle tombe sur place.")]
    [Range(0f, 3f)] public float throwMultiplier = 1.2f;

    [Header("Vibrations")]
    [Tooltip("Fait vibrer la manette quand l'arme tenue frappe quelque chose.")]
    public bool impactHaptics = true;
    [Tooltip("Intensité de la vibration pour un coup à pleine vitesse.")]
    [Range(0f, 1f)] public float hapticStrength = 0.7f;
    [Tooltip("Vitesse d'impact (m/s) en dessous de laquelle la manette ne vibre pas.")]
    public float hapticMinSpeed = 0.5f;
    [Tooltip("Vitesse d'impact (m/s) à laquelle la vibration est maximale.")]
    public float hapticMaxSpeed = 5f;

    [Header("Événements")]
    public UnityEvent onGrab;
    public UnityEvent onRelease;

    [Header("Diagnostic")]
    [Tooltip("Affiche dans la console l'état du Rigidbody à la saisie, au lâcher et un instant après.")]
    public bool debugLog;

    /// <summary>Main qui tient l'arme, ou null.</summary>
    public WeaponHand Holder { get; private set; }
    public bool IsHeld => Holder != null;

    // ------------------------------------------------------------------ État interne

    Rigidbody rb;
    Collider[] ownColliders;

    // Prise, exprimée dans le repère de l'arme (en mètres, indépendant de l'échelle).
    Vector3 gripLocalPos;
    Quaternion gripLocalRot = Quaternion.identity;

    // Mouvement vers la main au moment de la saisie
    float grabTime;
    Vector3 startPos;
    Quaternion startRot;

    // Réglages du Rigidbody avant la saisie, rendus au lâcher
    bool savedKinematic, savedGravity;
    RigidbodyInterpolation savedInterpolation;
    CollisionDetectionMode savedDetection;
    float savedMaxAngular;

    // Poses des derniers pas physiques : vitesse réelle de n'importe quel point de l'arme, rotation comprise.
    readonly Matrix4x4[] poses = new Matrix4x4[4];
    int poseHead;

    // Points répartis le long de l'arme, dont le trajet est vérifié à chaque pas
    Vector3[] strikePoints;
    float strikeRadius = 0.03f;
    readonly RaycastHit[] strikeHits = new RaycastHit[16];
    RuntimeFracture ownFracture;

    // Collisions coupées avec le joueur pendant la tenue
    Collider[] ignored;
    Coroutine restoreRoutine;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        ownColliders = GetComponentsInChildren<Collider>(true);
        ownFracture = GetComponent<RuntimeFracture>();
        BuildStrikePoints();

        if (GetComponent("XRGrabInteractable") != null)
            Debug.LogWarning($"[WeaponGrab] '{name}' porte aussi un XR Grab Interactable : retire-le, "
                           + "sinon les deux systèmes de saisie se disputent l'arme.", this);
    }

    void OnEnable()
    {
        Matrix4x4 m = transform.localToWorldMatrix;
        for (int i = 0; i < poses.Length; i++) poses[i] = m;
    }

    void OnDisable()
    {
        if (Holder != null) Holder.Release();
    }

    /// <summary>
    /// Vitesse (monde) d'un point de l'arme, moyennée sur les derniers pas physiques.
    /// Tient compte de la rotation : le bout d'une batte va bien plus vite que la main qui la tient.
    /// </summary>
    public Vector3 PointVelocity(Vector3 worldPoint)
    {
        Matrix4x4 newest = poses[poseHead];
        Matrix4x4 oldest = poses[(poseHead + 1) % poses.Length];
        Vector3 local = newest.inverse.MultiplyPoint3x4(worldPoint);
        float span = (poses.Length - 1) * Time.fixedDeltaTime;
        return (worldPoint - oldest.MultiplyPoint3x4(local)) / span;
    }

    // ------------------------------------------------------------------ Prise

    Transform GripFor(bool leftHand)
    {
        if (leftHand && gripPointLeft != null) return gripPointLeft;
        return gripPoint != null ? gripPoint : transform;
    }

    /// <summary>Endroit du manche où la main se placerait si elle saisissait l'arme maintenant.</summary>
    public Vector3 GripPosition(Vector3 handPosition, bool leftHand)
    {
        Vector3 a = GripFor(leftHand).position;
        if (handleEnd == null) return a;

        Vector3 ab = handleEnd.position - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-8f) return a;
        return a + ab * Mathf.Clamp01(Vector3.Dot(handPosition - a, ab) / len2);
    }

    /// <summary>
    /// Appelé par WeaponHand : attache l'arme à la main.
    /// instant = vrai : l'arme apparaît déjà calée dans la main, sans mouvement d'approche.
    /// </summary>
    public void AttachTo(WeaponHand hand, bool instant = false)
    {
        if (hand == null || Holder == hand) return;
        if (Holder != null)
        {
            WeaponHand previous = Holder;
            previous.Release(false); // changement de main : pas de lancer

            // L'autre main ne se savait plus porteuse de l'arme : on la détache ici, sinon l'état « tenue »
            // du Rigidbody serait mémorisé comme son état normal et rendu au prochain lâcher.
            if (Holder != null) Detach(Vector3.zero, Vector3.zero, previous.HoldPosition);
        }

        // Annule un éventuel rétablissement des collisions en attente (arme reprise juste après un lâcher).
        if (restoreRoutine != null)
        {
            StopCoroutine(restoreRoutine);
            restoreRoutine = null;
            SetIgnored(false);
        }

        Holder = hand;

        Transform grip = GripFor(hand.IsLeft);
        Vector3 gripWorld = GripPosition(hand.HoldPosition, hand.IsLeft);
        Quaternion invRot = Quaternion.Inverse(transform.rotation);
        gripLocalPos = invRot * (gripWorld - transform.position);
        gripLocalRot = invRot * grip.rotation;

        if (instant)
        {
            Quaternion rot = hand.HoldRotation * Quaternion.Inverse(gripLocalRot);
            Vector3 pos = hand.HoldPosition - rot * gripLocalPos;
            transform.SetPositionAndRotation(pos, rot);
            rb.position = pos;
            rb.rotation = rot;
            grabTime = Time.time - 10f; // mouvement d'approche déjà terminé
        }
        else
        {
            grabTime = Time.time;
        }
        startPos = transform.position;
        startRot = transform.rotation;

        savedKinematic = rb.isKinematic;
        savedGravity = rb.useGravity;
        savedInterpolation = rb.interpolation;
        savedDetection = rb.collisionDetectionMode;
        savedMaxAngular = rb.maxAngularVelocity;

        if (debugLog) Debug.Log($"[WeaponGrab] '{name}' saisie. Avant la saisie : {DescribeBody()}", this);

        if (holdMode == HoldMode.Rigide)
        {
            // Le mode de détection est changé avant de passer en kinematic (ContinuousDynamic n'y est pas permis).
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.isKinematic = true;
        }
        else
        {
            rb.isKinematic = false;
            rb.useGravity = false;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.maxAngularVelocity = 50f;
        }
        rb.interpolation = RigidbodyInterpolation.None; // affiche la dernière pose physique, sans retard ajouté

        // L'arme ne doit pas se cogner à la main ni au corps du joueur.
        ignored = hand.PlayerColliders;
        SetIgnored(true);

        onGrab?.Invoke();
    }

    /// <summary>Appelé par WeaponHand : détache l'arme et lui donne la vitesse de la main.</summary>
    public void Detach(Vector3 handVelocity, Vector3 handAngularVelocity, Vector3 handPosition)
    {
        if (Holder == null) return;
        Holder = null;

        bool fall = releasePhysics == ReleasePhysics.Tombe;
        rb.isKinematic = !fall && savedKinematic;
        rb.collisionDetectionMode = savedDetection;
        rb.useGravity = fall || savedGravity;
        rb.interpolation = savedInterpolation;
        rb.maxAngularVelocity = savedMaxAngular;
        if (fall)
        {
            // Une arme dont la position était bloquée (contraintes du Rigidbody) resterait en l'air.
            rb.constraints &= ~RigidbodyConstraints.FreezePosition;
            rb.WakeUp();
        }

        if (debugLog) Debug.Log($"[WeaponGrab] '{name}' lâchée. Après le lâcher : {DescribeBody()}", this);

        if (!rb.isKinematic)
        {
            // Vitesse du centre de l'arme : celle de la main, plus l'effet de la rotation du poignet.
            Vector3 v = handVelocity + Vector3.Cross(handAngularVelocity, rb.worldCenterOfMass - handPosition);
            SetVelocity(rb, v * throwMultiplier);
            rb.angularVelocity = handAngularVelocity * Mathf.Min(1f, throwMultiplier);
        }

        // Les collisions avec le joueur reviennent un instant plus tard, le temps que l'arme s'éloigne de la main.
        if (isActiveAndEnabled) restoreRoutine = StartCoroutine(RestoreCollisions());
        else SetIgnored(false);

        onRelease?.Invoke();
    }

    IEnumerator RestoreCollisions()
    {
        yield return new WaitForSeconds(0.3f);
        restoreRoutine = null;
        SetIgnored(false);

        // L'arme devait tomber, mais quelque chose l'a de nouveau figée depuis le lâcher : on le signale et on corrige.
        if (Holder == null && releasePhysics == ReleasePhysics.Tombe && Floating())
        {
            Debug.LogWarning($"[WeaponGrab] '{name}' a été lâchée mais ne tombe pas : un autre script ou composant a modifié "
                           + $"son Rigidbody après le lâcher ({DescribeBody()}). Composants présents : {DescribeComponents()}", this);
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.constraints &= ~RigidbodyConstraints.FreezePosition;
            rb.WakeUp();
        }
        else if (debugLog)
        {
            Debug.Log($"[WeaponGrab] '{name}', 0,3 s après le lâcher : {DescribeBody()}", this);
        }
    }

    /// <summary>Vrai si le Rigidbody ne peut pas tomber : figé, sans gravité, ou position bloquée sur l'axe vertical.</summary>
    bool Floating()
    {
        return rb.isKinematic || !rb.useGravity || (rb.constraints & RigidbodyConstraints.FreezePositionY) != 0;
    }

    string DescribeBody()
    {
        return $"Is Kinematic = {rb.isKinematic}, Use Gravity = {rb.useGravity}, Constraints = {rb.constraints}, "
             + $"objet actif = {gameObject.activeInHierarchy}, parent = {(transform.parent != null ? transform.parent.name : "aucun")}";
    }

    string DescribeComponents()
    {
        var names = new System.Text.StringBuilder();
        foreach (Component c in GetComponents<Component>())
        {
            if (c == null) continue;
            if (names.Length > 0) names.Append(", ");
            names.Append(c.GetType().Name);
        }
        return names.ToString();
    }

    void SetIgnored(bool ignore)
    {
        if (ignored == null) return;
        foreach (Collider mine in ownColliders)
        {
            if (mine == null) continue;
            foreach (Collider theirs in ignored)
            {
                if (theirs == null || theirs == mine) continue;
                Physics.IgnoreCollision(mine, theirs, ignore);
            }
        }
        if (!ignore) ignored = null;
    }

    // ------------------------------------------------------------------ Suivi de la main

    void FixedUpdate()
    {
        Matrix4x4 previous = poses[poseHead];
        poseHead = (poseHead + 1) % poses.Length;
        poses[poseHead] = transform.localToWorldMatrix;

        if (Holder == null) return;
        if (sweepHits) SweepStrikes(previous, poses[poseHead]);

        // Pose de l'arme pour que le point de prise coïncide avec le point de tenue de la main.
        Quaternion rot = Holder.HoldRotation * Quaternion.Inverse(gripLocalRot);
        Vector3 pos = Holder.HoldPosition - rot * gripLocalPos;

        // Mouvement adouci pendant les premières millisecondes de la saisie.
        if (snapTime > 0f)
        {
            float t = (Time.time - grabTime) / snapTime;
            if (t < 1f)
            {
                t = t * t * (3f - 2f * t);
                pos = Vector3.Lerp(startPos, pos, t);
                rot = Quaternion.Slerp(startRot, rot, t);
            }
        }

        if (holdMode == HoldMode.Rigide)
        {
            // MovePosition donne à l'arme une vraie vitesse : les objets frappés sont projetés correctement.
            rb.MovePosition(pos);
            rb.MoveRotation(rot);
            return;
        }

        float dt = Time.fixedDeltaTime;
        Vector3 toTarget = pos - rb.position;

        // Arme coincée loin derrière un obstacle : elle revient d'un coup dans la main.
        if (toTarget.sqrMagnitude > 0.5f * 0.5f)
        {
            rb.position = pos;
            rb.rotation = rot;
            SetVelocity(rb, Vector3.zero);
            rb.angularVelocity = Vector3.zero;
            return;
        }

        SetVelocity(rb, Vector3.ClampMagnitude(toTarget / dt, 30f));

        Quaternion delta = rot * Quaternion.Inverse(rb.rotation);
        delta.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f) angle -= 360f;
        if (Mathf.Abs(angle) > 0.01f && !float.IsNaN(axis.x) && !float.IsInfinity(axis.x))
            rb.angularVelocity = axis.normalized * (angle * Mathf.Deg2Rad / dt);
        else
            rb.angularVelocity = Vector3.zero;
    }

    // ------------------------------------------------------------------ Coups rapides

    /// <summary>Répartit 8 points le long de l'arme (son plus grand axe), d'après la forme de ses colliders.</summary>
    void BuildStrikePoints()
    {
        bool has = false;
        Bounds local = default;

        foreach (Collider c in ownColliders)
        {
            if (c == null || c.isTrigger || !LocalBounds(c, out Bounds cb)) continue;

            // Les 8 coins du collider, ramenés dans le repère de l'arme.
            for (int i = 0; i < 8; i++)
            {
                Vector3 sign = new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f);
                Vector3 corner = cb.center + Vector3.Scale(cb.extents, sign);
                Vector3 p = transform.InverseTransformPoint(c.transform.TransformPoint(corner));
                if (has) local.Encapsulate(p);
                else { local = new Bounds(p, Vector3.zero); has = true; }
            }
        }
        if (!has) return;

        // Tailles en mètres, pour comparer les axes malgré l'échelle de l'objet.
        Vector3 ls = transform.lossyScale;
        Vector3 world = new Vector3(Mathf.Abs(local.size.x * ls.x), Mathf.Abs(local.size.y * ls.y), Mathf.Abs(local.size.z * ls.z));

        int axis = world.x >= world.y && world.x >= world.z ? 0 : world.y >= world.z ? 1 : 2;
        Vector3 dir = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
        float half = local.extents[axis];

        const int count = 8;
        strikePoints = new Vector3[count];
        for (int i = 0; i < count; i++)
            strikePoints[i] = local.center + dir * Mathf.Lerp(-half, half, i / (count - 1f));

        // Rayon : demi-épaisseur moyenne de l'arme.
        float thickness = (world.x + world.y + world.z - world[axis]) * 0.25f;
        strikeRadius = Mathf.Clamp(thickness, 0.01f, 0.08f);
    }

    static bool LocalBounds(Collider c, out Bounds b)
    {
        if (c is BoxCollider box)
        {
            b = new Bounds(box.center, box.size);
            return true;
        }
        if (c is SphereCollider sphere)
        {
            b = new Bounds(sphere.center, Vector3.one * (sphere.radius * 2f));
            return true;
        }
        if (c is CapsuleCollider capsule)
        {
            Vector3 size = Vector3.one * (capsule.radius * 2f);
            size[capsule.direction] = Mathf.Max(capsule.height, capsule.radius * 2f);
            b = new Bounds(capsule.center, size);
            return true;
        }
        if (c is MeshCollider mesh && mesh.sharedMesh != null)
        {
            b = mesh.sharedMesh.bounds;
            return true;
        }
        b = default;
        return false;
    }

    /// <summary>
    /// Balaie le trajet parcouru par chaque point de l'arme pendant le dernier pas physique et signale le coup
    /// aux objets cassables rencontrés, avec la vitesse réelle du point qui les a touchés.
    /// </summary>
    void SweepStrikes(Matrix4x4 from, Matrix4x4 to)
    {
        if (strikePoints == null) return;
        if (Time.time - grabTime < snapTime + 0.1f) return; // pas pendant que l'arme rejoint la main

        float dt = Time.fixedDeltaTime;
        float solidity = ownFracture != null ? ownFracture.solidity : Mathf.Infinity;

        for (int i = 0; i < strikePoints.Length; i++)
        {
            Vector3 a = from.MultiplyPoint3x4(strikePoints[i]);
            Vector3 b = to.MultiplyPoint3x4(strikePoints[i]);
            Vector3 move = b - a;
            float dist = move.magnitude;

            // Trop lent : les contacts normaux suffisent. Trop long : l'arme vient d'être replacée d'un coup.
            if (dist < sweepMinSpeed * dt || dist > 0.75f) continue;

            int count = Physics.SphereCastNonAlloc(a, strikeRadius, move / dist, strikeHits, dist, strikeLayers,
                                                   QueryTriggerInteraction.Ignore);
            for (int h = 0; h < count; h++)
            {
                Collider c = strikeHits[h].collider;
                if (c == null || c.attachedRigidbody == rb) continue;

                RuntimeFracture target = c.GetComponentInParent<RuntimeFracture>();
                if (target == null || target == ownFracture) continue;

                // Distance nulle : le point était déjà dans l'objet au départ, Unity ne donne pas de point d'impact.
                Vector3 point = strikeHits[h].distance > 0f ? strikeHits[h].point : a;
                if (target.Hit(point, move / dt, solidity) && impactHaptics)
                    Holder.Vibrate(hapticStrength, 0.1f);
            }
        }
    }

    // ------------------------------------------------------------------ Vibrations à l'impact

    void OnCollisionEnter(Collision col)
    {
        if (Holder == null || !impactHaptics) return;

        float speed = col.relativeVelocity.magnitude;
        if (speed < 0.01f) speed = Holder.Velocity.magnitude;

        float t = Mathf.InverseLerp(hapticMinSpeed, hapticMaxSpeed, speed);
        if (t <= 0f) return;
        Holder.Vibrate(hapticStrength * Mathf.Lerp(0.3f, 1f, t), Mathf.Lerp(0.04f, 0.12f, t));
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
        Transform grip = gripPoint != null ? gripPoint : transform;

        // Repère du point de prise : il sera aligné sur le Hold Point de la main.
        Gizmos.color = Color.red; Gizmos.DrawLine(grip.position, grip.position + grip.right * 0.06f);
        Gizmos.color = Color.green; Gizmos.DrawLine(grip.position, grip.position + grip.up * 0.06f);
        Gizmos.color = Color.blue; Gizmos.DrawLine(grip.position, grip.position + grip.forward * 0.06f);

        // En mode Play : points de l'arme dont le trajet est vérifié pour les coups rapides.
        if (strikePoints != null)
        {
            Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.7f);
            foreach (Vector3 p in strikePoints) Gizmos.DrawWireSphere(transform.TransformPoint(p), strikeRadius);
        }

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(grip.position, 0.015f);
        if (handleEnd != null)
        {
            Gizmos.DrawLine(grip.position, handleEnd.position);
            Gizmos.DrawWireSphere(handleEnd.position, 0.015f);
        }
    }
}