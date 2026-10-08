using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Attachment;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;
using UnityEngine.XR.Interaction.Toolkit.Samples.StarterAssets;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace RageRoom
{
    /// <summary>
    /// Objet invisible qui survit aux changements de scène et exécute les réglages
    /// une frame après chaque chargement (quand l'ancienne scène est bien détruite).
    /// </summary>
    public class SceneSetupRunner : MonoBehaviour
    {
        static SceneSetupRunner instance;
        static Camera bridgeCamera;

        static SceneSetupRunner Instance
        {
            get
            {
                if (instance == null)
                {
                    var go = new GameObject("Réglages du jeu (auto)") { hideFlags = HideFlags.HideInHierarchy };
                    DontDestroyOnLoad(go);
                    instance = go.AddComponent<SceneSetupRunner>();
                }
                return instance;
            }
        }

        /// <summary>
        /// Charge une scène proprement. Les LazyFollow du template VR prennent Camera.main à leur
        /// activation ; pendant un chargement il n'y en a aucune (ancienne scène détruite, nouvelle pas
        /// encore active) et ils plantent. Une caméra relais invisible sert de cible le temps du chargement.
        /// </summary>
        public static void LoadScene(string sceneName)
        {
            if (bridgeCamera == null)
            {
                var go = new GameObject("Caméra relais (chargement)") { hideFlags = HideFlags.HideInHierarchy };
                DontDestroyOnLoad(go);
                var cam = Camera.main;
                if (cam != null) go.transform.SetPositionAndRotation(cam.transform.position, cam.transform.rotation);
                bridgeCamera = go.AddComponent<Camera>();
                // Rend dans une minuscule texture (avec profondeur, exigée par URP) : jamais affichée dans le casque
                bridgeCamera.targetTexture = new RenderTexture(16, 16, 24);
                bridgeCamera.cullingMask = 0;
                bridgeCamera.clearFlags = CameraClearFlags.Nothing;
                bridgeCamera.depth = -100f;
                go.tag = "MainCamera";
            }
            SceneManager.LoadSceneAsync(sceneName);
        }

        public static void RunNextFrame()
        {
            Instance.StopAllCoroutines();
            Instance.StartCoroutine(Instance.Run());
        }

        IEnumerator Run()
        {
            yield return null;
            RepairLazyFollows();
            InGameMenu.EnsureInScene();
            ControllerHandVisual.ApplyToScene(GameSettings.ControllerVisual == ControllerVisualMode.Mains);
            ApplyTurnSettings();
        }

        /// <summary>
        /// Locomotion et prise d'objets, appliquées aux composants du rig XRI de la scène
        /// (sans modifier la scène ni le prefab) :
        /// rotation toujours fluide, objets attrapés qui viennent dans la main, joystick réservé aux déplacements.
        /// </summary>
        public static void ApplyTurnSettings()
        {
            // Rotation toujours fluide, à la vitesse choisie
            int managers = 0, snaps = 0, rays = 0, attaches = 0, nearFars = 0;
            foreach (var manager in FindObjectsByType<ControllerInputActionManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                manager.smoothTurnEnabled = true;
                managers++;
            }
            foreach (var turn in FindObjectsByType<ContinuousTurnProvider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                turn.turnSpeed = GameSettings.TurnSpeed;
                turn.enableTurnAround = false; // le rotateur fluide fait aussi un demi-tour au joystick vers le bas
            }

            // Rotateur par crans coupé : c'est lui qui fait le demi-tour quand le joystick est tiré vers le bas
            foreach (var snap in FindObjectsByType<SnapTurnProvider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                snap.enableTurnAround = false;
                snap.enabled = false;
                snaps++;
            }

            // Téléportation : le joystick n'oriente plus la direction d'arrivée
            foreach (var ray in FindObjectsByType<XRRayInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                ray.manipulateAttachTransform = false;
                rays++;
            }

            // Prise d'objets : l'objet attrapé vient dans la main, et le joystick ne le rapproche / n'éloigne plus.
            // Le rig ne coupe donc plus la rotation ni le déplacement quand on tient quelque chose.
            foreach (var nearFar in FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                nearFar.farAttachMode = InteractorFarAttachMode.Near;
                GrabFeel.Attach(nearFar); // arrivée en douceur + tenue à une légère distance
                nearFars++;
            }
            foreach (var attach in FindObjectsByType<InteractionAttachController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                attach.useManipulationInput = false;
                attaches++;
            }

            Debug.Log($"[Rage Room] Locomotion appliquée : {managers} manettes (rotation fluide {GameSettings.TurnSpeed:0}°/s), " +
                      $"{snaps} rotateur(s) par crans coupé(s), {rays} rayon(s) de téléportation, {nearFars} main(s) qui ramènent les objets, " +
                      $"{attaches} manipulation(s) au joystick coupée(s).");
        }

        /// <summary>Rebranche les LazyFollow sans cible (ou sur la caméra relais) sur la vraie caméra, puis supprime le relais.</summary>
        static void RepairLazyFollows()
        {
            Transform bridge = bridgeCamera != null ? bridgeCamera.transform : null;
            if (bridgeCamera != null) bridgeCamera.gameObject.tag = "Untagged"; // pour que Camera.main renvoie celle du jeu

            var cam = Camera.main;
            if (cam != null)
            {
                foreach (var follow in FindObjectsByType<LazyFollow>(FindObjectsSortMode.None))
                {
                    if (follow.target != null && follow.target != bridge) continue;
                    follow.target = cam.transform;
                    if (follow.isActiveAndEnabled)
                    {
                        follow.enabled = false; // relance son initialisation avec la bonne caméra
                        follow.enabled = true;
                    }
                }
            }

            if (bridgeCamera != null)
            {
                if (bridgeCamera.targetTexture != null) bridgeCamera.targetTexture.Release();
                Destroy(bridgeCamera.targetTexture);
                Destroy(bridgeCamera.gameObject);
                bridgeCamera = null;
            }
        }
    }
}
