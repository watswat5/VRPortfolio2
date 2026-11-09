// Builds Assets/Player/VRPlayer.prefab: the player, a hovering robot driven from the headset and controllers.
//   XR Origin (floor tracking; XRMovement: smooth movement and snap turning, in a CharacterController; FlatControls for
//   testing without a headset)
//     Camera Offset
//       Main Camera: TrackedPoseDriver (the headset); leaves the MirrorOnly layer out, so the robot's own head
//       Left / Right Controller: TrackedPoseDriver (grip pose), XR direct interactor (grip grabs, trigger activates)
//     Robot: PlayerAvatar (the robot follows the headset and the hands), RobotLooks (3 bodies x 2 heads), PlayerMic
//     (the stage mic in the right fist, on the stage), Outfit (hat,
//       eyes, ears, neck, wrist; paintable shell), RobotFace (LED eyes blink, the LED mouth follows the voice: uLipSync)
//   XR Interaction Manager
// The robot's parts are low-poly models from art/robot/make_robot.py (Blender: Assets/Player/Models/robot_parts.fbx), put
// on a small skeleton (hips, chest, neck, head; shoulder, elbow, hand each side; the layout of the class's
// MultiInteractionExample2026 robot): three bodies (Skinny, Standard, Chonk) and two heads (the boxy one and a round one),
// mitten hands. Its head and anything worn on it are on MirrorOnly. The LED face is art/led_mouth's dot sheets (lit
// dots only) on the head's visor. Freestyle > Build VR Player.
using System.IO;
using Freestyle.Player;
using Freestyle.Wardrobe;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using UnityEngine.XR.Interaction.Toolkit.UI;
using UnityEngine.EventSystems;

public static class PlayerBuilder
{
    public const string Dir = "Assets/Player", PrefabPath = Dir + "/VRPlayer.prefab";
    const string Face = Dir + "/Face", Art = "../art/led_mouth";

    static Material shell, joint, visor, glow, ledEyes, ledMouth;
    static int mirrorOnly;

    [MenuItem("Freestyle/Build VR Player")]
    public static void Build()
    {
        if (EditorApplication.isPlaying) { EditorUtility.DisplayDialog("Build VR player", "Stop Play mode first.", "OK"); return; }
        EnsureLayer("MirrorOnly");
        mirrorOnly = LayerMask.NameToLayer("MirrorOnly");
        Folder(Dir); Folder(Face);
        Materials();

        var origin = new GameObject("XR Origin");
        try
        {
            var xr = origin.AddComponent<XROrigin>();
            var offset = new GameObject("Camera Offset").transform;
            offset.SetParent(origin.transform, false);
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            camGo.transform.SetParent(offset, false);
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.03f;
            cam.cullingMask &= ~(1 << mirrorOnly);   // your own head (and what's on it) is for the mirror
            camGo.AddComponent<AudioListener>();
            Track(camGo, "<XRHMD>/centerEyePosition", "<XRHMD>/centerEyeRotation", "<XRHMD>/trackingState");
            xr.Camera = cam;
            xr.Origin = origin;
            xr.CameraFloorOffsetObject = offset.gameObject;
            xr.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;
            xr.CameraYOffset = 1.65f;   // without a headset: standing eye height
            var left = Controller(offset, "Left");
            var right = Controller(offset, "Right");
            Pointer(offset, "Left");
            Pointer(offset, "Right");
            // menus: the rays (and on the flat screen the mouse) click them
            var events = new GameObject("EventSystem");
            events.transform.SetParent(origin.transform, false);
            events.AddComponent<EventSystem>();
            events.AddComponent<XRUIInputModule>();
            var flat = origin.AddComponent<FlatControls>();
            flat.cameraTransform = camGo.transform;
            flat.cameraOffset = offset;
            var capsule = origin.AddComponent<CharacterController>();   // XRMovement sizes it to the headset every frame
            capsule.radius = 0.2f; capsule.height = 1.65f; capsule.center = new Vector3(0, 0.825f, 0);
            capsule.stepOffset = 0.3f; capsule.skinWidth = 0.02f; capsule.minMoveDistance = 0f;
            origin.AddComponent<XRMovement>().head = camGo.transform;

            var manager = new GameObject("XR Interaction Manager").AddComponent<XRInteractionManager>();
            manager.transform.SetParent(origin.transform, false);

            Robot(origin.transform, camGo.transform, left, right);
            var settings = origin.AddComponent<SettingsMenu>();   // the left menu button
            settings.head = camGo.transform;
            settings.movement = origin.GetComponent<XRMovement>();
            settings.avatar = origin.GetComponentInChildren<PlayerAvatar>();
            PrefabUtility.SaveAsPrefabAsset(origin, PrefabPath);
        }
        finally { Object.DestroyImmediate(origin); }
        AssetDatabase.SaveAssets();
        Debug.Log($"VR player built: {PrefabPath}");
    }

