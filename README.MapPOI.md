# MAPPoI v2.2.0 — Live Battle Overlay

UPDATE: NEW


Shows where fighting is happening on the in-game map: decaying-heat circles, gun/hostile
pings with text labels, explosion icons, and a HUD ticker.

Replaces the v1.2.0 per-player decoy circle, which is still available behind the default-off
config flag `Enable legacy v1 decoy circles`.

**Status:** working and verified live on the server. Outstanding items are listed at the end.


---

## 1. Why the original spec changed

The first draft specified circles sized by participant count, coloured orange→red on
explosives, a TTL of "10s per bullet, +30s per explosive, capped at 120s/240s", and centres
fuzzed to 80% of a grid square. Three problems forced a redesign:

1. **The TTL model saturated.** One AK magazine is 30 rounds → 300s of TTL, clamped to the
   120s cap. Every real fight sat pinned at maximum, so the timer carried no information.
2. **Grid-cell bucketing split battles.** A fight straddling a cell boundary rendered as two
   to four separate circles.
3. **Fuzzed centre + grid-sized radius stacked two errors**, and re-rolling the offset each
   update made the circle visibly wobble.

What shipped: an **exponentially-decaying heat model** with **incremental clustering**, where
radius encodes real spatial spread and alpha encodes live intensity. All three original
behaviours remain selectable via `poistyle`, so nothing was removed.

---

## 2. The heat model

Each battle keeps decayed accumulators, not an event list — O(1) memory however long a fight
runs:

```
SumW    total decayed weight ("heat")     ExpW     weight from explosives only
SumWP   Σ w·p                             CatW[6]  weight per category
SumWP2  Σ w·|p|²                          Actors   decayed weight per participant
```

Every second all accumulators are multiplied by `pow(0.5, dt / halfLife)` (default 30s).
Numerator and denominator decay identically, so:

```
centre = SumWP / SumW
spread = sqrt(SumWP2 / SumW − |centre|²)     exact exponentially-weighted std-dev, metres
```

Positions are flattened to `y = 0` on ingest so terrain height does not pollute the spread.

Lifetime is **derived, not accumulated**: a battle dies when `SumW` falls below
`DeathThreshold`. The displayed countdown is `halfLife · log₂(SumW / MinPublishHeat)` — time
until the circle *stops rendering*, which is what a viewer cares about. (Counting down to the
death threshold instead was misleading: circles vanished at ~117s while the UI claimed 250s.)

> ### Invariant: `DeathThreshold` must stay below the smallest event weight
> This is a correctness condition, not a preference. If the threshold is at or above the
> smallest weight, a battle seeded by one low-weight hit is *already* below the death
> threshold and is culled on the very next tick — so it can never accumulate a second hit and
> that entire category becomes permanently invisible. This shipped broken (threshold 1.0 vs
> monument weight 0.5) and silently killed every monument/NPC/animal fight while PvP worked.
> `ValidateConfig` now derives the ceiling as `0.5 × min(weight)` so it self-heals for any
> weight config, and new battles get a `GraceSeconds` (15s) reprieve from expiry so a
> slow-burn fight has time to build.

### Event weights

| Event | Weight | Category |
|---|---|---|
| Player hit on player | 1.0 (+0.5 if damage > 50) | pvp |
| Player kill | 10 | pvp |
| Explosive damage to player | 6 | pvp + explosive |
| Explosive damage to player-owned structure | 4 | raid + explosive |
| Player ↔ NPC | 0.5 | monuments (in bounds) or npc |
| Combat involving cargo / heli / Bradley / CH47 | 3 | events |
| NPC ↔ NPC | 0.25 | npc |
| Player ↔ animal | 0.25 | animal |

Player-vs-NPC uses the **monument weight regardless of monument bounds** whenever a real
player is involved — `MonumentInfo.IsInBounds` returns false at Large Oil Rig, and at the
NPC-vs-NPC weight an oil rig fight would need ~12 hits before it could ever appear.

### Clustering

