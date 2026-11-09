// A keyboard in the world, for typing into a TextField with the ray pointer (the Quest's own keyboard isn't available
// to an OpenXR app without Meta's SDK). Letters, numbers, a symbols page, shift, cursor left and right, space,
// backspace and done; typing goes in at the field's cursor (point into the field to move it there). On the flat screen
// the real keyboard types into the focused field too, its arrows, Home, End and Delete moving and deleting.
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Freestyle.UI
{
    /// <summary>A field you type into with the keyboard: a box showing its text (dots for a password) with a cursor.
    /// Point at it and pull the trigger to type there: the cursor goes where you pointed, as on a phone. Long text
    /// scrolls to keep the cursor in view; a multiline field wraps.</summary>
    public class TextField : MonoBehaviour, IPointerDownHandler
    {
        public string Value { get; private set; } = "";
        /// <summary>Where the cursor is: the number of characters before it.</summary>
        public int Caret { get; private set; }
        public bool password, multiline;
        public string placeholder = "";
        public event Action<string> Changed, Submitted;
        public event Action<TextField> Focused;

        TextMeshProUGUI shown;
        RectTransform viewport;
        Image box, caret;
        bool focus;

        public static TextField Make(RectTransform parent, string placeholder, bool password, float height = 70, bool multiline = false)
        {
            var b = UIKit.Button(parent, "", null, multiline ? 28 : 32);
            UIKit.Size(b, height);
            var f = b.gameObject.AddComponent<TextField>();
            f.password = password; f.placeholder = placeholder; f.multiline = multiline;
            f.box = b.GetComponent<Image>();
            // the text sits in a viewport that clips it; the text itself slides to keep the cursor in view
            f.viewport = new GameObject("Viewport", typeof(RectTransform)).GetComponent<RectTransform>();
            f.viewport.SetParent(b.transform, false);
            UIKit.Fill(f.viewport);
            f.viewport.offsetMin = new Vector2(18, multiline ? 10 : 4);
            f.viewport.offsetMax = new Vector2(-18, multiline ? -10 : -4);
            f.viewport.gameObject.AddComponent<RectMask2D>();
            f.shown = UIKit.LabelOf(b);
            var t = f.shown.rectTransform;
            t.SetParent(f.viewport, false);
            f.shown.fontStyle = FontStyles.Normal;
            f.shown.richText = false;   // what's typed is shown as it is (no tags)
            f.shown.overflowMode = TextOverflowModes.Overflow;
            if (multiline)
            {
                f.shown.textWrappingMode = TextWrappingModes.Normal;
                f.shown.alignment = TextAlignmentOptions.TopLeft;
                t.anchorMin = new Vector2(0, 1); t.anchorMax = new Vector2(1, 1); t.pivot = new Vector2(0, 1);
                t.sizeDelta = new Vector2(0, height - 20);
            }
            else
            {
                f.shown.textWrappingMode = TextWrappingModes.NoWrap;
                f.shown.alignment = TextAlignmentOptions.MidlineLeft;
                t.anchorMin = new Vector2(0, 0); t.anchorMax = new Vector2(0, 1); t.pivot = new Vector2(0, 0.5f);
                t.sizeDelta = new Vector2(6000, 0);
            }
            t.anchoredPosition = Vector2.zero;
            f.caret = UIKit.Box(t, UIKit.Accent, "Cursor").GetComponent<Image>();
            f.caret.raycastTarget = false;
            var c = f.caret.rectTransform;
            c.anchorMin = c.anchorMax = t.pivot;
            c.pivot = new Vector2(0.5f, 0.5f);
            b.onClick.AddListener(() => f.Focused?.Invoke(f));
            f.Redraw();
            return f;
        }

        public void Set(string v, bool notify = false)
        {
            Value = v ?? "";
            Caret = Value.Length;
            Redraw();
            if (notify) Changed?.Invoke(Value);
        }

        /// <summary>Types at the cursor.</summary>
        public void Type(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            Value = Value.Insert(Caret, s);
            Caret += s.Length;
            Edited();
        }

        /// <summary>Deletes the character before the cursor (backspace).</summary>
        public void Back()
        {
            if (Caret == 0) return;
            Value = Value.Remove(Caret - 1, 1);
            Caret--;
            Edited();
        }

        /// <summary>Deletes the character after the cursor.</summary>
        public void Delete()
        {
            if (Caret >= Value.Length) return;
            Value = Value.Remove(Caret, 1);
            Edited();
        }

        /// <summary>Moves the cursor by this many characters.</summary>
        public void Move(int by) => MoveTo(Caret + by);
        public void MoveTo(int at) { Caret = Mathf.Clamp(at, 0, Value.Length); Redraw(); }
        public void Home() => MoveTo(0);
        public void End() => MoveTo(Value.Length);
        public void Submit() => Submitted?.Invoke(Value);

        void Edited() { Redraw(); Changed?.Invoke(Value); }

        public void SetFocus(bool on) { focus = on; Redraw(); }

        /// <summary>Pointed at and pressed: the cursor goes to the nearest gap between characters.</summary>
        public void OnPointerDown(PointerEventData e)
        {
            if (!shown) return;
            Vector3 world = e.pointerCurrentRaycast.worldPosition;
            if (world == Vector3.zero &&
                !RectTransformUtility.ScreenPointToWorldPointInRectangle(shown.rectTransform, e.position, e.pressEventCamera, out world))
                return;
            MoveTo(CaretAt(shown.rectTransform.InverseTransformPoint(world)));
        }

        /// <summary>The gap between characters nearest a point in the text's local space.</summary>
        int CaretAt(Vector2 p)
        {
            if (Value.Length == 0) return 0;
            shown.ForceMeshUpdate();
            var info = shown.textInfo;
            if (info.characterCount == 0) return 0;
            int line = 0;
            float best = float.MaxValue;
            for (int l = 0; l < info.lineCount; l++)
            {
                var li = info.lineInfo[l];
                float d = p.y > li.ascender ? p.y - li.ascender : p.y < li.descender ? li.descender - p.y : 0;
                if (d < best) { best = d; line = l; }
            }
            var ln = info.lineInfo[line];
            for (int i = ln.firstCharacterIndex; i <= ln.lastCharacterIndex && i < info.characterCount; i++)
            {
                var ch = info.characterInfo[i];
                if (p.x < (ch.origin + ch.xAdvance) / 2) return ch.index;
            }
            int last = Mathf.Min(ln.lastCharacterIndex, info.characterCount - 1);
            int after = info.characterInfo[last].index + 1;
            // the end of a wrapped line: before its trailing space, so the cursor stays on this line
            if (line < info.lineCount - 1 && after > 0 && after <= Value.Length && char.IsWhiteSpace(Value[after - 1])) after--;
            return Mathf.Clamp(after, 0, Value.Length);
        }

        void Redraw()
        {
            if (!shown) return;
            bool empty = Value.Length == 0;
            shown.text = empty ? placeholder : password ? new string('•', Value.Length) : Value;
            shown.color = empty ? UIKit.Dim : UIKit.TextColour;
            box.color = focus ? new Color(0.25f, 0.27f, 0.32f, 1f) : UIKit.ButtonColour;
            PlaceCaret(empty);
        }

        /// <summary>The cursor at its gap, and the text slid to keep it in view.</summary>
        void PlaceCaret(bool empty)
        {
            shown.ForceMeshUpdate();
            var info = shown.textInfo;
            float size = shown.fontSize;
            Vector2 at;
            float top, bottom;
            if (empty || info.characterCount == 0 || Caret == 0)
            {
                var first = !empty && info.characterCount > 0 ? info.characterInfo[0] : default;
                float x = !empty && info.characterCount > 0 ? first.origin : 0;
                if (!empty && info.lineCount > 0) { top = info.lineInfo[0].ascender; bottom = info.lineInfo[0].descender; }
                else if (multiline) { top = 0; bottom = -size * 1.15f; }
                else { top = size * 0.55f; bottom = -size * 0.55f; }
                at = new Vector2(x, (top + bottom) / 2);
            }
            else
            {
                var ch = info.characterInfo[Mathf.Min(Caret, info.characterCount) - 1];
                var li = info.lineInfo[ch.lineNumber];
                float x = ch.xAdvance;
                top = li.ascender; bottom = li.descender;
                // at the start of a wrapped line (after the previous line's space): the start of that line
                if (Caret < info.characterCount && info.characterInfo[Caret].lineNumber > ch.lineNumber && char.IsWhiteSpace(Value[Caret - 1]))
                {
                    var next = info.characterInfo[Caret];
                    var nl = info.lineInfo[next.lineNumber];
                    x = next.origin; top = nl.ascender; bottom = nl.descender;
                }
                at = new Vector2(x, (top + bottom) / 2);
            }
            var c = caret.rectTransform;
            c.sizeDelta = new Vector2(3, Mathf.Max(8, top - bottom));
            c.anchoredPosition = at;

            // keep the cursor in view: slide the text sideways (one line) or up (wrapped)
            var t = shown.rectTransform;
            var view = viewport.rect;
            var pos = t.anchoredPosition;
            // not being edited: from the start; and nothing to slide until it's laid out
            if (!focus || view.width <= 1 || view.height <= 1) { t.anchoredPosition = Vector2.zero; caret.enabled = focus; return; }
            if (multiline)
            {
                float y = pos.y;
                float caretTop = top + y, caretBottom = bottom + y;   // relative to the viewport's top
                if (caretBottom < -view.height) y += -view.height - caretBottom;
                if (caretTop > 0) y -= caretTop;
                pos.y = Mathf.Max(0, y);
            }
            else
            {
                float x = pos.x;
                float caretX = at.x + x;
                if (caretX > view.width - 6) x -= caretX - (view.width - 6);
                if (caretX < 0) x -= caretX;
                pos.x = Mathf.Min(0, x);
                if (empty || Caret == 0) pos.x = 0;
            }
            t.anchoredPosition = pos;
            caret.enabled = focus;
        }

        void Update()
        {
            if (caret) caret.enabled = focus && Time.unscaledTime % 1f < 0.6f;   // blinking
        }
    }

    public class VRKeyboard : MonoBehaviour
    {
        public TextField Target { get; private set; }
        public event Action Done;

        static VRKeyboard typingInto;
        /// <summary>Some field has the keyboard (on the flat screen the keys type, so WASD and the shortcuts stand down).</summary>
        public static bool Typing => typingInto && typingInto.Target && typingInto.isActiveAndEnabled;

        static readonly string[][] Letters =
        {
            new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
            new[] { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p" },
            new[] { "a", "s", "d", "f", "g", "h", "j", "k", "l", ":" },
            new[] { "z", "x", "c", "v", "b", "n", "m", ".", "/", "-" },
        };
        static readonly string[][] SymbolKeys =
        {
            new[] { "!", "@", "#", "$", "%", "^", "&", "*", "(", ")" },
            new[] { "_", "+", "=", "~", "`", "[", "]", "{", "}", "|" },
            new[] { ";", "'", "\"", ",", "<", ">", "?", "\\", ":", "." },
            new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
        };

        readonly List<(Button b, int row, int col)> keys = new();
        Button shiftKey, symKey;
        bool shift, symbols;

        /// <summary>A keyboard filling `parent` (a layout child of the given height).</summary>
        public static VRKeyboard Make(RectTransform parent, float keyHeight = 66)
        {
            var root = new GameObject("Keyboard", typeof(RectTransform)).GetComponent<RectTransform>();
            root.SetParent(parent, false);
            var v = root.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = 8; v.childControlWidth = v.childControlHeight = true; v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            var kb = root.gameObject.AddComponent<VRKeyboard>();
            for (int r = 0; r < Letters.Length; r++)
            {
                var row = UIKit.Row(root, keyHeight, 8);
                row.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = true;
                for (int c = 0; c < Letters[r].Length; c++)
                {
                    int rr = r, cc = c;
                    var b = UIKit.Button(row, Letters[r][c], () => kb.Key(rr, cc), 32);
                    UIKit.Size(b, keyHeight, -1, 1);
                    kb.keys.Add((b, r, c));
                }
            }
            var last = UIKit.Row(root, keyHeight, 8);
            kb.shiftKey = UIKit.Size(UIKit.Button(last, "SHIFT", kb.Shift, 26), keyHeight, -1, 1.4f);
            kb.symKey = UIKit.Size(UIKit.Button(last, "#+=", kb.ToggleSymbols, 26), keyHeight, -1, 1.2f);
            UIKit.Size(UIKit.Button(last, "\u2190", () => kb.Target?.Move(-1), 34), keyHeight, -1, 1f);   // cursor left
            UIKit.Size(UIKit.Button(last, "SPACE", () => kb.Target?.Type(" "), 26), keyHeight, -1, 2.4f);
            UIKit.Size(UIKit.Button(last, "\u2192", () => kb.Target?.Move(1), 34), keyHeight, -1, 1f);    // cursor right
            UIKit.Size(UIKit.Button(last, "BACK", () => kb.Target?.Back(), 26), keyHeight, -1, 1.2f);
            UIKit.Size(UIKit.Button(last, "DONE", kb.Finish, 26, new Color(0.1f, 0.45f, 0.42f, 1f)), keyHeight, -1, 1.6f);
            kb.Relabel();
            return kb;
        }

        public void Attach(TextField field)
        {
            if (Target) Target.SetFocus(false);
            Target = field;
            if (Target) { Target.SetFocus(true); typingInto = this; }
        }

        void Key(int r, int c)
        {
            if (!Target) return;
            string k = (symbols ? SymbolKeys : Letters)[r][c];
            Target.Type(shift ? k.ToUpperInvariant() : k);
            if (shift) { shift = false; Relabel(); }   // one capital, like a phone
        }

        void Shift() { shift = !shift; Relabel(); }
        void ToggleSymbols() { symbols = !symbols; shift = false; Relabel(); }

        void Finish()
        {
            Target?.Submit();
            Attach(null);
            Done?.Invoke();
        }

        void Relabel()
        {
            foreach (var (b, r, c) in keys)
            {
                string k = (symbols ? SymbolKeys : Letters)[r][c];
                UIKit.LabelOf(b).text = shift ? k.ToUpperInvariant() : k;
            }
            if (shiftKey) shiftKey.GetComponent<Image>().color = shift ? new Color(0.1f, 0.45f, 0.42f, 1f) : UIKit.ButtonColour;
            if (symKey) UIKit.LabelOf(symKey).text = symbols ? "ABC" : "#+=";
        }

        // the flat screen: the real keyboard types into the focused field
        void OnEnable() { if (Keyboard.current != null) Keyboard.current.onTextInput += OnText; }
        void OnDisable() { if (Keyboard.current != null) Keyboard.current.onTextInput -= OnText; }

        void Update()
        {
            // the flat screen: the real keyboard's cursor keys (typing comes through OnText)
            var kb = Keyboard.current;
            if (!Target || !isActiveAndEnabled || kb == null) return;
            if (kb.leftArrowKey.wasPressedThisFrame) Target.Move(-1);
            if (kb.rightArrowKey.wasPressedThisFrame) Target.Move(1);
            if (kb.homeKey.wasPressedThisFrame) Target.Home();
            if (kb.endKey.wasPressedThisFrame) Target.End();
            if (kb.deleteKey.wasPressedThisFrame) Target.Delete();
        }

        void OnText(char ch)
        {
            if (!Target || !isActiveAndEnabled) return;
            if (ch == '\b') Target.Back();
            else if (ch == '\r' || ch == '\n') Finish();
            else if (!char.IsControl(ch)) Target.Type(ch.ToString());
        }
    }
}
