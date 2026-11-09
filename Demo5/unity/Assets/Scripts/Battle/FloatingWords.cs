// Words to work into your verse, floating over the crowd: there from the start of the battle (from the opponent's
// topics), joined by a keyword from each line of the opponent's verse as it's rapped (the end of the line first: the
// word worth flipping back), so there's something to go off when your turn starts, and by the assist's word drops at
// the start of your turn. Each keyword brings a word that rhymes with it (the server's rhymes, GET /rhymes) in its
// colour, a little smaller, close by. At most maxWords keywords (so twice as many words): a new one sends the oldest
// pair away (word drops last). Each pair its own colour, at different depths in the box (this transform is its centre,
// `size` its extent), placed so that from where you stand no word overlaps another, across or up and down. They drift a
// little. Say a word during your turn and it pops (its partner stays); the word drops you didn't use go when your turn
// ends (with their rhymes), everything when the battle does.
// They can be turned off in the options menu (On, remembered): none show, and none are fetched.
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;

namespace Freestyle.Battle
{
    public class FloatingWords : MonoBehaviour
    {
        public BattleCoordinator battle;
        [Tooltip("The space the words float in, centred on this transform.")]
        public Vector3 size = new(6f, 1.1f, 4f);
        [Tooltip("Keywords at once; each brings a rhyme, so twice as many words.")]
        [Range(1, 8)] public int maxWords = 5;
        [Tooltip("Pair each keyword with a word that rhymes with it (from the server).")]
        public bool rhymes = true;
        [Tooltip("A rhyme's size next to its keyword's.")]
        [Range(0.5f, 1f)] public float rhymeSize = 0.85f;
        [Tooltip("How far from its keyword a rhyme may float, metres.")]
        public float rhymeReach = 0.9f;
        [Tooltip("Words from the opponent's topics at the start of the battle.")]
        [Range(0, 8)] public int startWords = 3;
        [Tooltip("How tall a word looks from where you stand, degrees (random in this range).")]
        public Vector2 wordAngle = new(1.8f, 2.4f);
        [Tooltip("Gap kept between words as you see them, degrees.")]
        public float gapAngle = 1.1f;
        public Color[] colours =
        {
            new(1f, 0.85f, 0.2f), new(0.3f, 0.95f, 1f), new(1f, 0.35f, 0.75f),
            new(0.55f, 1f, 0.35f), new(1f, 0.55f, 0.2f), new(0.75f, 0.55f, 1f),
        };
        public TMP_FontAsset font;

        [Header("Motion")]
        public float drift = 0.05f;        // metres
        public float driftSpeed = 0.35f;   // cycles per second, about
        public float popTime = 0.3f, appearTime = 0.35f, fadeTime = 0.6f;
        [Tooltip("Seconds between words coming in together (the battle's first ones, the word drops).")]
        public float stagger = 0.2f;

        class Word
        {
            public string text;
            public TextMeshPro tmp;
            public Vector3 home;
            public Rect view;            // where it is as you see it: yaw/pitch degrees
            public Rect room;            // a keyword waiting for its rhyme: its view widened, kept clear for the rhyme
            public float phase, speed, scale, born;
            public bool drop, leaving, rhyme;
            public Word partner;          // a keyword's rhyme, a rhyme's keyword (null once the other has gone)
        }

        static readonly Regex Letters = new("[a-z']+");
        static readonly HashSet<string> Skip = new((
            "a an the and or but if so as at by for from in into of off on onto out over to up with without about " +
            "above below under i me my mine you your yours he him his she her hers it its we us our they them their " +
            "this that these those there here what who whom which when where why how all any both each few more most " +
            "other some such no nor not only own same than too very can will just dont don't should now is are was were " +
            "be been being have has had having do does did doing am i'm im you're youre it's that's thats ain't aint " +
            "gonna wanna gotta yeah yo uh huh like got get gets getting go goes going went come came make made take took " +
            "know think say said tell told see seen look want need still even ever never always back down then well also " +
            "cause 'cause cuz could would might must shall i'll ill i've ive you'll youll we're they're theyre let's lets " +
            "let one two three thing things stuff really right okay man real way time everyone everybody someone anyone " +
            "nobody something nothing everything anything").Split(' '));
        static readonly HashSet<string> Crude = new(("dick dicks pussy cock cunt whore slut bitch bitches fuck fucker " +
            "fucking shit shitty ass asshole bastard damn hell piss tits fag nigga nigger").Split(' '));