Each event joins the nearest live battle within that battle's **join radius**, else starts a
new one:

```
joinRadius(b) = min(MergeRadius + b.spread, MaxJoinRadius)    default 120m, capped at 300m
```

The join radius **grows with the battle's own spread**, and this matters: with a fixed radius
a battle's spread can never exceed ~60m, so `radius = spread × 2` pins at the floor and every
circle renders identically — defeating poistyle 1 entirely. With spread-scaling, a raid with
attackers on three sides stays *one* battle drawing one large circle, while a duel keeps a
tight catchment. Measured: 15m-ring duel → 117m circle, 100m-ring raid → 200m circle.

Battles whose centres converge are merged once per tick. Merging is exact — the accumulators
are decayed weighted sums, so adding them pairwise gives the same statistics as if both
streams had been one battle from the start.

### Anti-abuse gates

Without these anyone can paint a decoy circle to bait players or mask a real raid.

- **Attacker must be a connected real client** (`net.connection != null`, Steam-ID account).
  Victims additionally count while sleeping, so offline raid kills register.
- Self-damage and same-team damage ignored.
- **Pellet coalescing** — same attacker↔victim pair inside 250ms collapses to one event, or a
  shotgun would count 12× an AK round.
- **Pair rate cap** — max 6 counted events per pair per 10s.
- **Publication gate** — ≥2 distinct participants *and* heat ≥3 before anything is drawn.

---

## 3. Classifying NPCs — do not assume `BasePlayer`

This build runs **two NPC generations side by side** (91 `ScientistNPC2` and 91
`ScientistNPC` were alive simultaneously during testing):

```
ScientistNPC2           <- BaseNPC2      <- BaseCombatEntity     new stack
Wolf2/Crocodile/Panther <- BaseNPC2      <- BaseCombatEntity     new-gen animals
ScientistNPC <- HumanNPC <- NPCPlayer    <- BasePlayer           old stack
Boar/Bear/Chicken/Stag  <- BaseAnimalNPC <- BaseNpc              old-gen animals
```

A `BasePlayer`-only test — the obvious implementation — drops **every** new-generation NPC.
That shipped broken and made oil rig fights register nothing at all while PvP worked fine.
Worse, **`BaseNPC2` is `internal`**, so `e is BaseNPC2` does not even compile from a plugin.

Classification therefore walks `GetType().BaseType` matching on type *name*, cached per
`System.Type` so the hot damage hook pays for it once per class. New-generation animals need
their own name list or they read as NPC combat and the `animal` category cannot switch them
off.

`mappoi.selftest` prints the live classification of every NPC-like entity on the server — run
it after any Rust update to check nothing has moved to a third hierarchy.

---

## 4. Presentation

### `poistyle` — three selectable centring functions

Grid cell is 146.3m. Final radius is multiplied by `circlescale` (default **3×**) because the
client draws these markers far smaller than the world-size normalisation implies.

| Style | Centre | Radius |
|---|---|---|
| **1** (default) | true weighted centroid, unfuzzed | `spread × 2`, floored 0.8 grid, capped 1.5 grid |
| **2** | centroid + random offset ≤0.8 grid, **re-rolled** each recentre | participants, 0.3→1.0 grid |
| **3** | same offset, **fixed** for the battle's lifetime | as style 2 |

Style 1 is honest: the radius *is* the uncertainty, so it is floored — you can never pinpoint
anyone, but a duel still reads differently from a sprawling raid. Styles 2 and 3 take their
imprecision from the offset, so their radius is free to use its full range; applying style 1's
floor to them as well squashed that range flat until it was fixed.

### Colour and alpha

- **Hue by dominant category**: pvp orange, raid hot-orange, monuments cyan, events magenta,
  npc grey-green, animal green.
- pvp and raid lerp **continuously** toward red by explosive fraction (`ExpW / SumW`),
  replacing the original binary orange/red flip.
- **Fill alpha tracks live heat**, clamped to [`AlphaMin`, `AlphaMax`]. A dying battle fades
  out rather than snapping off.

