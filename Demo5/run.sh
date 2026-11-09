#!/usr/bin/env bash
# Start the server.  HOST/PORT override the defaults (0.0.0.0 so the Quest on your network can reach it).
#
#   HTTPS=1 ./run.sh   serve https:// with a self-signed certificate (made once in backend/certs/), so the
#                      browser GUI can use the mic from another machine. Accept the browser warning once.
#                      The headset then connects with wss:// instead of ws://.
set -euo pipefail
cd "$(dirname "$0")"
[ -d .venv ] || { echo "run ./setup.sh first" >&2; exit 1; }
# shellcheck disable=SC1091
source .venv/bin/activate   # also puts the CUDA libs on LD_LIBRARY_PATH
cd backend

SSL=()
if [ "${HTTPS:-0}" = 1 ]; then
  if [ ! -f certs/cert.pem ]; then
    mkdir -p certs
    SAN="DNS:localhost,DNS:$(hostname),IP:127.0.0.1"
    for ip in $(hostname -I); do SAN="$SAN,IP:$ip"; done
    openssl req -x509 -newkey rsa:2048 -nodes -days 825 -subj "/CN=$(hostname)" \
      -addext "subjectAltName=$SAN" -keyout certs/key.pem -out certs/cert.pem 2>/dev/null
    echo "made a self-signed certificate for: $SAN"
  fi
  SSL=(--ssl-keyfile certs/key.pem --ssl-certfile certs/cert.pem)
  echo "open https://$(hostname -I | awk '{print $1}'):${PORT:-8000}"
fi
exec uvicorn freestyle.server:app --host "${HOST:-0.0.0.0}" --port "${PORT:-8000}" "${SSL[@]}"
