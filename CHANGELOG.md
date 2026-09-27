# Changelog

All notable changes to Island Taxi are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/); versions follow SemVer.

## [1.6.0] — 2026-09-27

### Added
- Fares follow the rider's standing with Cobalt when Cobalt Papers Please is loaded: Citizens ride 10% cheaper, the Wanted pay double, and an Enemy of the State is refused a car. Multipliers per band live in the new `Cobalt standing` config section (0 = refuse); the menu prices and the quote include them, and a line under the title says why. Without Papers Please nothing changes.

## [1.5.6] — 2026-09-12

First public release on GitHub — no gameplay changes.

### Added
- One-line load message naming the author and tip jar (`IslandTaxi v1.5.6 loaded - by LowPopLabs - ko-fi.com/lowpoplabs`).
- MIT license file and a README Support section with the Ko-fi button; support posture is as-is, issues welcome.

### Changed
- README skin credit names the creator and links the Workshop item, without a Steam profile link.

## [1.5.5] — 2026-09-04

### Fixed
- **Bus-stop posters floated in mid-air after the first wipe.** The shipped bus-stop
  spot had been captured 68m from the nearest bus stop, so it only looked right on
  the one wall of the original map; on every other bus stop (and every new map) the
  poster hung 27m up and 60m out. The spot is now the shelter's back wall, saved
  configs carrying the bad offset are corrected on load, and `/taxi posterhere`
  refuses a capture whose nearest decor prefab is more than 15m away.
- **Every monument poster hung twice** — each monument is both a registered
  monument and a map prefab, and both counted as anchors. One anchor per place now.

### Added
- **The poster image ships in the release** as `data/IslandTaxi/poster.png`,
  with the install step in the README. Earlier zips carried only the plugin, so
  a fresh install logged "Poster image missing" and hung nothing.
- **Poster placement audit** in the server log: every hung poster reports its
  anchor, world position, terrain and surface heights, and a verdict
  (`ok` / `FLOATING` / `UNDERGROUND`), so a mis-anchored spot is caught at load
  instead of by a player.

## [1.5.4] — 2026-09-03

### Fixed
- Compiles again on the September 3, 2026 Rust update (build 2633.288): the game removed `BaseEntity.SetFlag`, so entity flags are now set through the new flags-update scope with a network update, exactly what the old call did. No behaviour change. Five sites: vehicle headlights at night, the horse double saddle, and the poster photo and lock flags.

## [1.5.3] — 2026-08-31

### Added
- **The full ad campaign ships by default** — poster spots for six monument
  families out of the box, all captured on a live island: bus stops, Bandit Camp,
  Outpost, supermarkets, gas stations, and radtowns. One spot covers every
  instance of its monument, so a default install papers the whole map.
- **Skin attribution** — the README now credits Dansky, creator of the Palm
  Pattern collared shirt every company driver wears.

## [1.5.2] — 2026-08-30

