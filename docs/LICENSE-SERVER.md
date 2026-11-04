# Sloomy Tweaks License Server — Deployment

## Requirements

- .NET 8 runtime (ASP.NET Core) on Linux or Windows.
- SQLite library: `libsqlite3` on Linux (`apt install libsqlite3-0`). On Windows the built-in `winsqlite3.dll` is used.
- TLS. Use Kestrel with a certificate, or a reverse proxy (nginx, Caddy, IIS) that terminates HTTPS. Outside Development the server enables HSTS and HTTPS redirection.

## Recommended: Docker + Caddy (automatic HTTPS)

`deploy/license-server/` contains a Dockerfile, a docker-compose file (license server + Caddy) and a Caddyfile. Caddy obtains and renews a Let's Encrypt certificate for your domain; the license server listens only on the internal network and trusts Caddy's forwarded headers (`Sloomy__TrustForwardedHeaders=true`) so rate limits apply per client.

```bash
# On a Linux VPS with Docker, with DNS for licenses.<your-domain> pointing at it
cd deploy/license-server
cp .env.example .env          # set LICENSE_DOMAIN, Sloomy__AdminKeySha256, Sloomy__KeyPepper
mkdir -p secrets
docker compose run --rm -v "$PWD/secrets:/out" license generate-signing-key /out/signing-key.pem /out/public-key.pem
docker compose run --rm license generate-admin-key       # store the key offline, put the hash in .env
docker compose run --rm license generate-pepper          # put the value in .env
docker compose up -d
curl https://licenses.<your-domain>/health
```

Copy `secrets/public-key.pem` (never `signing-key.pem`) into the client's `licensing.json`. `.env` and `secrets/` are git-ignored; the database lives in the `license-data` Docker volume (back it up).

> These deployment files were written but **not yet run** (no Docker in the build environment). Test them on the target host before going live.

## Manual setup

```bash
dotnet publish src/SloomyTweaks.LicenseServer -c Release -o /opt/sloomy-license
cd /opt/sloomy-license

# 1. Signing key pair (private key stays on the server; public key goes into the client's licensing.json)
dotnet SloomyTweaks.LicenseServer.dll generate-signing-key secrets/signing-key.pem public-key.pem

# 2. Admin key: give the key to administrators, configure only its hash
dotnet SloomyTweaks.LicenseServer.dll generate-admin-key

# 3. Pepper for license-key hashing
dotnet SloomyTweaks.LicenseServer.dll generate-pepper
```

Configure through environment variables (or a secret store), never committed files:

```bash
export Sloomy__SigningKeyPath=/opt/sloomy-license/secrets/signing-key.pem
export Sloomy__AdminKeySha256=<hash from step 2>
export Sloomy__KeyPepper=<value from step 3>
export Sloomy__DatabasePath=/var/lib/sloomy-license/licenses.db
dotnet SloomyTweaks.LicenseServer.dll --urls http://127.0.0.1:5077   # behind your HTTPS reverse proxy
```

The server refuses to start if any secret is missing or malformed.

## Operations

- **Admin dashboard:** `https://<host>/admin/`. Sign in with the admin key, then generate keys (plans: 7/30/90/365 days or lifetime; device limit; optional fixed expiry), manage customers, view activations, deactivate devices, revoke/reinstate, edit expiry and device limits, and read the audit log.
- **Plan duration** starts at first activation unless a fixed expiry is set.
- **Offline grace:** tokens last `TokenLifetimeHours` (default 72 h). Revocation reaches a client at its next refresh, which happens at most every 12 h while it is online. Tokens expired for more than `MaxRefreshAgeDays` (default 30) cannot be refreshed, and the user must re-enter the key.
- **Backups:** back up the SQLite database file (stop the service or use `sqlite3 licenses.db ".backup backup.db"`).
- **Key rotation:** add a new key id. Ship clients that trust both public keys, then switch `SigningKeyId`/`SigningKeyPath`.
- **Rate limits:** the client API defaults to 10 requests/minute per IP (`ActivationRequestsPerMinute`); the admin API to 120/minute. If you run behind a proxy, configure forwarded headers so limits apply per client.

## Privacy

Each activation stores a device id hash, the computer name the user's Windows reports, the app version and timestamps. No hardware serials, MAC addresses or IP addresses are stored in the database. Document this in your privacy policy, and keep customer email addresses only as long as your legal basis allows.

## Security notes

- The admin API is not exposed to the desktop client. The client contains no admin code or credentials.
- License keys are stored as HMAC-SHA256 with a server-side pepper. Plaintext keys are shown once at generation.
- For production, consider restricting `/admin` by IP or putting it behind SSO at the reverse proxy, in addition to the admin key.
