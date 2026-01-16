# Standalone Scripts (Advanced)

These scripts provide an alternative to the self-contained plugin approach. Use them if you want mounts to be created **before** Jellyfin starts (container-level setup).

## When to Use These Scripts

- You want mounts ready immediately when Jellyfin starts (no 3-5 min delay on first boot)
- You prefer container-level configuration over plugin-based
- You're using a non-linuxserver Jellyfin image

## Installation

### 1. Configure Docker

Add to your `docker-compose.yml`:

```yaml
services:
  jellyfin:
    image: lscr.io/linuxserver/jellyfin:latest
    devices:
      - /dev/fuse:/dev/fuse
    cap_add:
      - SYS_ADMIN
    security_opt:
      - apparmor:unconfined
    volumes:
      - /path/to/jellyfin/config/custom-cont-init.d:/custom-cont-init.d:ro
```

### 2. Copy Scripts

```bash
mkdir -p /path/to/jellyfin/config/custom-cont-init.d
cp *.sh /path/to/jellyfin/config/custom-cont-init.d/
chmod +x /path/to/jellyfin/config/custom-cont-init.d/*.sh
```

### 3. Restart Container

```bash
docker-compose down && docker-compose up -d
```

## Scripts

| Script | Purpose |
|--------|---------|
| `install-rar2fs.sh` | Builds rar2fs from source (runs once, cached after) |
| `mount-rar-archives.sh` | Mounts RAR directories to UNRAR subfolders |
| `trigger-library-scan.sh` | Triggers library scan after Jellyfin starts |

## How They Work

1. Scripts run in alphabetical order during container startup
2. `install-rar2fs.sh` builds rar2fs if not already installed (~3-5 min first time)
3. `mount-rar-archives.sh` reads library paths from Jellyfin config and mounts archives
4. `trigger-library-scan.sh` waits for Jellyfin to be ready, then triggers a scan

## Comparison: Scripts vs Plugin

| Feature | Scripts | Plugin |
|---------|---------|--------|
| Mounts ready at Jellyfin start | Yes | After startup task runs |
| First boot delay | Before Jellyfin starts | During Jellyfin startup |
| Configuration UI | No | Yes |
| Requires volume mount | Yes | No |
| Auto-remount on restart | Yes | Yes |
