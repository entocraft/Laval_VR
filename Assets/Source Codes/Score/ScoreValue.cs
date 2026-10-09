using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace RageRoom
{
    /// <summary>
    /// Rend un objet cassable « comptable » : quand il casse (RuntimeFracture) ou qu'il est écrasé (RuntimeCrush),
    /// il le signale au score, avec ce qu'il faut pour savoir comment il a été cassé (tenu en main, lancé...).
    ///
    /// Mise en place : à côté de RuntimeFracture ou RuntimeCrush, sur l'objet qui porte le Rigidbody.
    /// Le menu « Rage Room > Score > Installer ou mettre à jour le score » l'ajoute tout seul sur les prefabs cassables.
    /// Les objets posés à la main dans la scène le reçoivent automatiquement au lancement.
    /// Les fragments n'en ont pas : un morceau qui recasse ne rapporte rien.
    /// </summary>
    [DisallowMultipleComponent]
    public class ScoreValue : MonoBehaviour
    {
        [Tooltip("Identifiant de l'objet dans le barème (liste « Objects » de ScoreTable). "
               + "Vide : le nom de l'objet est utilisé, sans « (Clone) » ni numéro.")]
        public string objectId = "";
        [Tooltip("Points de cet exemplaire précis. 0 = valeur du barème.")]
        [Min(0)] public int pointsOverride = 0;
        [Tooltip("Nom affiché dans le HUD pour cet exemplaire précis. Vide = nom du barème.")]
        public string labelOverride = "";

        /// <summary>Numéro unique de cet objet pour le score.</summary>
        public int Id { get; private set; }
        /// <summary>Vrai tant que le joueur tient l'objet.</summary>
        public bool IsHeld => grab != null && grab.isSelected;
        /// <summary>Moment (Time.time) du dernier lâcher, ou -1 si l'objet n'a jamais été tenu.</summary>
        public float ReleasedAt { get; private set; } = -1f;
        /// <summary>Position de l'objet au moment du dernier lâcher.</summary>
        public Vector3 ReleasePosition { get; private set; }
        public Rigidbody Body { get; private set; }

        /// <summary>Matériau de l'objet (dossier de sons de son preset) : Glass, Wood...</summary>
        public string Material
        {
            get
            {
                if (fracture != null && !string.IsNullOrEmpty(fracture.soundMaterial)) return fracture.soundMaterial;
                if (crush != null && !string.IsNullOrEmpty(crush.soundMaterial)) return crush.soundMaterial;
                return "";
            }
        }

        /// <summary>Identifiant utilisé dans le barème.</summary>
        public string TableId => string.IsNullOrEmpty(objectId) ? CleanName(name) : objectId;

        /// <summary>Colliders de l'objet, pour mesurer s'il est à portée d'un point d'impact.</summary>
        public Collider[] Colliders
        {
            get
            {
                if (colliders == null) colliders = GetComponentsInChildren<Collider>();
                return colliders;
            }
        }

        RuntimeFracture fracture;
        RuntimeCrush crush;
        XRBaseInteractable grab;
        Collider[] colliders;
        bool breakScored, crushScored, listening;

        void Awake()
        {
            Id = ScoreManager.NextId();
            Body = GetComponent<Rigidbody>();
            fracture = GetComponent<RuntimeFracture>();
            crush = GetComponent<RuntimeCrush>();
            grab = GetComponent<XRBaseInteractable>();
            if (grab == null) grab = GetComponentInParent<XRBaseInteractable>();
        }

        void OnEnable()
        {
            if (listening) return;
            listening = true;
            if (fracture != null) fracture.onBreak.AddListener(OnBroken);
            if (crush != null) crush.onCrush.AddListener(OnCrushed);
            if (grab != null)
            {
                grab.selectEntered.AddListener(OnGrabbed);
                grab.selectExited.AddListener(OnReleased);
            }
        }

        void OnDisable()
        {
            if (!listening) return;
            listening = false;
            if (fracture != null) fracture.onBreak.RemoveListener(OnBroken);
            if (crush != null) crush.onCrush.RemoveListener(OnCrushed);
            if (grab != null)
            {
                grab.selectEntered.RemoveListener(OnGrabbed);
                grab.selectExited.RemoveListener(OnReleased);
            }
        }

        void OnBroken(Vector3 impactPoint)
        {
            if (breakScored) return;
            breakScored = true;
            if (ScoreManager.Instance != null) ScoreManager.Instance.Report(this, impactPoint, false);
        }

        void OnCrushed(Vector3 contactPoint)
        {
            // Un écrasement dure : seul le premier compte.
            if (crushScored || breakScored) return;
            crushScored = true;
            if (ScoreManager.Instance != null) ScoreManager.Instance.Report(this, contactPoint, true);
        }

        void OnGrabbed(SelectEnterEventArgs args)
        {
            if (ScoreManager.Instance != null) ScoreManager.Instance.NotifyGrabbed(this);
        }

        void OnReleased(SelectExitEventArgs args)
        {
            // Passage d'une main à l'autre : l'objet est encore tenu.
            if (grab != null && grab.isSelected) return;
            ReleasedAt = Time.time;
            ReleasePosition = Body != null ? Body.worldCenterOfMass : transform.position;
            if (ScoreManager.Instance != null) ScoreManager.Instance.NotifyReleased(this);
        }

        /// <summary>Points de base de cet objet d'après le barème.</summary>
        public int Points(ScoreTable table)
        {
            if (pointsOverride > 0) return pointsOverride;
            return table != null ? table.PointsFor(TableId, Material) : 0;
        }

        /// <summary>Nom affiché dans le HUD.</summary>
        public string Label(ScoreTable table)
        {
            if (!string.IsNullOrEmpty(labelOverride)) return labelOverride;
            return table != null ? table.LabelFor(TableId, Material) : TableId;
        }

        /// <summary>Nom d'objet sans « (Clone) » ni numéro de copie : « plate (3) » et « Bottle(Clone) » donnent « plate » et « Bottle ».</summary>
        public static string CleanName(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return "";
            string s = objectName.Replace("(Clone)", "").Trim();

            // Numéro de copie ajouté par Unity : « nom (12) ».
            if (s.EndsWith(")"))
            {
                int open = s.LastIndexOf('(');
                if (open > 0)
                {
                    bool digits = open < s.Length - 2;
                    for (int i = open + 1; i < s.Length - 1 && digits; i++) digits = char.IsDigit(s[i]);
                    if (digits) s = s.Substring(0, open).Trim();
                }
            }
            return s;
        }
    }
}
