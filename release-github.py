#!/usr/bin/env python3
"""Publica una release en GitHub con los paquetes de una version (constitucion general, seccion 8).

Uso (desde la carpeta del repositorio, con la version ya commiteada y pusheada):
    python ..\\Shared\\release-github.py v2026.09.13.2 releases\\TdtOnline-2026.09.13.2.apk
    python ..\\..\\Shared\\release-github.py v2026.9.13.2 bin\\...\\publish\\sOCPhoneMirror.exe bin\\TaskManager.msix

- La etiqueta es `v` + la version del csproj (`v2026.09.13.2` en Android, `v2026.9.13.2` en Windows).
- Las notas salen del CHANGELOG.md: la primera seccion `## ...`, tal cual.
- El token de GitHub es el que ya tiene guardado git (`git credential fill`): no hay que dar nada.
- Si la release ya existe se reutiliza y se añaden (o sustituyen) los ficheros.
"""

import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request

API = "https://api.github.com"


def token():
    out = subprocess.run(["git", "credential", "fill"], input="protocol=https\nhost=github.com\n\n",
                         capture_output=True, text=True, check=True).stdout
    for line in out.splitlines():
        if line.startswith("password="):
            return line.split("=", 1)[1]
    raise SystemExit("git no tiene guardada ninguna credencial de github.com")


def repo_slug():
    url = subprocess.run(["git", "remote", "get-url", "origin"], capture_output=True, text=True, check=True).stdout.strip()
    m = re.search(r"github\.com[:/]([^/]+/[^/.]+)", url)
    if not m:
        raise SystemExit(f"El remoto no es de GitHub: {url}")
    return m.group(1)


def notes(tag):
    if not os.path.exists("CHANGELOG.md"):
        return f"Version {tag}"
    text = open("CHANGELOG.md", encoding="utf-8").read()
    sections = re.split(r"^## ", text, flags=re.M)
    if len(sections) < 2:
        return f"Version {tag}"
    first = sections[1].strip()
    title, _, body = first.partition("\n")
    return f"## {title}\n{body.strip()}"


def call(method, url, tok, data=None, content_type="application/json", raw=None):
    body = raw if raw is not None else (json.dumps(data).encode() if data is not None else None)
    req = urllib.request.Request(url, data=body, method=method, headers={
        "Authorization": f"token {tok}", "Accept": "application/vnd.github+json", "Content-Type": content_type})
    try:
        with urllib.request.urlopen(req) as r:
            return json.load(r) if r.status != 204 else None
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return None
        raise SystemExit(f"{method} {url}: {e.code} {e.read().decode(errors='replace')[:300]}")


def main():
    if len(sys.argv) < 2 or not sys.argv[1].startswith("v"):
        raise SystemExit(__doc__)
    tag, files = sys.argv[1], sys.argv[2:]
    for f in files:
        if not os.path.isfile(f):
            raise SystemExit(f"No existe {f}")

    tok = token()
    slug = repo_slug()
    branch = subprocess.run(["git", "rev-parse", "--abbrev-ref", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()

    release = call("GET", f"{API}/repos/{slug}/releases/tags/{tag}", tok)
    if release is None:
        release = call("POST", f"{API}/repos/{slug}/releases", tok, {
            "tag_name": tag, "target_commitish": branch, "name": tag, "body": notes(tag),
            "draft": False, "prerelease": False})
        print(f"release {tag} creada en {slug}")
    else:
        print(f"release {tag} ya existia en {slug}: se añaden los ficheros")

    existing = {a["name"]: a["id"] for a in release.get("assets", [])}
    upload_url = release["upload_url"].split("{")[0]
    for f in files:
        name = os.path.basename(f)
        if name in existing:
            call("DELETE", f"{API}/repos/{slug}/releases/assets/{existing[name]}", tok)
        with open(f, "rb") as fh:
            call("POST", f"{upload_url}?name={urllib.request.quote(name)}", tok,
                 content_type="application/octet-stream", raw=fh.read())
        print(f"  subido {name} ({os.path.getsize(f) // 1024} KB)")

    print(release["html_url"])


if __name__ == "__main__":
    main()
