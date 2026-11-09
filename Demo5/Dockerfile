# The Freestyle server (backend/) as a container, for Google Cloud Run: built from the repo as it is in git, so
# Cloud Run's "Continuously deploy from a repository" can build it on every push (Build type: Dockerfile,
# source location /Dockerfile). See "Hosting" in README.md for the service's settings.
# No secrets in the image: the API keys and FREESTYLE_PASSWORD come from Secret Manager as environment variables.
FROM python:3.12-slim

# espeak-ng: the free fallback voice (/tts) while the cloud voice loads, or if it fails
RUN apt-get update \
 && apt-get install -y --no-install-recommends espeak-ng \
 && rm -rf /var/lib/apt/lists/*

ENV PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1
WORKDIR /app

# Rhubarb Lip Sync (the server's mouth cues): third_party/ isn't in git, so the release is fetched here
# (just the binary and its models), where lipsync.find_rhubarb looks for it
ARG RHUBARB=1.14.0
RUN python -c "import io, os, urllib.request, zipfile; v = '${RHUBARB}'; top = f'Rhubarb-Lip-Sync-{v}-Linux/'; \
url = f'https://github.com/DanielSWolf/rhubarb-lip-sync/releases/download/v{v}/{top[:-1]}.zip'; \
z = zipfile.ZipFile(io.BytesIO(urllib.request.urlopen(url, timeout=120).read())); \
z.extractall('third_party', [n for n in z.namelist() if n == top + 'rhubarb' or n.startswith(top + 'res/')]); \
os.chmod(f'third_party/{top}rhubarb', 0o755)"

# Whisper (local speech-to-text) and pytest aren't needed here: TRANSCRIBER is deepgram or openrouter
COPY backend/requirements.txt backend/requirements.txt
RUN grep -v -E '^(faster-whisper|pytest)' backend/requirements.txt > /tmp/requirements.txt \
 && pip install -r /tmp/requirements.txt

COPY backend backend
WORKDIR /app/backend
ENV TRANSCRIBER=deepgram
# Cloud Run says which port in $PORT (8080). One process: a battle's session and its voice clips live in its memory.
# --proxy-headers: Cloud Run ends TLS in front of it.
CMD ["sh", "-c", "exec uvicorn freestyle.server:app --host 0.0.0.0 --port ${PORT:-8080} --proxy-headers --forwarded-allow-ips='*' --ws-ping-interval 20"]