        readonly List<Word> words = new();
        readonly Queue<(string text, bool drop, int tries)> coming = new();
        readonly HashSet<string> said = new();
        float nextIn;

        const string Pref = "Freestyle.FloatingWords";

        /// <summary>The floating words are shown (the options menu; remembered). Off: the ones up go, no more come.</summary>
        public static bool On
        {
            get => PlayerPrefs.GetInt(Pref, 1) == 1;
            set
            {
                PlayerPrefs.SetInt(Pref, value ? 1 : 0);
                foreach (var f in FindObjectsByType<FloatingWords>(FindObjectsInactive.Include, FindObjectsSortMode.None)) f.Apply();
            }
        }

        void Awake()
        {
            if (!battle) battle = FindAnyObjectByType<BattleCoordinator>();
            if (!font) font = TMP_Settings.defaultFontAsset;
        }

        void Start() => Apply();

        void Apply()
        {
            if (!On)
            {
                coming.Clear();
                foreach (var w in words.ToList()) StartCoroutine(Leave(w, false));   // (coroutines run on while disabled)
            }
            enabled = On;
        }

        void OnEnable()
        {
            if (!battle) return;
            battle.Ready += OnReady;
            battle.PhaseChanged += OnPhase;
            battle.TurnStarted += OnTurn;
            battle.OpponentLineStarted += OnLine;
            battle.WordDropsReceived += OnDrops;
            battle.WordDropUsed += Pop;
            battle.TranscriptChanged += OnTranscript;
        }

        void OnDisable()
        {
            if (!battle) return;
            battle.Ready -= OnReady;
            battle.PhaseChanged -= OnPhase;
            battle.TurnStarted -= OnTurn;
            battle.OpponentLineStarted -= OnLine;
            battle.WordDropsReceived -= OnDrops;
            battle.WordDropUsed -= Pop;
            battle.TranscriptChanged -= OnTranscript;
        }

        // ---------------------------------------------------------------- the battle

        /// <summary>The battle's on: a few words from what the opponent raps about (the last word of each topic).</summary>
        void OnReady(ReadyMsg r)
        {
            var mc = r?.mc ?? battle.Opponent;
            if (mc == null) return;
            var topics = Shuffle(mc.topics.Select(t => KeywordsIn(t).LastOrDefault()).Where(w => w != null).ToList());
            foreach (var w in topics.Distinct().Take(startWords)) coming.Enqueue((w, false, 0));
        }

        void OnPhase(BattlePhase p)
        {
            switch (p)
            {
                case BattlePhase.Starting:   // a new battle: a clean slate
                    coming.Clear();
                    foreach (var w in words.ToList()) StartCoroutine(Leave(w, false));
                    break;
                case BattlePhase.Judging:    // your turn's over: the drops you didn't use go (the rest stay to work with)
                    foreach (var w in words.Where(w => w.drop).ToList()) StartCoroutine(Leave(w, false));
                    break;
                case BattlePhase.Idle:
                case BattlePhase.Finished:
                    coming.Clear();
                    foreach (var w in words.ToList()) StartCoroutine(Leave(w, false));
                    break;
            }
        }

        void OnTurn(TurnMsg t) { if (t.turn == "user") said.Clear(); }

