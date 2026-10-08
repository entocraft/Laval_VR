using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace RageRoom
{
    /// <summary>
    /// Menu principal : Jouer (charge la scène du jeu), Paramètres (volumes, manettes / mains), Quitter.
    /// Les références sont remplies par le menu « Rage Room > Menu > Recréer la scène du menu ».
    /// </summary>
    public class MainMenu : MonoBehaviour
    {
        [Tooltip("Nom de la scène du jeu (doit être dans les Build Settings).")]
        [SerializeField] string gameScene = "SampleScene";

        [Header("Panneaux")]
        [SerializeField] GameObject mainPanel;
        [SerializeField] GameObject settingsPanel;

        [Header("Menu principal")]
        [SerializeField] Button playButton;
        [SerializeField] Button settingsButton;
        [SerializeField] Button quitButton;

        [Header("Paramètres")]
        [SerializeField] Slider masterSlider;
        [SerializeField] TMP_Text masterValue;
        [SerializeField] Slider fxSlider;
        [SerializeField] TMP_Text fxValue;
        [SerializeField] Button controllersButton;
        [SerializeField] Button handsButton;
        [SerializeField] Button backButton;

        [Header("Couleurs du choix manettes / mains")]
        [SerializeField] Color selectedColor = new Color(0.86f, 0.16f, 0.16f);
        [SerializeField] Color unselectedColor = new Color(0.25f, 0.25f, 0.28f);

        bool loading;

        void Awake()
        {
            playButton.onClick.AddListener(Play);
            settingsButton.onClick.AddListener(OpenSettings);
            quitButton.onClick.AddListener(Quit);
            backButton.onClick.AddListener(CloseSettings);

            masterSlider.SetValueWithoutNotify(GameSettings.MasterVolume);
            fxSlider.SetValueWithoutNotify(GameSettings.FxVolume);
            masterSlider.onValueChanged.AddListener(v => { GameSettings.MasterVolume = v; RefreshLabels(); });
            fxSlider.onValueChanged.AddListener(v => { GameSettings.FxVolume = v; RefreshLabels(); });

            controllersButton.onClick.AddListener(() => SetVisual(ControllerVisualMode.Manettes));
            handsButton.onClick.AddListener(() => SetVisual(ControllerVisualMode.Mains));

            RefreshLabels();
            RefreshVisualChoice();
            CloseSettings();
        }

        void Play()
        {
            if (loading) return;
            loading = true;
            GameSettings.Save();

            // Chargement en arrière-plan : le casque continue d'afficher le menu au lieu de figer l'image
            var label = playButton.GetComponentInChildren<TMP_Text>();
            if (label != null) label.text = "CHARGEMENT...";
            playButton.interactable = settingsButton.interactable = quitButton.interactable = false;
            SceneSetupRunner.LoadScene(gameScene);
        }

        void OpenSettings()
        {
            mainPanel.SetActive(false);
            settingsPanel.SetActive(true);
        }

        void CloseSettings()
        {
            GameSettings.Save();
            settingsPanel.SetActive(false);
            mainPanel.SetActive(true);
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

        void SetVisual(ControllerVisualMode mode)
        {
            GameSettings.ControllerVisual = mode;
            RefreshVisualChoice();
        }

        void RefreshLabels()
        {
            masterValue.text = Mathf.RoundToInt(masterSlider.value * 100f) + " %";
            fxValue.text = Mathf.RoundToInt(fxSlider.value * 100f) + " %";
        }

        void RefreshVisualChoice()
        {
            bool hands = GameSettings.ControllerVisual == ControllerVisualMode.Mains;
            Tint(controllersButton, hands ? unselectedColor : selectedColor);
            Tint(handsButton, hands ? selectedColor : unselectedColor);
        }

        static void Tint(Button b, Color c)
        {
            var colors = b.colors;
            colors.normalColor = c;
            colors.selectedColor = c;
            colors.highlightedColor = Color.Lerp(c, Color.white, 0.25f);
            colors.pressedColor = Color.Lerp(c, Color.black, 0.3f);
            b.colors = colors;
        }
    }
}
