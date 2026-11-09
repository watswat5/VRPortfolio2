// Freestyle Simulator web client: mic + metronome on one AudioContext clock, battle over /ws/battle.
// The game runs here: this page decides the turns and applies the rules (rules.js) to the server's ratings;
// the server transcribes, rates and writes the opponent's verse.
"use strict";

// Browser speech (Web Speech API) is optional: the Quest Browser has none. Without it, the opponent uses the
// server voices (Qwen, or espeak), so stand in a silent object rather than letting every call throw.
const speech = window.speechSynthesis || { getVoices: () => [], speak() {}, cancel() {}, speaking: false, pending: false };
const SR = 16000;
const BEATS_PER_BAR = 4;
const $ = (id) => document.getElementById(id);

const DIMS = ["intimidated", "entertained", "confident", "annoyed", "impressed"];
const FACES = { cocky: "😏", calm: "😐", amused: "😂", respectful: "🫡", heated: "😠", rattled: "😰" };
const GESTURES = { laughs: "laughs at you", shrugs: "shrugs it off", nods: "gives a nod", winces: "winces",
                   glares: "steps up and glares", steps_back: "steps back, rattled" };

let ctx = null, ws = null, mic = null, metro = null;
let micT0 = null;          // AudioContext time of mic timeline 0 (first sample sent)
let beat0 = 0;             // AudioContext time a beat is heard (bar line)
let bpm = 90, bars = 8;   // bars: the "Bars/turn" setting, for the metronome (a song's turns are its phrases)
let turn = { who: null };  // {who, startCtx, lengthS}
let verse = null;          // opponent playback {lines, startCtx, barS, timers, sources}
let filler = null;         // ad-libs while the LLM writes {timers, sources, busyUntil}
let committed = [], partial = "";
let game = null;           // Rules.Game: crowd, opponent mood, score
let rounds = 2;
let battle = 0;            // bumped on stop, so a running battle loop knows to quit
let waiting = {};          // message type -> resolve, for the battle loop
let lastSilence = 0;       // seconds of trailing silence in the latest metrics (dead air drains the room)
let opp = null;            // the opponent's turn: {ratings, applied, finished, done}
let ticker = null, lastTick = 0, tickN = 0;
let songTiming = null;     // the song's place on the clock and its phrases, for timing turns (null: metronome)
let prepS = 4;             // seconds from asking for the opponent's verse to it being ready (learned as it goes)
// Dev/demo: ?clip=<wav url> plays that clip into the mic input at each of your turns instead of using the mic.
const clipUrl = new URLSearchParams(location.search).get("clip");
let clip = null;

const status = (t) => ($("status").textContent = t);
const send = (m) => ws && ws.readyState === 1 && ws.send(JSON.stringify(m));
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);

// ---------- setup UI ----------
for (let i = 0; i < 12; i++) $("crowdMeter").appendChild(Object.assign(document.createElement("div"), { className: "seg" }));
$("oppDims").innerHTML = DIMS.map((d) =>
  `<div class="dim-row"><span>${d}</span><div class="bar"><div id="d-${d}"></div></div><span id="v-${d}">-</span></div>`).join("");
const FLOW_STATS = [["syllable_rate", "syllables / s", 1], ["syllables_per_beat", "syllables / beat", 1], ["pocket", "pocket", 2],
                    ["on_beat", "on beat", 2], ["rhyme_density", "rhyme density", 2], ["multi_rhymes", "multi rhymes", 0]];
$("flow").innerHTML = FLOW_STATS.map(([k, label]) => `<div class="stat"><div class="v" id="f-${k}">-</div><div class="k">${label}</div></div>`).join("");

const QWEN_VOICE = "qwen";            // the server's MC voice (Qwen, IndexTTS or Breeze), with the delivery directions
const SERVER_VOICE = "server";        // espeak-ng, whole line fitted to the bar (the original voice)
const GRID_VOICE = "server-grid";     // espeak-ng, each word placed on the 16th grid
// At most three choices: the server's MC voice (whatever engine it runs), one browser voice, espeak.
const BROWSER_VOICE = "browser";
const ENGINE_NAMES = { qwen: "Qwen", indextts: "IndexTTS", breeze: "Breeze", openrouter: "cloud" };
let voiceEngine = "";            // from /health: which TTS the server runs
function browserVoice() {        // the best English voice the browser has
  const en = speech.getVoices().filter((v) => v.lang.startsWith("en"));
  return en.find((v) => v.lang === "en-US" && v.localService) || en.find((v) => v.lang === "en-US") || en[0] || null;
}
function loadVoices() {
  const sel = $("voice"), keep = sel.value;
  const engine = ENGINE_NAMES[voiceEngine] || "server";
  sel.innerHTML = (qwenOnServer ? `<option value="${QWEN_VOICE}">MC voice (${esc(engine)})</option>` : "") +
                  (browserVoice() ? `<option value="${BROWSER_VOICE}">Browser voice</option>` : "") +
                  `<option value="${SERVER_VOICE}">Robot voice (espeak)</option>`;
  // Keep the viewer's own pick; otherwise default to Qwen when the server has it.
  if (voicePicked && keep) sel.value = keep;
  else if (qwenOnServer) sel.value = QWEN_VOICE;
}
let voicePicked = false;
$("voice").addEventListener("change", () => (voicePicked = true));
// Backing beats from backend/songs: tempo and bar grid analysed by the server.
let songs = [];
const songBuffers = new Map();   // id -> decoded AudioBuffer (decoded per AudioContext, so cleared on stop)
fetch("songs").then((r) => r.json()).then((list) => {
  songs = list;
  $("song").innerHTML = list.map((s) => `<option value="${esc(s.id)}">${esc(s.title)} · ${(+s.bpm).toFixed(1)} BPM</option>`).join("") +
                        `<option value="">Metronome</option>`;
  pickSong();
}).catch(() => {});
const currentSong = () => songs.find((s) => s.id === $("song").value) || null;
function pickSong() {
  const s = currentSong();
  $("bpm").disabled = !!s;
  $("bars").disabled = !!(s && s.phrases && s.phrases.length);   // a song's turns are its phrases
  if (s) $("bpm").value = (+s.bpm).toFixed(2);
}
$("song").addEventListener("change", pickSong);