### Display elements

**`pings` — the best channel available, and how text gets onto the map.**

`BasePlayer.AddPingAtLocation(PingType, pos, seconds, id)` is **public**, auto-expires, and
writes to `State.pings` — a budget entirely separate from the player's map notes
(`server.maximumPings` vs `server.maximumMapMarkers`, both 5 by default) — so it costs players
nothing. Icons: Hostile, GoTo, Dollar, Loot, Node, **Gun**, Build.

It is **per-player**, so unlike the circles it honours `/mappoi off`.

Vanilla never labels a ping — `ApplyPingStyle` sets only colour and icon — **but the client
renders `MapNote.label` on one anyway** (confirmed in game). After `AddPingAtLocation`, patch
`State.pings[last].label`, then `DirtyPlayerState()` + `SendPingsToClient()`. That path also
skips `ValidateMapNote`, so the 10-character uppercase cap on map notes does not apply.
Result: a gun icon with `FIREFIGHT 6` beside it, no shop marker, no slot cost.

Gotchas: `SendPingsToClient()` is listed private in the decompiled dump but is **public** at
runtime (look it up with `Public | NonPublic` or `GetMethod` returns null);
`AddPingAtLocation` silently skips a request within 0.25m of an existing ping of the same
icon, so remove-then-add rather than relying on expiry; pings render **in the world view as
well as the map**; capped at 3 so players keep slots for their own pings.

**`circles`** — `MapMarkerGenericRadius`, one per battle, colour-coded and fading with heat.

**`explosions`** — vanilla `MapMarkerExplosion` at C4/rocket/satchel detonations.

**`hud`** — CUI ticker listing the hottest battles (up to 3) with category colour, grid
reference, headcount and countdown. The configured rect describes **one row**; extra rows grow
*downward* from a fixed top edge so a busy server never pushes the panel back into the
compass. Move it with `/mappoi config hudpos <y>`; invalid anchors revert to defaults with a
warning rather than silently rendering nothing. Computed anchors use `InvariantCulture` — a
comma decimal separator from a non-English locale would produce an unusable rect.

**`labels`** — *superseded by `pings`, off by default.* `VendingMachineMapMarker.markerShopName`
is the only text field on any server-spawnable **world** marker, but it drags its **shop icon**
along, and the marker carries a `clusterMarkerObj` so battle labels can cluster with genuine
vending machines and skew the shop counts players see. There is no way to suppress the icon
from the server. Every other marker class either has no string field or is a client-side
`MonoBehaviour` (`PointOfInterestMapMarker` and `MapMarkerIconSelector` are the player's own
map-note UI, not server-controllable).

---

## 5. Rust API facts this cost us

> **Set `color1`/`color2`/`alpha`/`radius` BEFORE `Spawn()`.** Spawning a marker with
> `radius = 0` and pushing values afterwards through `SendUpdate()` leaves an **invisible**
> circle — the entity exists server-side, `mappoi.list` reports it, the RPC is sent, and
> nothing renders. This was the actual cause of "no circles on the map". v1.2.0 got it right
> and the v2 rewrite regressed it. `mappoi.testcircle` reproduces the v1 recipe verbatim to
> isolate transport problems from render-path problems.

- **`marker.radius` is a fraction of world size**, not metres. Below ~20m is sub-pixel.
- **Map-wide visibility** needs `globalBroadcast = true` *before* `Spawn()`, then
  `UpdateNetworkGroup()` after. The `EnableGlobalBroadcast()` helper early-returns when the
  flag already matches, skipping the group switch.
