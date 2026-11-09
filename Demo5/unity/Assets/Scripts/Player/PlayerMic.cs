// The player's stage mic: in the right fist whenever they're on the stage (inside the stage's MoveArea, or while a
// battle's on), gone off it. It's part of the hand, not a pickup: it can't be dropped or grabbed, and the fist stays
// closed round it (PlayerAvatar.holdRight) whatever the grip does. Across the palm, the grille out past the thumb, the
// switch and its power light on top where you can see them.
using Freestyle.Battle;
using UnityEngine;

namespace Freestyle.Player
{
    public class PlayerMic : MonoBehaviour
    {
        [Tooltip("The mic (Assets/Models/mic/Mic.prefab: Freestyle > Build Mic).")]
        public GameObject micPrefab;
        public PlayerAvatar avatar;
        [Tooltip("The left hand instead of the right.")]
        public bool leftHanded;
        [Tooltip("Where the handle sits in the fist, in the hand's space (m): under the palm, between wrist and knuckles.")]
        public Vector3 inFist = PlayerAvatar.FistLocal;
        [Tooltip("The MoveArea that counts as the stage. Empty: the one named Stage Move Area.")]
        public MoveArea stage;

        GameObject mic;
        BattleCoordinator battle;
        Transform head;
        bool held;

        void Start()
        {
            if (!avatar) avatar = GetComponentInChildren<PlayerAvatar>();
            battle = FindAnyObjectByType<BattleCoordinator>();
            if (!stage)
                foreach (var a in FindObjectsByType<MoveArea>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (a.name == "Stage Move Area") stage = a;
            if (Camera.main) head = Camera.main.transform;
            var hand = avatar ? (leftHanded ? avatar.leftHand : avatar.rightHand) : null;
            if (!micPrefab || !hand) { Debug.LogWarning("PlayerMic: no mic prefab or hand"); enabled = false; return; }
            mic = Instantiate(micPrefab, hand, false);
            mic.name = "Stage Mic";
            mic.transform.localPosition = new Vector3(leftHanded ? -inFist.x : inFist.x, inFist.y, inFist.z);
            // its length across the palm toward the thumb (the right hand's thumb is on its -X), its front up
            mic.transform.localRotation = Quaternion.LookRotation(Vector3.up, leftHanded ? Vector3.right : Vector3.left);
            foreach (var c in mic.GetComponentsInChildren<Collider>(true)) Destroy(c);
            foreach (var t in mic.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = hand.gameObject.layer;
            mic.SetActive(false);
        }

        /// <summary>On the stage: the head's over its MoveArea, or a battle is on.</summary>
        public bool OnStage
        {
            get
            {
                if (battle && battle.Phase != BattlePhase.Idle && battle.Phase != BattlePhase.Finished) return true;
                if (!head && Camera.main) head = Camera.main.transform;
                if (!stage || !head) return false;
                Vector3 at = head.position;
                at.y = stage.transform.position.y;   // (over it: any height)
                return stage.Contains(at, 0.3f);
            }
        }

        void Update()
        {
            bool on = OnStage;
            if (on == held || !mic) return;
            held = on;
            mic.SetActive(on);
            if (!avatar) return;
            if (leftHanded) avatar.holdLeft = on; else avatar.holdRight = on;
        }

        void OnDisable()
        {
            if (avatar) { if (leftHanded) avatar.holdLeft = false; else avatar.holdRight = false; }
        }
    }
}