let oppName = "MC Static";
let roster = [];
fetch("mcs").then((r) => r.json()).then((list) => {
  roster = list;
  $("mc").innerHTML = list.map((m) => `<option value="${esc(m.id)}">${esc(m.name)} (${esc(m.difficulty)}${m.explicit ? ", explicit" : ""})</option>`).join("");
  $("mc").value = "mc_static";
  showMc();
}).catch(() => {});
function showMc() {
  const m = roster.find((x) => x.id === $("mc").value);
  if (!m) return;
  oppName = m.name;
  $("oppName").textContent = m.name;
  $("oppBlurb").textContent = `${m.difficulty} · ${m.blurb}`;
  renderSheet(m);
}

// The MC's character sheet. Sore spots stay "?" until the player hits one.
const STAT_NAMES = { wordplay: "wordplay", flow: "flow", composure: "composure", presence: "stage presence", rep: "rep" };
function renderSheet(m) {
  if (!m || !m.stats) return;
  const chips = (xs) => `<div class="chips">${xs.map((x) => `<span class="chip">${esc(x)}</span>`).join("")}</div>`;
  const found = m.sore_spots_found || [];
  const spots = found.map((s) => `<span class="found">${esc(s)}</span>`).join("") +
                "<span>?</span>".repeat(Math.max(0, (m.sore_spots_total || 0) - found.length));
  $("sheet").innerHTML =
    `<div class="bg">${esc(m.background || "")}</div>` +
    (m.traits.length ? chips(m.traits) : "") +
    (m.topics.length ? `<div style="color:var(--dim);margin-bottom:4px">raps about</div>${chips(m.topics)}` : "") +
    Object.entries(STAT_NAMES).map(([k, label]) =>
      `<div class="stat"><span>${label}</span><div class="bar"><div style="width:${(m.stats[k] || 0) * 10}%"></div></div><span>${m.stats[k] ?? "-"}</span></div>`).join("") +
    (m.sore_spots_total ? `<div class="sore"><div style="color:var(--dim)">sore spots</div>${spots}</div>` : "");
}
$("mc").addEventListener("change", showMc);

let qwenOnServer = false;     // the server has Qwen TTS (loaded or loading)
let qwenLive = false;         // this battle: the server renders the opponent's audio
let adlibUrls = {};           // text -> url of a pre-rendered Qwen ad-lib
speech.onvoiceschanged = loadVoices;
loadVoices();
fetch("health").then((r) => r.json()).then((h) => {
  qwenOnServer = h.voice !== "client";
  voiceEngine = h.voice_engine || (qwenOnServer ? "qwen" : "");
  loadVoices();
}).catch(() => {});

$("vol").oninput = () => metro && metro.setVolume(+$("vol").value);
$("startBtn").onclick = start;
$("stopBtn").onclick = () => stop("Stopped.");
$("endBtn").onclick = () => endTurn();


// ---------- metronome ----------
class Metronome {
  constructor(ctx, bpm, startAt) {
    this.ctx = ctx; this.spb = 60 / bpm; this.start = startAt; this.n = 0;
    this.out = ctx.createGain(); this.out.connect(ctx.destination); this.setVolume(+$("vol").value);
    this.timer = setInterval(() => this.schedule(), 25);
    this.schedule();
  }
  setVolume(v) { this.out.gain.value = v; }
  schedule() {
    while (this.start + this.n * this.spb < this.ctx.currentTime + 0.15) {
      this.click(this.start + this.n * this.spb, this.n % BEATS_PER_BAR === 0);
      this.n++;
    }
  }
  click(t, accent) {
    const o = this.ctx.createOscillator(), g = this.ctx.createGain();
    o.frequency.value = accent ? 1500 : 950;
    g.gain.setValueAtTime(0.0001, t);
    g.gain.exponentialRampToValueAtTime(accent ? 1 : 0.55, t + 0.002);
    g.gain.exponentialRampToValueAtTime(0.0001, t + 0.06);
    o.connect(g).connect(this.out);
    o.start(t); o.stop(t + 0.07);
  }
  stop() { clearInterval(this.timer); this.out.disconnect(); }
}

// A song as the beat: starts COUNT_IN bars before the first phrase it has room to count in to (the drop, after an
// intro), so the count-in ends as the first turn begins, and loops whole bars from the drop, so the grid never drifts.
const COUNT_IN = 2;
class SongBeat {
  constructor(ctx, buffer, song, startAt) {
    const bar = BEATS_PER_BAR * 60 / song.bpm;
    const drop = song.drop_s ?? song.first_downbeat_s;
    const phrases = song.phrases || [];
    const first = phrases.find((p) => p.start - COUNT_IN * bar >= song.first_downbeat_s - 0.01);
    const from = first ? first.start - COUNT_IN * bar : Math.max(song.first_downbeat_s, drop - COUNT_IN * bar);   // a bar line
    this.start = startAt;
    this.timing = { from, loopStart: song.loop_start_s, loopEnd: song.loop_end_s, breaks: song.breaks || [], phrases,
                    marked: !!song.marked };
    this.out = ctx.createGain(); this.out.connect(ctx.destination); this.setVolume(+$("vol").value);
    this.src = ctx.createBufferSource();
    this.src.buffer = buffer;
    this.src.loop = true;
    this.src.loopStart = song.loop_start_s;
    this.src.loopEnd = song.loop_end_s;
    this.src.connect(this.out);
    this.src.start(startAt, from);
  }
  setVolume(v) { this.out.gain.value = v; }
  stop() { try { this.src.stop(); } catch {} this.out.disconnect(); }
}