        /// <summary>The opponent raps a line: its best word comes in (its last keyword, or else its longest).</summary>
        void OnLine(int index, string line)
        {
            var ks = KeywordsIn(line).Where(k => !Showing(k)).ToList();
            if (ks.Count == 0) return;
            var end = KeywordsIn(line).LastOrDefault();
            coming.Enqueue((end != null && !Showing(end) ? end : ks.OrderByDescending(k => k.Length).First(), false, 0));
        }

        void OnDrops(WordDropsMsg d)
        {
            foreach (var w in d.words.Select(w => w.ToLowerInvariant()))
            {
                var there = words.FirstOrDefault(x => !x.leaving && x.text == w);
                if (there != null) there.drop = true;   // already up: now it's worth a bonus too
                else coming.Enqueue((w, true, 0));
            }
        }

        void OnTranscript(TranscriptMsg t)
        {
            if (battle.Phase != BattlePhase.UserTurn) return;
            foreach (var w in t.committed) said.Add(w.w.ToLowerInvariant().Trim('.', ',', '!', '?', '"'));
            foreach (Match m in Letters.Matches((battle.PartialText ?? "").ToLowerInvariant())) said.Add(m.Value);
            foreach (var w in words.ToList())
                if (!w.leaving && said.Any(s => Says(w.text, s))) Pop(w.text);
        }

        /// <summary>The word, or a plural or possessive of it (as the server counts a word drop).</summary>
        static bool Says(string word, string heard) =>
            heard == word || heard == word + "s" || heard == word + "es" || heard == word + "'s" || heard.TrimEnd('s') == word;

        // ---------------------------------------------------------------- the words

        static IEnumerable<string> KeywordsIn(string text) =>
            Letters.Matches((text ?? "").ToLowerInvariant()).Select(m => m.Value.Trim('\'')).Where(Keyword);

        static bool Keyword(string w) => w.Length >= 4 && !Skip.Contains(w) && !Crude.Contains(w);

        static List<string> Shuffle(List<string> l) => l.OrderBy(_ => Random.value).ToList();

        bool Showing(string text) => words.Any(w => !w.leaving && w.text == text) || coming.Any(c => c.text == text);

        Vector3 Eye => Camera.main ? Camera.main.transform.position : transform.position + Vector3.forward * 5f;

        /// <summary>Brings in a keyword, sending the oldest pair away if there are already maxWords (word drops last), and
        /// asks for its rhyme. False when there's no room for it right now (try again in a moment).</summary>
        bool Add(string text, bool drop)
        {
            if (words.Any(w => !w.leaving && w.text == text)) return true;
            var keys = words.Where(w => !w.leaving && !w.rhyme).ToList();
            var gone = new HashSet<Word>();   // on their way out to make room: not in the new word's way
            if (keys.Count >= maxWords)
            {
                var victim = keys.OrderBy(w => w.drop).ThenBy(w => w.born).First();
                if (victim.drop && !drop) return true;   // the drops are worth more: a keyword doesn't push one out
                gone.Add(victim);
                if (victim.partner != null) gone.Add(victim.partner);
            }
            // rhymes left over from popped keywords make room too, oldest first, so it stays about 2 x maxWords
            foreach (var orphan in words.Where(w => !w.leaving && w.rhyme && w.partner == null).OrderBy(w => w.born))
                if (words.Count(w => !w.leaving && !gone.Contains(w)) >= maxWords * 2) gone.Add(orphan);

            var used = words.Where(w => !w.leaving && !gone.Contains(w)).Select(w => w.tmp.color).ToList();
            var free = colours.Where(c => !used.Contains(c)).ToList();
            var colour = free.Count > 0 ? free[Random.Range(0, free.Count)] : colours[Random.Range(0, colours.Length)];
            var w = Spawn(text, colour, 1f, null, gone);
            if (w == null) return false;
            w.drop = drop;
            foreach (var g in gone) StartCoroutine(Leave(g, false));
            if (rhymes) FetchRhyme(w);
            return true;
        }

