using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Attachment;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace RageRoom
{
    /// <summary>
    /// Sensation de prise des objets attrapés à distance (au rayon) :
    /// l'objet vient vers la main en douceur, puis reste tenu à une légère distance devant elle.
    /// Les prises de près (en touchant l'objet) ne sont pas modifiées.
    /// Ajouté automatiquement sur chaque NearFarInteractor par SceneSetupRunner.
    /// </summary>
    [RequireComponent(typeof(NearFarInteractor))]
    public class GrabFeel : MonoBehaviour
    {
        [Tooltip("Durée (s) du trajet de l'objet jusqu'à la main. XRI par défaut : 0,15 s.")]
        public float arrivalTime = 1f;
        [Tooltip("Distance (m) entre la main et l'objet tenu.")]
        public float holdDistance = 0.45f;
        [Tooltip("En dessous de cette distance au moment de la prise, c'est une prise de près : rien n'est modifié.")]
        public float farGrabThreshold = 0.35f;

        NearFarInteractor interactor;
        XRGrabInteractable current;
        float originalEaseIn;

        public static void Attach(NearFarInteractor nf)
        {
            if (nf.GetComponent<GrabFeel>() == null) nf.gameObject.AddComponent<GrabFeel>();
        }

        void Awake()
        {
            interactor = GetComponent<NearFarInteractor>();

            // Distance de tenue stable : pas de poussée / tirage automatiques selon la vitesse de la main
            if (interactor.interactionAttachController is InteractionAttachController attach)
            {
                attach.useDistanceBasedVelocityScaling = false;
                attach.useMomentum = false;
            }
        }

        void OnEnable()
        {
            interactor.selectEntered.AddListener(OnGrab);
            interactor.selectExited.AddListener(OnRelease);
        }

        void OnDisable()
        {
            interactor.selectEntered.RemoveListener(OnGrab);
            interactor.selectExited.RemoveListener(OnRelease);
        }

        void OnGrab(SelectEnterEventArgs args)
        {
            if (args.interactableObject is not XRGrabInteractable grab) return;

            // L'objet n'a pas encore bougé : on mesure s'il a été attrapé de loin
            float distance = Vector3.Distance(grab.transform.position, interactor.transform.position);
            if (distance < farGrabThreshold) return;

            current = grab;
            originalEaseIn = grab.attachEaseInTime;
            grab.attachEaseInTime = arrivalTime;

            // Le point de tenue est remis sur la main (mode « Near ») : on le décale un peu vers l'avant
            interactor.interactionAttachController?.ApplyLocalPositionOffset(new Vector3(0f, 0f, holdDistance));
        }

        void OnRelease(SelectExitEventArgs args)
        {
            if (current != null && ReferenceEquals(args.interactableObject, current))
            {
                current.attachEaseInTime = originalEaseIn;
                current = null;
            }
        }
    }
}
