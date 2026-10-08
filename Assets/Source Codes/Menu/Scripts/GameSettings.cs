using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RageRoom
{
    public enum ControllerVisualMode { Manettes, Mains }

    /// <summary>
    /// Réglages du joueur, sauvegardés (PlayerPrefs) et appliqués automatiquement dans toutes les scènes :
    /// volume général, volume des effets, et affichage des manettes ou de mains virtuelles.
    /// </summary>
    public static class GameSettings
    {
        const string MasterKey = "Settings.MasterVolume";
        const string FxKey = "Settings.FxVolume";
        const string VisualKey = "Settings.ControllerVisual";

        /// <summary>Appelé à chaque changement de réglage.</summary>
        public static event Action Changed;

        /// <summary>Volume de tout le jeu (0 à 1).</summary>
        public static float MasterVolume
        {
            get => PlayerPrefs.GetFloat(MasterKey, 1f);
            set
            {
                PlayerPrefs.SetFloat(MasterKey, Mathf.Clamp01(value));
                AudioListener.volume = MasterVolume;
                Changed?.Invoke();
            }
        }

        /// <summary>Volume des effets sonores (casse, impacts…), multiplié par le volume général (0 à 1).</summary>
        public static float FxVolume
        {
            get => PlayerPrefs.GetFloat(FxKey, 1f);
            set
            {
                PlayerPrefs.SetFloat(FxKey, Mathf.Clamp01(value));
                Changed?.Invoke();
            }
        }

        /// <summary>Ce que le joueur voit dans ses mains quand il tient les manettes.</summary>
        public static ControllerVisualMode ControllerVisual
        {
            get => (ControllerVisualMode)PlayerPrefs.GetInt(VisualKey, (int)ControllerVisualMode.Manettes);
            set
            {
                PlayerPrefs.SetInt(VisualKey, (int)value);
                ControllerHandVisual.ApplyToScene(value == ControllerVisualMode.Mains);
                Changed?.Invoke();
            }
        }

        /// <summary>Écrit les réglages sur le disque (à appeler en quittant l'écran des paramètres).</summary>
        public static void Save() => PlayerPrefs.Save();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            // Utile si le rechargement de domaine est désactivé dans l'éditeur
            Changed = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            ApplyAll();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => ApplyAll();

        static void ApplyAll()
        {
            AudioListener.volume = MasterVolume;
            ControllerHandVisual.ApplyToScene(ControllerVisual == ControllerVisualMode.Mains);
            // Menu en jeu, réparations, et ré-application une fois l'ancienne scène détruite
            SceneSetupRunner.RunNextFrame();
        }
    }
}
