// Imports .ild laser show files as IldaClips (drop one in the project and assign it to a LaserProjector). Problems
// the reader finds (an unknown format, a truncated file) show as import warnings; the frames before them still play.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Freestyle.Venue.Lasers
{
    [ScriptedImporter(1, "ild")]
    public class IldaImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            var bytes = File.ReadAllBytes(ctx.assetPath);
            var warnings = new List<string>();
            var frames = Ilda.Parse(bytes, warnings);
            var clip = ScriptableObject.CreateInstance<IldaClip>();
            clip.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            clip.data = bytes;
            clip.frameCount = frames.Count;
            clip.maxPoints = frames.Count > 0 ? frames.Max(f => f.points.Length) : 0;
            foreach (var w in warnings) ctx.LogImportWarning($"{clip.name}: {w}");
            if (frames.Count == 0) ctx.LogImportWarning($"{clip.name}: no frames");
            ctx.AddObjectToAsset("show", clip);
            ctx.SetMainObject(clip);
        }
    }
}