// ---------- start / stop ----------
async function start() {
  bpm = +$("bpm").value; bars = +$("bars").value;
  rounds = +$("rounds").value;
  battle++; waiting = {}; game = null; opp = null; lastSilence = 0;
  ["startBtn", "bpm", "bars", "rounds", "mc", "assist", "song"].forEach((id) => ($(id).disabled = true));
  $("stopBtn").disabled = false;
  committed = []; partial = ""; $("feed").innerHTML = ""; $("stage").innerHTML = ""; $("interp").textContent = "";

  if (!window.isSecureContext) {   // browsers only allow the mic and audio worklets on https or localhost
    stop(`Mic needs a secure page: open https://${location.hostname}:${location.port || 443} (./run.sh with HTTPS=1) ` +
         `or tunnel and use http://localhost:${location.port || 8000}  (ssh -N -L ${location.port || 8000}:localhost:${location.port || 8000} <server>)`);
    return;
  }
  try {
    // Device rate (usually 48 kHz) so the opponent's voice plays full-band; the worklet sends the mic at 16 kHz.
    ctx = new AudioContext({ latencyHint: "interactive" });
    await ctx.audioWorklet.addModule("mic-worklet.js");
    const node = new AudioWorkletNode(ctx, "mic-chunker", { processorOptions: { targetRate: SR } });
    const sink = ctx.createGain(); sink.gain.value = 0;   // keeps the worklet pulled without playing the mic
    node.connect(sink).connect(ctx.destination);
    let stream = null;
    if (clipUrl) {
      clip = await ctx.decodeAudioData(await (await fetch(clipUrl)).arrayBuffer());
    } else {
      stream = await navigator.mediaDevices.getUserMedia({
        audio: { channelCount: 1, echoCancellation: true, noiseSuppression: false, autoGainControl: true },
      });
      ctx.createMediaStreamSource(stream).connect(node);
    }
    mic = { stream, node };
  } catch (e) {
    stop("Mic unavailable: " + e.message);
    return;
  }

  const song = currentSong();
  let songBuf = null;
  if (song) {
    status(`Loading ${song.title}…`);
    songBuf = songBuffers.get(song.id) || await fetchUrl(song.url);
    if (!songBuf) { stop(`Couldn't load the beat ${song.file}`); return; }
    songBuffers.set(song.id, songBuf);
    bpm = +song.bpm;
  }
  const startAt = ctx.currentTime + 0.3;
  // The song's first downbeat (or the first click) sounds at startAt; everything else is timed from it.
  metro = song ? new SongBeat(ctx, songBuf, song, startAt) : new Metronome(ctx, bpm, startAt);
  beat0 = startAt + (ctx.outputLatency || ctx.baseLatency || 0);   // when the first beat is heard
  songTiming = song ? metro.timing : null;
  micT0 = null;

  status("Connecting…");
  ws = new WebSocket(`${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/ws/battle`);
  ws.binaryType = "arraybuffer";
  ws.onmessage = (ev) => onMessage(JSON.parse(ev.data));
  ws.onclose = () => { if (ctx) stop("Disconnected."); };
  ws.onerror = () => status("WebSocket error. Is the server running?");

  // Mic timeline 0 is the first chunk sent; the start message carries where the beat sits on it.
  mic.node.port.onmessage = ({ data }) => {
    showLevel(data.pcm);
    if (!ws || ws.readyState !== 1) return;
    if (micT0 === null) {
      micT0 = data.t;
      send({
        type: "start", sample_rate: SR, mc: $("mc").value, assist: $("assist").checked, mouth: false,
        beat: { bpm, offset_ms: (beat0 - micT0) * 1000, beats_per_bar: BEATS_PER_BAR },
      });
      runBattle(battle);
    }
    ws.send(data.pcm);
  };
  requestAnimationFrame(frame);
  lastTick = ctx.currentTime;
  ticker = setInterval(tick, 50);
}

function stop(msg) {
  battle++;
  waiting = {};
  if (ticker) clearInterval(ticker), (ticker = null);
  if (ws) { send({ type: "stop" }); ws.onclose = null; ws.close(); ws = null; }
  if (metro) metro.stop(), (metro = null);
  if (mic) mic.stream && mic.stream.getTracks().forEach((t) => t.stop()), (mic = null);
  if (verse) verse.timers.forEach(clearTimeout), verse.sources.forEach((s) => s.stop()), (verse = null);
  stopFiller();
  speech.cancel();
  if (ctx) ctx.close(), (ctx = null);
  turn = { who: null };
  ["startBtn", "bpm", "bars", "rounds", "mc", "assist", "song"].forEach((id) => ($(id).disabled = false));
  pickSong();
  songBuffers.clear();
  $("stopBtn").disabled = true; $("endBtn").disabled = true;
  if (msg) status(msg);
}

function showLevel(buf) {
  const x = new Int16Array(buf);
  let peak = 0;
  for (let i = 0; i < x.length; i += 4) peak = Math.max(peak, Math.abs(x[i]));
  $("micLevel").firstChild.style.width = Math.min(100, (peak / 32768) * 160) + "%";
}

