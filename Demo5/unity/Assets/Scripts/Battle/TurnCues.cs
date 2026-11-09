// Whose turn it is and how long is left, without reading anything (for VR):
//   Spotlights: the key light on whoever has the mic goes white, the other red (yours from the count-in on, theirs for
//     their turn, both red while your bars are judged); at the end the winner gets white (a draw: both). Before and
//     between battles they're back to their own colour.
//   Haptics, both controllers: a tick on each count-in beat (growing), a hit when your bars start, a tick on each beat
//     of your last bar, and a bump when your time's up. A soft one when the turn passes to the opponent.
// Everything comes from the BattleCoordinator's events; the lights are the stage's two key spots (Freestyle > Set Up
// Battle finds them: the spots aimed at each MC's mark).
using UnityEngine;
using UnityEngine.XR;

namespace Freestyle.Battle
{
    public class TurnCues : MonoBehaviour
    {
        public BattleCoordinator battle;

        [Header("Spotlights")]
        public Light playerKey;
        public Light opponentKey;
        public Color onColor = Color.white, offColor = new(1f, 0.08f, 0.05f);
        [Tooltip("Of each light's own intensity: on whoever has the mic, and on the other.")]
        public float onIntensity = 1.2f, offIntensity = 0.7f;
        [Tooltip("Seconds for a light to change.")]
        public float lightFade = 0.35f;

        [Header("Haptics")]
        public bool haptics = true;
        [Range(0, 1)] public float countInFrom = 0.15f, countInTo = 0.4f;   // the count-in ticks grow
        [Range(0, 1)] public float goStrength = 0.9f, lastBarTick = 0.35f, timeUp = 0.6f, handOver = 0.2f;

        enum Cue { Own, On, Off }

        /// <summary>A key light, eased toward its cue's colour and intensity.</summary>
        class Key
        {
            public Light light;
            public Color baseColor, from, to;
            public float baseIntensity, fromI, toI, t = 1f;
            public Key(Light l) { light = l; if (l) { baseColor = from = to = l.color; baseIntensity = fromI = toI = l.intensity; } }
        }

        Key player, opponent;
        bool wasLive;

        void Awake()
        {
            if (!battle) battle = FindAnyObjectByType<BattleCoordinator>();
            player = new Key(playerKey);
            opponent = new Key(opponentKey);
        }

        void OnEnable()
        {
            if (!battle) return;
            battle.PhaseChanged += OnPhase;
            battle.Beat += OnBeat;
            battle.UserTurnLive += OnLive;
            battle.BattleFinished += OnFinished;
        }

        void OnDisable()
        {
            if (!battle) return;
            battle.PhaseChanged -= OnPhase;
            battle.Beat -= OnBeat;
            battle.UserTurnLive -= OnLive;
            battle.BattleFinished -= OnFinished;
            foreach (var k in new[] { player, opponent })
                if (k?.light) { k.light.color = k.baseColor; k.light.intensity = k.baseIntensity; }
        }

        void Update()
        {
            float step = Time.deltaTime / Mathf.Max(0.01f, lightFade);
            foreach (var k in new[] { player, opponent })
            {
                if (k?.light == null || k.t >= 1f) continue;
                k.t = Mathf.Min(1f, k.t + step);
                float e = Mathf.SmoothStep(0f, 1f, k.t);
                k.light.color = Color.Lerp(k.from, k.to, e);
                k.light.intensity = Mathf.Lerp(k.fromI, k.toI, e);
            }
        }

        void Light(Cue p, Cue o) { Ease(player, p); Ease(opponent, o); }

        void Ease(Key k, Cue cue)
        {
            if (k?.light == null) return;
            k.from = k.light.color; k.fromI = k.light.intensity; k.t = 0f;
            k.to = cue == Cue.On ? onColor : cue == Cue.Off ? offColor : k.baseColor;
            k.toI = k.baseIntensity * (cue == Cue.On ? onIntensity : cue == Cue.Off ? offIntensity : 1f);
        }

        void OnPhase(BattlePhase p)
        {
            if (wasLive && p != BattlePhase.UserTurn) Buzz(timeUp, 0.15f);   // your time's up (or you ended it)
            wasLive = false;
            switch (p)
            {
                case BattlePhase.UserTurn: Light(Cue.On, Cue.Off); break;
                case BattlePhase.Judging: Light(Cue.Off, Cue.Off); break;
                case BattlePhase.OpponentTurn: Light(Cue.Off, Cue.On); Buzz(handOver, 0.1f); break;
                case BattlePhase.Starting:
                case BattlePhase.Idle: Light(Cue.Own, Cue.Own); break;
                // Finished: OnFinished lights the winner
            }
        }

        void OnBeat(int beatInBar, int bar)
        {
            if (battle.Phase != BattlePhase.UserTurn) return;
            double rel = battle.Now - battle.UserTurnStart, beat = battle.BeatSeconds;
            if (rel < -beat / 2)
            {
                // the count-in: louder as it gets closer
                int total = Mathf.Max(1, battle.countInBars * battle.BeatsPerBar);
                float left = Mathf.Clamp01((float)(-rel / beat) / total);
                Buzz(Mathf.Lerp(countInTo, countInFrom, left), 0.05f);
            }
            else if (rel > beat / 2)   // (the first beat is OnLive's)
            {
                int barNow = (int)(rel / battle.BarSeconds) + 1;
                if (barNow == battle.UserTurnBars) Buzz(lastBarTick, 0.05f);
            }
        }

        void OnLive(int round)
        {
            wasLive = true;
            Buzz(goStrength, 0.2f);
        }

        void OnFinished(ResultMsg r)
        {
            switch (r?.winner)
            {
                case "user": Light(Cue.On, Cue.Off); break;
                case "opponent": Light(Cue.Off, Cue.On); break;
                default: Light(Cue.On, Cue.On); break;
            }
        }

        /// <summary>Both controllers, `amplitude` 0-1 for `seconds`.</summary>
        void Buzz(float amplitude, float seconds)
        {
            if (!haptics || amplitude <= 0f) return;
            foreach (var node in new[] { XRNode.LeftHand, XRNode.RightHand })
            {
                var d = InputDevices.GetDeviceAtXRNode(node);
                if (d.isValid && d.TryGetHapticCapabilities(out var caps) && caps.supportsImpulse)
                    d.SendHapticImpulse(0, amplitude, seconds);
            }
        }
    }
}
