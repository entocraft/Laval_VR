using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace RageRoom
{
    /// <summary>
    /// Fabrique d'éléments d'interface VR (canvas world-space, panneaux, boutons, sliders) au style du menu.
    /// Les sprites viennent de l'asset « MenuUiSkin » (dossier Resources) ; sans lui, tout reste fonctionnel mais carré.
    /// </summary>
    public static class MenuUi
    {
        public static readonly Color PanelColor = new Color(0.05f, 0.05f, 0.06f, 0.92f);
        public static readonly Color Red = new Color(0.86f, 0.16f, 0.16f);
        public static readonly Color Grey = new Color(0.25f, 0.25f, 0.28f);

        static MenuUiSkin skin;
        static MenuUiSkin Skin => skin != null ? skin : skin = Resources.Load<MenuUiSkin>(MenuUiSkin.ResourceName);

        public static Canvas MakeCanvas(string name, float pixelSize, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;
            go.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 3f;
            go.AddComponent<TrackedDeviceGraphicRaycaster>();
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = size;
            rt.localScale = Vector3.one * pixelSize;
            return canvas;
        }

        public static RectTransform MakePanel(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            Stretch(rt);
            Style(go.AddComponent<Image>(), PanelColor);
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(40, 40, 36, 36);
            layout.spacing = 16f;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return rt;
        }

        public static RectTransform MakeRow(Transform parent, string name, float height)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var h = go.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 14f;
            h.childAlignment = TextAnchor.MiddleCenter;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = true;
            h.childForceExpandHeight = true;
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = le.minHeight = height;
            return (RectTransform)go.transform;
        }

        public static TMP_Text MakeText(Transform parent, string text, float size, FontStyles style,
                                        TextAlignmentOptions align, float height, Color color)
        {
            var go = new GameObject("Texte", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = align;
            t.color = color;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            var le = go.AddComponent<LayoutElement>();
            if (height > 0f) le.preferredHeight = le.minHeight = height;
            return t;
        }

        public static Button MakeButton(Transform parent, string label, Color color, float fontSize, float height)
        {
            var go = new GameObject("Bouton " + label, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Style(go.AddComponent<Image>(), Color.white);
            var b = go.AddComponent<Button>();
            b.navigation = new Navigation { mode = Navigation.Mode.None };
            Tint(b, color);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = le.minHeight = height;
            le.flexibleWidth = 1f;

            var t = MakeText(go.transform, label, fontSize, FontStyles.Bold, TextAlignmentOptions.Center, 0f, Color.white);
            Stretch((RectTransform)t.transform);
            return b;
        }

        public static Slider MakeSliderRow(Transform parent, string label, out TMP_Text value)
        {
            var header = MakeRow(parent, label, 34);
            header.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = false;
            var name = MakeText(header, label, 24, FontStyles.Normal, TextAlignmentOptions.Left, 0f, new Color(1f, 1f, 1f, 0.8f));
            name.GetComponent<LayoutElement>().flexibleWidth = 1f;
            value = MakeText(header, "100 %", 24, FontStyles.Bold, TextAlignmentOptions.Right, 0f, Color.white);
            value.GetComponent<LayoutElement>().preferredWidth = 100f;

            var res = new DefaultControls.Resources();
            if (Skin != null)
            {
                res.standard = Skin.standard;
                res.background = Skin.background;
                res.knob = Skin.knob;
            }
            var go = DefaultControls.CreateSlider(res);
            go.name = "Slider " + label;
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = le.minHeight = 36f;
            var slider = go.GetComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 1f;
            slider.navigation = new Navigation { mode = Navigation.Mode.None };

            var fill = go.transform.Find("Fill Area/Fill")?.GetComponent<Image>();
            if (fill != null) fill.color = Red;
            var handle = go.transform.Find("Handle Slide Area/Handle") as RectTransform;
            if (handle != null) handle.sizeDelta = new Vector2(40f, 0f);
            return slider;
        }

        public static void Tint(Button b, Color c)
        {
            var colors = b.colors;
            colors.normalColor = c;
            colors.selectedColor = c;
            colors.highlightedColor = Color.Lerp(c, Color.white, 0.25f);
            colors.pressedColor = Color.Lerp(c, Color.black, 0.3f);
            colors.fadeDuration = 0.05f;
            b.colors = colors;
        }

        public static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        static void Style(Image img, Color color)
        {
            img.color = color;
            if (Skin != null && Skin.standard != null)
            {
                img.sprite = Skin.standard;
                img.type = Image.Type.Sliced;
            }
        }
    }
}
