// The player's save, a JSON file on the headset (Application.persistentDataPath/freestyle_save.json): their pre-show
// interview (the answers, and the server's summary of them, which every battle sends the MCs as `profile`), the MCs
// they've beaten, and their top scores. Made by the interview (InterviewDirector) the first time they head for the
// stage; once it exists the interview is skipped. Settings > RESET SAVE deletes it (the interview comes back).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace Freestyle.Game
{
    [Serializable]
    public class SaveData
    {
        public int version = 1;
        public InterviewRecord interview = new();
        public List<string> beaten = new();          // MC ids, in the order they were beaten
        public List<ScoreRecord> topScores = new();  // best first
    }

    [Serializable]
    public class InterviewRecord
    {
        public string date = "";
        public string name = "";       // their rap name, if they gave one
        public string summary = "";    // the server's notes on them, for the opponents
        public string source = "";     // what wrote the summary ("openrouter:...", "answers", "offline")
        public List<InterviewAnswer> answers = new();
    }

    [Serializable]
    public class InterviewAnswer
    {
        public string question = "", answer = "";
    }

    [Serializable]
    public class ScoreRecord
    {
        public string date = "", mc = "", mcName = "", winner = "";
        public float score, opponentScore;
    }

    public static class SaveFile
    {
        public const int TopScores = 10;
        public static string Path => System.IO.Path.Combine(Application.persistentDataPath, "freestyle_save.json");

        static SaveData data;
        static bool loaded;

        /// <summary>Something in it changed (or it was made or deleted).</summary>
        public static event Action Changed;

        /// <summary>There's a save: the interview has been done.</summary>
        public static bool Exists { get { Load(); return data != null; } }

        /// <summary>The save, or null when there isn't one.</summary>
        public static SaveData Data { get { Load(); return data; } }

        /// <summary>The interview's summary, for the battle's start message (empty without a save).</summary>
        public static string Profile => Exists ? data.interview?.summary ?? "" : "";

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (File.Exists(Path)) data = JsonConvert.DeserializeObject<SaveData>(File.ReadAllText(Path)) ?? new SaveData();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Save file unreadable ({e.Message}): starting fresh, the old one kept as .bad");
                try { File.Copy(Path, Path + ".bad", true); } catch { /* keep going */ }
                data = null;
            }
        }

        /// <summary>Makes the save (the interview's done) or replaces its interview.</summary>
        public static void Create(InterviewRecord interview)
        {
            Load();
            data ??= new SaveData();
            data.interview = interview ?? new InterviewRecord();
            if (string.IsNullOrEmpty(data.interview.date)) data.interview.date = Now();
            Write();
        }

        /// <summary>A finished battle: into the top scores (best first, TopScores kept); a win marks the MC beaten.
        /// Nothing happens without a save.</summary>
        public static void RecordBattle(string mc, string mcName, float score, float opponentScore, string winner)
        {
            if (!Exists || string.IsNullOrEmpty(mc)) return;
            data.topScores.Add(new ScoreRecord
            {
                date = Now(), mc = mc, mcName = mcName ?? mc, winner = winner ?? "",
                score = Mathf.Round(score * 10) / 10, opponentScore = Mathf.Round(opponentScore * 10) / 10,
            });
            data.topScores = data.topScores.OrderByDescending(s => s.score).Take(TopScores).ToList();
            if (winner == "user" && !data.beaten.Contains(mc)) data.beaten.Add(mc);
            Write();
        }

        public static bool HasBeaten(string mc) => Exists && data.beaten.Contains(mc);

        /// <summary>Writes the save after editing Data in place (the player sheet).</summary>
        public static void Save() { if (Exists) Write(); }

        /// <summary>Deletes the save: the next trip to the stage starts with the interview again.</summary>
        public static void Delete()
        {
            try { if (File.Exists(Path)) File.Delete(Path); } catch (Exception e) { Debug.LogWarning($"Couldn't delete the save: {e.Message}"); }
            data = null;
            loaded = true;
            Changed?.Invoke();
        }

        static void Write()
        {
            try
            {
                string tmp = Path + ".tmp";   // written whole, then swapped in, so a crash can't leave half a file
                File.WriteAllText(tmp, JsonConvert.SerializeObject(data, Formatting.Indented));
                if (File.Exists(Path)) File.Delete(Path);
                File.Move(tmp, Path);
            }
            catch (Exception e) { Debug.LogWarning($"Couldn't write the save: {e.Message}"); }
            Changed?.Invoke();
        }

        static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm");
    }
}
