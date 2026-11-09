// The song list on the dressing room's wall: every beat the battle can play (the project's and the server's), to
// scroll through and pick with the ray pointer (point, pull the trigger; drag with the trigger held to scroll). The picked one is
// lit and the next battle plays it. Refreshed when the server's list arrives; Refresh asks again (after you've
// connected in the settings menu). Built at Start as a canvas centred on this transform, facing -Z, framed as a
// wall-mounted flat-panel screen (a slim glossy bezel, a mount plate behind, a power light).
// Picking a beat also previews it on the scene's boombox (on the spray table below), which loads
// it for the battle; picking it again while it plays stops the preview.
using System.Collections.Generic;
using Freestyle.Battle;
using Freestyle.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Freestyle.Venue
{
    public class SongSelector : MonoBehaviour
    {
        public BattleCoordinator battle;
        public Vector2 sizePx = new(820, 1050);
        public float metresPerPx = 0.001f;
        [Tooltip("Plays a preview of the picked beat. Empty: the scene's.")]
        public Boombox boombox;

        RectTransform content;
        TextMeshProUGUI picked, note;
        string noteText = "";
        readonly List<(Button b, SongInfo song)> rows = new();

        void Start()
        {
            if (!battle) battle = FindAnyObjectByType<BattleCoordinator>();
            var (canvas, panel) = UIKit.Canvas("Song List", transform, sizePx, metresPerPx);
            canvas.transform.localPosition = new Vector3(0, 0, -0.002f);   // the picture, just in front of the bezel
            panel.GetComponent<Image>().color = new Color(0.04f, 0.05f, 0.07f, 1f);   // a lit screen: blue-black
            Television(sizePx * metresPerPx);
            UIKit.Size(UIKit.Label(panel, "PICK A BEAT", 56, UIKit.Accent, TextAlignmentOptions.Center, true), 74);
            picked = UIKit.Size(UIKit.Label(panel, "", 30, UIKit.TextColour, TextAlignmentOptions.Center), 42);
            (_, content) = UIKit.Scroll(panel, sizePx.y - 360);
            note = UIKit.Size(UIKit.Label(panel, "", 26, UIKit.Dim, TextAlignmentOptions.Center), 40);
            UIKit.Size(UIKit.Button(panel, "REFRESH", () => { if (battle) _ = battle.LoadLobby(); }, 32), 76);
            if (battle) battle.LobbyLoaded += Fill;
            Fill();
            if (!boombox) boombox = FindAnyObjectByType<Boombox>();   // (Freestyle > Build Boombox puts one in the scene)
        }

        /// <summary>The TV round the picture: a glossy black bezel, a darker plate behind it against the wall, a light.</summary>
        void Television(Vector2 picture)
        {
            const float rim = 0.022f, depth = 0.035f;
            var gloss = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Screen Bezel" };
            gloss.SetColor("_BaseColor", new Color(0.02f, 0.02f, 0.025f));
            gloss.SetFloat("_Smoothness", 0.85f);
            var matte = new Material(gloss) { name = "Screen Back" };
            matte.SetFloat("_Smoothness", 0.25f);
            Block("Bezel", new Vector3(0, 0, depth / 2), new Vector3(picture.x + 2 * rim, picture.y + 2 * rim, depth), gloss);
            Block("Back", new Vector3(0, 0, depth + 0.006f), new Vector3(picture.x * 0.85f, picture.y * 0.85f, 0.012f), matte);
            var led = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "Power Light" };
            led.SetColor("_BaseColor", new Color(0.2f, 0.95f, 0.9f));
            Block("Power Light", new Vector3(picture.x / 2 - 0.03f, -picture.y / 2 - rim / 2, -0.001f), new Vector3(0.008f, 0.004f, 0.002f), led);
        }

        void Block(string name, Vector3 at, Vector3 size, Material mat)
        {
            var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
            b.name = name;
            Destroy(b.GetComponent<Collider>());
            b.transform.SetParent(transform, false);
            b.transform.localPosition = at;
            b.transform.localScale = size;
            b.GetComponent<Renderer>().sharedMaterial = mat;
        }

        void OnDestroy() { if (battle) battle.LobbyLoaded -= Fill; }

        void Fill()
        {
            foreach (Transform c in content) Destroy(c.gameObject);
            rows.Clear();
            var songs = battle ? battle.Songs : new List<SongInfo>();
            foreach (var s in songs)
            {
                var song = s;
                string name = string.IsNullOrEmpty(s.title) ? s.id : s.title;
                var b = UIKit.Size(UIKit.Button(content, "", () => Pick(song), 30), 84);
                var label = UIKit.LabelOf(b);
                label.alignment = TextAlignmentOptions.MidlineLeft;
                label.fontStyle = FontStyles.Normal;
                ((RectTransform)label.transform).offsetMin = new Vector2(22, 0);
                ((RectTransform)label.transform).offsetMax = new Vector2(-22, 0);
                label.text = $"{name}  <color=#9a9aa6><size=24>{(s.bpm > 0 ? $"{s.bpm:0} BPM" : "")}{(s.local ? "" : "  · server")}</size></color>";
                rows.Add((b, song));
            }
            noteText = songs.Count == 0
                ? (battle && !string.IsNullOrEmpty(battle.Status) ? battle.Status : "No songs yet: connect in Settings (left menu button)")
                : $"{songs.Count} beats";
            ShowNote();
            Highlight();
            UIKit.OnUILayer(gameObject);
        }

        void Pick(SongInfo s)
        {
            if (!battle) return;
            battle.songId = s.id;
            battle.useSong = true;
            Highlight();
            if (boombox) boombox.Toggle(s);
        }

        /// <summary>The note under the list: the boombox's preview while there is one, else how many beats.</summary>
        void ShowNote()
        {
            string t = boombox ? boombox.StatusText : null;
            if (t != null) t = $"<color=#33f2e6>{t}</color>";
            note.text = t ?? noteText;
        }

        void Highlight()
        {
            var sel = battle ? battle.SelectedSong : null;
            foreach (var (b, song) in rows)
                b.GetComponent<Image>().color = sel != null && song.id == sel.id ? new Color(0.1f, 0.45f, 0.42f, 1f) : UIKit.ButtonColour;
            picked.text = sel != null ? $"Next battle: <color=#33f2e6>{(string.IsNullOrEmpty(sel.title) ? sel.id : sel.title)}</color>" : "Next battle: metronome";
        }

        void LateUpdate()
        {
            // a battle can change the pick (the debug HUD): keep the list honest
            if (battle && rows.Count > 0 && Time.frameCount % 30 == 0) Highlight();
            if (note && Time.frameCount % 10 == 0) ShowNote();
        }
    }
}
