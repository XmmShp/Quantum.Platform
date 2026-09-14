#!/usr/bin/env bash
set -euo pipefail

readonly repository_url="https://github.com/XmmShp/Quantum.Platform"
readonly runner_version="2.336.0"
readonly runner_sha256="04cf0be1aff4c3ec3554466c39124ca250e3effd8873bb7e8d68535aa9505d5d"
readonly runner_root="${RUNNER_ROOT:-$HOME/actions-runner-quantum-platform}"
readonly archive="actions-runner-linux-x64-${runner_version}.tar.gz"

if [[ -z "${RUNNER_TOKEN:-}" ]]; then
  echo "RUNNER_TOKEN must contain a current repository runner registration token." >&2
  exit 2
fi

if [[ -e "$runner_root/.runner" ]]; then
  echo "A runner is already configured at $runner_root." >&2
  exit 3
fi

mkdir -p "$runner_root"
cd "$runner_root"

curl --fail --location --output "$archive" \
  "https://github.com/actions/runner/releases/download/v${runner_version}/${archive}"
echo "${runner_sha256}  ${archive}" | sha256sum --check --strict
tar --extract --gzip --file "$archive"
rm "$archive"

./config.sh \
  --unattended \
  --url "$repository_url" \
  --token "$RUNNER_TOKEN" \
  --name "$(hostname)-quantum-platform" \
  --labels "quantum-platform-prod" \
  --work "_work" \
  --replace

mkdir -p "$HOME/.config/systemd/user"
cat >"$HOME/.config/systemd/user/github-actions-runner-quantum-platform.service" <<UNIT
[Unit]
Description=GitHub Actions Runner for Quantum Platform
After=network-online.target docker.service
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=$runner_root
ExecStart=$runner_root/run.sh
Restart=always
RestartSec=5
KillMode=process

[Install]
WantedBy=default.target
UNIT

systemctl --user daemon-reload
systemctl --user enable --now github-actions-runner-quantum-platform.service
systemctl --user --no-pager status github-actions-runner-quantum-platform.service
