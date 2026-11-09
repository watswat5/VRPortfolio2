// The player's floor monitor (PlayerMonitor/Screen) shows how long is left, big and plain:
//   your turn: turquoise, the bars you have left (on the count-in, the beats until you start); the last bar flashes
//   between: white, the MC getting ready (after your turn, before its verse): the bars (in the last one, the beats)
//            until it starts, or "GETTING READY" with a beat pulse when it's running late, so the pause reads as meant
//   their turn: orange, the bars until it's yours
// A big number, a word or two above it, and a row of blocks, one per bar, lit for the bars left. Dark otherwise.
// It draws on top of the screen (the screen quad's forward points into it), sized to its 0.53 x 0.30 m.
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Freestyle.Battle
{
    public class TurnDisplay : MonoBehaviour
    {
        public BattleCoordinator battle;
        [Tooltip("The monitor's screen: a quad facing the MC, its forward into the screen (the venue's PlayerMonitor/Screen).")]
        public Transform screen;
        public Vector2 screenSize = new(0.53f, 0.30f);
        public Color yours = new(0.2f, 0.95f, 0.9f), theirs = new(1f, 0.55f, 0.1f), between = new(0.92f, 0.92f, 0.96f);
        [Range(0, 1)] public float unlit = 0.12f;   // how bright a spent bar's block is
        public TMP_FontAsset font;

        GameObject root;
        TextMeshPro number, label;
        readonly List<Renderer> blocks = new();
        MaterialPropertyBlock mpb;
        Material blockMat;
        int opponentBars = 8;
        int gapBars = 1;   // the wait for the MC, in bars, from when it began (the blocks' count)
        bool waiting;

        void Awake()
        {
            if (!battle) battle = FindAnyObjectByType<BattleCoordinator>();
            if (!font) font = TMP_Settings.defaultFontAsset;
            mpb = new MaterialPropertyBlock();
            if (screen) Build();
        }

        void OnEnable() { if (battle) battle.TurnStarted += OnTurn; }
        void OnDisable() { if (battle) battle.TurnStarted -= OnTurn; }

        void OnTurn(TurnMsg t) { if (t.turn == "opponent" && t.bars > 0) opponentBars = t.bars; }

        void Build()
        {
            root = new GameObject("Turn Display");
            root.transform.SetParent(screen, false);
            root.transform.localPosition = new Vector3(0, 0, -0.003f);   // just in front of the glass
            float w = screenSize.x, h = screenSize.y;
            label = Text("Label", new Vector3(0, h * 0.34f, 0), 0.33f);
            number = Text("Number", new Vector3(0, h * 0.02f, 0), 1.2f);
            blockMat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "TurnDisplayBlock" };
            root.SetActive(false);
        }

        TextMeshPro Text(string name, Vector3 at, float size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = at;
            var t = go.AddComponent<TextMeshPro>();
            if (font) t.font = font;
            t.fontSize = size;
            t.fontStyle = FontStyles.Bold;
            t.alignment = TextAlignmentOptions.Center;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.rectTransform.sizeDelta = new Vector2(screenSize.x, screenSize.y * 0.5f);
            return t;
        }

        /// <summary>One block per bar along the bottom (remade when the count changes).</summary>
        void Blocks(int n)
        {
            if (blocks.Count == n) return;
            foreach (var b in blocks) if (b) Destroy(b.gameObject);
            blocks.Clear();
            float w = screenSize.x * 0.9f, gap = 0.008f, bw = (w - gap * (n - 1)) / Mathf.Max(1, n), bh = screenSize.y * 0.14f;
            for (int i = 0; i < n; i++)
            {
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Destroy(q.GetComponent<Collider>());
                q.name = "Bar " + (i + 1);
                q.transform.SetParent(root.transform, false);
                q.transform.localPosition = new Vector3(-w / 2 + bw / 2 + i * (bw + gap), -screenSize.y * 0.36f, 0);
                q.transform.localScale = new Vector3(bw, bh, 1);
                var r = q.GetComponent<Renderer>();
                r.sharedMaterial = blockMat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                blocks.Add(r);
            }
        }

        void Update()
        {
            if (!root || !battle) return;
            bool show = true;
            Color c = yours;
            int bars = 0, left = 0;
            string big = "", small = "";
            bool flash = false;
            switch (battle.Phase)
            {
                case BattlePhase.UserTurn:
                    waiting = false;
                    bars = Mathf.Max(1, battle.UserTurnBars);
                    if (battle.CountInBeatsLeft > 0)
                    {
                        left = bars;
                        big = battle.CountInBeatsLeft.ToString();
                        small = "YOUR TURN IN";
                    }
                    else
                    {
                        left = Mathf.Clamp(bars - Mathf.Max(1, battle.CurrentBar) + 1, 0, bars);
                        big = left.ToString();
                        small = left == 1 ? "LAST BAR" : "BARS LEFT";
                        flash = left == 1;
                    }
                    break;
                case BattlePhase.Judging:
                case BattlePhase.OpponentTurn when battle.VerseWaiting:
                    c = between;
                    double at = battle.OpponentStartsAt, barS = battle.BarSeconds;
                    if (double.IsNaN(at) || barS <= 0)
                    {
                        // no estimate: the MC is still writing; pulse on the beat so it reads as alive
                        bars = left = 1;
                        big = "...";
                        small = "MC GETTING READY";
                        flash = true;
                        waiting = false;
                    }
                    else
                    {
                        double s = Math.Max(0, at - battle.Now);
                        int barsLeft = (int)Math.Ceiling(s / barS - 1e-3);
                        if (!waiting) { gapBars = Mathf.Max(1, barsLeft); waiting = true; }
                        bars = Mathf.Max(gapBars, barsLeft);
                        left = barsLeft;
                        if (barsLeft <= 1)   // the last bar: count the beats in, like yours
                        {
                            big = Math.Max(1, (int)Math.Ceiling(s / battle.BeatSeconds - 1e-3)).ToString();
                            small = "MC STARTS IN";
                        }
                        else
                        {
                            big = barsLeft.ToString();
                            small = "BARS TILL MC";
                        }
                    }
                    break;
                case BattlePhase.OpponentTurn:
                    waiting = false;
                    c = theirs;
                    {
                        bars = Mathf.Max(1, battle.TurnBars);
                        left = Mathf.Clamp(bars - Mathf.Max(1, battle.CurrentBar) + 1, 0, bars);
                    }
                    big = left.ToString();
                    small = "BARS TILL YOUR TURN";
                    break;
                default:
                    waiting = false;
                    show = false;
                    break;
            }
            if (root.activeSelf != show) root.SetActive(show);
            if (!show) return;

            float on = flash && battle.BeatPhase > 0.5f ? 0.35f : 1f;   // the last bar blinks on the beat
            number.text = big; number.color = c * on;
            label.text = small; label.color = c;
            Blocks(bars);
            for (int i = 0; i < blocks.Count; i++)
            {
                bool lit = i >= bars - left;   // spent bars go dark from the left
                mpb.SetColor("_BaseColor", lit ? c * on : c * unlit);
                blocks[i].SetPropertyBlock(mpb);
            }
        }
    }
}
