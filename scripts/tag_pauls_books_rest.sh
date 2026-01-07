#!/usr/bin/env bash
set -euo pipefail

DADS_DIR="${1-}"
MOMS_DIR="${2-}"
REMOTE_HOST="${3-}"
REMOTE_LIBRARY="${4-}"
TAG="${5-}"

if [ -z "${DADS_DIR}" ]; then
  DADS_DIR="/Users/paulferrer/Documents/Dad's Kindle"
fi
if [ -z "${MOMS_DIR}" ]; then
  MOMS_DIR="/Users/paulferrer/Documents/Mom's Kindle"
fi
if [ -z "${REMOTE_HOST}" ]; then
  REMOTE_HOST="pferrer@FS01pfBooks.synology.me"
fi
if [ -z "${REMOTE_LIBRARY}" ]; then
  REMOTE_LIBRARY="/volume1/books/Library"
fi
if [ -z "${TAG}" ]; then
  TAG="Paul's Books"
fi

exclude_b64="$(DADS_DIR="${DADS_DIR}" MOMS_DIR="${MOMS_DIR}" python3 - <<'PY'
import base64
import json
import os
import sys

def read_dir(path):
    try:
        entries = os.listdir(path)
    except FileNotFoundError:
        sys.stderr.write(f"Local folder not found: {path}\n")
        sys.exit(1)
    names = []
    for name in entries:
        full = os.path.join(path, name)
        if os.path.isfile(full):
            names.append(name)
    return names

dads = read_dir(os.environ["DADS_DIR"])
moms = read_dir(os.environ["MOMS_DIR"])
exclude = sorted(set(dads + moms))
payload = json.dumps(exclude).encode("utf-8")
print(base64.b64encode(payload).decode("ascii"))
PY
)"

python_code=$(cat <<'PY'
import base64
import json
import os

library = os.environ.get("LIBRARY", "/volume1/books/Library")
tag = os.environ.get("TAG", "Paul's Books")
raw = os.environ.get("EXCLUDE_B64", "")

exclude = set()
if raw:
    try:
        payload = base64.b64decode(raw.encode("ascii"))
        exclude = set(json.loads(payload.decode("utf-8")))
    except Exception:
        exclude = set()

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
    if file_name in exclude:
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
escaped_exclude=$(printf "%q" "${exclude_b64}")
escaped_py=$(printf "%q" "${python_code}")

ssh "${REMOTE_HOST}" \
  "TAG=${escaped_tag} LIBRARY=${escaped_library} EXCLUDE_B64=${escaped_exclude} python3 -c ${escaped_py}"
