// The scoreboard over the DJ: the round, and the battle as a tug of war: a bar with you at the left end (turquoise) and
// the opponent at the right (orange, the colours of the floor monitor), its marker pulled toward whoever leads, by how
// much of all the points they're ahead (BattleRules.Pull), easing over as points land. The band in the middle is the
// dead zone: end the battle in it and it's a draw (BattleCoordinator.drawMargin). Under it the crowd's level as a row of
// lights. At the end, who won.
// Built at Awake as a board facing -Z (toward the room and the MCs), `size` wide and tall, centred on this transform;
// unlit, like an LED board.
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Freestyle.Battle
{
    public class Scoreboard : MonoBehaviour
    {
        public BattleCoordinator battle;
        public Vector2 size = new(2.4f, 1.2f);
        public Color yours = new(0.2f, 0.95f, 0.9f), theirs = new(1f, 0.55f, 0.1f), text = new(0.92f, 0.92f, 0.95f);
        public Color board = new(0.03f, 0.03f, 0.04f);
        [Tooltip("How fast the marker moves toward where the points put it (of the bar's half-length a second).")]
        public float pullSpeed = 0.6f;
        public TMP_FontAsset font;

        TextMeshPro round, youName, themName, banner, tie;
        readonly List<Renderer> crowdLights = new();
        MaterialPropertyBlock mpb;
        Renderer deadZone, fill, marker, knot;
        float shownPull, halfBar, barY, zoneShown = -1f;

        void Awake()
        {
            if (!battle) battle = FindAnyObjectByType<BattleCoordinator>();
            if (!font) font = TMP_Settings.defaultFontAsset;
            mpb = new MaterialPropertyBlock();
            Build();
        }

        void Build()
        {
            float w = size.x, h = size.y;
            var unlit = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "ScoreboardUnlit" };
            Quad("Board", Vector3.forward * 0.01f, new Vector2(w, h), unlit, board);
            Quad("Edge", Vector3.forward * 0.015f, new Vector2(w + 0.06f, h + 0.06f), unlit, new Color(0.12f, 0.12f, 0.14f));

            round = Text("Round", new Vector3(0, h * 0.37f, 0), 1.0f, text);
            banner = Text("Banner", new Vector3(0, h * 0.37f, 0), 1.1f, text);
            youName = Text("You", new Vector3(-w * 0.27f, h * 0.17f, 0), 1.3f, yours);
            themName = Text("Them", new Vector3(w * 0.27f, h * 0.17f, 0), 1.3f, theirs);
            Text("Crowd", new Vector3(-w * 0.41f, -h * 0.38f, 0), 0.6f, text).text = "CROWD";

            // the tug of war: the bar (each half tinted its side's colour, a cap in it at each end), the dead zone in
            // the middle, the fill from the middle to the marker, the rope through it all, the marker
            halfBar = w * 0.43f;
            barY = -h * 0.06f;
            float bh = h * 0.17f;
            Quad("Bar", new Vector3(0, barY, 0.008f), new Vector2(halfBar * 2 + 0.03f, bh + 0.03f), unlit, new Color(0.13f, 0.13f, 0.15f));
            Quad("Your Half", new Vector3(-halfBar / 2, barY, 0.007f), new Vector2(halfBar, bh), unlit, yours * 0.16f);
            Quad("Their Half", new Vector3(halfBar / 2, barY, 0.007f), new Vector2(halfBar, bh), unlit, theirs * 0.16f);
            Quad("Your End", new Vector3(-halfBar + 0.015f, barY, 0.0065f), new Vector2(0.03f, bh), unlit, yours);
            Quad("Their End", new Vector3(halfBar - 0.015f, barY, 0.0065f), new Vector2(0.03f, bh), unlit, theirs);
            deadZone = Quad("Dead Zone", new Vector3(0, barY, 0.006f), new Vector2(0.01f, bh), unlit, new Color(0.42f, 0.42f, 0.46f));
            fill = Quad("Pull", new Vector3(0, barY, 0.005f), new Vector2(0.001f, bh * 0.62f), unlit, text);
            Quad("Rope", new Vector3(0, barY, 0.004f), new Vector2(halfBar * 2, 0.012f), unlit, new Color(0.78f, 0.68f, 0.5f));
            marker = Quad("Marker", new Vector3(0, barY, 0.002f), new Vector2(0.035f, bh + 0.09f), unlit, text);
            knot = Quad("Knot", new Vector3(0, barY, 0.001f), new Vector2(0.07f, 0.07f), unlit, text);
            knot.transform.localRotation = Quaternion.Euler(0, 0, 45);
            tie = Text("Tie", new Vector3(0, barY - bh / 2 - 0.1f, 0), 0.5f, new Color(0.6f, 0.6f, 0.65f));
            tie.text = "TIE";

            // the crowd: 12 lights, one per level (0-11)
            int n = 12;
            float x0 = -w * 0.3f, x1 = w * 0.45f, lw = (x1 - x0) / n;
            for (int i = 0; i < n; i++)
            {
                var r = Quad("Crowd " + i, new Vector3(x0 + lw * (i + 0.5f), -h * 0.38f, 0), new Vector2(lw * 0.8f, h * 0.07f), unlit, Color.black);
                crowdLights.Add(r);
            }
        }

        Renderer Quad(string name, Vector3 at, Vector2 wh, Material mat, Color c)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(q.GetComponent<Collider>());
            q.name = name;
            q.transform.SetParent(transform, false);
            q.transform.localPosition = at;
            q.transform.localScale = new Vector3(wh.x, wh.y, 1);
            var r = q.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            mpb.SetColor("_BaseColor", c);
            r.SetPropertyBlock(mpb);
            return r;
        }

        TextMeshPro Text(string name, Vector3 at, float fontSize, Color c)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = at;
            var t = go.AddComponent<TextMeshPro>();
            if (font) t.font = font;
            t.fontSize = fontSize;
            t.fontStyle = FontStyles.Bold | FontStyles.UpperCase;
            t.alignment = TextAlignmentOptions.Center;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.rectTransform.sizeDelta = new Vector2(size.x * 0.5f, size.y * 0.4f);
            t.color = c;
            return t;
        }

        void Colour(Renderer r, Color c)
        {
            mpb.SetColor("_BaseColor", c);
            r.SetPropertyBlock(mpb);
        }

        void Update()
        {
            if (!battle) return;
            var st = battle.CurrentState;
            bool on = battle.Phase != BattlePhase.Idle;
            float you = on ? st?.score?.user ?? 0f : 0f, them = on ? st?.score?.opponent ?? 0f : 0f;
            float pull = battle.Rules != null ? battle.Rules.Pull : BattleRules.PullOf(you, them);
            if (battle.Phase == BattlePhase.Starting || !on) shownPull = 0f;
            shownPull = Mathf.MoveTowards(shownPull, pull, pullSpeed * Time.deltaTime);

            youName.text = "YOU";
            themName.text = on ? battle.OpponentName : "";

            // the dead zone, as wide as the draw margin makes it; the marker toward whoever leads (you: left)
            float margin = battle.Rules != null ? battle.Rules.DrawMargin : battle.drawMargin;
            if (!Mathf.Approximately(margin, zoneShown))
            {
                zoneShown = margin;
                var z = deadZone.transform.localScale;
                deadZone.transform.localScale = new Vector3(Mathf.Max(0.01f, margin * halfBar * 2), z.y, 1);
            }
            float x = -shownPull * halfBar;
            bool tied = Mathf.Abs(shownPull) <= margin;
            var lead = tied ? text : shownPull > 0 ? yours : theirs;
            var f = fill.transform;
            f.localPosition = new Vector3(x / 2, barY, f.localPosition.z);
            f.localScale = new Vector3(Mathf.Max(0.001f, Mathf.Abs(x)), f.localScale.y, 1);
            Colour(fill, lead * (tied ? 0.6f : 0.85f));
            foreach (var r in new[] { marker, knot })
            {
                r.transform.localPosition = new Vector3(x, barY, r.transform.localPosition.z);
                Colour(r, lead);
            }
            Colour(deadZone, tied && on ? new Color(0.75f, 0.75f, 0.8f) : new Color(0.32f, 0.32f, 0.36f));   // lit while you're in it

            bool over = battle.Phase == BattlePhase.Finished && battle.Result != null;
            banner.gameObject.SetActive(over);
            round.gameObject.SetActive(!over);
            if (over)
            {
                string w = battle.Result.winner;
                banner.text = w == "user" ? "YOU WIN" : w == "opponent" ? $"{battle.OpponentName} WINS" : "DRAW";
                banner.color = w == "user" ? yours : w == "opponent" ? theirs : text;
            }
            else round.text = on && battle.Round > 0 ? $"ROUND {battle.Round} / {battle.rounds}" : "FREESTYLE BATTLE";

            // the crowd's level, lit up to where it is (dim green to hot red), the rest dark
            float level = on ? battle.CrowdLevel : 0f;
            for (int i = 0; i < crowdLights.Count; i++)
            {
                var hot = Color.Lerp(new Color(0.2f, 0.9f, 0.3f), new Color(1f, 0.15f, 0.1f), i / (crowdLights.Count - 1f));
                mpb.SetColor("_BaseColor", i < Mathf.RoundToInt(level) + 1 && on ? hot : hot * 0.12f);
                crowdLights[i].SetPropertyBlock(mpb);
            }
        }
    }
}
