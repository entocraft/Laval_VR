using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace RageRoom
{
    /// <summary>
    /// Barème du score : combien de points rapporte chaque objet, ce que vaut chaque façon de casser,
    /// et les conditions de tous les combos. Tout se règle ici, dans l'Inspector, sans toucher au code.
    ///
    /// L'asset doit s'appeler « ScoreTable » et se trouver dans un dossier Resources : il est créé et rempli
    /// par le menu « Rage Room > Score > Installer ou mettre à jour le score ».
    ///
    /// Calcul d'une casse : points de l'objet x multiplicateur du type de casse x multiplicateur du combo.
    /// Les bonus s'ajoutent tels quels, sans multiplicateur.
    /// </summary>
    [CreateAssetMenu(menuName = "Rage Room/Barème de score", fileName = "ScoreTable")]
    public class ScoreTable : ScriptableObject
    {
        public const string ResourceName = "ScoreTable";

        // ------------------------------------------------------------------ Points

        [Header("Points par objet")]
        [Tooltip("Une ligne par objet cassable. Remplie par le menu d'installation, avec une valeur proposée "
               + "d'après le matériau et la taille : change les points comme tu veux, ils ne seront pas écrasés.")]
        public List<ObjectPoints> objects = new List<ObjectPoints>();

        [Tooltip("Valeur d'un objet absent de la liste ci-dessus, d'après son matériau.")]
        public List<MaterialPoints> materials = new List<MaterialPoints>
        {
            new MaterialPoints("Glass", "Verre", 100),
            new MaterialPoints("Ceramic", "Céramique", 120),
            new MaterialPoints("Plastic", "Plastique", 60),
            new MaterialPoints("Wood", "Bois", 150),
            new MaterialPoints("Stone", "Pierre", 250),
            new MaterialPoints("Metal", "Métal", 80),
            new MaterialPoints("Cardboard", "Carton", 30),
            new MaterialPoints("Foam", "Mousse", 20),
            new MaterialPoints("Rubber", "Caoutchouc", 40),
        };

        [Tooltip("Valeur d'un objet qui n'est ni dans la liste des objets ni d'un matériau connu.")]
        [Min(0)] public int defaultPoints = 50;

        [Tooltip("Les points d'une casse sont arrondis au multiple le plus proche de cette valeur.")]
        [Min(1)] public int rounding = 5;

        // ------------------------------------------------------------------ Types de casse

        [Header("Types de casse")]
        [Tooltip("Multiplicateur appliqué selon la façon dont l'objet a été cassé.")]
        public List<CauseRule> causes = new List<CauseRule>
        {
            new CauseRule(ScoreCause.Coup, "Coup", 1f),
            new CauseRule(ScoreCause.EnMain, "Fracassé", 1f),
            new CauseRule(ScoreCause.Lancer, "Lancer", 1.25f),
            new CauseRule(ScoreCause.Projectile, "Projectile", 1.5f),
            new CauseRule(ScoreCause.Tir, "Tir", 1f),
            new CauseRule(ScoreCause.Explosion, "Explosion", 0.75f),
            new CauseRule(ScoreCause.Klaxon, "Klaxon", 1.25f),
            new CauseRule(ScoreCause.Collateral, "Collatéral", 0.5f),
        };

        // ------------------------------------------------------------------ Combo

        [Header("Combo (casses enchaînées)")]
        [Tooltip("Temps maximum (secondes) entre deux casses pour que le combo continue. Chaque casse relance le chrono.")]
        [Min(0.5f)] public float comboWindow = 3f;

        [Tooltip("Multiplicateur de points selon le nombre d'objets cassés dans le combo en cours.")]
        public List<MultiplierTier> multipliers = new List<MultiplierTier>
        {
            new MultiplierTier(1, 1f),
            new MultiplierTier(3, 2f),
            new MultiplierTier(6, 3f),
            new MultiplierTier(10, 4f),
            new MultiplierTier(15, 5f),
            new MultiplierTier(25, 6f),
        };

        // ------------------------------------------------------------------ Bonus

        [Header("Bonus d'explosion (dynamite, bidon...)")]
        [Tooltip("Compteur : objets cassés par une même explosion, réactions en chaîne comprises.")]
        public List<BonusTier> explosionBonus = new List<BonusTier>
        {
            new BonusTier(3, "Boum", 150),
            new BonusTier(5, "Gros boum", 300),
            new BonusTier(8, "Carnage", 600),
            new BonusTier(12, "Apocalypse", 1200),
        };

        [Tooltip("Compteur : explosifs qui sautent en chaîne (le premier compte pour 1).")]
        public List<BonusTier> chainBonus = new List<BonusTier>
        {
            new BonusTier(2, "Réaction en chaîne", 250),
            new BonusTier(3, "Effet domino", 500),
            new BonusTier(5, "Feu d'artifice", 1000),
        };

        [Tooltip("Délai maximum (secondes) entre deux explosions pour qu'elles forment une chaîne. "
               + "La seconde doit se trouver dans le rayon de la première.")]
        [Min(0.05f)] public float chainWindow = 0.6f;

        [Tooltip("Durée (secondes) après une explosion pendant laquelle un objet soufflé qui casse en retombant "
               + "compte encore pour cette explosion.")]
        [Min(0f)] public float blastAftermath = 2f;

        [Header("Bonus de tir (pistolet à billes)")]
        [Tooltip("Compteur : objets alignés cassés d'affilée par le pistolet.")]
        public List<BonusTier> lineBonus = new List<BonusTier>
        {
            new BonusTier(2, "Doublé", 200),
            new BonusTier(3, "Brochette", 500),
            new BonusTier(4, "Alignement parfait", 1000),
        };

        [Tooltip("Délai maximum (secondes) entre deux objets cassés pour qu'ils comptent comme alignés.")]
        [Min(0.02f)] public float lineWindow = 0.25f;

        [Tooltip("Écart d'angle maximum (degrés), vu depuis le canon, entre le premier objet cassé et les suivants.")]
        [Range(0.5f, 20f)] public float lineAngle = 4f;

        [Tooltip("Compteur : tirs consécutifs qui cassent chacun un objet. Un tir qui ne casse rien remet à zéro.")]
        public List<BonusTier> streakBonus = new List<BonusTier>
        {
            new BonusTier(5, "Sans faute", 250),
            new BonusTier(10, "Tireur d'élite", 750),
        };

        [Tooltip("Temps (secondes) laissé à une bille pour casser quelque chose avant que le tir compte comme raté.")]
        [Min(0.1f)] public float shotTimeout = 0.4f;

        [Tooltip("Distance entre le canon et l'objet cassé.")]
        public List<DistanceTier> longShotBonus = new List<DistanceTier>
        {
            new DistanceTier(4f, "Tir de loin", 50),
            new DistanceTier(7f, "Sniper", 150),
        };

        [Header("Bonus de coup (arme en main)")]
        [Tooltip("Compteur : objets cassés par un même coup d'arme.")]
        public List<BonusTier> sweepBonus = new List<BonusTier>
        {
            new BonusTier(2, "Coup double", 100),
            new BonusTier(3, "Balayage", 250),
            new BonusTier(5, "Tornade", 600),
        };

        [Tooltip("Délai maximum (secondes) entre deux objets cassés par la même arme pour qu'ils comptent comme un seul coup.")]
        [Min(0.05f)] public float sweepWindow = 0.35f;

        [Tooltip("Distance maximale (mètres) entre l'arme tenue et le point d'impact pour que la casse lui soit attribuée.")]
        [Min(0.05f)] public float reach = 0.35f;

        [Header("Bonus de lancer")]
        [Tooltip("Compteur : objets cassés par un même objet lancé.")]
        public List<BonusTier> strikeBonus = new List<BonusTier>
        {
            new BonusTier(2, "Double impact", 200),
            new BonusTier(3, "Strike", 500),
        };

        [Tooltip("Distance entre l'endroit où l'objet a été lâché et l'endroit où ça casse.")]
        public List<DistanceTier> longThrowBonus = new List<DistanceTier>
        {
            new DistanceTier(3f, "Beau lancer", 50),
            new DistanceTier(6f, "Lancer légendaire", 200),
        };

        [Tooltip("Durée (secondes) après le lâcher pendant laquelle un objet compte comme lancé.")]
        [Min(0.5f)] public float throwWindow = 4f;

        [Tooltip("Distance minimale (mètres) parcourue depuis le lâcher pour compter comme un lancer. "
               + "En dessous, l'objet compte comme fracassé en main.")]
        [Min(0f)] public float minThrowDistance = 0.5f;

        [Header("Bonus de variété")]
        [Tooltip("Compteur : types de casse différents dans un même combo (le collatéral ne compte pas).")]
        public List<BonusTier> varietyBonus = new List<BonusTier>
        {
            new BonusTier(3, "Touche-à-tout", 300),
            new BonusTier(5, "Arsenal complet", 1000),
        };

        // ------------------------------------------------------------------ HUD

        [Header("HUD")]
        [Tooltip("Affiche le score devant le joueur.")]
        public bool showHud = true;
        [Tooltip("ScoreHud.uxml. Rempli par le menu d'installation.")]
        public VisualTreeAsset hudUxml;
        [Tooltip("Panel Settings du HUD (World Space, 1000 pixels par unité, sans collider). Créé par le menu d'installation.")]
        public PanelSettings hudPanelSettings;
        [Tooltip("Police du HUD (optionnel). Le menu d'installation y met celle du shop si elle est dans le projet.")]
        public Font hudFont;
        [Tooltip("Distance (mètres) entre la tête et le HUD.")]
        [Range(0.5f, 3f)] public float hudDistance = 1.1f;
        [Tooltip("Angle (degrés) du HUD au-dessus du centre du regard. Négatif = en dessous.")]
        [Range(-40f, 40f)] public float hudAngleUp = 18f;
        [Tooltip("Angle (degrés) du HUD vers la gauche du regard. Négatif = à droite.")]
        [Range(-40f, 40f)] public float hudAngleLeft = 14f;
        [Tooltip("Taille du HUD. À 1, le panneau mesure 60 cm de large.")]
        [Range(0.2f, 2f)] public float hudScale = 0.7f;
        [Tooltip("Le HUD ne bouge pas tant que le regard reste dans ce cône (degrés), ni tant qu'on le regarde lui : "
               + "on peut donc le lire sans qu'il se sauve. Au-delà, il se replace par rapport au regard.")]
        [Range(5f, 60f)] public float hudDeadZone = 22f;
        [Tooltip("Vitesse à laquelle le HUD rattrape le regard. Plus c'est haut, plus il colle à la tête.")]
        [Range(1f, 20f)] public float hudFollowSpeed = 5f;
        [Tooltip("Nombre de lignes affichées en même temps sous le score.")]
        [Range(1, 6)] public int hudLines = 4;
        [Tooltip("Durée d'affichage (secondes) d'une ligne.")]
        [Min(0.5f)] public float hudLineLife = 2.5f;

        [Header("HUD : animation d'impact")]
        [Tooltip("Taille de départ d'une ligne qui vient s'écraser sur le HUD (1 = pas d'effet). Les bonus arrivent encore plus gros.")]
        [Range(1f, 6f)] public float hudSlamScale = 3f;
        [Tooltip("Durée (secondes) de la chute avant l'impact.")]
        [Range(0.05f, 0.5f)] public float hudSlamTime = 0.14f;
        [Tooltip("Recul du panneau (mètres) quand un bonus s'écrase dessus. Les lignes ordinaires le font reculer moitié moins. 0 = aucun.")]
        [Range(0f, 0.1f)] public float hudPunch = 0.035f;

        [Header("HUD : sons")]
        [Tooltip("Joue un son à chaque impact sur le HUD. Le tintement monte dans l'aigu à mesure que le combo grandit.")]
        public bool hudSounds = true;
        [Range(0f, 1f)] public float hudVolume = 0.7f;
        [Tooltip("Tes propres sons (optionnel). Vide = son fabriqué par le jeu.")]
        public AudioClip soundHit;
        [Tooltip("Tintement de combo : il est joué plus aigu à chaque casse, choisis un son bref.")]
        public AudioClip soundCombo;
        public AudioClip soundBonus;
        public AudioClip soundMultiplier;
        public AudioClip soundComboEnd;

        // ------------------------------------------------------------------ Démarrage

        [Header("Démarrage")]
        [Tooltip("Crée le score tout seul dans chaque scène qui contient des objets cassables. "
               + "À décocher pour placer toi-même un ScoreManager dans les scènes voulues.")]
        public bool autoCreate = true;
        [Tooltip("Scènes où le score ne doit jamais être créé automatiquement.")]
        public string[] excludedScenes = { "SC_Menu" };

        // ------------------------------------------------------------------ Lecture du barème

        /// <summary>Ligne du barème pour un objet, ou null. L'identifiant est comparé sans tenir compte de la casse.</summary>
        public ObjectPoints FindObject(string id)
        {
            if (string.IsNullOrEmpty(id) || objects == null) return null;
            for (int i = 0; i < objects.Count; i++)
                if (objects[i] != null && string.Equals(objects[i].id, id, StringComparison.OrdinalIgnoreCase)) return objects[i];
            return null;
        }

        /// <summary>Ligne du barème pour un matériau, ou null.</summary>
        public MaterialPoints FindMaterial(string material)
        {
            if (string.IsNullOrEmpty(material) || materials == null) return null;
            for (int i = 0; i < materials.Count; i++)
                if (materials[i] != null && string.Equals(materials[i].material, material, StringComparison.OrdinalIgnoreCase)) return materials[i];
            return null;
        }

        /// <summary>Points de base d'un objet : sa ligne, sinon celle de son matériau, sinon la valeur par défaut.</summary>
        public int PointsFor(string id, string material)
        {
            ObjectPoints o = FindObject(id);
            if (o != null) return o.points;
            MaterialPoints m = FindMaterial(material);
            return m != null ? m.points : defaultPoints;
        }

        /// <summary>Nom affiché pour un objet : celui de sa ligne, sinon celui de son matériau, sinon son identifiant.</summary>
        public string LabelFor(string id, string material)
        {
            ObjectPoints o = FindObject(id);
            if (o != null && !string.IsNullOrEmpty(o.label)) return o.label;
            MaterialPoints m = FindMaterial(material);
            if (m != null && !string.IsNullOrEmpty(m.label)) return m.label;
            return string.IsNullOrEmpty(id) ? "Objet" : id;
        }

        CauseRule FindCause(ScoreCause cause)
        {
            if (causes == null) return null;
            for (int i = 0; i < causes.Count; i++)
                if (causes[i] != null && causes[i].cause == cause) return causes[i];
            return null;
        }

        public float CauseMultiplier(ScoreCause cause)
        {
            CauseRule r = FindCause(cause);
            return r != null ? r.multiplier : 1f;
        }

        public string CauseLabel(ScoreCause cause)
        {
            CauseRule r = FindCause(cause);
            return r != null && !string.IsNullOrEmpty(r.label) ? r.label : cause.ToString();
        }

        /// <summary>Multiplicateur du combo pour un nombre d'objets cassés : le palier le plus haut atteint.</summary>
        public float MultiplierFor(int comboCount)
        {
            float best = 1f;
            int bestCount = int.MinValue;
            if (multipliers == null) return best;
            for (int i = 0; i < multipliers.Count; i++)
            {
                MultiplierTier t = multipliers[i];
                if (t == null || t.count > comboCount || t.count < bestCount) continue;
                bestCount = t.count;
                best = t.multiplier;
            }
            return best;
        }

        /// <summary>
        /// Avancement du combo vers le multiplicateur suivant, de 0 à 1.
        /// nextMultiplier vaut 0 quand le dernier palier est atteint (l'avancement vaut alors 1).
        /// </summary>
        public float MultiplierProgress(int comboCount, out float nextMultiplier)
        {
            int current = int.MinValue, next = int.MaxValue;
            nextMultiplier = 0f;
            if (multipliers != null)
                for (int i = 0; i < multipliers.Count; i++)
                {
                    MultiplierTier t = multipliers[i];
                    if (t == null) continue;
                    if (t.count <= comboCount)
                    {
                        if (t.count > current) current = t.count;
                    }
                    else if (t.count < next)
                    {
                        next = t.count;
                        nextMultiplier = t.multiplier;
                    }
                }

            if (next == int.MaxValue) return 1f;
            if (current == int.MinValue) current = 0;
            return Mathf.Clamp01((comboCount - current) / (float)(next - current));
        }

        /// <summary>Arrondit des points au multiple de « rounding » le plus proche.</summary>
        public int Round(float points)
        {
            int step = Mathf.Max(1, rounding);
            return Mathf.Max(0, Mathf.RoundToInt(points / step) * step);
        }
    }

    // ------------------------------------------------------------------ Types du barème
    // Déclarés après ScoreTable : Unity associe le fichier au premier type qu'il y trouve.

    /// <summary>Ce qui a cassé un objet. Chaque type a son libellé et son multiplicateur dans le barème.</summary>
    public enum ScoreCause
    {
        [InspectorName("Collatéral (chute, débris, cause inconnue)")] Collateral,
        [InspectorName("Coup (arme ou objet tenu en main)")] Coup,
        [InspectorName("Fracassé (l'objet cassé était tenu en main)")] EnMain,
        [InspectorName("Lancer (l'objet cassé a été lancé)")] Lancer,
        [InspectorName("Projectile (touché par un objet lancé)")] Projectile,
        [InspectorName("Tir (pistolet à billes)")] Tir,
        [InspectorName("Explosion")] Explosion,
        [InspectorName("Klaxon")] Klaxon
    }

    /// <summary>Points d'un objet précis. L'identifiant est le nom du prefab (ou de l'objet dans la scène).</summary>
    [Serializable]
    public class ObjectPoints
    {
        [Tooltip("Nom du prefab ou de l'objet, sans « (Clone) » ni numéro. Majuscules et minuscules indifférentes.")]
        public string id = "";
        [Tooltip("Nom affiché dans le HUD.")]
        public string label = "";
        [Tooltip("Points de base quand cet objet est cassé ou écrasé.")]
        [Min(0)] public int points = 100;

        public ObjectPoints() { }
        public ObjectPoints(string id, string label, int points) { this.id = id; this.label = label; this.points = points; }
    }

    /// <summary>Points par défaut d'un matériau, pour les objets absents de la liste des objets.</summary>
    [Serializable]
    public class MaterialPoints
    {
        [Tooltip("Nom du dossier de sons du matériau (champ Sound Material de l'objet) : Glass, Wood...")]
        public string material = "";
        [Tooltip("Nom affiché dans le HUD pour un objet de ce matériau sans nom propre.")]
        public string label = "";
        [Min(0)] public int points = 50;

        public MaterialPoints() { }
        public MaterialPoints(string material, string label, int points) { this.material = material; this.label = label; this.points = points; }
    }

    [Serializable]
    public class CauseRule
    {
        public ScoreCause cause;
        [Tooltip("Mot affiché dans le HUD à côté du nom de l'objet.")]
        public string label = "";
        [Tooltip("Les points de base de l'objet sont multipliés par cette valeur.")]
        [Min(0f)] public float multiplier = 1f;

        public CauseRule() { }
        public CauseRule(ScoreCause cause, string label, float multiplier) { this.cause = cause; this.label = label; this.multiplier = multiplier; }
    }

    [Serializable]
    public class MultiplierTier
    {
        [Tooltip("Nombre d'objets cassés dans le combo à partir duquel ce multiplicateur s'applique.")]
        [Min(1)] public int count = 1;
        [Min(1f)] public float multiplier = 1f;

        public MultiplierTier() { }
        public MultiplierTier(int count, float multiplier) { this.count = count; this.multiplier = multiplier; }
    }

    /// <summary>Palier d'un bonus : atteint quand le compteur du bonus arrive à « count ». Les paliers se cumulent.</summary>
    [Serializable]
    public class BonusTier
    {
        [Tooltip("Texte affiché dans le HUD.")]
        public string label = "";
        [Tooltip("Valeur du compteur qui déclenche ce palier.")]
        [Min(1)] public int count = 2;
        [Tooltip("Points ajoutés quand ce palier est atteint (en plus des paliers précédents).")]
        [Min(0)] public int points = 100;

        public BonusTier() { }
        public BonusTier(int count, string label, int points) { this.count = count; this.label = label; this.points = points; }
    }

    /// <summary>Palier de distance : seul le palier le plus élevé atteint est accordé.</summary>
    [Serializable]
    public class DistanceTier
    {
        public string label = "";
        [Tooltip("Distance minimale, en mètres.")]
        [Min(0f)] public float meters = 4f;
        [Min(0)] public int points = 50;

        public DistanceTier() { }
        public DistanceTier(float meters, string label, int points) { this.meters = meters; this.label = label; this.points = points; }
    }
}
