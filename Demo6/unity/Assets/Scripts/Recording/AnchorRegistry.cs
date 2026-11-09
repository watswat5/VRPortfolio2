using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Meta.XR.MRUtilityKit;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using WifiHeatmap.Core;

namespace WifiHeatmap.Recording
{
    /// <summary>
    /// The anchors samples can be recorded against: the headset's MRUK rooms (from Space Setup)
    /// and manual spatial anchors the user places. Manual anchors are saved on the headset and
    /// listed in anchors.json so they are reloaded and relocalised on the next launch.
    /// In Auto mode each sample uses the room containing it (else the nearest room); in Manual mode
    /// every sample uses the chosen manual anchor.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AnchorRegistry : MonoBehaviour
    {
        public enum Mode
        {
            AutoRooms,
            Manual,
        }

        public sealed class AnchorInfo
        {
            public Guid Uuid;
            public string Kind;
            public string Label;
            public Transform Transform;
            public bool Localized;
            public bool IsRoom => Kind == SessionHeader.AnchorEntry.MrukRoom;
        }

        private sealed class SavedAnchors
        {
            public List<SavedAnchor> Anchors { get; set; } = new List<SavedAnchor>();
            public Mode Mode { get; set; } = Mode.AutoRooms;
            public Guid Chosen { get; set; }
        }

        private sealed class SavedAnchor
        {
            public Guid Uuid { get; set; }
            public string Label { get; set; } = string.Empty;
            public DateTime CreatedUtc { get; set; }
        }

        [SerializeField] private Material markerMaterial;
        [SerializeField] private bool showMarkers = true;

        private readonly Dictionary<Guid, OVRSpatialAnchor> live = new Dictionary<Guid, OVRSpatialAnchor>();
        private SavedAnchors saved = new SavedAnchors();
        private bool loadingManual;
        private bool lastRoomsLocalized;
        private int lastManualLocalized;

        public Mode CurrentMode => saved.Mode;
        public Guid ChosenManual => saved.Chosen;
        public bool LoadingManualAnchors => loadingManual;
        public string LastError { get; private set; } = string.Empty;

        /// <summary>Anchors were added, removed, localised, or the mode changed.</summary>
        public event Action Changed;

        private static string StorePath => Path.Combine(Application.persistentDataPath, "anchors.json");

        private void Start()
        {
            LoadSaved();
            _ = LoadManualAnchorsAsync();
            if (MRUK.Instance != null)
            {
                MRUK.Instance.RegisterSceneLoadedCallback(() => Changed?.Invoke());
                MRUK.Instance.RoomCreatedEvent.AddListener(_ => Changed?.Invoke());
                MRUK.Instance.RoomRemovedEvent.AddListener(_ => Changed?.Invoke());
            }
        }

        private void Update()
        {
            // World lock and anchor tracking change without any MRUK/OVR event; raise Changed on
            // transitions so listeners (e.g. SessionViewer retrying unresolved samples) can react.
            bool roomsLocalized = RoomsLocalized();
            int manualLocalized = 0;
            foreach (OVRSpatialAnchor spatial in live.Values)
            {
                if (spatial != null && spatial.Localized)
                {
                    manualLocalized++;
                }
            }
            if (roomsLocalized != lastRoomsLocalized || manualLocalized != lastManualLocalized)
            {
                lastRoomsLocalized = roomsLocalized;
                lastManualLocalized = manualLocalized;
                Changed?.Invoke();
            }
        }

        private static bool RoomsLocalized()
        {
            return MRUK.Instance != null && (!MRUK.Instance.EnableWorldLock || MRUK.Instance.IsWorldLockActive);
        }

        /// <summary>Rooms first (in MRUK order), then manual anchors (oldest first).</summary>
        public List<AnchorInfo> GetAll()
        {
            List<AnchorInfo> all = new List<AnchorInfo>();
            List<MRUKRoom> rooms = LocalRooms();
            bool roomsLocalized = RoomsLocalized();
            for (int i = 0; i < rooms.Count; i++)
            {
                all.Add(new AnchorInfo
                {
                    Uuid = rooms[i].Anchor.Uuid,
                    Kind = SessionHeader.AnchorEntry.MrukRoom,
                    Label = $"Room {i + 1}",
                    Transform = rooms[i].transform,
                    Localized = roomsLocalized,
                });
            }
            foreach (SavedAnchor anchor in saved.Anchors)
            {
                live.TryGetValue(anchor.Uuid, out OVRSpatialAnchor spatial);
                all.Add(new AnchorInfo
                {
                    Uuid = anchor.Uuid,
                    Kind = SessionHeader.AnchorEntry.SpatialAnchor,
                    Label = anchor.Label,
                    Transform = spatial != null ? spatial.transform : null,
                    Localized = spatial != null && spatial.Localized,
                });
            }
            return all;
        }

        public List<MRUKRoom> LocalRooms()
        {
            return MRUK.Instance == null ? new List<MRUKRoom>() : MRUK.Instance.Rooms.Where(r => r != null && r.IsLocal).ToList();
        }

        /// <summary>
        /// Finds a localised room or manual anchor by UUID (for loading old sessions). Returns false
        /// until the anchor is relocalised, so old samples are never shown against an unaligned pose;
        /// <see cref="Changed"/> fires when localisation changes so callers can retry.
        /// </summary>
        public bool TryResolve(Guid uuid, out AnchorInfo info)
        {
            info = GetAll().FirstOrDefault(a => a.Uuid == uuid && a.Transform != null && a.Localized);
            return info != null;
        }

        /// <summary>The anchor a sample at <paramref name="worldPosition"/> should be recorded against.</summary>
        public bool TryChoose(Vector3 worldPosition, out AnchorInfo info)
        {
            info = null;
            List<AnchorInfo> all = GetAll();
            if (saved.Mode == Mode.Manual)
            {
                info = all.FirstOrDefault(a => a.Uuid == saved.Chosen && a.Transform != null);
                return info != null;
            }

            List<MRUKRoom> rooms = LocalRooms();
            if (rooms.Count > 0)
            {
                MRUKRoom room = rooms.FirstOrDefault(r => r.IsPositionInRoom(worldPosition))
                                ?? rooms.OrderBy(r => (r.transform.position - worldPosition).sqrMagnitude).First();
                info = all.First(a => a.Uuid == room.Anchor.Uuid);
                return true;
            }

            // No room scan: fall back to the nearest localised manual anchor.
            info = all.Where(a => !a.IsRoom && a.Transform != null && a.Localized)
                .OrderBy(a => (a.Transform.position - worldPosition).sqrMagnitude)
                .FirstOrDefault();
            return info != null;
        }

        public void SetMode(Mode mode)
        {
            saved.Mode = mode;
            Persist();
            Changed?.Invoke();
        }

        /// <summary>Records every following sample against this manual anchor.</summary>
        public void ChooseManual(Guid uuid)
        {
            if (saved.Anchors.All(a => a.Uuid != uuid))
            {
                return;
            }
            saved.Chosen = uuid;
            saved.Mode = Mode.Manual;
            Persist();
            Changed?.Invoke();
        }

        /// <summary>Creates, localises and saves a spatial anchor at <paramref name="pose"/>.</summary>
        public async Task<AnchorInfo> PlaceManualAnchorAsync(Pose pose, string label)
        {
            GameObject go = new GameObject($"Anchor: {label}");
            go.transform.SetPositionAndRotation(pose.position, pose.rotation);
            OVRSpatialAnchor anchor = go.AddComponent<OVRSpatialAnchor>();
            try
            {
                if (!await anchor.WhenLocalizedAsync())
                {
                    throw new InvalidOperationException("the headset could not localise the new anchor");
                }
                OVRResult<OVRAnchor.SaveResult> result = await anchor.SaveAnchorAsync();
                if (!result.Success)
                {
                    throw new InvalidOperationException($"saving the anchor failed ({result.Status})");
                }
            }
            catch (Exception exception)
            {
                Destroy(go);
                LastError = $"Could not place anchor: {exception.Message}";
                Debug.LogWarning(LastError, this);
                Changed?.Invoke();
                return null;
            }

            AddMarker(go.transform, label);
            live[anchor.Uuid] = anchor;
            saved.Anchors.Add(new SavedAnchor { Uuid = anchor.Uuid, Label = label, CreatedUtc = DateTime.UtcNow });
            Persist();
            LastError = string.Empty;
            Changed?.Invoke();
            return GetAll().First(a => a.Uuid == anchor.Uuid);
        }

        /// <summary>Erases a manual anchor from the headset and forgets it. Sessions recorded against it can no longer be placed.</summary>
        public async Task<bool> DeleteManualAnchorAsync(Guid uuid)
        {
            try
            {
                OVRResult<OVRAnchor.EraseResult> result;
                if (live.TryGetValue(uuid, out OVRSpatialAnchor anchor) && anchor != null)
                {
                    result = await anchor.EraseAnchorAsync();
                    Destroy(anchor.gameObject);
                }
                else
                {
                    result = await OVRSpatialAnchor.EraseAnchorsAsync(null, new[] { uuid });
                }
                if (!result.Success)
                {
                    Debug.LogWarning($"Erasing anchor {uuid} reported {result.Status}; forgetting it anyway.", this);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Erasing anchor {uuid} failed: {exception.Message}; forgetting it anyway.", this);
            }

            live.Remove(uuid);
            saved.Anchors.RemoveAll(a => a.Uuid == uuid);
            if (saved.Chosen == uuid)
            {
                saved.Chosen = Guid.Empty;
                saved.Mode = Mode.AutoRooms;
            }
            Persist();
            Changed?.Invoke();
            return true;
        }

        private async Task LoadManualAnchorsAsync()
        {
            if (saved.Anchors.Count == 0)
            {
                return;
            }
            loadingManual = true;
            Changed?.Invoke();
            try
            {
                List<OVRSpatialAnchor.UnboundAnchor> unbound = new List<OVRSpatialAnchor.UnboundAnchor>();
                var result = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(saved.Anchors.Select(a => a.Uuid), unbound);
                if (!result.Success)
                {
                    LastError = $"Loading saved anchors failed ({result.Status}).";
                    Debug.LogWarning(LastError, this);
                    return;
                }
                foreach (OVRSpatialAnchor.UnboundAnchor anchor in unbound)
                {
                    if (!anchor.Localized && !await anchor.LocalizeAsync())
                    {
                        // Not in this space right now (or not yet recognised); it stays listed as unlocalised.
                        continue;
                    }
                    SavedAnchor entry = saved.Anchors.First(a => a.Uuid == anchor.Uuid);
                    GameObject go = new GameObject($"Anchor: {entry.Label}");
                    if (anchor.TryGetPose(out Pose pose))
                    {
                        go.transform.SetPositionAndRotation(pose.position, pose.rotation);
                    }
                    OVRSpatialAnchor spatial = go.AddComponent<OVRSpatialAnchor>();
                    anchor.BindTo(spatial);
                    AddMarker(go.transform, entry.Label);
                    live[anchor.Uuid] = spatial;
                    Changed?.Invoke();
                }
            }
            catch (Exception exception)
            {
                LastError = $"Loading saved anchors failed: {exception.Message}";
                Debug.LogWarning(LastError, this);
            }
            finally
            {
                loadingManual = false;
                Changed?.Invoke();
            }
        }

        private void AddMarker(Transform parent, string label)
        {
            if (!showMarkers)
            {
                return;
            }
            GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(pole.GetComponent<Collider>());
            pole.transform.SetParent(parent, false);
            pole.transform.localPosition = new Vector3(0f, 0.05f, 0f);
            pole.transform.localScale = new Vector3(0.006f, 0.05f, 0.006f);
            GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(head.GetComponent<Collider>());
            head.transform.SetParent(parent, false);
            head.transform.localPosition = new Vector3(0f, 0.105f, 0f);
            head.transform.localScale = Vector3.one * 0.025f;
            if (markerMaterial != null)
            {
                pole.GetComponent<Renderer>().sharedMaterial = markerMaterial;
                head.GetComponent<Renderer>().sharedMaterial = markerMaterial;
            }

            GameObject text = new GameObject("Label");
            text.transform.SetParent(parent, false);
            text.transform.localPosition = new Vector3(0f, 0.14f, 0f);
            TextMeshPro tmp = text.AddComponent<TextMeshPro>();
            tmp.text = label;
            tmp.fontSize = 0.25f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.rectTransform.sizeDelta = new Vector2(0.3f, 0.05f);
            text.AddComponent<FaceViewer>();
        }

        private void LoadSaved()
        {
            try
            {
                if (File.Exists(StorePath))
                {
                    saved = JsonConvert.DeserializeObject<SavedAnchors>(File.ReadAllText(StorePath)) ?? new SavedAnchors();
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"anchors.json is unreadable ({exception.Message}); starting with no manual anchors.", this);
                saved = new SavedAnchors();
            }
        }

        private void Persist()
        {
            try
            {
                string temporary = StorePath + ".tmp";
                File.WriteAllText(temporary, JsonConvert.SerializeObject(saved, Formatting.Indented));
                if (File.Exists(StorePath))
                {
                    File.Replace(temporary, StorePath, null);
                }
                else
                {
                    File.Move(temporary, StorePath);
                }
            }
            catch (Exception exception)
            {
                LastError = $"Could not save anchors.json: {exception.Message}";
                Debug.LogWarning(LastError, this);
            }
        }
    }

    /// <summary>Keeps a label turned toward the main camera (yaw only).</summary>
    public sealed class FaceViewer : MonoBehaviour
    {
        private void LateUpdate()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                return;
            }
            Vector3 away = Vector3.ProjectOnPlane(transform.position - camera.transform.position, Vector3.up);
            if (away.sqrMagnitude > 1e-6f)
            {
                transform.rotation = Quaternion.LookRotation(away, Vector3.up);
            }
        }
    }
}
