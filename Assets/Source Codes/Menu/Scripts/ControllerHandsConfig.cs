using UnityEngine;

namespace RageRoom
{
    /// <summary>
    /// Réglages des mains virtuelles affichées à la place des manettes.
    /// L'asset doit s'appeler « ControllerHandsConfig » et être dans un dossier Resources.
    /// Les positions sont celles du poignet, dans le repère de la manette (Left/Right Controller).
    /// </summary>
    [CreateAssetMenu(menuName = "Rage Room/Config mains virtuelles", fileName = "ControllerHandsConfig")]
    public class ControllerHandsConfig : ScriptableObject
    {
        public const string ResourceName = "ControllerHandsConfig";

        [Header("Modèles")]
        public GameObject leftHandModel;
        public GameObject rightHandModel;
        public Material handMaterial;

        [Header("Placement du poignet (repère de la manette)")]
        public Vector3 leftWristPosition = new Vector3(-0.03f, -0.04f, -0.1f);
        public Vector3 leftWristRotation = new Vector3(20f, 0f, 90f);
        public Vector3 rightWristPosition = new Vector3(0.03f, -0.04f, -0.1f);
        public Vector3 rightWristRotation = new Vector3(20f, 0f, -90f);

        [Header("Doigts")]
        [Tooltip("Pliage au repos, manette tenue sans appuyer (0 = main ouverte, 1 = poing fermé).")]
        [Range(0f, 1f)] public float restCurl = 0.35f;
        [Tooltip("Axe local de pliage des phalanges. Si les doigts se plient sur le côté, essaie (0,1,0) ou (0,0,1).")]
        public Vector3 curlAxis = Vector3.right;
        [Tooltip("Angles max (degrés) : phalange proximale, intermédiaire, distale.")]
        public Vector3 fingerAngles = new Vector3(65f, 85f, 55f);
        [Tooltip("Angles max (degrés) du pouce : métacarpe, proximale, distale.")]
        public Vector3 thumbAngles = new Vector3(10f, 25f, 35f);
        [Tooltip("Vitesse de suivi des doigts.")]
        public float fingerSpeed = 20f;
    }
}