// ---------- server messages ----------
function onMessage(m) {
  switch (m.type) {
    case "ready":
      qwenLive = m.voice === "qwen";
      adlibUrls = m.adlibs || {};
      game = new Rules.Game(m.mc, $("assist").checked);
      status(`rater: ${m.rater} · opponent: ${m.opponent} · stt: ${m.transcriber} · voice: ${qwenLive ? (ENGINE_NAMES[voiceEngine] || "server") : "page"}`);
      renderState(game.state());
      break;
    case "word_drops":   // assist: words to work in this turn, for a crowd bonus
      $("assistBox").className = "on";
      $("drops").innerHTML = "Word drops:" + m.words.map((w) => `<span class="drop" data-w="${esc(w)}">${esc(w)}</span>`).join("");
      $("rhymes").innerHTML = "Rhymes will show up here as you rap.";
      break;
    case "hints":        // assist: rhymes for the last word heard
      $("rhymes").innerHTML = m.rhymes.length
        ? `Rhymes with <b>${esc(m.word)}</b>: ` + m.rhymes.map((r) => `<span class="r">${esc(r)}</span>`).join("")
        : `No common rhymes for <b>${esc(m.word)}</b>, switch it up`;
      break;
    case "transcript":
      committed.push(...m.committed.map((w) => w.w));
      partial = m.partial;
      renderTranscript();
      break;
    case "metrics":
      renderFlow(m.window, m.lyrics);
      lastSilence = m.silence_s || 0;
      break;
    case "rating":   // one chunk rated: the rules turn it into crowd, mood and points
      if (!game) break;
      if (m.performer === "user") {
        addReaction(m, game.applyRating(m, BEATS_PER_BAR * 60 / bpm));
        send(game.context());   // the next rating sees the room as it is now
        renderState(game.state());
      } else if (opp) {
        opp.ratings.push(m);    // applied when its lines have played
        applyOpponentRatings();
      }
      break;
    case "opponent_verse":
      if (m.audio === "stream" && useQwen()) streamVerse(m.lines);
      else playVerse(m.lines, Array.isArray(m.audio) ? m.audio : null);
      $("interp").textContent = m.interpretation ? `“${m.interpretation}”  (${m.source})` : `(${m.source})`;
      break;
    case "opponent_line":   // one streamed line of the opponent's verse is rendered
      onVerseLine(m.line, m.audio);
      break;
    case "error":
      status("Server: " + m.message);
      break;
  }
  if (waiting[m.type]) { const resolve = waiting[m.type]; delete waiting[m.type]; resolve(m); }
}

// ---------- the battle: rounds of your turn, then the opponent's ----------
const nextMessage = (type) => new Promise((resolve) => (waiting[type] = resolve));

async function runBattle(id) {
  await nextMessage("ready");
  const barLen = BEATS_PER_BAR * 60 / bpm;
  for (let r = 1; r <= rounds; r++) {
    if (id !== battle) return;
    game.round = r;
    // Your turn is the song's next phrase: right after the count-in in round one, else the next one, which can be
    // the one starting as the opponent's verse ends (you're ready the moment it is).
    const now = ctx.currentTime;
    const slot = nextSlot(r === 1 ? Math.max(beat0 + COUNT_IN * barLen, now + 0.5) : now - 0.15, bars);
    await userTurn(slot.start, slot.bars, r);
    if (id !== battle) return;
    game.turn = "judging";
    await nextMessage("listen_done");
    if (id !== battle) return;
    await opponentTurn(r);
  }
  if (id !== battle) return;
  game.turn = "idle";
  const s = game.state();
  $("resultTitle").textContent = game.winner === "user" ? "You win!" : game.winner === "opponent" ? `${oppName} wins` : "Draw";
  $("resultSub").textContent = `${s.score.user.toFixed(0)} – ${s.score.opponent.toFixed(0)} · crowd ${s.crowd.mood}`;
  $("result").style.display = "flex";
  renderState(s);
  stop("Battle over.");
}

// Where the song is (seconds into the file) when context time t is heard: it starts at `from` as beat0 is heard,
// then loops whole bars.
function songPos(t) {
  const s = songTiming, x = t - beat0, first = s.loopEnd - s.from;
  return x < first ? s.from + x : s.loopStart + ((x - first) % (s.loopEnd - s.loopStart));
}

/** The next turn slot at or after context time t, as {start, bars}. With the song's phrases (/songs `phrases`, marked by
 *  hand or analysed), a turn is a whole phrase, to its end:
 *  - the user's comes in on the phrase's start (or, when late, 4 bars in, or for its last 4 bars);
 *  - the opponent's (`opponent`) on the first bar line it's ready for, with at least 4 bars left: its writing time
 *    eats the phrase's first bar or two (ad-libs) instead of the next phrase.
 *  Breaks are nobody's: whoever is next waits for the drop.
 *  Without phrases (the metronome, older song data): the next bar line, out of any break, for `want` bars. */
