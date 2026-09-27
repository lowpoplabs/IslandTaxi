# Island Taxi

A full-fleet taxi service for Rust (Oxide/uMod) servers. Players dial **555-TAXI1
(55582941)** from any pay phone or mobile phone, pick a vehicle, drop a map pin where they
want to go, see the fare and ETA, and their ride arrives from over the horizon — flown,
sailed, driven, or ridden by a uniformed NPC chauffeur — picks them up, delivers them, and
departs into the distance.

<!-- lpl:links -->
**[Download v1.6.0](https://github.com/lowpoplabs/IslandTaxi/releases/latest)** · **Flyer:** [web](https://lowpoplabs.github.io/flyers/IslandTaxi.html) / [PDF](IslandTaxi-Flyer.pdf) · **[Changelog](CHANGELOG.md)** · **[Ko-fi](https://ko-fi.com/lowpoplabs)**
<!-- /lpl:links -->

## The fleet

| Vehicle | Terrain | Base fare | Per meter | Speed |
|---|---|---|---|---|
| Attack Heli (executive gunship) | air | 200 | 0.20 | 24 m/s |
| Armored Car (executive) | roads | 200 | 0.20 | 24 m/s |
| Scrap Heli (team ride) | air | 150 | 0.15 | 22 m/s |
| Minicopter (premium) | air | 100 | 0.10 | 20 m/s |
| Car (premium, radio cab) | roads | 100 | 0.10 | 20 m/s |
| Motorcycle (sidecar) | roads | 35 | 0.035 | 14 m/s |
| Boat (RHIB, dock service) | water | 30 | 0.03 | 13 m/s |
| Horse (double saddle) | roads & trails | 20 | 0.02 | 10 m/s |

Cars and motorcycles follow the actual road network (A* over roads and junctions; fares
price the true route) and drive around obstacles — even the junk cars that spawn on the
roadbed. Horses may also take trails. Boats route through open water around headlands and
beach right at the shoreline. All fares, speeds, and vehicles are config; the menu sorts
priciest-first and goes two-column for big fleets.

## Features

- **Call from any phone** — the taxi line is a real registered number that shows up as
  "Island Taxi" in every phone's directory. Dialing it opens the booking UI instead of a call.
- **Map-pin destinations** — after choosing a vehicle, the player drops a map marker; the
  plugin validates it (restricted zones, landable ground) and quotes a fare.
- **Fares** — base fare + per-meter rate, per vehicle. Pays through **Economics** or
  **ServerRewards** if installed, otherwise **scrap**. Refunds only happen if the taxi was
  never spawned; after that, no refunds — island policy.
- **Cobalt standing** — with Cobalt Papers Please loaded, fares follow the rider's band:
  Citizens ride 10% cheaper, the Wanted pay double, and an Enemy of the State is refused
  a car (multipliers per band in config; 0 = refuse). No effect without Papers Please.
- **Surge pricing** — a rolling one-hour demand meter scales fares in posted steps
  (defaults: 3+ rides/hour → +25%, 6+ → +50%, 10+ → +100%; tiers configurable). Only
  applies while enough players are online (default 2+), so a lone rider never surges
  himself. During surge the menu shows an orange notice and every price tag displays
  the surged rates — the multiplier locks when the menu opens, so the quote you
  confirm is the price you're charged.
- **Taxis that behave like taxis** — spawn ~400 m out and travel in, wait with the engine
  off (heli rotors wind down; boats bob at the shore; cars idle at the curb), board the
  passenger (walk up, or press E on a passenger seat), and depart properly after drop-off —
  flying off, reversing off the beach, or driving on down the road.
- **Boats are really driven** — on open water the RHIB runs on the game's own boat
  physics through the vanilla AI-driver seam: genuine throttle, rudder, wake, and
  engine sound, with obstacle avoidance that reads the water ahead (icebergs included)
  and a straight-astern back-out from every pickup. The proven waypoint autopilot
  handles docking and takes the wheel automatically anywhere the physics drive
  struggles.
- **Marked taxi zones** — named monuments are no-land territory unless the admin captures
  a landing pad or boat dock there (`/taxi padhere`, saved straight into config,
  monument-relative so one capture covers every instance and survives wipes). Pins near a
  zone snap to the exact point — this is how both Oil Rigs get service by air *and* sea.
- **Team rides** — on team-flagged vehicles (Scrap Heli by default), teammates standing
  near the caller are seated automatically at boarding, ride together, and are dropped
  off together.
- **The executive tier** — the Attack Heli flies with auto-deploying flares against
  homing missiles, an M249 in the gunner turret with company-supplied ammo, and HV
  rockets on the passenger's secondary fire. The Armored Car wraps driver and fare in
  armored modules, with the cab radio buried in the engine bay.
- **NPC drivers in uniform** — a disarmed, AI-disabled scientist chauffeur at the wheel of
  every taxi; the outfit is a config item list, with `shortname@skinid` support for
  accepted skins. Drivers cannot be dismounted, ejected, or talked out of the route.
- **Cab radio** — radio-equipped cars tune to a configurable station (default WEFUNK) and
  switch on when you board.
- **Night service** — headlights and running lights switch on after dark.
- **SAM clearance** — SAM sites ignore taxis (configurable), so aerial rides survive
  Launch Site flyovers and defended bases.
- **Loot-proof** — engine parts, fuel, and storage on an active taxi cannot be looted.
- **Pricing up front** — every vehicle button shows terrain, base fare + per-meter rate +
  speed; the confirm screen quotes distance, speed, estimated travel time, and exact fare.
- **Safe landings** — pickup and drop-off spots are ring-searched for gentle slope, dry
  ground, and clearance from construction and tool-cupboard range.
- **Clean aborts** — bail-outs, destroyed taxis, no-shows, cancels, deaths, and disconnects
  all land or clean up without stranding anyone at altitude or leaking vehicles. A taxi
  that never picks you up refunds the fare.
- **An unkillable phone line** — the taxi number re-registers itself within 15 seconds if
  anything wipes it (plugin reloads included).
- **In-game poster ads** — hang the included poster at admin-captured spots (bus stops
  and beyond) on paintable surfaces of your choice; posters are locked and pickup-proof.
- **Localization** — every player-facing message goes through Oxide's lang system.

## Installation

Drop `IslandTaxi.cs` into `oxide/plugins/`. The config generates at
`oxide/config/IslandTaxi.json` on first load.

The in-game posters need the poster image on the server: copy the release's
`data/IslandTaxi/poster.png` to `oxide/data/IslandTaxi/poster.png` (the `data`
folder in the zip drops straight into `oxide/`). Without it the plugin loads
fine but logs `Poster image missing ... no posters hung` and skips the ad
campaign. The image is already the small canvas's 320x240 cap, pre-rotated;
it ships ready to use.

## Configuration highlights

| Key | Default | Notes |
|---|---|---|
| Require permission (islandtaxi.use) to call a taxi | `false` | Open to everyone by default |
| Taxi phone number | `55582941` | 555-TAXI1; must be 8 digits (the dial pad requires it) |
| Payment → Provider | `Scrap` | `Economics`, `ServerRewards`, or `Scrap` (auto-falls back to scrap) |
| Vehicles | full fleet enabled | Per-vehicle type, prefab, fares, speed; menu order = config order |
| Road snap distance (m) | `100` | Max distance from player/pin to the road network |
| NPC driver in the driver seat | `true` | Plus a clothing list for the uniform (`shortname@skinid` supported) |
| SAM sites ignore taxis | `true` | Clearance codes for aerial rides |
| Landing pads / boat docks | `[]` | Capture with `/taxi padhere [heli\|boat] [filter]`; snap radius per pad |
| Cab radio station | `WEFUNK` | Station-list name or raw stream URL |
| Posters enabled + poster spots | `true` | Capture spots with `/taxi posterhere`; prefab configurable |
| Per-vehicle: Board the caller's whole team | Scrap Heli only | Team auto-boarding |
| Per-vehicle: Car modules in socket order | Armored Car | Build any modular-car taxi from config |
| Approach distance (m) | `400` | How far away the taxi spawns and travels in from |
| Max safe-spot search radius (m) | `60` | Ring-search radius around pins and pickups |
| Ride request timeout (s) | `120` | Booking steps and boarding wait |
| Despawn delay after drop-off (s) | `30` | The fly-away window before the empty taxi despawns |
| Cobalt standing → Fare multiplier by band | Citizen 0.9, Wanted 2, Enemy 0 | Needs Cobalt Papers Please; 0 refuses the ride |
| Restricted zones | `[]` | Monument name substrings or custom X/Z/radius circles |

## Permissions

- `islandtaxi.use` — can call a taxi (only enforced if the config flag is on)
- `islandtaxi.free` — rides at no charge
- `islandtaxi.admin` — admin commands (server admins qualify automatically)

## Commands

- `/taxi` — open the booking UI without a phone (admin/testing)
- `/taxi cancel` — cancel your booking or active ride
- `/taxi cancel <player>` — admin: cancel someone else's
- `/taxi status` — admin: phone/provider/booking/ride status
- `/taxi reload` — admin: reload the config
- `/taxi test <x> <z>` — admin: test the safe-spot finder at coordinates
- `/taxi water` — admin: water-pipeline diagnostics at your position
- `/taxi padhere [heli|boat] [filter]` — admin: capture a landing pad or boat dock where
  you stand (saved straight into config, live immediately)
- `/taxi posterhere [filter]` — admin: capture a poster spot on the wall you're facing
  (saved and re-hung island-wide)
- `/taxi postertry [n|find <text>]` — admin: audition paintable poster surfaces
- `/taxi monhere`, `/taxi monuments` — admin: monument-containment diagnostics
- `/taxi radiopos`, `/taxi spawncar` — admin: placement/testing diagnostics

## Roadmap

Ideas on the bench: grid-picker destinations, and physics-driven bikes (the current
road-follow is kinematic, which keeps rides unstickable but makes the motorcycle
driver's lean animation twitch — the boat's AI-driver seam adoption in 1.5.0 is the
template).

### Clothing skin creators

The company uniform is built from Steam Workshop clothing skins, shipped in the
default driver outfit (`shortname@skinid` entries in "NPC driver clothing" — swap
them there to re-dress the fleet). Shoutout to the creator whose work every
Island Taxi driver wears:

- **Dansky** — the
  [Palm Pattern](https://steamcommunity.com/sharedfiles/filedetails/?id=1382838119)
  collared shirt, the company's signature look.

## Compatibility

- Built against the September 3, 2026 Rust update (build 2633.288). Versions from 1.5.4 onward need that build or newer; the previous version is the last one that compiles on August builds.

## Support

Provided as-is. Bug reports welcome via GitHub Issues. No Discord, no custom work, no promises on turnaround. If it saved you time or you and your players enjoy it:

[![Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/lowpoplabs)

## License

[MIT](LICENSE).
