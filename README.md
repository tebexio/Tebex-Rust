![Logo](https://www.tebex.io/_nuxt/logo.BCN2mLkL.svg)
# Tebex Plugin for Rust Game Servers

Monetize your Rust server with the same tools used by FiveM and Hypixel. Sell items, subscriptions, and passes while Tebex handles payments, tax compliance, and risk all at a flat 5% fee.

This is our Oxide plugin designed specifically for Rust game server administrators.

## Features
- **Permission Replay**: Easily re-apply your players' permissions/groups after server wipes. All saved locally and fully customizable.
- **In-Game Storefront UI**: Players can browse an in-game representation of your store, if enabled.
- **QR Checkout**: Scan a QR code to checkout and pay with a mobile device instead of leaving the game.

## Installation and Setup
To install, simply upload the `TebexPlugin.cs` source file to the `oxide/plugins` directory of your game server.

You must [Create a Game Server](https://creator.tebex.io/) on your Tebex store to receive the key used to link your server.

Use the `tebex secret <key>` command as an administrator to set your game server key. Alternatively, it can be added to the config at `oxide/config/TebexPlugin.json`.

You may also use the `TEBEX_SECRET_KEY` environment variable which will override any configuration value.

## Configuration

The plugin is highly configurable to support a wide range of setups, both vanilla and modded. Below is the default configuration generated at startup:

```json
{
  "secret_key": "",
  "buy_command": "buy",
  "disable_ui": true,
  "admin_command": "tebex",
  "debug": false,
  "auto_report_logs": true,
  "enable_basket": true,
  "track_permissions": true,
  "custom_permission_commands": [],
  "queue_check_seconds": 120,
  "listing_refresh_seconds": 120,
  "telemetry_flush_seconds": 120,
  "join_flush_seconds": 60
}
```

| Key | Default | Description |
|---|---|---|
| `secret_key` | `""` | Your webstore's plugin secret key from the Tebex control panel |
| `buy_command` | `"buy"` | Chat command name that opens the store for players. |
| `admin_command` | `"tebex"` | Chat command name for all admin subcommands. |
| `disable_ui` | `true` | Toggle the full in-game storefront. Note: your server may be categorized as modded if UI is enabled. |
| `debug` | `false` | Enables verbose server-log output. |
| `auto_report_logs` | `true` | Whether the plugin sends telemetry/error events. |
| `enable_basket` | `true` | Enables selecting multiple items from the in-game storefront. |
| `track_permissions` | `true` | Enables the permission replay feature, which saves any permissions issued. |
| `custom_permission_commands` | `[]` | List of `{ label, grant_command, revoke_command }` deliverable commands so they're tracked and replayable too. |
| `queue_check_seconds` | `120` | Baseline interval (seconds) between polls of the Tebex command queue for due purchases. Minimum is 30s. |
| `listing_refresh_seconds` | `120` | How often package/category listings and community goals are re-fetched from Tebex. Minimum is 30s. |
| `telemetry_flush_seconds` | `120` | How often telemetry events are sent to Tebex. Minimum is 30s. |
| `join_flush_seconds` | `60` | How often player-join events are sent to Tebex. Minimum is 15s. |

## Commands

### User command

| Command | Access | Description |
|---|---|---|
| `/buy` | Everyone | Opens the in-game CUI store (browse categories/packages, basket, checkout, QR code). Falls back to printing the webstore link in console/chat if `disable_ui` is on. |

### Admin commands (`/tebex <subcommand>`, requires `tebex.admin` or server console)

| Section | Command | Description |
|---|---|---|
| Configuration | `/tebex secret <key>` | Set the webstore secret key |
| Configuration | `/tebex info` | Show server and account information |
| Configuration | `/tebex reload` | Reload configuration and listings |
| Queue & Delivery | `/tebex forcecheck` | Run a queue check right now |
| Queue & Delivery | `/tebex forcecheck <user>` | Check online commands for one cached user |
| Queue & Delivery | `/tebex replay <user\|all>` | Re-apply tracked permissions after a wipe |
| Store & Checkout | `/tebex checkout <pkgId>` | Get yourself an instant checkout link |
| Store & Checkout | `/tebex sendlink <pkg> <user>` | Send a checkout link to a player |
| Store & Checkout | `/tebex goals` | Show community goal progress |
| User Management | `/tebex ban <name> <reason> [ip]` | Ban a player from the webstore |
| User Management | `/tebex lookup <username>` | Not available in this build |
| Debug | `/tebex debug` | Opens the Plugin Health Check UI (or prints status from console) |
| Debug | `/tebex debug <true\|false>` | Toggle verbose server logging |
| Debug | `/tebex selftest` | Run the permission-ledger self-check |
| Debug | `/tebex help` | Opens the command reference |

**Notes:**
- `admin_command` and `buy_command` are configurable. `tebex`/`buy` are just the defaults.

## Permission Replay
Permission Replay lets you restore the permissions and groups your players **purchased** through your Tebex store after a server wipe, without asking buyers to re-purchase and without manually re-issuing commands.

When a Tebex package delivers an Oxide permission or group, the plugin records who got what. After a wipe (or any time Oxide permissions get reset), you can run `tebex replay <username|all>` to re-apply tracked purchases.

## Tracking custom (non-Oxide) permissions

If your perks are granted by another plugin instead of Oxide's built-in permissions, for example a VIP manager with `addvip` / `delvip`, teach the ledger about that plugin's grant/revoke commands with `custom_permission_commands` in the config:

```json
"custom_permission_commands": [
  {
    "label": "vip",
    "grant_command": "addvip",
    "revoke_command": "delvip"
  }
]
```

**Refunds/expiries** that come through as revoke commands automatically remove the entitlement, so a later `replay` won't re-grant something that was taken away.

All tracked permissions are saved to a ledger file in `oxide/data/Tebex/permission_ledger.json`. It is safe to back this up.

## Support
This repository is only used for bug reports via GitHub Issues. If you have found a bug, please [open an issue](https://github.com/tebexio/Tebex-Rust/issues).