function nextSlot(t, want, opponent = false) {
  const barLen = BEATS_PER_BAR * 60 / bpm;
  const ph = songTiming ? songTiming.phrases : [];
  if (!ph.length) return { start: afterBreak(beat0 + Math.ceil((t - beat0) / barLen - 1e-6) * barLen), bars: want };
  const s = songTiming, p = songPos(t);
  const entries = [];
  for (const x of ph) {
    const len = Math.round((x.end - x.start) / barLen);
    for (let k = 0; k <= Math.max(0, len - 4); k += opponent ? 1 : 4) entries.push(x.start + k * barLen);
    if (!opponent && len % 4 && len > 4) entries.push(x.end - 4 * barLen);
  }
  entries.sort((a, b) => a - b);
  let at = entries.find((x) => x >= p - 0.02), start;
  if (at !== undefined) start = t + (at - p);
  else {   // past the last one: the first one on the loop's next pass
    at = entries.find((x) => x >= s.loopStart - 0.02);
    start = t + (s.loopEnd - p) + (at - s.loopStart);
  }
  const phrase = ph.find((x) => x.start <= at + 0.02 && at < x.end - 0.02);
  const bars = Math.round((phrase.end - at) / barLen);   // to the end of the phrase
  return { start: beat0 + Math.round((start - beat0) / barLen) * barLen, bars };
}

/** When the opponent's verse comes in, once it can start at `earliest`: the slot planned for it, or the next one if
 *  it was late. Also learns how long verses take to be ready, for planning the next one. */
function verseStart(earliest) {
  const o = opp;
  if (o && !o.measured) {
    o.measured = true;
    prepS = 0.7 * prepS + 0.3 * Math.max(1, earliest - o.asked);
  }
  if (o && o.plan && o.plan.start >= earliest - 0.01) return o.plan.start;
  return nextSlot(earliest, bars, true).start;
}

/** A turn starting on bar line t waits out a break in the song (an interlude, a refrain): it starts where the
 *  beat drops again. */
function afterBreak(t) {
  if (!songTiming) return t;
  const p = songPos(t);
  const barLen = BEATS_PER_BAR * 60 / bpm;
  // whole bars, so the turn stays exactly on the grid
  for (const [a, b] of songTiming.breaks) if (p >= a - 0.05 && p < b - 0.05) return t + Math.round((b - p) / barLen) * barLen;
  return t;
}

function userTurn(startCtx, turnBars, r) {
  return new Promise((resolve) => {
    game.turn = "user";
    lastSilence = 0;
    turn = { who: "user", startCtx, bars: turnBars, lengthS: turnBars * BEATS_PER_BAR * 60 / bpm, round: r, ended: resolve };
    committed = []; partial = ""; renderTranscript();
    $("interp").textContent = "";
    $("endBtn").disabled = false;
    const room = game.context();   // the room as the rater should see it at the start of the turn
    send({ type: "listen", t: startCtx - micT0, round: r, crowd_level: room.crowd_level, opponent: room.opponent });
    if (clip) {   // test clip stands in for the rapper; the turn still runs its whole phrase, like a real one
      const src = ctx.createBufferSource();
      src.buffer = clip;
      src.connect(mic.node); src.connect(ctx.destination);
      src.start(startCtx);
    }
  });
}

/** Your turn is over: the End button, the test clip finishing, or the bars running out. */
function endTurn() {
  if (turn.who !== "user" || !turn.ended) return;
  const resolve = turn.ended;
  turn.ended = null;
  $("endBtn").disabled = true;
  send({ type: "listen_end" });
  resolve();
}

async function opponentTurn(r) {
  const id = battle;
  $("assistBox").className = "";
  turn = { who: "opponent", round: r };
  game.turn = "opponent";
  $("turnSub").dataset.adlib = "";
  if ($("adlibs").checked) startFiller();
  $("endBtn").disabled = true;
  $("stage").innerHTML = `<span class="partial">${esc(oppName)} is writing a comeback…</span>`;
  const o = (opp = { ratings: [], applied: new Set(), finished: false, n: null });
  const done = new Promise((resolve) => (o.done = resolve));
  // Its verse fills the phrase it will be ready for, so it is asked for that many lines.
  o.plan = nextSlot(ctx.currentTime + prepS, bars, true);
  o.asked = ctx.currentTime;
  send(game.verseRequest(o.plan.bars));
  await done;                       // the verse has played
  // Its last ratings may still be on the way: wait a little for them.
  for (let i = 0; i < 100 && id === battle && o.n !== null && o.applied.size < Math.ceil(o.n / 2); i++) {
    await new Promise((res) => setTimeout(res, 50));
  }
}

/** Opponent ratings land when their lines have played (or right away, if they arrive late). */
function applyOpponentRatings() {
  const o = opp;
  if (!o || !game) return;
  const v = verse;
  const played = o.finished ? Infinity
    : v && v.startCtx !== null && ctx ? Math.floor((ctx.currentTime - v.startCtx) / v.barS + 1e-3) : 0;
  for (const m of o.ratings) {
    if (o.applied.has(m) || m.chunk.lines[1] > played) continue;
    o.applied.add(m);
    addReaction(m, game.applyRating(m, BEATS_PER_BAR * 60 / bpm));
    renderState(game.state());
  }
}

/** The verse finished playing. */
function verseFinished(n) {
  if (!opp) return;
  opp.n = n;
  opp.finished = true;
  applyOpponentRatings();
  opp.done();
}

// ---------- every 50 ms: the rules' clock (the pop fades, dead air drains), and the end of your bars ----------
function tick() {
  if (!ctx || !game) return;
  const now = ctx.currentTime, dt = Math.max(0, now - lastTick);
  lastTick = now;
  const live = turn.who === "user" && now >= turn.startCtx;
  game.tick(dt, live ? lastSilence : 0);
  if (live && turn.ended && now >= turn.startCtx + turn.lengthS + 0.5) endTurn();
  if (opp && !opp.finished) applyOpponentRatings();
  if (++tickN % 2 === 0) renderState(game.state());
}

// ---------- opponent verse: TTS, one line per bar ----------
function syllables(line) {
  return (line.toLowerCase().match(/[aeiouy]+/g) || []).length || 1;
}

