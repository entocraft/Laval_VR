using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RageRoom
{
    /// <summary>
    /// Affiche/cache le panneau de commande avec le bouton B de la manette droite.
    /// Le panneau suit la manette droite tant qu'il est ouvert. À mettre sur un AUTRE objet que le panneau.
    /// </summary>
    public class OrderPanelPresenter : MonoBehaviour
    {
        [Header("Références")]
        [SerializeField] Transform panel;
        [Tooltip("Main Camera (auto si vide).")]
        [SerializeField] Transform head;

        [Header("Bouton manette droite")]
        [SerializeField] InputActionProperty toggleAction = new InputActionProperty(
            new InputAction("Toggle Order Panel", InputActionType.Button, "<XRController>{RightHand}/secondaryButton"));
        [Tooltip("Right Controller (auto si vide).")]
        [SerializeField] Transform pinnedController;
        [SerializeField] Vector3 pinnedOffset = new Vector3(0f, 0.2f, 0.05f);
        [SerializeField] bool pinnedFacesHead = true;
        [SerializeField] Vector3 pinnedEuler = new Vector3(30f, 0f, 0f);

        bool visible;

        void OnEnable()  { if (toggleAction.reference == null) toggleAction.action?.Enable(); }
        void OnDisable() { if (toggleAction.reference == null) toggleAction.action?.Disable(); }

        void Start()
        {
            // Retrouve automatiquement l'XR Origin si les références n'ont pas été branchées
            var origin = FindFirstObjectByType<XROrigin>();
            if (origin != null)
            {
                if (head == null && origin.Camera != null) head = origin.Camera.transform;
                if (pinnedController == null) pinnedController = FindDeep(origin.transform, "Right Controller");
            }
            if (head == null && Camera.main != null) head = Camera.main.transform;
            if (pinnedController == null) Debug.LogWarning("[OrderPanelPresenter] « Right Controller » introuvable : le panneau s'affichera devant la caméra.");
            if (panel == transform) Debug.LogError("[OrderPanelPresenter] Mets ce script sur un autre objet que le panneau.");

            SetVisible(false);
        }

        void Update()
        {
            if (panel == null || head == null) return;

            if (toggleAction.action != null && toggleAction.action.WasPressedThisFrame())
                SetVisible(!visible);

            if (!visible) return;

            if (pinnedController == null)
            {
                // Pas de manette trouvée : panneau flottant devant les yeux
                Vector3 front = head.position + head.forward * 0.5f;
                panel.SetPositionAndRotation(front, FaceHead(front));
                return;
            }

            Vector3 pos = pinnedController.TransformPoint(pinnedOffset);
            Quaternion rot = pinnedFacesHead ? FaceHead(pos) : pinnedController.rotation * Quaternion.Euler(pinnedEuler);
            panel.SetPositionAndRotation(pos, rot); // collé à la manette, sans lissage
        }

        void SetVisible(bool value)
        {
            visible = value;
            if (panel != null) panel.gameObject.SetActive(value);
        }

        // Le canvas est lisible quand son +Z pointe à l'opposé de la tête
        Quaternion FaceHead(Vector3 pos) => Quaternion.LookRotation(pos - head.position, Vector3.up);

        static Transform FindDeep(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name) return child;
                var found = FindDeep(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
