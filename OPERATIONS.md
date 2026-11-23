# FileHoster operations

This document covers production deployment and troubleshooting. Commands assume the production Compose file is stored in `~/filehoster` on the Linux host.

## Production configuration

Create a server-side `.env` file that is readable only by the deployment account:

```bash
POSTGRES_PASSWORD=<strong database password>
FILEHOSTER_SECRET_KEY=<long random HMAC key>
FILEHOSTER_PUBLIC_BASE_URL=http://<server>:5011
FILEHOSTER_BIND_ADDRESS=<trusted LAN interface address>
FILEHOSTER_PROXY_BIND_ADDRESS=<trusted LAN interface address>
FILEHOSTER_TRUSTED_API_NETWORKS=172.16.0.0/12,<uploader IP>/32
```

Protect it:

```bash
chmod 600 ~/filehoster/.env
```

The FileHoster and proxy secret values must match. Do not place production values in source control, image archives, shell history, or client applications.

**Rollout gate:** This release changes the upstream HMAC protocol and adds a database migration. Deploy the FileHoster and proxy images together; older proxies/upload scripts cannot authenticate to the new service. Update your uploader's `FILEHOSTER_SECRET_KEY` securely. The key must be unique and at least 32 characters. If `SampleApp` already exists, rotate its database `SecretKey` to match `.env` before restarting the services (back up the database first; use a privileged SQL session rather than typing the new key in shell history). Existing signed download URLs expire when the key is rotated. Confirm the new secret is not a known/example value.

**TLS is not yet configured here.** Binding to a specific LAN interface and allowing only the Docker network plus the uploader's source IP for `/api/` reduces exposure, but signed download URLs are still bearer credentials over plaintext HTTP. Keep both published ports behind a firewall/VPN and do not expose them to the public Internet. When a certificate and ingress are available, terminate HTTPS at that ingress, use HTTPS for `FILEHOSTER_PUBLIC_BASE_URL` and the client's proxy URL, and allow only ingress/uploader traffic to backend ports. Do not treat the current HTTP deployment as internet-safe. For a Docker bridge outside `172.16.0.0/12`, add its actual CIDR to `FILEHOSTER_TRUSTED_API_NETWORKS`; the service rejects unmatched administrative API calls.

## Build and transfer images

Run from the repository root on the build machine:

```powershell
docker build -f Dockerfile -t filehoster-filehoster:latest .
docker build -f FileHosterProxy\Dockerfile -t filehoster-proxy:latest .

docker save filehoster-filehoster:latest -o filehoster-image.tar
docker save filehoster-proxy:latest -o filehoster-proxy-image.tar

scp filehoster-image.tar <user>@<server>:~/filehoster/
scp filehoster-proxy-image.tar <user>@<server>:~/filehoster/
scp docker-compose-prod.yml <user>@<server>:~/filehoster/
```

Image archives are deployment artifacts. Delete them locally and remotely after a successful deployment.

## Deploy

On the Linux host:

```bash
cd ~/filehoster
docker load -i filehoster-image.tar
docker load -i filehoster-proxy-image.tar
docker compose -f docker-compose-prod.yml up -d
docker compose -f docker-compose-prod.yml ps
```

For later FileHoster-only updates that do **not** change the authentication protocol:

```bash
docker compose -f docker-compose-prod.yml up -d --no-deps filehoster
```

For later proxy-only updates that do **not** change the authentication protocol:

```bash
docker compose -f docker-compose-prod.yml up -d --no-deps filehoster-proxy
```

## Verify

```bash
FILEHOSTER_HOST="<trusted-lan-host>"
curl --fail "http://${FILEHOSTER_HOST}:5011/health"
curl --fail "http://${FILEHOSTER_HOST}:5001/health"
curl --fail "http://${FILEHOSTER_HOST}:5001/api/v1/filehoster/health"
curl --fail "http://${FILEHOSTER_HOST}:5001/api/v1/filehoster/files"
docker compose -f docker-compose-prod.yml ps
```

All three containers should become healthy:

- `filehoster-postgres-prod`
- `filehoster-prod`
- `filehoster-proxy-prod`

