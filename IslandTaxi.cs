using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Island Taxi", "LowPopLabs", "1.6.0")]
    [Description("Dial a taxi from any phone, pick a vehicle and destination, get delivered.")]
    public class IslandTaxi : RustPlugin
    {
        // Rust 2633.288 (2026-09-03) removed BaseEntity.SetFlag; flags now change inside a
        // FlagsUpdateScope. SendNetworkUpdate mode matches what the old call did.
        private static void SetFlagNet(BaseEntity entity, BaseEntity.Flags flag, bool value)
        {
            using (var scope = entity.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
            {
                scope.Set(flag, value);
            }
        }
        private const string PermUse = "islandtaxi.use";
        private const string PermFree = "islandtaxi.free";
        private const string PermAdmin = "islandtaxi.admin";

        private const string TelephonePrefab = "assets/prefabs/voiceaudio/telephone/telephone.deployed.prefab";
        private const string MinicopterPrefab = "assets/content/vehicles/minicopter/minicopter.entity.prefab";
        private const string RhibPrefab = "assets/content/vehicles/boats/rhib/rhib.prefab";
        private const string HeliDriverPrefab = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_peacekeeper.prefab";
        private const string BoatDriverPrefab = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_rhib.prefab";

        private const string ScrapHeliPrefab = "assets/content/vehicles/scrap heli carrier/scraptransporthelicopter.prefab";
        private const string AttackHeliPrefab = "assets/content/vehicles/attackhelicopter/attackhelicopter.entity.prefab";
        private const string CarChassisPrefab = "assets/content/vehicles/modularcar/car_chassis_2module.entity.prefab";
        private const string Car3ChassisPrefab = "assets/content/vehicles/modularcar/car_chassis_3module.entity.prefab";
        private const string MotorbikeSidecarPrefab = "assets/content/vehicles/bikes/motorbike_sidecar.prefab";
        private const string HorsePrefab = "assets/content/vehicles/horse/ridablehorse.prefab";
        // Artist canvas (owner pick 2026-08-29): frameless, art edge to edge.
        // SMALL canvas (owner pick 2026-08-30 after seeing the large in place:
        // "way too big"). Its image cap is 320x240 LANDSCAPE - client-side rule
        // found closing Task 8.5: every sign type validates downloaded images
        // against its own overrideMaxImageWidth/Height and silently discards
        // oversized ones, so the poster PNG must fit within the canvas's cap.
        private const string PosterSignPrefab = "assets/prefabs/misc/artist_dlc/artistscanvases/sign.artistcanvas.s.prefab";
        private const string LegacyPosterPrefab = "assets/prefabs/deployable/signs/sign.pictureframe.portrait.prefab";

        private const string TypeHelicopter = "Helicopter";
        private const string TypeBoat = "Boat";
        private const string TypeGround = "Ground";
        private const string TypeHorse = "Horse";
        private const float MinChannelDepth = 0.8f; // RHIB draft with margin

        private const string UiMain = "islandtaxi.main";
        private const string UiBanner = "islandtaxi.banner";
        private const string UiConfirm = "islandtaxi.confirm";

        [PluginReference] private Plugin Economics, ServerRewards, PapersPlease;

        private Configuration _config;
        private PhoneHandler _phone;
        private PaymentProvider _payment;
        private SafeSpotFinder _safeSpots;
        private readonly Dictionary<ulong, Booking> _bookings = new Dictionary<ulong, Booking>();

        #region Configuration

        private class VehicleConfig
        {
            [JsonProperty("Enabled")]
            public bool Enabled = false;

            [JsonProperty("Type (Helicopter, Boat or Ground)")]
            public string Type = TypeHelicopter;

            [JsonProperty("Display name")]
            public string DisplayName = "";

            [JsonProperty("Prefab path")]
            public string PrefabPath = "";

            [JsonProperty("Base fare")]
            public double BaseFare = 50;

            [JsonProperty("Rate per meter")]
            public double RatePerMeter = 0.05;

            [JsonProperty("Cruise altitude above terrain (m)")]
            public float CruiseAltitude = 60f;

            [JsonProperty("Speed (m/s)")]
            public float Speed = 17f;

            [JsonProperty("Board the caller's whole team")]
            public bool TeamRide = false;

            [JsonProperty("Car modules in socket order (modular cars only)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Modules = null;
        }

        // Surge pricing (Task 10.1): fares scale with demand - rides dispatched in a
        // rolling hour - but only while the island is busy enough to justify it
        // (player gate keeps a lone night rider from surging himself).
        // Fares follow the rider's Cobalt band when Cobalt Papers Please is loaded
        // (its GetBand API); without it every rider pays the plain fare.
        private class StandingConfig
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Fare multiplier by band (0 = the taxi refuses the ride)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, double> Multipliers = new Dictionary<string, double>
            {
                ["Citizen"] = 0.9,
                ["Neutral"] = 1.0,
                ["Suspect"] = 1.0,
                ["Wanted"] = 2.0,
                ["Enemy"] = 0.0,
            };
        }

        private class SurgeConfig
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Minimum players online for surge to apply")]
            public int MinPlayersOnline = 2;

            [JsonProperty("Tiers (rides in the last hour -> fare multiplier)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<SurgeTier> Tiers = new List<SurgeTier>
            {
                new SurgeTier { MinRidesPerHour = 3, Multiplier = 1.25 },
                new SurgeTier { MinRidesPerHour = 6, Multiplier = 1.5 },
                new SurgeTier { MinRidesPerHour = 10, Multiplier = 2.0 },
            };
        }

        private class SurgeTier
        {
            [JsonProperty("Min rides in the last hour")]
            public int MinRidesPerHour;

            [JsonProperty("Fare multiplier")]
            public double Multiplier = 1.0;
        }

        private class PaymentConfig
        {
            [JsonProperty("Provider (Economics, ServerRewards or Scrap)")]
            public string Provider = "Scrap";
        }

        private class PosterSpot
        {
            [JsonProperty("Monument name contains")]
            public string Monument = "";

            [JsonProperty("Local position (x y z)")]
            public string LocalPosition = "0 0 0";

            [JsonProperty("Local rotation euler (x y z)")]
            public string LocalRotation = "0 0 0";
        }

        private class LandingPad
        {
            [JsonProperty("Monument name contains")]
            public string Monument = "";

            [JsonProperty("Local position (x y z)")]
            public string LocalPosition = "0 0 0";

            [JsonProperty("Snap radius (m)")]
            public float SnapRadius = 50f;

            [JsonProperty("Vehicle type (Helicopter or Boat)")]
            public string VehicleType = TypeHelicopter;
        }

        private class RestrictedZone
        {
            [JsonProperty("Monument name contains (leave empty for custom circle)")]
            public string Monument = "";

            [JsonProperty("Center X")]
            public float X;

            [JsonProperty("Center Z")]
            public float Z;

            [JsonProperty("Radius (m)")]
            public float Radius;
        }

        private class Configuration
        {
            [JsonProperty("Require permission (islandtaxi.use) to call a taxi")]
            public bool RequireUsePermission = false;

            [JsonProperty("Taxi phone number")]
            public int TaxiPhoneNumber = 55582941; // 555-TAXI1 on a keypad (dial pad requires 8 digits)

            [JsonProperty("Phone directory name")]
            public string PhoneName = "Island Taxi";

            [JsonProperty("Payment")]
            public PaymentConfig Payment = new PaymentConfig();

            [JsonProperty("Destination mode (MapPin or Grid)")]
            public string DestinationMode = "MapPin";

            [JsonProperty("Vehicles")]
            public Dictionary<string, VehicleConfig> Vehicles = DefaultVehicles();

            [JsonProperty("Max safe-spot search radius (m)")]
            public float MaxSearchRadius = 60f;

            [JsonProperty("Road snap distance (m) - max distance from player/pin to the road network")]
            public float RoadSnapDistance = 100f;

            [JsonProperty("Approach distance (m) - the taxi spawns this far away and travels in")]
            public float ApproachDistance = 400f;

            [JsonProperty("Despawn delay after drop-off (s)")]
            public float DespawnDelaySeconds = 30f;

            [JsonProperty("Ride request timeout (s)")]
            public float RideTimeoutSeconds = 120f;

            [JsonProperty("Surge pricing")]
            public SurgeConfig Surge = new SurgeConfig();

            [JsonProperty("Cobalt standing (needs Cobalt Papers Please)")]
            public StandingConfig Standing = new StandingConfig();

            // ObjectCreationHandling.Replace on every list: without it Json.NET appends
            // the file's entries onto these field defaults, and LoadConfig's save-back
            // then grows the config by one copy of the defaults per plugin load.
            [JsonProperty("Restricted zones", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<RestrictedZone> RestrictedZones = new List<RestrictedZone>();

            [JsonProperty("NPC driver in the driver seat")]
            public bool NpcDriver = true;

            [JsonProperty("NPC driver clothing (item shortnames, optional @skinid)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> DriverClothing = new List<string>
            {
                // Any public workshop skin renders once the client can fetch it -
                // "skins won't draw" (2026-08-30) turned out to be the CLIENT's
                // Steam "allow downloads during gameplay" setting, not the ids.
                // Company shirt: "Palm Pattern" (workshop 1382838119).
                "hat.cap", "sunglasses", "shirt.collared@1382838119", "pants", "shoes.boots"
            };

            [JsonProperty("Cab radio station (station-list name or stream URL)")]
            public string RadioStation = "WEFUNK";

            [JsonProperty("SAM sites ignore taxis")]
            public bool SamIgnoreTaxis = true;

            [JsonProperty("Posters enabled")]
            public bool PostersEnabled = true;

            [JsonProperty("Poster sign prefab (try alternatives with /taxi postertry)")]
            public string PosterPrefab = PosterSignPrefab;

            [JsonProperty("Poster spots (in-game ads; capture offsets with /taxi posterhere)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<PosterSpot> Posters = new List<PosterSpot>
            {
                // All captured in-game with /taxi posterhere on the live island
                // (2026-08-30/31); one spot covers every instance of its monument.
                new PosterSpot
                {
                    // Back wall of the bus shelter. The 1.5.3 value ("3 27.467 -62.56")
                    // was captured 68m from the nearest bus stop - it happened to land
                    // on one wall of the OLD map and floated 27m in the air at every
                    // other bus stop (first wipe, 2026-09-04).
                    Monument = "busstop",
                    LocalPosition = "-0.936 1.463 -2.765",
                    LocalRotation = "0 0 0"
                },
                new PosterSpot
                {
                    Monument = "bandit_town",
                    LocalPosition = "-56.451 3.382 19.866",
                    LocalRotation = "359.5 323.6 0"
                },
                new PosterSpot
                {
                    Monument = "compound", // Outpost
                    LocalPosition = "9.392 1.123 37.674",
                    LocalRotation = "0 180 0"
                },
                new PosterSpot
                {
                    Monument = "supermarket_1",
                    LocalPosition = "0.235 1.225 10.52",
                    LocalRotation = "0 0 0"
                },
                new PosterSpot
                {
                    Monument = "gas_station_1",
                    LocalPosition = "9.02 4.061 27.115",
                    LocalRotation = "0 90 0"
                },
                new PosterSpot
                {
                    Monument = "radtown_1",
                    LocalPosition = "-23.417 1.238 17.723",
                    LocalRotation = "0 90 0"
                }
            };

            [JsonProperty("Landing pads / boat docks (capture with /taxi padhere)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LandingPad> LandingPads = new List<LandingPad>();

            public static Dictionary<string, VehicleConfig> DefaultVehicles()
            {
                return new Dictionary<string, VehicleConfig>
                {
                    ["Minicopter"] = new VehicleConfig
                    {
                        Enabled = true,
                        DisplayName = "Minicopter",
                        PrefabPath = MinicopterPrefab,
                        BaseFare = 100,
                        RatePerMeter = 0.1,
                        CruiseAltitude = 60f,
                        Speed = 20f
                    },
                    ["AttackHeli"] = new VehicleConfig
                    {
                        Enabled = true,
                        DisplayName = "Attack Heli",
                        PrefabPath = AttackHeliPrefab,
                        BaseFare = 200,
                        RatePerMeter = 0.2,
                        CruiseAltitude = 80f,
                        Speed = 24f
                    },
                    ["ScrapHeli"] = new VehicleConfig
                    {
                        Enabled = true,
                        DisplayName = "Scrap Heli",
                        PrefabPath = ScrapHeliPrefab,
                        BaseFare = 150,
                        RatePerMeter = 0.15,
                        CruiseAltitude = 75f,
                        Speed = 22f,
                        TeamRide = true
                    },
                    ["ArmoredCar"] = new VehicleConfig
                    {
                        Enabled = true,
                        Type = TypeGround,
                        DisplayName = "Armored Car",
                        PrefabPath = Car3ChassisPrefab,
                        BaseFare = 200,
                        RatePerMeter = 0.2,
                        Speed = 24f,
                        Modules = new List<string>
                        {
                            "vehicle.1mod.cockpit.armored",
                            "vehicle.1mod.passengers.armored",
                            "vehicle.1mod.engine"
                        }
                    },
                    ["Car"] = new VehicleConfig
                    {
                        Enabled = true,
                        Type = TypeGround,
                        DisplayName = "Car",
                        PrefabPath = CarChassisPrefab,
                        BaseFare = 100,
                        RatePerMeter = 0.1,
                        Speed = 20f
                    },
                    ["Boat"] = new VehicleConfig
                    {
                        Enabled = true,
                        Type = TypeBoat,
                        DisplayName = "Boat",
                        PrefabPath = RhibPrefab,
                        BaseFare = 30,
                        RatePerMeter = 0.03,
                        Speed = 13f
                    },
                    ["Motorcycle"] = new VehicleConfig
                    {
                        Enabled = true,
                        Type = TypeGround,
                        DisplayName = "Motorcycle",
                        PrefabPath = MotorbikeSidecarPrefab,
                        BaseFare = 35,
                        RatePerMeter = 0.035,
                        Speed = 14f
                    },
                    ["Horse"] = new VehicleConfig
                    {
                        Enabled = true,
                        Type = TypeHorse,
                        DisplayName = "Horse",
                        PrefabPath = HorsePrefab,
                        BaseFare = 20,
                        RatePerMeter = 0.02,
                        Speed = 10f
                    }
                };
            }
        }

        protected override void LoadDefaultConfig()
        {
            _config = new Configuration();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null)
                {
                    throw new JsonException("Config deserialized to null");
                }
            }
            catch
            {
                PrintWarning("Configuration is invalid; loading defaults. Delete oxide/config/IslandTaxi.json to regenerate.");
                LoadDefaultConfig();
            }
            UpgradeConfig();
            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config);
        }

        // Migrates pre-1.1 configs: the old Boat placeholder had no prefab/type,
        // and older vehicle entries predate the Type field.
        private void UpgradeConfig()
        {
            foreach (var vehicle in _config.Vehicles.Values)
            {
                if (string.IsNullOrEmpty(vehicle.Type))
                {
                    vehicle.Type = TypeHelicopter;
                }
            }
            if (_config.Vehicles.TryGetValue("Boat", out var boat) && string.IsNullOrEmpty(boat.PrefabPath))
            {
                boat.PrefabPath = RhibPrefab;
                boat.Type = TypeBoat;
                boat.Enabled = true;
                if (boat.Speed <= 0f)
                {
                    boat.Speed = 10f;
                }
            }
            // 1.2: the Car placeholder gains its prefab; Motorcycle and Horse are new.
            if (_config.Vehicles.TryGetValue("Car", out var car) && string.IsNullOrEmpty(car.PrefabPath))
            {
                car.PrefabPath = CarChassisPrefab;
                car.Type = TypeGround;
                car.Enabled = true;
                if (car.Speed <= 0f)
                {
                    car.Speed = 13f;
                }
            }
            var defaults = Configuration.DefaultVehicles();
            foreach (var key in new[] { "Motorcycle", "Horse", "ScrapHeli", "AttackHeli", "ArmoredCar" })
            {
                if (!_config.Vehicles.ContainsKey(key))
                {
                    _config.Vehicles[key] = defaults[key];
                }
            }
            // 1.3: the poster upgraded from the framed picture sign to the artist canvas.
            if (_config.PosterPrefab == LegacyPosterPrefab)
            {
                _config.PosterPrefab = PosterSignPrefab;
            }
            // 1.3: configs saved by earlier builds accumulated one copy of the default
            // list entries per plugin load (see ObjectCreationHandling note); collapse
            // exact duplicates once.
            var clothing = new List<string>();
            foreach (var item in _config.DriverClothing)
            {
                if (!clothing.Contains(item))
                {
                    clothing.Add(item);
                }
            }
            _config.DriverClothing = clothing;

            var posters = new List<PosterSpot>();
            foreach (var spot in _config.Posters)
            {
                if (!posters.Exists(p => p.Monument == spot.Monument &&
                                         p.LocalPosition == spot.LocalPosition &&
                                         p.LocalRotation == spot.LocalRotation))
                {
                    posters.Add(spot);
                }
            }
            _config.Posters = posters;
            // 1.5.5: the shipped bus-stop spot was mis-anchored (see the default list).
            foreach (var spot in _config.Posters)
            {
                if (string.Equals(spot.Monument, "busstop", StringComparison.OrdinalIgnoreCase) &&
                    spot.LocalPosition == "3 27.467 -62.56")
                {
                    spot.LocalPosition = "-0.936 1.463 -2.765";
                    spot.LocalRotation = "0 0 0";
                }
            }

            var zones = new List<RestrictedZone>();
            foreach (var zone in _config.RestrictedZones)
            {
                if (!zones.Exists(z => z.Monument == zone.Monument && z.X == zone.X &&
                                       z.Z == zone.Z && z.Radius == zone.Radius))
                {
                    zones.Add(zone);
                }
            }
            _config.RestrictedZones = zones;
        }

        private static bool IsHeli(VehicleConfig cfg) => string.Equals(cfg.Type, TypeHelicopter, StringComparison.OrdinalIgnoreCase);
        private static bool IsBoat(VehicleConfig cfg) => string.Equals(cfg.Type, TypeBoat, StringComparison.OrdinalIgnoreCase);
        private static bool IsHorse(VehicleConfig cfg) => string.Equals(cfg.Type, TypeHorse, StringComparison.OrdinalIgnoreCase);
        private static bool IsGround(VehicleConfig cfg) => IsHorse(cfg) || string.Equals(cfg.Type, TypeGround, StringComparison.OrdinalIgnoreCase);

        private static string FlavorSuffix(VehicleConfig cfg)
        {
            if (IsBoat(cfg)) return "Boat";
            if (IsHorse(cfg)) return "Horse";
            if (IsGround(cfg)) return "Ground";
            return "Heli";
        }

        private string TerrainTag(VehicleConfig cfg, BasePlayer player)
        {
            if (IsBoat(cfg)) return Msg("TerrainWater", player);
            if (IsHorse(cfg)) return Msg("TerrainRoadsTrails", player);
            if (IsGround(cfg)) return Msg("TerrainRoads", player);
            return Msg("TerrainAir", player);
        }

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You don't have permission to use the taxi service.",
                ["NoActiveRide"] = "You have no active taxi booking or ride.",
                ["BookingCancelled"] = "<color=#ffd479>Island Taxi</color>: Booking cancelled.",
                ["BookingTimeout"] = "<color=#ffd479>Island Taxi</color>: Booking timed out. Call again when you're ready.",
                ["DropPinChat"] = "<color=#ffd479>Island Taxi</color>: Put the phone away (ESC or click outside it), then open your map (G) and place a marker where you want to go. You have {0} seconds. Type /taxi cancel to abort.",
                ["BannerDropPin"] = "ISLAND TAXI  -  put the phone away, open your map (G), drop a pin at your destination",
                ["DestinationRestricted"] = "<color=#ffd479>Island Taxi</color>: Sorry, we don't drive to {0}. Drop a pin somewhere else.",
                ["DestinationUnsafe"] = "<color=#ffd479>Island Taxi</color>: No safe drop-off found near that pin. Try a spot on more open ground.",
                ["MonumentNoService"] = "<color=#ffd479>Island Taxi</color>: We don't land inside that compound - no marked landing zone. Drop your pin on open ground outside it.",
                ["PadSnapped"] = "<color=#ffd479>Island Taxi</color>: Serviced territory - we'll set you down at the marked taxi zone there.",
                ["TeamBoarded"] = "<color=#ffd479>Island Taxi</color>: Riding along with your team - hold on!",
                ["Countermeasures"] = "<color=#ffd479>Island Taxi</color>: Missile lock detected - countermeasures away. Sit tight.",
                ["MonumentNoPickup"] = "<color=#ffd479>Island Taxi</color>: No taxi stand at this monument - walk out to open ground and call again. You have not been charged.",
                ["CannotAfford"] = "<color=#ffd479>Island Taxi</color>: That ride costs {0} and you can't cover it. Booking cancelled.",
                ["Charged"] = "<color=#ffd479>Island Taxi</color>: Charged {0}. Your ride is being dispatched!",
                ["FareFree"] = "FREE",
                ["PickupUnsafe"] = "<color=#ffd479>Island Taxi</color>: No safe pickup spot near you - move to more open ground and call again. You have not been charged.",
                ["DispatchFailed"] = "<color=#ffd479>Island Taxi</color>: Dispatch failed on our end - you have been refunded.",
                ["TaxiDispatchedHeli"] = "<color=#ffd479>Island Taxi</color>: Your {0} is on the way - about {1} seconds out. Listen for the rotors!",
                ["TaxiDispatchedBoat"] = "<color=#ffd479>Island Taxi</color>: Your {0} is on the way - about {1} seconds out. Watch the horizon!",
                ["TaxiDispatchedGround"] = "<color=#ffd479>Island Taxi</color>: Your {0} is on the way - about {1} seconds out. Listen for the engine!",
                ["TaxiDispatchedHorse"] = "<color=#ffd479>Island Taxi</color>: Your {0} is on the way - about {1} seconds out. Listen for hoofbeats!",
                ["TaxiArrivedHeli"] = "<color=#ffd479>Island Taxi</color>: Your taxi has landed - walk up to board.",
                ["TaxiArrivedBoat"] = "<color=#ffd479>Island Taxi</color>: Your boat is waiting at the shore - hop aboard.",
                ["TaxiArrivedGround"] = "<color=#ffd479>Island Taxi</color>: Your ride has pulled up - hop in.",
                ["TaxiArrivedHorse"] = "<color=#ffd479>Island Taxi</color>: Your horse is waiting - climb into the back saddle.",
                ["PickupNotNearRoad"] = "<color=#ffd479>Island Taxi</color>: You're too far from a road for a car pickup. Get closer to a road and drop your pin again.",
                ["DestinationNotNearRoad"] = "<color=#ffd479>Island Taxi</color>: That pin is too far from a road. Drop it closer to one.",
                ["PickupNotNearTrail"] = "<color=#ffd479>Island Taxi</color>: You're too far from a road or trail for a horse pickup. Get closer and drop your pin again.",
                ["DestinationNotNearTrail"] = "<color=#ffd479>Island Taxi</color>: That pin is too far from any road or trail. Drop it closer to one.",
                ["NoRoadRoute"] = "<color=#ffd479>Island Taxi</color>: No connected route to there on the road network. Try a different destination or vehicle.",
                ["TaxiDeparting"] = "<color=#ffd479>Island Taxi</color>: Buckle up, departing now!",
                ["TaxiNoShow"] = "<color=#ffd479>Island Taxi</color>: You didn't board in time, so your taxi left. No refunds - island policy.",
                ["RideArrived"] = "<color=#ffd479>Island Taxi</color>: You've arrived. Thanks for riding Island Taxi!",
                ["RideEndedHere"] = "<color=#ffd479>Island Taxi</color>: The ride ends here. Thanks for riding Island Taxi!",
                ["RideBailed"] = "<color=#ffd479>Island Taxi</color>: You jumped out! The taxi is landing without you. No refunds - island policy.",
                ["RideStuck"] = "<color=#ffd479>Island Taxi</color>: We can't make it any further - setting down here. No refunds - island policy.",
                ["RideDestroyed"] = "<color=#ffd479>Island Taxi</color>: Your taxi was destroyed. No refunds - island policy.",
                ["RideCancelled"] = "<color=#ffd479>Island Taxi</color>: Ride cancelled. The fare stays on the meter - island policy.",
                ["RideAlreadyActive"] = "<color=#ffd479>Island Taxi</color>: You already have a taxi on the job. /taxi cancel first if you want a new one.",
                ["UiTitle"] = "ISLAND TAXI",
                ["UiSubtitle"] = "Where to, friend?",
                ["UiSurgeNotice"] = "SURGE PRICING IN EFFECT - fares +{0}%. Busy hour on the island.",
                ["UiStandingSurcharge"] = "COBALT RECORD - fares +{0}%. Hazard pay for the driver.",
                ["UiStandingDiscount"] = "Good standing with Cobalt - fares -{0}%.",
                ["RefusedEnemy"] = "<color=#ffd479>Island Taxi</color>: Dispatch won't send a car for an Enemy of the State. Cobalt watches the roads, friend - you're walking.",
                ["UiComingSoon"] = "{0}  -  coming soon",
                ["TerrainAir"] = "air",
                ["TerrainRoads"] = "roads",
                ["TerrainWater"] = "water",
                ["TerrainRoadsTrails"] = "roads & trails",
                ["UiCancel"] = "CANCEL",
                ["UiConfirmTitle"] = "CONFIRM YOUR RIDE",
                ["UiVehicleLine"] = "Vehicle:  {0}",
                ["UiDestinationLine"] = "Destination:  grid {0}",
                ["UiDistanceLine"] = "Distance:  {0}",
                ["UiSpeedLine"] = "Speed:  {0:0} m/s",
                ["UiEtaLine"] = "Est. travel time:  {0}",
                ["UiPriceTag"] = "{0} + {1}/m  ·  {2:0} m/s",
                ["UiFareLine"] = "Fare:  {0}",
                ["DestinationNotCoastal"] = "<color=#ffd479>Island Taxi</color>: The boat needs water - drop your pin on or near the coast.",
                ["PickupNotCoastal"] = "<color=#ffd479>Island Taxi</color>: You're too far from the water for a boat pickup. Get closer to shore and drop your pin again.",
                ["NoWaterRoute"] = "<color=#ffd479>Island Taxi</color>: No clear water route from here to there. Try a different destination or vehicle.",
                ["UiConfirm"] = "CONFIRM",
                ["SafeSpotFound"] = "Safe spot near ({0:0}, {1:0}): ({2:0}, {3:0}, {4:0}), grid {5}, {6:0} m from requested point.",
                ["SafeSpotNone"] = "No safe spot found within {0:0} m of ({1:0}, {2:0}).",
                ["AdminStatus"] = "Island Taxi status:\nPhone number: {0}\nPhone registered: {1}\nPayment provider: {2}\nActive bookings: {3}\nActive rides: {4}",
                ["ConfigReloaded"] = "Island Taxi configuration reloaded.",
                ["TestUsage"] = "Usage: /taxi test <x> <z>",
                ["PlayerNotFound"] = "No connected player matches \"{0}\".",
                ["AdminCancelDone"] = "Cancelled {0}'s taxi.",
                ["AdminCancelNone"] = "{0} has no active taxi booking or ride.",
            }, this);
        }

        private string Msg(string key, BasePlayer player, params object[] args)
        {
            var text = lang.GetMessage(key, this, player?.UserIDString);
            return args.Length > 0 ? string.Format(text, args) : text;
        }

        private void Message(BasePlayer player, string key, params object[] args)
        {
            if (player != null)
            {
                player.ChatMessage(Msg(key, player, args));
            }
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermFree, this);
            permission.RegisterPermission(PermAdmin, this);
            Puts($"IslandTaxi v{Version} loaded - by LowPopLabs - ko-fi.com/lowpoplabs");
        }

        private void OnServerInitialized()
        {
            _phone = new PhoneHandler(this);
            _phone.Create();
            _payment = new PaymentProvider(this);
            _safeSpots = new SafeSpotFinder(this);
            timer.Every(60f, UpdateRideLights);
            SpawnPosters();
            BuildPads();
        }

        // Headlights (and boat nav lights - same flag) on after dark, off by day.
        private static bool IsNightTime()
        {
            return TOD_Sky.Instance != null &&
                   (TOD_Sky.Instance.Cycle.Hour > 19f || TOD_Sky.Instance.Cycle.Hour < 8f);
        }

        private void UpdateRideLights()
        {
            var night = IsNightTime();
            foreach (var ride in _ridesByVehicle.Values)
            {
                if (ride.Vehicle != null && !ride.Vehicle.IsDestroyed &&
                    ride.Vehicle.HasFlag(BaseVehicle.Flag_Headlights) != night)
                {
                    SetFlagNet(ride.Vehicle, BaseVehicle.Flag_Headlights, night);
                }
            }
        }

        private void Unload()
        {
            foreach (var booking in new List<Booking>(_bookings.Values))
            {
                EndBooking(booking.Player, null);
            }
            _bookings.Clear();
            foreach (var ride in new List<Ride>(_rides.Values))
            {
                EndRide(ride.Player, despawnImmediately: true, messageKey: null);
            }
            _rides.Clear();
            _ridesByVehicle.Clear();
            _phone?.Destroy();
            _phone = null;
            KillPosters();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            EndBooking(player, null);
            if (player != null && _rides.ContainsKey(player.userID))
            {
                // Dismount the sleeper and let the empty taxi land, then despawn.
                EndRide(player, despawnImmediately: false, messageKey: null);
            }
        }

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null)
            {
                return;
            }
            if (_bookings.ContainsKey(player.userID))
            {
                EndBooking(player, "BookingCancelled");
            }
            if (_rides.ContainsKey(player.userID))
            {
                EndRide(player, despawnImmediately: false, messageKey: null);
            }
        }

        #endregion

        #region PhoneHandler

        // Owns the hidden telephone entity that backs the taxi number (docs/decisions/0002).
        // OnPhoneDial only fires for numbers registered in TelephoneManager, so the number
        // must belong to a real PhoneController.
        private class PhoneHandler
        {
            private readonly IslandTaxi _plugin;
            private Telephone _entity;

            public PhoneController Controller { get; private set; }

            public PhoneHandler(IslandTaxi plugin)
            {
                _plugin = plugin;
            }

            public bool IsTaxiPhone(PhoneController controller)
            {
                return Controller != null && ReferenceEquals(controller, Controller);
            }

            public void Create()
            {
                var entity = GameManager.server.CreateEntity(TelephonePrefab, new Vector3(0f, -1000f, 0f), Quaternion.identity);
                if (entity == null)
                {
                    _plugin.PrintError($"Failed to create the hidden taxi phone ({TelephonePrefab}).");
                    return;
                }

                entity.EnableSaving(false); // never persist into map saves
                entity.Spawn();

                _entity = entity as Telephone;
                if (_entity == null || _entity.Controller == null)
                {
                    _plugin.PrintError("Hidden taxi phone spawned but has no PhoneController; taxi number unavailable.");
                    entity.Kill();
                    _entity = null;
                    return;
                }

                Controller = _entity.Controller;

                // ServerInit already auto-assigned a random number and registered it;
                // swap in the configured number unless someone else already owns it.
                // The client dial pad only sends 8-digit numbers (verified in-game), so the
                // vanity number must stay inside the vanilla range: 55582941 = 555-TAXI1.
                var desired = _plugin._config.TaxiPhoneNumber;
                if (desired < TelephoneManager.MinPhoneNumber || desired > TelephoneManager.MaxPhoneNumber)
                {
                    _plugin.PrintWarning($"Configured taxi number {desired} is not 8 digits; using default 55582941 (555-TAXI1).");
                    desired = 55582941;
                }
                var existing = TelephoneManager.GetTelephone(desired);
                if (existing != null && !ReferenceEquals(existing, Controller))
                {
                    _plugin.PrintError($"Taxi number {desired} is already in use by another phone; keeping auto-assigned number {Controller.PhoneNumber}. Change \"Taxi phone number\" in the config.");
                }
                else
                {
                    TelephoneManager.DeregisterTelephone(Controller);
                    Controller.PhoneNumber = desired;
                    TelephoneManager.RegisterTelephone(Controller);
                }

                Controller.PhoneName = _plugin._config.PhoneName;
                _entity.SendNetworkUpdate();
                _plugin.Puts($"Taxi phone registered: {Controller.PhoneNumber} (\"{Controller.PhoneName}\")");
                // Standing watchdog: DeregisterTelephone removes by NUMBER blindly, so
                // any deferred cleanup of a predecessor (or anything else touching the
                // registry) can wipe this registration. Re-claim within 15 s, forever.
                _registrationWatchdog?.Destroy();
                _registrationWatchdog = _plugin.timer.Every(15f, EnsureRegistered);
            }

            private Timer _registrationWatchdog;

            public bool IsEntity(BaseNetworkable entity)
            {
                return _entity != null && ReferenceEquals(entity, _entity);
            }

            private void EnsureRegistered()
            {
                // Something outside the plugin kills the hidden phone entity after
                // fast reloads (registry empty while the old watchdog saw "nothing
                // wrong" - its early-exit hid the death, 2026-08-30). Dead phone ->
                // build a new one; the taxi line is immortal now.
                if (_entity == null || _entity.IsDestroyed || Controller == null)
                {
                    _plugin.PrintWarning("Hidden taxi phone entity was destroyed (outside the plugin); recreating it.");
                    _entity = null;
                    Controller = null;
                    Create();
                    return;
                }
                if (string.IsNullOrEmpty(Controller.PhoneName))
                {
                    // An empty name hides the entry from the phone directory even
                    // while the number itself still connects.
                    Controller.PhoneName = _plugin._config.PhoneName;
                    _entity.SendNetworkUpdate();
                    _plugin.Puts("Taxi phone directory name was empty; restored.");
                }
                var current = TelephoneManager.GetTelephone(Controller.PhoneNumber);
                if (ReferenceEquals(current, Controller))
                {
                    return; // registry intact
                }
                if (current != null)
                {
                    return; // genuinely owned by someone else; Create already warned
                }
                TelephoneManager.RegisterTelephone(Controller);
                _entity.SendNetworkUpdate();
                _plugin.Puts($"Taxi phone re-registered after registry wipe: {Controller.PhoneNumber}");
            }

            public void Destroy()
            {
                _registrationWatchdog?.Destroy();
                _registrationWatchdog = null;
                if (Controller != null)
                {
                    // Deregister NOW, then ZERO the number: the entity's deferred
                    // destroy calls DeregisterTelephone again, which removes by
                    // number blindly - with the number zeroed it removes key 0
                    // instead of wiping a successor's registration (the F1-reload
                    // directory-loss root cause, 2026-08-30).
                    TelephoneManager.DeregisterTelephone(Controller);
                    Controller.PhoneNumber = 0;
                }
                if (_entity != null && !_entity.IsDestroyed)
                {
                    _entity.Kill();
                }
                _entity = null;
                Controller = null;
            }
        }

        // TODO remove once the phone-killer is identified (Task: F1-reload phone loss):
        // logs the moment anything destroys the hidden taxi phone.
        private object OnPhoneDial(PhoneController callingPhone, PhoneController receiverPhone, BasePlayer player)
        {
            if (_phone == null || !_phone.IsTaxiPhone(receiverPhone))
            {
                return null;
            }

            // Always cancel the vanilla call to the taxi line, even without a valid caller.
            if (player == null)
            {
                return true;
            }

            if (!CanUseTaxi(player))
            {
                Message(player, "NoPermission");
                return true;
            }

            if (_rides.ContainsKey(player.userID))
            {
                Message(player, "RideAlreadyActive");
                return true;
            }

            StartBooking(player);
            // Hang up and release the phone so the vanilla phone screen closes -
            // otherwise the player must click out of it before the map will open.
            callingPhone.ServerHangUp();
            callingPhone.ClearCurrentUser();
            return true;
        }

        #endregion

        #region Booking flow

        private enum BookingState
        {
            SelectingVehicle,
            AwaitingPin,
            Confirming
        }

        private class Booking
        {
            public BasePlayer Player;
            public BookingState State;
            public VehicleConfig Vehicle;
            public Vector3 Destination;
            public Vector3 Pickup;             // boats: resolved at pin time
            public bool HasPickup;
            public List<Vector3> DestRoute = new List<Vector3>(); // boats: detour waypoints
            public float Distance;
            public double Fare;
            public bool IsFree;
            public double Surge = 1.0; // multiplier snapshotted when the menu opened
            public double Standing = 1.0; // Cobalt band multiplier, snapshotted with Surge
            public Timer Timeout;
        }

        // Open to everyone unless the config demands the permission; server admins always pass.
        private bool CanUseTaxi(BasePlayer player)
        {
            return !_config.RequireUsePermission ||
                   player.IsAdmin ||
                   permission.UserHasPermission(player.UserIDString, PermUse);
        }

        private bool IsTaxiAdmin(BasePlayer player)
        {
            return player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermAdmin);
        }

        private void StartBooking(BasePlayer player)
        {
            if (_rides.ContainsKey(player.userID))
            {
                Message(player, "RideAlreadyActive"); // covers /taxi as well as the phone
                return;
            }
            EndBooking(player, null); // restart cleanly if they call twice

            var standing = StandingMultiplier(player);
            if (standing <= 0)
            {
                Message(player, "RefusedEnemy");
                return;
            }

            var booking = new Booking
            {
                Player = player,
                State = BookingState.SelectingVehicle,
                IsFree = permission.UserHasPermission(player.UserIDString, PermFree),
                // Snapshotted here so the menu banner, the quote, and the charge all
                // agree even if the tier shifts mid-booking.
                Surge = CurrentSurgeMultiplier(),
                Standing = standing,
            };
            booking.Timeout = timer.Once(_config.RideTimeoutSeconds, () =>
            {
                if (_bookings.TryGetValue(player.userID, out var b) && ReferenceEquals(b, booking))
                {
                    EndBooking(player, "BookingTimeout");
                }
            });
            _bookings[player.userID] = booking;
            ShowVehicleSelect(player);
        }

        // Tears down UI and timer; sends messageKey to the player if given.
        private void EndBooking(BasePlayer player, string messageKey)
        {
            if (player == null)
            {
                return;
            }
            DestroyAllUi(player);
            if (!_bookings.TryGetValue(player.userID, out var booking))
            {
                return; // nothing to cancel (e.g. a stale UI cancel button)
            }
            booking.Timeout?.Destroy();
            _bookings.Remove(player.userID);
            if (messageKey != null)
            {
                Message(player, messageKey);
            }
        }

        private void SelectVehicle(BasePlayer player, string key)
        {
            if (!_bookings.TryGetValue(player.userID, out var booking) ||
                booking.State != BookingState.SelectingVehicle)
            {
                return;
            }
            if (!_config.Vehicles.TryGetValue(key, out var vehicle) || !vehicle.Enabled)
            {
                return;
            }

            booking.Vehicle = vehicle;
            booking.State = BookingState.AwaitingPin;

            CuiHelper.DestroyUi(player, UiMain);
            ShowPinBanner(player);
            Message(player, "DropPinChat", (int)_config.RideTimeoutSeconds);
        }

        private void OnMapMarkerAdded(BasePlayer player, ProtoBuf.MapNote note)
        {
            if (player == null || note == null ||
                !_bookings.TryGetValue(player.userID, out var booking) ||
                booking.State != BookingState.AwaitingPin)
            {
                return;
            }

            var requested = note.worldPosition;

            if (IsRestricted(requested, out var zoneName))
            {
                Message(player, "DestinationRestricted", zoneName);
                return; // stay in AwaitingPin; they can drop another pin
            }

            Vector3 dropoff;
            if (IsBoat(booking.Vehicle))
            {
                // Boats need water at both ends and a clear water route between them.
                // Dock points (captured pads) trump the coastal search at either end -
                // that's what makes Oil Rig serviceable - and each dock carries its own
                // radial staging corridor, because SeawardStaging and the depth-only
                // route check are blind to structures standing in deep water.
                var dropIsDock = TrySnapToPad(requested, boatPad: true, out var dropDock);
                if (dropIsDock)
                {
                    dropoff = dropDock.Position;
                    Message(player, "PadSnapped");
                }
                else if (!TryFindWaterSpot(requested, out dropoff))
                {
                    Puts($"[boat-debug] no water spot near PIN ({requested.x:0},{requested.z:0}); columnAtPin={WaterColumnDepth(requested):0.00}");
                    Message(player, "DestinationNotCoastal");
                    return;
                }
                Vector3 pickup;
                var pickIsDock = TrySnapToPad(player.transform.position, boatPad: true, out var pickDock);
                if (pickIsDock)
                {
                    pickup = pickDock.Position;
                }
                else if (!TryFindWaterSpot(player.transform.position, out pickup))
                {
                    var pp = player.transform.position;
                    Puts($"[boat-debug] no water spot near PLAYER ({pp.x:0},{pp.z:0}); columnAtPlayer={WaterColumnDepth(pp):0.00}");
                    Message(player, "PickupNotCoastal");
                    return;
                }
                List<Vector3> route;
                if (pickIsDock || dropIsDock)
                {
                    // Route between corridor mouths; the short radial dock legs are
                    // clear by construction.
                    var from = pickIsDock ? pickDock.Staging : SeawardStaging(pickup);
                    var to = dropIsDock ? dropDock.Staging : SeawardStaging(dropoff);
                    var mids = new List<Vector3>();
                    if (!ClearOrDetour(from, to, 0, mids))
                    {
                        Puts($"[boat-debug] NO ROUTE (dock leg) from=({from.x:0},{from.z:0}) to=({to.x:0},{to.z:0})");
                        Message(player, "NoWaterRoute");
                        return;
                    }
                    route = new List<Vector3> { from };
                    route.AddRange(mids);
                    route.Add(to);
                }
                else if (!TryFindWaterRoute(pickup, dropoff, out route))
                {
                    var sa = SeawardStaging(pickup);
                    var sb = SeawardStaging(dropoff);
                    Puts($"[boat-debug] NO ROUTE pickup=({pickup.x:0},{pickup.z:0}) d={WaterColumnDepth(pickup):0.00} stagingA=({sa.x:0},{sa.z:0}) d={WaterColumnDepth(sa):0.00} dropoff=({dropoff.x:0},{dropoff.z:0}) d={WaterColumnDepth(dropoff):0.00} stagingB=({sb.x:0},{sb.z:0}) d={WaterColumnDepth(sb):0.00} directClear={IsWaterRouteClear(sa, sb)}");
                    Message(player, "NoWaterRoute");
                    return;
                }
                Puts($"[boat-debug] route OK: {route.Count} waypoints, length {RouteLength(pickup, route, dropoff):0}m");
                booking.Pickup = pickup;
                booking.HasPickup = true;
                booking.DestRoute = route;
                booking.Distance = RouteLength(pickup, route, dropoff);
            }
            else if (IsGround(booking.Vehicle))
            {
                // Ground taxis live on the road network (horses also use trails).
                var network = GetNetwork(booking.Vehicle);
                if (!network.TryNearest(player.transform.position, _config.RoadSnapDistance, out var startNode))
                {
                    Message(player, IsHorse(booking.Vehicle) ? "PickupNotNearTrail" : "PickupNotNearRoad");
                    return;
                }
                if (!network.TryNearest(requested, _config.RoadSnapDistance, out var endNode))
                {
                    Message(player, IsHorse(booking.Vehicle) ? "DestinationNotNearTrail" : "DestinationNotNearRoad");
                    return;
                }
                if (!network.TryFindPath(startNode, endNode, out var roadPath) || roadPath.Count < 2)
                {
                    Message(player, "NoRoadRoute");
                    return;
                }
                booking.Pickup = roadPath[0];
                booking.HasPickup = true;
                dropoff = roadPath[roadPath.Count - 1];
                booking.DestRoute = roadPath.GetRange(1, roadPath.Count - 2);
                booking.Distance = RouteLength(booking.Pickup, booking.DestRoute, dropoff);
            }
            else
            {
                if (TrySnapToPad(requested, boatPad: false, out var heliPad))
                {
                    dropoff = heliPad.Position;
                    Message(player, "PadSnapped");
                }
                else if (!_safeSpots.TryFind(requested, out dropoff))
                {
                    Message(player, InNamedMonument(requested) ? "MonumentNoService" : "DestinationUnsafe");
                    return;
                }
                booking.Distance = Vector3.Distance(player.transform.position, dropoff);
            }

            booking.Destination = dropoff;
            booking.Fare = booking.IsFree
                ? 0
                : Math.Ceiling(CalculateFare(booking.Vehicle, booking.Distance) * booking.Surge * booking.Standing);
            booking.State = BookingState.Confirming;

            CuiHelper.DestroyUi(player, UiBanner);
            ShowConfirm(player, booking);
        }

        private void ConfirmBooking(BasePlayer player)
        {
            if (!_bookings.TryGetValue(player.userID, out var booking) ||
                booking.State != BookingState.Confirming)
            {
                return;
            }

            CuiHelper.DestroyUi(player, UiConfirm);

            if (!booking.IsFree)
            {
                if (!_payment.Charge(player, booking.Fare))
                {
                    Message(player, "CannotAfford", _payment.FormatAmount(booking.Fare));
                    EndBooking(player, null);
                    return;
                }
                Message(player, "Charged", _payment.FormatAmount(booking.Fare));
            }

            // Pickup must be findable near the caller; refund if not (nothing spawned yet).
            // Boats resolved their pickup (a water spot) at pin time.
            Vector3 pickup;
            if (booking.HasPickup)
            {
                pickup = booking.Pickup;
            }
            else if (TrySnapToPad(player.transform.position, boatPad: false, out var pickupPad))
            {
                pickup = pickupPad.Position;
            }
            else if (!_safeSpots.TryFind(player.transform.position, out pickup))
            {
                if (!booking.IsFree)
                {
                    _payment.Refund(player, booking.Fare);
                }
                EndBooking(player, InNamedMonument(player.transform.position) ? "MonumentNoPickup" : "PickupUnsafe");
                return;
            }

            var vehicle = booking.Vehicle;
            var destination = booking.Destination;
            var destRoute = booking.DestRoute;
            var paidFare = booking.IsFree ? 0 : booking.Fare;
            EndBooking(player, null); // booking UI/state done; the ride takes over
            DispatchRide(player, vehicle, pickup, destination, paidFare, destRoute);
        }

        // Named monuments (the ones that display on the map) are no-land zones for the
        // heli: freelance spots inside them chase endless weird geometry (probe
        // 2026-08-29: Launch Site roof/edge clips). Service returns per-monument via
        // designated landing pads (Task 8.3). Roadside decor prefabs (bus stops etc.)
        // are monuments too but unnamed, and stay landable.
        // Monument OBBs are unreliable (survey 2026-08-29): Launch Site ships a
        // 400x200 box for a ~550x280 compound - its own main building sits OUTSIDE
        // IsInBounds (live failure) - and Lighthouse ships zero bounds. Judge the 2D
        // footprint in monument-local space with inflated extents; radius fallback
        // for degenerate bounds. Y is ignored: a rooftop or basement is still inside.
        private const float BoundsInflation = 1.5f;
        private const float DegenerateBoundsWidth = 10f;
        private const float FallbackMonumentRadius = 60f;

        private static bool InNamedMonument(Vector3 pos)
        {
            var monuments = TerrainMeta.Path?.Monuments;
            if (monuments == null)
            {
                return false;
            }
            foreach (var monument in monuments)
            {
                if (monument == null || !monument.shouldDisplayOnMap)
                {
                    continue;
                }
                if (monument.GetWidest2DBound() < DegenerateBoundsWidth)
                {
                    var d = monument.transform.position - pos;
                    d.y = 0f;
                    if (d.sqrMagnitude <= FallbackMonumentRadius * FallbackMonumentRadius)
                    {
                        return true;
                    }
                    continue;
                }
                var local = monument.transform.InverseTransformPoint(pos);
                var bounds = monument.Bounds;
                if (Mathf.Abs(local.x - bounds.center.x) <= bounds.extents.x * BoundsInflation &&
                    Mathf.Abs(local.z - bounds.center.z) <= bounds.extents.z * BoundsInflation)
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsRestricted(Vector3 pos, out string zoneName)
        {
            foreach (var zone in _config.RestrictedZones)
            {
                if (!string.IsNullOrEmpty(zone.Monument))
                {
                    foreach (var monument in TerrainMeta.Path.Monuments)
                    {
                        if (monument.name.IndexOf(zone.Monument, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            monument.IsInBounds(pos))
                        {
                            zoneName = zone.Monument;
                            return true;
                        }
                    }
                }
                else if (zone.Radius > 0f)
                {
                    var dx = pos.x - zone.X;
                    var dz = pos.z - zone.Z;
                    if (Mathf.Sqrt(dx * dx + dz * dz) <= zone.Radius)
                    {
                        zoneName = $"({zone.X:0}, {zone.Z:0})";
                        return true;
                    }
                }
            }
            zoneName = null;
            return false;
        }

        #endregion

        #region FareCalculator

        private static double CalculateFare(VehicleConfig vehicle, float distanceMeters)
        {
            return Math.Ceiling(vehicle.BaseFare + vehicle.RatePerMeter * distanceMeters);
        }

        // Rolling-hour demand meter (Task 10.1). Dispatch timestamps on the server's
        // uptime clock (no wall-clock jumps); pruned on read, so no timers needed and
        // the surge decays by itself.
        private readonly List<float> _surgeRideTimes = new List<float>();

        private void RecordRideForSurge()
        {
            _surgeRideTimes.Add(Time.realtimeSinceStartup);
        }

        // 1.0 when disabled, when the island is too quiet (player gate), or when the
        // last hour was slow; otherwise the highest tier the ride count clears.
        // The rider's Cobalt band -> fare multiplier; 1 when Papers Please is absent,
        // the feature is off, or the band has no entry. 0 means refuse the ride.
        private double StandingMultiplier(BasePlayer player)
        {
            var cfg = _config.Standing;
            if (cfg == null || !cfg.Enabled || cfg.Multipliers == null || PapersPlease == null || !PapersPlease.IsLoaded)
            {
                return 1.0;
            }
            var band = PapersPlease.Call("GetBand", (ulong)player.userID) as string;
            return band != null && cfg.Multipliers.TryGetValue(band, out var mult) ? Math.Max(0, mult) : 1.0;
        }

        private double CurrentSurgeMultiplier()
        {
            var surge = _config.Surge;
            if (surge == null || !surge.Enabled ||
                BasePlayer.activePlayerList.Count < surge.MinPlayersOnline)
            {
                return 1.0;
            }
            var cutoff = Time.realtimeSinceStartup - 3600f;
            _surgeRideTimes.RemoveAll(t => t < cutoff);
            var multiplier = 1.0;
            foreach (var tier in surge.Tiers)
            {
                if (_surgeRideTimes.Count >= tier.MinRidesPerHour && tier.Multiplier > multiplier)
                {
                    multiplier = tier.Multiplier;
                }
            }
            return multiplier;
        }

        private static string FormatDistance(float meters)
        {
            return meters < 1000f ? $"{meters:0} m" : $"{meters / 1000f:0.0} km";
        }

        private static string FormatDuration(float seconds)
        {
            if (seconds < 60f)
            {
                return $"{Mathf.CeilToInt(seconds)} s";
            }
            var minutes = Mathf.FloorToInt(seconds / 60f);
            var rest = Mathf.CeilToInt(seconds - minutes * 60f);
            return rest > 0 ? $"{minutes} min {rest} s" : $"{minutes} min";
        }

        #endregion

        #region PaymentProvider

        // Charges/refunds through Economics or ServerRewards if configured and loaded,
        // otherwise falls back to scrap. Charge() is check-and-take in one call.
        private class PaymentProvider
        {
            private readonly IslandTaxi _plugin;
            private ItemDefinition _scrap;

            public string Mode { get; private set; }

            public PaymentProvider(IslandTaxi plugin)
            {
                _plugin = plugin;
                _scrap = ItemManager.FindItemDefinition("scrap");
                Resolve();
            }

            public void Resolve()
            {
                var want = _plugin._config.Payment.Provider?.Trim() ?? "Scrap";
                if (want.Equals("Economics", StringComparison.OrdinalIgnoreCase))
                {
                    Mode = _plugin.Economics != null ? "Economics" : "Scrap";
                }
                else if (want.Equals("ServerRewards", StringComparison.OrdinalIgnoreCase))
                {
                    Mode = _plugin.ServerRewards != null ? "ServerRewards" : "Scrap";
                }
                else
                {
                    Mode = "Scrap";
                }
                if (!Mode.Equals(want, StringComparison.OrdinalIgnoreCase) && !want.Equals("Scrap", StringComparison.OrdinalIgnoreCase))
                {
                    _plugin.PrintWarning($"Payment provider \"{want}\" is not loaded; falling back to scrap.");
                }
            }

            public string FormatAmount(double amount)
            {
                switch (Mode)
                {
                    case "Economics": return $"${amount:0}";
                    case "ServerRewards": return $"{amount:0} RP";
                    default: return $"{amount:0} scrap";
                }
            }

            public string FormatRate(double perMeter)
            {
                switch (Mode)
                {
                    case "Economics": return $"${perMeter:0.###}";
                    case "ServerRewards": return $"{perMeter:0.###} RP";
                    default: return $"{perMeter:0.###} scrap";
                }
            }

            public bool Charge(BasePlayer player, double amount)
            {
                if (amount <= 0)
                {
                    return true;
                }
                ulong id = player.userID;
                switch (Mode)
                {
                    case "Economics":
                        return _plugin.Economics?.Call("Withdraw", id, amount) is bool ok && ok;
                    case "ServerRewards":
                        return _plugin.ServerRewards?.Call("TakePoints", id, (int)amount) != null;
                    default:
                        var needed = (int)amount;
                        if (_scrap == null || player.inventory.GetAmount(_scrap.itemid) < needed)
                        {
                            return false;
                        }
                        player.inventory.Take(null, _scrap.itemid, needed);
                        return true;
                }
            }

            public void Refund(BasePlayer player, double amount)
            {
                if (amount <= 0)
                {
                    return;
                }
                ulong id = player.userID;
                switch (Mode)
                {
                    case "Economics":
                        _plugin.Economics?.Call("Deposit", id, amount);
                        break;
                    case "ServerRewards":
                        _plugin.ServerRewards?.Call("AddPoints", id, (int)amount);
                        break;
                    default:
                        if (_scrap != null)
                        {
                            var item = ItemManager.Create(_scrap, (int)amount);
                            player.GiveItem(item);
                        }
                        break;
                }
            }
        }

        #endregion

        #region SafeSpotFinder

        // Ring-searches outward from a requested point for ground a heli can land on:
        // gentle slope, not underwater, clear of construction and tool-cupboard range.
        private class SafeSpotFinder
        {
            private const float RingStep = 8f;
            private const float MaxSlopeDegrees = 25f;
            private const float MaxWaterDepth = 0.5f;
            private const float BlockCheckRadius = 4f;
            private const float PrivilegeCheckRadius = 30f; // ≈ TC building-privilege sphere
            private const float ClearanceRadius = 3f;       // rotor/body space above the spot
            private const float MaxHeightAboveTerrain = 3f; // World-layer ground must hug the heightmap
            private const float DescentColumnRadius = 4f;   // rotor radius + margin, swept down the column

            private static readonly int GroundMask = LayerMask.GetMask("Terrain", "World");
            private static readonly int ObstructionMask = LayerMask.GetMask("Construction", "Deployed", "Vehicle_World");
            // Rotor clearance also fears World geometry (monument walls, rocks, cliff
            // faces) - a spot right at a building edge passed the column ray but the
            // descent clipped the wall.
            private static readonly int ClearanceMask = LayerMask.GetMask("Construction", "Deployed", "Vehicle_World", "World");

            private readonly IslandTaxi _plugin;

            public SafeSpotFinder(IslandTaxi plugin)
            {
                _plugin = plugin;
            }

            public bool TryFind(Vector3 requested, out Vector3 spot)
            {
                var max = Mathf.Max(_plugin._config.MaxSearchRadius, RingStep);
                for (float radius = 0f; radius <= max; radius += RingStep)
                {
                    var samples = radius < 1f ? 1 : Mathf.CeilToInt(2f * Mathf.PI * radius / RingStep);
                    for (int i = 0; i < samples; i++)
                    {
                        var angle = (float)i / samples * 2f * Mathf.PI;
                        var candidate = new Vector3(
                            requested.x + Mathf.Sin(angle) * radius,
                            0f,
                            requested.z + Mathf.Cos(angle) * radius);
                        if (IsSafe(ref candidate))
                        {
                            spot = candidate;
                            return true;
                        }
                    }
                }
                spot = Vector3.zero;
                return false;
            }

            private bool IsSafe(ref Vector3 pos)
            {
                pos.y = TerrainMeta.HeightMap.GetHeight(pos);

                // Sweep a heli-sized sphere down the whole descent column, from far above
                // the tallest monument - a thin ray misses overhangs and roof edges just
                // OFF the column's center (clipped one live at Launch Site), and starting
                // low (the old +50) can begin inside a tall building and never see its
                // roof.
                RaycastHit hit;
                if (!Physics.SphereCast(pos + Vector3.up * 300f, DescentColumnRadius,
                        Vector3.down, out hit, 350f, GroundMask))
                {
                    return false;
                }

                // The first thing the swept column touches must be the ground itself
                // (within 3 m of the heightmap): a rooftop, overhang, or wall touched on
                // the way down all land the contact point high and reject the spot.
                // Ground-level monument slabs and roads still pass.
                if (hit.point.y - pos.y > MaxHeightAboveTerrain)
                {
                    return false;
                }
                pos.y = hit.point.y;

                if (Vector3.Angle(hit.normal, Vector3.up) > MaxSlopeDegrees)
                {
                    return false;
                }

                if (WaterLevel.GetWaterDepth(pos, true, false) > MaxWaterDepth)
                {
                    return false;
                }

                // Inside a named monument? Pad-only territory (Task 8.3), never freelance.
                if (InNamedMonument(pos))
                {
                    return false;
                }

                // Anything built or parked right on the spot, or the rotor space above it?
                if (Vis.AnyColliders(pos + Vector3.up * (ClearanceRadius + 0.5f), ClearanceRadius, ClearanceMask))
                {
                    return false;
                }
                var blocks = Facepunch.Pool.Get<List<BuildingBlock>>();
                Vis.Entities(pos, BlockCheckRadius, blocks, LayerMask.GetMask("Construction"));
                var blocked = blocks.Count > 0;
                Facepunch.Pool.FreeUnmanaged(ref blocks);
                if (blocked)
                {
                    return false;
                }

                // Inside anyone's tool cupboard range?
                var cupboards = Facepunch.Pool.Get<List<BuildingPrivlidge>>();
                Vis.Entities(pos, PrivilegeCheckRadius, cupboards, LayerMask.GetMask("Deployed"));
                var inPrivilege = cupboards.Count > 0;
                Facepunch.Pool.FreeUnmanaged(ref cupboards);
                return !inPrivilege;
            }
        }

        #endregion

        #region WaterNav

        // Water COLUMN depth (surface height minus seabed). WaterLevel.GetWaterDepth
        // measures how deep the query POINT is submerged - at sea level (y=0) that is
        // ~0 everywhere, which is useless for "is there enough water for a boat here".
        // WaterMap.GetHeight returns -500 (terrain origin) where it has no data - true
        // for most of the open ocean - so clamp the surface to ocean level (y=0);
        // rivers carry real WaterMap data above 0 and still win the Max.
        private static float WaterColumnDepth(Vector3 pos)
        {
            var surface = Mathf.Max(TerrainMeta.WaterMap.GetHeight(pos), 0f);
            return surface - TerrainMeta.HeightMap.GetHeight(pos);
        }

        // Nearest water spot with enough draft for a boat, clear of obstructions.
        // No maximum depth: the nearest valid water wins, and the ring search
        // naturally returns the spot closest to shore. Search radius is wider than
        // the land finder because pins usually land on the beach, not in the water.
        private bool TryFindWaterSpot(Vector3 requested, out Vector3 spot)
        {
            var max = Mathf.Max(_config.MaxSearchRadius, 160f);
            for (float radius = 0f; radius <= max; radius += 8f)
            {
                var samples = radius < 1f ? 1 : Mathf.CeilToInt(2f * Mathf.PI * radius / 8f);
                for (int i = 0; i < samples; i++)
                {
                    var angle = (float)i / samples * 2f * Mathf.PI;
                    var candidate = new Vector3(
                        requested.x + Mathf.Sin(angle) * radius,
                        0f,
                        requested.z + Mathf.Cos(angle) * radius);
                    if (WaterColumnDepth(candidate) < MinChannelDepth)
                    {
                        continue;
                    }
                    if (Vis.AnyColliders(candidate + Vector3.up * 1.5f, 4f,
                        LayerMask.GetMask("Construction", "Deployed", "Vehicle_World")))
                    {
                        continue;
                    }
                    candidate.y = TerrainMeta.WaterMap.GetHeight(candidate); // surface (rivers != 0)
                    spot = candidate;
                    return true;
                }
            }
            spot = Vector3.zero;
            return false;
        }

        // Straight water channel check: samples every 15 m for boat-depth water.
        private static bool IsWaterRouteClear(Vector3 from, Vector3 to)
        {
            var flat = to - from;
            flat.y = 0f;
            var dist = flat.magnitude;
            if (dist < 1f)
            {
                return true;
            }
            var dir = flat / dist;
            for (float d = 15f; d < dist; d += 15f)
            {
                if (WaterColumnDepth(from + dir * d) < MinChannelDepth)
                {
                    return false;
                }
            }
            return true;
        }

        // Shore endpoints hug the coast, so a direct line nearly always clips land.
        // Route shape: shore spot -> seaward staging point -> (detours) -> staging -> shore.
        // Only the deep-water middle is validated; the short shore legs are covered by
        // the boat's beaching tolerance.
        private static bool TryFindWaterRoute(Vector3 pickup, Vector3 dropoff, out List<Vector3> waypoints)
        {
            waypoints = new List<Vector3>();
            var stagingA = SeawardStaging(pickup);
            var stagingB = SeawardStaging(dropoff);
            var mids = new List<Vector3>();
            if (!ClearOrDetour(stagingA, stagingB, 0, mids))
            {
                return false;
            }
            waypoints.Add(stagingA);
            waypoints.AddRange(mids);
            waypoints.Add(stagingB);
            return true;
        }

        // Walks from a shore spot in the direction of fastest-deepening water until
        // it reaches ~3m depth (or 150m out) - a safe place to route from.
        private static Vector3 SeawardStaging(Vector3 spot)
        {
            var bestDir = Vector3.forward;
            var bestDepth = -1f;
            for (int i = 0; i < 8; i++)
            {
                var angle = (float)i / 8f * 2f * Mathf.PI;
                var dir = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
                var depth = WaterColumnDepth(spot + dir * 30f);
                if (depth > bestDepth)
                {
                    bestDepth = depth;
                    bestDir = dir;
                }
            }
            var staging = spot;
            for (float step = 15f; step <= 150f; step += 15f)
            {
                staging = spot + bestDir * step;
                if (WaterColumnDepth(staging) >= 3f)
                {
                    break;
                }
            }
            staging.y = 0f;
            return staging;
        }

        // Recursive poor-man's router: clear line, or split at a sideways detour
        // point in open water and route both halves. Two levels deep = up to three
        // detour waypoints, enough to round a headland or an island.
        private static bool ClearOrDetour(Vector3 from, Vector3 to, int depth, List<Vector3> acc)
        {
            if (IsWaterRouteClear(from, to))
            {
                return true;
            }
            var flat = to - from;
            flat.y = 0f;
            if (depth >= 2 || flat.magnitude < 30f)
            {
                return false;
            }
            var perpendicular = Vector3.Cross(Vector3.up, flat.normalized);
            var mid = (from + to) * 0.5f;
            foreach (var offset in new[] { 80f, -80f, 150f, -150f, 250f, -250f, 400f, -400f })
            {
                var detour = mid + perpendicular * offset;
                detour.y = 0f;
                if (WaterColumnDepth(detour) < 2f)
                {
                    continue;
                }
                var firstHalf = new List<Vector3>();
                var secondHalf = new List<Vector3>();
                if (ClearOrDetour(from, detour, depth + 1, firstHalf) &&
                    ClearOrDetour(detour, to, depth + 1, secondHalf))
                {
                    acc.AddRange(firstHalf);
                    acc.Add(detour);
                    acc.AddRange(secondHalf);
                    return true;
                }
            }
            return false;
        }

        private static float RouteLength(Vector3 from, List<Vector3> waypoints, Vector3 to)
        {
            var length = 0f;
            var current = from;
            foreach (var wp in waypoints)
            {
                length += Vector3.Distance(current, wp);
                current = wp;
            }
            return length + Vector3.Distance(current, to);
        }

        // A boat spawn ApproachDistance out with a clear channel to the pickup's
        // seaward staging point; the final shore leg rides on beaching tolerance.
        private bool TryFindBoatSpawn(Vector3 pickup, Vector3 destination, out Vector3 spawnPos, out List<Vector3> approachRoute)
        {
            // A dock pickup approaches through its own radial corridor, never the
            // depth-gradient staging (blind to the structure the dock hangs off).
            var staging = TrySnapToPad(pickup, boatPad: true, out var dock) &&
                          (dock.Position - pickup).sqrMagnitude < 1f
                ? dock.Staging
                : SeawardStaging(pickup);
            approachRoute = new List<Vector3> { staging };
            var awayFromDest = pickup - destination;
            awayFromDest.y = 0f;
            var startAngle = awayFromDest.sqrMagnitude > 1f
                ? Mathf.Atan2(awayFromDest.x, awayFromDest.z)
                : 0f;
            foreach (var dist in new[] { _config.ApproachDistance, _config.ApproachDistance * 0.5f, 100f })
            {
                for (int i = 0; i < 16; i++)
                {
                    var angle = startAngle + (float)i / 16f * 2f * Mathf.PI;
                    var dir = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
                    var candidate = staging + dir * dist;
                    if (WaterColumnDepth(candidate) >= 2f &&
                        IsWaterRouteClear(candidate, staging))
                    {
                        spawnPos = candidate;
                        return true;
                    }
                }
            }
            spawnPos = Vector3.zero;
            return false;
        }

        #endregion

        #region RoadNav

        // Road network graph built from the map's path lists (decision 0001: kinematic
        // road-follow, the Travelling Vendor's own reliable-movement technique).
        // Cars/motorcycles use roads; horses also get trails.
        private RoadNetwork _roadNet;
        private RoadNetwork _horseNet;

        private RoadNetwork GetNetwork(VehicleConfig cfg)
        {
            if (IsHorse(cfg))
            {
                return _horseNet ?? (_horseNet = RoadNetwork.Build(includeTrails: true));
            }
            return _roadNet ?? (_roadNet = RoadNetwork.Build(includeTrails: false));
        }

        private class RoadNetwork
        {
            private readonly List<Vector3> _nodes = new List<Vector3>();
            private readonly List<int> _segmentOf = new List<int>();
            private readonly List<List<int>> _edges = new List<List<int>>();

            public int NodeCount => _nodes.Count;

            public static RoadNetwork Build(bool includeTrails)
            {
                var net = new RoadNetwork();
                if (TerrainMeta.Path == null)
                {
                    return net;
                }
                var lists = new List<PathList>();
                lists.AddRange(TerrainMeta.Path.MainRoads);
                lists.AddRange(TerrainMeta.Path.SideRoads);
                if (includeTrails)
                {
                    lists.AddRange(TerrainMeta.Path.TrailRoads);
                }
                var segment = 0;
                foreach (var pathList in lists)
                {
                    var points = pathList.Path?.Points;
                    if (points == null || points.Length < 2)
                    {
                        continue;
                    }
                    var first = -1;
                    var prev = -1;
                    foreach (var point in points)
                    {
                        var idx = net.AddNode(point, segment);
                        if (prev >= 0)
                        {
                            net.Link(prev, idx);
                        }
                        else
                        {
                            first = idx;
                        }
                        prev = idx;
                    }
                    if (pathList.Path.GetStartPoint() == pathList.Path.GetEndPoint() && first >= 0 && prev >= 0)
                    {
                        net.Link(first, prev); // ring road closes on itself
                    }
                    segment++;
                }
                net.ConnectJunctions(12f);
                return net;
            }

            private int AddNode(Vector3 pos, int segment)
            {
                _nodes.Add(pos);
                _segmentOf.Add(segment);
                _edges.Add(new List<int>(2));
                return _nodes.Count - 1;
            }

            private void Link(int a, int b)
            {
                if (a == b)
                {
                    return;
                }
                if (!_edges[a].Contains(b))
                {
                    _edges[a].Add(b);
                }
                if (!_edges[b].Contains(a))
                {
                    _edges[b].Add(a);
                }
            }

            // Cross-links nodes of different road segments that sit near each other
            // (intersections/junctions), using a coarse spatial grid.
            private void ConnectJunctions(float radius)
            {
                var cell = new Dictionary<(int, int), List<int>>();
                const float cellSize = 16f;
                for (int i = 0; i < _nodes.Count; i++)
                {
                    var key = ((int)Mathf.Floor(_nodes[i].x / cellSize), (int)Mathf.Floor(_nodes[i].z / cellSize));
                    if (!cell.TryGetValue(key, out var list))
                    {
                        cell[key] = list = new List<int>();
                    }
                    list.Add(i);
                }
                var radiusSqr = radius * radius;
                for (int i = 0; i < _nodes.Count; i++)
                {
                    var cx = (int)Mathf.Floor(_nodes[i].x / cellSize);
                    var cz = (int)Mathf.Floor(_nodes[i].z / cellSize);
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (!cell.TryGetValue((cx + dx, cz + dz), out var list))
                            {
                                continue;
                            }
                            foreach (var j in list)
                            {
                                if (j <= i || _segmentOf[i] == _segmentOf[j])
                                {
                                    continue;
                                }
                                var d = _nodes[i] - _nodes[j];
                                d.y = 0f;
                                if (d.sqrMagnitude <= radiusSqr)
                                {
                                    Link(i, j);
                                }
                            }
                        }
                    }
                }
            }

            public bool TryNearest(Vector3 pos, float maxDistance, out int node)
            {
                node = -1;
                var best = maxDistance * maxDistance;
                for (int i = 0; i < _nodes.Count; i++)
                {
                    var d = _nodes[i] - pos;
                    d.y = 0f;
                    if (d.sqrMagnitude < best)
                    {
                        best = d.sqrMagnitude;
                        node = i;
                    }
                }
                return node >= 0;
            }

            public Vector3 NodePosition(int node)
            {
                return _nodes[node];
            }

            // Plain A* over the node graph. Networks are a few thousand nodes at most,
            // so the simple open-list min scan is fine.
            public bool TryFindPath(int start, int goal, out List<Vector3> path)
            {
                path = new List<Vector3>();
                if (start < 0 || goal < 0)
                {
                    return false;
                }
                var goalPos = _nodes[goal];
                var gScore = new Dictionary<int, float> { [start] = 0f };
                var fScore = new Dictionary<int, float> { [start] = Vector3.Distance(_nodes[start], goalPos) };
                var cameFrom = new Dictionary<int, int>();
                var open = new HashSet<int> { start };
                var closed = new HashSet<int>();
                while (open.Count > 0)
                {
                    var current = -1;
                    var bestF = float.MaxValue;
                    foreach (var n in open)
                    {
                        if (fScore[n] < bestF)
                        {
                            bestF = fScore[n];
                            current = n;
                        }
                    }
                    if (current == goal)
                    {
                        var walk = goal;
                        while (true)
                        {
                            path.Add(_nodes[walk]);
                            if (walk == start)
                            {
                                break;
                            }
                            walk = cameFrom[walk];
                        }
                        path.Reverse();
                        return true;
                    }
                    open.Remove(current);
                    closed.Add(current);
                    foreach (var neighbor in _edges[current])
                    {
                        if (closed.Contains(neighbor))
                        {
                            continue;
                        }
                        var tentative = gScore[current] + Vector3.Distance(_nodes[current], _nodes[neighbor]);
                        if (gScore.TryGetValue(neighbor, out var known) && tentative >= known)
                        {
                            continue;
                        }
                        cameFrom[neighbor] = current;
                        gScore[neighbor] = tentative;
                        fScore[neighbor] = tentative + Vector3.Distance(_nodes[neighbor], goalPos);
                        open.Add(neighbor);
                    }
                }
                return false;
            }

            // Greedy walk outward from `start`, never stepping toward `avoidPos` first,
            // until `distance` of road is covered. Used to stage the vehicle's arrival
            // (spawn up the road) and its departure (drive off down the road).
            public List<Vector3> WalkAway(int start, Vector3 avoidPos, float distance)
            {
                var chain = new List<Vector3>();
                var visited = new HashSet<int> { start };
                var current = start;
                var covered = 0f;
                while (covered < distance)
                {
                    var next = -1;
                    var bestScore = float.MinValue;
                    foreach (var neighbor in _edges[current])
                    {
                        if (visited.Contains(neighbor))
                        {
                            continue;
                        }
                        var d = _nodes[neighbor] - avoidPos;
                        d.y = 0f;
                        if (d.sqrMagnitude > bestScore)
                        {
                            bestScore = d.sqrMagnitude;
                            next = neighbor;
                        }
                    }
                    if (next < 0)
                    {
                        break; // dead end: spawn closer than requested
                    }
                    covered += Vector3.Distance(_nodes[current], _nodes[next]);
                    chain.Add(_nodes[next]);
                    visited.Add(next);
                    current = next;
                }
                return chain;
            }
        }

        #endregion

        #region RideManager

        private enum RidePhase
        {
            Approach,
            LandPickup,
            Boarding,
            Takeoff,
            Cruise,
            Descend,
            Depart,
            Done
        }

        private class Ride
        {
            public BasePlayer Player;
            public VehicleConfig Config;
            public BaseVehicle Vehicle;
            public RideAutopilot Autopilot;
            public HumanNPC Driver;
            public Vector3 Pickup;
            public Vector3 Destination;
            public List<Vector3> ApproachRoute = new List<Vector3>(); // boats/ground: spawn -> pickup
            public List<Vector3> DestRoute = new List<Vector3>();     // boats/ground: pickup -> destination
            public List<Vector3> DepartRoute = new List<Vector3>();   // ground: destination -> away
            public bool PassengerAboard; // aboard RIGHT NOW (cleared on bail-out)
            public bool EverBoarded;     // rode at all - the no-refund gate (island policy)
            public double PaidFare;
            public bool SuppressDismountAbort;
            public float BoardRadius = 5f;
            public float GroundClearance; // kinematic ride height above road points
            public Timer BoardingWatcher;
            public Timer NoShowTimer;
            public Timer DespawnTimer;
        }

        private readonly Dictionary<ulong, Ride> _rides = new Dictionary<ulong, Ride>();
        private readonly Dictionary<ulong, Ride> _ridesByVehicle = new Dictionary<ulong, Ride>();

        private void DispatchRide(BasePlayer player, VehicleConfig cfg, Vector3 pickup, Vector3 destination, double paidFare, List<Vector3> destRoute)
        {
            RecordRideForSurge(); // every dispatch is demand, free rides included
            EndRide(player, despawnImmediately: true, messageKey: null); // safety: one ride per player

            Vector3 spawnPos;
            var approachRoute = new List<Vector3>();
            var departRoute = new List<Vector3>();
            if (IsBoat(cfg))
            {
                if (!TryFindBoatSpawn(pickup, destination, out spawnPos, out approachRoute))
                {
                    _payment.Refund(player, paidFare);
                    Message(player, "DispatchFailed");
                    PrintWarning("Boat dispatch failed: no clear water spawn found near the pickup.");
                    return;
                }
            }
            else if (IsGround(cfg))
            {
                // Arrive along the road from the far side of the pickup, and leave
                // along the road past the destination.
                var network = GetNetwork(cfg);
                var awayFrom = destRoute != null && destRoute.Count > 0 ? destRoute[0] : destination;
                if (!network.TryNearest(pickup, 30f, out var pickupNode))
                {
                    _payment.Refund(player, paidFare);
                    Message(player, "DispatchFailed");
                    PrintWarning("Ground dispatch failed: pickup lost its road node.");
                    return;
                }
                // Chain runs outward from the pickup; the vehicle spawns at its far end
                // and drives the chain in reverse back to the pickup (which is the
                // autopilot's final target, so it is not itself a waypoint).
                var outbound = network.WalkAway(pickupNode, awayFrom, _config.ApproachDistance);
                if (outbound.Count > 0)
                {
                    spawnPos = outbound[outbound.Count - 1];
                    outbound.Reverse();
                    outbound.RemoveAt(0); // the spawn point itself
                    approachRoute = outbound;
                }
                else
                {
                    spawnPos = pickup; // dead-end road: arrive on the spot
                }
                spawnPos.y += 0.5f;
                var lastLegFrom = destRoute != null && destRoute.Count > 0 ? destRoute[destRoute.Count - 1] : pickup;
                if (network.TryNearest(destination, 30f, out var destNode))
                {
                    departRoute = network.WalkAway(destNode, lastLegFrom, _config.ApproachDistance);
                }
                if (departRoute.Count == 0 && destRoute != null)
                {
                    // Dead-end drop-off: no road onward, so drive back the way we came.
                    var covered = 0f;
                    var last = destination;
                    for (int i = destRoute.Count - 1; i >= 0 && covered < _config.ApproachDistance; i--)
                    {
                        departRoute.Add(destRoute[i]);
                        covered += Vector3.Distance(last, destRoute[i]);
                        last = destRoute[i];
                    }
                }
            }
            else
            {
                // Spawn out of sight and fly in - on the far side of the pickup from the
                // destination, so the taxi arrives, boards, and continues the same heading.
                var away = pickup - destination;
                away.y = 0f;
                var dir = away.magnitude > 1f ? away.normalized : Vector3.forward;
                spawnPos = pickup + dir * _config.ApproachDistance;
                spawnPos.y = GroundHeight(spawnPos) + cfg.CruiseAltitude;
            }

            var toPickup = pickup - spawnPos;
            toPickup.y = 0f;
            var facing = toPickup.sqrMagnitude > 1f ? Quaternion.LookRotation(toPickup.normalized) : Quaternion.identity;
            var entity = GameManager.server.CreateEntity(cfg.PrefabPath, spawnPos, facing);
            var vehicleEnt = entity as BaseVehicle;
            if (vehicleEnt == null)
            {
                entity?.Kill();
                _payment.Refund(player, paidFare);
                PrintError($"Dispatch failed: {cfg.PrefabPath} did not spawn a BaseVehicle.");
                Message(player, "DispatchFailed");
                return;
            }

            vehicleEnt.EnableSaving(false);
            vehicleEnt.Spawn();

            var ride = new Ride
            {
                Player = player,
                Config = cfg,
                Vehicle = vehicleEnt,
                Pickup = pickup,
                Destination = destination,
                PaidFare = paidFare,
                ApproachRoute = approachRoute,
                DestRoute = destRoute ?? new List<Vector3>(),
                DepartRoute = departRoute,
                BoardRadius = IsBoat(cfg) ? 10f : 5f, // boats may beach a little short
                // Road points sit at the surface; wheeled vehicles' origins ride higher
                // (they sank ~a foot at point height; 0.5 floated them - 0.25 is the
                // ride-tested sweet spot). Horses' origins are at their hooves.
                GroundClearance = IsGround(cfg) && !IsHorse(cfg) ? 0.25f : 0.05f
            };

            if (vehicleEnt is PlayerHelicopter heli)
            {
                // Engine visuals without a pilot: autoHover satisfies MeetsEngineRequirements,
                // a splash of fuel satisfies HasFuel, and fuelPerSec 0 means it never runs dry.
                heli.fuelPerSec = 0f;
                var fuelContainer = (heli.GetFuelSystem() as EntityFuelSystem)?.GetFuelContainer();
                if (fuelContainer != null)
                {
                    var fuel = ItemManager.CreateByName("lowgradefuel", 1);
                    if (fuel != null && !fuel.MoveToContainer(fuelContainer.inventory))
                    {
                        fuel.Remove();
                    }
                }
                heli.autoHover = true;
                heli.engineController.TryStartEngine(player);
                ride.Autopilot = heli.gameObject.AddComponent<HeliAutopilot>();
            }
            else if (IsGround(cfg))
            {
                if (vehicleEnt is ModularCar car)
                {
                    SetupCarModules(car, ride.Config);
                }
                if (vehicleEnt is RidableHorse horse)
                {
                    // Reserved10 = double saddle: front seat for the driver, rear for the fare.
                    SetFlagNet(horse, BaseEntity.Flags.Reserved10, true);
                    horse.UpdateMountFlags();
                }
                ride.Autopilot = vehicleEnt.gameObject.AddComponent<GroundAutopilot>();
            }
            else
            {
                ride.Autopilot = vehicleEnt.gameObject.AddComponent<BoatAutopilot>();
            }

            _rides[player.userID] = ride;
            _ridesByVehicle[vehicleEnt.net.ID.Value] = ride;
            if (IsNightTime())
            {
                SetFlagNet(vehicleEnt, BaseVehicle.Flag_Headlights, true);
            }
            // A beat later so module/saddle seats exist before the driver mounts.
            timer.Once(1f, () =>
            {
                if (_ridesByVehicle.ContainsKey(vehicleEnt.net?.ID.Value ?? 0))
                {
                    SpawnDriver(ride);
                    TryInstallCarRadio(ride);
                }
            });
            ride.Autopilot.Begin(this, ride);

            var eta = Mathf.CeilToInt(Vector3.Distance(spawnPos, pickup) / Mathf.Max(cfg.Speed, 1f));
            Message(player, "TaxiDispatched" + FlavorSuffix(cfg), cfg.DisplayName, eta);
        }

        // Taxi trim for a bare chassis: cockpit with engine up front, and the actual
        // vanilla TAXI module behind - passenger cabin with a working radio.
        private void SetupCarModules(ModularCar car, VehicleConfig cfg = null)
        {
            if (cfg?.Modules != null && cfg.Modules.Count > 0)
            {
                for (int socket = 0; socket < cfg.Modules.Count; socket++)
                {
                    TryAddCarModule(car, cfg.Modules[socket], socket);
                }
                return;
            }
            TryAddCarModule(car, "vehicle.1mod.cockpit.with.engine", 0);
            TryAddCarModule(car, "vehicle.1mod.taxi", 1);
        }

        private static readonly string[] EnginePartItems =
        {
            "carburetor1", "crankshaft1", "piston1", "sparkplug1", "valve1"
        };

        // Engine idle sound: cars need working engine internals + a driver; bikes just
        // need a driver. Our NPC driver satisfies HasDriver(), so fill parts, add fuel,
        // and turn the key.
        private void StartGroundEngine(Ride ride)
        {
            var vehicle = ride.Vehicle as GroundVehicle;
            if (vehicle == null || ride.Driver == null)
            {
                return;
            }
            if (vehicle is ModularCar car)
            {
                foreach (var moduleChild in car.children)
                {
                    if (!(moduleChild is VehicleModuleEngine engineModule))
                    {
                        continue;
                    }
                    foreach (var sub in engineModule.children)
                    {
                        if (!(sub is Rust.Modular.EngineStorage storage) || storage.inventory == null)
                        {
                            continue;
                        }
                        for (int slot = 0; slot < storage.inventory.capacity; slot++)
                        {
                            if (storage.inventory.GetSlot(slot) != null)
                            {
                                continue;
                            }
                            foreach (var partName in EnginePartItems)
                            {
                                var part = ItemManager.CreateByName(partName);
                                if (part == null)
                                {
                                    continue;
                                }
                                if (part.MoveToContainer(storage.inventory, slot))
                                {
                                    break;
                                }
                                part.Remove();
                            }
                        }
                    }
                }
            }
            var fuelContainer = (vehicle.GetFuelSystem() as EntityFuelSystem)?.GetFuelContainer();
            if (fuelContainer != null)
            {
                var fuel = ItemManager.CreateByName("lowgradefuel", 100);
                if (fuel != null && !fuel.MoveToContainer(fuelContainer.inventory))
                {
                    fuel.Remove();
                }
            }
            vehicle.engineController.TryStartEngine(ride.Driver);
        }

        private void TryAddCarModule(ModularCar car, string shortname, int socket)
        {
            var item = ItemManager.CreateByName(shortname);
            if (item == null)
            {
                PrintWarning($"Car module item \"{shortname}\" does not exist; taxi car will be missing parts.");
                return;
            }
            if (!car.TryAddModule(item, socket))
            {
                item.Remove();
                PrintWarning($"Could not attach {shortname} to taxi car socket {socket}.");
            }
        }

        private const string CarRadioPrefab = "assets/content/vehicles/modularcar/subents/modular_car_radio.prefab";

        // The Car Radio is a deployable (like a car lock): the entity parents to a
        // module. Nothing in server C# spawns it - vanilla placement is client deploy
        // data - so we parent it to the cockpit dashboard ourselves and switch it on.
        private void TryInstallCarRadio(Ride ride)
        {
            if (!(ride.Vehicle is ModularCar car))
            {
                return;
            }
            BaseVehicleModule cockpit = null;
            foreach (var child in car.children)
            {
                if (child is VehicleModuleEngine engineModule)
                {
                    cockpit = engineModule;
                    break;
                }
            }
            if (cockpit == null)
            {
                return;
            }
            var radio = GameManager.server.CreateEntity(CarRadioPrefab);
            if (radio == null)
            {
                Puts("[radio-debug] carradio.prefab failed to create");
                return;
            }
            radio.EnableSaving(false);
            radio.SetParent(cockpit, worldPositionStays: false);
            if (cockpit.ShortPrefabName.Contains("cockpit"))
            {
                // Anchor captured from a hand-deployed Car Radio on the taxi build
                // (/taxi radiopos, 2026-08-29): dash center, tilted back, facing the seats.
                radio.transform.localPosition = new Vector3(0f, 0.795f, 0.212f);
                radio.transform.localRotation = Quaternion.Euler(345.3f, 180f, 0f);
            }
            else
            {
                // Standalone engine module (armored car): the dash offset left the
                // radio floating above the bay - bury it in the engine block instead
                // (owner ask 2026-08-30); music from the engine bay is a feature.
                radio.transform.localPosition = new Vector3(0f, 0.45f, 0f);
                radio.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            }
            radio.Spawn();
            Puts($"[radio-debug] car radio deployed on {cockpit.GetType().Name}");
            timer.Once(0.5f, () => StartCabTunes(ride));
        }

        // Switches on any ModularCarRadio child found on the car's modules, tuned to
        // the company station.
        private void StartCabTunes(Ride ride)
        {
            if (!(ride.Vehicle is ModularCar car))
            {
                return;
            }
            foreach (var module in car.children)
            {
                if (!(module is BaseVehicleModule))
                {
                    continue;
                }
                foreach (var sub in module.children)
                {
                    if (sub is ModularCarRadio radio && radio.CarRadio != null)
                    {
                        ApplyRadioStation(radio.CarRadio);
                        radio.CarRadio.ServerTogglePlay(play: true);
                        return;
                    }
                }
            }
        }

        // Resolve the configured station against the game's station lists by name
        // (case-insensitive contains), or accept a raw URL/ip.
        private void ApplyRadioStation(BoomBox box)
        {
            var wanted = _config.RadioStation;
            if (box == null || string.IsNullOrWhiteSpace(wanted))
            {
                return;
            }
            string url = null;
            foreach (var stations in new[] { BoomBox.ValidStations, BoomBox.ServerValidStations })
            {
                if (stations == null)
                {
                    continue;
                }
                foreach (var kv in stations)
                {
                    if (kv.Key.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        url = kv.Value;
                        break;
                    }
                }
                if (url != null)
                {
                    break;
                }
            }
            if (url == null && wanted.Contains("."))
            {
                url = wanted; // raw URL/ip
            }
            if (url != null)
            {
                box.CurrentRadioIp = url;
            }
            else
            {
                PrintWarning($"Radio station \"{wanted}\" not found in the station lists; keeping the default.");
            }
        }

        private void DressDriver(HumanNPC npc)
        {
            foreach (var shortname in _config.DriverClothing)
            {
                if (string.IsNullOrWhiteSpace(shortname))
                {
                    continue;
                }
                // "shortname@skinid" wears a skinned variant (company collared shirt).
                var spec = shortname.Trim();
                ulong skin = 0;
                var at = spec.IndexOf('@');
                if (at > 0)
                {
                    ulong.TryParse(spec.Substring(at + 1), out skin);
                    spec = spec.Substring(0, at);
                }
                var item = ItemManager.CreateByName(spec, 1, skin);
                if (item == null)
                {
                    PrintWarning($"Driver clothing item \"{shortname}\" does not exist; skipping.");
                    continue;
                }
                if (!item.MoveToContainer(npc.inventory.containerWear))
                {
                    item.Remove();
                }
            }
            npc.SendNetworkUpdate();
        }

        // Cosmetic chauffeur: a brain-disabled scientist in the driver seat.
        private void SpawnDriver(Ride ride)
        {
            if (!_config.NpcDriver)
            {
                return;
            }
            var seat = FindDriverSeat(ride.Vehicle);
            if (seat == null)
            {
                PrintWarning($"No free driver seat on {ride.Vehicle?.ShortPrefabName}; taxi runs driverless.");
                return;
            }
            var prefab = IsBoat(ride.Config) ? BoatDriverPrefab : HeliDriverPrefab;
            var npc = GameManager.server.CreateEntity(prefab, ride.Vehicle.transform.position + Vector3.up * 0.5f) as HumanNPC;
            if (npc == null)
            {
                PrintWarning($"Driver prefab \"{prefab}\" failed to spawn; taxi runs driverless.");
                return;
            }
            npc.EnableSaving(false);
            npc.Spawn();
            npc.Brain?.SetEnabled(false); // statue driver: no AI decisions, no shooting
            npc.inventory?.Strip();       // empty hands on the wheel (also strips clothes)
            DressDriver(npc);             // company uniform from config
            ride.Driver = npc;            // set before mounting: CanMountEntity consults it
            seat.MountPlayer(npc);
            if (seat.GetMounted() != npc)
            {
                PrintWarning($"Driver mount refused on {ride.Vehicle?.ShortPrefabName}; taxi runs driverless.");
                npc.Kill();
                ride.Driver = null;
                return;
            }
            npc.transform.localPosition = Vector3.zero; // pin to the seat anchor
            if (IsGround(ride.Config) && !IsHorse(ride.Config))
            {
                StartGroundEngine(ride);
            }
            else if (IsBoat(ride.Config))
            {
                StartBoatEngine(ride);
            }
            if (ride.Vehicle is AttackHelicopter)
            {
                ProvisionGunship(ride);
            }
        }

        // Executive package: the gunner seat comes armed - an M249 in the turret and
        // company-supplied ammo. TopUpGunship keeps both magazines eternally full.
        private void ProvisionGunship(Ride ride)
        {
            var heli = ride.Vehicle as AttackHelicopter;
            var turretContainer = heli?.GetTurret();
            if (turretContainer?.inventory != null)
            {
                var hasWeapon = false;
                foreach (var item in turretContainer.inventory.itemList)
                {
                    if (item.info.category == ItemCategory.Weapon)
                    {
                        hasWeapon = true;
                        break;
                    }
                }
                if (!hasWeapon)
                {
                    var gun = ItemManager.CreateByName("lmg.m249");
                    if (gun != null && !gun.MoveToContainer(turretContainer.inventory))
                    {
                        gun.Remove();
                        PrintWarning("Could not install the turret gun (lmg.m249 rejected); gunner rides unarmed.");
                    }
                }
            }
            TopUpGunship(ride);
        }

        private void TopUpGunship(Ride ride)
        {
            var heli = ride.Vehicle as AttackHelicopter;
            if (heli == null || heli.IsDestroyed)
            {
                return;
            }
            var turretContainer = heli.GetTurret();
            if (turretContainer?.inventory != null)
            {
                TopUpAmmo(turretContainer.inventory, "ammo.rifle", 300, 128);
            }
            var rockets = heli.GetRockets();
            if (rockets?.inventory != null)
            {
                TopUpAmmo(rockets.inventory, "ammo.rocket.hv", 6, 6);
            }
        }

        private static void TopUpAmmo(ItemContainer container, string shortname, int keepAtLeast, int addAmount)
        {
            var def = ItemManager.FindItemDefinition(shortname);
            if (def == null)
            {
                return;
            }
            if (container.GetAmount(def.itemid, onlyUsableAmounts: false) >= keepAtLeast)
            {
                return;
            }
            var ammo = ItemManager.Create(def, addAmount);
            if (ammo != null && !ammo.MoveToContainer(container))
            {
                ammo.Remove();
            }
        }

        // Boat engine prep: EngineToggle refuses without real fuel in the tank
        // (HasFuel(forceCheck) reads the container - probe addendum 2026-08-30), so
        // seed a splash that never burns (fuelPerSec 0, the heli trick). The autopilot
        // keeps the engine alive from there: DriverHeartbeat + key-on every tick, and
        // real throttle now that cruise legs drive the boat through its own physics.
        private void StartBoatEngine(Ride ride)
        {
            var boat = ride.Vehicle as MotorRowboat;
            if (boat == null || ride.Driver == null)
            {
                return;
            }
            boat.fuelPerSec = 0f;
            var fuelContainer = (boat.GetFuelSystem() as EntityFuelSystem)?.GetFuelContainer();
            if (fuelContainer == null)
            {
                PrintWarning("[boat-debug] fuel container not found; engine will stay silent.");
            }
            else
            {
                var fuel = ItemManager.CreateByName("lowgradefuel", 1);
                if (fuel != null && !fuel.MoveToContainer(fuelContainer.inventory))
                {
                    fuel.Remove();
                }
            }
            // The RHIB's engine is a stateful toggle (EngineOn() == IsOn()), not
            // derived from driver+fuel - turn the key (live diagnosis 2026-08-30:
            // HasDriver=True, fuel ok, EngineOn=False).
            boat.EngineToggle(true);
        }

        // Autopilot callback: taxi has stopped at the pickup point.
        private void OnTaxiAtPickup(Ride ride)
        {
            var player = ride.Player;
            // Engine off while parked: the game's autoHover force would otherwise
            // hold the heli a meter off the ground and it never settles. The scrap
            // heli needs the engine truly stopped too - residual rotor lift kept the
            // big airframe floating above its skids, and teammates couldn't reach
            // the seats to jump on (2026-08-30). Boarding restarts the engine.
            if (ride.Vehicle is PlayerHelicopter parkedHeli && !parkedHeli.IsDestroyed)
            {
                parkedHeli.autoHover = false;
                parkedHeli.engineController?.StopEngine();
            }
            Message(player, "TaxiArrived" + FlavorSuffix(ride.Config));

            ride.BoardingWatcher = timer.Every(1f, () =>
            {
                if (ride.Vehicle == null || ride.Vehicle.IsDestroyed || player == null || !player.IsConnected)
                {
                    return; // other paths clean this up
                }
                if (Vector3.Distance(player.transform.position, ride.Vehicle.transform.position) > ride.BoardRadius)
                {
                    return;
                }
                // Boats seat the fare beside the driver: the rear bench stares into
                // the RHIB's aft running light, and "front-most" grabbed the bow seat.
                var seat = FindPassengerSeat(ride.Vehicle, nearDriver: IsBoat(ride.Config));
                if (seat == null && ride.Player.GetMountedVehicle() != ride.Vehicle)
                {
                    return;
                }
                if (seat != null)
                {
                    seat.MountPlayer(player);
                    if (seat.GetMounted() != player)
                    {
                        return; // mount refused; keep watching
                    }
                }
                ride.BoardingWatcher?.Destroy();
                ride.BoardingWatcher = null;
                ride.NoShowTimer?.Destroy();
                ride.NoShowTimer = null;
                ride.PassengerAboard = true;
                ride.EverBoarded = true;
                if (ride.Vehicle is PlayerHelicopter departingHeli)
                {
                    // Spool the engine back up, then lift off.
                    departingHeli.autoHover = true;
                    departingHeli.engineController.TryStartEngine(player);
                }
                StartCabTunes(ride); // driver puts the radio on for the fare
                Message(player, "TaxiDeparting");
                BoardTeammates(ride);
                timer.Once(3f, () =>
                {
                    BoardTeammates(ride); // last call for stragglers
                    ride.Autopilot?.BeginRide();
                });
            });

            ride.NoShowTimer = timer.Once(_config.RideTimeoutSeconds, () =>
            {
                if (ride.PassengerAboard)
                {
                    return; // boarded in the same tick the timer fired
                }
                Message(player, "TaxiNoShow");
                EndRide(player, despawnImmediately: true, messageKey: null);
            });
        }

        // Autopilot callback: taxi has stopped at the destination (or stop-in-place spot).
        private void OnTaxiLanded(Ride ride, bool completedRoute)
        {
            // Two different questions (conflating them refunded bail-outs, live
            // 2026-08-30): aboardNow (captured BEFORE dismounting clears it) drives
            // the dismount/teleport handling; EverBoarded drives the refund gate -
            // a ride that never picked its passenger up refunds (the taxi delivered
            // nothing), but a passenger who BAILED already rode. No refunds - island
            // policy - and they already got the RideBailed message mid-air.
            var aboardNow = ride.PassengerAboard;
            ride.SuppressDismountAbort = true;
            if (ride.Vehicle != null && !ride.Vehicle.IsDestroyed)
            {
                DismountPassengerOnly(ride);
                // Ground dismounts can eject INTO the vehicle (kinematic body = crush
                // risk); place the passenger safely beside it instead.
                if (aboardNow && IsGround(ride.Config) && ride.Player != null &&
                    ride.Player.IsConnected && !ride.Player.IsDead())
                {
                    var t = ride.Vehicle.transform;
                    var side = t.position + t.right * 3f;
                    side.y = GroundHeight(side) + 0.2f;
                    ride.Player.Teleport(side);
                }
            }
            if (!ride.EverBoarded)
            {
                if (ride.PaidFare > 0 && ride.Player != null)
                {
                    _payment.Refund(ride.Player, ride.PaidFare);
                }
                Message(ride.Player, "DispatchFailed");
            }
            else if (aboardNow)
            {
                Message(ride.Player, completedRoute ? "RideArrived" : "RideEndedHere");
            }
            // The ride is over for the passenger: release their booking lock now so
            // they can call again while the empty heli departs and despawns.
            if (ride.Player != null)
            {
                _rides.Remove(ride.Player.userID);
            }
            // Drop the passenger, then leave; the despawn timer kills the empty
            // vehicle out of sight. Boats linger longer - climbing off at a dock
            // ladder takes a moment, and the RHIB was pulling away underfoot.
            var departDelay = IsBoat(ride.Config) ? 9f : 4f;
            timer.Once(departDelay, () => ride.Autopilot?.BeginDeparture());
            ScheduleDespawn(ride, _config.DespawnDelaySeconds);
        }

        // Kinematic taxis generate bogus collision damage when something ends up in
        // their path (the vehicle "impacts" at speed every tick it overlaps - live
        // 2026-08-29 a bike-vs-car plow nearly killed both). Active taxi vehicles
        // shrug off Collision damage; bullets, explosions, and everything else still
        // apply - island drama stays possible.
        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (info == null || entity?.net == null ||
                !_ridesByVehicle.ContainsKey(entity.net.ID.Value))
            {
                return null;
            }
            if (info.damageTypes.Has(Rust.DamageType.Collision))
            {
                return true; // cancel
            }
            return null;
        }

        // Autopilot callback: no progress for too long while flying.
        private void OnTaxiStuck(Ride ride)
        {
            Message(ride.Player, "RideStuck");
            ride.Autopilot?.AbortAndLand();
        }

        // Autopilot callback: the empty departure leg wedged on something.
        private void OnDepartStuck(Ride ride)
        {
            ScheduleDespawn(ride, 0f);
        }

        private void ScheduleDespawn(Ride ride, float delay)
        {
            ride.DespawnTimer?.Destroy();
            ride.DespawnTimer = timer.Once(delay, () =>
            {
                RemoveRide(ride);
                if (ride.Vehicle != null && !ride.Vehicle.IsDestroyed)
                {
                    ride.Vehicle.Kill();
                }
            });
        }

        // allMountPoints (not mountPoints) also covers seats on child entities -
        // modular car modules and saddle seats live there. Default: rear-most free
        // seat (fares ride in the back cabin, not shotgun next to the driver).
        // nearDriver instead picks the free seat closest to the driver seat - for
        // the RHIB, whose rear bench faces the aft running light and whose
        // front-most seat is out on the bow.
        private static BaseMountable FindPassengerSeat(BaseVehicle vehicle, bool nearDriver = false)
        {
            BaseMountable best = null;
            var bestForwardness = float.MaxValue;
            var t = vehicle.transform;
            var driverPos = t.position;
            if (nearDriver)
            {
                foreach (var point in vehicle.allMountPoints)
                {
                    if (point.isDriver && point.mountable != null)
                    {
                        driverPos = point.mountable.transform.position;
                        break;
                    }
                }
            }
            foreach (var point in vehicle.allMountPoints)
            {
                if (point.isDriver || point.mountable == null || point.mountable.GetMounted() != null)
                {
                    continue;
                }
                var forwardness = nearDriver
                    ? Vector3.Distance(driverPos, point.mountable.transform.position)
                    : Vector3.Dot(t.forward, point.mountable.transform.position - t.position);
                if (forwardness < bestForwardness)
                {
                    bestForwardness = forwardness;
                    best = point.mountable;
                }
            }
            return best;
        }

        private static BaseMountable FindDriverSeat(BaseVehicle vehicle)
        {
            foreach (var point in vehicle.allMountPoints)
            {
                if (point.isDriver && point.mountable != null && point.mountable.GetMounted() == null)
                {
                    return point.mountable;
                }
            }
            return null;
        }

        // Dismounts every human passenger (the caller AND any teammates on a team
        // ride) - the NPC driver stays at the wheel for the departure leg.
        private static void DismountPassengerOnly(Ride ride)
        {
            if (ride.Vehicle == null || ride.Vehicle.IsDestroyed)
            {
                return;
            }
            foreach (var point in ride.Vehicle.allMountPoints)
            {
                var mounted = point.mountable?.GetMounted();
                if (mounted != null && !(mounted is HumanNPC))
                {
                    point.mountable.DismountPlayer(mounted);
                }
            }
        }

        // Central teardown. Dismounts, kills timers/autopilot, despawns now or lands first.
        private void EndRide(BasePlayer player, bool despawnImmediately, string messageKey)
        {
            if (player == null || !_rides.TryGetValue(player.userID, out var ride))
            {
                return;
            }
            if (messageKey != null)
            {
                Message(player, messageKey);
            }

            ride.BoardingWatcher?.Destroy();
            ride.NoShowTimer?.Destroy();

            if (despawnImmediately || ride.Vehicle == null || ride.Vehicle.IsDestroyed)
            {
                ride.SuppressDismountAbort = true;
                if (ride.Vehicle != null && !ride.Vehicle.IsDestroyed)
                {
                    // Never strand a passenger in mid-air (unload/shutdown/admin cancel):
                    // put them on the ground under the vehicle before it vanishes.
                    var vehiclePos = ride.Vehicle.transform.position;
                    var ground = new Vector3(vehiclePos.x, GroundHeight(vehiclePos), vehiclePos.z);
                    var airborne = vehiclePos.y - ground.y > 3f;
                    ride.Vehicle.DismountAllPlayers();
                    if (airborne && player.IsConnected && !player.IsDead())
                    {
                        player.Teleport(ground);
                    }
                }
                RemoveRide(ride);
                ride.DespawnTimer?.Destroy();
                if (ride.Vehicle != null && !ride.Vehicle.IsDestroyed)
                {
                    ride.Vehicle.Kill();
                }
            }
            else
            {
                // Land in place first - never dump a passenger out at altitude.
                // OnTaxiLanded dismounts everyone on touchdown, then despawns.
                ride.SuppressDismountAbort = true;
                ride.Autopilot?.AbortAndLand();
            }
        }

        private void RemoveRide(Ride ride)
        {
            if (ride.Player != null)
            {
                _rides.Remove(ride.Player.userID);
            }
            if (ride.Vehicle?.net != null)
            {
                _ridesByVehicle.Remove(ride.Vehicle.net.ID.Value);
            }
            if (ride.Autopilot != null)
            {
                UnityEngine.Object.Destroy(ride.Autopilot);
                ride.Autopilot = null;
            }
            if (ride.Driver != null && !ride.Driver.IsDestroyed)
            {
                ride.Driver.Kill();
            }
            ride.Driver = null;
        }

        private static float GroundHeight(Vector3 pos)
        {
            return Mathf.Max(TerrainMeta.HeightMap.GetHeight(pos), 0f);
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            // TODO remove once the phone-killer is identified (F1-reload phone loss):
            // logs the moment anything destroys the hidden taxi phone.
            if (_phone != null && _phone.IsEntity(entity))
            {
                PrintWarning($"[phone-debug] hidden taxi phone entity is being killed now (t={Time.realtimeSinceStartup:0.0}s)");
            }
            var vehicle = entity as BaseVehicle;
            if (vehicle?.net == null || !_ridesByVehicle.TryGetValue(vehicle.net.ID.Value, out var ride))
            {
                return;
            }
            // Externally destroyed (or our own Kill). No refund - price of island living.
            var wasActive = ride.Autopilot != null && ride.Autopilot.Phase != RidePhase.Done;
            RemoveRide(ride);
            ride.BoardingWatcher?.Destroy();
            ride.NoShowTimer?.Destroy();
            ride.DespawnTimer?.Destroy();
            if (wasActive)
            {
                Message(ride.Player, "RideDestroyed");
            }
        }

        // The game force-dismounts riders whose head clips terrain/walls (steep roads
        // ejected a bike driver mid-ride, 2026-08-30). Company drivers don't fall out.
        private object CanDismountEntity(BasePlayer player, BaseMountable seat)
        {
            if (player is HumanNPC)
            {
                foreach (var r in _ridesByVehicle.Values)
                {
                    if (r.Driver == player)
                    {
                        return false; // block every dismount path for on-duty drivers
                    }
                }
            }
            return null;
        }

        // Belt and braces: if a driver is ejected anyway, put them back in the seat.
        private void RecoverDriver(Ride ride)
        {
            if (ride?.Driver == null || ride.Driver.IsDestroyed ||
                ride.Vehicle == null || ride.Vehicle.IsDestroyed)
            {
                return;
            }
            var seat = FindDriverSeat(ride.Vehicle);
            if (seat == null)
            {
                return;
            }
            seat.MountPlayer(ride.Driver);
            if (seat.GetMounted() == ride.Driver)
            {
                ride.Driver.transform.localPosition = Vector3.zero;
                Puts("[ride-debug] driver recovered back into the seat");
            }
        }

        private void OnEntityDismounted(BaseMountable mount, BasePlayer player)
        {
            if (player is HumanNPC npc)
            {
                foreach (var r in _ridesByVehicle.Values)
                {
                    if (r.Driver == npc)
                    {
                        var rr = r;
                        timer.Once(0.5f, () => RecoverDriver(rr));
                        return;
                    }
                }
                return;
            }
            if (player == null || !_rides.TryGetValue(player.userID, out var ride) ||
                !ride.PassengerAboard || ride.SuppressDismountAbort)
            {
                return;
            }
            var phase = ride.Autopilot?.Phase ?? RidePhase.Done;
            if (phase == RidePhase.Takeoff || phase == RidePhase.Cruise || phase == RidePhase.Descend)
            {
                // Bailed out mid-flight: taxi lands where it is and the ride is over.
                ride.PassengerAboard = false;
                Message(player, "RideBailed");
                ride.Autopilot?.AbortAndLand();
            }
        }

        private object CanMountEntity(BasePlayer player, BaseMountable entity)
        {
            if (entity == null || player == null)
            {
                return null;
            }
            var vehicle = RootVehicleOf(entity);
            if (vehicle?.net == null || !_ridesByVehicle.TryGetValue(vehicle.net.ID.Value, out var ride))
            {
                return null;
            }
            // The booked passenger may take a passenger seat (BaseMountable.MountPlayer
            // runs this hook too, so the force-mount depends on this allowance - and the
            // passenger climbing in themselves is equally fine). The NPC driver gets the
            // driver seat. Everyone else is blocked.
            if (player == ride.Driver)
            {
                return null;
            }
            if (player == ride.Player && !IsDriverSeat(vehicle, entity))
            {
                return null;
            }
            // Team rides (scrap heli): the caller's teammates may take passenger seats.
            if (ride.Config != null && ride.Config.TeamRide &&
                !IsDriverSeat(vehicle, entity) && IsTeammate(ride.Player, player))
            {
                return null;
            }
            return false;
        }

        private static bool IsTeammate(BasePlayer owner, BasePlayer candidate)
        {
            if (owner == null || candidate == null)
            {
                return false;
            }
            if (owner == candidate)
            {
                return true;
            }
            var team = RelationshipManager.ServerInstance?.FindPlayersTeam(owner.userID);
            return team != null && team.members.Contains(candidate.userID);
        }

        // Team rides: seat every teammate standing near the taxi. Called when the
        // caller boards and again just before departure, so stragglers can hop in
        // during the countdown.
        private void BoardTeammates(Ride ride)
        {
            if (ride.Config == null || !ride.Config.TeamRide || ride.Player == null ||
                ride.Vehicle == null || ride.Vehicle.IsDestroyed)
            {
                return;
            }
            var team = RelationshipManager.ServerInstance?.FindPlayersTeam(ride.Player.userID);
            if (team == null)
            {
                return;
            }
            foreach (var memberId in team.members)
            {
                if (memberId == ride.Player.userID)
                {
                    continue;
                }
                var mate = BasePlayer.FindByID(memberId);
                if (mate == null || !mate.IsConnected || mate.IsDead() || mate.isMounted)
                {
                    continue;
                }
                if (Vector3.Distance(mate.transform.position, ride.Vehicle.transform.position) > ride.BoardRadius * 2f)
                {
                    continue;
                }
                var seat = FindPassengerSeat(ride.Vehicle);
                if (seat == null)
                {
                    return; // cab's full
                }
                seat.MountPlayer(mate);
                if (seat.GetMounted() == mate)
                {
                    Message(mate, "TeamBoarded");
                }
            }
        }

        // Taxi vehicles are company property: no looting the engine parts, fuel, or
        // storage mid-ride (a fare pulled the pistons out and stalled the cab).
        private object CanLootEntity(BasePlayer player, StorageContainer container)
        {
            var vehicle = RootVehicleOf(container);
            if (vehicle?.net != null && _ridesByVehicle.ContainsKey(vehicle.net.ID.Value))
            {
                return false;
            }
            return null;
        }

        // SAM clearance codes: launch-site and player SAMs skip active taxis.
        private object OnSamSiteTarget(SamSite samSite, SamSite.ISamSiteTarget target)
        {
            if (!_config.SamIgnoreTaxis)
            {
                return null;
            }
            var vehicle = target as BaseVehicle;
            if (vehicle?.net != null && _ridesByVehicle.ContainsKey(vehicle.net.ID.Value))
            {
                return true;
            }
            return null;
        }

        // Topmost vehicle in the parent chain: seats sit on modules (child vehicles)
        // on modular cars, so the first BaseVehicle ancestor isn't always the taxi.
        private static BaseVehicle RootVehicleOf(BaseEntity entity)
        {
            BaseVehicle found = null;
            var current = entity;
            for (int i = 0; current != null && i < 5; i++)
            {
                if (current is BaseVehicle vehicle)
                {
                    found = vehicle;
                }
                current = current.GetParentEntity();
            }
            return found;
        }

        private static bool IsDriverSeat(BaseVehicle vehicle, BaseMountable mountable)
        {
            foreach (var point in vehicle.allMountPoints)
            {
                if (point.mountable == mountable)
                {
                    return point.isDriver;
                }
            }
            return true; // unknown mountable on the vehicle: treat as off-limits
        }

        #endregion

        #region Autopilots

        // Shared chassis for per-type autopilots: phase state, stuck detection, facing,
        // and the public API the RideManager drives (BeginRide / AbortAndStop /
        // BeginDeparture).
        private abstract class RideAutopilot : MonoBehaviour
        {
            protected const float Acceleration = 12f;
            protected const float ArriveRadius = 5f;
            private const float StuckSeconds = 12f;
            private const float StuckMinMovement = 2f;

            protected IslandTaxi Plugin;
            protected Ride Ride;
            protected Rigidbody Rb;
            protected float Speed;
            protected bool AbortStop;

            private Vector3 _lastPos;
            private float _stuckTimer;

            public RidePhase Phase { get; protected set; }

            public void Begin(IslandTaxi plugin, Ride ride)
            {
                Plugin = plugin;
                Ride = ride;
                Rb = ride.Vehicle.rigidBody;
                Speed = Mathf.Max(ride.Config.Speed, 3f);
                _lastPos = transform.position;
                Phase = RidePhase.Approach;
                OnBegin();
            }

            protected virtual void OnBegin() { }

            public void BeginRide()
            {
                if (Phase == RidePhase.Boarding)
                {
                    OnLeavePickup();
                }
            }

            protected abstract void OnLeavePickup();

            public void AbortAndLand()
            {
                if (Phase == RidePhase.Done || Phase == RidePhase.Depart)
                {
                    return;
                }
                AbortStop = true;
                Phase = RidePhase.Descend;
                ResetStuckTimer(); // the abort descent gets its own stuck watchdog
            }

            public abstract void BeginDeparture();

            protected abstract void Tick();

            private void FixedUpdate()
            {
                if (Ride?.Vehicle == null || Ride.Vehicle.IsDestroyed || Phase == RidePhase.Done)
                {
                    return;
                }
                Tick();
            }

            protected void FaceVelocity(float minSpeedSqr = 4f)
            {
                var flat = Rb.linearVelocity;
                flat.y = 0f;
                if (flat.sqrMagnitude < minSpeedSqr)
                {
                    return;
                }
                var wanted = Quaternion.LookRotation(flat.normalized);
                Rb.MoveRotation(Quaternion.Slerp(Rb.rotation, wanted, Time.fixedDeltaTime * 1.5f));
            }

            protected void CheckStuck(Vector3 pos)
            {
                if (Vector3.Distance(pos, _lastPos) > StuckMinMovement)
                {
                    _lastPos = pos;
                    _stuckTimer = 0f;
                    return;
                }
                _stuckTimer += Time.fixedDeltaTime;
                if (_stuckTimer > StuckSeconds)
                {
                    _stuckTimer = float.NegativeInfinity; // fire once (until reset)
                    if (!OnStuck(pos))
                    {
                        Plugin.OnTaxiStuck(Ride);
                    }
                }
            }

            // Subclass veto: return true to swallow a stuck event (e.g. a boat beached
            // close enough to its target counts as arrived).
            protected virtual bool OnStuck(Vector3 pos)
            {
                return false;
            }

            protected void ResetStuckTimer()
            {
                _lastPos = transform.position;
                _stuckTimer = 0f;
            }

            protected static float GroundHeight(Vector3 pos)
            {
                return Mathf.Max(TerrainMeta.HeightMap.GetHeight(pos), 0f);
            }

            // Height of the actual surface under `pos` - terrain, monument structures,
            // player construction - unlike GroundHeight, which only knows the heightmap
            // and reports the ground UNDER a building's roof. Clamped to sea level.
            private static readonly int SurfaceMask = LayerMask.GetMask("Terrain", "World", "Construction");

            protected static float SurfaceHeight(Vector3 pos)
            {
                var start = new Vector3(pos.x, Mathf.Max(pos.y, GroundHeight(pos)) + 200f, pos.z);
                RaycastHit hit;
                if (Physics.Raycast(start, Vector3.down, out hit, 400f, SurfaceMask))
                {
                    return Mathf.Max(hit.point.y, 0f);
                }
                return GroundHeight(pos);
            }

            // First surface straight below `pos` (not below the heightmap ceiling like
            // SurfaceHeight - a vehicle sitting ON a roof must land on that roof).
            protected static float SurfaceBelow(Vector3 pos)
            {
                RaycastHit hit;
                if (Physics.Raycast(pos + Vector3.up * 1f, Vector3.down, out hit, 400f, SurfaceMask))
                {
                    return Mathf.Max(hit.point.y, 0f);
                }
                return GroundHeight(pos);
            }

            protected static float HorizontalDistance(Vector3 a, Vector3 b)
            {
                var dx = a.x - b.x;
                var dz = a.z - b.z;
                return Mathf.Sqrt(dx * dx + dz * dz);
            }
        }

        // Drives the helicopter's rigidbody directly (gravity off, velocity steered each
        // physics tick) through: approach -> land at pickup -> board -> takeoff -> cruise
        // -> descend -> done. The game's own flight model is rigidbody forces, so direct
        // velocity control is sanctioned and doesn't fight anything while no pilot exists.
        private class HeliAutopilot : RideAutopilot
        {
            private const float VerticalSpeed = 9f;

            private float _cruiseAlt;
            private Vector3 _departTarget;

            protected override void OnBegin()
            {
                _cruiseAlt = Mathf.Max(Ride.Config.CruiseAltitude, 15f);
                Rb.useGravity = false;
            }

            protected override void OnLeavePickup()
            {
                Rb.useGravity = false;
                Phase = RidePhase.Takeoff;
            }

            // Empty-heli exit after drop-off: climb out along the current heading
            // until the despawn timer removes it far from the drop-off.
            public override void BeginDeparture()
            {
                if (Phase != RidePhase.Done)
                {
                    return;
                }
                var flatForward = transform.forward;
                flatForward.y = 0f;
                if (flatForward.sqrMagnitude < 0.01f)
                {
                    flatForward = Vector3.forward;
                }
                var pos = transform.position;
                _departTarget = pos + flatForward.normalized * 2000f;
                _departTarget.y = pos.y + _cruiseAlt;
                Rb.useGravity = false;
                Phase = RidePhase.Depart;
                ResetStuckTimer(); // fresh watchdog for the exit leg
            }

            private void OnDestroy()
            {
                if (Rb != null)
                {
                    Rb.useGravity = true;
                }
            }

            private float _lastFlare;
            private float _lastTopUp;

            protected override void Tick()
            {
                var pos = transform.position;

                // Executive countermeasures: the seeker system sets radar warning/lock
                // flags on the heli while a homing launcher paints it (Reserved12/13);
                // the company pilot answers with flares - LaunchFlare() is the real
                // countermeasure, no ammo required.
                if (Ride.Vehicle is AttackHelicopter defHeli && !defHeli.IsDestroyed)
                {
                    if ((defHeli.HasFlag(BaseEntity.Flags.Reserved12) || defHeli.HasFlag(BaseEntity.Flags.Reserved13)) &&
                        Time.time - _lastFlare > 3f)
                    {
                        _lastFlare = Time.time;
                        defHeli.LaunchFlare();
                        Plugin.Message(Ride.Player, "Countermeasures");
                    }
                    // Gunner rockets: vanilla fires rockets from the PILOT seat, and
                    // ours is a statue - remap to the gunner's secondary fire. Rate
                    // limits and reloads stay vanilla (TryFireRocket handles them).
                    if (Ride.PassengerAboard && Ride.Player != null &&
                        Ride.Player.GetMountedVehicle() == Ride.Vehicle &&
                        Ride.Player.serverInput.IsDown(BUTTON.FIRE_SECONDARY))
                    {
                        defHeli.GetRockets()?.TryFireRocket(Ride.Player);
                    }
                    // Company pays for the ammo.
                    if (Time.time - _lastTopUp > 10f)
                    {
                        _lastTopUp = Time.time;
                        Plugin.TopUpGunship(Ride);
                    }
                }

                switch (Phase)
                {
                    case RidePhase.Approach:
                        MoveToward(CruisePointAbove(Ride.Pickup, pos));
                        CheckStuck(pos);
                        if (HorizontalDistance(pos, Ride.Pickup) < ArriveRadius)
                        {
                            Phase = RidePhase.LandPickup;
                        }
                        break;

                    case RidePhase.LandPickup:
                        CheckStuck(pos);
                        if (DescendOnto(Ride.Pickup, pos))
                        {
                            Phase = RidePhase.Boarding;
                            Rb.useGravity = true; // settle onto the skids for real
                            Plugin.OnTaxiAtPickup(Ride);
                        }
                        break;

                    case RidePhase.Boarding:
                        break; // parked under gravity; physics keeps it grounded

                    case RidePhase.Takeoff:
                        MoveToward(new Vector3(pos.x, GroundHeight(pos) + _cruiseAlt, pos.z));
                        if (pos.y >= GroundHeight(pos) + _cruiseAlt - 2f)
                        {
                            Phase = RidePhase.Cruise;
                        }
                        break;

                    case RidePhase.Cruise:
                        MoveToward(CruisePointAbove(Ride.Destination, pos));
                        CheckStuck(pos);
                        if (HorizontalDistance(pos, Ride.Destination) < ArriveRadius)
                        {
                            Phase = RidePhase.Descend;
                        }
                        break;

                    case RidePhase.Descend:
                        CheckStuck(pos);
                        // Abort lands on whatever is directly below - roof included; the
                        // heightmap would target ground UNDER a structure it is sitting on.
                        var target = AbortStop ? new Vector3(pos.x, SurfaceBelow(pos), pos.z) : Ride.Destination;
                        if (DescendOnto(target, pos))
                        {
                            Phase = RidePhase.Done;
                            Rb.linearVelocity = Vector3.zero;
                            Rb.useGravity = true; // settle instead of hovering frozen
                            Plugin.OnTaxiLanded(Ride, !AbortStop);
                        }
                        break;

                    case RidePhase.Depart:
                        // Surface-aware climb-out: the straight line at fixed altitude
                        // wedged an empty heli on a Launch Site building.
                        MoveToward(CruisePointAbove(_departTarget, pos));
                        CheckStuck(pos);
                        break;
                }
            }

            // A wedged departure has nobody aboard - no ceremony, just despawn now.
            protected override bool OnStuck(Vector3 pos)
            {
                if (Phase == RidePhase.Depart)
                {
                    Plugin.OnDepartStuck(Ride);
                    return true;
                }
                return false;
            }

            // Target point at cruise height over the leg toward `goal`, terrain-following
            // by sampling the surface here and ahead. SurfaceHeight (not GroundHeight):
            // monument buildings rise above the heightmap, and cruising at heightmap+alt
            // shaved the Launch Site rocket-assembly roof.
            private Vector3 CruisePointAbove(Vector3 goal, Vector3 pos)
            {
                var flat = goal - pos;
                flat.y = 0f;
                var dir = flat.magnitude > 1f ? flat.normalized : Vector3.zero;
                var floor = Mathf.Max(
                    SurfaceHeight(pos),
                    SurfaceHeight(pos + dir * 40f),
                    SurfaceHeight(pos + dir * 100f),
                    SurfaceHeight(goal));
                var target = goal;
                target.y = floor + _cruiseAlt;
                return target;
            }

            private void MoveToward(Vector3 target)
            {
                var toTarget = target - transform.position;
                var distance = toTarget.magnitude;
                var desired = distance < 0.1f
                    ? Vector3.zero
                    : toTarget / distance * Mathf.Min(Speed, distance * 1.2f);
                desired.y = Mathf.Clamp(desired.y, -VerticalSpeed, VerticalSpeed);
                Rb.linearVelocity = Vector3.MoveTowards(Rb.linearVelocity, desired, Acceleration * Time.fixedDeltaTime);
                FaceVelocity();
            }

            // Vertical letdown onto `spot`; returns true once settled on/near it.
            private bool DescendOnto(Vector3 spot, Vector3 pos)
            {
                var target = spot + Vector3.up * 0.5f;
                var toTarget = target - pos;
                if (toTarget.magnitude < 1.5f && Rb.linearVelocity.magnitude < 2f)
                {
                    Rb.linearVelocity = Vector3.zero;
                    return true;
                }
                var desired = toTarget.normalized * Mathf.Min(VerticalSpeed * 0.6f, toTarget.magnitude * 1.5f);
                Rb.linearVelocity = Vector3.MoveTowards(Rb.linearVelocity, desired, Acceleration * Time.fixedDeltaTime);
                return false;
            }
        }

        // Hands the autopilot's wanted controls to the boat through the vanilla
        // AI-driver seam (BaseVehicle.AddAIDriver): the game calls the 4-arg OnTick
        // every frame with ref access to the SAME steering/gasPedal fields player
        // input writes. Probe addendum 2026-08-30 (Task 9.4).
        private class BoatInputLink : IAiInputProvider
        {
            public BoatAutopilot Pilot;

            public void OnAdd(BaseVehicle vehicle) { }
            public void OnRemove(BaseVehicle vehicle) { }
            public void OnTick(BaseVehicle vehicle, float delta) { }

            public void OnTick(BaseVehicle vehicle, float delta, ref float steering, ref float gasPedal)
            {
                if (Pilot == null)
                {
                    steering = 0f;
                    gasPedal = 0f;
                    return;
                }
                steering = Pilot.WantSteering;
                gasPedal = Pilot.WantGas;
            }
        }

        // Boat autopilot v2 (Task 9.4): cruise legs drive the RHIB through its real
        // physics - throttle and rudder via the AI-driver seam - so clients see a
        // genuinely driven boat (engine pitch, wake, hull lean all correct for free).
        // Obstacle avoidance is BoatAI's context-steering recipe reimplemented here:
        // 8-direction danger map from raycasts, interest toward the goal, blur both,
        // steer toward argmax(interest - danger). Precision phases (dock approach,
        // boarding hold, final brake) keep the proven velocity station-keeping -
        // drag-only physics can't dock. Vertical stays with gravity and buoyancy.
        private class BoatAutopilot : RideAutopilot
        {
            private const float WaypointRadius = 12f;
            private const float PhysicsWaypointRadius = 18f; // real turning arcs need slack
            private const float BeachedArriveRadius = 30f; // grounded this close = arrived
            private const float AvoidRange = 30f;
            private const float AvoidInterval = 0.8f;  // BoatAI's avoidance cache cadence
            private const int AvoidMask = 1218781441;  // BoatAI's obstacle raycast mask
            private const int TriggerLayer = 18;       // triggers ignored unless tagged BoatAIAvoid

            // Read by BoatInputLink into the boat's real controls each frame.
            public float WantSteering;
            public float WantGas;

            private List<Vector3> _route;
            private Vector3 _final;
            private int _wpIndex;
            private Vector3 _departTarget;
            private bool _reversing;
            private float _reverseTimer;
            private Vector3 _departFrom;
            private Vector3 _stop;
            private bool _stopSet;

            private MotorRowboat _boat;
            private BoatInputLink _link;
            private bool _velocityFallback;
            private bool _clearingPickup; // velocity-drive the cluttered exit corridor
            private int _flipCount;       // gear flips inside the thrash window
            private float _flipWindow;
            private float _engineOffTimer;
            private float _keyTimer;
            private readonly float[] _danger = new float[8];
            private readonly float[] _interest = new float[8];
            private float _avoidTimer;
            private bool _dangerAny; // anything on the danger map this refresh?
            private float _bowBlockedDist = float.MaxValue; // nearest hit on the travel line
            private int _lastDirIndex = -1; // no hold-course bias until a first real pick
            private int _driveDirection = 1;
            private float _driveLock;
            private float _slowTimer;

            // World-fixed compass at 45-degree steps from +z, matching BoatAI's context map.
            private static readonly Vector3[] CompassDirs = BuildCompass();
            private static readonly RaycastHit[] AvoidHits = new RaycastHit[8];

            private static Vector3[] BuildCompass()
            {
                var dirs = new Vector3[8];
                for (var i = 0; i < 8; i++)
                {
                    dirs[i] = Quaternion.AngleAxis(i * 45f, Vector3.up) * Vector3.forward;
                }
                return dirs;
            }

            protected override void OnBegin()
            {
                _route = Ride.ApproachRoute ?? new List<Vector3>(); // spawn -> staging -> shore
                _final = Ride.Pickup;
                _wpIndex = 0;
                _stopSet = false;
                _boat = Ride.Vehicle as MotorRowboat;
                if (_boat == null)
                {
                    _velocityFallback = true; // not a motorboat; drive the old way
                    return;
                }
                _link = new BoatInputLink { Pilot = this };
                _boat.AddAIDriver(_link);
                _boat.UpdateMountFlags(); // HasDriver flag now reflects the AI driver
            }

            private void OnDestroy()
            {
                if (_link != null)
                {
                    _link.Pilot = null; // whatever happens next, the link writes zeros
                }
                if (_boat == null || _boat.IsDestroyed)
                {
                    return;
                }
                _boat.gasPedal = 0f;
                _boat.steering = 0f;
                if (_link != null)
                {
                    _boat.RemoveAIDriver();
                }
            }

            protected override void OnLeavePickup()
            {
                _route = Ride.DestRoute ?? new List<Vector3>();
                _final = Ride.Destination;
                _wpIndex = 0;
                _stopSet = false;
                // Back straight astern off the pickup before driving (owner call
                // 2026-08-30: the unstick's three-point turn wedged at the dock).
                // Same depart-style exit that proved out live: the bow-first approach
                // corridor is the one line we know is clear - reverse down it, then
                // VELOCITY-drive until clear of the pickup zone (ride test 4: icy
                // shoreline clutter thrashed the physics drive; the sled shrugs it
                // off). Physics takes over in open water.
                _reversing = true; // both modes: the straight-back exit is the owner call
                _clearingPickup = !_velocityFallback && _boat != null;
                _reverseTimer = 0f;
                _departFrom = transform.position;
                ResetDriveState();
                Phase = RidePhase.Cruise;
            }

            private void ResetDriveState()
            {
                _driveDirection = 1;
                _driveLock = 0f;
                _slowTimer = 0f;
            }

            // The navigation origin is the hull CENTER, so aiming it at the final
            // point shoves the front half of a ~10 m RHIB past it - up the Oil Rig
            // boat landing, live 2026-08-29. Stop with the BOW at the point instead:
            // pull the target back along the approach line by the hull half-length.
            // Frozen on first use per leg so the point doesn't wander as we close in.
            private Vector3 StopPoint(Vector3 pos)
            {
                if (!_stopSet)
                {
                    var dir = _final - pos;
                    dir.y = 0f;
                    if (dir.sqrMagnitude < 1f)
                    {
                        _stop = _final;
                    }
                    else
                    {
                        var bow = (Ride.Vehicle != null ? Mathf.Max(Ride.Vehicle.bounds.extents.z, 2f) : 4f) + 0.5f;
                        _stop = _final - dir.normalized * bow;
                    }
                    _stopSet = true;
                }
                return _stop;
            }

            public override void BeginDeparture()
            {
                if (Phase != RidePhase.Done)
                {
                    return;
                }
                // Straight astern the whole way out: the bow-first approach proved
                // that exact corridor clear, while a route-derived heading can angle
                // back ACROSS the structure we're docked at (caught the Oil Rig edge,
                // live 2026-08-29).
                var astern = -transform.forward;
                astern.y = 0f;
                if (astern.sqrMagnitude < 0.01f)
                {
                    astern = Vector3.forward;
                }
                _departTarget = transform.position + astern.normalized * 2000f;
                _departFrom = transform.position;
                _reversing = true;   // back off the dock/shore first...
                _reverseTimer = 0f;  // ...then run the same line at speed
                ResetDriveState();
                Phase = RidePhase.Depart;
                ResetStuckTimer(); // fresh watchdog for the exit leg
            }

            // A boat that grinds to a halt close to its goal is just beached at the
            // shoreline - that IS the stop, not a failure. A wedged empty departure
            // despawns immediately. Stuck mid-route in physics mode gets one more
            // life: downgrade to the velocity sled and drive on; only a stuck SLED
            // aborts the ride.
            protected override bool OnStuck(Vector3 pos)
            {
                if (Phase == RidePhase.Depart)
                {
                    Plugin.OnDepartStuck(Ride);
                    return true;
                }
                if (HorizontalDistance(pos, _final) <= BeachedArriveRadius)
                {
                    ResetStuckTimer();
                    if (Phase == RidePhase.Approach)
                    {
                        Phase = RidePhase.LandPickup;
                    }
                    else if (Phase == RidePhase.Cruise)
                    {
                        Phase = RidePhase.Descend;
                    }
                    return true;
                }
                if (!_velocityFallback)
                {
                    DowngradeToVelocity("stuck mid-route under physics drive");
                    ResetStuckTimer();
                    return true;
                }
                return false; // the sled is stuck too: normal abort
            }

            protected override void Tick()
            {
                var pos = transform.position;
                KeepEngineAlive();

                switch (Phase)
                {
                    case RidePhase.Approach:
                        NavigateRoute(pos, RidePhase.LandPickup);
                        CheckStuck(pos);
                        break;

                    case RidePhase.LandPickup:
                        WantGas = 0f; // idle at the dock; station-keeping steers
                        WantSteering = 0f;
                        HoldStation(StopPoint(pos), pos);
                        if (FlatSpeed() < 0.8f)
                        {
                            Phase = RidePhase.Boarding;
                            Plugin.OnTaxiAtPickup(Ride);
                        }
                        break;

                    case RidePhase.Boarding:
                        WantGas = 0f;
                        WantSteering = 0f;
                        HoldStation(StopPoint(pos), pos);
                        break;

                    case RidePhase.Takeoff: // unused for boats; OnLeavePickup goes straight to Cruise
                        Phase = RidePhase.Cruise;
                        break;

                    case RidePhase.Cruise:
                        if (_clearingPickup && !_reversing &&
                            (_wpIndex > 0 || HorizontalDistance(pos, _departFrom) > 60f))
                        {
                            _clearingPickup = false; // clear of the pickup zone; physics takes over
                        }
                        if (_reversing)
                        {
                            ReverseOut(pos);
                        }
                        else
                        {
                            NavigateRoute(pos, RidePhase.Descend);
                        }
                        CheckStuck(pos);
                        break;

                    case RidePhase.Descend: // braking to a stop at the final point
                        WantGas = 0f;
                        WantSteering = 0f;
                        MoveHorizontal(StopPoint(pos), pos, 1.2f);
                        if (FlatSpeed() < 0.8f)
                        {
                            Phase = RidePhase.Done;
                            Plugin.OnTaxiLanded(Ride, !AbortStop);
                        }
                        break;

                    case RidePhase.Depart:
                        if (_reversing)
                        {
                            ReverseOut(pos);
                        }
                        else
                        {
                            Propel(_departTarget, pos, Speed);
                        }
                        CheckStuck(pos);
                        break;
                }
            }

            // Engine keep-alive (probe addendum 2026-08-30): Flags.On is recomputed every
            // FixedUpdate and eligibility dies 75 s after the last DriverHeartbeat(), so
            // both the heartbeat and the key get fed every tick. A toggle that STAYS
            // refused in a drive phase (another plugin vetoing OnEngineStart) downgrades
            // the ride to the old velocity drive instead of leaving it dead in the water.
            private void KeepEngineAlive()
            {
                if (_boat == null || _boat.IsDestroyed)
                {
                    return;
                }
                _boat.DriverHeartbeat();
                if (_boat.IsOn())
                {
                    _engineOffTimer = 0f;
                    return;
                }
                _keyTimer -= Time.fixedDeltaTime;
                if (_keyTimer <= 0f)
                {
                    _keyTimer = 1f; // retry the key once a second, not per physics tick
                    _boat.EngineToggle(true);
                }
                var drivePhase = Phase == RidePhase.Approach || Phase == RidePhase.Cruise || Phase == RidePhase.Depart;
                if (_velocityFallback || !drivePhase)
                {
                    return;
                }
                _engineOffTimer += Time.fixedDeltaTime;
                if (_engineOffTimer > 3f)
                {
                    DowngradeToVelocity("engine refused to start for 3 s (OnEngineStart veto elsewhere?)");
                }
            }

            // Straight astern, no steering, until well clear of the dock/shallows.
            private void ReverseOut(Vector3 pos)
            {
                _reverseTimer += Time.fixedDeltaTime;
                var cleared = HorizontalDistance(pos, _departFrom);
                if (_reverseTimer > 12f || cleared > 35f)
                {
                    _reversing = false;
                    // Timed out without moving: a beached bow the propeller can't
                    // pull free. The old velocity drive drags free where physics
                    // can't - downgrade this ride instead of letting it wedge.
                    if (!_velocityFallback && cleared < 5f)
                    {
                        DowngradeToVelocity("reverse-out gained under 5 m");
                    }
                    return;
                }
                if (!_velocityFallback)
                {
                    WantSteering = 0f;
                    WantGas = -0.5f; // real reverse gear
                    return;
                }
                WantGas = -0.4f; // audio only; velocity does the work below
                var astern = -transform.forward;
                astern.y = 0f;
                if (astern.sqrMagnitude < 0.01f)
                {
                    return;
                }
                var current = Rb.linearVelocity;
                var desired = astern.normalized * 3f;
                var horizontal = Vector3.MoveTowards(
                    new Vector3(current.x, 0f, current.z), desired, Acceleration * Time.fixedDeltaTime);
                Rb.linearVelocity = new Vector3(horizontal.x, current.y, horizontal.z);
            }

            private void NavigateRoute(Vector3 pos, RidePhase arrivalPhase)
            {
                if (AbortStop)
                {
                    Phase = RidePhase.Descend;
                    _final = pos;
                    _stop = pos;
                    _stopSet = true;
                    return;
                }
                var onWaypoint = _wpIndex < (_route?.Count ?? 0);
                var target = onWaypoint ? _route[_wpIndex] : StopPoint(pos);
                // Physics mode hands off to the velocity brake earlier: throttle-off
                // decel is drag-only, so the boat reaches the old 7 m handoff still
                // carrying real momentum. 15 m gives the brake room to absorb it.
                var arriveAt = onWaypoint
                    ? (_velocityFallback ? WaypointRadius : PhysicsWaypointRadius)
                    : (_velocityFallback ? ArriveRadius + 2f : 15f);
                var distance = HorizontalDistance(pos, target);
                if (distance < arriveAt)
                {
                    if (onWaypoint)
                    {
                        _wpIndex++;
                    }
                    else
                    {
                        Phase = arrivalPhase;
                    }
                    return;
                }
                // Ease off over the last stretch to the final point: arriving with
                // full momentum rides the bow up whatever the point sits against
                // (Oil Rig boat landing, live 2026-08-29). Beaches forgave this.
                var cap = onWaypoint
                    ? Speed
                    : Mathf.Min(Speed, (distance - ArriveRadius) * 0.5f + 1.5f);
                Propel(target, pos, cap);
            }

            // Cruise propulsion: real throttle/rudder when the engine drives, the old
            // direct velocity control when it can't - or while threading the
            // cluttered pickup exit, where the sled is the proven tool.
            private void Propel(Vector3 target, Vector3 pos, float speedCap)
            {
                if (_velocityFallback || _clearingPickup)
                {
                    WantGas = Phase == RidePhase.Depart ? 0.7f : 0.5f; // audio only
                    MoveHorizontal(target, pos, speedCap);
                    return;
                }
                DriveToward(target, pos, speedCap);
            }

            // One-way downgrade to the old velocity drive for the rest of the ride.
            private void DowngradeToVelocity(string reason)
            {
                if (_velocityFallback)
                {
                    return;
                }
                _velocityFallback = true;
                Plugin.PrintWarning($"[boat] {reason} - velocity drive for the rest of this ride.");
            }

            // Physics drive: context-steer around obstacles toward `target`, throttle
            // governed to `speedCap`, with BoatAI's reverse-out unstick maneuver.
            private void DriveToward(Vector3 target, Vector3 pos, float speedCap)
            {
                var desired = target - pos;
                desired.y = 0f;
                if (desired.sqrMagnitude < 0.25f)
                {
                    WantGas = 0f;
                    WantSteering = 0f;
                    return;
                }
                desired.Normalize();
                UpdateDangerMap(pos);
                // Drive the TRUE bearing while the water is clear - quantizing the
                // goal into 45-degree compass buckets made the boat serpentine on
                // open water (ride test 2, 2026-08-30). The context map only takes
                // the wheel when a raycast actually registered danger, and even then
                // only if it picks a genuinely different direction than the goal.
                var dir = desired;
                if (_dangerAny)
                {
                    var best = ChooseDirectionIndex(desired);
                    if (best < 0)
                    {
                        WantGas = 0f; // boxed in on all sides; the stuck watchdog decides
                        WantSteering = 0f;
                        return;
                    }
                    if (best != DirIndex(desired))
                    {
                        dir = CompassDirs[best];
                    }
                }
                WantSteering = SteerToward(pos + dir * 10f);

                // Short-horizon unstick (BoatAI's recipe): wants to move but barely
                // moving for a second, and the way out is behind -> reverse gear until
                // the heading recovers. The 12 s watchdog stays as the backstop.
                var flatSpeed = FlatSpeed();
                _slowTimer = flatSpeed < 0.6f ? _slowTimer + Time.fixedDeltaTime : 0f;
                var align = Vector3.Dot(transform.forward, dir);
                if (_driveLock <= 0f)
                {
                    if (_driveDirection == 1 && _slowTimer >= 1f && align < -0.4f)
                    {
                        _driveDirection = -1;
                        _driveLock = 0.4f;
                        // Thrash detector (ride test 4, the Austin Powers hallway
                        // loop): ram-reverse-ram cycles move >2 m per swing, so the
                        // 12 s stuck watchdog never fires. Three reverse gear drops
                        // inside 20 s means the physics drive is beaten - hand the
                        // ride to the sled, which grinds past what propellers can't.
                        if (Time.time - _flipWindow > 20f)
                        {
                            _flipWindow = Time.time;
                            _flipCount = 0;
                        }
                        if (++_flipCount >= 3)
                        {
                            DowngradeToVelocity("physics drive thrashing (3 reverse cycles in 20 s)");
                            return;
                        }
                    }
                    else if (_driveDirection == -1 && align > -0.15f)
                    {
                        _driveDirection = 1;
                        _driveLock = 0.4f;
                    }
                }
                else
                {
                    _driveLock -= Time.fixedDeltaTime;
                }
                if (_driveDirection < 0)
                {
                    WantSteering = -WantSteering;
                    WantGas = -0.5f;
                    return;
                }
                // Throttle: proportional governor toward the cap, eased while the bow
                // is way off the wanted heading so turns stay tight.
                var governor = Mathf.Clamp01((speedCap - flatSpeed) / Mathf.Max(speedCap, 1f) * 3f);
                WantGas = governor * Mathf.Clamp01(0.35f + 0.65f * Mathf.Max(align, 0f));
                // Something on the travel line: shed speed as it closes, but keep
                // enough prop wash to hold rudder authority through the swerve.
                if (_bowBlockedDist < 60f)
                {
                    var brake = Mathf.Clamp01((_bowBlockedDist - 8f) / 25f);
                    WantGas = Mathf.Min(WantGas, Mathf.Max(brake, 0.15f));
                }
            }

            // Rudder toward a world point: proportional to heading error (full lock
            // at 35 degrees off) with a touch of yaw damping. The raw local-x rudder
            // saturated at ~1 m of lateral offset - bang-bang steering that overshot
            // and serpentined at cruise speed (ride test 2, 2026-08-30).
            private float SteerToward(Vector3 target)
            {
                var local = transform.InverseTransformPoint(target);
                if (local.z < 0f)
                {
                    return local.x >= 0f ? -1f : 1f; // behind us: full lock toward it
                }
                var angle = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg; // + = target to starboard
                var steer = Mathf.Clamp(-angle / 35f, -1f, 1f);
                steer += Mathf.Clamp(Rb.angularVelocity.y * 0.5f, -0.4f, 0.4f); // oppose the swing
                return Mathf.Clamp(steer, -1f, 1f);
            }

            // Refresh the 8-direction danger map from raycasts on BoatAI's cadence.
            private void UpdateDangerMap(Vector3 pos)
            {
                _avoidTimer -= Time.fixedDeltaTime;
                if (_avoidTimer > 0f)
                {
                    return;
                }
                _avoidTimer = AvoidInterval;
                _dangerAny = false;
                for (var i = 0; i < 8; i++)
                {
                    _danger[i] = 0f;
                }
                for (var i = 0; i < 8; i++)
                {
                    var count = Physics.RaycastNonAlloc(pos, CompassDirs[i], AvoidHits, AvoidRange, AvoidMask, QueryTriggerInteraction.Collide);
                    var closest = float.MaxValue;
                    for (var h = 0; h < count; h++)
                    {
                        var hit = AvoidHits[h];
                        if (hit.collider == null)
                        {
                            continue;
                        }
                        // Trigger volumes only count when vanilla marked them as boat
                        // hazards; our own hull and mounted crew never count.
                        if (hit.collider.gameObject.layer == TriggerLayer && !hit.collider.CompareTag("BoatAIAvoid"))
                        {
                            continue;
                        }
                        if (Ride.Vehicle != null && hit.collider.transform.root == Ride.Vehicle.transform.root)
                        {
                            continue;
                        }
                        if (hit.distance < closest)
                        {
                            closest = hit.distance;
                        }
                    }
                    if (closest < AvoidRange)
                    {
                        AddSpread(_danger, i, Mathf.Clamp01(1f - closest / AvoidRange), 1);
                        _dangerAny = true;
                    }
                }

                // Bow lookahead: a hazard dead on the travel line can sit BETWEEN two
                // compass rays until point-blank - at 30 m the ray tips are ~23 m
                // apart, and an iceberg fit through the gap (ride test 3, 2026-08-30).
                // A hull-width sphere-cast along the actual direction of travel,
                // ranged by speed, closes the gap. Its danger is written wide and
                // strong enough that the goal bucket can never outvote it.
                _bowBlockedDist = float.MaxValue;
                var bowDir = Rb.linearVelocity;
                bowDir.y = 0f;
                if (bowDir.sqrMagnitude > 1f)
                {
                    bowDir.Normalize();
                }
                else
                {
                    bowDir = transform.forward;
                    bowDir.y = 0f;
                    bowDir = bowDir.sqrMagnitude > 0.01f ? bowDir.normalized : Vector3.forward;
                }
                var lookahead = Mathf.Clamp(FlatSpeed() * 4f, 20f, 60f);
                var bowCount = Physics.SphereCastNonAlloc(pos, 2.5f, bowDir, AvoidHits, lookahead, AvoidMask, QueryTriggerInteraction.Collide);
                var bowClosest = float.MaxValue;
                for (var h = 0; h < bowCount; h++)
                {
                    var hit = AvoidHits[h];
                    if (hit.collider == null)
                    {
                        continue;
                    }
                    if (hit.collider.gameObject.layer == TriggerLayer && !hit.collider.CompareTag("BoatAIAvoid"))
                    {
                        continue;
                    }
                    if (Ride.Vehicle != null && hit.collider.transform.root == Ride.Vehicle.transform.root)
                    {
                        continue;
                    }
                    if (hit.distance < bowClosest)
                    {
                        bowClosest = hit.distance;
                    }
                }
                if (bowClosest < lookahead)
                {
                    _bowBlockedDist = bowClosest;
                    AddSpread(_danger, DirIndex(bowDir), 1.5f * Mathf.Clamp01(1.2f - bowClosest / lookahead), 2);
                    _dangerAny = true;
                }
            }

            // Blend interest (goal pull, wide spread so a detour always scores above a
            // dead stop) against danger, blur both, take the best direction's index.
            // -1 means every direction loses to danger.
            private int ChooseDirectionIndex(Vector3 desired)
            {
                for (var i = 0; i < 8; i++)
                {
                    _interest[i] = 0f;
                }
                AddSpread(_interest, DirIndex(desired), 1f, 4);
                var bestScore = 0.01f;
                var best = -1;
                for (var i = 0; i < 8; i++)
                {
                    var prev = (i + 7) % 8;
                    var next = (i + 1) % 8;
                    var interest = (_interest[prev] + _interest[i] + _interest[next]) / 3f;
                    var danger = (_danger[prev] + _danger[i] + _danger[next]) / 3f;
                    var score = interest - danger;
                    if (i == _lastDirIndex)
                    {
                        score += 0.05f; // hold course unless another way is clearly better
                    }
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = i;
                    }
                }
                _lastDirIndex = best >= 0 ? best : _lastDirIndex;
                return best;
            }

            private static void AddSpread(float[] map, int index, float strength, int spread)
            {
                for (var i = -spread; i <= spread; i++)
                {
                    var slot = (index + i + 8) % 8;
                    map[slot] += strength * (1f - Mathf.Abs(i) / (float)(spread + 1));
                }
            }

            private static int DirIndex(Vector3 dir)
            {
                var angle = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg; // 0 = +z, 90 = +x
                var idx = Mathf.RoundToInt(angle / 45f);
                return (idx % 8 + 8) % 8;
            }

            private void HoldStation(Vector3 point, Vector3 pos)
            {
                MoveHorizontal(point, pos, 1.5f);
            }

            private void MoveHorizontal(Vector3 target, Vector3 pos, float speedCap)
            {
                var flat = target - pos;
                flat.y = 0f;
                var distance = flat.magnitude;
                var desired = distance < 0.5f
                    ? Vector3.zero
                    : flat / distance * Mathf.Min(speedCap, distance * 0.8f);
                var current = Rb.linearVelocity;
                var horizontal = Vector3.MoveTowards(
                    new Vector3(current.x, 0f, current.z), desired, Acceleration * Time.fixedDeltaTime);
                Rb.linearVelocity = new Vector3(horizontal.x, current.y, horizontal.z);
                FaceVelocity(1f);
            }

            private float FlatSpeed()
            {
                var v = Rb.linearVelocity;
                return Mathf.Sqrt(v.x * v.x + v.z * v.z);
            }
        }

        // Ground autopilot: kinematic road-follow (decision 0001, the Travelling
        // Vendor's monument-spline technique generalized). The rigidbody is kinematic
        // and the transform is stepped along the road waypoints - it cannot get stuck,
        // cannot flip, and never fights vehicle physics.
        private class GroundAutopilot : RideAutopilot
        {
            private const float WaypointRadius = 3f;
            private const float BikeTelemetryInterval = 0.1f; // vanilla UpdateClients cadence

            private List<Vector3> _route;
            private Vector3 _final;
            private int _wpIndex;
            private int _departIndex;
            private float _bikeRpcTimer;
            private Vector3 _bikeLastPos;

            protected override void OnBegin()
            {
                if (Rb != null)
                {
                    Rb.isKinematic = true;
                }
                _route = Ride.ApproachRoute ?? new List<Vector3>();
                _final = Ride.Pickup;
                _wpIndex = 0;
                MuteVanillaBikeTelemetry();
                _bikeLastPos = transform.position;
            }

            // The client animates the rider (lean, balance, wheel spin) from a 10 Hz
            // BikeUpdate RPC the server sends while a driver is aboard - with values
            // read from CarPhysics, which is DEAD under kinematic driving. So vanilla
            // keeps broadcasting "speed 0, steer 0" while the bike glides: that's the
            // rider judder. Cancel vanilla's sender (its InvokeRandomized delegate is
            // matched by target+method equality) and broadcast our own telemetry.
            private void MuteVanillaBikeTelemetry()
            {
                if (!(Ride.Vehicle is Bike bike))
                {
                    return;
                }
                var method = typeof(Bike).GetMethod("UpdateClients",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (method == null)
                {
                    Plugin.PrintWarning("Bike.UpdateClients not found; rider animation may judder.");
                    return;
                }
                bike.CancelInvoke((Action)Delegate.CreateDelegate(typeof(Action), bike, method));
            }

            // Tier 3 of the judder fight (2026-08-30): with vanilla's zero-value
            // broadcaster muted, send NOTHING during the ride - synthetic telemetry
            // (raw, then smoothed) still read as jerky drive + revving + squeal.
            // The parked frame at route end still goes out.
            private const bool BikeTelemetrySilent = true;

            private void SendBikeTelemetry()
            {
                if (BikeTelemetrySilent || _telemetryFailed || !(Ride.Vehicle is Bike bike))
                {
                    return;
                }
                _bikeRpcTimer += Time.fixedDeltaTime;
                if (_bikeRpcTimer < BikeTelemetryInterval)
                {
                    return;
                }
                var pos = transform.position;
                // Moving = actually covering ground this interval; but REPORT the
                // configured cruise speed, not the measurement - measured speed is
                // noisy at 10 Hz and any wobble reads to the client as wheel slip
                // (engine revving + tire squeal, live 2026-08-30). Constant values
                // cannot oscillate.
                var moved = Vector3.Distance(pos, _bikeLastPos);
                _bikeRpcTimer = 0f;
                _bikeLastPos = pos;
                var moving = moved > 0.05f;

                // Steer toward the current waypoint, but SMOOTHED across sends and
                // kept gentle - raw per-send angles jump at every waypoint switch.
                var steerTarget = 0f;
                if (moving && _wpIndex < (_route?.Count ?? 0))
                {
                    var toTarget = _route[_wpIndex] - pos;
                    toTarget.y = 0f;
                    if (toTarget.sqrMagnitude > 0.25f)
                    {
                        steerTarget = Mathf.Clamp(
                            Vector3.SignedAngle(transform.forward, toTarget.normalized, Vector3.up),
                            -20f, 20f);
                    }
                }
                _bikeSteer = Mathf.Lerp(_bikeSteer, steerTarget, 0.25f);

                // Vanilla encoding: throttle -1..1 -> (t+1)*7, brake 0..1 -> b*15<<4.
                var throttle = (byte)((Mathf.Clamp01(moving ? 0.6f : 0f) + 1f) * 7f);
                var brake = (byte)((moving ? 0 : 15) << 4);
                var throttleAndBrake = (byte)(throttle + brake);
                var wheelVel = moving ? Speed : 0f;
                SendBikeRpc(bike, _bikeSteer, throttleAndBrake, wheelVel);
            }

            private float _bikeSteer;

            // Parked-and-braked frame, sent when a route completes: Tick stops running
            // in Done, and without this the client holds the last moving pose.
            private void SendBikeStopped()
            {
                if (Ride.Vehicle is Bike bike)
                {
                    SendBikeRpc(bike, 0f, (byte)(7 + (15 << 4)), 0f);
                }
            }

            private static void SendBikeRpc(Bike bike, float steer, byte throttleAndBrake, float wheelVel)
            {
                if (bike.hasSidecar)
                {
                    bike.ClientRPC(RpcTarget.NetworkGroup("BikeUpdateSC"), bike.GetNetworkTime(),
                        steer, throttleAndBrake, wheelVel, bike.GetFuelFraction(), bike.SidecarAngle);
                }
                else
                {
                    bike.ClientRPC(RpcTarget.NetworkGroup("BikeUpdate"), bike.GetNetworkTime(),
                        steer, throttleAndBrake, wheelVel, bike.GetFuelFraction());
                }
            }

            protected override void OnLeavePickup()
            {
                _route = Ride.DestRoute ?? new List<Vector3>();
                _final = Ride.Destination;
                _wpIndex = 0;
                Phase = RidePhase.Cruise;
            }

            public override void BeginDeparture()
            {
                if (Phase != RidePhase.Done || AbortStop ||
                    Ride.DepartRoute == null || Ride.DepartRoute.Count == 0)
                {
                    return; // nowhere sensible to go; the despawn timer handles it
                }
                _departIndex = 0;
                Phase = RidePhase.Depart;
            }

            private void OnDestroy()
            {
                if (Rb != null)
                {
                    Rb.isKinematic = false;
                }
            }

            protected override void Tick()
            {
                try
                {
                    SendBikeTelemetry();
                }
                catch (Exception e)
                {
                    if (!_telemetryFailed)
                    {
                        _telemetryFailed = true;
                        Plugin.PrintError($"Bike telemetry failed (disabled for this ride): {e.Message}");
                    }
                }
                try
                {
                    TickPhases();
                }
                catch (Exception e)
                {
                    if (Time.time - _lastTickError > 10f)
                    {
                        _lastTickError = Time.time;
                        Plugin.PrintError("[ground-debug] Tick exception: " + e);
                    }
                }
            }

            private float _lastTickError;

            private void TickPhases()
            {
                switch (Phase)
                {
                    case RidePhase.Approach:
                        if (DriveRoute())
                        {
                            Phase = RidePhase.Boarding;
                            Plugin.OnTaxiAtPickup(Ride);
                        }
                        break;

                    case RidePhase.LandPickup: // unused on the ground
                        Phase = RidePhase.Boarding;
                        break;

                    case RidePhase.Boarding:
                        break; // parked at the curb

                    case RidePhase.Takeoff: // unused on the ground
                        Phase = RidePhase.Cruise;
                        break;

                    case RidePhase.Cruise:
                        if (DriveRoute())
                        {
                            SendBikeStopped();
                            Phase = RidePhase.Done;
                            Plugin.OnTaxiLanded(Ride, completedRoute: true);
                        }
                        break;

                    case RidePhase.Descend: // abort: stop where we are
                        Phase = RidePhase.Done;
                        Plugin.OnTaxiLanded(Ride, completedRoute: false);
                        break;

                    case RidePhase.Depart:
                        if (_departIndex < Ride.DepartRoute.Count)
                        {
                            var departPos = transform.position;
                            CheckStuck(departPos);
                            if (!LaneClear(departPos, Ride.DepartRoute[_departIndex]))
                            {
                                break; // hold; a permanent block despawns via OnStuck
                            }
                            if (StepToward(Ride.DepartRoute[_departIndex], WaypointRadius))
                            {
                                _departIndex++;
                            }
                        }
                        break; // out of road: idle until despawn
                }
            }

            private bool DriveRoute()
            {
                if (AbortStop)
                {
                    Phase = RidePhase.Descend;
                    return false;
                }
                var pos = transform.position;
                CheckStuck(pos);
                var onWaypoint = _wpIndex < (_route?.Count ?? 0);
                var target = onWaypoint ? _route[_wpIndex] : _final;

                // Kinematic driving feels no obstacles: without this it plows through
                // parked vehicles and players on the road (live 2026-08-29: bike vs
                // junk car, both nearly died). Junk cars SPAWN on the roadbed and
                // never move, so a blocked lane dodges to a point ABREAST of the
                // obstacle (offsetting the far waypoint doesn't separate the lanes
                // near the obstacle - rev 1 held forever and aborted rides); only a
                // full blockade holds (stuck watchdog -> "setting down here").
                if (!LaneClear(pos, target))
                {
                    var ahead = target - pos;
                    ahead.y = 0f;
                    var dir = ahead.sqrMagnitude > 0.01f ? ahead.normalized : transform.forward;
                    var right = Vector3.Cross(Vector3.up, dir);
                    foreach (var offset in SwerveOffsets)
                    {
                        var dodge = pos + dir * 4f + right * offset;
                        dodge.y = target.y;
                        if (LaneClear(pos, dodge))
                        {
                            StepToward(dodge, 1f);
                            return false; // dodging; waypoint progress resumes when the lane clears
                        }
                    }
                    return false; // fully blocked: hold position
                }
                if (StepToward(target, onWaypoint ? WaypointRadius : 1.5f))
                {
                    if (onWaypoint)
                    {
                        _wpIndex++;
                        return false;
                    }
                    return true;
                }
                return false;
            }

            private static readonly float[] SwerveOffsets = { 3f, -3f, 5f, -5f };
            private static readonly int ObstacleMask =
                LayerMask.GetMask("Vehicle_World", "Player (Server)", "Deployed");

            // Is the lane from pos toward target clear of vehicles/players/deployed
            // things? Hits belonging to this ride (the taxi itself, its mounted
            // driver/passenger) don't count.
            private bool LaneClear(Vector3 pos, Vector3 target)
            {
                var dir = target - pos;
                dir.y = 0f;
                var dist = dir.magnitude;
                if (dist < 0.5f)
                {
                    return true; // on top of the target: nothing to sweep
                }
                dir /= dist;
                var origin = pos + Vector3.up * 0.8f;
                var hits = Physics.SphereCastAll(origin, 1.1f, dir, Mathf.Min(dist, 5f), ObstacleMask);
                foreach (var hit in hits)
                {
                    var ent = hit.GetEntity();
                    if (ent == null || (ent == Ride.Driver) ||
                        (Ride.Player != null && ent == Ride.Player))
                    {
                        continue;
                    }
                    var mounted = (ent as BasePlayer)?.GetMountedVehicle();
                    if (mounted != null && mounted == Ride.Vehicle)
                    {
                        continue;
                    }
                    // Walk the whole parent chain: seats and modules are child
                    // ENTITIES, sometimes nested (seat -> module -> vehicle), and a
                    // one-level check left the taxi permanently "blocked" by its own
                    // sidecar seat (every bike ride aborted, live 2026-08-29).
                    var chain = ent;
                    var ownPart = false;
                    while (chain != null)
                    {
                        if (chain == Ride.Vehicle)
                        {
                            ownPart = true;
                            break;
                        }
                        chain = chain.GetParentEntity();
                    }
                    if (ownPart)
                    {
                        continue;
                    }
                    if (Time.time - _lastBlockLog > 5f)
                    {
                        _lastBlockLog = Time.time;
                        Plugin.Puts($"[ground-debug] lane blocked by {ent.ShortPrefabName} ({ent.GetType().Name}) at {hit.distance:0.0}m");
                    }
                    return false; // blocker found: lane NOT clear
                }
                return true; // nothing in the lane
            }

            private float _lastBlockLog;
            private float _lastDebug;
            private bool _telemetryFailed;

            // A wedged empty departure just idles until despawn; anything else goes
            // through the normal stuck abort ("setting down here").
            protected override bool OnStuck(Vector3 pos)
            {
                if (Phase == RidePhase.Depart)
                {
                    Plugin.OnDepartStuck(Ride);
                    return true;
                }
                return false;
            }

            // Kinematic step along the road; returns true when within arriveAt.
            // MovePosition/MoveRotation (not raw transform writes) so clients get
            // interpolated motion - raw writes made mounted NPCs judder.
            private bool StepToward(Vector3 target, float arriveAt)
            {
                target.y += Ride.GroundClearance;
                var pos = transform.position;
                if (HorizontalDistance(pos, target) < arriveAt)
                {
                    return true;
                }
                var next = Vector3.MoveTowards(pos, target, Speed * Time.fixedDeltaTime);
                var rotation = transform.rotation;
                var toward = target - pos;
                var flatSqr = toward.x * toward.x + toward.z * toward.z;
                if (flatSqr > 0.25f)
                {
                    // Pitch with the road (roll stays zero): a dead-level vehicle on a
                    // steep road buries its nose in the hillside, and the game's
                    // anti-clip check ejects the rider (driver fell out, 2026-08-30).
                    var wanted = Quaternion.LookRotation(toward.normalized);
                    var euler = wanted.eulerAngles;
                    euler.z = 0f;
                    rotation = Quaternion.Slerp(rotation, Quaternion.Euler(euler), Time.fixedDeltaTime * 3f);
                }
                if (Rb != null)
                {
                    // (The 2026-08-29 spawn-freeze was an inverted LaneClear return,
                    // not this - MovePosition works; keep it for interpolated motion.)
                    if (Rb.IsSleeping())
                    {
                        Rb.WakeUp();
                    }
                    Rb.MovePosition(next);
                    Rb.MoveRotation(rotation);
                }
                else
                {
                    transform.position = next;
                    transform.rotation = rotation;
                }
                return false;
            }
        }

        #endregion

        #region PosterManager

        // In-game advertising: locked picture-frame signs painted with the poster
        // (oxide/data/IslandTaxi/poster.png), hung at configured local offsets on
        // every monument whose name matches - one captured offset covers every
        // instance of an identical prefab (e.g. all bus stops).
        private readonly List<BaseEntity> _posters = new List<BaseEntity>();

        private void SpawnPosters()
        {
            if (!_config.PostersEnabled || _config.Posters.Count == 0)
            {
                return;
            }
            var imagePath = System.IO.Path.Combine(Interface.Oxide.DataDirectory, "IslandTaxi", "poster.png");
            if (!System.IO.File.Exists(imagePath))
            {
                PrintWarning($"Poster image missing at {imagePath}; no posters hung.");
                return;
            }
            var image = System.IO.File.ReadAllBytes(imagePath);
            var hung = 0;
            foreach (var spot in _config.Posters)
            {
                if (string.IsNullOrEmpty(spot.Monument) ||
                    !TryParseVector(spot.LocalPosition, out var localPos) ||
                    !TryParseVector(spot.LocalRotation, out var localEuler))
                {
                    continue;
                }
                foreach (var anchor in FindAnchors(spot.Monument))
                {
                    var position = anchor.Rotation * localPos + anchor.Position;
                    HangPoster(image, position, anchor.Rotation * Quaternion.Euler(localEuler));
                    hung++;
                    // Placement audit: a poster more than a storey above the surface
                    // or below the terrain is mis-anchored (a new map, a reworked
                    // monument prefab, or a capture taken far from its anchor).
                    var ground = TerrainMeta.HeightMap.GetHeight(position);
                    var surface = PosterSurfaceBelow(position, ground);
                    var verdict = position.y < ground - 0.5f ? "UNDERGROUND"
                        : position.y > surface + 6f ? "FLOATING" : "ok";
                    Puts($"[poster-debug] {verdict} \"{spot.Monument}\" on {anchor.Name} " +
                         $"anchor=({anchor.Position.x:0} {anchor.Position.y:0} {anchor.Position.z:0}) " +
                         $"poster=({position.x:0.#} {position.y:0.#} {position.z:0.#}) terrain={ground:0.#} surface={surface:0.#}");
                }
            }
            if (hung > 0)
            {
                Puts($"Hung {hung} Island Taxi poster(s).");
            }
        }

        // First solid surface (terrain, monument, building) under a poster spot.
        private static float PosterSurfaceBelow(Vector3 pos, float ground)
        {
            RaycastHit hit;
            if (Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out hit, 400f,
                LayerMask.GetMask("Terrain", "World", "Construction")))
            {
                return hit.point.y;
            }
            return ground;
        }

        private struct PosterAnchor
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public string Name;
        }

        // Anchors are monuments (by name) OR map prefabs (by path) - bus stops and
        // other roadside decor never register as monuments, but every placed prefab
        // is in the world's prefab data.
        private static List<PosterAnchor> FindAnchors(string filter)
        {
            var anchors = new List<PosterAnchor>();
            foreach (var monument in TerrainMeta.Path.Monuments)
            {
                if (monument.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    anchors.Add(new PosterAnchor
                    {
                        Position = monument.transform.position,
                        Rotation = monument.transform.rotation,
                        Name = monument.name
                    });
                }
            }
            var world = World.Serialization?.world;
            if (world?.prefabs != null)
            {
                foreach (var prefab in world.prefabs)
                {
                    var path = StringPool.Get(prefab.id);
                    if (string.IsNullOrEmpty(path) ||
                        path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    // Every monument is ALSO in the world prefab list at the same
                    // spot; one anchor per place, or every poster hangs twice.
                    var position = prefab.position;
                    if (anchors.Exists(a => (a.Position - position).sqrMagnitude < 1f))
                    {
                        continue;
                    }
                    anchors.Add(new PosterAnchor
                    {
                        Position = position,
                        Rotation = Quaternion.Euler(prefab.rotation),
                        Name = path
                    });
                }
            }
            return anchors;
        }

        // The small canvas's image cap is 320x240 LANDSCAPE, but the poster reads
        // portrait - so the art ships rotated 90 CW in image space and the CANVAS
        // hangs rolled 90 around its own face axis (owner call 2026-08-30; +90
        // hung it upside down in the live test, -90 is the right way up).
        // The two cancel out: portrait poster, upright text, zero letterboxing.
        private const float PosterRollDegrees = -90f;

        private void HangPoster(byte[] image, Vector3 position, Quaternion rotation, string prefabOverride = null)
        {
            var prefab = prefabOverride ?? _config.PosterPrefab;
            if (string.IsNullOrEmpty(prefab))
            {
                prefab = PosterSignPrefab;
            }
            // Roll about the sign's own face axis - and counter-shift the swing:
            // the canvas pivot is not its visual center, so a raw roll orbits the
            // face around the pivot (seen off-center in the live test). Shift by
            // how the roll displaces the prefab's bounds center to pin the face
            // exactly where the captured spot put it.
            var prefabCenter = GameManager.server.FindPrefab(prefab)?.GetComponent<BaseEntity>()?.bounds.center ?? Vector3.zero;
            var unrolled = rotation;
            rotation *= Quaternion.Euler(0f, 0f, PosterRollDegrees);
            position += unrolled * prefabCenter - rotation * prefabCenter;
            var entity = GameManager.server.CreateEntity(prefab, position, rotation);
            if (entity == null)
            {
                PrintWarning($"Poster prefab \"{prefab}\" does not exist on this server.");
                return;
            }
            if (entity is Signage sign)
            {
                sign.EnableSaving(false);
                sign.Spawn();
                if (sign.textureIDs != null && sign.textureIDs.Length > 0)
                {
                    sign.textureIDs[0] = FileStorage.server.Store(image, FileStorage.Type.png, sign.net.ID);
                }
                if (_posters.Count == 0)
                {
                    // crc=0 would mean FileStorage.Store itself is failing (db trouble).
                    var src = sign.paintableSources != null && sign.paintableSources.Length > 0 ? sign.paintableSources[0] : null;
                    Puts($"[poster-debug] sign {sign.GetType().Name} image={PngWidth(image)}x{PngHeight(image)} crc={(sign.textureIDs != null && sign.textureIDs.Length > 0 ? sign.textureIDs[0] : 0)} entity={sign.net?.ID.Value ?? 0} " +
                         $"srcTex={(src != null ? $"{src.texWidth}x{src.texHeight}" : "none")} sources={sign.paintableSources?.Length ?? -1} overrideMax={sign.overrideMaxImageWidth}x{sign.overrideMaxImageHeight}");
                }
                LockPoster(sign);
            }
            else if (entity is PhotoFrame frame)
            {
                // The "artist canvas": PhotoFrame is not a Signage but carries a
                // full-surface PNG overlay - art edge to edge, thin frame lip only.
                // The client is strict about the overlay matching the canvas's own
                // texture resolution; log both so a mismatch is visible.
                frame.EnableSaving(false);
                frame.Spawn();
                frame._overlayTextureCrc = FileStorage.server.Store(image, FileStorage.Type.png, frame.net.ID);
                if (_posters.Count == 0)
                {
                    var ts = frame.TextureSize;
                    Puts($"[poster-debug] canvas TextureSize={ts.x}x{ts.y}, poster.png={PngWidth(image)}x{PngHeight(image)}, crc={frame._overlayTextureCrc}");
                }
                LockPoster(frame);
            }
            else if (entity is WantedPoster wanted)
            {
                // Photo region + name line only; the aged-paper border is baked.
                // Needs a JPG: oxide/data/IslandTaxi/poster.jpg.
                var jpgPath = System.IO.Path.Combine(Interface.Oxide.DataDirectory, "IslandTaxi", "poster.jpg");
                if (!System.IO.File.Exists(jpgPath))
                {
                    PrintWarning($"Wanted-poster prefab needs {jpgPath}; skipping.");
                    entity.Kill();
                    return;
                }
                wanted.EnableSaving(false);
                wanted.Spawn();
                wanted.imageCrc = FileStorage.server.Store(System.IO.File.ReadAllBytes(jpgPath), FileStorage.Type.jpg, wanted.net.ID);
                wanted.playerName = "ISLAND TAXI  555-TAXI1";
                SetFlagNet(wanted, BaseEntity.Flags.Reserved1, true); // HasTarget: show the photo
                LockPoster(wanted);
            }
            else
            {
                PrintWarning($"Poster prefab \"{prefab}\" is not a paintable surface (got {entity.GetType().Name}).");
                entity.Kill();
                return;
            }
        }

        // PNG IHDR width/height (big-endian at offsets 16/20).
        private static int PngWidth(byte[] png) => png.Length > 24 ? (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19] : 0;
        private static int PngHeight(byte[] png) => png.Length > 24 ? (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23] : 0;

        // Vanilla's admin bypass (AdminsCanAlwaysUpdateSigns) outranks the Locked
        // flag, so an admin could accidentally repaint an ad - this hook runs before
        // the bypass and hard-blocks edits on company posters for everyone. The
        // client-side edit PROMPT still renders for admins (their client predicts
        // the bypass locally); regular players see no prompt at all.
        private object CanUpdateSign(BasePlayer player, BaseCombatEntity sign)
        {
            if (sign != null && _posters.Contains(sign))
            {
                return false;
            }
            return null;
        }

        private void LockPoster(BaseCombatEntity poster)
        {
            SetFlagNet(poster, BaseEntity.Flags.Locked, true); // nobody repaints our ads
            poster.pickup.enabled = false;                 // and nobody hammers them off the wall
            poster.SendNetworkUpdate();
            _posters.Add(poster);
        }

        private void KillPosters()
        {
            foreach (var poster in _posters)
            {
                if (poster != null && !poster.IsDestroyed)
                {
                    poster.Kill();
                }
            }
            _posters.Clear();
        }

        private static bool TryParseVector(string text, out Vector3 result)
        {
            result = Vector3.zero;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            var parts = text.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 ||
                !float.TryParse(parts[0], out var x) ||
                !float.TryParse(parts[1], out var y) ||
                !float.TryParse(parts[2], out var z))
            {
                return false;
            }
            result = new Vector3(x, y, z);
            return true;
        }

        #endregion

        #region LandingPads

        // Admin-captured service points inside otherwise no-land monuments (heli pads
        // on helipad decks, boat docks at the waterline by boarding ladders). Captured
        // monument-relative via /taxi padhere, resolved to world positions at load -
        // one captured offset covers every instance of the monument. A booking whose
        // pin (or caller) is within a pad's snap radius uses the pad point exactly,
        // trusted: no safe-spot search, no monument ban.
        private struct ResolvedPad
        {
            public Vector3 Position;
            public Vector3 Staging; // boat docks: open water 45 m radially out from the monument
            public float SnapRadius;
            public bool IsBoat;
        }

        private const float DockStagingDistance = 45f;

        private readonly List<ResolvedPad> _pads = new List<ResolvedPad>();

        private void BuildPads()
        {
            _pads.Clear();
            foreach (var spec in _config.LandingPads)
            {
                if (string.IsNullOrEmpty(spec.Monument) ||
                    !TryParseVector(spec.LocalPosition, out var local))
                {
                    PrintWarning($"Landing pad entry for \"{spec.Monument}\" is malformed; skipping.");
                    continue;
                }
                foreach (var anchor in FindAnchors(spec.Monument))
                {
                    var position = anchor.Rotation * local + anchor.Position;
                    // Approach/exit corridor for docks: straight out from the monument
                    // center through the dock - guaranteed open water, unlike the
                    // depth-gradient SeawardStaging, which is blind to structures and
                    // can point THROUGH an oil rig (all deep water to a depth sampler).
                    var outward = position - anchor.Position;
                    outward.y = 0f;
                    outward = outward.sqrMagnitude > 1f ? outward.normalized : Vector3.forward;
                    var staging = position + outward * DockStagingDistance;
                    staging.y = 0f;
                    _pads.Add(new ResolvedPad
                    {
                        Position = position,
                        Staging = staging,
                        SnapRadius = Mathf.Max(spec.SnapRadius, 5f),
                        IsBoat = string.Equals(spec.VehicleType, TypeBoat, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            if (_pads.Count > 0)
            {
                Puts($"Resolved {_pads.Count} landing pad/dock point(s).");
            }
        }

        // Nearest matching pad whose snap radius covers `pos` (horizontal distance).
        private bool TrySnapToPad(Vector3 pos, bool boatPad, out ResolvedPad snapped)
        {
            snapped = default(ResolvedPad);
            var bestSqr = float.MaxValue;
            var found = false;
            foreach (var pad in _pads)
            {
                if (pad.IsBoat != boatPad)
                {
                    continue;
                }
                var d = pad.Position - pos;
                d.y = 0f;
                var sqr = d.sqrMagnitude;
                if (sqr <= pad.SnapRadius * pad.SnapRadius && sqr < bestSqr)
                {
                    bestSqr = sqr;
                    snapped = pad;
                    found = true;
                }
            }
            return found;
        }

        #endregion

        #region TaxiUi

        private void DestroyAllUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UiMain);
            CuiHelper.DestroyUi(player, UiBanner);
            CuiHelper.DestroyUi(player, UiConfirm);
        }

        private void ShowVehicleSelect(BasePlayer player)
        {
            DestroyAllUi(player);
            var ui = new CuiElementContainer();

            // Big fleets go two-column - a single column above six vehicles either
            // clips the price/speed line or swallows the screen (clipped again at
            // eight, 2026-08-30). Rows keep their five-vehicle height either way.
            var vehicleCount = Mathf.Max(_config.Vehicles.Count, 1);
            var columns = vehicleCount > 6 ? 2 : 1;
            var rowsCount = Mathf.CeilToInt((float)vehicleCount / columns);
            var halfHeight = Mathf.Clamp(0.10f + rowsCount * 0.036f, 0.22f, 0.34f);
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.09 0.09 0.09 0.97" },
                RectTransform =
                {
                    AnchorMin = $"{(columns == 2 ? 0.28f : 0.36f):0.###} {0.5f - halfHeight:0.###}",
                    AnchorMax = $"{(columns == 2 ? 0.72f : 0.64f):0.###} {0.5f + halfHeight:0.###}"
                },
                CursorEnabled = true
            }, "Overlay", UiMain);

            ui.Add(new CuiLabel
            {
                Text = { Text = Msg("UiTitle", player), FontSize = 22, Align = TextAnchor.MiddleCenter, Color = "1 0.83 0.47 1" },
                RectTransform = { AnchorMin = "0 0.86", AnchorMax = "1 1" }
            }, UiMain);
            // Surge notice takes the subtitle's slot: the friendly greeting yields to
            // the money warning, and the price tags below already show surged rates.
            var haveBooking = _bookings.TryGetValue(player.userID, out var menuBooking);
            var surge = haveBooking ? menuBooking.Surge : 1.0;
            var standing = haveBooking ? menuBooking.Standing : 1.0;
            var surging = surge > 1.001;
            // The Cobalt band notice sits beside (or in place of) the surge one.
            string standingNotice = null;
            if (standing > 1.001) standingNotice = Msg("UiStandingSurcharge", player, Math.Round((standing - 1.0) * 100.0));
            else if (standing < 0.999) standingNotice = Msg("UiStandingDiscount", player, Math.Round((1.0 - standing) * 100.0));
            var subtitle = surging ? Msg("UiSurgeNotice", player, Math.Round((surge - 1.0) * 100.0)) : null;
            if (standingNotice != null) subtitle = subtitle == null ? standingNotice : subtitle + "\n" + standingNotice;
            var warn = surging || standing > 1.001;
            ui.Add(new CuiLabel
            {
                Text =
                {
                    Text = subtitle ?? Msg("UiSubtitle", player),
                    FontSize = 13,
                    Align = TextAnchor.MiddleCenter,
                    Color = warn ? "1 0.55 0.25 1" : (standingNotice != null ? "0.55 0.85 0.55 1" : "0.8 0.8 0.8 1")
                },
                RectTransform = { AnchorMin = "0 0.78", AnchorMax = "1 0.87" }
            }, UiMain);

            // Rows share the space between the subtitle and the cancel button, so any
            // number of vehicles fits without overlapping the controls.
            var top = 0.76f;
            var rowStep = (top - 0.17f) / rowsCount;
            var rowHeight = Mathf.Min(0.13f, rowStep - 0.012f);
            // Priciest ride on top - the executive tier leads the menu.
            var sortedVehicles = new List<KeyValuePair<string, VehicleConfig>>(_config.Vehicles);
            sortedVehicles.Sort((a, b) => b.Value.BaseFare.CompareTo(a.Value.BaseFare));
            var nameSize = columns == 2 ? 13 : 15;
            var subSize = columns == 2 ? 10 : 11;
            var index = 0;
            foreach (var pair in sortedVehicles)
            {
                var col = index % columns;
                var rowTop = top - (index / columns) * rowStep;
                var xMin = columns == 1 ? 0.08f : (col == 0 ? 0.05f : 0.515f);
                var xMax = columns == 1 ? 0.92f : (col == 0 ? 0.485f : 0.95f);
                var min = $"{xMin:0.###} {rowTop - rowHeight:0.###}";
                var max = $"{xMax:0.###} {rowTop:0.###}";
                if (pair.Value.Enabled)
                {
                    // Menu shows what the meter will actually run at - surged rates,
                    // not sticker prices.
                    var priceTag = Msg("UiPriceTag", player,
                        _payment.FormatAmount(Math.Ceiling(pair.Value.BaseFare * surge * standing)),
                        _payment.FormatRate(pair.Value.RatePerMeter * surge * standing),
                        pair.Value.Speed);
                    ui.Add(new CuiButton
                    {
                        Button = { Color = "0.22 0.45 0.22 1", Command = $"islandtaxi.ui vehicle {pair.Key}" },
                        RectTransform = { AnchorMin = min, AnchorMax = max },
                        Text = { Text = $"{pair.Value.DisplayName} ({TerrainTag(pair.Value, player)})\n<size={subSize}>{priceTag}</size>", FontSize = nameSize, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                    }, UiMain);
                }
                else
                {
                    ui.Add(new CuiPanel
                    {
                        Image = { Color = "0.18 0.18 0.18 1" },
                        RectTransform = { AnchorMin = min, AnchorMax = max }
                    }, UiMain, UiMain + ".row." + pair.Key);
                    ui.Add(new CuiLabel
                    {
                        Text = { Text = Msg("UiComingSoon", player, pair.Value.DisplayName), FontSize = nameSize, Align = TextAnchor.MiddleCenter, Color = "0.45 0.45 0.45 1" },
                        RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                    }, UiMain + ".row." + pair.Key);
                }
                index++;
            }

            ui.Add(new CuiButton
            {
                Button = { Color = "0.55 0.2 0.2 1", Command = "islandtaxi.ui cancel" },
                RectTransform = { AnchorMin = "0.3 0.04", AnchorMax = "0.7 0.14" },
                Text = { Text = Msg("UiCancel", player), FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, UiMain);

            CuiHelper.AddUi(player, ui);
        }

        private void ShowPinBanner(BasePlayer player)
        {
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.09 0.09 0.09 0.9" },
                RectTransform = { AnchorMin = "0.3 0.91", AnchorMax = "0.7 0.965" }
            }, "Overlay", UiBanner);
            ui.Add(new CuiLabel
            {
                Text = { Text = Msg("BannerDropPin", player), FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "1 0.83 0.47 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, UiBanner);
            CuiHelper.AddUi(player, ui);
        }

        private void ShowConfirm(BasePlayer player, Booking booking)
        {
            DestroyAllUi(player);
            var ui = new CuiElementContainer();

            ui.Add(new CuiPanel
            {
                Image = { Color = "0.09 0.09 0.09 0.97" },
                RectTransform = { AnchorMin = "0.36 0.28", AnchorMax = "0.64 0.72" },
                CursorEnabled = true
            }, "Overlay", UiConfirm);

            ui.Add(new CuiLabel
            {
                Text = { Text = Msg("UiConfirmTitle", player), FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 0.83 0.47 1" },
                RectTransform = { AnchorMin = "0 0.84", AnchorMax = "1 1" }
            }, UiConfirm);

            var lines = new[]
            {
                Msg("UiVehicleLine", player, booking.Vehicle.DisplayName),
                Msg("UiDestinationLine", player, MapHelper.PositionToString(booking.Destination)),
                Msg("UiDistanceLine", player, FormatDistance(booking.Distance)),
                Msg("UiSpeedLine", player, booking.Vehicle.Speed),
                Msg("UiEtaLine", player, FormatDuration(booking.Distance / Mathf.Max(booking.Vehicle.Speed, 1f))),
                Msg("UiFareLine", player, booking.IsFree ? Msg("FareFree", player) : _payment.FormatAmount(booking.Fare)),
            };
            var top = 0.82f;
            const float lineHeight = 0.105f;
            foreach (var line in lines)
            {
                ui.Add(new CuiLabel
                {
                    Text = { Text = line, FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "0.9 0.9 0.9 1" },
                    RectTransform = { AnchorMin = $"0.1 {top - lineHeight:0.###}", AnchorMax = $"0.95 {top:0.###}" }
                }, UiConfirm);
                top -= lineHeight;
            }

            ui.Add(new CuiButton
            {
                Button = { Color = "0.22 0.45 0.22 1", Command = "islandtaxi.ui confirm" },
                RectTransform = { AnchorMin = "0.08 0.05", AnchorMax = "0.48 0.18" },
                Text = { Text = Msg("UiConfirm", player), FontSize = 15, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, UiConfirm);
            ui.Add(new CuiButton
            {
                Button = { Color = "0.55 0.2 0.2 1", Command = "islandtaxi.ui cancel" },
                RectTransform = { AnchorMin = "0.52 0.05", AnchorMax = "0.92 0.18" },
                Text = { Text = Msg("UiCancel", player), FontSize = 15, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, UiConfirm);

            CuiHelper.AddUi(player, ui);
        }

        #endregion

        #region Commands

        [ConsoleCommand("islandtaxi.ui")]
        private void CcmdUi(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null)
            {
                return;
            }
            switch (arg.GetString(0, ""))
            {
                case "vehicle":
                    SelectVehicle(player, arg.GetString(1, ""));
                    break;
                case "confirm":
                    ConfirmBooking(player);
                    break;
                case "cancel":
                    EndBooking(player, "BookingCancelled");
                    break;
            }
        }

        [ChatCommand("taxi")]
        private void CmdTaxi(BasePlayer player, string command, string[] args)
        {
            if (player == null)
            {
                return;
            }

            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            var isAdmin = IsTaxiAdmin(player);

            switch (sub)
            {
                case "cancel":
                    if (args.Length >= 2)
                    {
                        // /taxi cancel <player> - admin cancels someone else's booking/ride.
                        if (!isAdmin)
                        {
                            Message(player, "NoPermission");
                            return;
                        }
                        var target = BasePlayer.Find(args[1]);
                        if (target == null)
                        {
                            Message(player, "PlayerNotFound", args[1]);
                            return;
                        }
                        var found = false;
                        if (_bookings.ContainsKey(target.userID))
                        {
                            EndBooking(target, "BookingCancelled");
                            found = true;
                        }
                        if (_rides.TryGetValue(target.userID, out var targetRide))
                        {
                            var waiting = targetRide.Autopilot == null ||
                                          targetRide.Autopilot.Phase <= RidePhase.Boarding;
                            EndRide(target, despawnImmediately: waiting, messageKey: "RideCancelled");
                            found = true;
                        }
                        Message(player, found ? "AdminCancelDone" : "AdminCancelNone", target.displayName);
                        return;
                    }
                    if (_bookings.ContainsKey(player.userID))
                    {
                        EndBooking(player, "BookingCancelled");
                    }
                    else if (_rides.TryGetValue(player.userID, out var activeRide))
                    {
                        // Waiting taxi leaves immediately; mid-flight taxi sets you down
                        // where it is. Fare stays paid either way.
                        var boarding = activeRide.Autopilot == null ||
                                       activeRide.Autopilot.Phase <= RidePhase.Boarding;
                        EndRide(player, despawnImmediately: boarding, messageKey: "RideCancelled");
                    }
                    else
                    {
                        Message(player, "NoActiveRide");
                    }
                    return;

                case "status":
                    if (!isAdmin)
                    {
                        Message(player, "NoPermission");
                        return;
                    }
                    Message(player, "AdminStatus",
                        _phone?.Controller?.PhoneNumber ?? 0,
                        _phone?.Controller != null,
                        _payment?.Mode ?? "-",
                        _bookings.Count,
                        _rides.Count);
                    return;

                case "reload":
                    if (!isAdmin)
                    {
                        Message(player, "NoPermission");
                        return;
                    }
                    LoadConfig();
                    _payment?.Resolve();
                    BuildPads();
                    Message(player, "ConfigReloaded");
                    return;

                case "water":
                {
                    // Admin diagnostic (intentionally not localized): dumps the whole
                    // water pipeline at the player's position to chat and console.
                    if (!isAdmin)
                    {
                        Message(player, "NoPermission");
                        return;
                    }
                    var p = player.transform.position;
                    var surface = TerrainMeta.WaterMap.GetHeight(p);
                    var terrain = TerrainMeta.HeightMap.GetHeight(p);
                    var report = $"pos=({p.x:0.0},{p.y:0.0},{p.z:0.0}) waterSurface={surface:0.00} terrain={terrain:0.00} column={surface - terrain:0.00} pointDepth={WaterLevel.GetWaterDepth(p, true, true):0.00}";
                    if (TryFindWaterSpot(p, out var ws))
                    {
                        var stag = SeawardStaging(ws);
                        report += $"\nwaterSpot=({ws.x:0.0},{ws.z:0.0}) {Vector3.Distance(p, ws):0.0}m away, depth={WaterColumnDepth(ws):0.00}";
                        report += $"\nstaging=({stag.x:0.0},{stag.z:0.0}) {Vector3.Distance(ws, stag):0.0}m seaward, depth={WaterColumnDepth(stag):0.00}";
                    }
                    else
                    {
                        report += "\nwaterSpot=NONE within search radius";
                    }
                    player.ChatMessage(report);
                    Puts("[boat-debug] /taxi water: " + report.Replace("\n", " | "));
                    return;
                }

                case "spawncar":
                {
                    // Admin diagnostic: spawns the exact taxi car build (untracked - no
                    // driver, autopilot, or auto-radio) for hand-placing a Car Radio.
                    // Clean up afterwards with ent kill.
                    if (!isAdmin)
                    {
                        Message(player, "NoPermission");
                        return;
                    }
                    var carPos = player.transform.position + player.eyes.BodyForward() * 4f;
                    carPos.y = GroundHeight(carPos) + 0.5f;
                    var carEnt = GameManager.server.CreateEntity(CarChassisPrefab, carPos,
                        Quaternion.Euler(0f, player.viewAngles.y + 90f, 0f)) as ModularCar;
                    if (carEnt == null)
                    {
                        player.ChatMessage("Chassis spawn failed.");
                        return;
                    }
                    carEnt.EnableSaving(false);
                    carEnt.Spawn();
                    SetupCarModules(carEnt);
                    player.ChatMessage("Taxi test car spawned (no driver/autopilot). Deploy your Car Radio on it, then run /taxi radiopos. Remove it with: ent kill");
                    return;
                }

                case "postertry":
                {
                    // Admin prefab shootout (Task 8.4): /taxi postertry lists the
                    // candidate paintable signs; /taxi postertry <n> (or a raw prefab
                    // path) hangs a test poster with it where you're looking. Pick the
                    // most frameless look, then set "Poster sign prefab" in the config.
                    if (!isAdmin)
                    {
                        Message(player, "NoPermission");
                        return;
                    }
                    // Real paths from the server's own prefab registry - guessed paths
                    // burned us (photoframes are not under /signs/ as assumed).
                    var candidates = new List<string>();
                    foreach (var kv in StringPool.toNumber)
                    {
                        var p = kv.Key;
                        if (p.EndsWith(".prefab") &&
                            (p.Contains("photoframe") || p.Contains("/signs/") ||
                             p.Contains("wanted") || p.Contains("canvas")))
                        {
                            candidates.Add(p);
                        }
                    }
                    candidates.Sort();
                    // /taxi postertry find <text>: search the whole prefab registry.
                    if (args.Length >= 3 && args[1].Equals("find", StringComparison.OrdinalIgnoreCase))
                    {
                        var found = new List<string>();
                        foreach (var kv in StringPool.toNumber)
                        {
                            if (kv.Key.EndsWith(".prefab") &&
                                kv.Key.IndexOf(args[2], StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                found.Add(kv.Key);
                            }
                        }
                        found.Sort();
                        var msg2 = found.Count == 0
                            ? $"No prefab paths contain \"{args[2]}\"."
                            : $"Prefabs matching \"{args[2]}\" (use the full path with /taxi postertry <path>):\n" +
                              string.Join("\n", found.GetRange(0, Math.Min(found.Count, 15))) +
                              (found.Count > 15 ? $"\n...and {found.Count - 15} more (full list in console)" : "");
                        player.ChatMessage(msg2);
                        Puts("[poster-debug] prefab search \"" + args[2] + "\":\n" + string.Join("\n", found));
                        return;
                    }
                    if (args.Length < 2)
                    {
                        var list = "Poster prefab candidates (current marked *):";
                        for (int i = 0; i < candidates.Count; i++)
                        {
                            var mark = candidates[i] == _config.PosterPrefab ? " *" : "";
                            var shortName = candidates[i].Substring(candidates[i].LastIndexOf('/') + 1).Replace(".prefab", "");
                            list += $"\n{i + 1}. {shortName}{mark}";
                        }
                        list += "\nUsage: /taxi postertry <n> [roll] while looking at a wall (roll in degrees, e.g. 90 to turn a landscape sign portrait). Test signs go away on plugin reload.";
                        player.ChatMessage(list);
                        Puts("[poster-debug] postertry candidates:\n" + string.Join("\n", candidates));
                        return;
                    }
                    string tryPrefab;
                    if (int.TryParse(args[1], out var idx) && idx >= 1 && idx <= candidates.Count)
                    {
                        tryPrefab = candidates[idx - 1];
                    }
                    else
                    {
                        tryPrefab = args[1];
                    }
                    RaycastHit tryHit;
                    if (!Physics.Raycast(player.eyes.position, player.eyes.HeadForward(), out tryHit, 12f,
                        LayerMask.GetMask("Construction", "World", "Default", "Terrain", "Deployed")))
                    {
                        player.ChatMessage("Look at a wall/surface within 12m and try again.");
                        return;
                    }
                    // Optional 5th arg: alternate image file in oxide/data/IslandTaxi
                    // (e.g. poster_240x320.png) - for probing a paintable's native
                    // resolution: canvases only render exact-size images.
                    var tryImageName = args.Length >= 5 ? args[4] : "poster.png";
                    var tryImagePath = System.IO.Path.Combine(Interface.Oxide.DataDirectory, "IslandTaxi", tryImageName);
                    if (!System.IO.File.Exists(tryImagePath))
                    {
                        player.ChatMessage($"{tryImageName} missing from oxide/data/IslandTaxi.");
                        return;
                    }
                    // Optional roll (degrees) around the facing axis - e.g. 90 turns a
                    // landscape wood sign portrait. The art will read sideways during
                    // the test; production bakes the roll into the config rotation and
                    // ships a pre-rotated poster.png.
                    var roll = 0f;
                    if (args.Length >= 3)
                    {
                        float.TryParse(args[2], out roll);
                    }
                    // Optional yaw (4th arg): 180 flips which face points out of the
                    // wall - some paintables (artist canvas?) paint the opposite face.
                    var yaw = 0f;
                    if (args.Length >= 4)
                    {
                        float.TryParse(args[3], out yaw);
                    }
                    var tryRot = Quaternion.LookRotation(tryHit.normal) * Quaternion.Euler(0f, yaw, roll);
                    var beforeCount = _posters.Count;
                    HangPoster(System.IO.File.ReadAllBytes(tryImagePath),
                        tryHit.point + tryHit.normal * 0.02f,
                        tryRot,
                        tryPrefab);
                    player.ChatMessage(_posters.Count > beforeCount
                        ? $"Test poster hung with {tryPrefab.Substring(tryPrefab.LastIndexOf('/') + 1)}" + (roll != 0f ? $" rolled {roll:0}°" : "")
                        : $"FAILED - \"{tryPrefab}\" did not spawn as a paintable surface (see console for its type).");
                    return;
                }

                case "padhere":
                {
                    // Admin capture: stand on the landing spot (or tread water at the
                    // dock spot) and run /taxi padhere [heli|boat] [filter]. Captures
                    // your position relative to the nearest matching monument anchor
                    // and saves it STRAIGHT into the config - no hand-editing.
                    if (!isAdmin)
                    {
                        Message(player, "NoPermission");
                        return;
                    }
                    var padType = TypeHelicopter;
                    string padFilter = null;
                    for (int i = 1; i < args.Length; i++)
                    {
                        if (args[i].Equals("heli", StringComparison.OrdinalIgnoreCase) ||
                            args[i].Equals("helicopter", StringComparison.OrdinalIgnoreCase))
                        {
                            padType = TypeHelicopter;
                        }
                        else if (args[i].Equals("boat", StringComparison.OrdinalIgnoreCase))
                        {
                            padType = TypeBoat;
                        }
                        else
                        {
                            padFilter = args[i];
                        }
                    }
                    var padPos = player.transform.position;
                    if (padType == TypeBoat)
                    {
                        // Docks live at the waterline, wherever the admin is treading.
                        padPos.y = Mathf.Max(TerrainMeta.WaterMap.GetHeight(padPos), 0f);
                    }

                    var padCands = padFilter != null
                        ? FindAnchors(padFilter)
                        : new List<PosterAnchor>();
                    if (padFilter == null)
                    {
                        foreach (var m in TerrainMeta.Path.Monuments)
                        {
                            padCands.Add(new PosterAnchor
                            {
                                Position = m.transform.position,
                                Rotation = m.transform.rotation,
                                Name = m.name
                            });
                        }
                    }
                    PosterAnchor? padAnchor = null;
                    var padAnchorDist = float.MaxValue;
                    foreach (var c in padCands)
                    {
                        var d3 = Vector3.Distance(padPos, c.Position);
                        if (d3 < padAnchorDist)
                        {
                            padAnchorDist = d3;
                            padAnchor = c;
                        }
                    }
                    if (padAnchor == null)
                    {
                        player.ChatMessage(padFilter != null
                            ? $"No monument/prefab matches \"{padFilter}\"."
                            : "No monument anchor found near you.");
                        return;
                    }
                    var pa = padAnchor.Value;
                    var padLocal = Quaternion.Inverse(pa.Rotation) * (padPos - pa.Position);
                    var storeFilter = padFilter;
                    if (storeFilter == null)
                    {
                        // Derive a contains-filter from the anchor's prefab filename.
                        storeFilter = pa.Name;
                        var slash = storeFilter.LastIndexOf('/');
                        if (slash >= 0)
                        {
                            storeFilter = storeFilter.Substring(slash + 1);
                        }
                        if (storeFilter.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                        {
                            storeFilter = storeFilter.Substring(0, storeFilter.Length - ".prefab".Length);
                        }
                    }
                    var newPad = new LandingPad
                    {
                        Monument = storeFilter,
                        LocalPosition = $"{padLocal.x:0.###} {padLocal.y:0.###} {padLocal.z:0.###}",
                        VehicleType = padType
                    };
                    _config.LandingPads.Add(newPad);
                    SaveConfig();
                    BuildPads();
                    var padMsg = $"{padType} pad saved for \"{storeFilter}\" (anchor {padAnchorDist:0}m away), local ({newPad.LocalPosition}), snap {newPad.SnapRadius:0}m. {_pads.Count} pad point(s) now active.";
                    player.ChatMessage(padMsg);
                    Puts("[pad-debug] padhere: " + padMsg + " anchor=" + pa.Name);
                    return;
                }

                case "monhere":
                {
                    // Admin diagnostic (Task 8.2): monument containment truth at the
                    // player's position - which named monuments are near, whether their
                    // OBB bounds are real or degenerate, and what InNamedMonument says.
                    if (!isAdmin)
                    {
                        Message(player, "NoPermission");
                        return;
                    }
                    var here = player.transform.position;
                    var lines = new List<string>
                    {
                        $"InNamedMonument(here) = {InNamedMonument(here)} at ({here.x:0}, {here.z:0})"
                    };
                    var all = TerrainMeta.Path?.Monuments;
                    if (all != null)
                    {
                        var nearest = new List<MonumentInfo>(all);
                        nearest.Sort((a, b) =>
                            (a.transform.position - here).sqrMagnitude.CompareTo(
                                (b.transform.position - here).sqrMagnitude));
                        for (int i = 0; i < nearest.Count && i < 4; i++)
                        {
                            var m = nearest[i];
                            var label = m.displayPhrase?.english;
                            if (string.IsNullOrEmpty(label))
                            {
                                label = m.name;
                            }
                            var flat = m.transform.position - here;
                            flat.y = 0f;
                            lines.Add($"{i + 1}. \"{label}\" named={m.shouldDisplayOnMap} dist={flat.magnitude:0}m widest2D={m.GetWidest2DBound():0}m inBounds={m.IsInBounds(here)}");
                        }
                    }
                    var report2 = string.Join("\n", lines);
                    player.ChatMessage(report2);
                    Puts("[monument-debug] " + report2.Replace("\n", " | "));
                    return;
                }

                case "posterhere":
                {
                    // Admin capture: look at the wall spot, run /taxi posterhere [filter].
                    // Spawns a live test poster there (plugin spawns ignore building
                    // block) and logs config-ready offsets relative to the nearest
                    // matching anchor (map prefab like "busstop", or nearest monument).
                    if (!isAdmin)
                    {
                        Message(player, "NoPermission");
                        return;
                    }
                    RaycastHit hit;
                    if (!Physics.Raycast(player.eyes.position, player.eyes.HeadForward(), out hit, 12f,
                        LayerMask.GetMask("Construction", "World", "Default", "Terrain", "Deployed")))
                    {
                        player.ChatMessage("Look at a wall/surface within 12m and try again.");
                        return;
                    }
                    var posterPos = hit.point + hit.normal * 0.02f;
                    var posterRot = Quaternion.LookRotation(hit.normal);

                    var filter = args.Length >= 2 ? args[1] : null;
                    PosterAnchor? anchor = null;
                    var anchorDist = float.MaxValue;
                    // No filter: nearest anchor of ANY kind - monuments AND map
                    // prefabs. Monuments-only picked a substation 37m away over the
                    // bus stop 3m away (live capture miss, 2026-08-29).
                    var candidates = FindAnchors(filter ?? "");
                    foreach (var candidate in candidates)
                    {
                        var d = Vector3.Distance(posterPos, candidate.Position);
                        if (d < anchorDist)
                        {
                            anchorDist = d;
                            anchor = candidate;
                        }
                    }
                    if (anchor == null || anchorDist > 200f)
                    {
                        player.ChatMessage($"No matching anchor within 200m{(filter != null ? $" for \"{filter}\"" : "")}.");
                        return;
                    }
                    // Decor prefabs (bus stops etc.) are a few metres across: a wall
                    // 68m from the nearest one is NOT that bus stop, and the offset
                    // would float in mid-air at every other instance (1.5.3 shipped
                    // exactly that). Monuments are big; only decor gets the guard.
                    if (anchor.Value.Name.IndexOf("/decor/", StringComparison.OrdinalIgnoreCase) >= 0 && anchorDist > 15f)
                    {
                        player.ChatMessage($"Nearest \"{anchor.Value.Name.Substring(anchor.Value.Name.LastIndexOf('/') + 1)}\" is {anchorDist:0}m away - that's not the one you're looking at. Not saved.");
                        return;
                    }

                    var a = anchor.Value;
                    var lp = Quaternion.Inverse(a.Rotation) * (posterPos - a.Position);
                    var le = (Quaternion.Inverse(a.Rotation) * posterRot).eulerAngles;

                    // Save straight into the config (upsert by anchor filter) and
                    // re-hang everything so the change is live immediately.
                    var storePosterFilter = filter;
                    if (storePosterFilter == null)
                    {
                        storePosterFilter = a.Name;
                        var slash2 = storePosterFilter.LastIndexOf('/');
                        if (slash2 >= 0)
                        {
                            storePosterFilter = storePosterFilter.Substring(slash2 + 1);
                        }
                        if (storePosterFilter.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                        {
                            storePosterFilter = storePosterFilter.Substring(0, storePosterFilter.Length - ".prefab".Length);
                        }
                    }
                    var posterSpot = _config.Posters.Find(s => string.Equals(s.Monument, storePosterFilter, StringComparison.OrdinalIgnoreCase));
                    if (posterSpot == null)
                    {
                        posterSpot = new PosterSpot { Monument = storePosterFilter };
                        _config.Posters.Add(posterSpot);
                    }
                    posterSpot.LocalPosition = $"{lp.x:0.###} {lp.y:0.###} {lp.z:0.###}";
                    posterSpot.LocalRotation = $"{le.x:0.#} {le.y:0.#} {le.z:0.#}";
                    SaveConfig();
                    KillPosters();
                    SpawnPosters();

                    var entry = $"anchor={a.Name} ({anchorDist:0}m away)\nLocalPosition=\"{posterSpot.LocalPosition}\"\nLocalRotation=\"{posterSpot.LocalRotation}\"";
                    player.ChatMessage($"Poster spot \"{storePosterFilter}\" saved and re-hung island-wide. " + entry);
                    Puts("[poster-debug] posterhere saved: filter=" + storePosterFilter + " | " + entry.Replace("\n", " | "));
                    return;
                }

                case "monuments":
                {
                    // Admin diagnostic: the five nearest monuments and your offset in
                    // each one's local space - tells us what a spot registers as.
                    if (!isAdmin)
                    {
                        Message(player, "NoPermission");
                        return;
                    }
                    var p = player.transform.position;
                    var all = new List<MonumentInfo>(TerrainMeta.Path.Monuments);
                    all.Sort((a, b) =>
                        Vector3.Distance(p, a.transform.position).CompareTo(
                        Vector3.Distance(p, b.transform.position)));
                    for (int i = 0; i < Mathf.Min(5, all.Count); i++)
                    {
                        var m = all[i];
                        var lp = m.transform.InverseTransformPoint(p);
                        var line = $"{Vector3.Distance(p, m.transform.position):0}m  {m.name}  yourLocalPos=({lp.x:0.##} {lp.y:0.##} {lp.z:0.##})";
                        player.ChatMessage(line);
                        Puts("[poster-debug] " + line);
                    }
                    return;
                }

                case "posterpos":
                {
                    // Admin diagnostic: hang a picture frame where the poster should go,
                    // stand next to it, run this - it logs the config-ready monument
                    // name and local offsets.
                    if (!isAdmin)
                    {
                        Message(player, "NoPermission");
                        return;
                    }
                    var signs = Facepunch.Pool.Get<List<Signage>>();
                    Vis.Entities(player.transform.position, 6f, signs);
                    Signage nearest = null;
                    var bestDist = float.MaxValue;
                    foreach (var s in signs)
                    {
                        var d = Vector3.Distance(player.transform.position, s.transform.position);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            nearest = s;
                        }
                    }
                    Facepunch.Pool.FreeUnmanaged(ref signs);
                    if (nearest == null)
                    {
                        player.ChatMessage("No sign within 6m.");
                        return;
                    }
                    MonumentInfo closest = null;
                    var monDist = float.MaxValue;
                    foreach (var m in TerrainMeta.Path.Monuments)
                    {
                        var d = Vector3.Distance(nearest.transform.position, m.transform.position);
                        if (d < monDist)
                        {
                            monDist = d;
                            closest = m;
                        }
                    }
                    if (closest == null)
                    {
                        player.ChatMessage("No monuments on this map?");
                        return;
                    }
                    var pos = closest.transform.InverseTransformPoint(nearest.transform.position);
                    var rot = (Quaternion.Inverse(closest.transform.rotation) * nearest.transform.rotation).eulerAngles;
                    var report = $"monument={closest.name} ({monDist:0}m from its origin)\nLocalPosition=\"{pos.x:0.###} {pos.y:0.###} {pos.z:0.###}\"\nLocalRotation=\"{rot.x:0.#} {rot.y:0.#} {rot.z:0.#}\"";
                    player.ChatMessage(report);
                    Puts("[poster-debug] " + report.Replace("\n", " | "));
                    return;
                }

                case "radiopos":
                {
                    // Admin diagnostic: logs every car radio within 10m with its parent
                    // and local transform - place a real Car Radio by hand, run this,
                    // and the correct offsets land in the console.
                    if (!isAdmin)
                    {
                        Message(player, "NoPermission");
                        return;
                    }
                    var radios = Facepunch.Pool.Get<List<ModularCarRadio>>();
                    Vis.Entities(player.transform.position, 10f, radios);
                    if (radios.Count == 0)
                    {
                        player.ChatMessage("No car radios within 10m.");
                    }
                    foreach (var r in radios)
                    {
                        var lp = r.transform.localPosition;
                        var lr = r.transform.localRotation.eulerAngles;
                        var line = $"radio parent={r.GetParentEntity()?.GetType().Name ?? "none"} localPos=({lp.x:0.###}, {lp.y:0.###}, {lp.z:0.###}) localRot=({lr.x:0.#}, {lr.y:0.#}, {lr.z:0.#})";
                        player.ChatMessage(line);
                        Puts("[radio-debug] " + line);
                    }
                    Facepunch.Pool.FreeUnmanaged(ref radios);
                    return;
                }

                case "test":
                    if (!isAdmin)
                    {
                        Message(player, "NoPermission");
                        return;
                    }
                    float x, z;
                    if (args.Length < 3 || !float.TryParse(args[1], out x) || !float.TryParse(args[2], out z))
                    {
                        Message(player, "TestUsage");
                        return;
                    }
                    var requested = new Vector3(x, 0f, z);
                    if (_safeSpots.TryFind(requested, out var spot))
                    {
                        Message(player, "SafeSpotFound", x, z, spot.x, spot.y, spot.z,
                            MapHelper.PositionToString(spot),
                            Vector3.Distance(new Vector3(x, spot.y, z), spot));
                    }
                    else
                    {
                        Message(player, "SafeSpotNone", _config.MaxSearchRadius, x, z);
                    }
                    return;

                default:
                    // Testing entry that skips the phone (admin only; players use 555-TAXI1).
                    if (!isAdmin)
                    {
                        Message(player, "NoPermission");
                        return;
                    }
                    StartBooking(player);
                    return;
            }
        }

        #endregion
    }
}
