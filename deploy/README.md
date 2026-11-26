# Deploy the room service on a VPS

The VPS only coordinates rooms: invitations, who is in each room, who holds the streaming role, and WebRTC signaling. Video, voice and controls go directly between participants. When a direct path fails, Cloudflare's TURN service relays it, so no media passes through the VPS and its bandwidth use stays small.

Without a registered domain, use [sslip.io](https://sslip.io): a name such as `203-0-113-10.sslip.io` resolves to the IP address it spells (`203.0.113.10`), so the server can still get a public certificate. The examples below use that placeholder; substitute your server's name.

## Layout

- **HAProxy** owns public TCP 443 (`haproxy.cfg`). It forwards TLS connections to Caddy with the PROXY protocol and everything else to sshd, because some networks allow SSH only on 443. Clients that wait for the server's SSH banner connect after a 5 second delay. Because sshd sees these connections from loopback, HAProxy limits SSH attempts to 10 per minute per client address; use key-only SSH authentication on such a server.
- **Caddy** (2.7 or later, for `proxy_protocol`) terminates HTTPS on `127.0.0.1:8443` (`Caddyfile`). It obtains certificates with the TLS-ALPN challenge through 443, so port 80 stays free for other services.
- **BlackPearl.Server** listens on `127.0.0.1:5080` (`blackpearl.service`). It trusts `X-Forwarded-For` only from loopback, so rate limits apply per real client.

## Requirements

- Ubuntu with `haproxy`, `aspnetcore-runtime-10.0`, and Caddy from the [official repository](https://caddyserver.com/docs/install#debian-ubuntu-raspbian). Ubuntu's own Caddy package is too old.
- Public TCP 443 reachable. Nothing else needs to be open.
- A Cloudflare Realtime TURN key: its key ID and API token. Without them the service still works, but only participants who can connect directly will succeed.

## Install or update

1. Publish on a development machine: `dotnet publish src/BlackPearl.Server -c Release -o artifacts/server-publish`. Copy the output to `/opt/blackpearl` on the VPS.
2. Install `blackpearl.service` to `/etc/systemd/system/`. Create `/etc/blackpearl/blackpearl.env` from `blackpearl.env.example` with mode 600, then `systemctl enable --now blackpearl`.
3. Install `Caddyfile` to `/etc/caddy/Caddyfile`. Set `Environment=BLACKPEARL_DOMAIN=203-0-113-10.sslip.io` (your server's name) in a `systemctl edit caddy` drop-in, then restart Caddy.
4. Install `haproxy.cfg` to `/etc/haproxy/haproxy.cfg`. Set `Environment=MULTIPLEX_BIND=:443` in a `systemctl edit haproxy` drop-in. Anything else listening on 443 must be stopped first. Keep a second SSH route open, or schedule an automatic rollback, before switching 443.
5. Verify that `https://203-0-113-10.sslip.io/health` returns `status: ok`, `relayConfigured: true` and `stunConfigured: true`, and that `ssh -p 443` still connects.

A server installed while the app was called Watchroom (`/opt/watchroom`, `watchroom.service`, `/etc/watchroom/watchroom.env`, `/var/lib/watchroom`) keeps working with its `WATCHROOM_*` settings, which the server still reads. To update it without moving anything, publish as in step 1 to `/opt/watchroom` and change `ExecStart` in `watchroom.service` to run `BlackPearl.Server.dll` instead of `Watchroom.Server.dll`. Keep the Caddy drop-in's `WATCHROOM_DOMAIN` unless you also install the new `Caddyfile`, which reads `BLACKPEARL_DOMAIN`.

To update by hand, repeat step 1 and run `systemctl restart blackpearl`, or let the server update itself as described next. Saved rooms, with their members and the people removed from them, are stored under `/var/lib/blackpearl` and survive a restart; everyone in a room is disconnected by it and their apps re-enter by themselves.

## Automatic updates

Every release from 0.3.159 on carries `BlackPearl-Server-VERSION-linux-x64.tar.gz`, the room service built by the release workflow, listed in the release's `SHA256SUMS.txt`. `blackpearl-update.sh` keeps the server on the latest one. `blackpearl-update.timer` runs it as root every 15 minutes, and each run does this:

1. It asks GitHub for the latest stable release and stops if that is not newer than the running build.
2. It downloads the room service build and checks it against GitHub's SHA-256 and the release's `SHA256SUMS.txt`.
3. It unpacks the build into `/opt/blackpearl-releases/VERSION`, owned by root and not writable by anyone else, and runs it once as `nobody` with `--describe`. That shows the build starts here and reports its version and room protocols.
4. It switches when nobody is in a room: the service writes `status.json` to its data folder every 10 seconds, with how many people are connected to rooms. Phone remote links don't count, since a PC with the remote turned on keeps one open. It switches right away when the new build speaks a room protocol the running one does not, so newer apps aren't turned away for long, and after `MAX_WAIT_HOURS` (24) in any case.
5. It points `/opt/blackpearl` at the new folder, restarts the service, and waits up to a minute for `/health` and for `status.json` to report the new version. If the build doesn't come up, it goes back to the previous folder, prints the failed build's log lines, and skips that version until a newer one is released.
6. It keeps the three newest releases.

To install it:

```sh
install -D -m 755 blackpearl-update.sh /usr/local/lib/blackpearl/blackpearl-update.sh
install -m 644 blackpearl-update.service blackpearl-update.timer /etc/systemd/system/
systemctl daemon-reload && systemctl enable --now blackpearl-update.timer
```

`/opt/blackpearl` may hold a build installed by hand. On its first switch, the updater moves that folder to `/opt/blackpearl-releases/manual-DATE` and keeps it to go back to. A server installed under other names, such as the Watchroom ones above, sets them in `/etc/blackpearl/update.conf`; `blackpearl-update.conf.example` lists every setting. `blackpearl-update.sh --check` reports what a run would do without changing anything. `journalctl -u blackpearl-update` shows what each run did. `--file ARCHIVE --version X.Y.Z` installs a local build in the same way.

The service speaks two room protocols, listed by `/health` as `protocols`. Protocol 2 is rooms without a host. Protocol 1 is what apps from before that speak: a host who admits each guest. Update the service before releasing an app on a newer protocol: an app says so when the room server is too old for it, while older apps keep working with a newer service. With automatic updates, a server switches to such a release within about 15 minutes of it being published. A room serves one protocol at a time, set by whoever is in it: someone on the other kind of app is told to update, or that the others need to. Its invitation, creator, name and people are the same on both, so a room carries over as its people update.

## Internet acceptance test

Use two Windows computers on different internet connections (for example home broadband and a mobile hotspot), not merely two processes on the same LAN.

1. Play a real MKV and MP4 for at least 20 minutes, checking pause, seek, subtitles, and reconnection.
2. Set `BLACKPEARL_FORCE_RELAY=true`, restart `blackpearl`, and repeat to exercise Cloudflare's relay. Restore `false` afterward.
3. Limit the streamer's upload and verify shared buffering and automatic resume.
4. As the room's creator, remove someone while streaming and confirm media access stops and they cannot re-enter. Test a wrong code.
5. Have a second person start a video while the first is playing one, and have the streamer leave: the stream should move, voice should not drop, and the room should stay open.

The current architecture sends one copy per viewer, even through TURN, so a relay does not reduce the streamer's upload requirements. Every member also holds a connection to every other member for voice. Cloudflare bills relayed traffic beyond its free allowance; monitor usage in the Cloudflare dashboard.

## Operations

- Cloudflare credentials are fetched at startup and refreshed in the background. Each credential handed to a participant stays valid for at least 12 hours. If Cloudflare is unreachable, participants receive STUN only and the service retries every minute.
- SSH through HAProxy reaches sshd from `127.0.0.1`, so sshd logs show loopback instead of the client address.
- Invitation codes are random 96-bit capabilities: anyone holding one can enter the room, and `POST /presence` tells them who is in it. The room's creator can remove a member, who is then turned away until let back in. A room holds eight people at once. Rooms the app creates are saved; unsaved rooms, which only tests create, expire after six hours.
- The server does not persist accounts, videos, chat or libraries. Keep `/etc/blackpearl/blackpearl.env` private.
