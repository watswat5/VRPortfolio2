using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WifiHeatmap.Probe;
using WifiHeatmap.Recording;

namespace WifiHeatmap.UI
{
    /// <summary>
    /// Config menu tab: choose how samples are anchored (automatically by room, or to one manual
    /// anchor), place new manual anchors at the probe, and delete manual anchors.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AnchorsPanel : MonoBehaviour
    {
        [Serializable]
        public sealed class Row
        {
            public GameObject root;
            public TMP_Text label;
            public Button use;
            public Button delete;
        }

        [SerializeField] private AnchorRegistry anchors;
        [SerializeField] private ProbeMount mount;
        [SerializeField] private SessionRecorder recorder;
        [SerializeField] private Button autoButton;
        [SerializeField] private Button placeButton;
        [SerializeField] private TMP_Text status;
        [SerializeField] private Row[] rows = new Row[0];
        [SerializeField] private Button previousPageButton;
        [SerializeField] private Button nextPageButton;
        [SerializeField] private TMP_Text pageText;

        private readonly ConfirmTap confirmDelete = new ConfirmTap();
        private List<AnchorRegistry.AnchorInfo> list = new List<AnchorRegistry.AnchorInfo>();
        private int page;
        private bool placing;
        private string message = string.Empty;

        private void Awake()
        {
            autoButton.onClick.AddListener(() => anchors.SetMode(AnchorRegistry.Mode.AutoRooms));
            placeButton.onClick.AddListener(Place);
            previousPageButton.onClick.AddListener(() => page--);
            nextPageButton.onClick.AddListener(() => page++);
            for (int i = 0; i < rows.Length; i++)
            {
                int index = i;
                rows[i].use.onClick.AddListener(() => Use(index));
                rows[i].delete.onClick.AddListener(() => Delete(index));
            }
        }

        private void OnEnable()
        {
            anchors.Changed += Refresh;
            Refresh();
        }

        private void OnDisable()
        {
            anchors.Changed -= Refresh;
        }

        private void Refresh()
        {
            list = anchors.GetAll();
        }

        private AnchorRegistry.AnchorInfo At(int row)
        {
            int index = page * rows.Length + row;
            return index < list.Count ? list[index] : null;
        }

        private async void Place()
        {
            if (placing)
            {
                return;
            }
            placing = true;
            message = "Placing anchor at the probe...";
            try
            {
                // Upright: keep the probe's heading but not its tilt, so the anchor's axes stay level.
                Transform antenna = mount.Antenna;
                Vector3 heading = Vector3.ProjectOnPlane(antenna.forward, Vector3.up);
                Quaternion rotation = heading.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(heading, Vector3.up) : Quaternion.identity;
                int manualCount = list.FindAll(a => !a.IsRoom).Count;
                AnchorRegistry.AnchorInfo placed = await anchors.PlaceManualAnchorAsync(
                    new Pose(antenna.position, rotation), $"Anchor {manualCount + 1}");
                message = placed != null ? $"Placed {placed.Label}. Tap Use to record against it." : anchors.LastError;
            }
            finally
            {
                placing = false;
                Refresh();
            }
        }

        private void Use(int row)
        {
            AnchorRegistry.AnchorInfo anchor = At(row);
            if (anchor == null || anchor.IsRoom)
            {
                return;
            }
            if (recorder.IsRecording)
            {
                message = "Stop recording before changing anchors.";
                return;
            }
            anchors.ChooseManual(anchor.Uuid);
        }

        private async void Delete(int row)
        {
            AnchorRegistry.AnchorInfo anchor = At(row);
            if (anchor == null || anchor.IsRoom)
            {
                return;
            }
            if (recorder.IsRecording)
            {
                message = "Stop recording before deleting anchors.";
                return;
            }
            if (!confirmDelete.Tap(anchor.Uuid))
            {
                message = $"Tap Delete again to erase {anchor.Label}. Sessions recorded against it can't be shown afterwards.";
                return;
            }
            await anchors.DeleteManualAnchorAsync(anchor.Uuid);
            message = $"{anchor.Label} deleted.";
        }

        private void Update()
        {
            bool auto = anchors.CurrentMode == AnchorRegistry.Mode.AutoRooms;
            MenuStyle.Tint(autoButton, auto);
            autoButton.interactable = !recorder.IsRecording;
            placeButton.interactable = !placing;
            MenuStyle.SetLabel(placeButton, placing ? "Placing..." : "Place anchor at probe");

            int rooms = list.FindAll(a => a.IsRoom).Count;
            string mode = auto
                ? rooms > 0 ? $"Auto: each sample uses the room it's in ({rooms} room(s) scanned)."
                            : "Auto: no room scan here, so the nearest manual anchor is used."
                : $"Manual: every sample uses {list.Find(a => a.Uuid == anchors.ChosenManual)?.Label ?? "the chosen anchor"}.";
            status.text = $"{mode}{(anchors.LoadingManualAnchors ? " Loading saved anchors..." : "")}\n{message}";

            int pages = Mathf.Max(1, Mathf.CeilToInt(list.Count / (float)Mathf.Max(1, rows.Length)));
            page = Mathf.Clamp(page, 0, pages - 1);
            pageText.text = list.Count == 0 ? "No rooms or anchors" : $"{page + 1}/{pages}";
            previousPageButton.interactable = page > 0;
            nextPageButton.interactable = page < pages - 1;

            for (int i = 0; i < rows.Length; i++)
            {
                AnchorRegistry.AnchorInfo anchor = At(i);
                rows[i].root.SetActive(anchor != null);
                if (anchor == null)
                {
                    continue;
                }
                string where = anchor.Transform == null ? "not found here" : anchor.Localized ? "localised" : "not localised";
                rows[i].label.text = $"{anchor.Label}\n<size=80%>{(anchor.IsRoom ? "room scan" : "manual anchor")} · {where}</size>";
                bool chosen = !auto && anchor.Uuid == anchors.ChosenManual;
                rows[i].use.gameObject.SetActive(!anchor.IsRoom);
                rows[i].delete.gameObject.SetActive(!anchor.IsRoom);
                rows[i].use.interactable = anchor.Transform != null && !recorder.IsRecording;
                MenuStyle.SetLabel(rows[i].use, chosen ? "In use" : "Use");
                MenuStyle.Tint(rows[i].use, chosen);
                bool confirming = confirmDelete.IsPending(anchor.Uuid);
                MenuStyle.SetLabel(rows[i].delete, confirming ? "Sure?" : "Delete");
                MenuStyle.Tint(rows[i].delete, confirming ? MenuStyle.Warning : MenuStyle.Normal);
                rows[i].delete.interactable = !recorder.IsRecording;
            }
        }
    }
}
