#!/usr/bin/env python3
"""Check tracked public content. Reports paths/pattern names only, never secret values."""
from pathlib import Path
import re
import subprocess
root = Path(__file__).resolve().parents[1]
paths = subprocess.check_output(["git", "ls-files", "-z"], cwd=root).decode().split("\0")
forbidden = ("Assets/Standard Assets/", "Assets/Models/", "Assets/Art/Military/Characters/", "Assets/Scenes/", "Assets/Prefabs/", "Assets/Animations/", "Assets/Maps/", "UserSettings/", "Library/", "Builds/", "Server/deploy/out/", "Server/deploy/.build/")
patterns = {
    "AWS access key": re.compile(rb"(?:AKIA|ASIA)[A-Z0-9]{16}"),
    "private key": re.compile(rb"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
    "GitHub token": re.compile(rb"(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})"),
    "Slack token": re.compile(rb"xox[baprs]-[A-Za-z0-9-]{20,}"),
}
failures = []
for rel in filter(None, paths):
    if rel.startswith(forbidden) or rel == "Server/deploy/config.env" or rel.endswith((".pem", ".pfx", ".key")):
        failures.append((rel, "excluded path"))
    file = root / rel
    if not file.is_file(): continue
    data = file.read_bytes()
    for label, pattern in patterns.items():
        if pattern.search(data): failures.append((rel, label))
for name in ("README.md", "README.en.md", "README.zh-CN.md", "LICENSE", "THIRD_PARTY_NOTICES.md"):
    if name not in paths: failures.append((name, "required document missing"))
for rel, label in failures: print(f"FAIL: {rel}: {label}")
if failures: raise SystemExit(1)
print(f"Public-content checks passed ({len([p for p in paths if p])} tracked files).")