- **Per-player markers cannot be world-anchored.** There is no `CanNetworkTo` hook on this
  build, and `ShouldNetworkTo` requires `parentEntity == player` for `limitNetworking` — so a
  one-player marker must be parented to them. A parented marker then inherits the parent's
  **rotation**, and the server's player transform has `bodyYaw = 0` while the client's does
  not. Measured: a circle intended for F6 rendered 1331m from the player at ~75° off (intended
  1368m), i.e. the marker **orbits the player as they turn the mouse**. Compensating would
  mean re-sending position at mouse-movement rates, per battle, per viewer. `global` is
  therefore the only supported mode; `perplayer` is retained only for experimentation and
  warns when selected. An earlier `auto` mode silently switched to per-player as soon as
  anyone opted out — that was actively harmful and is gone.
- **Native clans have no map-marker channel.** ClanManager/ClanTable exist; there are zero
  clan note/marker/ping symbols. `PlayerTeam.leaderMapNotes` (the leader's notes broadcast to
  members without using member slots) is the only team-scoped map-note channel.
- **The `RustAPI/` decompiled dump is stale in places** — it lacks `ScientistNPC2` entirely and
  reports `SendPingsToClient` as private when it is public. Treat it as a hint, verify at
  runtime.
- **Mono dictionary enumeration is stricter than .NET Core**: assigning to an already-existing
  key inside `foreach` throws. Snapshot `.Keys` first.
- **Non-BMP emoji do not survive to the client** — `\U0001F4A5` renders as literal `F4A5`. BMP
  glyphs (⚔ ⚑ ☠ ✈ ⚙ ✦) work.
- **`TerrainPath.Monuments` is `internal`** — cache
  `UnityEngine.Object.FindObjectsOfType<MonumentInfo>()` at `OnServerInitialized` instead.
- **Grid references are correct**, verified against player-placed map notes to within 1–3m.
  Offshore positions beyond the terrain square produce cells past the labelled range (a player
  at x=1213 on an 1800m map reads as "O8" when the grid stops at L11). Left unclamped
  deliberately — markers *are* visible out there, so the true computed cell describes the
  position better than pinning it to the nearest labelled one.

---

## 6. Commands

Permission `mappoi.admin` (or admin flag) gates everything except the four player commands.
All 11 console commands are permission-gated; RCON/server console passes.

**Players:**

```
/mappoi                 status + help
/mappoi on | off        battle overlay on/off  (hides HUD and pings; circles are server-wide)
/mappoi show | hide     HUD ticker             (persisted)
```

**Admin — chat or RCON:**

```
/mappoi config                                    dump settings
/mappoi config poistyle 1|2|3
/mappoi config visibility off|live|<nn>s          default 30s
/mappoi config track pvp,raid,monuments,events,npc,animal | none
/mappoi config display circles,explosions,hud,pings,labels | none
/mappoi config markermode global|perplayer
/mappoi config halflife <seconds>
/mappoi config circlescale <n>                    default 3
/mappoi config hudpos <y> | <xmin> <ymin> <xmax> <ymax>
/mappoi config reset
/mappoi list

mappoi.config / mappoi.list                       RCON equivalents
mappoi.debug            hook counts + per-reason reject tally  ← FIRST STOP when nothing shows
mappoi.resetcounters
mappoi.selftest         live NPC classification
mappoi.simulate <x> <z> [heat] [cat] [expFrac] [actors] [spread]
mappoi.atme [radius]    circle at your exact position (ground-truth reference)
mappoi.mynotes          reads your map notes and measures them against live circles
mappoi.testcircle       v1.2.0 marker recipe verbatim (transport check)
mappoi.testperplayer <grid>   parented far/near markers
mappoi.calibrate <grid>       A/B parented vs global placement
mappoi.pinglabel <text>       labelled ping probe
mappoi.pingaged [0-0.99] [type]  ping-sound probe: ping arrives that fraction through its life
mappoi.pingnote [text] [0|1]     ping-sound probe: isPing=false (+noteType) via the ping channel
mappoi.pingtypes [interval] [m]  one ping of every PingType (7), spaced so each sound is distinct
mappoi.pingclear                 wipe the target's State.pings (own pings too) and re-push
/pingtest aged|types|type|note|clear   chat front-end for the same probes (self only)
```

`/mappoi config pingage <0-0.9>` sets how far through their life battle pings arrive (default
0.1); `0` restores vanilla fresh pings, with sound.

