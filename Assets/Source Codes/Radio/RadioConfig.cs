using UnityEngine;
using UnityEngine.UIElements;

namespace RageRoom
{
    /// <summary>
    /// Réglages de la radio et de son annonce dans le HUD. L'asset doit s'appeler « RadioConfig » et être dans un
    /// dossier Resources ; il est créé et rempli par « Rage Room > Radio > Installer ou mettre à jour la radio ».
    /// Les radios elles-mêmes sont des assets RadioStation, dans « Resources/Radios ».
    /// </summary>
    public class RadioConfig : ScriptableObject
    {
        public const string ResourceName = "RadioConfig";
        /// <summary>Sous-dossier de Resources où le jeu cherche les radios.</summary>
        public const string StationsFolder = "Radios";

        [Header("Lecture")]
        [Tooltip("La radio démarre toute seule au lancement du jeu (sur la dernière radio choisie par le joueur).")]
        public bool playOnStart = true;
        [Tooltip("Volume de la musique tant que le joueur ne l'a pas réglé dans le menu.")]
        [Range(0f, 1f)] public float defaultVolume = 0.5f;
        [Tooltip("Écrit les titres, artistes et albums en majuscules, comme le reste de l'interface.")]
        public bool uppercase = true;

        [Header("HUD : annonce du morceau")]
        [Tooltip("Affiche l'annonce « À l'écoute » au début de chaque morceau.")]
        public bool showHud = true;
        [Tooltip("RadioHud.uxml. Rempli par le menu d'installation.")]
        public VisualTreeAsset hudUxml;
        [Tooltip("Panel Settings de l'annonce (World Space, 1000 pixels par unité, sans collider). Créé par le menu d'installation.")]
        public PanelSettings hudPanelSettings;
        [Tooltip("Police de l'annonce (optionnel). Le menu d'installation y met celle du shop si elle est dans le projet.")]
        public Font hudFont;
        [Tooltip("Durée d'affichage de l'annonce, en secondes.")]
        [Min(1f)] public float hudDuration = 6f;
        [Tooltip("Accroche l'annonce sous le HUD de score quand il existe : elle bouge avec lui. "
               + "Décoché, ou sans HUD de score dans la scène : elle suit le regard toute seule, avec les réglages ci-dessous.")]
        public bool hudUnderScore = true;
        [Tooltip("Écart entre le HUD de score et l'annonce, en pixels du HUD (1 pixel = 1 mm à l'échelle 1).")]
        [Range(0f, 80f)] public float hudGap = 14f;

        [Header("HUD : suivi du regard (sans HUD de score)")]
        [Tooltip("Distance (mètres) entre la tête et l'annonce.")]
        [Range(0.5f, 3f)] public float hudDistance = 1.1f;
        [Tooltip("Angle (degrés) au-dessus du centre du regard. Négatif = en dessous.")]
        [Range(-40f, 40f)] public float hudAngleUp = 12f;
        [Tooltip("Angle (degrés) vers la gauche du regard. Négatif = à droite.")]
        [Range(-40f, 40f)] public float hudAngleLeft = 14f;
        [Tooltip("Taille de l'annonce. À 1, elle mesure 52 cm de large.")]
        [Range(0.2f, 2f)] public float hudScale = 0.7f;
        [Tooltip("L'annonce ne bouge pas tant que le regard reste dans ce cône (degrés).")]
        [Range(5f, 60f)] public float hudDeadZone = 22f;
        [Tooltip("Vitesse à laquelle l'annonce rattrape le regard.")]
        [Range(1f, 20f)] public float hudFollowSpeed = 5f;
    }
}
