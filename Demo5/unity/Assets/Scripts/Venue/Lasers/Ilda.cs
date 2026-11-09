// Reads ILDA Image Data Transfer Format files (.ild), the laser show industry's standard file: what Pangolin, LaserOS,
// LaserShowGen and the rest read and write. A file is a run of sections, each a 32-byte header and its records:
//
//   header   "ILDA", 3 reserved bytes, format code, name (8 ASCII), company (8 ASCII), record count (uint16),
//            frame or palette number (uint16), total frames (uint16), projector number, 1 reserved byte
//   format 0 3D point, palette colour:   X Y Z (int16 each), status, colour index            8 bytes
//   format 1 2D point, palette colour:   X Y (int16 each), status, colour index              6 bytes
//   format 2 colour palette:             R G B                                               3 bytes
//   format 4 3D point, true colour:      X Y Z (int16 each), status, blue, green, red        10 bytes
//   format 5 2D point, true colour:      X Y (int16 each), status, blue, green, red          8 bytes
//
// All numbers are big-endian. Status bit 7 marks a frame's last point, bit 6 "blanked" (the beam is off at that point,
// so the move to it isn't drawn). A header with no records ends the file. Coordinates are where the scanner mirrors aim,
// -32768..32767 each way (+X right, +Y up as the audience sees it); here they're scaled to -1..1.
// Palette colours come from the file's last format 2 section, else ILDA's default 64-colour palette.
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Freestyle.Venue.Lasers
{
    public struct IldaPoint
    {
        public Vector3 position;   // -1..1 on each axis (z 0 in a 2D file)
        public Color32 color;
        public bool blanked;       // the beam is off here (and on the way here)

        /// <summary>Drawn: not blanked, and not black (some files blank with black instead of the flag).</summary>
        public bool Lit => !blanked && (color.r | color.g | color.b) != 0;
    }

    public class IldaFrame
    {
        public string name, company;
        public int number, total, projector, format;
        public IldaPoint[] points;
    }

    public static class Ilda
    {
        /// <summary>ILDA's default palette, as the format's standard gives it: 64 colours round the hue circle from red
        /// through yellow, green, cyan, blue and magenta, then white and back to red through pinks. For files with palette
        /// colours and no palette of their own.</summary>
        public static readonly Color32[] DefaultPalette =
        {
            C(255, 0, 0), C(255, 16, 0), C(255, 32, 0), C(255, 48, 0), C(255, 64, 0), C(255, 80, 0), C(255, 96, 0), C(255, 112, 0),
            C(255, 128, 0), C(255, 144, 0), C(255, 160, 0), C(255, 176, 0), C(255, 192, 0), C(255, 208, 0), C(255, 224, 0), C(255, 240, 0),
            C(255, 255, 0), C(224, 255, 0), C(192, 255, 0), C(160, 255, 0), C(128, 255, 0), C(96, 255, 0), C(64, 255, 0), C(32, 255, 0),
            C(0, 255, 0), C(0, 255, 36), C(0, 255, 73), C(0, 255, 109), C(0, 255, 146), C(0, 255, 182), C(0, 255, 219), C(0, 255, 255),
            C(0, 227, 255), C(0, 198, 255), C(0, 170, 255), C(0, 142, 255), C(0, 113, 255), C(0, 85, 255), C(0, 56, 255), C(0, 28, 255),
            C(0, 0, 255), C(32, 0, 255), C(64, 0, 255), C(96, 0, 255), C(128, 0, 255), C(160, 0, 255), C(192, 0, 255), C(224, 0, 255),
            C(255, 0, 255), C(255, 32, 255), C(255, 64, 255), C(255, 96, 255), C(255, 128, 255), C(255, 160, 255), C(255, 192, 255), C(255, 224, 255),
            C(255, 255, 255), C(255, 224, 224), C(255, 192, 192), C(255, 160, 160), C(255, 128, 128), C(255, 96, 96), C(255, 64, 64), C(255, 32, 32),
        };

        static Color32 C(byte r, byte g, byte b) => new(r, g, b, 255);

        static int RecordSize(int format) => format switch { 0 => 8, 1 => 6, 2 => 3, 4 => 10, 5 => 8, _ => -1 };

        /// <summary>Every frame in the file, in order. Problems (an unknown format, a truncated file) are added to
        /// `warnings` and reading stops there, keeping the frames read so far.</summary>
        public static List<IldaFrame> Parse(byte[] data, List<string> warnings = null)
        {
            var frames = new List<IldaFrame>();
            if (data == null) return frames;
            Color32[] palette = DefaultPalette;
            int p = 0;
            while (p + 32 <= data.Length)
            {
                if (data[p] != 'I' || data[p + 1] != 'L' || data[p + 2] != 'D' || data[p + 3] != 'A')
                {
                    warnings?.Add($"no ILDA header at byte {p}: stopped");
                    break;
                }
                int format = data[p + 7];
                string name = Ascii(data, p + 8), company = Ascii(data, p + 16);
                int count = U16(data, p + 24), number = U16(data, p + 26), total = U16(data, p + 28), projector = data[p + 30];
                p += 32;
                if (count == 0) break;   // the end-of-file header
                int size = RecordSize(format);
                if (size < 0)
                {
                    warnings?.Add($"unknown format {format} at byte {p - 32}: stopped");
                    break;
                }
                if (p + count * size > data.Length)
                {
                    int have = (data.Length - p) / size;
                    warnings?.Add($"section \"{name}\" says {count} records, the file has {have}: truncated");
                    count = have;
                }

                if (format == 2)
                {
                    palette = new Color32[count];
                    for (int i = 0; i < count; i++)
                        palette[i] = new Color32(data[p + i * 3], data[p + i * 3 + 1], data[p + i * 3 + 2], 255);
                    p += count * size;
                    continue;
                }

                bool threeD = format == 0 || format == 4, indexed = format == 0 || format == 1;
                var points = new IldaPoint[count];
                for (int i = 0; i < count; i++)
                {
                    int q = p + i * size;
                    float x = S16(data, q) / 32768f, y = S16(data, q + 2) / 32768f, z = threeD ? S16(data, q + 4) / 32768f : 0f;
                    int s = q + (threeD ? 6 : 4);
                    byte status = data[s];
                    Color32 c;
                    if (indexed)
                    {
                        int k = data[s + 1];
                        c = k < palette.Length ? palette[k] : new Color32(255, 255, 255, 255);
                    }
                    else c = new Color32(data[s + 3], data[s + 2], data[s + 1], 255);   // stored blue, green, red
                    points[i] = new IldaPoint { position = new Vector3(x, y, z), color = c, blanked = (status & 0x40) != 0 };
                }
                p += count * size;
                frames.Add(new IldaFrame
                {
                    name = name, company = company, number = number, total = total, projector = projector, format = format,
                    points = points,
                });
            }
            return frames;
        }

        static int U16(byte[] d, int i) => (d[i] << 8) | d[i + 1];
        static short S16(byte[] d, int i) => (short)((d[i] << 8) | d[i + 1]);
        static string Ascii(byte[] d, int i) => Encoding.ASCII.GetString(d, i, 8).TrimEnd('\0', ' ');
    }
}
