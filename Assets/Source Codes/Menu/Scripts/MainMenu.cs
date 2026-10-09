using UnityEngine;
using UnityEngine.UIElements;

namespace RageRoom
{
    /// <summary>
    /// Menu principal (MainMenu.uxml) : Jouer (charge la scène du jeu), Paramètres (volumes, manettes / mains),
    /// Radio (choix de la radio, morceau, volume de la musique), Quitter.
    /// À placer sur le même objet que le UI Document du menu ; la scène est créée par
    /// « Rage Room > Menu > Recréer la scène du menu ».
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class MainMenu : MonoBehaviour
    {
        [Tooltip("Nom de la scène du jeu (doit être dans les Build Settings).")]
        [SerializeField] string gameScene = "SampleScene";

        VisualElement mainPanel, settingsPanel, radioPanel;
        Button playButton, settingsButton, radioButton, quitButton;
        bool loading;

        // Le UI Document reconstruit ses éléments à chaque activation : on rebranche tout ici.
        // Il s'active avant ce script, son arbre visuel est donc déjà prêt.
        void OnEnable()
        {
            var document = GetComponent<UIDocument>();
            if (document == null)
            {
                Debug.LogError("[Rage Room] Le menu principal n'a pas de UI Document : la scène date de l'ancien menu (canvas). "
                             + "Relance « Rage Room > Menu > Recréer la scène du menu ».", this);
                return;
            }

            // Filet de sécurité si le UI Document de la scène a perdu ses références
            var skin = MenuUi.Skin;
            if (skin != null)
            {
                if (document.panelSettings == null && skin.panelSettings != null) document.panelSettings = skin.panelSettings;
                if (document.visualTreeAsset == null && skin.mainMenu != null) document.visualTreeAsset = skin.mainMenu;
            }

            var root = document.rootVisualElement;
            if (root == null || document.panelSettings == null || document.visualTreeAsset == null)
            {
                Debug.LogError("[Rage Room] Le UI Document du menu principal n'a pas son Panel Settings ou son UXML (MainMenu.uxml). "
                             + "Lance « Rage Room > Menu > Relier les UXML des menus ».", this);
                return;
            }

            MenuUi.EnsureInteraction();

            mainPanel = MenuUi.Find<VisualElement>(root, "main-panel");
            settingsPanel = MenuUi.Find<VisualElement>(root, "settings-panel");
            radioPanel = MenuUi.Find<VisualElement>(root, "radio-panel");
            playButton = MenuUi.Find<Button>(root, "play-button");
            settingsButton = MenuUi.Find<Button>(root, "settings-button");
            radioButton = MenuUi.Find<Button>(root, "radio-button");
            quitButton = MenuUi.Find<Button>(root, "quit-button");

            MenuUi.OnClick(playButton, Play);
            MenuUi.OnClick(settingsButton, () => Show(settingsPanel));
            MenuUi.OnClick(radioButton, () => Show(radioPanel));
            MenuUi.OnClick(quitButton, Quit);
            MenuUi.OnClick(MenuUi.Find<Button>(root, "back-button"), CloseSettings);
            MenuUi.OnClick(MenuUi.Find<Button>(root, "radio-back-button"), () => Show(mainPanel));

            // Volumes, manettes / mains : branchés et remis aux valeurs enregistrées
            new MenuUi.SettingsBlock(root);
            // Radio : branchée sur le lecteur, se met à jour toute seule quand le morceau change
            new RadioMenuBlock(root);

            loading = false;
            Show(mainPanel);
        }

        void Play()
        {
            if (loading) return;
            loading = true;
            GameSettings.Save();

            // Chargement en arrière-plan : le casque continue d'afficher le menu au lieu de figer l'image
            if (playButton != null) playButton.text = "CHARGEMENT...";
            foreach (var button in new[] { playButton, settingsButton, radioButton, quitButton })
                if (button != null) button.SetEnabled(false);
            SceneSetupRunner.LoadScene(gameScene);
        }

        void CloseSettings()
        {
            GameSettings.Save();
            Show(mainPanel);
        }

        /// <summary>Affiche un seul des trois panneaux (principal, paramètres, radio).</summary>
        void Show(VisualElement panel)
        {
            if (panel == null) panel = mainPanel;
            MenuUi.Show(mainPanel, panel == mainPanel);
            MenuUi.Show(settingsPanel, panel == settingsPanel);
            MenuUi.Show(radioPanel, panel == radioPanel);
        }

        void Quit()
        {
            GameSettings.Save();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
