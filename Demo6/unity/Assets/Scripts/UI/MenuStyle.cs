using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WifiHeatmap.UI
{
    /// <summary>Shared look for the config menu's buttons.</summary>
    public static class MenuStyle
    {
        public static readonly Color Selected = new Color(0.2f, 0.55f, 0.35f, 1f);
        public static readonly Color Normal = new Color(0.16f, 0.18f, 0.2f, 1f);
        public static readonly Color Warning = new Color(0.65f, 0.2f, 0.15f, 1f);

        public static void Tint(Button button, bool on)
        {
            Tint(button, on ? Selected : Normal);
        }

        public static void Tint(Button button, Color color)
        {
            if (button != null && button.targetGraphic != null)
            {
                button.targetGraphic.color = color;
            }
        }

        public static void SetLabel(Button button, string text)
        {
            TMP_Text label = button != null ? button.GetComponentInChildren<TMP_Text>() : null;
            if (label != null && label.text != text)
            {
                label.text = text;
            }
        }
    }

    /// <summary>"Tap again to confirm" for destructive buttons.</summary>
    public sealed class ConfirmTap
    {
        private object pending;
        private float until;

        /// <summary>True on the second tap for the same item within <paramref name="seconds"/>.</summary>
        public bool Tap(object item, float seconds = 3f)
        {
            if (pending != null && pending.Equals(item) && Time.realtimeSinceStartup < until)
            {
                pending = null;
                return true;
            }
            pending = item;
            until = Time.realtimeSinceStartup + seconds;
            return false;
        }

        public bool IsPending(object item)
        {
            return pending != null && pending.Equals(item) && Time.realtimeSinceStartup < until;
        }
    }
}
