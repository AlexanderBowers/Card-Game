# Running the Critical Count server

The server is one small .NET program and one SQLite file. It costs **$0 to host**. The only paid
part is a domain name (about $10 a year), and you need one before production anyway because
Google Play wants `app-ads.txt` on your developer site.

```
 phone ──TLS (HTTPS / WSS)──▶ Cloudflare ──encrypted tunnel──▶ cloudflared ──localhost──▶ server
                                                               (on the Oracle VM, dials OUT)
```

- **Phones only ever talk to Cloudflare.** They never see the server's address or each other's.
- **The VM has no open web ports.** `cloudflared` connects outward to Cloudflare, and the server
  listens on `127.0.0.1` only. Nobody can reach it except through the tunnel.
- **TLS is Cloudflare's certificate.** There is nothing to renew.
- **No IPs are stored.** The server never writes an IP address anywhere. Cloudflare sees
  visitors' IPs as part of carrying the traffic, and the privacy policy says so.

---

## 0. Run it on your PC first

From the repo root:

```powershell
dotnet test server/CriticalCount.Server.Tests      # 64 tests
dotnet run --project server/CriticalCount.Server   # listens on http://127.0.0.1:5080
powershell -ExecutionPolicy Bypass -File server/smoke.ps1
```

`smoke.ps1` runs the whole thing end to end, then deletes the two accounts it made:

1. makes two accounts
2. names them
3. refuses a rude name
4. adds them as friends by code
5. searches
6. posts a leaderboard score
7. plays a full quick match over the socket

### Settings

Settings are environment variables, or `--Name=value` on the command line.

| Setting | Default | Meaning |
|---|---|---|
| `Db` | `<app folder>/data/criticalcount.db` | The SQLite file |
| `ASPNETCORE_URLS` | `http://127.0.0.1:5080` | Keep it on 127.0.0.1 |
| `OnlineSpecials` | `false` | `true` adds one random effect Modifier to each online hand, as local 2-player does with specials on |

---

## 1. Domain + Cloudflare (free plan)

1. Create a free Cloudflare account.
2. Buy a domain through Cloudflare Registrar, which sells at cost, or move an existing domain's
   nameservers to Cloudflare.
3. The game server will live on a subdomain, e.g. `play.cloudydaygames.com`. The bare domain
   can host the developer site and `app-ads.txt`.
4. Under **SSL/TLS → Edge Certificates**, turn on **Always Use HTTPS** and set **Minimum TLS
   Version** to **1.2**.
5. WebSockets are on by default; leave them on.

## 2. Oracle Cloud Always Free VM

1. Sign up at cloud.oracle.com. A card is needed for identity checks; Always Free resources are
   not charged.
   - Pick your **home region** carefully, because it can't be changed. A US region near you
     (e.g. Phoenix) is fine.
2. **Compute → Instances → Create instance.**
   - Image: **Ubuntu 24.04** (aarch64).
   - Shape: **VM.Standard.A1.Flex**, **1 OCPU / 6 GB**. That's plenty. The Always Free
     allowance is 2 OCPU / 12 GB total since June 2026.
   - Add your SSH public key.
   - If it says "out of capacity", try another availability domain or try again later. This is
     common for free ARM shapes.
3. **Networking:** in the instance's VCN security list, the only ingress rule should be SSH (22),
   ideally limited to your home IP. Do **not** open 80 or 443, because the tunnel doesn't need
   them.
4. Oracle may reclaim Always Free instances that sit idle for a week.
   - A game server with players is not idle.
   - To rule it out entirely, upgrade the account to **Pay As You Go**. Always Free resources
     stay free on it. Then set a **budget alert at $1** so you'd hear about any charge at once.

## 3. Install the server

On your PC, build a self-contained Linux ARM binary. The VM then needs no .NET installed.

```powershell
dotnet publish server/CriticalCount.Server -c Release -r linux-arm64 --self-contained -o build/server
scp -r build/server ubuntu@<vm-ip>:/tmp/criticalcount
```

On the VM:

```bash
sudo useradd --system --home /var/lib/criticalcount --create-home criticalcount
sudo mkdir -p /opt/criticalcount && sudo cp -r /tmp/criticalcount/* /opt/criticalcount/
sudo chmod +x /opt/criticalcount/CriticalCount.Server
sudo chown -R criticalcount: /var/lib/criticalcount

sudo tee /etc/systemd/system/criticalcount.service >/dev/null <<'EOF'
[Unit]
Description=Critical Count server
After=network-online.target

[Service]
User=criticalcount
WorkingDirectory=/opt/criticalcount
ExecStart=/opt/criticalcount/CriticalCount.Server
Environment=ASPNETCORE_URLS=http://127.0.0.1:5080
Environment=Db=/var/lib/criticalcount/criticalcount.db
Restart=always
RestartSec=3
NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths=/var/lib/criticalcount

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload && sudo systemctl enable --now criticalcount
curl -s http://127.0.0.1:5080/v1/health      # {"ok":true}
```

## 4. Cloudflare Tunnel

1. In the Cloudflare dashboard, go to **Zero Trust → Networks → Tunnels → Create a tunnel →
   Cloudflared**.
2. Name the tunnel `criticalcount`.
3. Pick **Debian / arm64** and run the install command it shows on the VM. That command installs
   `cloudflared` as a service with its token.
4. **Public hostname:** `play.<your-domain>` → Service **HTTP** → `127.0.0.1:5080`.

Then, from your PC:

```powershell
powershell -ExecutionPolicy Bypass -File server/smoke.ps1 -Base https://play.<your-domain>
```

## 5. Backups (optional, recommended)

The database is small. A nightly copy on the VM:

```bash
sudo apt-get install -y sqlite3
echo '15 3 * * * criticalcount sqlite3 /var/lib/criticalcount/criticalcount.db ".backup /var/lib/criticalcount/backup.db"' | sudo tee /etc/cron.d/criticalcount-backup
```

## 6. Updating

```powershell
dotnet publish server/CriticalCount.Server -c Release -r linux-arm64 --self-contained -o build/server
scp -r build/server/* ubuntu@<vm-ip>:/tmp/criticalcount/
ssh ubuntu@<vm-ip> "sudo cp -r /tmp/criticalcount/* /opt/criticalcount/ && sudo systemctl restart criticalcount"
```

### What a restart does

- Matches in progress are lost. Do updates when nobody is playing.
- Every phone signs in again on its own.
- Accounts, friends and the leaderboard are in the database and stay put.

### When the rules change

The server compiles the game's own rules files. So when the rules change, update the server in
the same release as the app.
