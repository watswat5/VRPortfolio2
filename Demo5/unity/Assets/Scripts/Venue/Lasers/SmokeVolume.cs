// The smoke the lasers light up, shared by every projector: a looping recording of a small smoke simulation in a box
// that tiles on every side (art/lasers/make_smoke.py writes it to Resources/LaserSmoke.bytes), as one 3D texture with
// the frames stacked along z, each frame Size+1 slices deep (the last repeats the first, so filtering across the tile's
// z edge stays inside the frame). The LaserBeam shader samples it by world position and blends from frame to frame.
// Without the file, a single still frame of tiling noise stands in.
using UnityEngine;

namespace Freestyle.Venue.Lasers
{
    public static class SmokeVolume
    {
        static Texture3D texture;
        static int size, frames;

        public static Texture3D Texture { get { Load(); return texture; } }
        /// <summary>Cells along each side of the tile.</summary>
        public static int Size { get { Load(); return size; } }
        /// <summary>Frames in the loop (1: a still).</summary>
        public static int Frames { get { Load(); return frames; } }

        static void Load()
        {
            if (texture) return;
            var file = Resources.Load<TextAsset>("LaserSmoke");
            byte[] d = file ? file.bytes : null;
            if (d != null && d.Length > 12 && d[0] == 'S' && d[1] == 'M' && d[2] == 'K' && d[3] == '1')
            {
                size = System.BitConverter.ToInt32(d, 4);
                frames = System.BitConverter.ToInt32(d, 8);
                long need = (long)size * size * (size + 1) * frames;
                if (size > 0 && frames > 0 && d.Length - 12 >= need)
                {
                    var voxels = new byte[need];
                    System.Array.Copy(d, 12, voxels, 0, need);
                    texture = Make(size, (size + 1) * frames, voxels, "Laser Smoke");
                    Resources.UnloadAsset(file);
                    return;
                }
                Debug.LogWarning("LaserSmoke.bytes is short: using still noise (run art/lasers/make_smoke.py)");
            }
            // stand-in: one still frame of smooth tiling noise
            size = 32; frames = 1;
            var n = new byte[size * size * (size + 1)];
            var noise = new Noise(11);
            for (int z = 0; z <= size; z++)
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float v = noise.Fbm(new Vector3(x, y, z % size) / size) * 0.5f + 0.5f;
                        n[x + size * (y + size * z)] = (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255), 0, 255);
                    }
            texture = Make(size, size + 1, n, "Laser Smoke (noise)");
        }

        static Texture3D Make(int n, int depth, byte[] voxels, string name)
        {
            var tex = new Texture3D(n, n, depth, TextureFormat.R8, false)
            {
                name = name, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear,
            };
            tex.SetPixelData(voxels, 0);
            tex.Apply(false, true);   // (the CPU copy isn't kept)
            return tex;
        }

        /// <summary>Periodic gradient (Perlin) noise over the unit cube, so the stand-in tiles seamlessly.</summary>
        class Noise
        {
            readonly int[] perm = new int[512];
            readonly Vector3[] grads = new Vector3[256];

            public Noise(int seed)
            {
                var rng = new System.Random(seed);
                var p = new int[256];
                for (int i = 0; i < 256; i++) { p[i] = i; grads[i] = Random3(rng); }
                for (int i = 255; i > 0; i--) { int j = rng.Next(i + 1); (p[i], p[j]) = (p[j], p[i]); }
                for (int i = 0; i < 512; i++) perm[i] = p[i & 255];
            }

            static Vector3 Random3(System.Random rng)
            {
                Vector3 v;
                do v = new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1);
                while (v.sqrMagnitude > 1 || v.sqrMagnitude < 1e-3f);
                return v.normalized;
            }

            public float Fbm(Vector3 p) => 0.6f * Perlin(p * 4, 4) + 0.28f * Perlin(p * 8, 8) + 0.12f * Perlin(p * 16, 16);

            float Perlin(Vector3 p, int period)
            {
                int x0 = Mathf.FloorToInt(p.x), y0 = Mathf.FloorToInt(p.y), z0 = Mathf.FloorToInt(p.z);
                Vector3 f = p - new Vector3(x0, y0, z0);
                Vector3 u = new(Fade(f.x), Fade(f.y), Fade(f.z));
                float n = 0;
                for (int c = 0; c < 8; c++)
                {
                    int dx = c & 1, dy = (c >> 1) & 1, dz = (c >> 2) & 1;
                    int h = perm[perm[perm[Wrap(x0 + dx, period)] + Wrap(y0 + dy, period)] + Wrap(z0 + dz, period)];
                    float w = (dx == 1 ? u.x : 1 - u.x) * (dy == 1 ? u.y : 1 - u.y) * (dz == 1 ? u.z : 1 - u.z);
                    n += w * Vector3.Dot(grads[h], f - new Vector3(dx, dy, dz));
                }
                return n * 1.6f;
            }

            static int Wrap(int i, int period) => ((i % period) + period) % period;
            static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
        }
    }
}
