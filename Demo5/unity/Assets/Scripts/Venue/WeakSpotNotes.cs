// Sticky notes on the dressing room's mirror: after each loss, one of that MC's sore spots the player hasn't been told
// (from the server, GET /mcs/{id}/tip; one the player already hit in the battle comes only once the rest are told).
// The notes shown are the next opponent's; beating an MC takes theirs down. Remembered between sessions (PlayerPrefs).
// Built on the UI layer, so the mirror doesn't reflect them. MirrorControls adds one if the scene has none.
using System.Collections.Generic;
using System.Linq;
using Freestyle.Battle;
using Freestyle.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Freestyle.Venue
{
    public class WeakSpotNotes : MonoBehaviour
    {
        public OpponentStage stage;
        public BattleCoordinator battle;
        [Tooltip("The mirror's glass. Empty: the scene's StereoMirror.")]
        public StereoMirror mirror;
        [Tooltip("Size of a note (m).")]
        public float size = 0.13f;
        [Tooltip("Down from the glass's top edge to the notes' middles (m).")]
        public float fromTop = 0.16f;

        static readonly Color Paper = new(1f, 0.9f, 0.38f, 1f), Glue = new(0.93f, 0.8f, 0.28f, 1f), Ink = new(0.16f, 0.14f, 0.12f, 1f);
        const float Px = 260f;   // a note's canvas, pixels square
        const string Key = "Freestyle.Tips.";

        readonly List<GameObject> notes = new();
        string shownFor;

        void Start()
        {
            if (!stage) stage = FindAnyObjectByType<OpponentStage>();
            if (!battle) battle = FindAnyObjectByType<BattleCoordinator>();
            if (!mirror) mirror = FindAnyObjectByType<StereoMirror>();
            if (stage) stage.Finished += OnFinished;
            Redraw();
        }

        void OnDestroy() { if (stage) stage.Finished -= OnFinished; }

        static List<string> Saved(string mc) =>
            string.IsNullOrEmpty(mc) ? new List<string>() :
            PlayerPrefs.GetString(Key + mc, "").Split('\n').Where(s => s.Length > 0).ToList();

        static void Save(string mc, List<string> spots)
        {
            if (spots.Count == 0) PlayerPrefs.DeleteKey(Key + mc);
            else PlayerPrefs.SetString(Key + mc, string.Join("\n", spots));
            PlayerPrefs.Save();
        }

        /// <summary>Takes every MC's notes down (a fresh run).</summary>
        public void ClearAll()
        {
            foreach (var p in stage ? stage.lineup : new Freestyle.Avatar.Performer[0])
                if (p) PlayerPrefs.DeleteKey(Key + p.characterId);
            PlayerPrefs.Save();
            Redraw();
        }

        async void OnFinished(string winner)
        {
            string mc = battle ? battle.mcId : null;   // the MC just battled (the lineup has moved on if they lost)
            if (string.IsNullOrEmpty(mc)) return;
            if (winner == "user") { Save(mc, new List<string>()); Redraw(); return; }
            if (winner != "opponent") { Redraw(); return; }   // a draw or a stopped battle: no tip
            var known = Saved(mc);
            var found = battle.Rules?.Found?.ToList() ?? new List<string>();
            var tip = await battle.Tip(mc, known, found);
            if (!this) return;
            if (tip == null) Debug.LogWarning($"WeakSpotNotes: no tip for {mc} (server unreachable)");
            else if (!string.IsNullOrEmpty(tip.spot) && !known.Contains(tip.spot)) { known.Add(tip.spot); Save(mc, known); }
            Redraw();
        }

        /// <summary>The next opponent's notes on the glass.</summary>
        void Redraw()
        {
            foreach (var n in notes) if (n) Destroy(n);
            notes.Clear();
            var next = stage ? stage.Next : null;
            shownFor = next ? next.characterId : null;
            if (!mirror || string.IsNullOrEmpty(shownFor)) return;
            string name = battle?.Mcs.FirstOrDefault(m => m.id == shownFor)?.name;
            if (string.IsNullOrEmpty(name)) name = string.IsNullOrEmpty(next.displayName) ? shownFor : next.displayName;

            var glass = mirror.transform;
            float half = mirror.size.x / 2f, gap = size * 1.25f;
            var spots = Saved(shownFor);
            for (int i = 0; i < spots.Count; i++)
            {
                // along the top of the glass, from the left corner inward, a second row under if it fills up
                int perRow = Mathf.Max(1, Mathf.FloorToInt((mirror.size.x - 0.2f) / gap));
                int col = i % perRow, row = i / perRow;
                Vector3 at = glass.position + glass.up * (mirror.size.y / 2f - fromTop - row * gap)
                             + glass.right * (-half + 0.1f + size / 2f + col * gap) - glass.forward * 0.004f;
                float tilt = ((i * 37) % 11 - 5) * 0.9f;   // stuck on by hand: a little crooked
                notes.Add(Note(i == 0 ? $"{name.ToUpperInvariant()}\ncan't stand" : $"{name} also hates", spots[i], at,
                               glass.rotation * Quaternion.Euler(0, 0, tilt)));
            }
        }

        GameObject Note(string heading, string spot, Vector3 at, Quaternion rot)
        {
            var (canvas, panel) = UIKit.Canvas("Sticky Note", transform, new Vector2(Px, Px), size / Px);
            canvas.transform.SetPositionAndRotation(at, rot);
            foreach (var r in canvas.GetComponents<BaseRaycaster>()) Destroy(r);   // paper: the rays go past it
            var img = panel.GetComponent<Image>();
            img.color = Paper; img.raycastTarget = false;
            var v = panel.GetComponent<VerticalLayoutGroup>();
            v.padding = new RectOffset(18, 18, 0, 14); v.spacing = 4;
            v.childForceExpandHeight = false;
            var glue = UIKit.Size(UIKit.Box(panel, Glue, "Glue"), 22);   // the sticky strip along the top
            glue.GetComponent<Image>().raycastTarget = false;
            var h = UIKit.Size(UIKit.Label(panel, heading, 26, Ink, TextAlignmentOptions.Center, true), 66);
            h.textWrappingMode = TextWrappingModes.Normal;
            var t = UIKit.Size(UIKit.Label(panel, spot, 28, Ink, TextAlignmentOptions.Center), Px - 22 - 66 - 14 - 8);
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Overflow;
            t.enableAutoSizing = true; t.fontSizeMin = 16; t.fontSizeMax = 30;
            t.fontStyle = FontStyles.Italic;
            UIKit.OnUILayer(canvas.gameObject);
            return canvas.gameObject;
        }

        void LateUpdate()
        {
            // the next opponent changed some other way (the debug HUD's pick): their notes instead
            if (Time.frameCount % 30 == 0 && stage && (stage.Next ? stage.Next.characterId : null) != shownFor) Redraw();
        }
    }
}
