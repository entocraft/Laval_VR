using UnityEngine;
using UnityEngine.UIElements;

namespace RageRoom
{
    /// <summary>
    /// Ce dont les menus (UI Toolkit) ont besoin pour s'afficher : les deux UXML et le Panel Settings.
    /// L'asset doit s'appeler « MenuUiSkin » et être dans un dossier Resources : le menu en jeu est créé
    /// par code dans chaque scène et le charge par son nom.
    /// Les champs sont remplis tout seuls par « Rage Room > Menu > Relier les UXML des menus ».
    /// </summary>
    [CreateAssetMenu(menuName = "Rage Room/Skin des menus", fileName = "MenuUiSkin")]
    public class MenuUiSkin : ScriptableObject
    {
        public const string ResourceName = "MenuUiSkin";

        [Tooltip("Panel Settings des menus : Render Mode « World Space », Pixels Per Unit à 1000 (1 pixel = 1 mm), "
               + "Collider Update Mode « Match 2-D document rect ». Créé automatiquement (MenuPanelSettings).")]
        public PanelSettings panelSettings;

        [Tooltip("MainMenu.uxml")]
        public VisualTreeAsset mainMenu;

        [Tooltip("InGameMenu.uxml")]
        public VisualTreeAsset inGameMenu;
    }
}
