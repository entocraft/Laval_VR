using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Laisse une tache de peinture (décal URP) quand un objet rempli est cassé.
///
/// Mise en place :
///  - À placer sur le même objet que RuntimeFracture (la racine de la bouteille).
///    Le script s'abonne tout seul à son événement onBreak.
///  - "Decal Prefab" : un prefab contenant un URP Decal Projector avec le matériau
///    issu du Shader Graph de décal (propriétés _BaseMap et _Color).
///  - "Allowed Decals" : les PNG de taches parmi lesquels le script pioche au hasard.
///  - "Preset" : remplit d'un coup couleur, opacité et taille (jus d'orange, vin rouge, ketchup...).
///    Les réglages restent modifiables ensuite ; "Custom" ne touche à rien.
/// </summary>
public class PaintSplash : MonoBehaviour
{
    public enum Preset { Custom, Water, Milk, OrangeJuice, RedWine, Cola, Beer, Ketchup, Mustard, Paint }

    [Header("Preset")]
    [Tooltip("Remplit automatiquement la couleur, l'opacité et la taille ci-dessous. "
           + "Les valeurs restent modifiables ensuite. Custom : aucun changement.")]
    public Preset preset = Preset.Custom;
    [SerializeField, HideInInspector] Preset appliedPreset = Preset.Custom;

    [Header("Contenu")]
    [Tooltip("Décoché : l'objet est vide et ne laisse aucune tache.")]
    public bool filled = true;
    [Tooltip("Coché : couleur vive tirée au hasard. Décoché : utilise la couleur ci-dessous.")]
    public bool randomColor = true;
    [Tooltip("Couleur de la tache quand Random Color est décoché.")]
    [ColorUsage(false)] public Color color = Color.red;
    [Tooltip("Textures de taches autorisées pour cet objet (une est tirée au hasard).")]
    public List<Texture2D> allowedDecals = new List<Texture2D>();

    [Header("Décal")]
    [Tooltip("Prefab avec un URP Decal Projector et le matériau de décal.")]
    public DecalProjector decalPrefab;
    [Range(0f, 1f)] public float opacity = 0.9f;
    [Tooltip("Taille de la tache en mètres (min, max).")]
    public Vector2 sizeRange = new Vector2(0.3f, 0.6f);
    [Tooltip("Layers pouvant recevoir une tache. Retire celui du joueur et des mains.")]
    public LayerMask surfaceLayers = ~0;
    [Tooltip("Objet cassé en l'air : distance max (m) sur laquelle le liquide tombe jusqu'au sol.")]
    public float maxDropDistance = 3f;

    // Nombre maximum de taches dans la scène : au-delà, la plus ancienne disparaît.
    const int MaxDecals = 40;
    static readonly Queue<DecalProjector> liveDecals = new Queue<DecalProjector>();

    static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    RuntimeFracture fracture;

    // Dans l'éditeur : applique le preset dès qu'on change le menu déroulant.
    void OnValidate()
    {
        if (preset == appliedPreset) return;
        ApplyPreset(preset);
    }

    /// <summary>Applique un preset (utilisable aussi depuis un autre script).</summary>
    public void ApplyPreset(Preset p)
    {
        preset = appliedPreset = p;
        switch (p)
        {
            //                                    couleur (R, V, B)              opacité  taille min/max (m)
            case Preset.Water: SetLiquid(new Color(0.10f, 0.12f, 0.15f), 0.35f, 0.30f, 0.60f); break; // trace mouillée
            case Preset.Milk: SetLiquid(new Color(0.97f, 0.96f, 0.92f), 0.90f, 0.30f, 0.60f); break;
            case Preset.OrangeJuice: SetLiquid(new Color(1.00f, 0.60f, 0.05f), 0.85f, 0.30f, 0.60f); break;
            case Preset.RedWine: SetLiquid(new Color(0.35f, 0.02f, 0.08f), 0.90f, 0.30f, 0.60f); break;
            case Preset.Cola: SetLiquid(new Color(0.20f, 0.09f, 0.03f), 0.80f, 0.30f, 0.60f); break;
            case Preset.Beer: SetLiquid(new Color(0.90f, 0.65f, 0.15f), 0.60f, 0.30f, 0.60f); break;
            case Preset.Ketchup: SetLiquid(new Color(0.75f, 0.05f, 0.03f), 1.00f, 0.20f, 0.40f); break; // épais : s'étale moins
            case Preset.Mustard: SetLiquid(new Color(0.90f, 0.70f, 0.10f), 1.00f, 0.20f, 0.40f); break;
            case Preset.Paint:
                randomColor = true;
                opacity = 1f;
                sizeRange = new Vector2(0.35f, 0.70f);
                break;
        }
    }

