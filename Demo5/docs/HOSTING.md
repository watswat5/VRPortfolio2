# Hosting the server on Google Cloud Run

How the hosted server is deployed. Running it yourself instead: [SETUP.md](SETUP.md).

The repo's `Dockerfile` builds the server as a container (no secrets in it; Rhubarb is fetched during the build,
local Whisper left out), so Cloud Run can build and deploy it from GitHub on every push:

1. In the Cloud console: **Cloud Run → Create service → Continuously deploy from a repository**, connect GitHub and
   pick this repo and branch, **Build type: Dockerfile**, source location `/Dockerfile`.
2. **Region** near the players (it can't be changed later), e.g. `us-east1`.
3. **Authentication: Allow public access** (the server's own password guards it). **Billing: request-based**.
4. **Scaling: minimum 1, maximum 1 instance.** A battle's session and its voice clips live in the server's memory, so
   it must be exactly one instance; always one also means no cold start.
5. **Containers:** port `8080`, memory 2 GiB, **request timeout 3600 s** (a battle is one long websocket; the default
   5 minutes cuts it off).
6. **Variables & secrets:** from Secret Manager, `OPENROUTER_API_KEY`, `DEEPGRAM_API_KEY` and `FREESTYLE_PASSWORD`
   (required: the URL is public); as plain variables, `VOICE=openrouter`, `TTS_MODEL=fish-audio/drama-3` (and any
   other settings from your `backend/.env`). `TRANSCRIBER` defaults to `deepgram` in the image.

Then on the headset: options menu → SERVER: the service's `https://...run.app/` URL, PASSWORD: yours. A new
instance renders every MC's ad-libs once at startup (cents); `backend/out/` isn't in git, so nothing is cached
between deploys.
