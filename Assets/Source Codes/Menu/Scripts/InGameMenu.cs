using TMPro;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace RageRoom
{
    /// <summary>
    /// Menu de paramètres en jeu : s'ouvre/se ferme avec le bouton Menu (≡) de la manette gauche
    /// (Échap au clavier dans l'éditeur). Volumes, manettes / mains, Reprendre, Menu principal.
    /// Créé automatiquement dans chaque scène de jeu par GameSettings : rien à placer dans les scènes.
    /// </summary>
    public class InGameMenu : MonoBehaviour
    {
        const string MainMenuScene = "SC_Menu";

        InputAction toggleAction;
        Canvas canvas;
        Slider master, fx;
        TMP_Text masterValue, fxValue;
        Button controllersButton, handsButton;
        Slider turnSpeed;
        TMP_Text turnSpeedValue;
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
            canvas.gameObject.SetActive(false);

            toggleAction = new InputAction("Menu en jeu", InputActionType.Button);
            toggleAction.AddBinding("<XRController>{LeftHand}/menu");
            toggleAction.AddBinding("<Keyboard>/escape");
            toggleAction.Enable();
        }

        void OnDestroy() => toggleAction?.Dispose();

        void Update()
        {
            if (toggleAction.WasPressedThisFrame())
            {
                if (canvas.gameObject.activeSelf) Close();
                else Open();
            }
        }

        void Build()
        {
            canvas = MenuUi.MakeCanvas("Menu en jeu", 0.001f, new Vector2(560f, 780f));
            canvas.transform.SetParent(transform, false);
            var panel = MenuUi.MakePanel(canvas.transform, "Paramètres");

            MenuUi.MakeText(panel, "PARAMÈTRES", 38, FontStyles.Bold, TextAlignmentOptions.Center, 52, Color.white);
            master = MenuUi.MakeSliderRow(panel, "Volume général", out masterValue);
            fx = MenuUi.MakeSliderRow(panel, "Volume des effets", out fxValue);
            MenuUi.MakeText(panel, "Dans les mains", 24, FontStyles.Normal, TextAlignmentOptions.Left, 34, new Color(1f, 1f, 1f, 0.8f));
            var choice = MenuUi.MakeRow(panel, "Choix manettes / mains", 60);
            controllersButton = MenuUi.MakeButton(choice, "Manettes", MenuUi.Red, 28, 60);
            handsButton = MenuUi.MakeButton(choice, "Mains", MenuUi.Grey, 28, 60);

            turnSpeed = MenuUi.MakeSliderRow(panel, "Vitesse de rotation", out turnSpeedValue);
            turnSpeed.minValue = GameSettings.MinTurnSpeed;
            turnSpeed.maxValue = GameSettings.MaxTurnSpeed;
            turnSpeed.wholeNumbers = true;
            var actions = MenuUi.MakeRow(panel, "Actions", 64);
            var resume = MenuUi.MakeButton(actions, "Reprendre", MenuUi.Red, 28, 64);
            var mainMenu = MenuUi.MakeButton(actions, "Menu principal", MenuUi.Grey, 26, 64);

            master.onValueChanged.AddListener(v => { GameSettings.MasterVolume = v; RefreshLabels(); });
            fx.onValueChanged.AddListener(v => { GameSettings.FxVolume = v; RefreshLabels(); });
            controllersButton.onClick.AddListener(() => SetVisual(ControllerVisualMode.Manettes));
            handsButton.onClick.AddListener(() => SetVisual(ControllerVisualMode.Mains));
            turnSpeed.onValueChanged.AddListener(v => { GameSettings.TurnSpeed = v; RefreshLabels(); });
            resume.onClick.AddListener(Close);
            mainMenu.onClick.AddListener(BackToMainMenu);
        }

        void Open()
        {
            var cam = Camera.main;
            if (cam == null) return;
            canvas.worldCamera = cam;

            // Devant le joueur, à hauteur de poitrine, face à lui
            var head = cam.transform;
            var forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.ProjectOnPlane(head.up, Vector3.up);
            forward.Normalize();
            var pos = head.position + forward * 0.9f + Vector3.down * 0.15f;
            canvas.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - head.position, Vector3.up));

            master.SetValueWithoutNotify(GameSettings.MasterVolume);
            fx.SetValueWithoutNotify(GameSettings.FxVolume);
            turnSpeed.SetValueWithoutNotify(GameSettings.TurnSpeed);
            RefreshLabels();
            RefreshVisualChoice();
            canvas.gameObject.SetActive(true);
        }

        void Close()
        {
            GameSettings.Save();
            canvas.gameObject.SetActive(false);
        }

        void BackToMainMenu()
        {
            if (leaving) return;
            leaving = true;
            GameSettings.Save();
            SceneSetupRunner.LoadScene(MainMenuScene);
        }

        void SetVisual(ControllerVisualMode mode)
        {
            GameSettings.ControllerVisual = mode;
            RefreshVisualChoice();
        }

        void RefreshLabels()
        {
            masterValue.text = Mathf.RoundToInt(master.value * 100f) + " %";
            fxValue.text = Mathf.RoundToInt(fx.value * 100f) + " %";
            turnSpeedValue.text = Mathf.RoundToInt(turnSpeed.value) + " °/s";
        }

        void RefreshVisualChoice()
        {
            bool hands = GameSettings.ControllerVisual == ControllerVisualMode.Mains;
            MenuUi.Tint(controllersButton, hands ? MenuUi.Grey : MenuUi.Red);
            MenuUi.Tint(handsButton, hands ? MenuUi.Red : MenuUi.Grey);
        }
    }
}