        /// <summary>Asks the server for words that rhyme with the keyword and floats the best one not up yet beside it.</summary>
        async void FetchRhyme(Word key)
        {
            if (!battle) return;
            List<string> found;
            try { found = await battle.Rhymes(key.text); }
            catch (System.Exception e) { Debug.LogWarning($"[FloatingWords] no rhymes for {key.text}: {e.Message}"); return; }
            if (!this || key.leaving || !key.tmp || key.partner != null) return;
            var pick = found.Select(r => r.ToLowerInvariant())
                            .FirstOrDefault(r => r.Length >= 3 && r != key.text && !Crude.Contains(r) && !Skip.Contains(r)
                                                 && !words.Any(o => !o.leaving && o.text == r));
            if (pick == null) return;
            Word rhyme = null;
            for (int attempt = 0; attempt < 4 && rhyme == null; attempt++)
            {
                if (attempt > 0) await Awaitable.WaitForSecondsAsync(fadeTime);   // no room yet: the pair it replaced is still fading
                if (!this || key.leaving || !key.tmp || key.partner != null) return;
                rhyme = Spawn(pick, key.tmp.color, rhymeSize, key, null);
            }
            if (rhyme == null) return;
            rhyme.rhyme = true;
            rhyme.partner = key;
            key.partner = rhyme;
        }

        /// <summary>A word up in the box: its text, colour and size (a share of wordAngle), near `near` when given, out of
        /// the way of every other word but `ignore`. Null when there's no room.</summary>
        Word Spawn(string text, Color colour, float size, Word near, ICollection<Word> ignore)
        {
            var go = new GameObject("Word " + text);
            go.transform.SetParent(transform, false);
            var tmp = go.AddComponent<TextMeshPro>();
            if (font) tmp.font = font;
            tmp.text = text;
            tmp.fontStyle = FontStyles.Bold | FontStyles.UpperCase;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.fontSize = 10;
            tmp.outlineWidth = 0.2f;
            tmp.outlineColor = new Color32(0, 0, 0, 255);
            tmp.color = colour;
            tmp.ForceMeshUpdate();
            Vector2 unit = tmp.GetPreferredValues(text.ToUpperInvariant());   // at fontSize 10, scale 1

            Vector3 home = default; float scale = 0; Rect view = default;
            bool placed = near != null && Place(unit, size, near, ignore, out home, out scale, out view);   // beside its keyword
            if (!placed && !Place(unit, size, null, ignore, out home, out scale, out view)) { Destroy(go); return null; }   // else anywhere
            var w = new Word { text = text, tmp = tmp, home = home, view = view, room = near == null ? Widened(view) : default, scale = scale, born = Time.time,
                               phase = Random.value * 100f, speed = driftSpeed * Random.Range(0.75f, 1.25f) };
            go.transform.position = home;
            go.transform.localScale = Vector3.zero;
            words.Add(w);
            StartCoroutine(Appear(w));
            return w;
        }

