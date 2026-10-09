using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace RageRoom
{
    /// <summary>
    /// Menu de paramètres en jeu (InGameMenu.uxml) : s'ouvre/se ferme avec le bouton Menu (≡) de la manette gauche
    /// (Échap au clavier dans l'éditeur). Volumes, manettes / mains, vitesse de rotation, Radio, Reprendre, Menu principal.
    /// Créé automatiquement dans chaque scène de jeu par GameSettings : rien à placer dans les scènes.
    /// </summary>
    public class InGameMenu : MonoBehaviour
    {
        const string MainMenuScene = "SC_Menu";

        // 560 x 820 pixels, 1 pixel = 1 mm. La hauteur doit rester celle de « .menu--ingame » dans Radio.uss.
        static readonly Vector2 Size = new Vector2(560f, 820f);
        const float Scale = 1f;

        InputAction toggleAction;
        UIDocument document;
        VisualElement settingsPanel, radioPanel;
        bool leaving;

        /// <summary>Ajoute le menu en jeu si la scène a un joueur VR et n'est pas le menu principal.</summary>
        public static void EnsureInScene()
        {
            if (FindFirstObjectByType<MainMenu>(FindObjectsInactive.Include) != null) return;
            if (FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include) == null) return;
            if (FindFirstObjectByType<InGameMenu>(FindObjectsInactive.Include) != null) return;
            new GameObject("Menu en jeu (auto)").AddComponent<InGameMenu>();
        }

        void Awake()
        {
            Build();

            toggleAction = new InputAction("Menu en jeu", InputActionType.Button);
            toggleAction.AddBinding("<XRController>{LeftHand}/menu");
            toggleAction.AddBinding("<Keyboard>/escape");
            toggleAction.Enable();
        }

        void OnDestroy() => toggleAction?.Dispose();

        void Update()
        {
            if (document == null || !toggleAction.WasPressedThisFrame()) return;

            if (document.gameObject.activeSelf) Close();
            else Open();
        }

        void Build()
        {
            var skin = MenuUi.Skin;
            if (skin == null || skin.panelSettings == null || skin.inGameMenu == null)
            {
                Debug.LogError("[Rage Room] Menu en jeu indisponible : l'asset « MenuUiSkin » (dossier Resources) n'a pas son Panel Settings "
                             + "ou son UXML (InGameMenu.uxml). Lance « Rage Room > Menu > Relier les UXML des menus ».");
                return;
            }

            document = MenuUi.MakeDocument("Menu en jeu", transform, skin.panelSettings, skin.inGameMenu, Size, Scale);
            document.gameObject.SetActive(false);
        }

        void Open()
        {
            var cam = Camera.main;
            if (cam == null) return;

            MenuUi.EnsureInteraction();

            // Devant le joueur, à hauteur de poitrine, face à lui
            var head = cam.transform;
            var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.ProjectOnPlane(head.up, Vector3.up);
            forward.Normalize();
            var pos = head.position + forward * 0.9f + Vector3.down * 0.15f;
            document.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - head.position, Vector3.up));

            // Le UI Document reconstruit ses éléments à chaque activation : on rebranche tout juste après
            document.gameObject.SetActive(true);
            Bind(document.rootVisualElement);
        }

        void Bind(VisualElement root)
        {
            settingsPanel = MenuUi.Find<VisualElement>(root, "settings-panel");
            radioPanel = MenuUi.Find<VisualElement>(root, "radio-panel");

            // Volumes, manettes / mains, vitesse de rotation : branchés et remis aux valeurs enregistrées
            new MenuUi.SettingsBlock(root);
            // Radio : branchée sur le lecteur, se met à jour toute seule quand le morceau change
            new RadioMenuBlock(root);

            MenuUi.OnClick(MenuUi.Find<Button>(root, "radio-button"), () => ShowRadio(true));
            MenuUi.OnClick(MenuUi.Find<Button>(root, "radio-back-button"), () => ShowRadio(false));
            MenuUi.OnClick(MenuUi.Find<Button>(root, "resume-button"), Close);
            MenuUi.OnClick(MenuUi.Find<Button>(root, "main-menu-button"), BackToMainMenu);

            // Le menu s'ouvre toujours sur les paramètres
            ShowRadio(false);
        }

        void ShowRadio(bool radio)
        {
            MenuUi.Show(settingsPanel, !radio);
            MenuUi.Show(radioPanel, radio);
        }

        void Close()
        {
            GameSettings.Save();
            document.gameObject.SetActive(false);
        }

        void BackToMainMenu()
        {
            if (leaving) return;
            leaving = true;
            GameSettings.Save();
            SceneSetupRunner.LoadScene(MainMenuScene);
        }
    }
}