const useQwen = () => $("voice").value === QWEN_VOICE && qwenLive;
const hasBrowserVoices = () => speech.getVoices().length > 0;
// Audio comes from the server (Qwen or espeak) rather than the browser's speech engine.
// Qwen picked but not ready this battle: the browser's default voice, or espeak if there is none.
function useServerVoice() {
  const v = $("voice").value;
  if (v === QWEN_VOICE) return qwenLive || !hasBrowserVoices();
  return v === SERVER_VOICE || v === GRID_VOICE || !hasBrowserVoices();
}

async function fetchUrl(url) {
  try {
    const r = await fetch(url);
    return r.ok && ctx ? await ctx.decodeAudioData(await r.arrayBuffer()) : null;
  } catch { return null; }
}

// A verse line is one bar: either fitted as a whole (seconds) or word by word on the grid (bpm).
// Ad-libs are plain speech.
async function fetchClip(text, asLine = false) {
  const barLen = BEATS_PER_BAR * 60 / bpm;
  const q = !asLine ? "" : $("voice").value === GRID_VOICE ? `bpm=${bpm}&` : `seconds=${(barLen * 0.95).toFixed(2)}&`;
  try {
    const r = await fetch(`tts?${q}text=${encodeURIComponent(text)}`);
    return r.ok && ctx ? await ctx.decodeAudioData(await r.arrayBuffer()) : null;
  } catch { return null; }
}

function speakNow(text, rate = 1) {
  if (!window.SpeechSynthesisUtterance) return;
  const u = new SpeechSynthesisUtterance(text);
  const voice = browserVoice();
  if (voice) u.voice = voice;
  u.rate = rate;
  speech.speak(u);
}

// ---------- ad-libs while the LLM writes the verse ----------
const OPENERS = ["my turn", "aight, my turn", "okay, okay, my turn"];
const ADLIBS = ["uh huh", "yeah, yeah", "hold up", "okay", "check it", "you done?", "aight, aight", "mm hmm",
                "let's go", "watch this", "uh", "yeah"];
const pick = (a) => a[Math.floor(Math.random() * a.length)];
const fillerCache = new Map();   // text -> AudioBuffer (server voice)

function startFiller() {
  stopFiller();
  const spb = 60 / bpm, barLen = BEATS_PER_BAR * spb;
  const f = (filler = { timers: [], sources: [], busyUntil: 0, until: Infinity });   // until: verse start
  const server = useServerVoice();
  const qwen = useQwen();
  const cacheKey = (t) => (qwen ? "q:" : "e:") + t;
  const openers = qwen ? OPENERS.filter((t) => adlibUrls[t]) : OPENERS;
  // With Qwen, every ad-lib the server rendered for this MC (its own extras included).
  const adlibs = qwen ? Object.keys(adlibUrls).filter((t) => !OPENERS.includes(t)) : ADLIBS;
  if (server) [...openers, ...adlibs].forEach((t) => fillerCache.has(cacheKey(t)) ||
    (qwen ? fetchUrl(adlibUrls[t]) : fetchClip(t)).then((b) => b && fillerCache.set(cacheKey(t), b)));

  // First line on the next beat, then one ad-lib per bar (on beat 1, sometimes beat 3).
  let n = Math.ceil((ctx.currentTime + 0.25 - beat0) / spb), first = true;
  const scheduleNext = () => {
    if (filler !== f || !ctx || !openers.length || !adlibs.length) return;
    const t = beat0 + n * spb, text = first ? pick(openers) : pick(adlibs);
    // Decide well before the beat (timers can fire late, the Quest Browser especially) and schedule the clip
    // on the audio clock, so it starts exactly on the beat.
    const lead = Math.max(0, (t - ctx.currentTime - 0.3) * 1000);
    f.timers.push(setTimeout(() => {
      if (filler !== f || !ctx) return;
      const buf = fillerCache.get(cacheKey(text));
      if (t + (buf ? buf.duration : 0.9) > f.until - 0.15) return;   // would run into the verse: done
      const at = t - (beat0 - metro.start);                           // graph time that is heard on the beat
      let said = false;
      if (server && buf && at > ctx.currentTime + 0.005) {            // too late for this beat: skip, don't play off it
        const src = ctx.createBufferSource();
        src.buffer = buf; src.connect(ctx.destination);
        src.start(at);
        f.sources.push(src);
        f.busyUntil = t + buf.duration;
        said = true;
      } else if (!server && !speech.speaking && !speech.pending) {
        speakNow(text, 1.1);   // skip rather than queue: a backed-up speech queue throws the verse off the beat
        f.busyUntil = t + 0.9;
        said = true;
      }
      if (said) { $("turnSub").dataset.adlib = text; first = false; }
      const beatInBar = ((n % BEATS_PER_BAR) + BEATS_PER_BAR) % BEATS_PER_BAR;
      n += beatInBar === 0 && Math.random() < 0.4 ? 2 : BEATS_PER_BAR - beatInBar;   // next: beat 3 or next bar line
      scheduleNext();
    }, lead));
  };
  scheduleNext();
}

function stopFiller() {
  if (!filler) return;
  filler.timers.forEach(clearTimeout);
  const now = ctx ? ctx.currentTime : 0;
  filler.sources.forEach((s) => { try { s.stop(now + 0.6); } catch {} });   // let a started ad-lib finish
  filler = null;
}

