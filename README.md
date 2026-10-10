# MapPOI

A live battle map for Rust servers running Oxide/uMod: it shows where fighting is happening.

When players fight each other, raid a base or take on a monument, MAPPoI marks the spot on everyone's
map with a coloured circle. The circle covers the area the fight spreads over and fades as the fight
cools down, so a busy battle stands out and a finished one disappears on its own. Explosions get their
own map icon, the biggest fights get a labelled ping (for example a gun icon marked "FIREFIGHT 6"), and
a small on-screen ticker lists the hottest battles with their grid square, how many players are
involved and how long until they fade.

Players need nothing installed: it all uses the game's own map markers.

## What counts as a fight

| Activity | Shown as |
|---|---|
| Players shooting or killing each other | PvP |
| Explosives used on players or on player-built bases | Raid |
| Players fighting NPCs inside a monument | Monuments |
| Players fighting NPCs elsewhere, or animals | NPC / animal |

By default only **PvP and raids** are tracked; admins can switch the others on with
`/mappoi config track`.

Each hit, kill and explosion adds "heat" to the nearest battle, and heat fades by half every 30 seconds.
Kills and explosives count for much more than single shots. By default a fight only appears on the
map 30 seconds after it starts, so the overlay never gives away a fight the moment it begins.

## Commands

**Players:**

| Command | What it does |
|---|---|
| `/mappoi` | Status and help |
| `/mappoi on` / `off` | Your pings and ticker on or off (the circles are shared by everyone) |
| `/mappoi show` / `hide` | The on-screen ticker on or off (remembered) |

**Admins** (`mappoi.admin`, or server admins): `/mappoi config …` changes what is tracked, what is
shown, the 30-second delay, how fast fights fade, circle size, the ticker's position and the centring
style, and `/mappoi list` lists the live battles. The same settings are in `oxide/config/MAPPoI.json`.

## Install

Copy `MAPPoI.cs` into the server's `oxide/plugins` folder. It loads within a few seconds and needs no
permissions for players.

## More

[README.MapPOI.md](README.MapPOI.md) is the full guide: how the heat and clustering work, every event
weight and setting, all admin and test commands, what was learned about Rust's map markers, and the open
items.
