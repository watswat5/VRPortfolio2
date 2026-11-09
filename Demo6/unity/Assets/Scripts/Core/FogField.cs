using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace WifiHeatmap.Core
{
    /// <summary>Bounded display interpolation of measured cell RSSI, not an RF propagation model.
    /// A compact kernel leaves areas outside measured support empty. Confidence is independent of RSSI.</summary>
    public static class FogField
    {
        public const int MaximumAxis = 64;
        public readonly struct Point
        {
            public readonly Vector3 Position;
            public readonly float Dbm, Confidence;
            public Point(Vector3 position, float dbm, float confidence)
            { Position = position; Dbm = dbm; Confidence = confidence; }
        }
        public sealed class Field
        {
            public int Width, Height, Depth;
            // RGBA: normalized RSSI * coverage, confidence * coverage, coverage, unused.
            // Premultiplication prevents interpolation against empty texels from looking like weak RSSI.
            public float[] Channels;
            public float SupportRadius;
        }
        public static Field Build(IReadOnlyList<Point> points, Vector3 minimum, Vector3 size, float resolution,
            CancellationToken cancellation)
        {
            if (!Finite(minimum) || !Finite(size) || size.X <= 0 || size.Y <= 0 || size.Z <= 0 ||
                float.IsNaN(resolution) || float.IsInfinity(resolution) || resolution < .02f || resolution > .5f)
                throw new ArgumentOutOfRangeException(nameof(size), "A finite volume and valid cell resolution are required.");
            float spacing = Math.Max(resolution, Math.Max(size.X,Math.Max(size.Y,size.Z))/(MaximumAxis-1));
            int w=Dimension(size.X,spacing), h=Dimension(size.Y,spacing), d=Dimension(size.Z,spacing);
            Vector3 step = new Vector3(size.X/w,size.Y/h,size.Z/d);
            float radius = Math.Max(resolution*2,Math.Max(step.X,Math.Max(step.Y,step.Z))*1.5f);
            int count=w*h*d;
            var weights=new float[count]; var signal=new float[count]; var quality=new float[count]; var coverage=new float[count];
            float radiusSquared=radius*radius;
            for (int p=0;p<points.Count;p++)
            {
                if ((p&63)==0) cancellation.ThrowIfCancellationRequested();
                Point point=points[p];
                if (!Finite(point.Position) || !Finite(point.Dbm) || point.Dbm < -150 || point.Dbm > 10 ||
                    !Finite(point.Confidence) || point.Confidence < 0 || point.Confidence > 1) continue;
                Vector3 local=point.Position-minimum;
                // Ignore measurements outside this room's volume, even if their kernel would overlap it.
                if (local.X<0 || local.Y<0 || local.Z<0 || local.X>size.X || local.Y>size.Y || local.Z>size.Z) continue;
                int x0=Clamp((int)Math.Floor((local.X-radius)/step.X),w), x1=Clamp((int)Math.Floor((local.X+radius)/step.X),w);
                int y0=Clamp((int)Math.Floor((local.Y-radius)/step.Y),h), y1=Clamp((int)Math.Floor((local.Y+radius)/step.Y),h);
                int z0=Clamp((int)Math.Floor((local.Z-radius)/step.Z),d), z1=Clamp((int)Math.Floor((local.Z+radius)/step.Z),d);
                float normalized=(point.Dbm+150)/160;
                for (int z=z0;z<=z1;z++) for (int y=y0;y<=y1;y++) for (int x=x0;x<=x1;x++)
                {
                    Vector3 delta = new Vector3((x+.5f)*step.X,(y+.5f)*step.Y,(z+.5f)*step.Z)-local;
                    float squared=delta.LengthSquared(); if (squared>=radiusSquared) continue;
                    float kernel=1-squared/radiusSquared; kernel*=kernel;
                    int i=x+w*(y+h*z);
                    weights[i]+=kernel; signal[i]+=kernel*normalized; quality[i]+=kernel*point.Confidence;
                    // Max support prevents overlapping cells from manufacturing denser/high-confidence fog.
                    coverage[i]=Math.Max(coverage[i],kernel);
                }
            }
            var channels=new float[count*4];
            for (int i=0;i<count;i++)
            {
                if ((i&4095)==0) cancellation.ThrowIfCancellationRequested();
                if (weights[i]<=0) continue;
                channels[i*4]=signal[i]/weights[i]*coverage[i];
                channels[i*4+1]=quality[i]/weights[i]*coverage[i];
                channels[i*4+2]=coverage[i];
            }
            return new Field { Width=w, Height=h, Depth=d, Channels=channels, SupportRadius=radius };
        }
        private static int Dimension(float size,float spacing) => Math.Max(2,Math.Min(MaximumAxis,(int)Math.Ceiling(size/spacing)));
        private static int Clamp(int value,int count) => Math.Max(0,Math.Min(count-1,value));
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.X) && Finite(value.Y) && Finite(value.Z);
    }
}