async function playVerse(lines, audio) {
  const barLen = BEATS_PER_BAR * 60 / bpm;
  let useServer = useServerVoice();
  let clips = [];
  if (useQwen() && audio) {   // the server rendered every line (with its delivery) already fitted to a bar
    clips = await Promise.all(audio.map(fetchUrl));
    useServer = clips.some(Boolean);
  } else if (useServer) {   // fetch every line first so each can start exactly on its bar (ad-libs keep going meanwhile)
    clips = await Promise.all(lines.map((l) => fetchClip(l, true)));
  }
  if (!ctx) return;
  let busyUntil = filler ? filler.busyUntil : 0;
  stopFiller();
  if (!useServer) {   // don't cancel the speech engine (Chrome voices start late after a cancel); wait for it
    for (let i = 0; i < 40 && (speech.speaking || speech.pending); i++) await new Promise((r) => setTimeout(r, 50));
    if (!ctx) return;
    busyUntil = 0;
  }
  // Come in on its phrase, after any ad-lib that is still being said.
  const startCtx = verseStart(Math.max(ctx.currentTime + 0.4, busyUntil + 0.1));
  const outLat = beat0 - metro.start;   // schedule early by the output latency so it is heard on the bar
  verse = { lines, startCtx, barS: barLen, timers: [], sources: [] };
  $("stage").innerHTML = lines.map((l, i) => `<div class="line" id="l-${i}">${esc(l)}</div>`).join("");
  const at = (t, fn) => verse.timers.push(setTimeout(fn, Math.max(0, (t - ctx.currentTime) * 1000)));

  lines.forEach((line, i) => {
    if (useServer && clips[i]) {
      const src = ctx.createBufferSource();
      src.buffer = clips[i];
      src.connect(ctx.destination);
      src.start(startCtx + i * barLen - outLat);
      verse.sources.push(src);
    }
    at(startCtx + i * barLen, () => {
      if (i > 0) $(`l-${i - 1}`).className = "line done";
      $(`l-${i}`).className = "line now";
      if (useServer) return;
      speech.cancel();   // stay on the beat even if the last line ran long
      // Roughly fit the line into one bar (rate 1 is about 3.5 syllables/s).
      speakNow(line, Math.max(0.9, Math.min(2.2, syllables(line) / (barLen * 0.9) / 3.5)));
    });
  });
  at(startCtx + lines.length * barLen, () => {
    const last = $(`l-${lines.length - 1}`);
    if (last) last.className = "line done";
    verse = null;
    verseFinished(lines.length);
  });
}

// ---------- streamed verse: lines arrive one by one while the ad-libs play ----------
// The verse starts on a bar line once enough lines are in that, at the measured render rate, every later
// line arrives before its bar. Lines are scheduled on their bars as they arrive.
function streamVerse(lines) {
  const barLen = BEATS_PER_BAR * 60 / bpm;
  verse = { lines, startCtx: null, barS: barLen, timers: [], sources: [], stream: true,
            clips: new Array(lines.length).fill(undefined), arrived: [], sentCtx: ctx.currentTime };
  $("stage").innerHTML = lines.map((l, i) => `<div class="line" id="l-${i}">${esc(l)}</div>`).join("");
}

async function onVerseLine(i, url) {
  const v = verse;
  if (!v || !v.stream || !ctx) return;
  const buf = url ? await fetchUrl(url) : null;
  if (verse !== v || !ctx) return;
  v.clips[i] = buf;                  // AudioBuffer, or null: voice this line ourselves
  v.arrived[i] = ctx.currentTime;
  if (v.startCtx === null) maybeStartVerse(v);
  else scheduleVerseLine(v, i);
}

function maybeStartVerse(v) {
  const n = v.lines.length, b = v.barS, now = ctx.currentTime;
  let have = 0;
  while (have < n && v.clips[have] !== undefined) have++;
  if (!have) return;
  // Seconds per line so far (from the verse message to the newest line), with a safety margin.
  const rate = 1.2 * (v.arrived[have - 1] - v.sentCtx) / have;
  let earliest = now + 0.4;
  for (let k = have; k < n; k++) earliest = Math.max(earliest, v.arrived[have - 1] + (k - have + 1) * rate + 0.3 - k * b);
  if (have < n && earliest > now + 2 * b) {   // not close yet: keep the ad-libs going, decide on a later line
    if (filler) filler.until = Infinity;
    return;
  }
  if (filler) earliest = Math.max(earliest, filler.busyUntil + 0.1);
  v.startCtx = verseStart(earliest);
  if (filler) filler.until = v.startCtx;
  const at = (t, fn) => v.timers.push(setTimeout(fn, Math.max(0, (t - ctx.currentTime) * 1000)));
  at(v.startCtx - 0.05, stopFiller);
  for (let i = 0; i < n; i++) {
    if (v.clips[i] !== undefined) scheduleVerseLine(v, i);
    at(v.startCtx + i * b, () => {
      if (i > 0) $(`l-${i - 1}`).className = "line done";
      $(`l-${i}`).className = "line now";
      applyOpponentRatings();
    });
  }
  at(v.startCtx + n * b, () => {
    const last = $(`l-${n - 1}`);
    if (last) last.className = "line done";
    verse = null;
    verseFinished(n);
  });
}

function scheduleVerseLine(v, i) {
  const t = v.startCtx + i * v.barS, outLat = beat0 - metro.start;
  if (t - ctx.currentTime < -0.4) return;            // arrived too late for its bar: skip, keep the beat
  const buf = v.clips[i];
  if (buf) {
    const src = ctx.createBufferSource();
    src.buffer = buf; src.connect(ctx.destination);
    src.start(Math.max(ctx.currentTime, t - outLat));
    v.sources.push(src);
  } else {                                           // Qwen failed for this line: the page's own voice
    v.timers.push(setTimeout(() => {
      speech.cancel();
      speakNow(v.lines[i], Math.max(0.9, Math.min(2.2, syllables(v.lines[i]) / (v.barS * 0.9) / 3.5)));
    }, Math.max(0, (t - ctx.currentTime) * 1000)));
  }
}

