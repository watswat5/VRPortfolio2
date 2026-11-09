// A laser show (.ild file) in the project: Unity imports .ild files as one of these (IldaImporter). It keeps the
// file's bytes and reads them into frames the first time they're asked for, so builds carry the file as it is.
using System.Collections.Generic;
using UnityEngine;

namespace Freestyle.Venue.Lasers
{
    public class IldaClip : ScriptableObject
    {
        [HideInInspector] public byte[] data;
        [Tooltip("Frames in the file (filled in on import).")]
        public int frameCount;
        [Tooltip("The most points in any one frame (filled in on import).")]
        public int maxPoints;

        List<IldaFrame> frames;

        public IReadOnlyList<IldaFrame> Frames => frames ??= Ilda.Parse(data);
    }
}