Verify an actual signed download without printing the token into shared logs. Request a link through the proxy, then perform a small ranged `GET` against the returned URL. A working endpoint returns `206 Partial Content`.

## Upload release packages

From the trusted Windows uploader (CMD, in the repository root), replace the example paths and hosts, then preview and upload only packages not already hosted:

```cmd
scripts\upload-missing-packages.cmd -SourcePath "\\server\share\packages" -ApiUrl "http://server:5011" -ProxyUrl "http://server:5001" -Preview
scripts\upload-missing-packages.cmd -SourcePath "\\server\share\packages" -ApiUrl "http://server:5011" -ProxyUrl "http://server:5001" -SshTarget "user@server"
```

The wrapper requires PowerShell 7 and the uploader's source IP in `FILEHOSTER_TRUSTED_API_NETWORKS`. When an upload is needed, `-SshTarget` must identify an account that can read `~/filehoster/.env`; the wrapper expects its `FILEHOSTER_SECRET_KEY` to be 64 lowercase hexadecimal characters. It reads the secret over SSH only when needed and does not save it to disk or display it. The proxy listing must be complete for duplicate detection; otherwise the script fails rather than uploading blindly. Do not paste `.env` or signed URLs into logs or chat.

For a different trusted source with its own securely configured key, use the generic script directly in PowerShell 7:

```powershell
.\scripts\upload-packages.ps1 `
  -SourcePath "\\server\share\path\to\packages" `
  -ApiUrl "http://<server>:5011" `
  -AppId "SampleApp"
```

The generic script uploads `.zip` and `.tar.gz` files with matching `.json` metadata and accepts `-FileNames` to select individual archives. Set `FILEHOSTER_SECRET_KEY` securely before calling it; it does not skip duplicates on its own.

After upload, verify the proxy list endpoint using the `FILEHOSTER_HOST` set above, and confirm the expected filename, size, and `contentHash`:

```bash
curl --fail "http://${FILEHOSTER_HOST}:5001/api/v1/filehoster/files"
```

## Logs and diagnostics

```bash
docker logs --tail 100 filehoster-prod
docker logs --tail 100 filehoster-proxy-prod
docker compose -f docker-compose-prod.yml ps
docker stats --no-stream
```

Inspect health-check history:

```bash
docker inspect --format='{{json .State.Health}}' filehoster-prod
docker inspect --format='{{json .State.Health}}' filehoster-proxy-prod
```

### Signed link returns 401

Check:

1. The link has not expired.
2. The proxy and FileHoster use the same application ID and secret.
3. The URL host is reachable by the client.
4. The deployed FileHoster includes Base64URL download-token support.

Do not log or share signed URLs. They are bearer credentials until expiration.

### Proxy is unhealthy but responds

Inspect the health history. The proxy image includes `curl`, and the Compose health check calls:

```bash
curl -f http://localhost:5001/health
```

If the executable is missing, rebuild the proxy image from the current Dockerfile.

### FileHoster is starting

The FileHoster health check has a 40-second start period. A connection reset immediately after replacement can be normal. Wait for `healthy`, then retry.

## Backup and recovery

Back up both:

- PostgreSQL data (`filehoster_postgres_data`)
- Hosted files (`filehoster_data`)

Metadata without package storage, or package storage without metadata, is not a complete backup. Test restores periodically.
On startup, FileHoster removes interrupted `.pending` uploads, restores or removes quarantined deletions according to the database, and removes orphaned files with generated IDs that have no database record. Restore the database and package volume together before starting the service after a disaster.

Before destructive maintenance:

```bash
docker compose -f docker-compose-prod.yml ps
docker volume ls
```

Never run `docker compose down -v` in production unless permanent data deletion is intended.

## Security checklist

- Keep `.env` mode `600`.
- Rotate shared secrets after suspected disclosure.
- Expose the proxy to clients; restrict direct FileHoster upload/API access to trusted networks.
- Put HTTPS in front of both public endpoints.
- Monitor failed authentication and disk usage.
- Retain and rotate application logs.
- Remove transferred image archives after deployment.
