#!/usr/bin/env bash
set -euo pipefail

LOCAL_DIR="${1-}"
REMOTE_HOST="${2-}"
REMOTE_LIBRARY="${3-}"
TAG="${4-}"

if [ -z "${LOCAL_DIR}" ]; then
  LOCAL_DIR="/Users/paulferrer/Documents/Dad's Kindle"
fi
if [ -z "${REMOTE_HOST}" ]; then
  REMOTE_HOST="pferrer@FS01pfBooks.synology.me"
fi
if [ -z "${REMOTE_LIBRARY}" ]; then
  REMOTE_LIBRARY="/volume1/books/Library"
fi
if [ -z "${TAG}" ]; then
  TAG="Dad's Books"
fi

list_b64="$(LOCAL_DIR="${LOCAL_DIR}" python3 - <<'PY'
import base64
import json
import os
import sys

root = os.environ.get("LOCAL_DIR", "/Users/paulferrer/Documents/Dad's Kindle")
try:
    entries = os.listdir(root)
except FileNotFoundError:
    sys.stderr.write(f"Local folder not found: {root}\n")
    sys.exit(1)

names = []
for name in sorted(entries):
    path = os.path.join(root, name)
    if os.path.isfile(path):
        names.append(name)

payload = json.dumps(names).encode("utf-8")
print(base64.b64encode(payload).decode("ascii"))
PY
)"

python_code=$(cat <<'PY'
import base64
import json
import os

library = os.environ.get("LIBRARY", "/volume1/books/Library")
tag = os.environ.get("TAG", "Dad's Books")
raw = os.environ.get("LIST_B64", "")

names = set()
if raw:
    try:
        payload = base64.b64decode(raw.encode("ascii"))
        names = set(json.loads(payload.decode("utf-8")))
    except Exception:
        names = set()

updated = 0
matched = 0
for entry in os.listdir(library):
    if not entry.endswith(".meta.json"):
        continue
    path = os.path.join(library, entry)
    try:
        with open(path, "r", encoding="utf-8") as handle:
            meta = json.load(handle)
    except Exception:
        continue

    file_name = meta.get("fileName") or entry[:-10]
    if file_name not in names:
        continue

    matched += 1
    tags = meta.get("tags")
    if not isinstance(tags, list):
        tags = []

    existing = {str(t).lower() for t in tags}
    if tag.lower() not in existing:
        tags.append(tag)
        meta["tags"] = tags
        with open(path, "w", encoding="utf-8") as handle:
            json.dump(meta, handle, indent=2)
        updated += 1

print(f"Matched {matched} book(s). Updated {updated} metadata file(s).")
PY
)

escaped_tag=$(printf "%q" "${TAG}")
escaped_library=$(printf "%q" "${REMOTE_LIBRARY}")
escaped_list=$(printf "%q" "${list_b64}")
escaped_py=$(printf "%q" "${python_code}")

ssh "${REMOTE_HOST}" \
  "TAG=${escaped_tag} LIBRARY=${escaped_library} LIST_B64=${escaped_list} python3 -c ${escaped_py}"
