// A spray can's sounds (SprayCan adds it), from Resources/SprayCan (made by art/spraycan/make_sounds.py):
// - the rattle: an invisible mixing ball loose inside the can (a cylinder: its travel up and down, a little room round
//   the axis), in thick paint. It falls under gravity and the can moves round it (where the can is each frame, in
//   small steps between, so a shake or a twist of the wrist throws it about); the paint, moving with the can, drags
//   on it. It clacks (one of the recorded clacks, louder the faster) when it hits the top or the bottom, and ticks
//   (lighter) off the side, higher toward the top of the can and lower toward the bottom. The bottom is domed, so a hit there knocks it sideways: it skitters off the walls
//   after each stroke, as in a real can. Shaken hard it clacks at both ends of each stroke (the top only when the can
//   drops faster than gravity), gently only at the bottom; tipped slowly it just rolls.
//   The steps are simulated for the frame just gone, so each click is scheduled at its own moment in it (a frame late).
// - the hiss: a looped mist while the can sprays, faded in and out.
// - the thunk: the can landing or hitting something while not held, louder the harder.
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Freestyle.Wardrobe
{
    [RequireComponent(typeof(SprayCan))]
    public class SprayCanSounds : MonoBehaviour
    {
        [Header("Rattle (the mixing ball)")]
        [Range(0, 1)] public float rattleVolume = 0.8f;
        [Tooltip("How far the ball can travel inside, end to end (m).")]
        public float travel = 0.12f;
        [Tooltip("The middle of the ball's travel, up the can from its base (m).")]
        public float middle = 0.1f;
        [Tooltip("How far the ball can move off the axis before it touches the side (m): the can's inside radius less the " +
                 "ball's.")]
        public float sideRoom = 0.022f;
        [Tooltip("The paint's drag on the ball (1/s), against how it moves through the paint (which moves with the can).")]
        public float paintDrag = 2f;
        [Tooltip("The paint's friction on the ball (m/s²): it slows by this much more, whatever its speed.")]
        public float paintFriction = 2f;
        [Tooltip("How much of its speed the ball keeps bouncing off an end, and off the side.")]
        [Range(0, 1)] public float endBounce = 0.45f;
        [Range(0, 1)] public float sideBounce = 0.4f;
        [Tooltip("The domed bottom: the share of a bottom hit's speed that's knocked sideways.")]
        [Range(0, 1)] public float dome = 0.35f;
        [Tooltip("Hits slower than this make no sound (m/s); at Loud Hit and above, full volume.")]
        public float quietHit = 0.1f;
        public float loudHit = 2f;
        [Tooltip("Side hits against end hits: lighter.")]
        public float sideVolume = 0.5f;
        [Tooltip("Where it hits sets the pitch: the top (the shoulder) higher, the bottom lower; the side from Side Pitch " +
                 "Low at the bottom of its travel to Side Pitch High at the top.")]
        public float topPitch = 1.2f;
        public float bottomPitch = 0.85f;
        public float sidePitchLow = 1.0f;
        public float sidePitchHigh = 1.35f;
        [Header("Spray")]
        [Range(0, 1)] public float sprayVolume = 0.35f;
        [Header("Thunk (hitting things when not held)")]
        [Range(0, 1)] public float thunkVolume = 0.9f;
        [Tooltip("Impacts slower than this make no sound (m/s).")]
        public float quietImpact = 0.4f;

        static AudioClip[] rattles;
        static AudioClip spray, thunk;

        SprayCan can;
        XRGrabInteractable grab;
        Rigidbody rb;
        AudioSource shots, hiss;
        readonly AudioSource[] clicks = new AudioSource[6];   // round robin: each click its own pitch, at its own time
        int nextClick;
        Vector3 ballPos, ballVel;   // world
        Vector3 ballLocal;          // in the can (its base the origin): kept while the can rests, or jumps
        Vector3 lastPos;
        Quaternion lastRot;
        bool known;                 // lastPos/lastRot are last frame's (false: the motion starts again from here)
        double stepTime;            // dsp time of the step being simulated
        double quietUntil;          // two clicks closer than this are one
        int lastRattle = -1;
        float nextThunk;

        void Awake()
        {
            can = GetComponent<SprayCan>();
            grab = GetComponent<XRGrabInteractable>();
            rb = GetComponent<Rigidbody>();
            if (rattles == null)
            {
                rattles = System.Array.FindAll(Resources.LoadAll<AudioClip>("SprayCan"), c => c.name.StartsWith("rattle"));
                spray = Resources.Load<AudioClip>("SprayCan/spray");
                thunk = Resources.Load<AudioClip>("SprayCan/thunk");
            }
            shots = Source(gameObject);
            var nozzle = can.nozzle ? can.nozzle : transform;
            hiss = Source(new GameObject("Hiss"));
            hiss.transform.SetParent(nozzle, false);
            hiss.clip = spray;
            hiss.loop = true;
            hiss.volume = 0;
            for (int i = 0; i < clicks.Length; i++) clicks[i] = Source(gameObject);
            ballLocal = new Vector3(0, middle - travel / 2, 0);   // resting at the bottom
        }

        static AudioSource Source(GameObject go)
        {
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false;
            s.spatialBlend = 1;
            s.minDistance = 0.4f;
            s.maxDistance = 15;
            s.dopplerLevel = 0;
            return s;
        }

        void OnEnable()
        {
            grab.selectEntered.AddListener(OnGrabbed);
            can.Respawned += ResetMotion;
            ResetMotion();
        }

        void OnDisable()
        {
            grab.selectEntered.RemoveListener(OnGrabbed);
            can.Respawned -= ResetMotion;
            hiss.Stop();
        }

        // the grab snaps the can into the hand in one frame: not a shake
        void OnGrabbed(UnityEngine.XR.Interaction.Toolkit.SelectEnterEventArgs e) => ResetMotion();

        void ResetMotion() => known = false;

        void Update()
        {
            Hiss();
            float dt = Time.deltaTime;
            if (dt <= 0) return;
            Vector3 pos = transform.position;
            Quaternion rot = transform.rotation;
            bool resting = !grab.isSelected && rb.IsSleeping();
            if (!known || resting || (pos - lastPos).sqrMagnitude > 12f * 12f * dt * dt)   // (a teleport, not a motion)
            {
                // the ball stays where it was in the can, moving with it
                ballPos = pos + rot * ballLocal;
                ballVel = Vector3.zero;
                lastPos = pos; lastRot = rot; known = true;
                return;
            }
            int steps = Mathf.CeilToInt(dt / 0.003f);
            float h = dt / steps;
            double now = AudioSettings.dspTime;
            Vector3 p0 = lastPos;
            Quaternion r0 = lastRot;
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                Vector3 p1 = Vector3.Lerp(lastPos, pos, t);
                Quaternion r1 = Quaternion.Slerp(lastRot, rot, t);
                stepTime = now + t * dt;   // the frame just gone, replayed a frame late
                Step(p0, r0, p1, r1, h);
                p0 = p1; r0 = r1;
            }
            lastPos = pos; lastRot = rot;
        }

        /// <summary>One step of the ball, the can moving from pose 0 to pose 1.</summary>
        void Step(Vector3 p0, Quaternion r0, Vector3 p1, Quaternion r1, float h)
        {
            // the paint's velocity where the ball is: how that point of the can moves over the step
            Vector3 here = Quaternion.Inverse(r0) * (ballPos - p0);
            Vector3 paint = (p1 + r1 * here - ballPos) / h;
            ballVel += Physics.gravity * h;
            Vector3 rel = ballVel - paint;
            float speed = rel.magnitude;
            if (speed > 0) rel *= Mathf.Max(0, speed - (paintDrag * speed + paintFriction) * h) / speed;
            ballVel = paint + rel;
            ballPos += ballVel * h;

            // kept inside the can (pose 1): its ends, then its side
            Quaternion inv = Quaternion.Inverse(r1);
            Vector3 l = inv * (ballPos - p1);
            Vector3 lv = inv * (ballVel - paint);   // against the can
            float lo = middle - travel / 2, hi = middle + travel / 2;
            if (l.y < lo || l.y > hi)
            {
                bool top = l.y > hi;
                l.y = top ? hi : lo;
                float into = top ? lv.y : -lv.y;
                if (into > 0)
                {
                    Click(into, 1, top ? topPitch : bottomPitch);
                    lv.y = -lv.y * endBounce;
                    if (!top)   // off the dome, sideways (outward, or any way from the very middle)
                    {
                        var out2 = new Vector2(l.x, l.z);
                        if (out2.sqrMagnitude < 1e-8f) out2 = Random.insideUnitCircle;
                        out2.Normalize();
                        lv += new Vector3(out2.x, 0, out2.y) * (into * dome);
                    }
                }
            }
            float r = new Vector2(l.x, l.z).magnitude;
            if (r > sideRoom)
            {
                var n = new Vector3(l.x / r, 0, l.z / r);
                l.x = n.x * sideRoom; l.z = n.z * sideRoom;
                float into = Vector3.Dot(lv, n);
                if (into > 0)
                {
                    Click(into, sideVolume, Mathf.Lerp(sidePitchLow, sidePitchHigh, Mathf.InverseLerp(lo, hi, l.y)));
                    lv -= n * (into * (1 + sideBounce));
                }
            }
            ballLocal = l;
            ballPos = p1 + r1 * l;
            ballVel = paint + r1 * lv;
        }

        void Click(float speed, float volume, float pitch)
        {
            if (speed < quietHit || rattles.Length == 0 || stepTime < quietUntil) return;
            quietUntil = stepTime + 0.006;
            int k = Random.Range(0, rattles.Length);
            if (k == lastRattle && rattles.Length > 1) k = (k + 1) % rattles.Length;   // not the same clack twice
            lastRattle = k;
            float loud = Mathf.InverseLerp(quietHit, loudHit, speed);
            var src = clicks[nextClick];
            nextClick = (nextClick + 1) % clicks.Length;
            src.clip = rattles[k];
            src.pitch = pitch * Random.Range(0.94f, 1.06f);
            src.volume = rattleVolume * volume * Mathf.Lerp(0.04f, 1f, Mathf.Pow(loud, 1.2f));
            src.PlayScheduled(stepTime);
        }

        void Hiss()
        {
            if (!spray) return;
            float target = can.Spraying ? sprayVolume : 0;
            float rate = can.Spraying ? 1 / 0.03f : 1 / 0.08f;   // a quick psst on, a short tail off
            hiss.volume = Mathf.MoveTowards(hiss.volume, target, sprayVolume * rate * Time.deltaTime);
            if (hiss.volume > 0 && !hiss.isPlaying)
            {
                hiss.time = Random.Range(0, spray.length);   // not the same stretch of noise each time
                hiss.pitch = Random.Range(0.97f, 1.03f);
                hiss.Play();
            }
            else if (hiss.volume <= 0 && hiss.isPlaying) hiss.Stop();
        }

        void OnCollisionEnter(Collision c)
        {
            if (grab.isSelected || !thunk || Time.time < nextThunk) return;
            float speed = c.relativeVelocity.magnitude;
            if (speed < quietImpact) return;
            nextThunk = Time.time + 0.08f;
            shots.pitch = Random.Range(0.92f, 1.08f);
            shots.PlayOneShot(thunk, thunkVolume * Mathf.Lerp(0.2f, 1f, Mathf.InverseLerp(quietImpact, 4f, speed)));
        }
    }
}
