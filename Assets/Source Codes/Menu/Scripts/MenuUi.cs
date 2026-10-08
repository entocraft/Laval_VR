using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace RageRoom
{
    /// <summary>
    /// Outils communs aux menus en UI Toolkit (menu principal et menu en jeu) :
    /// création du panneau dans le monde, interaction avec les manettes, et branchement du bloc
    /// de réglages (Settings.uxml) partagé par les deux menus.
    /// </summary>
    public static class MenuUi
    {
        /// <summary>Pixels par mètre du Panel Settings des menus : 1 pixel = 1 mm, comme les anciens canvas.</summary>
        public const float PixelsPerUnit = 1000f;

        /// <summary>Classe USS qui masque un panneau.</summary>
        public const string HiddenClass = "panel--hidden";

        static MenuUiSkin skin;

        /// <summary>UXML et Panel Settings des menus (asset « MenuUiSkin » d'un dossier Resources).</summary>
        public static MenuUiSkin Skin => skin != null ? skin : skin = Resources.Load<MenuUiSkin>(MenuUiSkin.ResourceName);

        /// <summary>
        /// Crée un panneau UI Toolkit dans le monde. <paramref name="size"/> est en pixels ;
        /// avec le Panel Settings des menus, 1 pixel = 1 mm multiplié par <paramref name="scale"/>.
        /// Le point de pivot est le centre du panneau, et le texte se lit en regardant dans le sens
        /// de l'axe Z de l'objet (comme un canvas).
        /// </summary>
        public static UIDocument MakeDocument(string name, Transform parent, PanelSettings panelSettings,
                                              VisualTreeAsset uxml, Vector2 size, float scale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * scale;

            var document = go.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            document.visualTreeAsset = uxml;
            document.worldSpaceSizeMode = UIDocument.WorldSpaceSizeMode.Fixed;
            document.worldSpaceSize = size;
            // Pivot calculé sur cette taille fixe : le panneau ne bouge pas quand un bouton grossit au survol
            document.pivotReferenceSize = PivotReferenceSize.Layout;
            return document;
        }

        /// <summary>
        /// Les rayons des manettes n'agissent sur UI Toolkit que si la scène contient un XR UI Toolkit Manager
        /// et un Panel Input Configuration réglé sur « No input redirection ». On les ajoute s'ils manquent,
        /// sans toucher à ceux déjà présents dans la scène.
        /// </summary>
        public static void EnsureInteraction()
        {
            if (UnityEngine.Object.FindFirstObjectByType<UnityEngine.XR.Interaction.Toolkit.UI.XRUIToolkitManager>() == null)
                new GameObject("XR UI Toolkit Manager (auto)").AddComponent<UnityEngine.XR.Interaction.Toolkit.UI.XRUIToolkitManager>();

            if (UnityEngine.Object.FindFirstObjectByType<PanelInputConfiguration>() == null)
            {
                var config = new GameObject("Panel Input Configuration (auto)").AddComponent<PanelInputConfiguration>();
                config.panelInputRedirection = PanelInputConfiguration.PanelInputRedirection.Never;
            }
        }

        /// <summary>Cherche un élément du UXML par son nom et signale clairement s'il manque.</summary>
        public static T Find<T>(VisualElement root, string elementName) where T : VisualElement
        {
            T element = root != null ? root.Q<T>(elementName) : null;
            if (element == null)
                Debug.LogError($"[Rage Room] Élément introuvable dans le UXML du menu : il faut un {typeof(T).Name} nommé \"{elementName}\". "
                             + "Vérifie son type et son champ Name dans UI Builder.");
            return element;
        }

        /// <summary>Branche une action sur un bouton, sans erreur si le bouton manque dans le UXML.</summary>
        public static void OnClick(Button button, Action action)
        {
            if (button != null) button.clicked += action;
        }

        /// <summary>Affiche ou masque un panneau (classe « panel--hidden » de Menu.uss).</summary>
        public static void Show(VisualElement panel, bool visible)
        {
            if (panel != null) panel.EnableInClassList(HiddenClass, !visible);
        }

        /// <summary>
        /// Bloc de réglages de Settings.uxml : volumes, manettes / mains, vitesse de rotation.
        /// À recréer chaque fois que le panneau est (ré)activé : un UI Document reconstruit ses éléments à chaque activation.
        /// </summary>
        public sealed class SettingsBlock
        {
            const string SelectedClass = "choice__button--selected";

            readonly Slider master, fx;
            readonly SliderInt turnSpeed;
            readonly Label masterValue, fxValue, turnSpeedValue;
            readonly Button controllersButton, handsButton;

            public SettingsBlock(VisualElement root)
            {
                master = Find<Slider>(root, "master-slider");
                masterValue = Find<Label>(root, "master-value");
                fx = Find<Slider>(root, "fx-slider");
                fxValue = Find<Label>(root, "fx-value");
                controllersButton = Find<Button>(root, "controllers-button");
                handsButton = Find<Button>(root, "hands-button");
                turnSpeed = Find<SliderInt>(root, "turn-speed-slider");
                turnSpeedValue = Find<Label>(root, "turn-speed-value");

                if (master != null)
                {
                    master.lowValue = 0f;
                    master.highValue = 1f;
                    master.RegisterValueChangedCallback(evt => { GameSettings.MasterVolume = evt.newValue; RefreshLabels(); });
                }
                if (fx != null)
                {
                    fx.lowValue = 0f;
                    fx.highValue = 1f;
                    fx.RegisterValueChangedCallback(evt => { GameSettings.FxVolume = evt.newValue; RefreshLabels(); });
                }
                if (turnSpeed != null)
                {
                    turnSpeed.lowValue = Mathf.RoundToInt(GameSettings.MinTurnSpeed);
                    turnSpeed.highValue = Mathf.RoundToInt(GameSettings.MaxTurnSpeed);
                    turnSpeed.RegisterValueChangedCallback(evt => { GameSettings.TurnSpeed = evt.newValue; RefreshLabels(); });
                }
                OnClick(controllersButton, () => SetVisual(ControllerVisualMode.Manettes));
                OnClick(handsButton, () => SetVisual(ControllerVisualMode.Mains));

                Refresh();
            }

            /// <summary>Recopie les réglages enregistrés dans les sliders, les textes et le choix manettes / mains.</summary>
            public void Refresh()
            {
                if (master != null) master.SetValueWithoutNotify(GameSettings.MasterVolume);
                if (fx != null) fx.SetValueWithoutNotify(GameSettings.FxVolume);
                if (turnSpeed != null) turnSpeed.SetValueWithoutNotify(Mathf.RoundToInt(GameSettings.TurnSpeed));
                RefreshLabels();
                RefreshVisualChoice();
            }

            void SetVisual(ControllerVisualMode mode)
            {
                GameSettings.ControllerVisual = mode;
                RefreshVisualChoice();
            }

            void RefreshLabels()
            {
                if (masterValue != null && master != null) masterValue.text = Mathf.RoundToInt(master.value * 100f) + " %";
                if (fxValue != null && fx != null) fxValue.text = Mathf.RoundToInt(fx.value * 100f) + " %";
                if (turnSpeedValue != null && turnSpeed != null) turnSpeedValue.text = turnSpeed.value + " °/s";
            }

            void RefreshVisualChoice()
            {
                bool hands = GameSettings.ControllerVisual == ControllerVisualMode.Mains;
                if (controllersButton != null) controllersButton.EnableInClassList(SelectedClass, !hands);
                if (handsButton != null) handsButton.EnableInClassList(SelectedClass, hands);
            }
        }
    }
}