    // ---------------------------------------------------------------- rig

    static void Track(GameObject go, string pos, string rot, string state)
    {
        var t = go.AddComponent<TrackedPoseDriver>();
        t.positionInput = new InputActionProperty(new InputAction(go.name + " Position", InputActionType.Value, pos, expectedControlType: "Vector3"));
        t.rotationInput = new InputActionProperty(new InputAction(go.name + " Rotation", InputActionType.Value, rot, expectedControlType: "Quaternion"));
        t.trackingStateInput = new InputActionProperty(new InputAction(go.name + " Tracking State", InputActionType.Value, state, expectedControlType: "Integer"));
    }

    /// <summary>A ray pointer for the menus, from the controller's aim pose: trigger clicks and drags (lists scroll by dragging; the sticks are for moving). It
    /// can't grab anything (no interaction layers) and only sees the UI layer, where the menus are (that mask is also
    /// what it hands the menus' raycaster: nothing in it, no menus). Its line shows only while it's on a menu.</summary>
    static void Pointer(Transform parent, string side)
    {
        var go = new GameObject($"{side} Pointer");
        go.transform.SetParent(parent, false);
        string dev = $"<XRController>{{{side}Hand}}";
        Track(go, dev + "/pointerPosition", dev + "/pointerRotation", dev + "/trackingState");
        var ray = go.AddComponent<XRRayInteractor>();
        ray.handedness = side == "Left" ? InteractorHandedness.Left : InteractorHandedness.Right;
        ray.interactionLayers = 0;    // menus only: hats and doors are for the hands
        ray.raycastMask = 1 << Freestyle.UI.UIKit.Layer;   // the menus (no colliders there: walls and props don't stop it)
        ray.enableUIInteraction = true;
        ray.maxRaycastDistance = 8f;
        Button(ray.uiPressInput, $"{side} UI Press", dev + "/triggerPressed", dev + "/trigger");
        ray.uiScrollInput.inputSourceMode = XRInputValueReader.InputSourceMode.Unused;   // lists scroll by dragging: the sticks move you
        var line = go.AddComponent<LineRenderer>();
        line.material = AssetDatabase.LoadAssetAtPath<Material>(RayMaterial()) ;
        line.numCapVertices = 4;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        var visual = go.AddComponent<XRInteractorLineVisual>();
        visual.lineWidth = 0.004f;
        visual.validColorGradient = Fade(new Color(0.2f, 0.95f, 0.9f), 0.9f, 0.4f);    // on a menu: a turquoise line to it
        visual.invalidColorGradient = Fade(Color.white, 0f, 0f);                        // anywhere else: nothing
    }

