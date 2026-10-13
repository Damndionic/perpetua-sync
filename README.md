# Perpetua Sync

The Windows app that keeps the **Perpetua** WoW addon and [perpetua.gg](https://perpetua.gg) in step, for the guild's
officers. It uploads what the addon gathered in game (guildmates' profiles, the guild roster, loot and rolls) and
brings the site's raid calendar and forums back into the addon.

## What it does, and doesn't do

- **Reads** the addon's saved data, `WTF\Account\<account>\SavedVariables\Perpetua.lua`, in your WoW folder. It reads it
  as data: it never runs it.
- **Writes one file**: `Interface\AddOns\Perpetua\SiteData.lua` (the calendar and forums for the addon), and only where
  the Perpetua addon is installed. Everything from the site is escaped before it goes in, because the game runs that file.
- **Talks only to perpetua.gg**, over HTTPS. The address is fixed in the code; there's no setting that points it
  anywhere else, and it refuses redirects.
- **Runs as you**, never as administrator. No installer, no services, no drivers.
- **Remembers** your WoW folder and its connection in `%APPDATA%\Perpetua Sync\settings.json`; the connection key is
  encrypted with Windows (DPAPI) so only your Windows account on that PC can use it. Its state and `error.log` are in
  `%LOCALAPPDATA%\Perpetua Sync\`.
- **Starts with Windows** in the system tray (turn it off in Settings), via your own user's Run key. Closing the window
  keeps it syncing in the tray; Exit (tray menu or Settings) stops it.
- No telemetry, no ads, no auto-update, no third-party packages: only .NET itself.

## How connecting works

1. The app makes a random key and sends perpetua.gg only its SHA-256, getting a short code back.
2. An officer, signed in with Discord, pastes that code at perpetua.gg/admin/sync, sees which PC asked and from where,
   and approves.
3. From then on the app uses the key. The site keeps only the hash, checks on every request that the approving officer
   still has the officer role on Discord, and any officer can revoke any app. **Disconnect** in the app revokes its own.

## Checking a download

Every release is built by GitHub Actions from this repository ([workflow](.github/workflows/build.yml)) and comes with
a signed build-provenance attestation:

```
gh attestation verify PerpetuaSync.exe --repo Damndionic/perpetua-sync
```

Or compare `Get-FileHash PerpetuaSync.exe` (PowerShell) with the SHA-256 on the release page.

The exe isn't code-signed yet, so Windows SmartScreen says "Windows protected your PC" the first time: **More info →
Run anyway**. Signing is planned.

## Building it yourself

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). Builds on Windows or Linux.

```
dotnet run --project tests/PerpetuaSync.Tests      # tests; ends "ALL PASS"
dotnet publish src/PerpetuaSync -c Release -o out  # out/PerpetuaSync.exe, .NET included
```

`PerpetuaSync.exe --dev` uses the test site (dev.perpetua.gg) with its own settings.

## Fonts

Cinzel and Cormorant Garamond, under the SIL Open Font License 1.1 (`src/PerpetuaSync/Assets/Fonts/OFL-*.txt`).