`mappoi.simulate` injects synthetic heat so styles, colours and labels can be tuned without
staging a fight; it bypasses the visibility delay deliberately. `mappoi.mynotes` is the
precision tool — drop a note where something *looks* like it is, and it reports the exact
dx/dz against where the plugin put it.

Files: `oxide/config/MAPPoI.json` (a v1 config is detected and regenerated with a warning),
`oxide/data/MapPOI.json` (per-player opt-out and HUD preference).

---

## 7. Verified live

- Compiles and loads; v1 config migrated.
- Circles render at true world positions — **measured to ±3m** against player-placed map notes
  at three separate cells.
- Grid references correct in both directions (world→grid and grid→world).
- Clustering keeps separate fights separate and a dispersed raid as one battle; radius scales
  with spread (117m duel vs 200m raid) and with participants in styles 2/3 (64m vs 146m).
- Style 2 re-rolls its offset between recentres; style 3 held the same fuzzed centre across
  four recentre intervals while the true centre was unchanged.
- Decay lifecycle: heat 22.5 → 7.4 → 2.4 → expired, alpha fading 0.49 → 0.18, markers
  reclaimed at the publication threshold.
- Teardown: server marker count 20 → 24 with two battles → 20 after unload/reload. No orphans.
- Damage hooks fire on real gameplay; `ScientistNPC2` classifies correctly after the fix.
- Pings render with **gun icon + text label** on the map and in the world view.

**Not yet verified:** a live two-player PvP fight end to end, and explosion icons at a real
detonation.

---

## 8. Known limitations and open items

- **`/mappoi off` cannot hide the circles.** Map markers are server-wide on this build and the
  only per-player mechanism is the parenting trick that cannot place circles across the map.
  Opt-out does hide the HUD and the pings, which are per-player.
- **`perplayer` marker mode is dead weight** — proven unusable, kept only for experimentation.
  Removing it and `PerPlayerTolerance` would drop ~100 lines that can only produce wrong
  results.
- **`circlescale 3` may be too large** — a 351m radius spans ~4.8 grid cells, which makes it
  hard to judge which cell a fight is in. Worth comparing against 1.5–2.
- Pings appear in the **world view** as well as the map. No server-side switch to make them
  map-only — the wire format (`ProtoBuf.MapNote`: `noteType, worldPosition, icon, colourIndex,
  label, isPing, timeRemaining, totalDuration, associatedId`) has no HUD/world/sound flag, and
  the client has no ping convar (only the `+ping` bind and `graphics.compass`).

### Ping sound — solved (2026-09-01, v2.2.0)

The sound + compass flash is a *client* reaction to a ping arriving with a **full timer**
(`timeRemaining == totalDuration`). Verified live with the probes above: a ping sent with
`timeRemaining` at 90% or 50% of `totalDuration` renders normally and is **silent**;
`isPing=false` through the ping channel still sounds; removal never sounds; all 7 `PingType`s
(Hostile, GoTo, Dollar, Loot, Node, Gun, Build — `LAST=6`) sound when fresh.

v2.1 `RefreshAllPings` removed and re-added every ping each cycle through the vanilla helpers, so
each cycle manufactured a fresh ping — a sound every 10 s per battle — and every helper call ran
`TeamUpdate`, pushing our pings to all teammates. v2.2 hand-builds the `MapNote`s already
`PingAgeFraction` (0.1) through their life, swaps ours in `State.pings` by reference and pushes
**one** `Client_ReceivePings` per player. Vanilla `AddPingAtLocation` must not be used on this
path: it sends its own fresh RPC before anything can be patched. Teammates now only see our
pings when a natural `TeamUpdate` happens to carry `State.pings`. This Oxide build has no
`OnPlayerPingsSend` hook (only `OnPlayerPinged`); none is needed since the plugin owns
`State.pings`.
- The demo half-life of 240s must be returned to 30s (`/mappoi config halflife 30`).