    static Gradient Fade(Color c, float from, float to)
    {
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(c, 0f), new GradientColorKey(c, 1f) },
                  new[] { new GradientAlphaKey(from, 0f), new GradientAlphaKey(to, 1f) });
        return g;
    }

    /// <summary>The pointers' line material (vertex colours with alpha: Sprites/Default), made once.</summary>
    static string RayMaterial()
    {
        string path = Dir + "/PointerLine.mat";
        if (!AssetDatabase.LoadAssetAtPath<Material>(path))
            AssetDatabase.CreateAsset(new Material(Shader.Find("Sprites/Default")) { name = "PointerLine" }, path);
        return path;
    }

    static Transform Controller(Transform parent, string side)
    {
        var go = new GameObject($"{side} Controller");
        go.transform.SetParent(parent, false);
        string dev = $"<XRController>{{{side}Hand}}";
        Track(go, dev + "/devicePosition", dev + "/deviceRotation", dev + "/trackingState");
        var rb = go.AddComponent<Rigidbody>();
        rb.isKinematic = true; rb.useGravity = false;
        var reach = go.AddComponent<SphereCollider>();
        reach.isTrigger = true; reach.radius = 0.07f;
        var direct = go.AddComponent<XRDirectInteractor>();
        direct.handedness = side == "Left" ? InteractorHandedness.Left : InteractorHandedness.Right;
        Button(direct.selectInput, $"{side} Grip", dev + "/gripPressed", dev + "/grip");
        Button(direct.activateInput, $"{side} Trigger", dev + "/triggerPressed", dev + "/trigger");
        return go.transform;
    }

    static void Button(XRInputButtonReader reader, string name, string pressed, string value)
    {
        reader.inputSourceMode = XRInputButtonReader.InputSourceMode.InputAction;
        reader.inputActionPerformed = new InputAction(name, InputActionType.Button, pressed);
        reader.inputActionValue = new InputAction(name + " Value", InputActionType.Value, value, expectedControlType: "Axis");
    }

    // ---------------------------------------------------------------- the robot

    const string Parts = Dir + "/Models/robot_parts.fbx";   // art/robot/make_robot.py (Blender)
    const float VisorFront = 0.10f, EyeY = 0.10f;           // as in make_robot.py
    const float RoundHatScale = 0.85f;   // hats on the round head, against the standard head's (1): see HeadEntry
    static System.Collections.Generic.Dictionary<string, Mesh> meshes;

    /// <summary>A part from robot_parts.fbx on parent (its pivot is the parent's origin); kept in the paintable list
    /// when it's shell, with a trigger collider around it so spray paint finds the body.</summary>
    static GameObject Piece(string mesh, Transform parent, Material mat, Outfit paint = null, bool collider = false)
    {
        var go = new GameObject(mesh);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = meshes[mesh];
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        if (paint) paint.paintable.Add(r);
        if (collider) { var b = go.AddComponent<BoxCollider>(); b.isTrigger = true; }
        return go;
    }

    static void Robot(Transform parent, Transform hmd, Transform left, Transform right)
    {
        AssetDatabase.ImportAsset(Parts, ImportAssetOptions.ForceUpdate);
        var imp = (ModelImporter)AssetImporter.GetAtPath(Parts);
        imp.materialImportMode = ModelImporterMaterialImportMode.None;
        imp.animationType = ModelImporterAnimationType.None;
        imp.importBlendShapes = false;
        imp.SaveAndReimport();
        meshes = new System.Collections.Generic.Dictionary<string, Mesh>();
        foreach (var m in AssetDatabase.LoadAllAssetsAtPath(Parts)) if (m is Mesh mesh) meshes[mesh.name] = mesh;

        var root = new GameObject("Robot").transform;
        root.SetParent(parent, false);
        Transform Bone(string n, Transform p, Vector3 pos, Quaternion rot)
        {
            var t = new GameObject(n).transform; t.SetParent(p); t.SetPositionAndRotation(pos, rot); return t;
        }
        var id = Quaternion.identity;
        var hips = Bone("Hips", root, new Vector3(0, 0.95f, 0), id);
        var chest = Bone("Chest", hips, new Vector3(0, 1.32f, 0), id);
        var neck = Bone("Neck", chest, new Vector3(0, 1.50f, 0), id);
        var head = Bone("Head", neck, new Vector3(0, 1.58f, 0), id);
        Transform lu = Bone("L Upper Arm", chest, new Vector3(-0.2f, 1.43f, 0), id), ll = Bone("L Forearm", lu, new Vector3(-0.47f, 1.43f, 0), id),
                  lh = Bone("L Hand", ll, new Vector3(-0.72f, 1.43f, 0), Quaternion.LookRotation(Vector3.left, Vector3.up));
        Transform ru = Bone("R Upper Arm", chest, new Vector3(0.2f, 1.43f, 0), id), rl = Bone("R Forearm", ru, new Vector3(0.47f, 1.43f, 0), id),
                  rh = Bone("R Hand", rl, new Vector3(0.72f, 1.43f, 0), Quaternion.LookRotation(Vector3.right, Vector3.up));

        var outfit = root.gameObject.AddComponent<Outfit>();
        var looks = root.gameObject.AddComponent<RobotLooks>();
        looks.outfit = outfit;
        looks.leftShoulder = lu; looks.rightShoulder = ru;

        // arms: rods stretched between the joints (LimbVisual), dark balls at the shoulders and elbows
        var upper = new System.Collections.Generic.List<LimbVisual>();
        var fore = new System.Collections.Generic.List<LimbVisual>();
        foreach (var (a0, b0, isUpper) in new[] { (lu, ll, true), (ll, lh, false), (ru, rl, true), (rl, rh, false) })
        {
            var g = Piece("Limb", root, shell, outfit);
            g.name = a0.name + " Visual";
            var lv = g.AddComponent<LimbVisual>(); lv.from = a0; lv.to = b0;
            (isUpper ? upper : fore).Add(lv);
        }
        looks.upperArms = upper.ToArray(); looks.forearms = fore.ToArray();
        foreach (var j in new[] { lu, ll, ru, rl }) Piece("Joint", j, joint).transform.localScale = Vector3.one * 0.075f;

        // mitten hands: palm, one wide three-jointed finger, a thumb; the finger curls with the grip (PlayerAvatar)
        Transform[] Mitten(Transform hand, string side, out Transform thumb)
        {
            Piece($"Hand_{side}_Cuff", hand, joint);
            Piece($"Hand_{side}_Palm", hand, shell, outfit);
            var segs = new Transform[3];
            Transform at = hand; float z = 0.084f;
            for (int k = 0; k < 3; k++)
            {
                var seg = new GameObject($"Finger {k + 1}").transform;
                seg.SetParent(at, false);
                seg.localPosition = new Vector3(0, 0, z);
                Piece("Finger_Seg", seg, shell, outfit);
                segs[k] = seg; at = seg; z = 0.034f;
            }
            thumb = new GameObject("Thumb").transform;
            thumb.SetParent(hand, false);
            float sx = side == "L" ? 1f : -1f;
            thumb.localPosition = new Vector3(0.042f * sx, 0.004f, 0.03f);
            thumb.localRotation = Quaternion.Euler(0, 35f * sx, 0);   // angled out and forward from the side of the palm
            Piece($"Hand_{side}_Thumb", thumb, shell, outfit);
            return segs;
        }

        // bodies (on the chest, so they turn with it), each with its own shoulders and arm thickness
        Transform Spot(string n, Transform p, Vector3 local, float scale = 1f)
        {
            var t = new GameObject(n).transform; t.SetParent(p, false); t.localPosition = local; t.localScale = Vector3.one * scale; return t;
        }
        // a body, with its necklace spot on top of its torso (at the base of the neck, where the chain goes round; the
        // necklace drapes down its front), scaled to the torso's size
        RobotLooks.Body BodyOf(string name, string description, Vector3 shoulder, float upperArm, float forearm, float torsoTop, float neckScale, float wristScale)
        {
            var g = new GameObject($"Body {name}").transform;
            g.SetParent(chest, false);
            Piece($"Body_{name}_Shell", g, shell, outfit, collider: true);
            Piece($"Body_{name}_Dark", g, joint);
            Piece($"Body_{name}_Light", g, glow);
            return new RobotLooks.Body
            {
                name = description, visual = g.gameObject, shoulder = shoulder, upperArm = upperArm, forearm = forearm,
                neckSpot = Spot("Necklace", g, new Vector3(0, torsoTop, 0), neckScale), wristScale = wristScale,
            };
        }
        looks.bodies = new[]   // torso tops as in make_robot.py (in the chest's space)
        {
            BodyOf("Skinny", "a skinny body", new Vector3(0.15f, 0.12f, 0), 0.038f, 0.032f, 0.14f, 0.75f, 0.7f),
            BodyOf("Standard", "a standard body", new Vector3(0.2f, 0.11f, 0), 0.07f, 0.062f, 0.15f, 1f, 1f),
            BodyOf("Chonk", "a big round chonky body", new Vector3(0.33f, 0.05f, 0), 0.12f, 0.1f, 0.15f, 1.5f, 1.4f),   // on the round top, where it meets a necklace this big
        };

        // heads (on the head bone, MirrorOnly): shell, dark ear pods and neck, a glossy visor with the LED face on it
        var face = root.gameObject.AddComponent<RobotFace>();
        var headSizes = new System.Collections.Generic.Dictionary<string, (float w, float h, float d, float vw, float vh)>   // as in make_robot.py
        {
            ["Skinny"] = (0.15f, 0.19f, 0.16f, 0.115f, 0.125f), ["Standard"] = (0.22f, 0.18f, 0.19f, 0.17f, 0.115f),
            ["Chonk"] = (0.27f, 0.22f, 0.22f, 0.215f, 0.145f),
            ["Round"] = (0.24f, 0.23f, 0.23f, 0.165f, 0.11f),   // the ball head (make_robot.py ROUND)
        };
        GameObject HeadOf(string name)
        {
            var g = new GameObject($"Head {name}").transform;
            g.SetParent(head, false);
            Piece($"Head_{name}_Shell", g, shell, outfit, collider: true);
            Piece($"Head_{name}_Visor", g, visor);
            Piece($"Head_{name}_Dark", g, joint);
            // flat on the visor, inside it: the panels (eyes over mouth: 0.925 of their width tall, plus a gap) fit its height
            var sz = headSizes[name];
            float width = Mathf.Min(sz.vw * 0.85f, (sz.vh * 0.86f - 0.01f) / 0.925f);
            Panels(name, g, face, EyeY, width, (x, y) => new Vector3(x, y, VisorFront + 0.001f));
            SetLayer(g.gameObject, mirrorOnly);
            return g.gameObject;
        }
        // a head, with its hat (on top), eyes (in front of the visor) and ears (its middle) spots, each scaled to the head
        // (the standard head's are 1: what the hats were sized for; worn things take their spot's scale), so a hat fits the
        // narrow head and the big one alike
        var standard = headSizes["Standard"];
        RobotLooks.Head HeadEntry(string name, string description)
        {
            var (w, h, d, vw, _) = headSizes[name];
            float top = EyeY + 0.005f + h / 2, zc = VisorFront - 0.012f - d / 2;
            float across = w / standard.w, visorAcross = vw / standard.vw;
            float hatY = top, hatScale = across;
            if (name == "Round")
            {
                // A ball narrows to a point on top, where a hat's opening (sized for the standard head's flat top) can only
                // perch. Smaller, and down to where the ball is as wide and deep as the opening: the hat's pivot is the
                // middle of its opening (AccessoryTools).
                hatScale = RoundHatScale;
                float fit = hatScale * Mathf.Max(standard.w / w, standard.d / d);   // the opening over the ball's section
                hatY = EyeY + 0.005f + h / 2 * Mathf.Sqrt(1f - fit * fit);
            }
            var g = HeadOf(name).transform;
            return new RobotLooks.Head
            {
                name = description, visual = g.gameObject,
                hatSpot = Spot("Hat", g, new Vector3(0, hatY, zc), hatScale),
                eyesSpot = Spot("Eyes", g, new Vector3(0, EyeY, VisorFront + 0.02f), visorAcross),
                earsSpot = Spot("Ears", g, new Vector3(0, EyeY + 0.005f, zc), across),
            };
        }
        looks.heads = new[]   // two heads, either on any body: the boxy one and the round one
        {
            HeadEntry("Standard", "a boxy head with ear pods"),
            HeadEntry("Round", "a round ball head"),
        };

        // the outfit's locations start on the standard head and body (RobotLooks moves them to the shapes that are on);
        // the wrist is on the left forearm at the hand; all face the robot's forward
        outfit.hat = looks.heads[0].hatSpot;
        outfit.eyes = looks.heads[0].eyesSpot;
        outfit.ears = looks.heads[0].earsSpot;
        outfit.neck = looks.bodies[1].neckSpot;
        outfit.wrist = Spot("Wrist", ll, ll.InverseTransformPoint(lh.position));
        outfit.wrist.rotation = Quaternion.LookRotation(Vector3.left, Vector3.up);   // along the forearm, out to the hand
        looks.body = 1; looks.head = 0;   // the standard body, the boxy head

        // the mouth hears the voice: uLipSync on the head, playing the shared mic silently
        var voice = new GameObject("Voice");
        voice.transform.SetParent(head, false);
        var src = voice.AddComponent<AudioSource>();
        src.playOnAwake = false; src.loop = true; src.spatialBlend = 0f;
        var uls = voice.AddComponent<uLipSync.uLipSync>();
        uls.profile = AssetDatabase.LoadAssetAtPath<uLipSync.Profile>("Packages/com.hecomi.ulipsync/Assets/Profiles/uLipSync-Profile-Sample-Male.asset");
        uls.outputSoundGain = 0f;
        face.lipSync = uls;

        // drive it
        var a = root.gameObject.AddComponent<PlayerAvatar>();
        // the stage mic: in the right fist whenever they're on the stage (Freestyle > Build Mic makes its prefab)
        var stageMic = root.gameObject.AddComponent<PlayerMic>();
        stageMic.avatar = a;
        stageMic.micPrefab = MicBuilder.Ensure();
        a.hmd = hmd; a.trackingSpace = parent; a.leftController = left; a.rightController = right;
        a.head = head; a.chest = chest; a.spine = new Transform[0];
        a.leftUpper = lu; a.leftLower = ll; a.leftHand = lh; a.rightUpper = ru; a.rightLower = rl; a.rightHand = rh;
        a.leftMitten = Mitten(lh, "L", out a.leftThumb);
        a.rightMitten = Mitten(rh, "R", out a.rightThumb);
        a.hmdToHead = new Vector3(0, -0.1f, -0.09f);
    }

    static float Sq(float v) => v * v;

    /// <summary>The LED eyes and mouth on a head's face, centred at height y, the eyes panel width wide (each panel is
    /// 16 x 8 LEDs, 2:1), the mouth a little narrower below it. surface(x, y) is the face in the head's space (just out
    /// from it), so the panels bend with a curved head.</summary>
    static void Panels(string name, Transform head, RobotFace face, float y, float width, System.Func<float, float, Vector3> surface)
    {
        float eh = width / 2, mw = width * 0.85f, mh = mw / 2, gap = 0.01f, top = y + (eh + gap + mh) / 2;
        Panel(name + " Eyes", head, top - eh, width, eh, surface, ledEyes, face.eyes);
        Panel(name + " Mouth", head, top - eh - gap - mh, mw, mh, surface, ledMouth, face.mouths);
    }

    /// <summary>A panel mesh laid on the surface: w x h, its bottom at y0, centred on x 0. Seen from the front (+Z), its
    /// texture's left is on the viewer's left (the robot's right, +X).</summary>
    static void Panel(string name, Transform parent, float y0, float w, float h, System.Func<float, float, Vector3> surface,
                      Material mat, System.Collections.Generic.List<Renderer> into)
    {
        const int nx = 16, ny = 8;
        var verts = new Vector3[(nx + 1) * (ny + 1)];
        var uvs = new Vector2[verts.Length];
        for (int j = 0; j <= ny; j++)
            for (int i = 0; i <= nx; i++)
            {
                float u = i / (float)nx, v = j / (float)ny;
                verts[j * (nx + 1) + i] = surface(w / 2 - u * w, y0 + v * h);
                uvs[j * (nx + 1) + i] = new Vector2(u, v);
            }
        var tris = new int[nx * ny * 6];
        for (int j = 0, k = 0; j < ny; j++)
            for (int i = 0; i < nx; i++, k += 6)
            {
                int a = j * (nx + 1) + i, b = a + 1, c = a + nx + 1, d = c + 1;
                tris[k] = a; tris[k + 1] = c; tris[k + 2] = b; tris[k + 3] = b; tris[k + 4] = c; tris[k + 5] = d;
            }
        var mesh = new Mesh { name = name, vertices = verts, uv = uvs, triangles = tris };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        string path = $"{Face}/{name}.asset";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(mesh, path);
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        into.Add(r);
    }

    static void SetLayer(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform c in go.transform) SetLayer(c.gameObject, layer);
    }

    // ---------------------------------------------------------------- assets

    static void Materials()
    {
        var lit = Shader.Find("Universal Render Pipeline/Lit");
        var unlit = Shader.Find("Universal Render Pipeline/Unlit");
        shell = Mat("RobotShell", lit, new Color(0.83f, 0.81f, 0.77f), 0.35f, 0.05f);   // warm light grey (the sheet)
        joint = Mat("RobotJoint", lit, new Color(0.13f, 0.13f, 0.14f), 0.4f, 0.2f);
        visor = Mat("RobotVisor", lit, new Color(0.02f, 0.02f, 0.025f), 0.92f, 0.1f);    // glossy black: the LEDs show on it
        glow = Mat("RobotGlow", unlit, new Color(0.35f, 0.92f, 1f), 0, 0);
        ledEyes = Led("LedEyes", "led_eyes.png", unlit);
        ledMouth = Led("LedMouth", "led_mouth.png", unlit);
    }

    static Material Mat(string name, Shader shader, Color c, float smooth, float metal)
    {
        string path = $"{Dir}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (!m) { m = new Material(shader) { name = name }; AssetDatabase.CreateAsset(m, path); }
        m.shader = shader;
        m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
        EditorUtility.SetDirty(m);
        return m;
    }

    /// <summary>An LED panel: the art's sheet of LED dots (white on transparent), its faint unlit dots dropped, see-through
    /// so only the lit dots show on the head, unlit, tinted by RobotFace; one frame shown at a time.</summary>
    static Material Led(string name, string sheet, Shader unlit)
    {
        string dst = $"{Face}/{sheet}";
        var tex = new Texture2D(2, 2);
        tex.LoadImage(File.ReadAllBytes(Path.Combine(Art, sheet)));
        var px = tex.GetPixels32();
        for (int i = 0; i < px.Length; i++) if (px[i].a < 48) px[i] = new Color32(0, 0, 0, 0);   // drop the unlit dots (alpha 0.1)
        tex.SetPixels32(px);
        File.WriteAllBytes(dst, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(dst);
        var ti = (TextureImporter)AssetImporter.GetAtPath(dst);
        ti.alphaIsTransparency = true;
        ti.filterMode = FilterMode.Trilinear; ti.mipmapEnabled = true; ti.wrapMode = TextureWrapMode.Clamp;
        ti.npotScale = TextureImporterNPOTScale.None; ti.maxTextureSize = 2048;
        ti.SaveAndReimport();
        var m = Mat(name, unlit, new Color(0.22f, 0.91f, 1f), 0, 0);
        m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(dst));
        m.SetTextureScale("_BaseMap", new Vector2(0.2f, 0.25f));
        m.SetTextureOffset("_BaseMap", new Vector2(0, 0.75f));   // frame 0
        // URP Unlit, transparent: blended by the texture's alpha, no depth write
        m.SetFloat("_Surface", 1); m.SetFloat("_Blend", 0); m.SetFloat("_AlphaClip", 0);
        m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One); m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0);
        m.SetFloat("_Cull", 0);   // both sides
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = (int)RenderQueue.Transparent;
        return m;
    }

    static void Folder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        AssetDatabase.CreateFolder(Path.GetDirectoryName(path).Replace('\\', '/'), Path.GetFileName(path));
    }

    /// <summary>Adds a user layer with this name if there isn't one.</summary>
    public static void EnsureLayer(string name)
    {
        var tm = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tm.FindProperty("layers");
        for (int i = 8; i < 32; i++) if (layers.GetArrayElementAtIndex(i).stringValue == name) return;
        for (int i = 31; i >= 8; i--)
            if (string.IsNullOrEmpty(layers.GetArrayElementAtIndex(i).stringValue)) { layers.GetArrayElementAtIndex(i).stringValue = name; break; }
        tm.ApplyModifiedProperties();
    }
}
