# AccessAPP Linux Startup Updater

This project provides a standalone updater executable for Cassia Linux hosts.

## Goal

Run the updater before `AccessAPP` starts:

1. Download update manifest from your Apache server.
2. Download the referenced zip.
3. Validate `sha256` and optional `sizeBytes`.
4. Extract to a staging folder.
5. Preserve device-specific files (default: `mqtt.json`).
6. Atomically replace `/home/cassia/FWUpgrade`.

## Build (publish for Linux ARM and Linux x64)

```bash
# linux-arm
dotnet publish extras/AccessAppUpdater/AccessAppUpdater.csproj \
  -c Release -r linux-arm --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish-updater/linux-arm

# linux-x64 (packaged/runtime-tagged as linux-64 in the AccessAPP update feed)
dotnet publish extras/AccessAppUpdater/AccessAppUpdater.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish-updater/linux-x64
```

## Config

Copy `extras/AccessAppUpdater/accessapp-updater.sample.json` to target, for example:

- `/etc/accessapp-updater.json`

Channel selection can be overridden at runtime by writing one of `stable`, `test`, `develop`
to `/etc/accessapp-updater.channel`. Use `{channel}` in `ManifestUrl` to resolve per-channel feeds.

## systemd integration (recommended)

Add this pre-start step to your `accessapp.service`:

```ini
[Service]
ExecStartPre=/usr/local/bin/AccessAppUpdater --config /etc/accessapp-updater.json
ExecStart=/home/cassia/FWUpgrade/AccessAPP
WorkingDirectory=/home/cassia/FWUpgrade
Restart=always
```

Then run:

```bash
sudo systemctl daemon-reload
sudo systemctl restart accessapp
```

## Manifest format

Hosted on Apache at e.g. `https://updates.example.com/accessapp/manifest.json`.

```json
{
  "app": "AccessAPP",
  "channel": "stable",
  "generatedAtUtc": "2026-02-09T00:00:00Z",
  "latest": {
    "version": "0.7.9",
    "url": "https://updates.example.com/accessapp/AccessAPP-0.7.9-linux-arm.zip",
    "sha256": "hex-sha256",
    "sizeBytes": 1234567,
    "publishedAtUtc": "2026-02-09T00:00:00Z"
  }
}
```

## Notes

- Updater expects the zip root to contain `AccessAPP` directly.
- `version.txt` is written into the install directory after a successful update.
- Run with `--dry-run` to validate connectivity/selection logic only.

## Publish Feed To Apache (Windows host)

Use:

`scripts/publish-accessapp-update-feed.ps1`

Defaults are already set to:

- Base URL: `http://prod.statistics.niko-test.nu/accessapp`
- Web root: `C:\Ampps\www\public\accessapp`

Example:

```powershell
.\scripts\publish-accessapp-update-feed.ps1 -Version 0.7.9
```


## First-run setup (interactive)

If `/etc/accessapp-updater.json` does not exist (or you pass `--setup`), the updater will:

- Ask for channel (stable/test/develop)
- Ask for system (linux-arm or linux-64)
- Optionally ask for a pinned 
- Write:
  - `/etc/accessapp-updater.json`
  - `/etc/accessapp-updater.channel`
  - `/etc/accessapp-updater.system`
- Install `/etc/init.d/AccessApp` and enable autostart using available init tooling:
  - `update-rc.d` (SysV)
  - `systemctl` (systemd)

Then you can start AccessAPP with:

```bash
/etc/init.d/AccessApp start
```

## Manifest schema

The update feed now supports multiple builds per version via:

- `latest.builds.{runtime}`
- `releases[].builds.{runtime}`

Legacy fields (`latest.url`, etc.) are still written and point to the linux-arm build for backwards compatibility.
