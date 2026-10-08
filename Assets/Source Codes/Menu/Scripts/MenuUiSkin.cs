using UnityEngine;

namespace RageRoom
{
    /// <summary>
    /// Sprites utilisés par les menus créés en jeu (coins arrondis, slider).
    /// L'asset doit s'appeler « MenuUiSkin » et être dans un dossier Resources.
    /// </summary>
    [CreateAssetMenu(menuName = "Rage Room/Skin des menus", fileName = "MenuUiSkin")]
    public class MenuUiSkin : ScriptableObject
    {
        public const string ResourceName = "MenuUiSkin";

        public Sprite standard;
        public Sprite background;
        public Sprite knob;
    }
}
