// The microphone, shared: Unity records a device once, so everything that listens (the battle streaming it to the
// server, the robot's lip sync) takes the same looping clip from here. The first to ask starts it; it stops when the
// last lets go. Everyone should ask for the same device and rate (the first request wins; read Clip.frequency).
using UnityEngine;

namespace Freestyle.Audio
{
    public static class SharedMic
    {
        static AudioClip clip;
        static string device;
        static int users;

        /// <summary>The recording clip (looping), or null when nobody is listening.</summary>
        public static AudioClip Clip => clip;
        /// <summary>The device being recorded (null: the default one).</summary>
        public static string Device => device;
        /// <summary>Where the recording is in the clip now (samples).</summary>
        public static int Position => clip ? Microphone.GetPosition(device) : 0;

        /// <summary>Starts the mic if it isn't already and counts you in; null if it couldn't start.</summary>
        public static AudioClip Acquire(string dev, int rate, int seconds = 4)
        {
            if (clip && !Microphone.IsRecording(device)) Restart();   // (Android can stop it: the app paused, a reload)
            if (clip)
            {
                users++;
                return clip;
            }
            if (Microphone.devices.Length == 0) return null;
            clip = Microphone.Start(dev, true, seconds, rate);
            if (!clip) return null;
            device = dev;
            users = 1;
            return clip;
        }

        /// <summary>Starts the recording again, for everyone listening (a new Clip: read it again). For a mic that has
        /// stopped, or stalled: on Quest a mic started again just after it was ended can sit still.</summary>
        public static AudioClip Restart()
        {
            if (!clip) return null;
            int rate = clip.frequency, seconds = Mathf.Max(1, clip.samples / Mathf.Max(1, rate));
            Microphone.End(device);
            Object.Destroy(clip);
            clip = Microphone.Start(device, true, seconds, rate);
            if (!clip) users = 0;
            Debug.LogWarning($"[Mic] restarted ({(clip ? "recording" : "failed")})");
            return clip;
        }

        /// <summary>Stops the mic whoever is listening (the scene is going: GameReset).</summary>
        public static void Reset()
        {
            if (clip) { Microphone.End(device); Object.Destroy(clip); }
            clip = null;
            users = 0;
        }

        /// <summary>You're done listening; the mic stops when everyone is.</summary>
        public static void Release()
        {
            if (!clip || --users > 0) return;
            Microphone.End(device);
            Object.Destroy(clip);
            clip = null;
            users = 0;
        }
    }
}
