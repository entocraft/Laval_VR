using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RageRoom
{
    /// <summary>
    /// Affiche une main virtuelle à la place du modèle de manette (« Left/Right Controller Visual »).
    /// Les doigts se plient selon la gâchette (index) et le grip (autres doigts).
    /// Ajouté automatiquement sur les manettes de l'XR Origin par GameSettings : rien à placer à la main.
    /// En hand tracking, le rig désactive la manette entière, donc cette main disparaît avec elle.
    /// </summary>
    public class ControllerHandVisual : MonoBehaviour
    {
        enum Side { Left, Right }

        Side side;
        ControllerHandsConfig config;
        GameObject controllerVisual;
        GameObject hand;
        InputAction gripAction, triggerAction;
        float grip, trigger;
        bool showHands;

        struct Bone { public Transform t; public Quaternion bind; public float maxAngle; public int group; }
        readonly List<Bone> bones = new List<Bone>();
        const int GroupIndex = 0, GroupGrip = 1, GroupThumb = 2;

        /// <summary>Applique le choix manettes / mains à l'XR Origin de la scène actuelle.</summary>
        public static void ApplyToScene(bool hands)
        {
            var origin = FindFirstObjectByType<XROrigin>(FindObjectsInactive.Include);
            if (origin == null) return;

            foreach (var side in new[] { Side.Left, Side.Right })
            {
                var controller = FindDeep(origin.transform, side == Side.Left ? "Left Controller" : "Right Controller");
                if (controller == null) continue;
                if (!controller.TryGetComponent<ControllerHandVisual>(out var visual))
                {
                    visual = controller.gameObject.AddComponent<ControllerHandVisual>();
                    visual.Setup(side);
                }
                visual.SetShowHands(hands);
            }
        }

        void Setup(Side s)
        {
            side = s;
            config = Resources.Load<ControllerHandsConfig>(ControllerHandsConfig.ResourceName);
            controllerVisual = FindDeep(transform, s == Side.Left ? "Left Controller Visual" : "Right Controller Visual")?.gameObject;

            string hand = s == Side.Left ? "{LeftHand}" : "{RightHand}";
            gripAction = new InputAction("Grip", InputActionType.Value, $"<XRController>{hand}/grip");
            triggerAction = new InputAction("Trigger", InputActionType.Value, $"<XRController>{hand}/trigger");
            gripAction.Enable();
            triggerAction.Enable();
        }

        void SetShowHands(bool value)
        {
            showHands = value;
            if (showHands && hand == null) CreateHand();

            bool handOk = hand != null;
            if (hand != null) hand.SetActive(showHands);
            // Si la main n'a pas pu être créée, on garde la manette visible
            if (controllerVisual != null) controllerVisual.SetActive(!showHands || !handOk);
        }

        void CreateHand()
        {
            if (config == null)
            {
                Debug.LogWarning($"[ControllerHandVisual] Asset « {ControllerHandsConfig.ResourceName} » introuvable dans un dossier Resources.");
                return;
            }
            var model = side == Side.Left ? config.leftHandModel : config.rightHandModel;
            if (model == null) { Debug.LogWarning("[ControllerHandVisual] Modèle de main non assigné dans ControllerHandsConfig."); return; }

            hand = Instantiate(model, transform);
            hand.name = side == Side.Left ? "Main gauche (virtuelle)" : "Main droite (virtuelle)";
            if (config.handMaterial != null)
                foreach (var r in hand.GetComponentsInChildren<Renderer>(true))
                    r.sharedMaterial = config.handMaterial;

            // Place la racine pour que le poignet tombe à la position voulue
            string prefix = side == Side.Left ? "L_" : "R_";
            var wrist = FindDeep(hand.transform, prefix + "Wrist");
            var wantPos = side == Side.Left ? config.leftWristPosition : config.rightWristPosition;
            var wantRot = Quaternion.Euler(side == Side.Left ? config.leftWristRotation : config.rightWristRotation);
            hand.transform.localPosition = Vector3.zero;
            hand.transform.localRotation = Quaternion.identity;
            if (wrist != null)
            {
                var wristRel = Quaternion.Inverse(hand.transform.rotation) * wrist.rotation;
                var wristPosRel = hand.transform.InverseTransformPoint(wrist.position);
                hand.transform.localRotation = wantRot * Quaternion.Inverse(wristRel);
                hand.transform.localPosition = wantPos - hand.transform.localRotation * Vector3.Scale(wristPosRel, hand.transform.localScale);
            }
            else
            {
                hand.transform.localPosition = wantPos;
                hand.transform.localRotation = wantRot;
            }

            // Phalanges à plier
            foreach (var finger in new[] { "Index", "Middle", "Ring", "Little" })
            {
                int group = finger == "Index" ? GroupIndex : GroupGrip;
                AddBone(prefix + finger + "Proximal", config.fingerAngles.x, group);
                AddBone(prefix + finger + "Intermediate", config.fingerAngles.y, group);
                AddBone(prefix + finger + "Distal", config.fingerAngles.z, group);
            }
            AddBone(prefix + "ThumbMetacarpal", config.thumbAngles.x, GroupThumb);
            AddBone(prefix + "ThumbProximal", config.thumbAngles.y, GroupThumb);
            AddBone(prefix + "ThumbDistal", config.thumbAngles.z, GroupThumb);
        }

        void AddBone(string name, float angle, int group)
        {
            var t = FindDeep(hand.transform, name);
            if (t != null) bones.Add(new Bone { t = t, bind = t.localRotation, maxAngle = angle, group = group });
        }

        void Update()
        {
            if (!showHands || hand == null || config == null) return;

            float k = 1f - Mathf.Exp(-config.fingerSpeed * Time.deltaTime);
            grip = Mathf.Lerp(grip, gripAction.ReadValue<float>(), k);
            trigger = Mathf.Lerp(trigger, triggerAction.ReadValue<float>(), k);

            float rest = config.restCurl;
            float indexCurl = rest + (1f - rest) * trigger;
            float gripCurl = rest + (1f - rest) * grip;
            float thumbCurl = rest + (1f - rest) * Mathf.Max(grip, trigger) * 0.6f;
            var axis = config.curlAxis.sqrMagnitude > 0f ? config.curlAxis.normalized : Vector3.right;

            foreach (var b in bones)
            {
                float curl = b.group == GroupIndex ? indexCurl : b.group == GroupGrip ? gripCurl : thumbCurl;
                b.t.localRotation = b.bind * Quaternion.AngleAxis(b.maxAngle * curl, axis);
            }
        }

        void OnDestroy()
        {
            gripAction?.Dispose();
            triggerAction?.Dispose();
        }

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
