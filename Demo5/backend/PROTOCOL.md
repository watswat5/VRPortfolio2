# Battle WebSocket protocol

One WebSocket per battle: `ws://<server>:8000/ws/battle`.
When the server has a password (`FREESTYLE_PASSWORD`), every request needs it; see [Password](#password).
Binary frames carry audio. Text frames are JSON objects with a `type`.
Times (`t`) are seconds on the **mic timeline**: 0 is the first audio sample the client sent.

**The game runs in the client.** The client decides the rounds and turns, and applies the game rules (crowd,
opponent mood, score, winner: [game rules](#game-rules)) to the server's ratings. The server is a set of AI services
for the battle: it transcribes the user's turn, measures the flow, rates each chunk, and writes, voices and rates the
opponent's verse. It keeps only what the AI needs: the audio, the transcript, and what each side said last.

A battle, from the client's side:

1. `start`, wait for `ready`, stream mic audio from then on.
2. Each round: pick the bar line where the user's bars start (after a count-in in round one), send `listen`. Apply
   each `rating` as it arrives and send `context` after it. When the bars run out (or the user presses End), send
   `listen_end`; `listen_done` follows the turn's last rating.
3. Send `verse`. Play `opponent_verse` (and its `opponent_line` audio) a line per bar. Its `rating`s arrive at once;
   apply each when its lines have played.
4. After the last round, the higher score wins.

## Client to server

| type | when | fields |
|---|---|---|
| `start` | first message | `sample_rate` (default 16000), `beat` `{bpm, offset_ms, beats_per_bar}` or null, `mc` (from `GET /mcs`), `assist` (rhyme hints and word drops; default false), `outfit` (what the user is wearing: a short plain description per accessory, e.g. `["a red baseball cap", "a robot body painted purple"]`; at most 8, each cut to one line of 80 characters; the opponent may clown it, and the rater knows it too), `profile` (the player's pre-show interview summed up, from `POST /interview/summary`: the opponents use it to make it personal; at most 700 characters), `mouth` (false: no mouth cues with the opponent's lines, saving the server's CPU for a client with no face to animate; default true) |
| *(binary)* | continuously, from `ready` until the end | mono PCM16 little-endian, any chunk size (20 ms recommended). Keep sending during the opponent's verse too, so the timeline matches the beat. |
| `listen` | the user's turn | `t` (mic timeline: when the user's bars start, a bar line), `round`, `crowd_level` (0-11), `opponent` (the five mood dimensions, 0-10) |
| `context` | after applying each user rating | `crowd_level`, `opponent`: the room as the client's rules see it now, so the next rating starts from it |
| `listen_end` | the user's turn is over | |
| `verse` | ask for the comeback | `round`, `bars`, `crowd` `{level, mood}`, `opponent` `{mood, intimidated, entertained, confident, annoyed, impressed}`, `score` `{user, opponent}`, `riled` (sore spots hit since the MC's last verse), `hits` (up to three user lines that got to the opponent most) |
| `ping` | any time | `client` (echoed back in `pong`) |
| `stop` | any time | closes the battle |

`beat.offset_ms` is when any beat of the backing track lands, measured on the mic timeline.
On Quest this is "beat playback start time minus mic capture start time", corrected for output latency.

## Server to client

| type | meaning | key fields |
|---|---|---|
| `ready` | pipeline is up, start streaming | `sample_rate`, `rater`, `opponent`, `transcriber`, `mc` (the MC's public sheet, with `tuning` and `mood` for the rules), `voice` (`"qwen"` when the server voices the opponent, whichever engine it runs; else `"client"`), `mouth` (`"rhubarb"` when `opponent_line` carries mouth cues; null when it won't: lip-sync from the audio), `adlibs` (ad-lib text → clip URL in that voice; empty without one) |
| `word_drops` | assist, at `listen` | `words` to work in (from the opponent's last verse, or battle topics in round one) |
| `transcript` | live words | `committed` `[{w, s, e}]` (final), `partial` (may change) |
| `hints` | assist, as the user raps | `word` (last content word heard), `rhymes` (common perfect, then slant rhymes) |
| `metrics` | 2 Hz flow metrics during the user's turn | `window` (last 6 s), `turn` (whole turn), `lyrics`, `silence_s` (trailing silence: 1.5 s or more is dead air) |
| `rating` | one chunk was rated | `performer` (`user`/`opponent`), `round`, `chunk` (user: `{text, start, end}` on the mic timeline; opponent: `{text, lines: [a, b]}`, verse lines a to b-1), `stall` (user: a pause with no words), `crowd` `{reaction 0-11, punchline, fell_off, flow, lyricism, confidence, source, latency_ms}`, `opponent` `{dims, gesture, gesture_probs, trigger (sore spot hit, or null), trigger_p, confidence, ...}`, `word_drops` (drops used in this chunk) |
| `listen_done` | the user's turn is finished, after its last rating | `round`, `verse` (the whole turn) |
| `opponent_verse` | the comeback | `lines[]` (one per bar), `bar_s`, `interpretation`, `source`, `audio`: `"stream"` when the server voices it (lines follow as `opponent_line`), else null (client voices it) |
| `opponent_line` | one line of a streamed verse is rendered | `line` (index), `audio` (URL of a WAV fitted to one bar, or null: voice this line yourself), `mouth`: the line's mouth cues, `[[start_s, shape], ...]` from the start of the WAV, Rhubarb's shapes A (closed: P B M), B (teeth: K S T EE), C (open: EH), D (wide: AA), E (rounded: AO ER), F (puckered: OO W), G (F V), H (L), X (rest), each holding until the next; null without Rhubarb on the server (lip-sync from the audio instead). Start the verse on a bar line once the lines in hand plus the arrival rate cover the whole verse; play each line on its bar |
| `pong` | answer to `ping` | `client` (as sent), `t` (seconds of mic audio received) |
| `error` | something failed | `message` |

A user chunk is a few words, or a phrase ending in a pause, rated mid-verse; the opponent's verse is rated two
lines at a time.

### Metric fields (`window` / `turn`)

`syllable_rate` (syllables/s while rapping), `syllables_per_beat` (4 = straight 16ths), `rate_consistency` (0-1),
`speech_ratio`, `pause_count`, `longest_pause_s`, `dead_air_s` (silence in gaps over 1.5 s),
`loudness_db`, `dynamics_db`, `on_beat` (share of syllables within 25% of a 16th slot),
`pocket` (0-1 lock to the 16th grid), `downbeat_hits` (share of beats with a syllable on them).

`lyrics`: `words`, `syllables`, `rhyme_density`, `multi_rhymes`, `end_rhyme_rate`, `unique_ratio`, `repeat_ratio`.

## Game rules

The reference is [`freestyle/game.py`](freestyle/game.py) (used by `tools/test_client.py` and the tests); the Unity
client (`BattleRules.cs`) and the web GUI (`web/rules.js`) are ports of it. For each `rating`:

- **Crowd** runs 0 (dead) to 11 (raise the roof) and is `base + reaction`: `reaction` is the instant pop for the
  last chunk and fades with a 2 s half-life, `base` is where the room sits and only moves when good chunks raise it,
  weak ones lower it, or dead air (`silence_s` of 1.5 s or more during the user's turn) drains it. Names by level:
  dead, silent, restless, polite, warming up, nodding, into it, cheering, hyped, going wild, losing it, raise the roof.
- **Opponent mood**: five dimensions, each 0-10, eased toward each rating; `mood` is one of cocky, calm, amused,
  respectful, heated, rattled; `gesture` is one of laughs, shrugs, nods, winces, glares, steps_back (for the avatar).
  A `trigger` (sore spot hit) annoys the MC and costs it confidence; it goes into the next `verse` request's `riled`.
- **Points** come from the crowd, times the chunk's length (`end - start`, or `bar_s` per opponent line); a stall
  earns none; word drops add crowd levels. Each turn's points are scaled to a standard-length turn (standard bars
  / the turn's bars, kept within 0.25-4), so a side given fewer bars (a song's phrases differ) isn't behind for it.
- The MC's `tuning` (from `ready.mc` or `GET /mcs`) sets the difficulty: `crowd_offset` and `crowd_home` shift the
  crowd for the user and the MC, `opp_gain` scales how far the user moves the MC's mood, and `resilience` is how
  fast it settles back to its starting `mood`.

## HTTP

`POST /lipsync?text=...&accurate=1` (a WAV as the body) returns `{"mouth": [[start_s, shape], ...]}`, the same cues
as `opponent_line`, for clips the server didn't voice (ad-libs, the headset's own); `accurate` uses Rhubarb's English
recognizer, better with `text` but slower than real time. 404 without Rhubarb (`./setup.sh --rhubarb`).

`GET /` serves the web GUI (`web/`). `GET /tts?text=...&bpm=90` returns one bar of espeak-ng audio
(WAV) with the words placed on the 16th-note grid, the last syllable on beat 4 (the opponent's
verse, one call per line); `seconds=` instead fits the whole line to that length. With neither it returns
plain speech (ad-libs). 404 when espeak-ng isn't installed.
`GET /clip/{key}` and `GET /adlib/{mc}/{i}` serve the server voice's rendered lines and ad-libs (the URLs in
`opponent_line` and `ready.adlibs`).
`GET /songs` lists the backing beats: `id`, `file`, `title`, `bpm` (the tempo to rap at), `beats_per_bar`,
`first_downbeat_s`, `drop_s`/`drop_bar` (where the beat kicks in after an intro), `loop_start_s`/`loop_end_s`
(whole bars from the drop), `breaks` (`[[start_s, end_s], ...]`: whole bars in the loop where the beat drops out, an
interlude, a stripped-down refrain or a one-bar pause; `end_s` is where it drops again), `phrases`
(`[{start, end, section}, ...]`: the loop's beat cut into turn-sized slots, see below), `marked` (true when the
phrases and breaks were marked by hand), `duration_s`, `url`. All times are seconds into the file.

`POST /songs/{id}/markup` with `{segments: [{kind: "phrase" | "break", start, end}], first_downbeat_s?}` (seconds, on
the bar grid) replaces a song's analysed phrases and breaks with hand-marked ones, kept in `backend/songs/markup.json`;
the drop becomes the first phrase, and the loop runs from it to the end of the last segment. `DELETE` on the same path
goes back to the analysis. The web GUI's song markup page (`/markup.html`) does both.

**Timing turns to a song** (both clients do this; `web/app.js` `nextSlot` is the reference):

- Start playback on a bar line `count_in_bars` before the first phrase there's room to count in to (the drop, after an
  intro; else a later phrase start), and send the moment a bar line is heard as `beat.offset_ms`, with `bpm`, in `start`.
- A `section` is the beat between breaks of 2+ bars; its phrases are contiguous. One-bar drop-outs stay inside it but
  end a phrase, and the 8-bar grid starts again after them.
- A turn is a whole phrase (marked by hand or analysed), to its end. The user's comes in on the phrase's start (or,
  when late, 4 bars in, or for its last 4 bars). The opponent's comes in on the first bar line it's ready for, with at
  least 4 bars left: its writing time eats the first bar or two of the phrase (ad-libs), not the next phrase. So turn
  boundaries are phrase boundaries and no turn crosses a break.
- The opponent's slot is planned when the user's turn ends, from how long its verses take to be ready, and `verse`
  asks for that many `bars`. If it is late, it comes in at the next slot.
- Breaks are nobody's turn: whoever is next waits for the drop (the player gets their breather; the opponent writes).
- Without `phrases` (the metronome, older song data), a turn starts on the next bar line out of any break and runs
  the client's bars-per-turn setting.

The server only analyses the songs and serves them: a battle never knows which song is playing. It gets the tempo
and where the beat sits on the mic timeline (`start.beat`), for the timing metrics; playing the song, and timing the
turns to it, is the client's job. So `/songs` is optional: a client can bring its own tracks, with their timing
from the same analysis run offline (`tools/export_songs.py` writes this format).
`GET /mcs` lists the opponents with their public character sheets (background, traits, topics, catchphrases,
`outfit` (what the MC wears in the game; the opponent LLM and the rater know it, so jabs at it can land), stats, `sore_spots_total`; the sore spots themselves stay hidden, and a client reveals one when a rating's
`trigger` names it), plus `tuning` and `mood` for the game rules.
`GET /mcs/<id>/tip` (`known`, `avoid`: sore spots, repeatable) returns `{"mc", "name", "spot", "left", "total"}`: one
sore spot not in `known` (one in `avoid`, e.g. found in the battle, only once the rest are told), or `spot: null` when
all are known. The headset sticks one on the dressing room's mirror after each loss.
The pre-show interview (the headset's first trip to the stage): `GET /interview/lines` is the interviewer's script
(`intro`, `questions`, `affirmations`, `outro`, each id -> text; her clips are pre-rendered from it by
`tools/render_interviewer.py`). `POST /interview/transcribe` takes one spoken answer, a 16-bit WAV as the body, and
returns `{"text"}`. `POST /interview/summary` takes `{"answers": [{"question", "answer"}]}` and returns `{"name",
"summary", "source"}`: notes on the player for the opponents (the LLM's, else the answers themselves), which the
headset keeps in its save file and sends as `start.profile`.
`GET /dj/name?name=<rapper name>` returns a WAV of the DJ announcing that name (the player's, from the headset's
save; the DJ's other lines are pre-rendered by `tools/render_dj.py` in the same voice, `[dj]` in voices.toml). The
name is cleaned to one short line; each name is rendered once and cached. 404 when there's no voice to say it in.
`GET /rhymes?word=<w>` (`n`, default 6; `explicit`, default false) returns `{"word", "rhymes"}`: common words
that rhyme with it, perfect rhymes first, then slant rhymes (the assist's rhyme engine). The headset uses it to pair
each floating keyword with a rhyme.
`GET /health` reports the active `transcriber`, `rater` and `opponent`, `voice` (`qwen` when a server voice is
loaded, whatever the engine; `loading`; or `client`) and `voice_engine` (`qwen`, `indextts`, `breeze`, `openrouter`
or `none`), and `mouth` (`rhubarb` when the server makes mouth cues, else null).

## Password

With `FREESTYLE_PASSWORD` set, every request (the WebSocket handshake too) needs either
`Authorization: Bearer <password>` (headset, tools) or the `fs_session` cookie from the login form (browser:
`GET /` redirects to `/login`; `POST /login` with form field `password` sets a session cookie, which ends when the
browser closes). Without it: 401 for HTTP, a refused handshake (403) for the WebSocket. Five different wrong
passwords from one address within a minute lock it out for a minute. Unset, the server is open.
