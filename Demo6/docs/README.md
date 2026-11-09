# Design and implementation notes

Start with the [project README](../README.md) (what it is, running it, controls) and the [full guide](GUIDE.md) (building, flashing, timing, data, current capability). The repository's
[AGENTS.md](../AGENTS.md) specifies architecture, protocol intent, quality requirements and
hardware verification gates. It is not a claim that every milestone is complete.

## Contents

| Path | Purpose |
|---|---|
| [GUIDE.md](GUIDE.md) | Everything beyond the README: what is included, repository map, requirements, building and flashing, timing and placement, data and replay, development and validation |
| [Recovery checks — 2026-10-29](recovery-checks-2026-10-29.md) | Crash recovery, completed density UI wiring and fresh software verification results |
| `session-writer-plan.md` | Recording decisions, binary/JSON layout, clock/pose fusion, lifecycle and implementation notes |
| [concepts/](concepts/README.md) | Early visual concepts and CAD iteration screenshots |

The recording document contains both proposals and an **Implementation notes (as built)**
section. Check source when they differ. In particular, automatic realignment, full spatial
accuracy validation and all future visualization modes should not be assumed complete.

## Maintaining evidence

Record the exact board, firmware/core, Quest OS and SDK versions with hardware observations.
Distinguish compilation, mocked checks, a single real-device reproduction and a supported
capability matrix. Include timing/pose uncertainty and known limits when publishing results.
Current BLE timing investigation and callback-clock details are in the [Unity guide](../unity/README.md).

Personal captures and raw survey data are ignored by default. Add a deliberately sanitized
fixture or image explicitly when it is needed to reproduce a public issue. Historical concepts
must remain distinguishable from printable CAD and the current application (probe on either controller or the wrist).
