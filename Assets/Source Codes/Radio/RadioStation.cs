using System;
using System.Collections.Generic;
using UnityEngine;

namespace RageRoom
{
    /// <summary>Un morceau d'une radio : le son, et ce que le HUD et le menu affichent.</summary>
    [Serializable]
    public class RadioTrack
    {
        [Tooltip("Le fichier audio. Pour un morceau long, règle son import sur Load Type « Streaming » "
               + "(fait tout seul par « Rage Room > Radio > Ajouter un dossier de musiques »).")]
        public AudioClip clip;
        [Tooltip("Nom du morceau.")]
        public string title = "";
        [Tooltip("Nom de l'artiste.")]
        public string artist = "";
        [Tooltip("Nom de l'album.")]
        public string album = "";
        [Tooltip("Pochette de l'album (image carrée). Vide : la pochette par défaut de la radio.")]
        public Texture2D cover;
        [Tooltip("Volume propre à ce morceau, pour rattraper un fichier plus fort ou plus faible que les autres.")]
        [Range(0f, 1f)] public float volume = 1f;
    }

    /// <summary>
    /// Une radio = un asset : son nom et la liste de ses morceaux.
    /// Pour ajouter une radio au jeu, crée un asset de ce type (« Rage Room > Radio > Créer une radio »)
    /// dans un dossier « Resources/Radios » : le jeu charge tout ce dossier au lancement, rien d'autre à brancher.
    /// </summary>
    [CreateAssetMenu(menuName = "Rage Room/Radio", fileName = "Radio")]
    public class RadioStation : ScriptableObject
    {
        [Tooltip("Nom affiché dans le menu et le HUD.")]
        public string stationName = "Radio";
        [Tooltip("Place de la radio dans la liste du menu (les plus petits numéros d'abord).")]
        public int order;
        [Tooltip("Morceaux dans le désordre. Décoché : dans l'ordre de la liste.")]
        public bool shuffle = true;
        [Tooltip("Pochette affichée pour les morceaux qui n'en ont pas.")]
        public Texture2D defaultCover;
        public List<RadioTrack> tracks = new List<RadioTrack>();

        /// <summary>Pochette à afficher pour un morceau de cette radio.</summary>
        public Texture2D CoverOf(RadioTrack track)
        {
            return track != null && track.cover != null ? track.cover : defaultCover;
        }
    }
}
