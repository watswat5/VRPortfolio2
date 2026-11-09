// The battle answers the player's gestures (Gestures), while one is on:
//   Pointing at the MC: the MC reacts by mood (steps back rattled, glares heated, laughs it off cocky or amused; its
//     face flashes with it), the crowd goes "ooh", and it gets under the MC's skin: during your bars it intimidates
//     them, while they rap at you it annoys them (BattleRules' mood, which the comeback plays out).
//   Raising the roof: the crowd swells; during your bars it lifts the room's level for a moment too.
// Added to the battle's object by itself when the scene loads (no scene change), with its own PointGesture and
// HandsRaisedGesture (tune them on that object, or wire more to their events).
using Freestyle.Gestures;
using Freestyle.Venue;
using UnityEngine;

namespace Freestyle.Battle
{
    public class BattleGestures : MonoBehaviour
    {
        public BattleCoordinator battle;
        public OpponentStage stage;
        public Crowd crowd;
        public CrowdAudio crowdAudio;
        [Tooltip("Pointing at the MC. Empty: made on this object.")]
        public PointGesture point;
        [Tooltip("Raising the roof. Empty: made on this object.")]
        public HandsRaisedGesture roof;

        [Header("Pointing at the MC")]
        [Tooltip("How much more intimidated the MC gets when you point during your bars (0-10 scale), at a clean point.")]
        public float pointIntimidates = 0.8f;
        [Tooltip("How much more annoyed when you point back while they rap.")]
        public float pointAnnoys = 0.6f;
        [Tooltip("Seconds the crowd's \"ooh\" lasts, and how loud next to a punchline's roar.")]
        public float oohTime = 1.8f;
        [Range(0, 2)] public float oohGain = 0.7f;

        [Header("Raising the roof")]
        [Tooltip("Seconds the crowd swells, and how loud next to a punchline's roar.")]
        public float roofTime = 3f;
        [Range(0, 3)] public float roofGain = 1.4f;
        [Tooltip("How much it lifts the room's level during your bars (0-11 scale, fading like any reaction).")]
        public float roofLift = 1.2f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (_, _) => AddTo();
            AddTo();
        }

        static void AddTo()
        {
            var b = FindAnyObjectByType<BattleCoordinator>();
            if (b && !FindAnyObjectByType<BattleGestures>()) b.gameObject.AddComponent<BattleGestures>();
        }

        void Awake()
        {
            if (!battle) battle = FindAnyObjectByType<BattleCoordinator>();
            if (!stage) stage = FindAnyObjectByType<OpponentStage>();
            if (!crowd) crowd = FindAnyObjectByType<Crowd>();
            if (!crowdAudio) crowdAudio = FindAnyObjectByType<CrowdAudio>();
            if (!point) { point = gameObject.AddComponent<PointGesture>(); point.gestureName = "PointAtMC"; }
            if (!roof) { roof = gameObject.AddComponent<HandsRaisedGesture>(); roof.gestureName = "RaiseTheRoof"; }
            point.enabled = roof.enabled = false;
        }

        void OnEnable()
        {
            if (point) point.Fired += OnPoint;
            if (roof) roof.Fired += OnRoof;
        }

        void OnDisable()
        {
            if (point) point.Fired -= OnPoint;
            if (roof) roof.Fired -= OnRoof;
        }

        /// <summary>A battle's on: the turns (count-ins included), not before the first or after the last.</summary>
        bool Battling => battle && battle.Phase is BattlePhase.UserTurn or BattlePhase.Judging or BattlePhase.OpponentTurn;

        void Update()
        {
            var mc = stage ? stage.Opponent : null;
            point.target = mc ? mc.transform : null;
            point.enabled = Battling && mc;
            roof.enabled = Battling;
        }

        void OnPoint(GestureEvent e)
        {
            var mc = stage ? stage.Opponent : null;
            if (!mc) return;
            bool yourBars = battle.UserTurnIsLive;
            // the MC's answer, by how they feel (the face flashes with the gesture)
            string answer = mc.mood switch
            {
                "rattled" => "steps_back",
                "heated" => "glares",
                "cocky" or "amused" => "laughs",
                _ => yourBars ? "steps_back" : "glares",
            };
            mc.Gesture(answer);
            if (crowd) crowd.CheerFor(oohTime);
            if (crowdAudio) crowdAudio.CheerFor(oohTime, oohGain);
            var rules = battle.Rules;
            if (rules == null) return;
            string dim = yourBars ? "intimidated" : "annoyed";
            float by = (yourBars ? pointIntimidates : pointAnnoys) * Mathf.Lerp(0.5f, 1f, e.strength);
            rules.Mood[dim] = Mathf.Clamp((rules.Mood.TryGetValue(dim, out var v) ? v : 0f) + by, 0f, 10f);
        }

        void OnRoof(GestureEvent e)
        {
            if (crowd) crowd.CheerFor(roofTime);
            if (crowdAudio) crowdAudio.CheerFor(roofTime, roofGain);
            var rules = battle.Rules;
            if (rules != null && battle.UserTurnIsLive)
                rules.Reaction = Mathf.Min(rules.Reaction + roofLift, BattleRules.CrowdMax - rules.Base);
        }
    }
}
