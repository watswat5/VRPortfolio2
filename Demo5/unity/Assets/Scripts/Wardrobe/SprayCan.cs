// A can of spray paint. Grab it (XR grab interactable) and pull the trigger: it sprays a mist from its nozzle, and
// whatever body with an Outfit the spray hits (within reach) takes this colour by how long it's sprayed: a quick burst
// tints it, a long spray (the Outfit's sprayFullTime) covers it. The opponent hears the body's colour by its common name
// (ColorNames: a light pink tint on grey may be "greyish pink"), not the can's.
// It's a real object: let go, it falls, rolls and can be thrown (a flat base so it stands on the table); not held for
// respawnAfter seconds away from its spot, it goes back where it started. SprayCanSounds (added here) rattles its
// mixing ball, hisses while spraying and thunks when it lands.
// Its colour is its label's: the material on the Label renderer (art/spraycan's can has a child Label for it), so a
// can is recoloured by giving its label another material; `color` is only for a can without one.
// Pointed at a paintable wall (Venue.GraffitiSurface) instead, it tags it: closer is a smaller, sharper line, and the
// paint runs if it's held on one spot (its reach, 0.6 m, is about 2 ft).
// The spray comes out of the nozzle transform (the child Nozzle, else the can itself) along its forward.
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace Freestyle.Wardrobe
{
    [RequireComponent(typeof(XRGrabInteractable))]
    [DefaultExecutionOrder(100)]   // after PlayerAvatar has posed the hands (LateUpdate)
    public class SprayCan : MonoBehaviour
    {
        [Tooltip("The label: the paint is its material's base colour. Empty: the child named Label.")]
        public Renderer label;
        [Tooltip("The paint's colour when there's no label.")]
        public Color color = Color.red;
        [Tooltip("The can's own label (\"hot pink\"); the opponent hears the body's colour by its common name instead.")]
        public string colorName = "red";
        [Tooltip("Where the spray comes out, pointing out of the nozzle. Empty: the child named Nozzle, else the can.")]
        public Transform nozzle;
        [Tooltip("The mist's material (Models/spray_can/SprayMist.mat: a material asset, so its particle shader ships in " +
                 "builds; made in code it would be stripped and show magenta).")]
        public Material mistMaterial;
        [Header("In the hand")]
        [Tooltip("Where the hand holds it, from the can's base (m): high on the label, just under the shoulder, near the nozzle.")]
        public Vector3 grip = new(0f, 0.125f, 0f);
        [Tooltip("Without the player's avatar: how it sits against the controller's grip pose (whose forward runs up " +
                 "through the fist and whose up points back at you): -90 on X stands it upright, the nozzle away from you. " +
                 "With it, the can sits in the robot's fist instead: up toward the thumb, the nozzle along the knuckles.")]
        public Vector3 holdEuler = new(-90f, 0f, 0f);

        /// <summary>The paint's colour: the label material's (read each time, so changing the material changes the paint).</summary>
        public Color PaintColor
        {
            get
            {
                var m = label ? label.sharedMaterial : null;
                if (!m) return color;
                return m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.color;
            }
        }
        [Tooltip("How far the spray reaches (m): bodies and walls further off aren't painted (0.6 m: about 2 ft).")]
        public float reach = 0.6f;
        [Tooltip("How wide the spray is at full reach (m).")]
        public float spread = 0.12f;
        [Tooltip("Let go (or knocked over) away from its spot: back where it started after this long (s), unless " +
                 "picked up again.")]
        public float respawnAfter = 6f;

        /// <summary>The trigger is held: paint is coming out.</summary>
        public bool Spraying => spraying;
        /// <summary>It went back to its spot (SprayCanSounds forgets the motion).</summary>
        public event System.Action Respawned;

        XRGrabInteractable grab;
        Transform hold;   // the grip (attach transform): moved each frame while held so the can sits in the robot's fist
        Transform holder;   // the hand bone holding it (null: not held, or no avatar)
        UnityEngine.XR.Interaction.Toolkit.Interactors.IXRSelectInteractor holdingInteractor;
        bool heldLeft;
        ParticleSystem mist;
        Transform startParent;
        Vector3 startPos;
        Quaternion startRot;
        bool spraying;
        Rigidbody rb;
        float awayFor;   // s not held and off its spot

        void Awake()
        {
            grab = GetComponent<XRGrabInteractable>();
            rb = GetComponent<Rigidbody>();
            // a falling, rolling can (the grab makes it kinematic while held, and back after)
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.mass = 0.35f;
            rb.angularDamping = 0.8f;   // rolls a little way, not forever
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;   // thrown at a wall: doesn't pass through
            grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            grab.throwOnDetach = true;
            AddBase();
            // always held the same way, upright with the nozzle forward (not at whatever angle it was picked up: off the
            // table, facing you, that's nozzle-first at your face)
            grab.useDynamicAttach = false;
            var hold = new GameObject("Grip").transform;
            hold.SetParent(transform, false);
            hold.localPosition = grip;
            hold.localRotation = Quaternion.Euler(holdEuler);
            grab.attachTransform = hold;
            this.hold = hold;
            if (!label) label = FindChild("Label")?.GetComponent<Renderer>();
            if (!nozzle) nozzle = FindChild("Nozzle");
            if (!nozzle) nozzle = transform;
            mist = MakeMist();
            startParent = transform.parent;
            startPos = transform.position;
            startRot = transform.rotation;
            if (!GetComponent<SprayCanSounds>()) gameObject.AddComponent<SprayCanSounds>();
        }

        /// <summary>The capsule (round, for the hands and for rolling) can't stand on its end: a flat base inside it does.
        /// Its corners stay inside the capsule's radius, so it doesn't bump a can rolling on its side.</summary>
        void AddBase()
        {
            var metal = new PhysicsMaterial("Can")
            {
                dynamicFriction = 0.35f, staticFriction = 0.5f, bounciness = 0.2f,
                bounceCombine = PhysicsMaterialCombine.Maximum,
            };
            var capsule = GetComponent<CapsuleCollider>();
            if (capsule) capsule.sharedMaterial = metal;
            if (FindChild("Base")) return;
            float r = capsule ? capsule.radius : 0.034f;
            var go = new GameObject("Base");
            go.transform.SetParent(transform, false);
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(r * 1.3f, 0.01f, r * 1.3f);   // half-diagonal 0.92 r
            box.center = new Vector3(0, capsule ? capsule.center.y - capsule.height / 2 + 0.005f : 0.005f, 0);
            box.sharedMaterial = metal;
        }

        void OnEnable()
        {
            grab.selectEntered.AddListener(OnGrabbed);
            grab.activated.AddListener(OnTrigger);
            grab.deactivated.AddListener(OnTriggerUp);
            grab.selectExited.AddListener(OnReleased);
        }

        void OnDisable()
        {
            grab.selectEntered.RemoveListener(OnGrabbed);
            grab.activated.RemoveListener(OnTrigger);
            grab.deactivated.RemoveListener(OnTriggerUp);
            grab.selectExited.RemoveListener(OnReleased);
        }

        /// <summary>Grabbed: from now until it's let go, the can follows the robot's fist (LateUpdate), not the controller.</summary>
        void OnGrabbed(SelectEnterEventArgs e)
        {
            if (!hold) return;
            hold.localPosition = grip;
            hold.localRotation = Quaternion.Euler(holdEuler);
            holdingInteractor = e.interactorObject;
            heldLeft = e.interactorObject is UnityEngine.XR.Interaction.Toolkit.Interactors.XRBaseInteractor b &&
                       b.handedness == UnityEngine.XR.Interaction.Toolkit.Interactors.InteractorHandedness.Left;
            var avatar = e.interactorObject.transform.GetComponentInParent<Freestyle.Player.PlayerAvatar>()
                         ?? FindAnyObjectByType<Freestyle.Player.PlayerAvatar>();
            holder = avatar ? (heldLeft ? avatar.leftHand : avatar.rightHand) : null;
        }

        /// <summary>Held: the grip lands in the middle of the hand bone's fist (PlayerAvatar.FistLocal, as the stage mic's
        /// handle does), the can's up toward the thumb and its nozzle along the knuckles. Done each frame after the avatar
        /// has posed its arms (the visible hand isn't always where the controller is: the arm can fall short of it), by
        /// moving the attach transform so the grab interactable puts the can there itself (throws and all).</summary>
        void LateUpdate()
        {
            if (!holder || holdingInteractor == null || !grab.isSelected) return;
            var fist = Freestyle.Player.PlayerAvatar.FistLocal;
            if (heldLeft) fist.x = -fist.x;
            // the right hand's thumb is on the bone's -X, the left's on its +X; its +Z runs out along the knuckles
            Quaternion rot = holder.rotation * Quaternion.LookRotation(Vector3.forward, heldLeft ? Vector3.right : Vector3.left);
            Vector3 pos = holder.TransformPoint(fist) - rot * Vector3.Scale(grip, transform.lossyScale);
            // where the hand's attach must sit on the can for the can to land at pos, rot
            var attach = holdingInteractor.GetAttachTransform(grab);
            if (!attach) return;
            float scale = Mathf.Max(1e-4f, transform.lossyScale.x);
            hold.localPosition = Quaternion.Inverse(rot) * (attach.position - pos) / scale;
            hold.localRotation = Quaternion.Inverse(rot) * attach.rotation;
        }

        void OnTrigger(ActivateEventArgs e) => SetSpraying(true);
        void OnTriggerUp(DeactivateEventArgs e) => SetSpraying(false);

        void OnReleased(SelectExitEventArgs e)
        {
            SetSpraying(false);
            holder = null;
            holdingInteractor = null;
            if (transform.parent != startParent) transform.SetParent(startParent, true);   // where it was let go
            awayFor = 0;
        }

        /// <summary>Back where it started, at rest.</summary>
        public void Respawn()
        {
            SetSpraying(false);
            transform.SetParent(startParent, true);
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            transform.SetPositionAndRotation(startPos, startRot);
            rb.position = startPos;
            rb.rotation = startRot;
            awayFor = 0;
            Respawned?.Invoke();
        }

        void CheckAway()
        {
            bool away = !grab.isSelected && ((transform.position - startPos).sqrMagnitude > 0.03f * 0.03f ||
                                             Quaternion.Angle(transform.rotation, startRot) > 15f);
            awayFor = away ? awayFor + Time.deltaTime : 0;
            if (away && (awayFor >= respawnAfter || transform.position.y < startPos.y - 20f)) Respawn();   // (or fell out of the world)
        }

        /// <summary>Starts or stops spraying (the trigger does this; also for testing without a headset).</summary>
        public void SetSpraying(bool on)
        {
            spraying = on;
            if (on)
            {
                var c = PaintColor;   // the label may have been changed since
                var main = mist.main;
                main.startColor = c;
                var r = mist.GetComponent<ParticleSystemRenderer>();
                if (r.sharedMaterial) r.sharedMaterial.color = c;
                mist.Play();
            }
            else mist.Stop();
        }

        Transform FindChild(string name) =>
            System.Array.Find(GetComponentsInChildren<Transform>(true), t => t != transform && t.name == name);

        void Update()
        {
            CheckAway();
            if (!spraying) return;
            // a few rays through the cone: paint the first body with an outfit any of them hits; failing that, the
            // middle one tags the wall it reaches (a GraffitiSurface)
            Venue.GraffitiSurface wall = null;
            RaycastHit wallHit = default;
            for (int i = 0; i < 5; i++)
            {
                Vector3 dir = nozzle.forward * reach + (i == 0 ? Vector3.zero
                    : Quaternion.AngleAxis(90f * i, nozzle.forward) * nozzle.up * spread);
                if (!First(nozzle.position, dir.normalized, dir.magnitude, out var hit)) continue;
                var outfit = hit.collider.GetComponentInParent<Outfit>();
                if (outfit)
                {
                    outfit.Spray(PaintColor, colorName, Time.deltaTime);
                    return;
                }
                if (i == 0) hit.collider.TryGetComponent(out wall);
                if (wall && i == 0) wallHit = hit;
            }
            if (!wall) return;
            // like a real can: up close a small, sharp, strong line; at full reach a wide, soft, faint mist
            float far = Mathf.Clamp01(wallHit.distance / reach);
            float radius = Mathf.Lerp(0.008f, spread * 0.45f, far);
            float strength = 1 - Mathf.Exp(-Time.deltaTime * Mathf.Lerp(40f, 5f, far));
            wall.Spray(wallHit.point, PaintColor, radius, strength, Mathf.Lerp(0.55f, 0.05f, far), Time.deltaTime);
        }

        readonly RaycastHit[] hits = new RaycastHit[16];

        /// <summary>The first thing along the ray the paint lands on: a body (its trigger colliders), a paintable wall, or
        /// anything solid (which stops it). Other triggers (the hands' grab zones) and the can itself are passed through.</summary>
        bool First(Vector3 from, Vector3 dir, float dist, out RaycastHit first)
        {
            int n = Physics.RaycastNonAlloc(from, dir, hits, dist, ~0, QueryTriggerInteraction.Collide);
            System.Array.Sort(hits, 0, n, HitOrder);
            for (int k = 0; k < n; k++)
            {
                var c = hits[k].collider;
                if (c.transform.IsChildOf(transform)) continue;
                if (!c.isTrigger || c.GetComponentInParent<Outfit>() || c.GetComponent<Venue.GraffitiSurface>())
                {
                    first = hits[k];
                    return true;
                }
            }
            first = default;
            return false;
        }

        static readonly System.Collections.Generic.IComparer<RaycastHit> HitOrder = Comparer.Instance;
        class Comparer : System.Collections.Generic.IComparer<RaycastHit>
        {
            public static readonly Comparer Instance = new();
            public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
        }

        ParticleSystem MakeMist()
        {
            var go = new GameObject("Mist");
            go.transform.SetParent(nozzle, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.startLifetime = 0.35f;
            main.startSpeed = reach / 0.35f;
            main.startSize = 0.025f;
            main.startColor = PaintColor;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.playOnAwake = false;
            var emission = ps.emission;
            emission.rateOverTime = 120f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = Mathf.Atan2(spread, reach) * Mathf.Rad2Deg;
            shape.radius = 0.005f;
            var r = go.GetComponent<ParticleSystemRenderer>();
            var shader = mistMaterial ? null : Shader.Find("Universal Render Pipeline/Particles/Unlit");   // (the editor only)
            if (mistMaterial || shader) r.sharedMaterial = mistMaterial ? new Material(mistMaterial) : new Material(shader);
            if (r.sharedMaterial) r.sharedMaterial.color = PaintColor;
            return ps;
        }
    }
}
