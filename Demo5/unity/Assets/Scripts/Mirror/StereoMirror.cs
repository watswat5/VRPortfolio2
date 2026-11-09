using UnityEngine;
using UnityEngine.XR;

// From the class's MultiInteractionExample2026 (AvatarDemo), as is, except: with no head assigned it uses the main
// camera (the player is spawned separately from the venue that holds the mirror).

/// <summary>
/// A flat mirror that works in stereo. Each eye gets its own camera, placed at that eye's reflection
/// in the mirror plane and given an off-axis (asymmetric) frustum whose near plane is exactly the
/// mirror's rectangle. Each camera renders into its own texture, and the StereoMirror shader shows the
/// left texture to the left eye and the right texture to the right eye. One camera for both eyes
/// would look flat: the reflection would have no depth.
///
/// Ported from the HealXR mirror (CustumFrustum.cs) to URP and single-pass instanced rendering.
/// The mirror is a Unity Quad (visible from its -Z side) scaled to `size`.
/// </summary>
[RequireComponent(typeof(MeshRenderer))]
public class StereoMirror : MonoBehaviour
{
    public Transform head;                         // the XR camera (center eye)
    public Vector2 size = new Vector2(1.6f, 1.8f); // width and height of the mirror, meters (the quad's scale)
    public int resolution = 1024;                  // per eye
    public LayerMask reflect = ~0;                 // what the mirror shows: leave out the mirror itself
    public float fallbackIpd = 0.064f;             // used only if the runtime doesn't report eye positions
    public float far = 30f;

    readonly Camera[] cams = new Camera[2];
    readonly RenderTexture[] rts = new RenderTexture[2];

    void Start()
    {
        var mat = GetComponent<MeshRenderer>().material;
        for (int i = 0; i < 2; i++)
        {
            rts[i] = new RenderTexture(resolution, Mathf.RoundToInt(resolution * size.y / size.x), 24, RenderTextureFormat.ARGB32)
            { antiAliasing = 4, name = i == 0 ? "Mirror L" : "Mirror R" };
            var go = new GameObject(i == 0 ? "Mirror Eye L" : "Mirror Eye R");
            go.transform.SetParent(transform, false);
            var c = go.AddComponent<Camera>();
            // a plain camera rendering to a texture, not an XR camera
            if (!go.TryGetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>(out var urp))
                urp = go.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            urp.allowXRRendering = false;
            c.targetTexture = rts[i];
            c.cullingMask = reflect;
            c.depth = -10;                                  // render before the headset's camera
            cams[i] = c;
        }
        mat.SetTexture("_LeftTex", rts[0]);
        mat.SetTexture("_RightTex", rts[1]);
    }

    void LateUpdate()
    {
        if (head == null && Camera.main) head = Camera.main.transform;
        if (head == null) return;
        Vector3 left, right;
        if (!EyePositions(out left, out right))
        {
            left = head.position - head.right * fallbackIpd * 0.5f;
            right = head.position + head.right * fallbackIpd * 0.5f;
        }
        Place(cams[0], left);
        Place(cams[1], right);
    }

    // The runtime reports eye positions in tracking space; the head's parent is the tracking space here.
    bool EyePositions(out Vector3 left, out Vector3 right)
    {
        left = right = Vector3.zero;
        var dev = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
        if (!dev.isValid) return false;
        if (!dev.TryGetFeatureValue(CommonUsages.leftEyePosition, out var l)) return false;
        if (!dev.TryGetFeatureValue(CommonUsages.rightEyePosition, out var r)) return false;
        if ((l - r).sqrMagnitude < 1e-6f) return false;
        var space = head.parent;
        left = space ? space.TransformPoint(l) : l;
        right = space ? space.TransformPoint(r) : r;
        return true;
    }

    void Place(Camera cam, Vector3 eyeWorld)
    {
        // The mirror's own frame, without its scale: x right, y up, z into the mirror.
        Vector3 o = transform.position, rx = transform.right, uy = transform.up, fz = transform.forward;
        Vector3 d = eyeWorld - o;
        float ex = Vector3.Dot(d, rx), ey = Vector3.Dot(d, uy), ez = Vector3.Dot(d, fz);
        float n = -ez;                                     // distance from the eye to the mirror plane
        cam.enabled = n > 0.02f;                           // behind the mirror: nothing to show
        if (!cam.enabled) return;

        // Reflect the eye through the plane (z -> -z) and look back at the mirror.
        cam.transform.SetPositionAndRotation(o + rx * ex + uy * ey - fz * ez, Quaternion.LookRotation(-fz, uy));

        // The camera's right is the mirror's left, so the mirror's x maps to -x in camera space.
        float w = size.x * 0.5f, h = size.y * 0.5f;
        cam.nearClipPlane = n;
        cam.farClipPlane = far;
        cam.projectionMatrix = Matrix4x4.Frustum(ex - w, ex + w, -h - ey, h - ey, n, far);
    }

    void OnDestroy()
    {
        foreach (var rt in rts) if (rt) rt.Release();
    }
}
