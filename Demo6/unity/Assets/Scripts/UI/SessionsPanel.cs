using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WifiHeatmap.Core;
using WifiHeatmap.Recording;

namespace WifiHeatmap.UI
{
    /// <summary>Config menu tab: start/stop recording, and list, load (show in the room) or delete saved sessions.</summary>
    [DisallowMultipleComponent]
    public sealed class SessionsPanel : MonoBehaviour
    {
        [Serializable]
        public sealed class Row
        {
            public GameObject root;
            public TMP_Text label;
            public Button load;
            public Button delete;
        }

        [SerializeField] private SessionRecorder recorder;
        [SerializeField] private SessionViewer viewer;
        [SerializeField] private Button recordButton;
        [SerializeField] private TMP_Text recordStatus;
        [SerializeField] private Row[] rows = new Row[0];
        [SerializeField] private Button previousPageButton;
        [SerializeField] private Button nextPageButton;
        [SerializeField] private TMP_Text pageText;
        [SerializeField] private Button unloadButton;
        [SerializeField] private TMP_Text viewerStatus;

        private readonly List<(string Folder, SessionHeader Header)> sessions = new List<(string, SessionHeader)>();
        private readonly ConfirmTap confirmDelete = new ConfirmTap();
        private int page;
        private string message = string.Empty;

        private void Awake()
        {
            recordButton.onClick.AddListener(ToggleRecording);
            previousPageButton.onClick.AddListener(() => page--);
            nextPageButton.onClick.AddListener(() => page++);
            unloadButton.onClick.AddListener(() => viewer.Unload());
            for (int i = 0; i < rows.Length; i++)
            {
                int index = i;
                rows[i].load.onClick.AddListener(() => Load(index));
                rows[i].delete.onClick.AddListener(() => Delete(index));
            }
        }

        private void OnEnable()
        {
            recorder.Changed += RefreshList;
            RefreshList();
        }

        private void OnDisable()
        {
            recorder.Changed -= RefreshList;
        }

        private void ToggleRecording()
        {
            if (recorder.IsRecording)
            {
                recorder.StopRecording();
                message = recorder.LastMessage;
            }
            else
            {
                message = recorder.StartRecording(out string reason) ? recorder.LastMessage : reason;
            }
        }

        private void RefreshList()
        {
            sessions.Clear();
            foreach (string folder in SessionStore.ListSessionFolders(SessionRecorder.SessionsRoot))
            {
                try
                {
                    sessions.Add((folder, SessionStore.ReadHeader(folder)));
                }
                catch (Exception)
                {
                    // An unreadable header (e.g. a newer format) is left on disk and not listed.
                }
            }
        }

        private (string Folder, SessionHeader Header)? At(int row)
        {
            int index = page * rows.Length + row;
            return index < sessions.Count ? sessions[index] : ((string, SessionHeader)?)null;
        }

        private void Load(int row)
        {
            var session = At(row);
            if (session == null)
            {
                return;
            }
            if (viewer.LoadedFolder == session.Value.Folder)
            {
                viewer.Unload();
                return;
            }
            viewer.Load(session.Value.Folder);
        }

        private void Delete(int row)
        {
            var session = At(row);
            if (session == null)
            {
                return;
            }
            string folder = session.Value.Folder;
            if (folder == recorder.CurrentFolder)
            {
                message = "Stop recording before deleting this session.";
                return;
            }
            if (!confirmDelete.Tap(folder))
            {
                return;
            }
            if (viewer.LoadedFolder == folder)
            {
                viewer.Unload();
            }
            try
            {
                SessionStore.Delete(folder);
                message = "Session deleted.";
            }
            catch (Exception exception)
            {
                message = $"Delete failed: {exception.Message}";
            }
            RefreshList();
        }

        private void Update()
        {
            MenuStyle.SetLabel(recordButton, recorder.IsRecording ? "Stop recording" : "Record");
            MenuStyle.Tint(recordButton, recorder.IsRecording ? MenuStyle.Warning : MenuStyle.Normal);
            recordStatus.text = recorder.IsRecording
                ? $"{recorder.CurrentHeader.AccessPoint.Ssid} ch {recorder.CurrentHeader.AccessPoint.Channel} · " +
                  $"{FormatDuration(recorder.Duration)} · {recorder.Written} samples · {recorder.AnchorsUsed} anchor(s)\n" +
                  $"flagged {recorder.FlaggedSamples} · dropped: no clock {recorder.DroppedNoClock}, " +
                  $"no pose {recorder.DroppedNoPose}, no anchor {recorder.DroppedNoAnchor}"
                : message;

            int pages = Mathf.Max(1, Mathf.CeilToInt(sessions.Count / (float)Mathf.Max(1, rows.Length)));
            page = Mathf.Clamp(page, 0, pages - 1);
            pageText.text = sessions.Count == 0 ? "No sessions" : $"{page + 1}/{pages}";
            previousPageButton.interactable = page > 0;
            nextPageButton.interactable = page < pages - 1;

            for (int i = 0; i < rows.Length; i++)
            {
                var session = At(i);
                rows[i].root.SetActive(session != null);
                if (session == null)
                {
                    continue;
                }
                SessionHeader h = session.Value.Header;
                bool shown = viewer.LoadedFolder == session.Value.Folder;
                bool live = session.Value.Folder == recorder.CurrentFolder;
                string name = string.IsNullOrEmpty(h.AccessPoint.Ssid) ? h.AccessPoint.Bssid : h.AccessPoint.Ssid;
                string when = h.StartedUtc.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.InvariantCulture);
                string rooms = string.Join(", ", h.Anchors.Select(a => a.Label));
                string state = live ? "recording" : h.Records.Recovered ? "recovered" : h.Records.Complete ? "" : "unfinished";
                rows[i].label.text = $"{when}  {name} (ch {h.AccessPoint.Channel})\n" +
                                     $"<size=80%>{(live ? recorder.Written : h.Records.Count)} samples · {rooms}" +
                                     $"{(state.Length > 0 ? " · " + state : "")}</size>";
                MenuStyle.SetLabel(rows[i].load, shown ? "Hide" : "Load");
                MenuStyle.Tint(rows[i].load, shown);
                rows[i].load.interactable = !live;
                bool confirming = confirmDelete.IsPending(session.Value.Folder);
                MenuStyle.SetLabel(rows[i].delete, confirming ? "Sure?" : "Delete");
                MenuStyle.Tint(rows[i].delete, confirming ? MenuStyle.Warning : MenuStyle.Normal);
                rows[i].delete.interactable = !live;
            }

            unloadButton.interactable = viewer.LoadedFolder != null;
            viewerStatus.text = viewer.LoadedFolder != null
                ? viewer.Summary
                : "Live cloud builds while measuring. Load a saved cloud; adjust its appearance in Cloud.";
        }

        private static string FormatDuration(float seconds)
        {
            TimeSpan span = TimeSpan.FromSeconds(seconds);
            return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
        }
    }
}