        /// <summary>A spot in the box (near `near`, within rhymeReach, when given), and a scale for the word there, where (as
        /// you see it) it doesn't overlap another.</summary>
        bool Place(Vector2 unit, float size, Word near, ICollection<Word> ignore, out Vector3 home, out float scale, out Rect view)
        {
            Vector3 eye = Eye;
            var ahead = Vector3.ProjectOnPlane(transform.position - eye, Vector3.up);
            var look = Quaternion.Inverse(Quaternion.LookRotation(ahead.sqrMagnitude > 1e-4f ? ahead : Vector3.forward));
            float driftDeg = 0f;
            Vector3 side = Vector3.Cross(Vector3.up, ahead.sqrMagnitude > 1e-4f ? ahead.normalized : Vector3.forward);
            // beside its keyword first (right, left, below, above, at its depth), then anywhere within reach of it
            var beside = new List<Vector3>();
            if (near != null && near.tmp)
            {
                var kb = near.tmp.textBounds.size * near.scale;                   // the keyword, in metres
                float rw = unit.x / Mathf.Max(0.01f, unit.y) * kb.y * size;     // the rhyme, as tall as the keyword times size
                // the gap the overlap check wants between them (gapAngle and both drifts), in metres at that distance
                float d = Vector3.Distance(eye, near.home), dDeg = Mathf.Atan2(drift, d) * Mathf.Rad2Deg;
                float gap = d * Mathf.Tan((gapAngle + 4f * dDeg + 0.3f) * Mathf.Deg2Rad);
                float across = kb.x / 2f + rw / 2f + gap, upDown = kb.y * (1f + size) / 2f + gap;
                foreach (var (x, y) in new[] { (1f, 0f), (-1f, 0f), (0f, -1f), (0f, 1f), (0.6f, -1f), (-0.6f, -1f), (0.6f, 1f), (-0.6f, 1f) })
                    beside.Add(near.home + side * (x * across) + Vector3.up * (y * upDown));
            }
            for (int tries = 0; tries < 80; tries++)
            {
                home = tries < beside.Count ? beside[tries]
                    : near != null
                    ? near.home + side * Random.Range(-rhymeReach, rhymeReach) + Vector3.up * Random.Range(-0.5f, 0.5f) * rhymeReach
                      + ahead.normalized * Random.Range(-0.3f, 0.3f) * rhymeReach
                    : transform.TransformPoint(new Vector3(Random.Range(-0.5f, 0.5f) * this.size.x, Random.Range(-0.5f, 0.5f) * this.size.y,
                                                           Random.Range(-0.5f, 0.5f) * this.size.z));
                // as you see it: angles from the line to the box's middle (so nothing wraps round at 180 degrees)
                Vector3 to = look * (home - eye);
                float dist = to.magnitude;
                if (dist < 1f) continue;
                // sized to look wordAngle tall from here, whatever its depth
                float angle = near != null ? Mathf.Clamp(near.view.height - gapAngle, wordAngle.x, wordAngle.y) : Random.Range(wordAngle.x, wordAngle.y);
                float tall = 2f * dist * Mathf.Tan(0.5f * angle * size * Mathf.Deg2Rad);
                scale = tall / Mathf.Max(0.01f, unit.y);
                float wide = unit.x * scale;
                // the whole word in the box, not just its middle (a long word near a side would stick into the walls)
                Vector3 local = transform.InverseTransformPoint(home);
                float halfReach = (wide / 2f + drift) / Mathf.Max(0.01f, transform.lossyScale.x);
                if (Mathf.Abs(local.x) + halfReach > this.size.x / 2f || Mathf.Abs(local.z) + halfReach > this.size.z / 2f
                    || Mathf.Abs(local.y) > this.size.y / 2f) continue;
                driftDeg = Mathf.Atan2(drift, dist) * Mathf.Rad2Deg;
                float yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
                float pitch = Mathf.Atan2(to.y, new Vector2(to.x, to.z).magnitude) * Mathf.Rad2Deg;
                float w = 2f * Mathf.Atan2(wide / 2f, dist) * Mathf.Rad2Deg + 2f * driftDeg + gapAngle;
                float h = 2f * Mathf.Atan2(tall / 2f, dist) * Mathf.Rad2Deg + 2f * driftDeg + gapAngle;
                view = new Rect(yaw - w / 2f, pitch - h / 2f, w, h);
                // a keyword keeps room beside it for its rhyme (half as wide again either side), until the rhyme is there
                var v = near == null && rhymes ? Widened(view) : view;
                if (words.Any(o => o.tmp && (ignore == null || !ignore.Contains(o)) && Footprint(o, near).Overlaps(v))) continue;   // (one fading out still counts)
                // in plain sight: nothing solid (a wall, the stage) between you and its middle or either end
                Vector3 across = Vector3.Cross(Vector3.up, home - eye).normalized * (wide / 2f);
                if (Blocked(eye, home) || Blocked(eye, home + across) || Blocked(eye, home - across)) continue;
                return true;
            }
            home = default; scale = 0; view = default;
            return false;
        }

