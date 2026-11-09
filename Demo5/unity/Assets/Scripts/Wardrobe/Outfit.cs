// What the player is wearing. Put it on the player's body (or rig) and give it a transform for each location on the
// body (any left empty can't be worn there): each gets an XR socket that snaps on the matching Accessory, its
// attachPoint on the transform. One accessory per location: letting go of another there takes the worn one off,
// and it respawns where it started. It can also be spray painted (SprayCan): Spray blends the paintable renderers toward
// the can's colour by how long it's been sprayed (a light spray tints, sprayFullTime seconds covers; another can layers
// over what shows); Paint is all at once. The opponent hears the shown colour's common name (ColorNames).
// Descriptions() is what goes to the server (BattleCoordinator.outfit): the body itself (baseDescription, with its
// paint), then each accessory.
// A location can move to another transform at runtime (SetLocation: another head or body shape), its socket and anything
// worn there going with it. Worn accessories are scaled by their location's scale (lossy: a bigger body's necklace spot is
// bigger, and the whole wearer may be scaled), so one model fits every shape.
// Accessories worn on the head (hat, eyes, ears) go on hiddenLayer while worn (MirrorOnly: the headset camera leaves it
// out), so they don't sit in front of the player's own eyes; a mirror still shows them.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Freestyle.Wardrobe
{
    public class Outfit : MonoBehaviour
    {
        [Header("Locations (where each accessory sits; its attach point lands here, rotated to match)")]
        public Transform hat;
        public Transform eyes, ears, neck, wrist;

        [Tooltip("How close (m) an accessory has to be let go to its location to go on.")]
        public float snapRadius = 0.15f;
        [Tooltip("Show a see-through preview of the accessory on the body while it's held near.")]
        public bool showPreview = true;

        [Header("Paint")]
        [Tooltip("What spray paint recolours (their material's base colour).")]
        public List<Renderer> paintable = new();
        [Tooltip("Seconds of spraying one can to go from what's on the body to full coverage in its colour.")]
        public float sprayFullTime = 2.5f;
        /// <summary>The colour the body shows now.</summary>
        public Color PaintColor => painted ? shown : Unpainted();
        /// <summary>The common name of the colour the body shows (the nearest of ColorNames' 256), or null when unpainted.</summary>
        public string PaintName => painted ? ColorNames.Nearest(shown) : null;
        /// <summary>How far the latest can has covered what was under it (0-1).</summary>
        public float PaintCoverage => coverage;
        /// <summary>The paint job in words, for the opponent ("spray painted dusty rose"), or null when unpainted.</summary>
        public string PaintDescription => painted ? $"spray painted {PaintName}" : null;

        bool painted;
        Color shown, from, sprayColor;
        string sprayName, lastPaint;
        float coverage;

        [Header("Description and view")]
        [Tooltip("The body itself, for the opponent: \"a hovering robot with a TV head\". Empty: just the accessories.")]
        public string baseDescription = "";
        [Tooltip("Head accessories go on this layer while worn (one the wearer's own camera doesn't draw). Empty: leave them.")]
        public string hiddenLayer = "MirrorOnly";

        readonly Dictionary<Accessory, Dictionary<GameObject, int>> layersBefore = new();
        MaterialPropertyBlock block;

        /// <summary>Something was put on or taken off.</summary>
        public event Action Changed;

        readonly Dictionary<BodyLocation, AccessorySocket> sockets = new();
        readonly Dictionary<Accessory, Vector3> scaleBefore = new();
        static readonly List<Outfit> all = new();

        public Transform LocationOf(BodyLocation where) => where switch
        {
            BodyLocation.Hat => hat, BodyLocation.Eyes => eyes, BodyLocation.Ears => ears,
            BodyLocation.Neck => neck, BodyLocation.Wrist => wrist,
            _ => null,
        };

        void Awake()
        {
            foreach (BodyLocation where in Enum.GetValues(typeof(BodyLocation)))
            {
                var at = LocationOf(where);
                if (!at) continue;
                var go = new GameObject($"Socket {where}");
                go.transform.SetParent(at, false);
                var rb = go.AddComponent<Rigidbody>();   // a moving trigger: kinematic, so it's tracked properly
                rb.isKinematic = true; rb.useGravity = false;
                var trigger = go.AddComponent<SphereCollider>();
                trigger.isTrigger = true;
                trigger.radius = snapRadius;
                var socket = go.AddComponent<AccessorySocket>();
                socket.Outfit = this;
                socket.location = where;
                socket.attachTransform = at;
                socket.recycleDelayTime = 0f;
                socket.showInteractableHoverMeshes = showPreview;
                socket.selectEntered.AddListener(e => OnWorn(e.interactableObject.transform.GetComponent<Accessory>()));
                socket.selectExited.AddListener(e => OnTakenOff(e.interactableObject.transform.GetComponent<Accessory>()));
                sockets[where] = socket;
            }
        }

        void OnEnable() => all.Add(this);

        void LateUpdate()
        {
            foreach (var a in scaleBefore.Keys) if (a) Fit(a);
        }

        /// <summary>A worn accessory at its own scale times its location's (world) scale.</summary>
        void Fit(Accessory a)
        {
            var at = LocationOf(a.location);
            if (at && scaleBefore.TryGetValue(a, out var own)) a.transform.localScale = own * at.lossyScale.x;
        }

        /// <summary>Moves a location to another transform (another head or body shape): its socket, and anything worn
        /// there, go with it.</summary>
        public void SetLocation(BodyLocation where, Transform at)
        {
            switch (where)
            {
                case BodyLocation.Hat: hat = at; break;
                case BodyLocation.Eyes: eyes = at; break;
                case BodyLocation.Ears: ears = at; break;
                case BodyLocation.Neck: neck = at; break;
                case BodyLocation.Wrist: wrist = at; break;
            }
            if (!at || !sockets.TryGetValue(where, out var socket)) return;
            socket.transform.SetParent(at, false);
            socket.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            socket.attachTransform = at;
            var worn = At(where);
            if (worn) Fit(worn);
        }
        void OnDisable() => all.Remove(this);

        void OnWorn(Accessory a)
        {
            if (!a) return;
            a.Wearer = this;
            if (!scaleBefore.ContainsKey(a)) scaleBefore[a] = a.transform.localScale;
            Fit(a);
            if (a.location is BodyLocation.Hat or BodyLocation.Eyes or BodyLocation.Ears) Hide(a, true);
            Changed?.Invoke();
        }

        void OnTakenOff(Accessory a)
        {
            if (!a) return;
            if (a.Wearer == this) a.Wearer = null;
            if (scaleBefore.TryGetValue(a, out var before)) { a.transform.localScale = before; scaleBefore.Remove(a); }
            Hide(a, false);
            Changed?.Invoke();
        }

        /// <summary>Puts a worn head accessory on hiddenLayer (or back on its own layers).</summary>
        void Hide(Accessory a, bool hide)
        {
            int layer = string.IsNullOrEmpty(hiddenLayer) ? -1 : LayerMask.NameToLayer(hiddenLayer);
            if (hide && layer >= 0 && !layersBefore.ContainsKey(a))
            {
                var before = new Dictionary<GameObject, int>();
                foreach (var r in a.GetComponentsInChildren<Renderer>(true)) { before[r.gameObject] = r.gameObject.layer; r.gameObject.layer = layer; }
                layersBefore[a] = before;
            }
            else if (!hide && layersBefore.TryGetValue(a, out var before))
            {
                foreach (var kv in before) if (kv.Key) kv.Key.layer = kv.Value;
                layersBefore.Remove(a);
            }
        }

        /// <summary>Spray paints the body all at once: the paintable renderers take this colour; name is how the opponent hears it.</summary>
        public void Paint(Color color, string name) => Spray(color, name, float.PositiveInfinity);

        /// <summary>`seconds` of spraying this colour: the body blends from what it showed when this can started toward
        /// it, fully covered after sprayFullTime seconds in all. Another can (or colour) starts over from what shows now.</summary>
        public void Spray(Color color, string name, float seconds)
        {
            name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            if (!painted || color != sprayColor || name != sprayName)
            {
                from = PaintColor;
                sprayColor = color;
                sprayName = name;
                coverage = 0;
            }
            coverage = Mathf.Clamp01(coverage + seconds / Mathf.Max(0.01f, sprayFullTime));
            shown = Color.Lerp(from, sprayColor, coverage);
            painted = true;
            block ??= new MaterialPropertyBlock();
            foreach (var r in paintable)
            {
                if (!r) continue;
                r.GetPropertyBlock(block);
                block.SetColor("_BaseColor", shown);
                r.SetPropertyBlock(block);
            }
            // tell listeners only when the words change (not every frame of a spray)
            string words = PaintDescription;
            if (words != lastPaint) { lastPaint = words; Changed?.Invoke(); }
        }

        /// <summary>The shell's own colour, before any paint.</summary>
        Color Unpainted()
        {
            var r = paintable.FirstOrDefault(p => p && p.sharedMaterial);
            return r && r.sharedMaterial.HasProperty("_BaseColor") ? r.sharedMaterial.GetColor("_BaseColor") : Color.white;
        }

        /// <summary>Tell listeners the outfit changed (the body's description, say).</summary>
        public void NotifyChanged() => Changed?.Invoke();

        /// <summary>What's worn at a location, or null.</summary>
        public Accessory At(BodyLocation where) =>
            sockets.TryGetValue(where, out var s) && s.hasSelection ? s.interactablesSelected[0].transform.GetComponent<Accessory>() : null;

        /// <summary>The accessories worn now.</summary>
        public IEnumerable<Accessory> Worn => sockets.Keys.Select(At).Where(a => a);

        /// <summary>Puts it on from anywhere (no hands), taking off whatever is worn there. False when this body has
        /// nowhere for it.</summary>
        public bool Wear(Accessory item)
        {
            if (!sockets.TryGetValue(item.location, out var socket)) return false;
            if (At(item.location) == item) return true;
            if (item.Wearer && item.Wearer != this) item.Wearer.TakeOff(item, respawn: false);
            Clear(item.location, except: item);
            var mgr = socket.interactionManager;
            IXRSelectInteractable grab = item.Grab;
            foreach (var who in item.Grab.interactorsSelecting.ToArray()) mgr.SelectExit(who, grab);   // out of the hand
            mgr.SelectEnter((IXRSelectInteractor)socket, grab);
            return true;
        }

        /// <summary>Takes it off; it respawns where it started unless respawn is false.</summary>
        public void TakeOff(Accessory item, bool respawn = true)
        {
            if (!sockets.TryGetValue(item.location, out var socket) || At(item.location) != item) return;
            socket.Ejected(item);
            socket.interactionManager.SelectExit((IXRSelectInteractor)socket, (IXRSelectInteractable)item.Grab);
            if (respawn) item.Respawn();
        }

        /// <summary>Takes off whatever is worn at a location (other than except); it respawns where it started.</summary>
        public void Clear(BodyLocation where, Accessory except = null)
        {
            var worn = At(where);
            if (worn && worn != except) TakeOff(worn);
        }

        /// <summary>Takes everything off.</summary>
        public void Undress()
        {
            foreach (var a in Worn.ToList()) TakeOff(a);
        }

        /// <summary>What the player looks like, for the server: the body (and its paint), then one per accessory.</summary>
        public List<string> Descriptions()
        {
            var list = new List<string>();
            string body = baseDescription?.Trim();
            string paint = PaintDescription;
            if (!string.IsNullOrEmpty(body) || paint != null)
                list.Add((string.IsNullOrEmpty(body) ? "their body" : body) + (paint != null ? $", {paint}" : ""));
            list.AddRange(Worn.Select(a => a.description?.Trim()).Where(d => !string.IsNullOrEmpty(d)));
            return list;
        }

        /// <summary>The socket for this accessory's location that it's being held over, if any.</summary>
        public static AccessorySocket SocketHovering(Accessory item)
        {
            foreach (var o in all)
                if (o.sockets.TryGetValue(item.location, out var s) && s.IsHovering(item.Grab)) return s;
            return null;
        }
    }
}