// ---------- rendering ----------
function renderTranscript() {
  $("stage").innerHTML = esc(committed.join(" ")) + (partial ? ` <span class="partial">${esc(partial)}</span>` : "");
}

function renderState(s) {
  if (!s) return;
  if (s.mc && s.mc.stats && (s.mc.sore_spots_found || []).length !== ($("sheet").querySelectorAll(".found").length)) renderSheet(s.mc);
  const c = s.crowd, o = s.opponent;
  const segs = $("crowdMeter").children;
  for (let i = 0; i < segs.length; i++) {
    const on = c.level >= i + 0.5;
    const hue = 200 - i * 17;   // cool to hot
    segs[i].style.background = on ? `hsl(${hue} 80% ${i < c.base ? 45 : 60}%)` : "";
  }
  $("crowdMood").textContent = c.mood;
  $("crowdNums").textContent = `level ${c.level.toFixed(1)} / 11 · base ${c.base.toFixed(1)}`;
  $("scoreYou").textContent = s.score.user.toFixed(0);
  $("scoreOpp").textContent = s.score.opponent.toFixed(0);
  $("oppFace").textContent = FACES[o.mood] || "😐";
  $("oppMood").textContent = o.mood;
  $("oppGesture").textContent = GESTURES[o.gesture] || o.gesture;
  for (const d of DIMS) {
    $(`d-${d}`).style.width = o[d] * 10 + "%";
    $(`v-${d}`).textContent = o[d].toFixed(1);
  }
  $("composure").style.width = o.composure * 10 + "%";
  $("composureV").textContent = o.composure.toFixed(1);
}

function renderFlow(w, lyr) {
  const vals = { ...w, rhyme_density: lyr.rhyme_density, multi_rhymes: lyr.multi_rhymes };
  for (const [k, , digits] of FLOW_STATS) {
    const v = vals[k];
    $(`f-${k}`).textContent = v == null ? "-" : (+v).toFixed(digits);
  }
}

function addReaction(m, points) {
  const c = m.crowd;
  for (const w of m.word_drops || []) {
    const chip = document.querySelector(`.drop[data-w="${CSS.escape(w)}"]`);
    if (chip) chip.classList.add("used");
  }
  const nerve = m.opponent && m.opponent.trigger;
  const tags = (nerve ? `<span class="tag fell">hit a nerve: ${esc(nerve)}</span>` : "") +
               (m.word_drops && m.word_drops.length ? `<span class="tag punch">word drop: ${esc(m.word_drops.join(", "))}</span>` : "") +
               (c.punchline > 0.6 ? `<span class="tag punch">punchline</span>` : "") +
               (c.fell_off > 0.6 ? `<span class="tag fell">fell off</span>` : "");
  const el = document.createElement("div");
  el.className = "rx";
  el.innerHTML = `<div><span class="who-${m.performer}">${m.performer === "user" ? "you" : "opp"}</span> “${esc(m.chunk.text)}”${tags}</div>
    <div class="meta">crowd ${c.reaction.toFixed(1)} · +${points.toFixed(0)} · ${esc(c.source)}</div>`;
  $("feed").prepend(el);
}

// ---------- per-frame: beat dots, turn countdown ----------
function frame() {
  if (!ctx) return;
  const spb = 60 / bpm, barLen = BEATS_PER_BAR * spb, now = ctx.currentTime;
  const b = Math.floor((now - beat0) / spb);
  const phase = (now - beat0) / spb - b;
  [...$("beats").children].forEach((d, i) => {
    d.className = "dot" + (b >= 0 && ((b % BEATS_PER_BAR) + BEATS_PER_BAR) % BEATS_PER_BAR === i && phase < 0.35 ? " on" : "") + (i === 0 ? " one" : "");
  });

  const label = $("turnLabel"), sub = $("turnSub"), prog = $("progress").firstChild;
  if (turn.who === "user" && micT0 !== null) {
    const rel = now - turn.startCtx;
    label.className = "user";
    if (rel < 0) {
      label.textContent = `Your turn in ${Math.ceil(-rel / spb)}`;
      sub.textContent = `Round ${turn.round} · ${turn.bars} bars at ${bpm} BPM`;
      prog.style.width = "0";
    } else {
      label.textContent = "Your turn — spit!";
      const bar = Math.min(turn.bars, Math.floor(rel / barLen) + 1);
      sub.textContent = `Round ${turn.round} · bar ${bar} of ${turn.bars} · press End when you're done`;
      prog.style.width = Math.min(100, (rel / (turn.bars * barLen)) * 100) + "%";
    }
    prog.style.background = "var(--you)";
  } else if (turn.who === "opponent") {
    label.className = "opponent";
    label.textContent = `${oppName}'s turn`;
    if (verse && now >= verse.startCtx) {
      const i = Math.floor((now - verse.startCtx) / verse.barS);
      sub.textContent = `Round ${turn.round} · bar ${Math.min(i + 1, verse.lines.length)} of ${verse.lines.length}`;
      prog.style.width = Math.min(100, ((now - verse.startCtx) / (verse.lines.length * verse.barS)) * 100) + "%";
    } else {
      const adlib = sub.dataset.adlib;
      sub.textContent = (verse ? "Coming in on the next bar…" : "Writing the comeback…") + (adlib ? `  “${adlib}”` : "");
      prog.style.width = "0";
    }
    prog.style.background = "var(--opp)";
  }
  requestAnimationFrame(frame);
}
