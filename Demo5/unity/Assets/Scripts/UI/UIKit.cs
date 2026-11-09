// Builds the game's world-space menus in code (uGUI + TextMesh Pro), one look for all of them: dark panels, big text
// and big targets for a ray pointer, stacked with layout groups. Canvases get a TrackedDeviceGraphicRaycaster (the XR
// rays) and a GraphicRaycaster (the mouse, on the flat screen); the scene's EventSystem is an XRUIInputModule.
using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace Freestyle.UI
{
    public static class UIKit
    {
        public static readonly Color PanelColour = new(0.07f, 0.07f, 0.09f, 0.96f);
        public static readonly Color ButtonColour = new(0.18f, 0.18f, 0.22f, 1f);
        public static readonly Color Accent = new(0.2f, 0.95f, 0.9f, 1f);   // turquoise, as on the stage's monitor
        public static readonly Color TextColour = new(0.93f, 0.93f, 0.96f, 1f);
        public static readonly Color Dim = new(0.6f, 0.6f, 0.66f, 1f);

        /// <summary>A world-space canvas `sizePx` big, `metresPerPx` to the pixel, facing -Z of its transform (readable
        /// from in front), with a dark panel filling it that stacks its children top to bottom.</summary>
        public static (Canvas canvas, RectTransform panel) Canvas(string name, Transform parent, Vector2 sizePx, float metresPerPx)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = sizePx;
            rt.localScale = Vector3.one * metresPerPx;
            go.AddComponent<GraphicRaycaster>();
            go.AddComponent<TrackedDeviceGraphicRaycaster>();
            var panel = Box(rt, PanelColour);
            Fill(panel);
            var v = panel.gameObject.AddComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(36, 36, 30, 30);
            v.spacing = 16;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            return (canvas, panel);
        }

        public const int Layer = 5;   // Unity's UI layer: the menus (the mirror leaves it out, so they don't show backwards)

        /// <summary>Puts a menu and everything in it on the UI layer (after building, and after adding to it).</summary>
        public static void OnUILayer(GameObject root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = Layer;
        }

        public static RectTransform Box(RectTransform parent, Color colour, string name = "Box")
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = colour;
            return (RectTransform)go.transform;
        }

        public static void Fill(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        /// <summary>A height (and a width, when flexible width isn't wanted) for a child of a layout group.</summary>
        public static T Size<T>(T c, float height, float width = -1, float flexWidth = -1) where T : Component
        {
            var le = c.GetComponent<LayoutElement>() ? c.GetComponent<LayoutElement>() : c.gameObject.AddComponent<LayoutElement>();
            le.preferredHeight = le.minHeight = height;
            if (width > 0) le.preferredWidth = le.minWidth = width;
            if (flexWidth >= 0) le.flexibleWidth = flexWidth;
            return c;
        }

        public static TextMeshProUGUI Label(RectTransform parent, string text, float size, Color colour,
                                            TextAlignmentOptions align = TextAlignmentOptions.Left, bool bold = false)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size;
            t.color = colour;
            t.alignment = align;
            t.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.raycastTarget = false;
            return t;
        }

        /// <summary>A row: children side by side.</summary>
        public static RectTransform Row(RectTransform parent, float height, float spacing = 14)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var h = go.AddComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.childControlWidth = h.childControlHeight = true;
            h.childForceExpandWidth = false; h.childForceExpandHeight = true;
            h.childAlignment = TextAnchor.MiddleLeft;
            var rt = (RectTransform)go.transform;
            Size(rt, height);
            return rt;
        }

        public static Button Button(RectTransform parent, string text, UnityAction onClick, float fontSize = 34, Color? colour = null)
        {
            var rt = Box(parent, colour ?? ButtonColour, "Button " + text);
            var b = rt.gameObject.AddComponent<Button>();
            b.targetGraphic = rt.GetComponent<Image>();
            var cb = b.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.35f, 1.35f, 1.35f, 1f);
            cb.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            cb.selectedColor = Color.white;
            cb.colorMultiplier = 1.6f;
            b.colors = cb;
            var nav = b.navigation; nav.mode = Navigation.Mode.None; b.navigation = nav;   // (no keyboard focus ring)
            var label = Label(rt, text, fontSize, TextColour, TextAlignmentOptions.Center, true);
            Fill((RectTransform)label.transform);
            if (onClick != null) b.onClick.AddListener(onClick);
            return b;
        }

        public static TextMeshProUGUI LabelOf(Button b) => b.GetComponentInChildren<TextMeshProUGUI>();

        /// <summary>A slider from min to max (a track, a fill, a big handle).</summary>
        public static Slider Slider(RectTransform parent, float min, float max, float value, Action<float> onChange)
        {
            var rt = Box(parent, new Color(0, 0, 0, 0), "Slider");
            var track = Box(rt, new Color(0.22f, 0.22f, 0.27f, 1f), "Track");
            track.anchorMin = new Vector2(0, 0.38f); track.anchorMax = new Vector2(1, 0.62f); track.offsetMin = track.offsetMax = Vector2.zero;
            var fillArea = new GameObject("Fill Area", typeof(RectTransform)).GetComponent<RectTransform>();
            fillArea.SetParent(rt, false);
            fillArea.anchorMin = new Vector2(0, 0.38f); fillArea.anchorMax = new Vector2(1, 0.62f); fillArea.offsetMin = fillArea.offsetMax = Vector2.zero;
            var fill = Box(fillArea, Accent * new Color(1, 1, 1, 0.8f), "Fill");
            fill.anchorMin = Vector2.zero; fill.anchorMax = new Vector2(0, 1); fill.offsetMin = fill.offsetMax = Vector2.zero;
            var handleArea = new GameObject("Handle Area", typeof(RectTransform)).GetComponent<RectTransform>();
            handleArea.SetParent(rt, false);
            Fill(handleArea);
            handleArea.offsetMin = new Vector2(20, 0); handleArea.offsetMax = new Vector2(-20, 0);
            var handle = Box(handleArea, TextColour, "Handle");
            handle.sizeDelta = new Vector2(40, 0);
            handle.anchorMin = new Vector2(0, 0); handle.anchorMax = new Vector2(0, 1);
            var s = rt.gameObject.AddComponent<Slider>();
            s.fillRect = fill; s.handleRect = handle; s.targetGraphic = handle.GetComponent<Image>();
            s.minValue = min; s.maxValue = max; s.value = value;
            var nav = s.navigation; nav.mode = Navigation.Mode.None; s.navigation = nav;
            s.onValueChanged.AddListener(v => onChange?.Invoke(v));
            rt.GetComponent<Image>().raycastTarget = true;   // the whole strip can be grabbed, not just the handle
            return s;
        }

        /// <summary>A vertical scrolling list: returns the content to add items to (stacked, as tall as they need).</summary>
        public static (ScrollRect scroll, RectTransform content) Scroll(RectTransform parent, float height)
        {
            var root = Size(Box(parent, new Color(0, 0, 0, 0.35f), "List"), height);
            var viewport = Box(root, new Color(1, 1, 1, 0.01f), "Viewport");
            Fill(viewport);
            viewport.offsetMax = new Vector2(-40, 0);   // room for the scrollbar
            viewport.gameObject.AddComponent<RectMask2D>();
            var content = new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>();
            content.SetParent(viewport, false);
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(0.5f, 1);
            content.offsetMin = content.offsetMax = Vector2.zero;
            var v = content.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = 10; v.padding = new RectOffset(10, 10, 10, 10);
            v.childControlWidth = v.childControlHeight = true; v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var barRt = Box(root, new Color(0.15f, 0.15f, 0.19f, 1f), "Scrollbar");
            barRt.anchorMin = new Vector2(1, 0); barRt.anchorMax = Vector2.one; barRt.pivot = new Vector2(1, 0.5f);
            barRt.sizeDelta = new Vector2(34, 0); barRt.anchoredPosition = Vector2.zero;
            var handle = Box(barRt, Dim, "Handle");
            Fill(handle);
            var bar = barRt.gameObject.AddComponent<Scrollbar>();
            bar.direction = Scrollbar.Direction.BottomToTop;
            bar.handleRect = handle; bar.targetGraphic = handle.GetComponent<Image>();

            var scroll = root.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport; scroll.content = content;
            scroll.horizontal = false; scroll.vertical = true;
            scroll.verticalScrollbar = bar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;   // a mouse wheel (in VR: drag with the trigger)
            return (scroll, content);
        }
    }
}
