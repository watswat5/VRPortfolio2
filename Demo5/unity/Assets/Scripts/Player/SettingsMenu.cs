// The options menu: the left controller's menu button (Tab on the flat screen) opens it in front of you, again closes
// it. Two tabs:
//   SETTINGS: height (sizes the robot; Measure fits it to you standing straight), SEATED (playing sitting down: your
//     view is lifted to the height above, from where your head is in the seat; Measure then re-measures the seat), arm
//     length, the comfort vignette's strength, the assist (rhyme hints, word drops and a kinder crowd, from the next battle),
//     the floating words (on or off), the server's address and
//     its password, Connect, and the server's status.
//   PLAYER: the player sheet (the save file, SaveFile), all of it editable: the rap name, the notes the MCs get, each
//     interview answer, which MCs are beaten; the top scores; DELETE SAVE (are you sure?), after which the pre-show
//     interview runs again. Without a save, a note saying the interview makes one (or skip it with a blank sheet).
// Point and pull the trigger to use it; point into a text box to type there, with the keyboard underneath.
// Height, arm length, vignette, assist, floating words and the server address are remembered (PlayerPrefs); the password never is: it's typed
// once per launch and kept only in memory (through a Delete Save reload, which reconnects), as the server expects.
using Freestyle.Battle;
using Freestyle.UI;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Freestyle.Player
{
    /// <summary>Calls back when the pointer goes down on it (the settings menu uses it to put the keyboard away).</summary>
    public class OnPress : MonoBehaviour, UnityEngine.EventSystems.IPointerDownHandler
    {
        public System.Action pressed;
        public void OnPointerDown(UnityEngine.EventSystems.PointerEventData e) => pressed?.Invoke();
    }

    public class SettingsMenu : MonoBehaviour
    {
        public BattleCoordinator battle;
        public PlayerAvatar avatar;
        public XRMovement movement;
        [Tooltip("The headset camera (where the menu opens in front of). Defaults to the main camera.")]
        public Transform head;
        public float distance = 0.8f;
        [Tooltip("The server to use until one is typed in the menu (then that's remembered); DEFAULT in the menu comes back " +
                 "to this. Empty: the BattleCoordinator's Server Url.")]
        public string defaultServerUrl = "";
        [Tooltip("Metres to a pixel of the menu (900 px wide).")]
        public float metresPerPx = 0.0006f;

        const string Height = "Freestyle.Height", Arms = "Freestyle.ArmLength", Vignette = "Freestyle.Vignette", Server = "Freestyle.ServerUrl",
                     Assist = "Freestyle.Assist", Seated = "Freestyle.Seated", SeatedEye = "Freestyle.SeatedEye";
        // (Arms is only saved once set by hand: until then the robot learns your arms each time)
        const float EyeToTop = 0.11f;   // a person is about this much taller than their eyes

        readonly InputAction toggle = new("Settings", InputActionType.Button);
        Canvas canvas;
        TextMeshProUGUI heightText, armText, vignetteText, status;
        Slider heightSlider, armSlider, vignetteSlider;
        TextField url, password;
        Button assistButton, wordsButton, seatedButton, settingsTab, playerTab;
        bool seated;
        float seatedEye;          // the headset's height over the floor in the seat (measured when seated is turned on)
        Transform cameraOffset;   // the XR Origin's Camera Offset: lifted while seated
        RectTransform settingsPage, playerPage, sheet;   // the two tabs' pages, and the player sheet's scrolling list
        bool sheetDirty, savingSheet;
        VRKeyboard keyboard;
        RectTransform keyboardRow;

        void Awake()
        {
            if (!battle) battle = FindAnyObjectByType<BattleCoordinator>();
            if (!avatar) avatar = GetComponentInChildren<PlayerAvatar>();
            if (!movement) movement = GetComponent<XRMovement>();
            toggle.AddBinding("<XRController>{LeftHand}/menuButton");
            toggle.AddBinding("<XRController>{LeftHand}/menu");
            toggle.AddBinding("<Keyboard>/tab");
            // the server: the one typed last time, else the default, before the battle asks it for its songs (its Start)
            if (battle && string.IsNullOrWhiteSpace(defaultServerUrl)) defaultServerUrl = battle.serverUrl;
            if (battle) battle.serverUrl = PlayerPrefs.HasKey(Server) ? PlayerPrefs.GetString(Server) : Clean(defaultServerUrl);
        }

        void Start()
        {
            if (!head && Camera.main) head = Camera.main.transform;
            if (avatar && PlayerPrefs.HasKey(Height)) avatar.SetEyeHeight(PlayerPrefs.GetFloat(Height));
            if (avatar && PlayerPrefs.HasKey(Arms)) { avatar.armLength = PlayerPrefs.GetFloat(Arms); avatar.autoArmLength = false; }
            if (movement) movement.vignette = PlayerPrefs.GetFloat(Vignette, movement.vignette);
            if (battle && PlayerPrefs.HasKey(Assist)) battle.assist = PlayerPrefs.GetInt(Assist) == 1;
            var origin = GetComponentInParent<Unity.XR.CoreUtils.XROrigin>() ?? FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
            if (origin && origin.CameraFloorOffsetObject) cameraOffset = origin.CameraFloorOffsetObject.transform;
            seated = PlayerPrefs.GetInt(Seated, 0) == 1;
            seatedEye = PlayerPrefs.GetFloat(SeatedEye, 1.15f);
            Build();
            canvas.gameObject.SetActive(false);
        }

        void OnEnable() => toggle.Enable();
        void OnDisable() => toggle.Disable();

        void Build()
        {
            var made = UIKit.Canvas("Settings Menu", null, new Vector2(900, Tall), metresPerPx);
            canvas = made.canvas;
            var panel = made.panel;
            var tabs = UIKit.Row(panel, 76);
            tabs.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = true;
            settingsTab = UIKit.Size(UIKit.Button(tabs, "SETTINGS", () => ShowTab(false), 34), 76, -1, 1);
            playerTab = UIKit.Size(UIKit.Button(tabs, "PLAYER", () => ShowTab(true), 34), 76, -1, 1);

            settingsPage = Page(panel);
            var page = settingsPage;

            heightText = Caption(page);
            var hRow = UIKit.Row(page, 70);
            heightSlider = UIKit.Size(UIKit.Slider(hRow, 1.3f, 2.1f, 1.75f, v => SetHeight(v, true)), 70, -1, 1);
            UIKit.Size(UIKit.Button(hRow, "MEASURE", Measure, 28), 70, 220);
            seatedButton = UIKit.Size(UIKit.Button(page, "", ToggleSeated, 30), 76);

            armText = Caption(page);
            var aRow = UIKit.Row(page, 70);
            armSlider = UIKit.Size(UIKit.Slider(aRow, 0.8f, 1.4f, 1f, v => SetArms(v, true)), 70, -1, 1);
            UIKit.Size(UIKit.Button(aRow, "AUTO", AutoArms, 28), 70, 220);

            vignetteText = Caption(page);
            vignetteSlider = UIKit.Size(UIKit.Slider(page, 0f, 1f, 0.8f, v => SetVignette(v, true)), 70);

            assistButton = UIKit.Size(UIKit.Button(page, "", ToggleAssist, 30), 76);
            wordsButton = UIKit.Size(UIKit.Button(page, "", ToggleWords, 30), 76);

            Caption(page).text = "SERVER";
            var uRow = UIKit.Row(page, 70);
            url = TextField.Make(uRow, "http://192.168.1.20:8000/", false);
            UIKit.Size(url, 70, -1, 1);
            UIKit.Size(UIKit.Button(uRow, "DEFAULT", UseDefault, 26), 70, 190);
            Caption(page).text = "PASSWORD";
            password = TextField.Make(page, "(only if the server has one)", true);

            var buttons = UIKit.Row(page, 80);
            UIKit.Size(UIKit.Button(buttons, "CONNECT", Connect, 32, new Color(0.1f, 0.45f, 0.42f, 1f)), 80, -1, 1);
            UIKit.Size(UIKit.Button(buttons, "CLOSE", () => Show(false), 32), 80, -1, 1);
            status = UIKit.Size(UIKit.Label(page, "", 26, UIKit.Dim, TextAlignmentOptions.Left), 44);


            playerPage = Page(panel);
            (_, sheet) = UIKit.Scroll(playerPage, Tall - 270);
            UIKit.Size(UIKit.Button(playerPage, "CLOSE", () => Show(false), 32), 80);

            keyboardRow = (RectTransform)new GameObject("Keyboard Row", typeof(RectTransform)).transform;
            keyboardRow.SetParent(panel, false);
            UIKit.Size(keyboardRow, 5 * 66 + 4 * 8);
            var v = keyboardRow.gameObject.AddComponent<VerticalLayoutGroup>();
            v.childControlWidth = v.childControlHeight = true; v.childForceExpandWidth = true;
            keyboard = VRKeyboard.Make(keyboardRow);
            keyboard.Done += () => Keys(false);

            Hook(panel);   // (url and password bring the keyboard up from here)
            url.Submitted += _ => Connect();
            password.Submitted += _ => Connect();
            Keys(false);
            Refresh();
            ShowTab(false);
            Freestyle.Game.SaveFile.Changed += SaveChanged;
            UIKit.OnUILayer(canvas.gameObject);
        }

        void OnDestroy() => Freestyle.Game.SaveFile.Changed -= SaveChanged;

        /// <summary>The keyboard is only up while a text box has the focus: anything else in the menu puts it away.
        /// Text boxes bring it up. (For what's under root, once each.)</summary>
        void Hook(RectTransform root)
        {
            foreach (var sel in root.GetComponentsInChildren<Selectable>(true))
            {
                if (sel.GetComponentInParent<VRKeyboard>(true) || sel.GetComponent<OnPress>()) continue;
                if (sel.TryGetComponent<TextField>(out var f)) { sel.gameObject.AddComponent<OnPress>(); f.Focused += Type; continue; }
                sel.gameObject.AddComponent<OnPress>().pressed = () => Keys(false);   // (a press, even without a change)
                if (sel is Button b) b.onClick.AddListener(() => Keys(false));
            }
        }

        RectTransform Page(RectTransform panel)
        {
            var rt = (RectTransform)new GameObject("Page", typeof(RectTransform)).transform;
            rt.SetParent(panel, false);
            var v = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = 16;
            v.childControlWidth = v.childControlHeight = true;
            v.childForceExpandWidth = true; v.childForceExpandHeight = false;
            return rt;
        }

        void ShowTab(bool player)
        {
            SaveSheet();
            Keys(false);
            settingsPage.gameObject.SetActive(!player);
            playerPage.gameObject.SetActive(player);
            var on = new Color(0.1f, 0.45f, 0.42f, 1f);
            settingsTab.GetComponent<Image>().color = player ? UIKit.ButtonColour : on;
            playerTab.GetComponent<Image>().color = player ? on : UIKit.ButtonColour;
            if (player) BuildSheet();
        }

        // ---------------------------------------------------------------- the player sheet

        void SaveSheet()
        {
            if (!sheetDirty) return;
            sheetDirty = false;
            savingSheet = true;
            Freestyle.Game.SaveFile.Save();
            savingSheet = false;
        }

        void SaveChanged()
        {
            // the save changed some other way (the interview just finished, a battle was recorded): show it
            if (!savingSheet && playerPage && playerPage.gameObject.activeInHierarchy && !VRKeyboard.Typing) BuildSheet();
        }

        TextMeshProUGUI Words(RectTransform at, string text, float size, Color colour, float height, bool bold = false)
        {
            var l = UIKit.Size(UIKit.Label(at, text, size, colour, TextAlignmentOptions.TopLeft, bold), height);
            l.textWrappingMode = TextWrappingModes.Normal;
            l.overflowMode = TextOverflowModes.Overflow;
            return l;
        }

        TextField Field(RectTransform at, string value, string placeholder, float height, bool multiline, System.Action<string> changed)
        {
            var f = TextField.Make(at, placeholder, false, height, multiline);
            f.Set(value);
            f.Changed += v => { changed(v); sheetDirty = true; };
            f.Submitted += _ => { Keys(false); };
            return f;
        }

        /// <summary>The player sheet, (re)made from the save.</summary>
        void BuildSheet()
        {
            if (!sheet) return;
            foreach (Transform c in sheet) Destroy(c.gameObject);
            var save = Freestyle.Game.SaveFile.Data;
            if (save == null)
            {
                Words(sheet, "PLAYER SHEET", 40, UIKit.Accent, 54, true);
                Words(sheet, "No player sheet yet. It's made in your pre-show interview, the first time you head for the stage.", 28, UIKit.TextColour, 84);
                UIKit.Size(UIKit.Button(sheet, "SKIP THE INTERVIEW (START A BLANK SHEET)", () =>
                {
                    Freestyle.Game.SaveFile.Create(new Freestyle.Game.InterviewRecord { source = "by hand" });
                    BuildSheet();
                }, 26), 70);
            }
            else
            {
                var iv = save.interview ??= new Freestyle.Game.InterviewRecord();
                Words(sheet, $"PLAYER SHEET  <size=22><color=#9a9aa6>{(string.IsNullOrEmpty(iv.date) ? "" : "interviewed " + iv.date)}</color></size>", 40, UIKit.Accent, 54, true);

                Words(sheet, "RAP NAME", 26, UIKit.TextColour, 34, true);
                Field(sheet, iv.name, "(what they call you on the mic)", 70, false, v => iv.name = v);

                Words(sheet, "NOTES FOR THE MCs  <size=22><color=#9a9aa6>what they know about you, and will use</color></size>", 26, UIKit.TextColour, 34, true);
                Field(sheet, iv.summary, "(nothing yet)", 190, true, v => iv.summary = v);

                if (iv.answers.Count > 0) Words(sheet, "THE INTERVIEW", 26, UIKit.TextColour, 34, true);
                foreach (var a in iv.answers)
                {
                    var answer = a;
                    Words(sheet, answer.question, 24, UIKit.Dim, 34);
                    Field(sheet, answer.answer, "(no answer)", 96, true, v => answer.answer = v);
                }

                Words(sheet, "BEATEN  <size=22><color=#9a9aa6>tap to change</color></size>", 26, UIKit.TextColour, 34, true);
                var stage = FindAnyObjectByType<OpponentStage>();
                var mcs = new System.Collections.Generic.List<(string id, string name)>();
                if (stage) foreach (var m in stage.lineup) if (m) mcs.Add((m.characterId, string.IsNullOrEmpty(m.displayName) ? m.characterId : m.displayName));
                foreach (var id in save.beaten) if (!mcs.Exists(x => x.id == id)) mcs.Add((id, id));
                foreach (var (id, name) in mcs)
                {
                    Button b = null;
                    void Show() => UIKit.LabelOf(b).text = save.beaten.Contains(id) ? $"<color=#33f2e6>BEATEN</color>  {name}" : $"<color=#9a9aa6>not yet</color>  {name}";
                    b = UIKit.Size(UIKit.Button(sheet, "", () =>
                    {
                        if (!save.beaten.Remove(id)) save.beaten.Add(id);
                        sheetDirty = true;
                        SaveSheet();
                        if (stage) stage.Resume();   // the run picks up at the first MC not beaten
                        Show();
                    }, 28), 60);
                    Show();
                }

                Words(sheet, "TOP SCORES", 26, UIKit.TextColour, 34, true);
                if (save.topScores.Count == 0) Words(sheet, "None yet.", 26, UIKit.Dim, 34);
                for (int i = 0; i < save.topScores.Count; i++)
                {
                    var t = save.topScores[i];
                    Words(sheet, $"{i + 1}.  {t.score:0} to {t.opponentScore:0}  vs {t.mcName}  " +
                                 $"<color=#9a9aa6>{(t.winner == "user" ? "won" : t.winner == "opponent" ? "lost" : "draw")}, {t.date}</color>", 26, UIKit.TextColour, 36);
                }

                // delete, with an are-you-sure
                var del = UIKit.Size(UIKit.Button(sheet, "DELETE SAVE...", null, 28, new Color(0.45f, 0.14f, 0.12f, 1f)), 70);
                var sure = (RectTransform)new GameObject("Are You Sure", typeof(RectTransform)).transform;
                sure.SetParent(sheet, false);
                var sv = sure.gameObject.AddComponent<VerticalLayoutGroup>();
                sv.spacing = 10; sv.childControlWidth = sv.childControlHeight = true; sv.childForceExpandWidth = true; sv.childForceExpandHeight = false;
                Words(sure, "<color=#ff8a3d>Are you sure?</color> Your player sheet, beaten MCs and scores are deleted, and the game starts over from the dressing room (the interview runs again the next time you head for the stage).", 26, UIKit.TextColour, 76);
                var yesNo = UIKit.Row(sure, 70);
                yesNo.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = true;
                UIKit.Size(UIKit.Button(yesNo, "YES, DELETE IT", () =>
                {
                    Freestyle.Game.SaveFile.Delete();
                    sheetDirty = false;
                    Freestyle.Game.GameReset.Restart();   // a fresh start: nothing carries over from the old run
                }, 28, new Color(0.55f, 0.16f, 0.13f, 1f)), 70, -1, 1);
                UIKit.Size(UIKit.Button(yesNo, "CANCEL", () => sure.gameObject.SetActive(false), 28), 70, -1, 1);
                sure.gameObject.SetActive(false);
                del.onClick.AddListener(() => sure.gameObject.SetActive(true));
            }
            Hook(sheet);
            UIKit.OnUILayer(canvas.gameObject);
        }

        const float Tall = 1378f, KeyboardTall = 360f;   // the menu, and what the keyboard adds when it's up

        /// <summary>The keyboard shown or hidden (the menu grows to fit it).</summary>
        void Keys(bool on)
        {
            keyboardRow.gameObject.SetActive(on);
            if (!on) SaveSheet();
            ((RectTransform)canvas.transform).sizeDelta = new Vector2(900, Tall + (on ? KeyboardTall : 0f));
            if (!on) keyboard.Attach(null);
        }

        TextMeshProUGUI Caption(RectTransform panel) => UIKit.Size(UIKit.Label(panel, "", 30, UIKit.TextColour), 40);

        void Type(TextField f)
        {
            Keys(true);
            keyboard.Attach(f);
        }

        void Refresh()
        {
            float eye = avatar ? avatar.EyeHeight : 1.64f;
            heightSlider.SetValueWithoutNotify(eye + EyeToTop);
            SetHeight(eye + EyeToTop, false);
            armSlider.SetValueWithoutNotify(avatar ? avatar.armLength : 1f);
            SetArms(armSlider.value, false);
            vignetteSlider.SetValueWithoutNotify(movement ? movement.vignette : 0.8f);
            SetVignette(vignetteSlider.value, false);
            if (battle) url.Set(battle.serverUrl);
            if (battle) password.Set(battle.Password);
            ShowAssist();
            ShowWords();
            ShowSeated();
        }

        void SetHeight(float tall, bool apply)
        {
            heightText.text = $"HEIGHT  <color=#33f2e6>{tall:0.00} m</color>  ({Mathf.FloorToInt(tall / 0.3048f)}' {Mathf.RoundToInt(tall / 0.0254f % 12)}\")";
            if (!apply || !avatar) return;
            avatar.SetEyeHeight(tall - EyeToTop);
            PlayerPrefs.SetFloat(Height, tall - EyeToTop);
            ShowSeated();
        }

        void Measure()
        {
            if (seated) { MeasureSeat(); return; }   // sitting: where your head is in the seat
            if (!avatar) return;
            PlayerPrefs.DeleteKey(Height);
            avatar.FitHeight();   // stand up straight: the robot's eyes go to yours
            Refresh();
        }

        void SetArms(float k, bool apply)
        {
            if (apply && avatar)   // set by hand: that's the length now, no more learning
            {
                avatar.armLength = k;
                avatar.autoArmLength = false;
                PlayerPrefs.SetFloat(Arms, k);
            }
            bool auto = avatar && avatar.autoArmLength;
            armText.text = $"ARM LENGTH  <color=#33f2e6>{k * 100:0}%</color>" +
                           (auto ? "  <color=#9a9aa6>AUTO: reach out to fit</color>" : "  <color=#9a9aa6>SET BY HAND</color>");
        }

        /// <summary>Back to learning your arms (from the robot's own length): reach out and they fit.</summary>
        void AutoArms()
        {
            if (!avatar) return;
            PlayerPrefs.DeleteKey(Arms);
            avatar.autoArmLength = true;
            avatar.armLength = 1f;
            armSlider.SetValueWithoutNotify(1f);
            SetArms(1f, false);
        }

        void SetVignette(float k, bool apply)
        {
            vignetteText.text = $"COMFORT VIGNETTE (WHILE MOVING)  <color=#33f2e6>{(k <= 0.001f ? "OFF" : $"{k * 100:0}%")}</color>";
            if (!apply || !movement) return;
            movement.vignette = k;
            PlayerPrefs.SetFloat(Vignette, k);
        }

        // ---------------------------------------------------------------- seated

        void ToggleSeated()
        {
            seated = !seated;
            PlayerPrefs.SetInt(Seated, seated ? 1 : 0);
            if (seated) MeasureSeat();
            ShowSeated();
        }

        /// <summary>The headset's height over the floor now is where your eyes are in the seat.</summary>
        void MeasureSeat()
        {
            var cam = head ? head : Camera.main ? Camera.main.transform : null;
            if (!cam || !cameraOffset) return;
            float y = cameraOffset.InverseTransformPoint(cam.position).y;   // the tracked height, without any lift
            if (y < 0.5f || y > 1.6f) return;   // not tracked, or standing up: keep the last measure
            seatedEye = y;
            PlayerPrefs.SetFloat(SeatedEye, y);
            ShowSeated();
        }

        /// <summary>The lift while seated: up to the eye height of the height chosen above.</summary>
        float Lift => seated && avatar ? Mathf.Clamp(avatar.EyeHeight - seatedEye, 0f, 1.2f) : 0f;

        void ShowSeated()
        {
            if (!seatedButton) return;
            UIKit.LabelOf(seatedButton).text = $"SEATED  {(seated ? "<color=#33f2e6>ON</color>" : "<color=#9a9aa6>OFF</color>")}" +
                (seated ? $"  <size=22><color=#9a9aa6>view lifted {Lift * 100:0} cm to your height; MEASURE re-measures the seat</color></size>"
                        : "  <size=22><color=#9a9aa6>playing sitting down</color></size>");
            seatedButton.GetComponent<Image>().color = seated ? new Color(0.1f, 0.32f, 0.3f, 1f) : UIKit.ButtonColour;
        }

        void LateUpdate()
        {
            // seated: the whole rig (head, hands) lifted so your eyes are at your chosen height; standing: no lift
            if (!cameraOffset || !UnityEngine.XR.XRSettings.isDeviceActive) return;
            var p = cameraOffset.localPosition;
            float y = Lift;
            if (Mathf.Abs(p.y - y) > 0.0005f) cameraOffset.localPosition = new Vector3(p.x, y, p.z);
        }

        void ToggleAssist()
        {
            if (!battle) return;
            battle.assist = !battle.assist;
            PlayerPrefs.SetInt(Assist, battle.assist ? 1 : 0);
            ShowAssist();
        }

        void ToggleWords()
        {
            FloatingWords.On = !FloatingWords.On;
            ShowWords();
        }

        void ShowWords()
        {
            bool on = FloatingWords.On;
            UIKit.LabelOf(wordsButton).text = $"FLOATING WORDS  {(on ? "<color=#33f2e6>ON</color>" : "<color=#9a9aa6>OFF</color>")}" +
                                              "  <size=22><color=#9a9aa6>words to flip, over the crowd</color></size>";
            wordsButton.GetComponent<Image>().color = on ? new Color(0.1f, 0.32f, 0.3f, 1f) : UIKit.ButtonColour;
        }

        void ShowAssist()
        {
            bool on = battle && battle.assist;
            UIKit.LabelOf(assistButton).text = $"ASSIST  {(on ? "<color=#33f2e6>ON</color>" : "<color=#9a9aa6>OFF</color>")}" +
                                               "  <size=22><color=#9a9aa6>rhyme hints, word drops, a kinder crowd</color></size>";
            assistButton.GetComponent<Image>().color = on ? new Color(0.1f, 0.32f, 0.3f, 1f) : UIKit.ButtonColour;
        }

        /// <summary>An address as the battle wants it: http:// in front, a / at the end.</summary>
        static string Clean(string u)
        {
            u = (u ?? "").Trim();
            if (u.Length > 0 && !u.StartsWith("http")) u = "http://" + u;
            if (u.Length > 0 && !u.EndsWith("/")) u += "/";
            return u;
        }

        void UseDefault()
        {
            PlayerPrefs.DeleteKey(Server);
            url.Set(Clean(defaultServerUrl));
        }

        void Connect()
        {
            if (!battle) return;
            string u = Clean(url.Value);
            url.Set(u);
            battle.serverUrl = u;
            battle.Password = password.Value;   // in memory only
            PlayerPrefs.SetString(Server, u);
            PlayerPrefs.Save();
            Keys(false);
            _ = battle.LoadLobby();
        }

        public void Show(bool on)
        {
            if (!canvas) return;
            if (on)
            {
                Refresh();
                // in front of you, a little below your eyes, turned to face you (upright)
                var cam = head ? head : Camera.main ? Camera.main.transform : transform;
                Vector3 ahead = Vector3.ProjectOnPlane(cam.forward, Vector3.up);
                if (ahead.sqrMagnitude < 1e-4f) ahead = Vector3.ProjectOnPlane(cam.up, Vector3.up);
                ahead.Normalize();
                canvas.transform.position = cam.position + ahead * distance - Vector3.up * 0.15f;
                canvas.transform.rotation = Quaternion.LookRotation(ahead) * Quaternion.Euler(12f, 0f, 0f);   // tipped back a little
                canvas.worldCamera = cam.GetComponent<Camera>();
            }
            else
            {
                Keys(false);
                SaveSheet();
                PlayerPrefs.Save();
            }
            canvas.gameObject.SetActive(on);
        }

        void Update()
        {
            if (toggle.WasPressedThisFrame() && !(VRKeyboard.Typing && Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame))
                Show(!canvas.gameObject.activeSelf);
            if (canvas && canvas.gameObject.activeSelf && battle) status.text = battle.Status;
            if (canvas && canvas.gameObject.activeSelf && avatar && avatar.autoArmLength && Mathf.Abs(armSlider.value - avatar.armLength) > 0.005f)
            {
                armSlider.SetValueWithoutNotify(avatar.armLength);   // learning while the menu's open: show it
                SetArms(avatar.armLength, false);
            }
        }
    }
}
