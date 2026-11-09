// A wall you can tag with a spray can (SprayCan): a quad laid just in front of the wall (its -Z side facing the room,
// scaled to the wall's size) with a thin trigger box round it. Painting goes into a texture of its own, made the first
// time it's sprayed (about pixelsPerMetre a metre), shown through a see-through lit material over the wall; unsprayed,
// it's switched off and costs nothing.
// Each spray is a soft dot (GraffitiStamp.shader) laid over what's there: SprayCan sizes it by distance (close: small,
// sharp, strong; at its 0.6 m reach: wide, soft, faint). Hold the can still on one spot and the paint runs: a drip slides
// down from it. Built by Freestyle > Make Dressing Room Walls Paintable.
using System.Collections.Generic;
using UnityEngine;

namespace Freestyle.Venue
{
    [RequireComponent(typeof(MeshRenderer))]
    public class GraffitiSurface : MonoBehaviour
    {
        [Tooltip("Hidden/Freestyle/GraffitiStamp (a reference, so builds include it).")]
        public Shader stampShader;
        [Tooltip("The paint's resolution, pixels per metre of wall.")]
        public float pixelsPerMetre = 256f;
        [Header("Drips")]
        [Tooltip("Seconds of spraying about the same spot before the paint runs.")]
        public float dripAfter = 1f;
        [Tooltip("How far the spray can wander (m) and still count as the same spot (hands wobble).")]
        public float dripSpot = 0.04f;
        [Tooltip("How far a drip runs (m), shortest to longest.")]
        public Vector2 dripLength = new(0.04f, 0.14f);
        public int maxDrips = 6;

        /// <summary>Whether anything has been sprayed on it.</summary>
        public bool Painted => paint;

        RenderTexture paint;
        Material stamp;
        MeshRenderer view;
        Vector3 dwellAt;
        float dwell, sinceDrip;

        class Drip { public Vector2 uv; public float left, speed, radius; public Color colour; }
        readonly List<Drip> drips = new();

        void Awake()
        {
            view = GetComponent<MeshRenderer>();
            view.enabled = false;   // nothing to show until it's painted
        }

        void OnDestroy()
        {
            if (paint) { paint.Release(); Destroy(paint); }
            if (stamp) Destroy(stamp);
        }

        void Ready()
        {
            if (paint) return;
            Vector3 s = transform.lossyScale;
            int w = Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(s.x) * pixelsPerMetre), 64, 2048);
            int h = Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(s.y) * pixelsPerMetre), 64, 2048);
            paint = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
            {
                name = name + " Paint", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, useMipMap = false,
            };
            paint.Create();
            var was = RenderTexture.active;
            RenderTexture.active = paint;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = was;
            stamp = new Material(stampShader ? stampShader : Shader.Find("Hidden/Freestyle/GraffitiStamp"));
            var m = view.material;   // its own copy of the paint material
            m.SetTexture("_BaseMap", paint);
            m.mainTexture = paint;
            view.enabled = true;
        }

        /// <summary>A spray at a point on the wall: a dot `radius` across (m) of this colour, laid over what's there with
        /// `strength` (0-1), solid inside `hardness` of its radius. `dt`: the frame's time (for the drips).</summary>
        public void Spray(Vector3 point, Color colour, float radius, float strength, float hardness, float dt)
        {
            Ready();
            Vector2 uv = UV(point);
            Stamp(uv, radius, colour, strength, hardness);

            // a spot held too long runs: seconds of spraying near the same place (it follows the hand's wobble; a real
            // move away starts the count again)
            if ((point - dwellAt).sqrMagnitude < dripSpot * dripSpot) { dwell += dt; dwellAt = Vector3.Lerp(dwellAt, point, 0.1f); }
            else { dwellAt = point; dwell = 0; }
            if (dwell > dripAfter && sinceDrip > 0.35f && drips.Count < maxDrips)
            {
                drips.Add(new Drip
                {
                    uv = uv + new Vector2(Random.Range(-0.4f, 0.4f) * radius / Mathf.Abs(transform.lossyScale.x), 0),
                    left = Random.Range(dripLength.x, dripLength.y), speed = Random.Range(0.05f, 0.09f),
                    radius = Mathf.Clamp(radius * 0.18f, 0.0025f, 0.006f), colour = colour,
                });
                sinceDrip = 0;
                dwell = dripAfter * 0.5f;
            }
        }

        void Update()
        {
            sinceDrip += Time.deltaTime;
            for (int i = drips.Count - 1; i >= 0; i--)
            {
                var d = drips[i];
                float step = d.speed * Time.deltaTime;
                d.speed = Mathf.Max(0.015f, d.speed * (1 - 1.2f * Time.deltaTime));   // slows as it runs out
                d.left -= step;
                d.uv.y -= step / Mathf.Abs(transform.lossyScale.y);
                // thinning toward its end, with a bead at the bottom when it stops
                Stamp(d.uv, d.left > 0 ? d.radius : d.radius * 1.4f, d.colour, 0.85f, 0.6f);
                d.radius *= 1 - 0.25f * Time.deltaTime;
                if (d.left <= 0) drips.RemoveAt(i);
            }
        }

        Vector2 UV(Vector3 point)
        {
            Vector3 p = transform.InverseTransformPoint(point);   // the quad: -0.5..0.5 in x and y
            return new Vector2(p.x + 0.5f, p.y + 0.5f);
        }

        void Stamp(Vector2 uv, float radius, Color colour, float strength, float hardness)
        {
            Vector3 s = transform.lossyScale;
            stamp.SetVector("_Stamp", new Vector4(uv.x, uv.y, radius / Mathf.Abs(s.x), radius / Mathf.Abs(s.y)));
            stamp.SetColor("_Color", new Color(colour.r, colour.g, colour.b, Mathf.Clamp01(strength)));
            stamp.SetFloat("_Hardness", Mathf.Clamp(hardness, 0f, 0.98f));
            var tmp = RenderTexture.GetTemporary(paint.descriptor);
            Graphics.Blit(paint, tmp, stamp);
            if (SystemInfo.copyTextureSupport != UnityEngine.Rendering.CopyTextureSupport.None) Graphics.CopyTexture(tmp, paint);
            else Graphics.Blit(tmp, paint);
            RenderTexture.ReleaseTemporary(tmp);
        }

        /// <summary>Wipes it clean.</summary>
        public void Clear()
        {
            drips.Clear();
            if (!paint) return;
            var was = RenderTexture.active;
            RenderTexture.active = paint;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = was;
        }
    }
}
