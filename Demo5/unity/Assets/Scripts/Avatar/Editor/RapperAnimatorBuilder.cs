// Builds Assets/Avatars/Rocketbox/Rapper.controller, the Animator controller every rapper uses: each clip in Moves
// imported onto the Humanoid rig (in place; looping or not) and given a state named after it, in a sub-state machine per
// slot (idle, laughs, rapping, ...). The plain idle is the default state; layer 0 has IK Pass on (LookAndBlink, HoldMic).
// No transitions: Performer (or a test) cross-fades between states by path. A clip named <file>_mirror is that file's
// clip played mirrored (left for right: the DJ waves with the hand that isn't holding the mic).
// Freestyle > Build Rapper Animator. Rebuilding keeps the asset (and its GUID), so scenes and prefabs stay wired.
using System.Linq;
using Freestyle.Avatar;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class RapperAnimatorBuilder
{
    public const string Path = "Assets/Avatars/Rocketbox/Rapper.controller";
    const string Anims = "Assets/Avatars/Rocketbox/Animations", RigFrom = "Assets/Avatars/Rocketbox/Male_Adult_20/Male_Adult_20_facial.fbx";

    [MenuItem("Freestyle/Build Rapper Animator")]
    public static AnimatorController Build()
    {
        var rig = AssetDatabase.LoadAllAssetsAtPath(RigFrom).OfType<UnityEngine.Avatar>().First();
        foreach (var m in Moves.All.GroupBy(m => File(m.clip)).Select(g => g.First())) ImportClip(File(m.clip), m.loop, rig);

        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(Path) ?? AnimatorController.CreateAnimatorControllerAtPath(Path);
        var layers = ctrl.layers;
        layers[0].iKPass = true;
        ctrl.layers = layers;
        var sm = ctrl.layers[0].stateMachine;
        foreach (var s in sm.states.ToArray()) sm.RemoveState(s.state);
        foreach (var s in sm.stateMachines.ToArray()) sm.RemoveStateMachine(s.stateMachine);

        int row = 0;
        foreach (var slot in Moves.Slots)
        {
            var sub = sm.AddStateMachine(slot, new Vector3(300 + 260 * (row % 3), 60 * (row / 3) * 2, 0));
            row++;
            int i = 0;
            foreach (var m in Moves.InSlot(slot))
            {
                var motion = AssetDatabase.LoadAllAssetsAtPath($"{Anims}/{File(m.clip)}.fbx").OfType<AnimationClip>().First(a => !a.name.StartsWith("__preview"));
                var state = sub.AddState(m.clip, new Vector3(300, 60 * i++, 0));
                state.motion = motion;
                state.mirror = m.clip != File(m.clip);
                if (m.clip == Moves.InSlot(Moves.Idle)[0].clip) sm.defaultState = state;
            }
        }
        EditorUtility.SetDirty(ctrl);
        AssetDatabase.SaveAssets();
        Debug.Log($"Rapper animator built: {Path} ({Moves.All.Length} clips)");
        return ctrl;
    }

    /// <summary>The clip's file (a mirrored clip's is the clip's own).</summary>
    static string File(string clip) => clip.EndsWith("_mirror") ? clip[..^"_mirror".Length] : clip;

    /// <summary>A Rocketbox clip on the Humanoid rig, in place; looping or not.</summary>
    static void ImportClip(string clip, bool loop, UnityEngine.Avatar rig)
    {
        var imp = (ModelImporter)AssetImporter.GetAtPath($"{Anims}/{clip}.fbx");
        imp.animationType = ModelImporterAnimationType.Human;
        imp.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
        imp.sourceAvatar = rig;
        imp.importBlendShapes = false;
        imp.materialImportMode = ModelImporterMaterialImportMode.None;
        var clips = imp.defaultClipAnimations;
        foreach (var c in clips)
        {
            c.loopTime = loop; c.loopPose = loop;
            c.lockRootRotation = c.lockRootHeightY = c.lockRootPositionXZ = true;   // stays on its spot
            c.keepOriginalOrientation = c.keepOriginalPositionY = c.keepOriginalPositionXZ = true;
        }
        imp.clipAnimations = clips;
        imp.SaveAndReimport();
    }
}