    void SetLiquid(Color c, float o, float sizeMin, float sizeMax)
    {
        randomColor = false;
        color = c;
        opacity = o;
        sizeRange = new Vector2(sizeMin, sizeMax);
    }

    void Awake()
    {
        fracture = GetComponent<RuntimeFracture>();
        if (fracture != null) fracture.onBreak.AddListener(Splash);
        else Debug.LogWarning($"[PaintSplash] Aucun RuntimeFracture sur '{name}' : appelle Splash() à la main.", this);
    }

    void OnDestroy()
    {
        if (fracture != null) fracture.onBreak.RemoveListener(Splash);
    }

    /// <summary>Pose une tache à partir du point d'impact (monde). Appelé automatiquement à la casse.</summary>
    public void Splash(Vector3 impact)
    {
        if (!filled || decalPrefab == null || allowedDecals.Count == 0) return;
        if (!FindSurface(impact, out Vector3 point, out Vector3 normal)) return;
        filled = false;

        // Le projecteur vise la surface (axe Z), avec une rotation aléatoire pour varier les taches.
        float size = Random.Range(sizeRange.x, sizeRange.y);
        Quaternion rotation = Quaternion.LookRotation(-normal) * Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));

        DecalProjector decal = Instantiate(decalPrefab, point, rotation);
        decal.size = new Vector3(size, size, 0.2f);
        decal.pivot = Vector3.zero; // boîte de projection centrée sur la surface

        // Une copie du matériau par tache : chacune a sa texture et sa couleur.
        Material mat = new Material(decal.material);
        mat.SetTexture(BaseMapId, allowedDecals[Random.Range(0, allowedDecals.Count)]);
        mat.SetColor(ColorId, randomColor ? Random.ColorHSV(0f, 1f, 0.7f, 1f, 0.8f, 1f) : color);
        decal.material = mat;
        decal.fadeFactor = opacity;

        liveDecals.Enqueue(decal);
        while (liveDecals.Count > MaxDecals)
        {
            DecalProjector old = liveDecals.Dequeue();
            if (old == null) continue; // déjà détruit (changement de scène)
            Destroy(old.material);
            Destroy(old.gameObject);
        }
    }

    // Cherche la surface à tacher : d'abord celle qui a été heurtée, sinon le sol en dessous.
    bool FindSurface(Vector3 impact, out Vector3 point, out Vector3 normal)
    {
        Vector3 center = TryGetComponent(out Rigidbody rb) ? rb.worldCenterOfMass : transform.position;
        Vector3 toImpact = impact - center;

        RaycastHit hit = default;
        bool found = toImpact.sqrMagnitude > 1e-6f
                     && StaticHit(center, toImpact.normalized, toImpact.magnitude + 0.1f, out hit);

        // Cassé en l'air (par une batte, un autre objet...) : le liquide tombe au sol.
        if (!found) found = StaticHit(impact + Vector3.up * 0.05f, Vector3.down, maxDropDistance, out hit);

        point = hit.point;
        normal = hit.normal;
        return found;
    }

    // Premier collider fixe (sans Rigidbody) sur le rayon : ignore fragments, outils et objets mobiles.
    bool StaticHit(Vector3 origin, Vector3 direction, float distance, out RaycastHit best)
    {
        best = default;
        float bestDistance = float.MaxValue;
        foreach (RaycastHit h in Physics.RaycastAll(origin, direction, distance, surfaceLayers, QueryTriggerInteraction.Ignore))
        {
            if (h.rigidbody != null || h.distance >= bestDistance) continue;
            best = h;
            bestDistance = h.distance;
        }
        return bestDistance < float.MaxValue;
    }
}