        static Rect Widened(Rect r) => new(r.x - r.width * 0.45f, r.y, r.width * 1.9f, r.height);

        /// <summary>The space a word takes as you see it: a keyword still waiting for its rhyme holds the room beside it too
        /// (except from that rhyme, which goes there).</summary>
        Rect Footprint(Word o, Word placing) =>
            rhymes && !o.rhyme && o.partner == null && !o.leaving && o != placing && o.room.width > 0 ? o.room : o.view;

        static bool Blocked(Vector3 from, Vector3 to) => Physics.Linecast(from, to, out var hit, ~0, QueryTriggerInteraction.Ignore)
                                                           && !hit.collider.GetComponentInParent<Player.XRMovement>();

        void Update()
        {
            if (coming.Count > 0 && Time.time >= nextIn)   // one at a time, so they don't all land at once
            {
                var (text, drop, tries) = coming.Dequeue();
                bool placed = Add(text, drop);
                if (!placed && tries < 6) coming.Enqueue((text, drop, tries + 1));   // no room yet: once the others have faded
                nextIn = Time.time + (placed ? stagger : fadeTime / 2f);
            }
            Vector3 eye = Eye;
            foreach (var w in words)
            {
                if (!w.tmp) continue;
                float t = Time.time * w.speed * Mathf.PI * 2f + w.phase;
                var offset = new Vector3(Mathf.Sin(t), Mathf.Sin(t * 0.77f + 1.3f) * 0.7f, Mathf.Sin(t * 0.53f + 2.1f) * 0.5f) * drift;
                var tr = w.tmp.transform;
                tr.position = w.home + offset;
                // facing you, tipping a little as it drifts
                tr.rotation = Quaternion.LookRotation(tr.position - eye) * Quaternion.Euler(0, 0, Mathf.Sin(t * 0.6f) * 3f);
            }
        }

        IEnumerator Appear(Word w)
        {
            for (float t = 0; t < appearTime && w.tmp && !w.leaving; t += Time.deltaTime)
            {
                float k = t / appearTime, s = 1f + 2.7f * Mathf.Pow(k - 1f, 3) + 1.7f * Mathf.Pow(k - 1f, 2);   // ease out, a little overshoot
                w.tmp.transform.localScale = Vector3.one * (w.scale * s);
                yield return null;
            }
            if (w.tmp && !w.leaving) w.tmp.transform.localScale = Vector3.one * w.scale;
        }

        /// <summary>The word was said: it pops (swells and vanishes).</summary>
        public void Pop(string text)
        {
            text = text.ToLowerInvariant();
            foreach (var w in words.Where(w => !w.leaving && w.text == text).ToList()) StartCoroutine(Leave(w, true));
        }

        IEnumerator Leave(Word w, bool pop)
        {
            if (w.leaving) yield break;
            w.leaving = true;
            var partner = w.partner;
            if (partner != null)
            {
                partner.partner = null;   // (unlinked first: the partner's own Leave runs at once, below)
                w.partner = null;
                // a keyword sent away (or faded at the end of a turn) takes its rhyme along; one that was said leaves it up
                if (!pop && !w.rhyme) StartCoroutine(Leave(partner, false));
            }
            float time = pop ? popTime : fadeTime, from = w.tmp ? w.tmp.transform.localScale.x : 0f;
            Color c = w.tmp ? w.tmp.color : Color.white;
            for (float t = 0; t < time && w.tmp; t += Time.deltaTime)
            {
                float k = t / time;
                w.tmp.transform.localScale = Vector3.one * from * (pop ? 1f + 0.6f * k : 1f - 0.3f * k);
                w.tmp.color = new Color(c.r, c.g, c.b, (pop ? 1f - k * k : 1f - k) * c.a);
                if (pop) w.home += Vector3.up * (Time.deltaTime * 0.6f);
                yield return null;
            }
            if (w.tmp) Destroy(w.tmp.gameObject);
            words.Remove(w);
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.8f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(Vector3.zero, size);
        }
    }
}