### Fixed
- **Bail-outs no longer refund** — jumping out of a moving taxi told you
  "No refunds — island policy," then quietly refunded the fare anyway when the
  empty vehicle landed (one flag answered both "aboard right now?" and "ever
  rode?"). Island policy is now enforced as advertised, on every vehicle; rides
  that genuinely never pick you up still refund in full. A bailed passenger also
  no longer gets a stray "dispatch failed" message — or, on ground vehicles,
  teleported back to wherever the abandoned taxi ends up parking.

## [1.5.1] — 2026-08-30

The posters are up. A long-standing mystery — poster canvases rendering blank —
turned out to be the game client silently discarding sign images larger than each
sign type's own size cap.

### Fixed
- **Posters render** — the small artist canvas accepts at most 320×240 (landscape);
  the poster art now ships at exactly that size, rotated 90°, and the canvas hangs
  rolled -90° about its face axis (with pivot compensation) so the weathered
  portrait poster reads upright, edge to edge, no letterboxing.

### Added
- **Weathered poster art** — sun-faded, stained, island-worn; regenerated from a
  3:4 source composed for the canvas's exact ratio.
- **Repaint-proof ads** — a CanUpdateSign hook blocks sign edits on company
  posters for everyone, including admins (vanilla's admin bypass outranked the
  lock flag).
- **Palm Pattern driver uniform** — the company collared shirt now defaults to the
  "Palm Pattern" workshop skin (any public workshop skin renders fine; the earlier
  "skins don't draw" note was a client Steam setting, not the skin ids).

## [1.5.0] — 2026-08-30

Supply, meet demand: fares now surge when the island runs hot.

### Added
- **Surge pricing** — dispatched rides feed a rolling one-hour demand meter; clear
  a tier and every fare scales: 3+ rides/hour → +25%, 6+ → +50%, 10+ → +100%
  (tier table fully configurable). Active players act as a gate, not an input —
  by default surge only applies with 2+ players online, so a lone night rider
  never surges himself.
- **Posted, not hidden** — during surge the vehicle menu swaps its greeting for an
  orange "SURGE PRICING IN EFFECT — fares +X%" notice, and every price tag on the
  menu shows the surged flag fall and per-meter rate, not sticker prices. The
  multiplier is locked in the moment the menu opens: banner, quote, and charge
  always agree, and refunds return exactly what was charged.

## [1.4.1] — 2026-08-30

Boats get a real driver. Water taxis now cruise under their own engine power —
genuine throttle, rudder, wake, and engine sound — with the proven autopilot
standing by underneath.

### Added
- **Physics-driven boat cruising** — on open water the RHIB is driven through the
  game's own boat physics via the vanilla AI-driver seam: real steering and
  throttle, so the engine pitch, prop wash, wake, and hull lean are all genuine.
- **Obstacle avoidance at sea** — an 8-direction danger map plus a speed-ranged,
  hull-width bow lookahead steers the boat around icebergs and structures the
  water router can't see, shedding speed through the swerve.
- **Straight-back pickup exits** — every water pickup now ends with the boat
  backing straight out the way it came in before turning for the destination.

### Fixed
- **Boat engine silence** — the engine flag was being cleared every physics tick
  because the autopilot never sent driver heartbeats; boats now idle audibly at
  the dock and rev under way.

### Reliability
- The classic velocity autopilot remains the workhorse under the new drive: it
  threads the cluttered pickup exit corridor, handles docking and boarding holds,
  and the ride automatically hands itself back to it if the physics drive gets
  beaten (dense ice fields, wedged bows, thrash loops, or an engine that refuses
  to start). No failure path strands a fare.

## [1.4.0] — 2026-08-30

The executive expansion: three new top-shelf rides, marked taxi zones with Oil Rig
service by air and sea, and a long list of hard-won ride-quality fixes. (Absorbs the
unreleased 1.3.0.)

### Added
- **Scrap Heli — the team taxi** — premium heavy-lift option (150 scrap + 0.15/m):
  when the caller boards, teammates standing nearby are seated automatically (with a
  last-call sweep before liftoff), and everyone is dropped off together. Configurable
  per vehicle via "Board the caller's whole team".
- **Attack Heli — executive transport** — top-tier armored ride (200 scrap + 0.2/m,
  fastest in the fleet). You ride the gunner seat behind a company-installed M249
  with bottomless ammo, HV rockets ride your secondary fire, and the pilot pops real
  flares automatically the moment a homing launcher locks on.
- **Armored Car — executive ground transport** — armored cockpit and passenger cabin
  on a 3-module chassis (200 scrap + 0.2/m, faster than the standard Car), with the
  cab radio buried in the engine bay. Modular-car taxis are now module-configurable
  ("Car modules in socket order").
- **Company radio station** — cab radios tune to a configurable station (default
  WEFUNK; name from the game's station list or a raw stream URL).
- **Skinned driver uniforms** — clothing entries accept "shortname@skinid"; the
  default uniform upgrades the t-shirt to a company collared shirt.
- **Cost-sorted, two-column menu** — the vehicle list orders priciest-first and
  switches to two columns beyond six vehicles.
- **Boats wait 9 seconds after drop-off** before pulling away — time to chat up the
  driver and climb off at a dock ladder.
- **Designated landing pads and boat docks** — admins mark exact service points at
  monuments with `/taxi padhere [heli|boat] [filter]` (saved straight into the config,
  monument-relative so they survive wipes and cover every instance). A destination pin
  or pickup call within a pad's snap radius (default 50 m) uses the marked point
  exactly — this is how monuments earn heli service back, and boat docks open up
  Oil Rig by sea.

### Changed
- **Posters are now frameless artist canvases** — the bus-stop ads swap the wooden
  picture frame for the artist-DLC canvas, painted edge to edge (configurable via
  "Poster sign prefab"; `/taxi postertry` hangs live test posters with any paintable
  prefab on the server, `postertry find <text>` searches the prefab registry).
- **Posters can no longer be stolen** — hung posters are locked and hammer-pickup-proof
  (previously any player could pick all of them off the walls).
- **Flyer restyled** in the LowPopLabs municipal-department format (matching the
  Public Works flyer): Dept. of Island Transportation, real dispatch chat, fleet
  table, marked-taxi-zone routes, and BY THE NUMBERS.
- **Named monuments are now pad-only territory** — the heli no longer freelances
  landing spots inside any map-labeled monument (Launch Site, Airfield, ...). A pin or
  pickup call inside one gets a polite refusal (and no charge) until the monument has a
  designated landing pad (coming in this release). Roadside spots — bus stops included —
  are unaffected.

### Fixed
- **Ground taxis stop for (and drive around) obstacles** — kinematic driving plowed
  straight through anything parked on the road; taxis now sweep the lane ahead, pull
  around static blockers like drivable junk cars, and hold for a full blockade.
  Active taxis also no longer take bogus collision damage from overlap.
- **Drivers stay in the saddle** — ground taxis pitch with the road (a dead-level
  bike on a steep road clipped terrain and the game ejected the rider), on-duty NPC
  drivers refuse all dismounts, and an ejected driver is re-seated automatically.
- **Failed pickups refund** — a taxi that aborts before picking you up refunds the
  fare ("Dispatch failed on our end"), and no longer teleports you to wherever it
  stopped.
- **The taxi line is immortal** — two separate reload bugs killed the phone: a
  deregistration race (vanilla deregisters by number, blindly - fixed by zeroing the
  outgoing phone's number) and an unidentified process that destroys the fresh phone
  entity after fast reloads. A standing watchdog now verifies the phone every 15
  seconds and rebuilds it from scratch if anything kills it.
- **Boat engine noise** — the RHIB taxi ran silent (dry tank read as engine-off);
  it now idles, revs, and throws prop wash like a real boat.
- **No more rooftop landings, and no more clipping monument buildings** (found live at
  Launch Site's rocket assembly):
  - The landing-spot search judged the ground with a ray cast from only 50 m up — inside
    a tall building that ray starts under the roof and never sees it. It now casts from
    300 m, so the first surface in the whole descent column is what gets judged, and any
    surface more than 3 m above the terrain heightmap is rejected (monument ground slabs
    and roads still pass).
  - The heli's terrain-following cruise used the raw heightmap, which doesn't know
    buildings exist — it now follows the real surface (terrain, monuments, player
    construction) and clears rooftops at cruise altitude.
  - A heli obstructed while landing sat there forever with the engine running: stuck
    detection now also covers the descend/land phases, and an aborted landing sets down
    on whatever is actually beneath the heli instead of the heightmap ground that may be
    under a structure.
  - The landing-spot check was a thin ray that missed overhangs and roof edges just off
    its center — it is now a helicopter-sized sphere swept down the full descent column.
  - The empty heli's exit climb flew a straight line at fixed altitude (and could wedge
    on a building); it now flies the same building-aware climb as cruise, and a wedged
    departure despawns immediately instead of idling.
- **Config lists no longer duplicate on every load** — Json.NET was appending the file's
  entries onto the built-in defaults (driver clothing, poster spots, restricted zones) and
  saving the merged result back, growing the config each plugin load; poster spots had
  doubled, hanging two stacked posters per bus stop. Lists now replace defaults on load,
  and existing configs are deduplicated automatically on upgrade.

## [1.2.0] — 2026-08-29

The full fleet. First public release.

### Added
- **Ground fleet** — three new vehicles on the map's actual road network (A* routing over
  roads and junctions, arrival and departure along the road, fares priced on true route
  length):
  - **Car (premium)** — 2-module chassis with engine cockpit and the vanilla taxi cabin;
    ride in the back with a working dash radio (auto-on when you board).
  - **Motorcycle** — sidecar variant; you ride in the sidecar.
  - **Horse** — double saddle (driver up front, you behind), and horses may also take
    **trails**, not just roads.
- **Night service** — taxis with lights (car, motorcycle, boat) switch them on after dark
  and off at dawn.
- **Premium pricing tier** — Minicopter and Car: 100 base + 0.1/m at 20 m/s. Standard:
  Motorcycle 35 + 0.035 (14 m/s), Boat 30 + 0.03 (13 m/s), Horse 20 + 0.02 (10 m/s).
- **Menu polish** — terrain tag on every vehicle (air / roads / water / roads & trails),
  rows sized to fit any fleet, premium options on top.
- **NPC driver uniforms** — configurable clothing list (default: cap, sunglasses, tshirt,
  pants, boots).
- Taxis are **loot-proof** while active (engine parts, fuel, and storage are locked).
- Engine idle sound on cars and motorcycles; passengers are placed safely beside ground
  vehicles at drop-off.
- Dialing the taxi hangs up and closes the phone screen automatically; destination
  instructions spell out the put-phone-away step.
- Admin diagnostics: `/taxi water`, `/taxi radiopos`, `/taxi spawncar`.

### Fixed
- Water detection measures the true water column and clamps the surface to ocean level
  (the water map has no data over most of the open ocean).
- Boat routing runs shore → seaward staging → detoured deep-water legs → shore; boats
  beach at the shoreline (stuck-near-target counts as arrival) and reverse out before
  driving off.
- Dead-end drop-offs: ground taxis depart back the way they came.
- Per-vehicle dispatch/arrival wording (rotors / horizon / engine / hoofbeats).

## [1.1.0] — 2026-08-28

### Added
- **Boat taxi (RHIB)** — coastal pickups and drop-offs: the boat spawns out at sea, drives
  in via a seaward staging point, beaches at the shoreline, boards the passenger, runs a
  deep-water route (recursive detours around headlands and islands), noses up to the shore
  at the destination, then backs out, turns, and drives off. Fares and ETAs price the full
  route length.
- **NPC drivers** — a disarmed, AI-disabled scientist chauffeur in the driver seat of every
  taxi (peacekeeper for helicopters, RHIB crew for boats). Config: "NPC driver in the
  driver seat".
- **SAM clearance** — SAM sites (player-built and monument) ignore active taxis. Config:
  "SAM sites ignore taxis".
- **CUI upgrades** — vehicle buttons show base fare + per-meter rate + speed; the confirm
  screen adds speed and estimated travel time.
- Vehicle `Type` config field (Helicopter / Boat / Ground); pre-1.1 configs migrate
  automatically. `/taxi water` admin diagnostic for the water pipeline.

### Fixed
- Water detection measures the actual water column and clamps the surface to ocean level
  (the game's water map has no data over most of the open ocean).
- Boats treat grinding to a halt near the target as arrival (beaching), not a stuck-abort.

## [1.0.0] — 2026-08-27

Initial release — the helicopter MVP.

### Added
- Taxi phone line **555-TAXI1 (55582941)**: a hidden registered phone backs the number, so
  it appears in every phone directory as "Island Taxi"; dialing it opens the booking UI.
- Booking flow: vehicle select CUI (Minicopter; Boat/Car previewed as coming soon), map-pin
  destination capture with restricted-zone and safe-landing validation, fare quote and
  confirm screen.
- Payments through Economics or ServerRewards with scrap fallback; `islandtaxi.free`
  permission for free rides; refunds only before the vehicle spawns.
- The ride: Minicopter spawns ~400 m out and flies in, lands with rotors winding down,
  boards the passenger (proximity force-mount or press-E self-boarding), terrain-following
  cruise, landing at the pin, and a fly-away departure before despawning.
- Safe-spot finder: ring search rejecting steep slopes, water, construction, and
  tool-cupboard range; used for both pickup and drop-off.
- Abort handling: bail-out, taxi destroyed, no-show, cancel (self and admin), death,
  disconnect, and plugin unload all land in place or clean up; passengers are never
  dismounted at altitude.
- Admin tools: `/taxi`, `/taxi cancel [player]`, `/taxi status`, `/taxi reload`,
  `/taxi test <x> <z>`.
- Full localization via the Oxide lang system.
