// MAPPoI.cs — Live Battle Overlay
//
// Draws circles on the in-game map wherever fighting is happening, driven by an
// exponentially-decaying "heat" score rather than a bullet-counted TTL.
//
// Design notes:
//   - Heat model: every qualifying damage event adds weight at a world position. All
//     accumulators decay by pow(0.5, dt/halfLife) each tick, so a battle's centre and
//     spatial spread are exact exponentially-weighted statistics kept in O(1) memory —
//     no event list. A battle dies when its heat falls below DeathThreshold, which means
//     a potshot fades in ~20s while a sustained firefight stays lit and fades out smoothly.
//   - Clustering: events join the nearest live battle within MergeRadius, else start a new
//     one. Battles whose centres converge are merged (adding decayed accumulators is exact).
//     This keeps one circle per battle regardless of where the vanilla grid lines fall.
//   - Rendering: MapMarkerGenericRadius. color1/color2/alpha/radius are NOT in the protobuf
//     snapshot — they only reach the client via the MarkerUpdate ClientRPC (SendUpdate() for
//     global markers, RpcTarget.Player for per-player ones). marker.radius is a fraction of
//     world size, not metres.
//   - Marker visibility: this build has no CanNetworkTo hook, and ShouldNetworkTo requires
//     parentEntity == player for limitNetworking. So per-player markers must be parented to
//     the viewer, which makes them drift with that player; we correct the position whenever
//     the viewer has moved more than PerPlayerTolerance metres. Global markers (globalBroadcast)
//     have no such problem, so "auto" mode uses them until somebody actually opts out.
//   - Labels use VendingMachineMapMarker.markerShopName — the only way to get readable text
//     onto the Rust map. Explosion icons use the vanilla MapMarkerExplosion prefab; note its
//     SetDuration() takes MINUTES.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("MAPPoI", "Lucien Murray-Pitts", "2.2.0")]
    [Description("Live battle overlay: decaying-heat PvP/raid circles, gun/hostile pings with text, explosion icons and a HUD ticker.")]
    public class MAPPoI : RustPlugin
    {
        // ── Constants ────────────────────────────────────────────────────────────
        private const string CirclePrefab    = "assets/prefabs/tools/map/genericradiusmarker.prefab";
        private const string LabelPrefab     = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";
        private const string ExplosionPrefab = "assets/prefabs/tools/map/explosionmarker.prefab";

        private const string AdminPerm = "mappoi.admin";
        private const string DataFile  = "MapPOI";
        private const string HudPanel  = "mappoi.hud";

        // Vanilla map grid cell size in metres.
        private const float GridCell = 146.3f;

        private const int CatPvp = 0, CatRaid = 1, CatMonument = 2, CatEvent = 3, CatNpc = 4, CatAnimal = 5;
        private const int CatCount = 6;
        private static readonly string[] CatNames = { "pvp", "raid", "monuments", "events", "npc", "animal" };

        private static readonly string[] DisplayNames = { "circles", "labels", "explosions", "hud", "pings" };
        private const int DispCircles = 0, DispLabels = 1, DispExplosions = 2, DispHud = 3, DispPings = 4;

        /// <summary>Ping icon per category. Pings live in State.pings, which is separate from the
        /// player's map notes (its own maximumPings budget), auto-expire, and are per-player —
        /// so unlike the circles they CAN be hidden for someone who opted out.</summary>
        private static readonly BasePlayer.PingType[] CatPing =
        {
            BasePlayer.PingType.Gun,      // pvp
            BasePlayer.PingType.Hostile,  // raid
            BasePlayer.PingType.Gun,      // monuments
            BasePlayer.PingType.Dollar,   // events
            BasePlayer.PingType.Hostile,  // npc
            BasePlayer.PingType.Node,     // animal
        };

        // ── Config ───────────────────────────────────────────────────────────────
        private Configuration _cfg;

        private class Configuration
        {
            [JsonProperty("Schema version")] public int Schema { get; set; } = 2;

            [JsonProperty("POI style (1=centroid+spread, 2=fuzzed, 3=fuzzed stable)")] public int PoiStyle { get; set; } = 1;
            [JsonProperty("Visibility delay seconds (-1 = off, 0 = live)")] public float VisibilityDelay { get; set; } = 30f;
            [JsonProperty("Tracked categories")] public List<string> Track { get; set; } = new List<string> { "pvp", "raid" };
            // "labels" is OFF by default: the only server-spawnable marker that can carry text
            // is VendingMachineMapMarker, which drags its shop icon along and can cluster with
            // real vending machines on the client map. Opt in only if you accept that.
            [JsonProperty("Display elements")] public List<string> Display { get; set; } = new List<string> { "circles", "explosions", "hud" };
            // "global" is the only mode that places circles correctly. A per-player marker must
            // be parented to the viewer (limitNetworking requires parentEntity == player), and a
            // parented marker is positioned RELATIVE to its parent client-side — verified in
            // game: a circle 75m from the player rendered, the same circle at map centre did
            // not. So perplayer can only draw near the viewer and must never be automatic.
            [JsonProperty("Marker mode (global|perplayer)")] public string MarkerMode { get; set; } = "global";

            [JsonProperty("Heat half-life seconds")] public float HalfLife { get; set; } = 30f;
            // Must stay BELOW the smallest single-event weight (npc/animal, 0.25), otherwise a
            // battle seeded by one low-weight hit is culled on the next tick and can never
            // accumulate — monument and NPC fights would simply never appear.
            [JsonProperty("Heat death threshold")] public float DeathThreshold { get; set; } = 0.15f;
            [JsonProperty("New battle grace seconds")] public float GraceSeconds { get; set; } = 15f;
            [JsonProperty("Publish: minimum distinct participants")] public int MinParticipants { get; set; } = 2;
            [JsonProperty("Publish: minimum heat")] public float MinPublishHeat { get; set; } = 3f;
            [JsonProperty("Cluster merge radius (metres)")] public float MergeRadius { get; set; } = 120f;
            [JsonProperty("Maximum cluster join radius (metres)")] public float MaxJoinRadius { get; set; } = 300f;
            [JsonProperty("Maximum concurrent battles")] public int MaxBattles { get; set; } = 12;
            [JsonProperty("Reference heat for full alpha")] public float RefHeat { get; set; } = 25f;

            [JsonProperty("Weight: player hit")] public float WeightHit { get; set; } = 1f;
            [JsonProperty("Weight: heavy hit bonus (damage > 50)")] public float WeightHeavyBonus { get; set; } = 0.5f;
            [JsonProperty("Weight: player kill")] public float WeightKill { get; set; } = 10f;
            [JsonProperty("Weight: explosive on player")] public float WeightExplosivePlayer { get; set; } = 6f;
            [JsonProperty("Weight: explosive on structure (raid)")] public float WeightRaid { get; set; } = 4f;
            [JsonProperty("Weight: monument PvE")] public float WeightMonument { get; set; } = 0.5f;
            [JsonProperty("Weight: event (cargo/heli/bradley)")] public float WeightEvent { get; set; } = 3f;
            [JsonProperty("Weight: NPC combat")] public float WeightNpc { get; set; } = 0.25f;
            [JsonProperty("Weight: animal")] public float WeightAnimal { get; set; } = 0.25f;

            [JsonProperty("Pellet coalesce window (seconds)")] public float PelletWindow { get; set; } = 0.25f;
            [JsonProperty("Pair rate cap: max events")] public int PairCapEvents { get; set; } = 6;
            [JsonProperty("Pair rate cap: window seconds")] public float PairCapWindow { get; set; } = 10f;
            [JsonProperty("Minimum damage to count")] public float MinDamage { get; set; } = 1f;

            [JsonProperty("Recentre interval seconds")] public float RecentreInterval { get; set; } = 10f;
            [JsonProperty("Circle min radius (grid squares)")] public float RadiusMinGrid { get; set; } = 0.8f;
            [JsonProperty("Circle max radius (grid squares)")] public float RadiusMaxGrid { get; set; } = 1.5f;
            // Applied to the final radius of every style. The grid-square figures above are the
            // intended semantics; this scales what actually renders, because the client draws
            // these circles far smaller than the world-size normalisation implies.
            [JsonProperty("Circle size multiplier")] public float CircleScale { get; set; } = 3f;
            [JsonProperty("Fill alpha min")] public float AlphaMin { get; set; } = 0.18f;
            [JsonProperty("Fill alpha max")] public float AlphaMax { get; set; } = 0.55f;
            [JsonProperty("Explosion icon duration (seconds)")] public float ExplosionIconSeconds { get; set; } = 8f;
            [JsonProperty("Explosion icon minimum spacing (seconds)")] public float ExplosionIconSpacing { get; set; } = 2f;
            [JsonProperty("Per-player marker reposition tolerance (metres)")] public float PerPlayerTolerance { get; set; } = 15f;
            [JsonProperty("HUD refresh seconds")] public float HudRefresh { get; set; } = 5f;
            [JsonProperty("HUD maximum battles listed")] public int HudMaxEntries { get; set; } = 3;
            [JsonProperty("Ping refresh seconds")] public float PingRefresh { get; set; } = 10f;
            // Kept below server.maximumPings (5) so a player's own pings are not evicted.
            [JsonProperty("Maximum battles pinged")] public int PingMaxEntries { get; set; } = 3;
            // The client plays the ping sound (and flashes the compass) only for a ping that
            // arrives with a FULL timer. Sending ours already this far through their life keeps
            // them silent (verified live at 0.1). 0 restores vanilla fresh pings with sound.
            [JsonProperty("Ping arrival age fraction (0 = fresh ping with sound)")] public float PingAgeFraction { get; set; } = 0.1f;
            // Sits below the vanilla compass and its direction markers, which occupy roughly
            // y 0.93-1.0 at top centre. Adjust live with: /mappoi config hudpos
            // The height of this rect is ONE row. Kept close to the font size (0.025 ~= 27px at
            // 1080p for a 12px font) — a taller row just adds dead space between lines.
            [JsonProperty("HUD anchor min (x y)")] public string HudAnchorMin { get; set; } = "0.34 0.905";
            [JsonProperty("HUD anchor max (x y)")] public string HudAnchorMax { get; set; } = "0.66 0.93";

            [JsonProperty("Enable legacy v1 decoy circles")] public bool LegacyDecoy { get; set; } = false;
            [JsonProperty("Legacy decoy: min radius (metres)")] public float LegacyRadiusMin { get; set; } = 40f;
            [JsonProperty("Legacy decoy: max radius (metres)")] public float LegacyRadiusMax { get; set; } = 120f;
            [JsonProperty("Legacy decoy: spawn range min (metres)")] public float LegacySpawnMin { get; set; } = 5f;
            [JsonProperty("Legacy decoy: spawn range max (metres)")] public float LegacySpawnMax { get; set; } = 75f;
        }

        protected override void LoadDefaultConfig() => _cfg = new Configuration();
        protected override void SaveConfig() => Config.WriteObject(_cfg, true);

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                // A v1 config has none of the v2 keys; ReadObject would silently hand back
                // all-defaults, so detect the old schema explicitly and say so.
                if (Config["Schema version"] == null)
                {
                    PrintWarning("MAPPoI config predates v2 — regenerating with v2 defaults.");
                    _cfg = new Configuration();
                }
                else
                {
                    _cfg = Config.ReadObject<Configuration>() ?? new Configuration();
                }
            }
            catch
            {
                _cfg = new Configuration();
                PrintWarning("MAPPoI config unreadable — regenerated with defaults.");
            }

            ValidateConfig();
            SaveConfig();
        }

        private void ValidateConfig()
        {
            _cfg.Schema = 2;
            _cfg.PoiStyle = Mathf.Clamp(_cfg.PoiStyle, 1, 3);
            if (_cfg.VisibilityDelay < -1f) _cfg.VisibilityDelay = -1f;
            if (_cfg.VisibilityDelay > 600f) _cfg.VisibilityDelay = 600f;
            _cfg.MarkerMode = NormaliseMarkerMode(_cfg.MarkerMode);

            _cfg.HalfLife = Mathf.Clamp(_cfg.HalfLife, 2f, 600f);
            // Derive the ceiling from the weights rather than hard-coding it, so a single event
            // of the SMALLEST weight always lands a battle comfortably above the death
            // threshold. Also repairs pre-existing configs that saved a too-high value.
            var minWeight = Mathf.Min(
                Mathf.Min(_cfg.WeightNpc, _cfg.WeightAnimal),
                Mathf.Min(_cfg.WeightMonument, _cfg.WeightHit));
            _cfg.DeathThreshold = Mathf.Clamp(_cfg.DeathThreshold, 0.01f, Mathf.Max(0.02f, minWeight * 0.5f));
            _cfg.GraceSeconds = Mathf.Clamp(_cfg.GraceSeconds, 0f, 120f);
            _cfg.MinParticipants = Mathf.Max(1, _cfg.MinParticipants);
            _cfg.MinPublishHeat = Mathf.Max(_cfg.DeathThreshold, _cfg.MinPublishHeat);
            _cfg.MergeRadius = Mathf.Clamp(_cfg.MergeRadius, 20f, 1000f);
            _cfg.MaxJoinRadius = Mathf.Clamp(_cfg.MaxJoinRadius, _cfg.MergeRadius, 2000f);
            _cfg.MaxBattles = Mathf.Clamp(_cfg.MaxBattles, 1, 64);
            _cfg.RefHeat = Mathf.Max(1f, _cfg.RefHeat);

            _cfg.PelletWindow = Mathf.Clamp(_cfg.PelletWindow, 0f, 5f);
            _cfg.PairCapEvents = Mathf.Max(1, _cfg.PairCapEvents);
            _cfg.PairCapWindow = Mathf.Clamp(_cfg.PairCapWindow, 1f, 120f);
            _cfg.MinDamage = Mathf.Max(0f, _cfg.MinDamage);

            _cfg.RecentreInterval = Mathf.Clamp(_cfg.RecentreInterval, 1f, 120f);
            _cfg.RadiusMinGrid = Mathf.Clamp(_cfg.RadiusMinGrid, 0.15f, 5f);
            _cfg.RadiusMaxGrid = Mathf.Clamp(_cfg.RadiusMaxGrid, _cfg.RadiusMinGrid, 8f);
            _cfg.CircleScale = Mathf.Clamp(_cfg.CircleScale, 0.1f, 20f);
            _cfg.AlphaMin = Mathf.Clamp01(_cfg.AlphaMin);
            _cfg.AlphaMax = Mathf.Clamp(_cfg.AlphaMax, _cfg.AlphaMin, 1f);
            _cfg.ExplosionIconSeconds = Mathf.Clamp(_cfg.ExplosionIconSeconds, 1f, 300f);
            _cfg.ExplosionIconSpacing = Mathf.Clamp(_cfg.ExplosionIconSpacing, 0f, 60f);
            _cfg.PerPlayerTolerance = Mathf.Clamp(_cfg.PerPlayerTolerance, 1f, 200f);
            _cfg.HudRefresh = Mathf.Clamp(_cfg.HudRefresh, 1f, 60f);
            _cfg.HudMaxEntries = Mathf.Clamp(_cfg.HudMaxEntries, 1, 6);
            _cfg.PingRefresh = Mathf.Clamp(_cfg.PingRefresh, 2f, 120f);
            _cfg.PingMaxEntries = Mathf.Clamp(_cfg.PingMaxEntries, 0, 5);
            _cfg.PingAgeFraction = Mathf.Clamp(_cfg.PingAgeFraction, 0f, 0.9f);

            // A malformed anchor silently breaks the whole CUI panel, so validate the pair and
            // fall back to the defaults rather than shipping an invisible HUD.
            float x0, y0, x1, y1;
            if (!TryParseAnchor(_cfg.HudAnchorMin, out x0, out y0) ||
                !TryParseAnchor(_cfg.HudAnchorMax, out x1, out y1) ||
                x1 <= x0 || y1 <= y0)
            {
                PrintWarning("HUD anchors invalid — reverting to defaults.");
                _cfg.HudAnchorMin = "0.34 0.905";
                _cfg.HudAnchorMax = "0.66 0.93";
            }

            if (_cfg.Track == null) _cfg.Track = new List<string> { "pvp", "raid" };
            if (_cfg.Display == null) _cfg.Display = new List<string>(DisplayNames);
            _cfg.Track = _cfg.Track.Where(t => Array.IndexOf(CatNames, t) >= 0).Distinct().ToList();
            _cfg.Display = _cfg.Display.Where(d => Array.IndexOf(DisplayNames, d) >= 0).Distinct().ToList();

            RebuildMasks();
        }

        private static bool TryParseAnchor(string s, out float x, out float y)
        {
            x = y = 0f;
            if (string.IsNullOrEmpty(s)) return false;
            var parts = s.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2
                   && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                   && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
        }

        private static string NormaliseMarkerMode(string s)
        {
            // "auto" is retained only so existing configs load; it now resolves to global.
            return (s ?? "").ToLowerInvariant() == "perplayer" ? "perplayer" : "global";
        }

        private readonly bool[] _trackMask = new bool[CatCount];
        private readonly bool[] _dispMask = new bool[DisplayNames.Length];

        private void RebuildMasks()
        {
            for (var i = 0; i < CatCount; i++) _trackMask[i] = _cfg.Track.Contains(CatNames[i]);
            for (var i = 0; i < DisplayNames.Length; i++) _dispMask[i] = _cfg.Display.Contains(DisplayNames[i]);
        }

        // ── Persisted per-player data ────────────────────────────────────────────
        private class PlayerPrefs
        {
            [JsonProperty("optOut")]    public bool OptOut { get; set; }
            [JsonProperty("hudHidden")] public bool HudHidden { get; set; }
        }

        private class StoredData
        {
            [JsonProperty("players")]
            public Dictionary<ulong, PlayerPrefs> Players { get; set; } = new Dictionary<ulong, PlayerPrefs>();
        }

        private StoredData _data = new StoredData();

        private void LoadData()
        {
            try
            {
                _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFile) ?? new StoredData();
                if (_data.Players == null) _data.Players = new Dictionary<ulong, PlayerPrefs>();
            }
            catch
            {
                _data = new StoredData();
                PrintWarning("MapPOI data file unreadable — starting fresh.");
            }
            RecomputeOptOutFlag();
        }

        private void SaveData()
        {
            try { Interface.Oxide.DataFileSystem.WriteObject(DataFile, _data); }
            catch (Exception e) { PrintWarning("Failed to save MapPOI data: " + e.Message); }
        }

        /// <summary>Creates the record — only for the command paths that are about to write.</summary>
        private PlayerPrefs Prefs(ulong id)
        {
            PlayerPrefs p;
            if (!_data.Players.TryGetValue(id, out p) || p == null)
            {
                p = new PlayerPrefs();
                _data.Players[id] = p;
            }
            return p;
        }

        /// <summary>Read-only lookup. The render and HUD loops run every tick for every
        /// player, so they must not populate the data file with empty default records.</summary>
        private static readonly PlayerPrefs DefaultPrefs = new PlayerPrefs();

        private PlayerPrefs PrefsRead(ulong id)
        {
            PlayerPrefs p;
            return _data.Players.TryGetValue(id, out p) && p != null ? p : DefaultPrefs;
        }

        private bool _anyOptOut;
        private void RecomputeOptOutFlag() => _anyOptOut = _data.Players.Values.Any(p => p != null && p.OptOut);

        // ── Battle model ─────────────────────────────────────────────────────────
        private class Battle
        {
            public int Id;

            // Exponentially-weighted accumulators. Positions are flattened to y = 0 so the
            // spread statistic measures ground distance and is not polluted by terrain height.
            public float SumW;
            public Vector3 SumWP;
            public float SumWP2;
            public float ExpW;
            public readonly float[] CatW = new float[CatCount];
            public readonly Dictionary<ulong, float> Actors = new Dictionary<ulong, float>();

            public float CreatedAt;
            public bool Published;

            // Render state
            public Vector3 RenderCentre;
            public float RenderRadiusMetres;
            public float NextRecentre;
            public Color LastFill;
            public float LastAlpha = -1f;
            public float LastNormRadius = -1f;
            public Vector3 LastSentCentre = new Vector3(float.MinValue, 0f, 0f);
            public float NextForcedPush;
            public string LastLabel;

            // Markers
            public MapMarkerGenericRadius GlobalCircle;
            public VendingMachineMapMarker GlobalLabel;
            public readonly Dictionary<ulong, MapMarkerGenericRadius> PlayerCircles = new Dictionary<ulong, MapMarkerGenericRadius>();
            public readonly Dictionary<ulong, VendingMachineMapMarker> PlayerLabels = new Dictionary<ulong, VendingMachineMapMarker>();
            public readonly Dictionary<ulong, Vector3> PlayerAnchor = new Dictionary<ulong, Vector3>();

            public Vector3 Centre => SumW > 0.0001f ? SumWP / SumW : Vector3.zero;

            public float Spread
            {
                get
                {
                    if (SumW <= 0.0001f) return 0f;
                    var c = Centre;
                    return Mathf.Sqrt(Mathf.Max(0f, SumWP2 / SumW - c.sqrMagnitude));
                }
            }

            public int DominantCategory
            {
                get
                {
                    var best = 0;
                    for (var i = 1; i < CatCount; i++) if (CatW[i] > CatW[best]) best = i;
                    return best;
                }
            }

            public float ExplosiveFraction => SumW > 0.0001f ? Mathf.Clamp01(ExpW / SumW) : 0f;
        }

        private struct PendingEvent
        {
            public float RevealAt;
            public Vector3 Pos;
            public float Weight;
            public int Cat;
            public bool Explosive;
            public ulong ActorA, ActorB;
        }

        private readonly List<Battle> _battles = new List<Battle>();
        private readonly Queue<PendingEvent> _pending = new Queue<PendingEvent>();
        private readonly List<MapMarkerExplosion> _explosionIcons = new List<MapMarkerExplosion>();
        private int _nextBattleId = 1;

        private float _lastTick;
        private Timer _tickTimer, _hudTimer;
        private bool _perPlayerActive;
        private MonumentInfo[] _monuments = new MonumentInfo[0];

        // Anti-abuse bookkeeping
        private readonly Dictionary<long, float> _lastPairHit = new Dictionary<long, float>();
        private readonly Dictionary<long, PairCap> _pairCaps = new Dictionary<long, PairCap>();
        private readonly List<long> _pruneScratch = new List<long>();
        private readonly List<ulong> _actorScratch = new List<ulong>();

        private class PairCap { public float WindowStart; public int Count; }

        // Diagnostics for `mappoi.debug` — cheap counters so a "why am I seeing nothing"
        // report can be answered without guessing.
        private int _dbgHookCalls, _dbgNoVictimKind, _dbgLowDamage, _dbgIngested, _dbgRejected;
        private string _dbgLastReject = "-", _dbgLastIngest = "-", _dbgLastNoKind = "-";
        private readonly Dictionary<string, int> _dbgReasons = new Dictionary<string, int>();

        private void Tally(string key)
        {
            int n;
            _dbgReasons[key] = _dbgReasons.TryGetValue(key, out n) ? n + 1 : 1;
        }

        private static long PairKey(ulong a, ulong b)
        {
            // Order-independent so A->B and B->A share one budget.
            var lo = a < b ? a : b;
            var hi = a < b ? b : a;
            return unchecked((long)(((lo & 0xFFFFFFFFUL) << 32) | (hi & 0xFFFFFFFFUL)));
        }

        // ── Lifecycle ────────────────────────────────────────────────────────────
        private void Init()
        {
            permission.RegisterPermission(AdminPerm, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            _monuments = UnityEngine.Object.FindObjectsOfType<MonumentInfo>() ?? new MonumentInfo[0];
            _lastTick = Time.realtimeSinceStartup;
            _perPlayerActive = WantPerPlayer();

            _tickTimer = timer.Every(1f, Tick);
            _hudTimer = timer.Every(_cfg.HudRefresh, RefreshAllHud);
            _pingTimer = timer.Every(_cfg.PingRefresh, RefreshAllPings);

            if (_cfg.LegacyDecoy)
            {
                foreach (var p in BasePlayer.activePlayerList) CreateLegacyMarker(p);
            }

            ApplyHookState();
        }

        private void Unload()
        {
            _tickTimer?.Destroy();
            _hudTimer?.Destroy();
            _pingTimer?.Destroy();

            ClearAllPings();
            foreach (var b in _battles) DestroyBattleMarkers(b);
            _battles.Clear();

            foreach (var icon in _explosionIcons)
                if (icon != null && !icon.IsDestroyed) icon.Kill();
            _explosionIcons.Clear();

            foreach (var kvp in _legacyMarkers)
                if (kvp.Value != null && !kvp.Value.IsDestroyed) kvp.Value.Kill();
            _legacyMarkers.Clear();

            foreach (var p in BasePlayer.activePlayerList)
                if (p != null) CuiHelper.DestroyUi(p, HudPanel);

            SaveData();
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (_cfg.LegacyDecoy)
            {
                timer.Once(2f, () => { if (player != null && player.IsConnected) CreateLegacyMarker(player); });
            }
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            ulong id = player.userID;
            _playerPings.Remove(id);   // pings live in player state and expire on their own

            foreach (var b in _battles)
            {
                MapMarkerGenericRadius circle;
                if (b.PlayerCircles.TryGetValue(id, out circle))
                {
                    if (circle != null && !circle.IsDestroyed) circle.Kill();
                    b.PlayerCircles.Remove(id);
                }
                VendingMachineMapMarker label;
                if (b.PlayerLabels.TryGetValue(id, out label))
                {
                    if (label != null && !label.IsDestroyed) label.Kill();
                    b.PlayerLabels.Remove(id);
                }
                b.PlayerAnchor.Remove(id);
            }

            MapMarkerGenericRadius legacy;
            if (_legacyMarkers.TryGetValue(id, out legacy))
            {
                _legacyMarkers.Remove(id);
                if (legacy != null && !legacy.IsDestroyed) legacy.Kill();
            }
        }

        /// <summary>Detaches the damage hooks entirely when the overlay can produce nothing,
        /// so a disabled plugin costs zero on the hottest hook on the server.</summary>
        private void ApplyHookState()
        {
            var wanted = _cfg.VisibilityDelay >= 0f && _cfg.Track.Count > 0;
            if (wanted)
            {
                Subscribe(nameof(OnEntityTakeDamage));
                Subscribe(nameof(OnEntityDeath));
            }
            else
            {
                Unsubscribe(nameof(OnEntityTakeDamage));
                Unsubscribe(nameof(OnEntityDeath));
                _pending.Clear();
            }
        }

        // ── Damage ingestion ─────────────────────────────────────────────────────
        private enum Kind { None, Player, Npc, Animal, Event, Structure }

        private static bool IsRealClient(BasePlayer p)
            => p != null && !p.IsNpc && p.userID.IsSteamId() && p.net?.connection != null;

        /// <summary>Victims count as real players even while sleeping (offline raid kills),
        /// but SNPC bots must not — they are BasePlayer with IsNpc == false, so the reliable
        /// discriminator is the absence of a client connection on a Steam-ID account.</summary>
        private static bool IsRealVictim(BasePlayer p)
            => p != null && !p.IsNpc && p.userID.IsSteamId() && (p.net?.connection != null || p.IsSleeping());

        private Kind KindOf(BaseEntity e, bool asVictim)
        {
            if (e == null) return Kind.None;
            var bp = e as BasePlayer;
            if (bp != null)
            {
                if (asVictim) return IsRealVictim(bp) ? Kind.Player : Kind.Npc;
                return IsRealClient(bp) ? Kind.Player : Kind.Npc;
            }
            // Old-generation animals derive from BaseAnimalNPC; the new BaseNPC2-based ones
            // (Wolf2, Crocodile, Panther) do not, and would otherwise be classified as NPC
            // combat — so turning off the `animal` category would not actually silence them.
            if (e is BaseAnimalNPC || IsAnimalType(e.GetType())) return Kind.Animal;
            if (e is PatrolHelicopter || e is BradleyAPC || e is CargoShip || e is CH47Helicopter) return Kind.Event;

            // Not every NPC derives from BasePlayer. Verified on this build:
            //   ScientistNPC2 <- BaseNPC2 <- BaseCombatEntity
            // BaseNPC2 is a SEPARATE hierarchy from the old BaseNpc, and it is *internal*, so
            // it cannot be named from plugin code at all — hence the name-walk below. Testing
            // only BasePlayer (or only BaseNpc) silently drops every monument fight.
            if (IsNpcType(e.GetType())) return Kind.Npc;

            if (e is DecayEntity) return Kind.Structure;

            // Attacker side only: something is dealing damage and we could not name it — a
            // future NPC type, a turret, a trap. Counting it as NPC combat beats discarding
            // the fight. Vehicles and corpses are excluded (roadkill is not a battle), and
            // victims stay strict or shooting a barrel would spawn one.
            if (!asVictim && e is BaseCombatEntity && !(e is BaseVehicle) && !(e is BaseCorpse))
                return Kind.Npc;

            return Kind.None;
        }

        // Resolved by name because BaseNPC2 is internal to Assembly-CSharp and plugins compile
        // into a separate assembly. Cached per System.Type — the walk runs once per distinct
        // entity class, not once per bullet.
        private static readonly Dictionary<Type, bool> NpcTypeCache = new Dictionary<Type, bool>();

        private static readonly HashSet<string> AnimalTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Wolf", "Wolf2", "Bear", "Bear2", "Polarbear", "Polarbear2", "Boar", "Boar2",
            "Chicken", "Chicken2", "Stag", "Stag2", "Crocodile", "Panther", "Tiger",
            "Snake", "Shark", "SimpleShark", "Horse", "RidableHorse2"
        };

        private static readonly Dictionary<Type, bool> AnimalTypeCache = new Dictionary<Type, bool>();

        private static bool IsAnimalType(Type t)
        {
            bool cached;
            if (AnimalTypeCache.TryGetValue(t, out cached)) return cached;

            var result = false;
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                if (AnimalTypeNames.Contains(cur.Name)) { result = true; break; }
            }

            AnimalTypeCache[t] = result;
            return result;
        }

        private static bool IsNpcType(Type t)
        {
            bool cached;
            if (NpcTypeCache.TryGetValue(t, out cached)) return cached;

            var result = false;
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                var n = cur.Name;
                if (n == "BaseNPC2" || n == "BaseNpc" || n == "NPCPlayer" || n == "HumanNPC")
                {
                    result = true;
                    break;
                }
            }

            NpcTypeCache[t] = result;
            return result;
        }

        /// <summary>Reports a type and its base chain, so an unrecognised entity can be
        /// classified without guessing against a stale decompiled dump.</summary>
        private static string TypeChain(object o)
        {
            if (o == null) return "null";
            var sb = new System.Text.StringBuilder();
            for (var t = o.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append(t.Name);
            }
            return sb.ToString();
        }

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            // This is the hottest hook on the server — cheapest tests first, no allocation,
            // no LINQ, and bail before touching damageTypes wherever possible.
            if (entity == null || info == null) return;
            _dbgHookCalls++;

            var victimKind = KindOf(entity, true);
            if (victimKind == Kind.None)
            {
                _dbgNoVictimKind++;
                _dbgLastNoKind = entity.GetType().Name;
                Tally("victimKind=None: " + TypeChain(entity));
                return;
            }

            var initiator = info.Initiator;
            var attacker = info.InitiatorPlayer;
            var attackerKind = attacker != null ? KindOf(attacker, false) : KindOf(initiator, false);

            var dmg = info.damageTypes?.Total() ?? 0f;
            var explosive = (info.damageTypes?.GetMajorityDamageType() ?? Rust.DamageType.Generic) == Rust.DamageType.Explosion;
            if (dmg < _cfg.MinDamage && !explosive) { _dbgLowDamage++; return; }

            var pos = entity.transform.position;
            ulong actorA = attacker != null ? (ulong)attacker.userID : 0UL;
            // Count the victim as a participant whether or not they are a real player —
            // otherwise a player shooting scientists only ever registers one actor and can
            // never satisfy the >=2 participant publication gate.
            var victimPlayer = entity as BasePlayer;
            ulong actorB = victimPlayer != null ? (ulong)victimPlayer.userID : 0UL;

            int cat;
            float weight;

            if (victimKind == Kind.Player)
            {
                var victim = victimPlayer;

                if (attackerKind == Kind.Player)
                {
                    if (actorA == actorB) { Reject("self-damage"); return; }
                    if (SameTeam(attacker, victim)) { Reject("same-team"); return; }
                    if (!_trackMask[CatPvp]) { Reject("pvp not tracked"); return; }
                    if (!PassAbuseGates(actorA, actorB)) { Reject("abuse gate (pellet/pair cap)"); return; }

                    cat = CatPvp;
                    weight = explosive
                        ? _cfg.WeightExplosivePlayer
                        : _cfg.WeightHit + (dmg > 50f ? _cfg.WeightHeavyBonus : 0f);
                }
                else if (attackerKind == Kind.Event)
                {
                    if (!_trackMask[CatEvent]) { Reject("events not tracked"); return; }
                    cat = CatEvent; weight = _cfg.WeightEvent;
                }
                else if (attackerKind == Kind.Npc)
                {
                    if (InMonument(pos) && _trackMask[CatMonument]) cat = CatMonument;
                    else if (_trackMask[CatNpc]) cat = CatNpc;
                    else { Reject("npc->player, neither monuments nor npc tracked"); return; }
                    // A real player is one of the parties, so weight it as a PvE fight even when
                    // the bounds say otherwise — Large Oil Rig reports atMonument=False, and at
                    // the NPC-vs-NPC weight it would need ~12 hits to ever become visible.
                    weight = _cfg.WeightMonument;
                }
                else if (attackerKind == Kind.Animal)
                {
                    if (!_trackMask[CatAnimal]) { Reject("animal not tracked"); return; }
                    cat = CatAnimal; weight = _cfg.WeightAnimal;
                }
                else
                {
                    // Name the actual initiator: "None" almost always means bleeding, fall,
                    // cold, radiation or a turret rather than a real combatant.
                    var who = initiator == null ? "null-initiator" : TypeChain(initiator);
                    var dt = info.damageTypes?.GetMajorityDamageType().ToString() ?? "?";
                    Reject($"player victim, attackerKind={attackerKind} via {who} [{dt}]");
                    return;
                }
            }
            else if (victimKind == Kind.Structure)
            {
                // Raids only: explosive damage to a player-owned structure.
                if (!explosive) { Reject("structure hit, not explosive"); return; }
                if (attackerKind != Kind.Player) { Reject("structure hit, attackerKind=" + attackerKind); return; }
                if (!_trackMask[CatRaid]) { Reject("raid not tracked"); return; }
                if (!entity.OwnerID.IsSteamId()) { Reject("structure not player-owned"); return; }
                cat = CatRaid; weight = _cfg.WeightRaid;
            }
            else if (victimKind == Kind.Event)
            {
                if (attackerKind != Kind.Player) { Reject("event victim, attackerKind=" + attackerKind); return; }
                if (!_trackMask[CatEvent]) { Reject("events not tracked"); return; }
                cat = CatEvent; weight = _cfg.WeightEvent;
            }
            else if (victimKind == Kind.Animal)
            {
                if (attackerKind != Kind.Player) { Reject("animal victim, attackerKind=" + attackerKind); return; }
                if (!_trackMask[CatAnimal]) { Reject("animal not tracked"); return; }
                cat = CatAnimal; weight = _cfg.WeightAnimal;
            }
            else // Kind.Npc victim
            {
                if (attackerKind == Kind.Player)
                {
                    if (InMonument(pos) && _trackMask[CatMonument]) cat = CatMonument;
                    else if (_trackMask[CatNpc]) cat = CatNpc;
                    else { Reject("player->npc, neither monuments nor npc tracked"); return; }
                    weight = _cfg.WeightMonument;   // player involved — see note above
                }
                else if (attackerKind == Kind.Npc || attackerKind == Kind.Event)
                {
                    if (!_trackMask[CatNpc]) { Reject("npc not tracked"); return; }
                    cat = CatNpc; weight = _cfg.WeightNpc;
                }
                else { Reject("npc victim, attackerKind=" + attackerKind); return; }
            }

            _dbgIngested++;
            _dbgLastIngest = $"{CatNames[cat]} w={weight:F2} actors={(actorA != 0 ? 1 : 0) + (actorB != 0 ? 1 : 0)} " +
                             $"{FormatRustMapGrid(pos)} monument={InMonument(pos)}";

            if (explosive && (cat == CatPvp || cat == CatRaid)) TrySpawnExplosionIcon(pos);
            Ingest(pos, weight, cat, explosive, actorA, actorB);
        }

        private void Reject(string why)
        {
            _dbgRejected++;
            _dbgLastReject = why;
            Tally("reject: " + why);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (!_trackMask[CatPvp] || entity == null || info == null) return;
            var victim = entity as BasePlayer;
            if (!IsRealVictim(victim)) return;

            var attacker = info.InitiatorPlayer;
            if (!IsRealClient(attacker)) return;

            ulong a = attacker.userID, b = victim.userID;
            if (a == b || SameTeam(attacker, victim)) return;

            Ingest(entity.transform.position, _cfg.WeightKill, CatPvp, false, a, b);
        }

        private static bool SameTeam(BasePlayer a, BasePlayer b)
            => a != null && b != null && a.currentTeam != 0UL && a.currentTeam == b.currentTeam;

        private bool InMonument(Vector3 pos)
        {
            for (var i = 0; i < _monuments.Length; i++)
            {
                var m = _monuments[i];
                if (m != null && m.IsInBounds(pos)) return true;
            }
            return false;
        }

        /// <summary>Pellet coalescing plus a per-pair rate cap. Without the first a shotgun
        /// counts 12x an AK round; without the second one player farming an alt can max a
        /// circle on demand and use it to bait or to mask a real raid.</summary>
        private bool PassAbuseGates(ulong a, ulong b)
        {
            if (a == 0UL || b == 0UL) return true;
            var now = Time.realtimeSinceStartup;
            var key = PairKey(a, b);

            float last;
            if (_cfg.PelletWindow > 0f && _lastPairHit.TryGetValue(key, out last) && now - last < _cfg.PelletWindow)
                return false;
            _lastPairHit[key] = now;

            PairCap cap;
            if (!_pairCaps.TryGetValue(key, out cap))
            {
                cap = new PairCap { WindowStart = now, Count = 0 };
                _pairCaps[key] = cap;
            }
            if (now - cap.WindowStart >= _cfg.PairCapWindow) { cap.WindowStart = now; cap.Count = 0; }
            if (cap.Count >= _cfg.PairCapEvents) return false;
            cap.Count++;
            return true;
        }

        private void Ingest(Vector3 pos, float weight, int cat, bool explosive, ulong a, ulong b)
        {
            if (weight <= 0f) return;
            pos.y = 0f;

            if (_cfg.VisibilityDelay > 0.01f)
            {
                _pending.Enqueue(new PendingEvent
                {
                    RevealAt = Time.realtimeSinceStartup + _cfg.VisibilityDelay,
                    Pos = pos, Weight = weight, Cat = cat, Explosive = explosive, ActorA = a, ActorB = b
                });
                return;
            }

            Apply(pos, weight, cat, explosive, a, b);
        }

        private void Apply(Vector3 pos, float weight, int cat, bool explosive, ulong a, ulong b)
        {
            var battle = FindOrCreateBattle(pos);
            if (battle == null) return;

            battle.SumW += weight;
            battle.SumWP += pos * weight;
            battle.SumWP2 += weight * pos.sqrMagnitude;
            battle.CatW[cat] += weight;
            if (explosive) battle.ExpW += weight;

            if (a != 0UL) AddActor(battle, a, weight);
            if (b != 0UL) AddActor(battle, b, weight);
        }

        private static void AddActor(Battle b, ulong id, float weight)
        {
            float cur;
            b.Actors[id] = b.Actors.TryGetValue(id, out cur) ? cur + weight : weight;
        }

        /// <summary>How far out a battle still absorbs new events. This grows with the battle's
        /// own spread: a base raid with attackers on three sides must stay ONE battle (and so
        /// draw one large circle) rather than fragmenting into a cluster of identical small ones,
        /// while a tight duel keeps a tight catchment.</summary>
        private float JoinRadius(Battle b) => Mathf.Min(_cfg.MergeRadius + b.Spread, _cfg.MaxJoinRadius);

        private Battle FindOrCreateBattle(Vector3 pos)
        {
            Battle best = null;
            var bestSqr = float.MaxValue;

            for (var i = 0; i < _battles.Count; i++)
            {
                var b = _battles[i];
                var join = JoinRadius(b);
                var sqr = (b.Centre - pos).sqrMagnitude;
                if (sqr <= join * join && sqr < bestSqr) { bestSqr = sqr; best = b; }
            }
            if (best != null) return best;

            if (_battles.Count >= _cfg.MaxBattles)
            {
                // At capacity: only displace the coldest battle if it is genuinely cold.
                Battle coldest = null;
                foreach (var b in _battles) if (coldest == null || b.SumW < coldest.SumW) coldest = b;
                if (coldest == null || coldest.SumW > _cfg.MinPublishHeat) return null;
                DestroyBattleMarkers(coldest);
                _battles.Remove(coldest);
            }

            var fresh = new Battle { Id = _nextBattleId++, CreatedAt = Time.realtimeSinceStartup };
            _battles.Add(fresh);
            return fresh;
        }

        // ── Tick ─────────────────────────────────────────────────────────────────
        private void Tick()
        {
            var now = Time.realtimeSinceStartup;
            var dt = Mathf.Max(0.001f, now - _lastTick);
            _lastTick = now;

            DrainPending(now);
            DecayBattles(dt);
            MergeBattles();
            ExpireBattles();
            PruneAbuseTables(now);
            PruneExplosionIcons();

            var wantPerPlayer = WantPerPlayer();
            if (wantPerPlayer != _perPlayerActive)
            {
                PrintWarning(wantPerPlayer
                    ? "Switching to per-player markers (someone has opted out) — cost is battles x viewers."
                    : "Switching to global markers (no opt-outs remaining).");
                foreach (var b in _battles) DestroyBattleMarkers(b);
                _perPlayerActive = wantPerPlayer;
            }

            RenderBattles(now);
            if (_cfg.LegacyDecoy) TickLegacyMarkers();
        }

        private void DrainPending(float now)
        {
            while (_pending.Count > 0 && _pending.Peek().RevealAt <= now)
            {
                var e = _pending.Dequeue();
                Apply(e.Pos, e.Weight, e.Cat, e.Explosive, e.ActorA, e.ActorB);
            }
        }

        private void DecayBattles(float dt)
        {
            var f = Mathf.Pow(0.5f, dt / _cfg.HalfLife);
            foreach (var b in _battles)
            {
                b.SumW *= f;
                b.SumWP *= f;
                b.SumWP2 *= f;
                b.ExpW *= f;
                for (var i = 0; i < CatCount; i++) b.CatW[i] *= f;

                // Snapshot the keys first: on Mono even overwriting an existing key counts as
                // a structural modification and invalidates a live enumerator.
                _actorScratch.Clear();
                foreach (var key in b.Actors.Keys) _actorScratch.Add(key);
                for (var i = 0; i < _actorScratch.Count; i++)
                {
                    var key = _actorScratch[i];
                    var v = b.Actors[key] * f;
                    if (v < 0.05f) b.Actors.Remove(key);
                    else b.Actors[key] = v;
                }
            }
        }

        /// <summary>Merging is exact: the accumulators are decayed weighted sums, so adding
        /// them pairwise yields the same statistics as if both streams had been one battle.</summary>
        private void MergeBattles()
        {
            for (var i = 0; i < _battles.Count; i++)
            {
                for (var j = _battles.Count - 1; j > i; j--)
                {
                    var a = _battles[i];
                    var b = _battles[j];

                    // Mirror JoinRadius: two sprawling battles that overlap should coalesce,
                    // two tight ones a hundred metres apart should not.
                    var mergeDist = Mathf.Min(_cfg.MergeRadius * 0.6f + 0.5f * (a.Spread + b.Spread), _cfg.MaxJoinRadius);
                    if ((a.Centre - b.Centre).sqrMagnitude > mergeDist * mergeDist) continue;

                    // Always fold the colder battle into the hotter one, then drop it. Keeping
                    // the survivor at index i lets the outer loop continue without reindexing.
                    var keep = a.SumW >= b.SumW ? a : b;
                    var drop = keep == a ? b : a;

                    keep.SumW += drop.SumW;
                    keep.SumWP += drop.SumWP;
                    keep.SumWP2 += drop.SumWP2;
                    keep.ExpW += drop.ExpW;
                    for (var c = 0; c < CatCount; c++) keep.CatW[c] += drop.CatW[c];
                    foreach (var kvp in drop.Actors) AddActor(keep, kvp.Key, kvp.Value);

                    DestroyBattleMarkers(drop);
                    _battles.RemoveAt(j);
                    _battles[i] = keep;
                }
            }
        }

        private void ExpireBattles()
        {
            var now = Time.realtimeSinceStartup;
            for (var i = _battles.Count - 1; i >= 0; i--)
            {
                if (_battles[i].SumW >= _cfg.DeathThreshold) continue;
                // Grace period: a battle seeded by a single low-weight event must survive long
                // enough for follow-up hits to land, or a slow-burn fight never accumulates.
                if (now - _battles[i].CreatedAt < _cfg.GraceSeconds) continue;
                DestroyBattleMarkers(_battles[i]);
                _battles.RemoveAt(i);
            }

            if (_battles.Count <= _cfg.MaxBattles) return;
            _battles.Sort((x, y) => y.SumW.CompareTo(x.SumW));
            for (var i = _battles.Count - 1; i >= _cfg.MaxBattles; i--)
            {
                DestroyBattleMarkers(_battles[i]);
                _battles.RemoveAt(i);
            }
        }

        private void PruneAbuseTables(float now)
        {
            _pruneScratch.Clear();
            foreach (var kvp in _lastPairHit) if (now - kvp.Value > 60f) _pruneScratch.Add(kvp.Key);
            for (var i = 0; i < _pruneScratch.Count; i++) _lastPairHit.Remove(_pruneScratch[i]);

            _pruneScratch.Clear();
            foreach (var kvp in _pairCaps) if (now - kvp.Value.WindowStart > 60f) _pruneScratch.Add(kvp.Key);
            for (var i = 0; i < _pruneScratch.Count; i++) _pairCaps.Remove(_pruneScratch[i]);
        }

        private void PruneExplosionIcons()
        {
            for (var i = _explosionIcons.Count - 1; i >= 0; i--)
                if (_explosionIcons[i] == null || _explosionIcons[i].IsDestroyed) _explosionIcons.RemoveAt(i);
        }

        private bool IsPublished(Battle b)
            => b.SumW >= _cfg.MinPublishHeat && b.Actors.Count >= _cfg.MinParticipants;

        /// <summary>Only an explicit opt-in selects per-player markers. It used to switch
        /// automatically as soon as anyone opted out, which silently broke every circle that
        /// was not right next to the viewer.</summary>
        private bool WantPerPlayer() => _cfg.MarkerMode == "perplayer";

        // ── Geometry: the three POI styles ───────────────────────────────────────
        private void ComputeCircle(Battle b, float now, out Vector3 centre, out float radiusMetres)
        {
            var maxR = _cfg.RadiusMaxGrid * GridCell;
            centre = b.Centre;

            switch (_cfg.PoiStyle)
            {
                // Styles 2 and 3 get their imprecision from the fuzzed CENTRE, so the radius is
                // free to encode participant count over its full 0.3-1.0 grid range. Applying
                // style 1's uncertainty floor here as well would stack two errors and squash
                // the range flat — only the sub-pixel visibility minimum applies.
                case 2: // Fuzzed centre, offset re-rolled on every recentre.
                    centre += RandomFuzz(UnityEngine.Random.Range(0f, Mathf.PI * 2f), UnityEngine.Random.value);
                    radiusMetres = ParticipantRadius(b);
                    break;

                case 3: // Fuzzed centre, offset fixed for the battle's lifetime — no wobble.
                {
                    var rng = new System.Random(b.Id * 73856093);
                    centre += RandomFuzz((float)(rng.NextDouble() * Math.PI * 2.0), (float)rng.NextDouble());
                    radiusMetres = ParticipantRadius(b);
                    break;
                }

                default: // 1: honest centroid — the radius IS the uncertainty, so it is floored.
                    radiusMetres = Mathf.Clamp(b.Spread * 2f, _cfg.RadiusMinGrid * GridCell, maxR);
                    break;
            }

            radiusMetres = Mathf.Min(radiusMetres, maxR) * _cfg.CircleScale;
            centre.y = TerrainHeight(centre);
        }

        /// <summary>Styles 2/3 size the circle by distinct participants, 0.3 grid for a duel up
        /// to 1.0 grid at six or more. Floored at 0.25 grid because anything under ~20m is
        /// sub-pixel on the client map and simply invisible.</summary>
        private static float ParticipantRadius(Battle b)
        {
            var t = Mathf.Clamp01((b.Actors.Count - 1) / 5f);
            return Mathf.Max(Mathf.Lerp(0.3f, 1.0f, t), 0.25f) * GridCell;
        }

        private static Vector3 RandomFuzz(float angle, float unit)
        {
            // sqrt keeps the offset uniform over the disc rather than clustered at the centre.
            var dist = Mathf.Sqrt(Mathf.Clamp01(unit)) * 0.8f * GridCell;
            return new Vector3(Mathf.Cos(angle) * dist, 0f, Mathf.Sin(angle) * dist);
        }

        private static float TerrainHeight(Vector3 p)
            => TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(new Vector3(p.x, 0f, p.z)) : 0f;

        private static float WorldSize => TerrainMeta.Size.x > 0f ? TerrainMeta.Size.x : 4000f;

        // ── Colour ───────────────────────────────────────────────────────────────
        private static readonly Color[] CatBase =
        {
            new Color(1.00f, 0.55f, 0.10f), // pvp       — orange
            new Color(1.00f, 0.30f, 0.05f), // raid      — hot orange
            new Color(0.20f, 0.75f, 0.95f), // monuments — cyan
            new Color(0.80f, 0.25f, 0.90f), // events    — magenta
            new Color(0.55f, 0.65f, 0.50f), // npc       — grey-green
            new Color(0.35f, 0.80f, 0.35f), // animal    — green
        };

        private static readonly Color PvpHot  = new Color(1.00f, 0.10f, 0.10f); // red
        private static readonly Color RaidHot = new Color(0.80f, 0.00f, 0.00f); // crimson

        private Color FillColour(Battle b)
        {
            var cat = b.DominantCategory;
            var basec = CatBase[cat];
            if (cat == CatPvp) return Color.Lerp(basec, PvpHot, b.ExplosiveFraction);
            if (cat == CatRaid) return Color.Lerp(basec, RaidHot, b.ExplosiveFraction);
            return basec;
        }

        private float FillAlpha(Battle b)
            => Mathf.Clamp(_cfg.AlphaMax * (b.SumW / _cfg.RefHeat), _cfg.AlphaMin, _cfg.AlphaMax);

        private static Color Brighten(Color c, float amount)
            => new Color(Mathf.Clamp01(c.r + amount), Mathf.Clamp01(c.g + amount), Mathf.Clamp01(c.b + amount), 1f);

        // ── Rendering ────────────────────────────────────────────────────────────
        private void RenderBattles(float now)
        {
            foreach (var b in _battles)
            {
                if (!IsPublished(b))
                {
                    if (b.Published) { DestroyBattleMarkers(b); b.Published = false; }
                    continue;
                }

                // Latch the centre on the recentre interval so the circle does not creep
                // every second; colour and alpha still update smoothly in between.
                if (!b.Published || now >= b.NextRecentre)
                {
                    Vector3 centre; float radius;
                    ComputeCircle(b, now, out centre, out radius);
                    b.RenderCentre = centre;
                    b.RenderRadiusMetres = radius;
                    b.NextRecentre = now + _cfg.RecentreInterval;
                }
                b.Published = true;

                var fill = FillColour(b);
                var alpha = FillAlpha(b);
                var normRadius = b.RenderRadiusMetres / WorldSize;

                var moved = (b.RenderCentre - b.LastSentCentre).sqrMagnitude > 225f; // >15m
                var changed = moved
                    || Mathf.Abs(normRadius - b.LastNormRadius) > b.LastNormRadius * 0.02f
                    || Mathf.Abs(alpha - b.LastAlpha) > 0.03f
                    || ColourDelta(fill, b.LastFill) > 0.02f
                    || now >= b.NextForcedPush;

                if (_dispMask[DispCircles])
                {
                    if (_perPlayerActive) RenderPerPlayerCircles(b, fill, alpha, normRadius, changed);
                    else RenderGlobalCircle(b, fill, alpha, normRadius, changed);
                }
                else DestroyCircles(b);

                if (_dispMask[DispLabels]) RenderLabels(b);
                else DestroyLabels(b);

                if (changed)
                {
                    b.LastFill = fill;
                    b.LastAlpha = alpha;
                    b.LastNormRadius = normRadius;
                    b.LastSentCentre = b.RenderCentre;
                    b.NextForcedPush = now + 10f;
                }
            }
        }

        private static float ColourDelta(Color a, Color b)
            => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);

        private void RenderGlobalCircle(Battle b, Color fill, float alpha, float normRadius, bool changed)
        {
            if (b.GlobalCircle == null || b.GlobalCircle.IsDestroyed)
            {
                b.GlobalCircle = SpawnGlobalCircle(b.RenderCentre, fill, alpha, normRadius);
                if (b.GlobalCircle == null) return;
                return;   // visuals were applied pre-spawn and pushed; nothing more to send
            }

            if (!changed) return;

            var m = b.GlobalCircle;
            m.transform.position = b.RenderCentre;
            m.color1 = fill;
            m.color2 = Brighten(fill, 0.4f);
            m.alpha = alpha;
            m.radius = normRadius;
            m.SendNetworkUpdate();
            m.SendUpdate();
        }

        /// <summary>Visuals MUST be set before Spawn — matching the v1.2.0 recipe. Spawning a
        /// marker with radius 0 and pushing values afterwards produces an invisible circle even
        /// though the entity exists server-side and the RPC is sent.</summary>
        private MapMarkerGenericRadius SpawnGlobalCircle(Vector3 pos, Color fill, float alpha, float normRadius)
        {
            var m = GameManager.server.CreateEntity(CirclePrefab, pos) as MapMarkerGenericRadius;
            if (m == null) { PrintError("Failed to create battle circle marker."); return null; }

            m.enableSaving = false;
            m.globalBroadcast = true;
            m.color1 = fill;
            m.color2 = Brighten(fill, 0.4f);
            m.alpha = alpha;
            m.radius = normRadius;
            m.Spawn();
            m.UpdateNetworkGroup();   // globalBroadcast was set pre-spawn; force the group switch
            m.SendUpdate();
            return m;
        }

        private void RenderPerPlayerCircles(Battle b, Color fill, float alpha, float normRadius, bool changed)
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected || player.net?.connection == null) continue;
                ulong id = player.userID;
                if (PrefsRead(id).OptOut) { KillPlayerCircle(b, id); continue; }

                MapMarkerGenericRadius m;
                var fresh = false;
                if (!b.PlayerCircles.TryGetValue(id, out m) || m == null || m.IsDestroyed)
                {
                    m = SpawnPlayerCircle(player, b.RenderCentre, fill, alpha, normRadius);
                    if (m == null) continue;
                    b.PlayerCircles[id] = m;
                    b.PlayerAnchor[id] = player.transform.position;
                    fresh = true;
                }

                // A parented marker rides its parent, so re-anchor once the viewer has drifted
                // past the tolerance. On a 234m-wide circle a 15m lag is imperceptible, and a
                // stationary viewer costs nothing.
                Vector3 anchor;
                var drifted = !b.PlayerAnchor.TryGetValue(id, out anchor)
                              || (player.transform.position - anchor).sqrMagnitude > _cfg.PerPlayerTolerance * _cfg.PerPlayerTolerance;

                if (fresh) continue;              // already applied and pushed pre-spawn
                if (!changed && !drifted) continue;

                m.transform.position = b.RenderCentre;
                m.color1 = fill;
                m.color2 = Brighten(fill, 0.4f);
                m.alpha = alpha;
                m.radius = normRadius;
                m.SendNetworkUpdate();
                SendMarkerUpdate(m, player);
                b.PlayerAnchor[id] = player.transform.position;
            }

            // Drop markers belonging to players who are no longer online.
            _actorScratch.Clear();
            foreach (var kvp in b.PlayerCircles)
            {
                var p = BasePlayer.FindByID(kvp.Key);
                if (p == null || !p.IsConnected) _actorScratch.Add(kvp.Key);
            }
            for (var i = 0; i < _actorScratch.Count; i++) KillPlayerCircle(b, _actorScratch[i]);
        }

        private MapMarkerGenericRadius SpawnPlayerCircle(BasePlayer player, Vector3 pos,
                                                        Color fill, float alpha, float normRadius)
        {
            var m = GameManager.server.CreateEntity(CirclePrefab, pos) as MapMarkerGenericRadius;
            if (m == null) return null;

            m.OwnerID = player.userID;
            m.enableSaving = false;
            // Parent before Spawn so the entity lands in the player's network group;
            // limitNetworking then restricts ShouldNetworkTo to this player only.
            m.SetParent(player, worldPositionStays: true);
            m.limitNetworking = true;
            // Visuals before Spawn — see SpawnGlobalCircle.
            m.color1 = fill;
            m.color2 = Brighten(fill, 0.4f);
            m.alpha = alpha;
            m.radius = normRadius;
            m.Spawn();
            SendMarkerUpdate(m, player);
            return m;
        }

        private void KillPlayerCircle(Battle b, ulong id)
        {
            MapMarkerGenericRadius m;
            if (b.PlayerCircles.TryGetValue(id, out m))
            {
                if (m != null && !m.IsDestroyed) m.Kill();
                b.PlayerCircles.Remove(id);
            }
            VendingMachineMapMarker l;
            if (b.PlayerLabels.TryGetValue(id, out l))
            {
                if (l != null && !l.IsDestroyed) l.Kill();
                b.PlayerLabels.Remove(id);
            }
            b.PlayerAnchor.Remove(id);
        }

        /// <summary>color1/color2/alpha/radius never ride the entity snapshot — they only
        /// reach the client through this RPC.</summary>
        private static void SendMarkerUpdate(MapMarkerGenericRadius m, BasePlayer player)
        {
            m.ClientRPC(
                RpcTarget.Player("MarkerUpdate", player),
                new Vector3(m.color1.r, m.color1.g, m.color1.b),
                m.color1.a,
                new Vector3(m.color2.r, m.color2.g, m.color2.b),
                m.alpha,
                m.radius);
        }

        // ── Labels ───────────────────────────────────────────────────────────────
        private string LabelFor(Battle b)
        {
            var cat = b.DominantCategory;
            var n = b.Actors.Count;
            string title;
            switch (cat)
            {
                // BMP characters only — non-BMP emoji (surrogate pairs) do not survive the trip
                // to the client here, rendering as raw code-point text like "F4A5".
                case CatPvp: title = n <= 3 ? "⚔ Skirmish" : (n <= 8 ? "⚔ Firefight" : "⚔ War"); break;
                case CatRaid: title = "⚑ Raid"; break;
                case CatMonument: title = "☠ Monument"; break;
                case CatEvent: title = "✈ Event"; break;
                case CatNpc: title = "⚙ NPC combat"; break;
                default: title = "✦ Hunting"; break;
            }
            return $"{title} · {n} · {FormatRustMapGrid(b.RenderCentre)}";
        }

        private void RenderLabels(Battle b)
        {
            var text = LabelFor(b);
            var textChanged = text != b.LastLabel;

            if (_perPlayerActive)
            {
                foreach (var kvp in b.PlayerCircles)
                {
                    var player = BasePlayer.FindByID(kvp.Key);
                    if (player == null || !player.IsConnected) continue;

                    VendingMachineMapMarker l;
                    if (!b.PlayerLabels.TryGetValue(kvp.Key, out l) || l == null || l.IsDestroyed)
                    {
                        l = SpawnLabel(b.RenderCentre, text, player);
                        if (l == null) continue;
                        b.PlayerLabels[kvp.Key] = l;
                        continue;
                    }
                    if (!textChanged && (l.transform.position - b.RenderCentre).sqrMagnitude < 1f) continue;
                    l.markerShopName = text;
                    l.transform.position = b.RenderCentre;
                    l.SendNetworkUpdate();
                }
            }
            else
            {
                if (b.GlobalLabel == null || b.GlobalLabel.IsDestroyed)
                {
                    b.GlobalLabel = SpawnLabel(b.RenderCentre, text, null);
                }
                else if (textChanged || (b.GlobalLabel.transform.position - b.RenderCentre).sqrMagnitude > 1f)
                {
                    b.GlobalLabel.markerShopName = text;
                    b.GlobalLabel.transform.position = b.RenderCentre;
                    b.GlobalLabel.SendNetworkUpdate();
                }
            }

            b.LastLabel = text;
        }

        private VendingMachineMapMarker SpawnLabel(Vector3 pos, string text, BasePlayer owner)
        {
            var l = GameManager.server.CreateEntity(LabelPrefab, pos) as VendingMachineMapMarker;
            if (l == null) return null;
            l.enableSaving = false;
            // markerShopName IS written into the snapshot by VendingMachineMapMarker.Save(),
            // and server_vendingMachine staying null is handled there.
            l.markerShopName = text;
            if (owner != null)
            {
                l.OwnerID = owner.userID;
                l.globalBroadcast = false;
                l.SetParent(owner, worldPositionStays: true);
                l.limitNetworking = true;
                l.Spawn();
                l.transform.position = pos;
            }
            else
            {
                l.globalBroadcast = true;
                l.Spawn();
                l.UpdateNetworkGroup();
            }
            return l;
        }

        // ── Explosion icons ──────────────────────────────────────────────────────
        private float _nextExplosionIcon;

        private void TrySpawnExplosionIcon(Vector3 pos)
        {
            if (!_dispMask[DispExplosions]) return;
            var now = Time.realtimeSinceStartup;
            if (now < _nextExplosionIcon) return;
            _nextExplosionIcon = now + _cfg.ExplosionIconSpacing;

            pos.y = TerrainHeight(pos);
            var icon = GameManager.server.CreateEntity(ExplosionPrefab, pos) as MapMarkerExplosion;
            if (icon == null) return;
            icon.enableSaving = false;
            icon.globalBroadcast = true;
            icon.Spawn();
            icon.UpdateNetworkGroup();
            icon.SetDuration(_cfg.ExplosionIconSeconds / 60f);   // SetDuration() takes MINUTES
            _explosionIcons.Add(icon);
        }

        // ── Marker teardown ──────────────────────────────────────────────────────
        private void DestroyCircles(Battle b)
        {
            if (b.GlobalCircle != null && !b.GlobalCircle.IsDestroyed) b.GlobalCircle.Kill();
            b.GlobalCircle = null;
            foreach (var m in b.PlayerCircles.Values) if (m != null && !m.IsDestroyed) m.Kill();
            b.PlayerCircles.Clear();
            b.PlayerAnchor.Clear();
        }

        private void DestroyLabels(Battle b)
        {
            if (b.GlobalLabel != null && !b.GlobalLabel.IsDestroyed) b.GlobalLabel.Kill();
            b.GlobalLabel = null;
            foreach (var l in b.PlayerLabels.Values) if (l != null && !l.IsDestroyed) l.Kill();
            b.PlayerLabels.Clear();
        }

        private void DestroyBattleMarkers(Battle b)
        {
            DestroyCircles(b);
            DestroyLabels(b);
            b.LastLabel = null;
            b.LastAlpha = -1f;
            b.LastNormRadius = -1f;
            b.LastSentCentre = new Vector3(float.MinValue, 0f, 0f);
        }

        // ── HUD ticker ───────────────────────────────────────────────────────────
        private void RefreshAllHud()
        {
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || !p.IsConnected) continue;
                RefreshHud(p);
            }
        }

        // ── Pings ────────────────────────────────────────────────────────────────
        // Our notes per player, by reference, so a refresh can drop exactly ours from
        // State.pings without touching the player's own pings.
        private readonly Dictionary<ulong, List<ProtoBuf.MapNote>> _playerPings = new Dictionary<ulong, List<ProtoBuf.MapNote>>();
        private Timer _pingTimer;

        // Vanilla never labels a ping (ApplyPingStyle sets only colour and icon), but the client
        // DOES render MapNote.label on one — confirmed in game. Setting it directly also skips
        // ValidateMapNote, so the 10-char uppercase cap on map notes does not apply here.
        // Resolved once: the decompiled dump lists this as private, but it is public on this build.
        private static System.Reflection.MethodInfo _sendPings;

        private static System.Reflection.MethodInfo SendPingsMethod
        {
            get
            {
                return _sendPings ?? (_sendPings = typeof(BasePlayer).GetMethod("SendPingsToClient",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public));
            }
        }

        private static string PingLabel(Battle b)
        {
            var n = b.Actors.Count;
            switch (b.DominantCategory)
            {
                case CatPvp: return (n <= 3 ? "SKIRMISH " : (n <= 8 ? "FIREFIGHT " : "WAR ")) + n;
                case CatRaid: return "RAID " + n;
                case CatMonument: return "MONUMENT " + n;
                case CatEvent: return "EVENT " + n;
                case CatNpc: return "NPC " + n;
                default: return "ANIMAL " + n;
            }
        }

        /// <summary>Re-pings the top battles for every opted-in player — SILENTLY.
        ///
        /// The ping sound + compass flash is a client reaction to a ping that arrives with a
        /// full timer; a ping whose timeRemaining is already below totalDuration is drawn but
        /// not announced (verified live 2026-09-01 at 10% and 50% aged; isPing=false still
        /// sounded; removal never sounds). So every cycle we drop our previous notes from
        /// State.pings by reference, append freshly-built notes that are already
        /// PingAgeFraction through their life, and push the list in ONE Client_ReceivePings.
        /// The vanilla AddPingAtLocation/RemovePingAtLocation helpers are avoided on purpose:
        /// each sends its own fresh-looking RPC (the old remove-then-add pair manufactured a
        /// "new" ping — and a sound — every refresh) and each calls TeamUpdate, which pushed our
        /// pings to every teammate.</summary>
        private void RefreshAllPings()
        {
            if (!_dispMask[DispPings] || _cfg.PingMaxEntries <= 0) { ClearAllPings(); return; }
            if (SendPingsMethod == null) return;

            BuildHudRows();   // same ranking as the HUD: hottest first

            // Duration outlives the refresh interval so the ping never blinks between cycles,
            // but still self-clears (TickPings) if the plugin stops updating.
            var total = _cfg.PingRefresh * 2f;
            var remaining = total * (1f - _cfg.PingAgeFraction);

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected || player.State == null) continue;
                ulong id = player.userID;

                var removed = DropOurNotes(player);
                if (PrefsRead(id).OptOut)
                {
                    if (removed) PushPings(player);
                    continue;
                }

                var n = Mathf.Min(_hudRows.Count, _cfg.PingMaxEntries);
                if (n == 0)
                {
                    if (removed) PushPings(player);
                    continue;
                }

                if (player.State.pings == null) player.State.pings = new List<ProtoBuf.MapNote>();
                var mine = new List<ProtoBuf.MapNote>(n);
                for (var i = 0; i < n; i++)
                {
                    var b = _hudRows[i];
                    var note = BuildNote(CatPing[b.DominantCategory], b.RenderCentre, total, remaining, PingLabel(b), true, 0);
                    player.State.pings.Add(note);
                    mine.Add(note);
                }
                _playerPings[id] = mine;
                PushPings(player);   // one push per player, after every note is in place
            }
        }

        private void PushPings(BasePlayer player)
        {
            player.DirtyPlayerState();
            SendPingsMethod.Invoke(player, null);
        }

        /// <summary>Removes our notes (by reference) from the player's State.pings without
        /// sending. Returns true if anything was removed.</summary>
        private bool DropOurNotes(BasePlayer player)
        {
            List<ProtoBuf.MapNote> prev;
            if (!_playerPings.TryGetValue(player.userID, out prev)) return false;
            _playerPings.Remove(player.userID);
            var pings = player.State?.pings;
            if (pings == null) return false;
            var any = false;
            foreach (var note in prev) any |= pings.Remove(note);
            return any;
        }

        private void ClearPingsFor(BasePlayer player)
        {
            if (player == null || SendPingsMethod == null) return;
            if (DropOurNotes(player)) PushPings(player);
        }

        private void ClearAllPings()
        {
            if (_playerPings.Count == 0) return;
            foreach (var player in BasePlayer.activePlayerList)
                if (player != null) ClearPingsFor(player);
            _playerPings.Clear();
        }

        private readonly List<Battle> _hudRows = new List<Battle>();

        /// <summary>Hottest published battles first, capped at HudMaxEntries.</summary>
        private void BuildHudRows()
        {
            _hudRows.Clear();
            foreach (var b in _battles) if (IsPublished(b)) _hudRows.Add(b);
            _hudRows.Sort((x, y) => y.SumW.CompareTo(x.SumW));
            if (_hudRows.Count > _cfg.HudMaxEntries)
                _hudRows.RemoveRange(_cfg.HudMaxEntries, _hudRows.Count - _cfg.HudMaxEntries);
        }

        // CUI anchors are parsed with invariant formatting; a comma decimal separator from a
        // non-English server locale would silently produce an unusable rect.
        private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        private void RefreshHud(BasePlayer player)
        {
            ulong id = player.userID;
            var prefs = PrefsRead(id);

            CuiHelper.DestroyUi(player, HudPanel);
            if (!_dispMask[DispHud] || prefs.HudHidden || prefs.OptOut) return;

            BuildHudRows();
            var n = _hudRows.Count;
            if (n == 0) return;

            float x0, y0, x1, y1;
            if (!TryParseAnchor(_cfg.HudAnchorMin, out x0, out y0) ||
                !TryParseAnchor(_cfg.HudAnchorMax, out x1, out y1)) return;

            // The configured rect is ONE row. Extra rows grow downward from the fixed top edge
            // so a busy server never pushes the panel back up into the compass.
            var rowH = y1 - y0;
            var bottom = Mathf.Max(0f, y1 - rowH * n);

            var c = new CuiElementContainer();
            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.55" },
                RectTransform = { AnchorMin = $"{F(x0)} {F(bottom)}", AnchorMax = $"{F(x1)} {F(y1)}" },
                CursorEnabled = false
            }, "Hud", HudPanel);

            for (var i = 0; i < n; i++)
            {
                var b = _hudRows[i];
                var hex = ColorUtility.ToHtmlStringRGB(FillColour(b));
                var text = $"<color=#{hex}>{LabelFor(b)}</color>  <color=#888888>{Mathf.RoundToInt(RemainingSeconds(b))}s</color>";

                // Row 0 is the hottest and sits at the top of the panel.
                var rowMin = (float)(n - 1 - i) / n;
                var rowMax = (float)(n - i) / n;

                c.Add(new CuiLabel
                {
                    Text = { Text = text, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = $"0.02 {F(rowMin)}", AnchorMax = $"0.98 {F(rowMax)}" }
                }, HudPanel);
            }

            CuiHelper.AddUi(player, c);
        }

        /// <summary>Derived countdown: how long the circle stays VISIBLE, i.e. until heat decays
        /// below the publication threshold. Counting down to the death threshold instead was
        /// misleading — a battle stops rendering well before its record is discarded.</summary>
        private float RemainingSeconds(Battle b)
        {
            var floor = Mathf.Max(_cfg.MinPublishHeat, _cfg.DeathThreshold);
            if (b.SumW <= floor) return 0f;
            return _cfg.HalfLife * Mathf.Log(b.SumW / floor, 2f);
        }

        // ── Grid reference ───────────────────────────────────────────────────────
        /// <summary>Vanilla map grid: (0,0) is the MAP CENTRE, cells are 146.3m, letters run
        /// west->east from the west edge, numbers run north->south from the north edge.</summary>
        private static string FormatRustMapGrid(Vector3 pos)
        {
            var half = Mathf.Max(1f, TerrainMeta.Size.x) * 0.5f;
            // Offshore monuments sit outside the terrain square, producing grid refs past the
            // labelled range (a player at x=1213 on an 1800 map reads as "O8"). Left unclamped
            // deliberately: markers ARE visible out there, so the true computed cell describes
            // the position better than pinning it to the nearest labelled one.
            var columnIndex = Mathf.Max(0, Mathf.FloorToInt((pos.x + half) / GridCell));
            var rowIndex = Mathf.Max(0, Mathf.FloorToInt((half - pos.z) / GridCell));
            return ToSpreadsheetColumnName(columnIndex) + rowIndex.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Inverse of FormatRustMapGrid: returns the world-space centre of a grid cell
        /// such as "C2". Row/column indices follow the vanilla scheme — letters run west to east
        /// from the west edge, numbers run north to south from the north edge.</summary>
        private static bool TryParseGrid(string grid, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (string.IsNullOrEmpty(grid)) return false;

            grid = grid.Trim().ToUpperInvariant();
            var i = 0;
            var col = 0;
            while (i < grid.Length && grid[i] >= 'A' && grid[i] <= 'Z')
            {
                col = col * 26 + (grid[i] - 'A' + 1);
                i++;
            }
            if (i == 0 || i >= grid.Length) return false;
            col -= 1;

            int row;
            if (!int.TryParse(grid.Substring(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out row))
                return false;

            var half = Mathf.Max(1f, TerrainMeta.Size.x) * 0.5f;
            var x = (col + 0.5f) * GridCell - half;
            var z = half - (row + 0.5f) * GridCell;
            pos = new Vector3(x, 0f, z);
            pos.y = TerrainHeight(pos);
            return true;
        }

        private static string ToSpreadsheetColumnName(int n)
        {
            var s = string.Empty;
            while (n >= 0)
            {
                s = (char)('A' + n % 26) + s;
                n = n / 26 - 1;
            }
            return s;
        }

        // ── Commands ─────────────────────────────────────────────────────────────
        [ChatCommand("mappoi")]
        private void CmdMapPoi(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            switch (sub)
            {
                case "":
                case "help":
                case "status":
                    ShowStatus(player);
                    return;

                case "on":
                case "off":
                    SetOptOut(player, sub == "off");
                    return;

                case "show":
                case "hide":
                    SetHud(player, sub == "hide");
                    return;

                case "list":
                    if (!IsAdmin(player)) { Say(player, "You need the <color=#ffcc66>mappoi.admin</color> permission."); return; }
                    ListBattles(player);
                    return;

                case "config":
                    if (!IsAdmin(player)) { Say(player, "You need the <color=#ffcc66>mappoi.admin</color> permission."); return; }
                    ConfigCommand(player, args);
                    return;

                default:
                    Say(player, $"Unknown option <color=#ffcc66>{sub}</color>. Try <color=#ffcc66>/mappoi help</color>.");
                    return;
            }
        }

        private bool IsAdmin(BasePlayer player)
            => player.IsAdmin || permission.UserHasPermission(player.UserIDString, AdminPerm);

        private void ShowStatus(BasePlayer player)
        {
            ulong id = player.userID;
            var prefs = PrefsRead(id);
            var live = _battles.Count(IsPublished);

            Say(player,
                "<color=#ffcc66>Battle overlay</color>\n" +
                $"  Overlay: <color=#88ff88>{(prefs.OptOut ? "off" : "on")}</color>   HUD: <color=#88ff88>{(prefs.HudHidden ? "hidden" : "shown")}</color>\n" +
                $"  Live battles: <color=#88ff88>{live}</color>   Visibility: <color=#88ff88>{DescribeVisibility()}</color>\n" +
                "  <color=#aaaaaa>/mappoi on|off</color> — show/hide battle circles\n" +
                "  <color=#aaaaaa>/mappoi show|hide</color> — show/hide the HUD ticker" +
                (IsAdmin(player) ? "\n  <color=#aaaaaa>/mappoi config</color> — admin settings   <color=#aaaaaa>/mappoi list</color> — live battles" : ""));

            if (prefs.OptOut && !WantPerPlayer())
                Say(player, "<color=#ffaa55>Note:</color> map circles are server-wide on this build and stay visible; " +
                            "your opt-out hides the HUD.");
        }

        private void SetOptOut(BasePlayer player, bool optOut)
        {
            ulong id = player.userID;
            Prefs(id).OptOut = optOut;
            RecomputeOptOutFlag();
            SaveData();
            RefreshHud(player);
            if (optOut) ClearPingsFor(player); else RefreshAllPings();

            if (!optOut) { Say(player, "Battle overlay <color=#88ff88>on</color>."); return; }

            Say(player, "Battle overlay <color=#ff8888>off</color> — HUD hidden. " +
                        "Re-enable with <color=#ffcc66>/mappoi on</color>.");
            if (!WantPerPlayer())
                Say(player, "<color=#ffaa55>Note:</color> map circles are server-wide and cannot be hidden " +
                            "for one player on this build, so they remain visible.");
        }

        private void SetHud(BasePlayer player, bool hide)
        {
            ulong id = player.userID;
            Prefs(id).HudHidden = hide;
            SaveData();
            RefreshHud(player);
            Say(player, hide
                ? "HUD ticker <color=#ff8888>hidden</color>. Re-enable with <color=#ffcc66>/mappoi show</color>."
                : "HUD ticker <color=#88ff88>shown</color>.");
        }

        private void ListBattles(BasePlayer player)
        {
            if (_battles.Count == 0) { Say(player, "No live battles."); return; }

            var sb = new System.Text.StringBuilder("<color=#ffcc66>Live battles</color>");
            foreach (var b in _battles.OrderByDescending(x => x.SumW))
            {
                var centre = b.Centre;
                centre.y = TerrainHeight(centre);
                sb.Append($"\n  #{b.Id} <color=#88ff88>{FormatRustMapGrid(centre)}</color> " +
                          $"{CatNames[b.DominantCategory]} heat {b.SumW:F1} " +
                          $"actors {b.Actors.Count} spread {b.Spread:F0}m " +
                          $"exp {b.ExplosiveFraction * 100f:F0}% " +
                          $"ttl {RemainingSeconds(b):F0}s " +
                          (IsPublished(b) ? "<color=#88ff88>shown</color>" : "<color=#888888>pending</color>"));
            }
            if (_pending.Count > 0) sb.Append($"\n  <color=#888888>{_pending.Count} event(s) held by the visibility delay</color>");
            Say(player, sb.ToString());
        }

        private string DescribeVisibility()
        {
            if (_cfg.VisibilityDelay < 0f) return "off";
            if (_cfg.VisibilityDelay < 0.01f) return "live";
            return $"{_cfg.VisibilityDelay:0.#}s delayed";
        }

        private void ConfigCommand(BasePlayer player, string[] args)
        {
            if (args.Length < 2) { Say(player, DescribeConfig()); return; }
            Say(player, ApplyConfigChange(args));
            Say(player, DescribeConfig());
        }

        /// <summary>Shared by the chat and console paths. args[0] is "config", args[1] the key,
        /// the rest the value. Returns the message to show; on success the config is validated,
        /// written and the hook subscriptions re-evaluated before returning.</summary>
        private string ApplyConfigChange(string[] args)
        {
            var key = args[1].ToLowerInvariant();
            var val = args.Length > 2 ? string.Join(" ", args.Skip(2).ToArray()).Trim() : null;
            var note = string.Empty;

            switch (key)
            {
                case "poistyle":
                {
                    int style;
                    if (val == null || !int.TryParse(val, out style) || style < 1 || style > 3)
                        return "Usage: <color=#ffcc66>poistyle 1|2|3</color>  (1=centroid+spread, 2=fuzzed, 3=fuzzed stable)";
                    _cfg.PoiStyle = style;
                    foreach (var b in _battles) b.NextRecentre = 0f;   // re-derive centres immediately
                    break;
                }

                case "visibility":
                {
                    if (val == null) return "Usage: <color=#ffcc66>visibility off|live|30s</color>";
                    float delay;
                    var lower = val.ToLowerInvariant();
                    if (lower == "off") delay = -1f;
                    else if (lower == "live") delay = 0f;
                    else if (!float.TryParse(lower.TrimEnd('s'), NumberStyles.Float, CultureInfo.InvariantCulture, out delay) || delay < 0f)
                        return "Usage: <color=#ffcc66>visibility off|live|30s</color>";

                    _cfg.VisibilityDelay = Mathf.Min(delay, 600f);
                    // Reveal times were stamped against the old delay, so the queue's ordering
                    // is no longer meaningful — drop it rather than release a mistimed burst.
                    if (_pending.Count > 0)
                    {
                        note = $"\n<color=#888888>Discarded {_pending.Count} delayed event(s).</color>";
                        _pending.Clear();
                    }
                    break;
                }

                case "track":
                {
                    if (val == null) return "Usage: <color=#ffcc66>track pvp,raid,monuments,events,npc,animal</color> (or <color=#ffcc66>none</color>)";
                    List<string> parsed;
                    if (!ParseSet(val, CatNames, out parsed))
                        return $"Valid categories: <color=#ffcc66>{string.Join(", ", CatNames)}</color>, or <color=#ffcc66>none</color>.";
                    _cfg.Track = parsed;
                    break;
                }

                case "display":
                {
                    if (val == null) return "Usage: <color=#ffcc66>display circles,labels,explosions,hud</color> (or <color=#ffcc66>none</color>)";
                    List<string> parsed;
                    if (!ParseSet(val, DisplayNames, out parsed))
                        return $"Valid elements: <color=#ffcc66>{string.Join(", ", DisplayNames)}</color>, or <color=#ffcc66>none</color>.";
                    _cfg.Display = parsed;
                    break;
                }

                case "markermode":
                    if (val == null) return "Usage: <color=#ffcc66>markermode global|perplayer</color>";
                    _cfg.MarkerMode = NormaliseMarkerMode(val);
                    if (_cfg.MarkerMode == "perplayer")
                        note = "\n<color=#ffaa55>Warning:</color> per-player markers are parented to the viewer, " +
                               "so circles only render near them — distant battles will not appear.";
                    break;

                case "pingage":
                {
                    float f;
                    if (val == null || !float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                        return $"Usage: <color=#ffcc66>pingage 0.1</color>  (0 = fresh ping WITH sound; current {_cfg.PingAgeFraction:0.##})";
                    _cfg.PingAgeFraction = Mathf.Clamp(f, 0f, 0.9f);
                    RefreshAllPings();
                    break;
                }

                case "halflife":
                {
                    float hl;
                    if (val == null || !float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out hl))
                        return "Usage: <color=#ffcc66>halflife 30</color>  (seconds)";
                    _cfg.HalfLife = hl;
                    break;
                }

                case "hudpos":
                {
                    if (val == null)
                        return "Usage: <color=#ffcc66>hudpos <y></color> to move it vertically, " +
                               "or <color=#ffcc66>hudpos <xmin> <ymin> <xmax> <ymax></color> for full control. " +
                               $"Current: {_cfg.HudAnchorMin} .. {_cfg.HudAnchorMax}";

                    var p = val.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    float ax0, ay0, ax1, ay1;
                    TryParseAnchor(_cfg.HudAnchorMin, out ax0, out ay0);
                    TryParseAnchor(_cfg.HudAnchorMax, out ax1, out ay1);

                    if (p.Length == 1)
                    {
                        // Single value shifts the panel vertically, preserving its height.
                        float y;
                        if (!float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                            return "Usage: <color=#ffcc66>hudpos 0.855</color>  (0 = bottom, 1 = top)";
                        var h = ay1 - ay0;
                        ay0 = Mathf.Clamp(y, 0f, 1f - h);
                        ay1 = ay0 + h;
                    }
                    else if (p.Length == 4)
                    {
                        if (!float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out ax0) ||
                            !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out ay0) ||
                            !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out ax1) ||
                            !float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out ay1))
                            return "All four values must be numbers between 0 and 1.";
                        if (ax1 <= ax0 || ay1 <= ay0) return "max values must exceed min values.";
                    }
                    else return "Usage: <color=#ffcc66>hudpos <y></color> or <color=#ffcc66>hudpos <xmin> <ymin> <xmax> <ymax></color>";

                    _cfg.HudAnchorMin = $"{ax0.ToString("0.###", CultureInfo.InvariantCulture)} {ay0.ToString("0.###", CultureInfo.InvariantCulture)}";
                    _cfg.HudAnchorMax = $"{ax1.ToString("0.###", CultureInfo.InvariantCulture)} {ay1.ToString("0.###", CultureInfo.InvariantCulture)}";
                    break;
                }

                case "circlescale":
                {
                    float scale;
                    if (val == null || !float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out scale))
                        return $"Usage: <color=#ffcc66>circlescale 3</color>  (current {_cfg.CircleScale:0.##})";
                    _cfg.CircleScale = scale;
                    foreach (var b in _battles) b.NextRecentre = 0f;   // re-derive radii immediately
                    break;
                }

                case "reset":
                    _cfg = new Configuration();
                    _pending.Clear();
                    break;

                default:
                    return $"Unknown setting <color=#ffcc66>{key}</color>. Valid: poistyle, visibility, track, display, markermode, halflife, pingage, hudpos, circlescale, reset.";
            }

            ValidateConfig();
            SaveConfig();
            ApplyHookState();
            // Visual settings may have changed shape entirely — rebuild markers from scratch.
            foreach (var b in _battles) DestroyBattleMarkers(b);
            RefreshAllHud();
            RefreshAllPings();
            return $"<color=#88ff88>{key}</color> updated." + note;
        }

        private static bool ParseSet(string val, string[] valid, out List<string> result)
        {
            result = new List<string>();
            if (val.Trim().ToLowerInvariant() == "none") return true;

            foreach (var raw in val.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = raw.Trim().ToLowerInvariant();
                if (Array.IndexOf(valid, t) < 0) return false;
                if (!result.Contains(t)) result.Add(t);
            }
            return true;
        }

        private string DescribeConfig()
        {
            return
                "<color=#ffcc66>MAPPoI settings</color>\n" +
                $"  poistyle    <color=#88ff88>{_cfg.PoiStyle}</color> ({PoiStyleName(_cfg.PoiStyle)})\n" +
                $"  visibility  <color=#88ff88>{DescribeVisibility()}</color>\n" +
                $"  track       <color=#88ff88>{(_cfg.Track.Count == 0 ? "none" : string.Join(",", _cfg.Track.ToArray()))}</color>\n" +
                $"  display     <color=#88ff88>{(_cfg.Display.Count == 0 ? "none" : string.Join(",", _cfg.Display.ToArray()))}</color>\n" +
                $"  markermode  <color=#88ff88>{_cfg.MarkerMode}</color> (active: {(WantPerPlayer() ? "perplayer" : "global")})\n" +
                $"  halflife    <color=#88ff88>{_cfg.HalfLife:0.#}s</color>   battles live: <color=#88ff88>{_battles.Count}</color>\n" +
                $"  hudpos      <color=#88ff88>{_cfg.HudAnchorMin}</color> .. <color=#88ff88>{_cfg.HudAnchorMax}</color>\n" +
                $"  circlescale <color=#88ff88>{_cfg.CircleScale:0.##}x</color>";
        }

        private static string PoiStyleName(int s)
            => s == 2 ? "fuzzed" : (s == 3 ? "fuzzed stable" : "centroid+spread");

        private void Say(BasePlayer player, string msg) => player.ChatMessage(msg);

        // ── Console commands (RCON-friendly) ─────────────────────────────────────
        private static string StripTags(string s) => System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", "");

        // ConsoleSystem.Arg.Args is Facepunch.StringView[] on this build, so go through
        // GetString() rather than indexing it, and parse invariantly.
        private static bool ArgFloat(ConsoleSystem.Arg arg, int index, out float value)
            => float.TryParse(arg.GetString(index), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private bool ConsoleAllowed(ConsoleSystem.Arg arg)
        {
            var p = arg.Player();
            if (p == null) return true;                       // server console / RCON
            if (IsAdmin(p)) return true;
            arg.ReplyWith("You need the mappoi.admin permission.");
            return false;
        }

        [ConsoleCommand("mappoi.list")]
        private void CcList(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;

            // The marker totals print even with no battles — that is precisely when you want
            // to check for entities orphaned by a bad unload.
            var sb = new System.Text.StringBuilder(_battles.Count == 0 ? "No live battles." : "Live battles:");
            foreach (var b in _battles.OrderByDescending(x => x.SumW))
            {
                var centre = b.Centre;
                centre.y = TerrainHeight(centre);
                var circles = (b.GlobalCircle != null && !b.GlobalCircle.IsDestroyed ? 1 : 0) + b.PlayerCircles.Count;
                var labels = (b.GlobalLabel != null && !b.GlobalLabel.IsDestroyed ? 1 : 0) + b.PlayerLabels.Count;

                sb.Append($"\n  #{b.Id} {FormatRustMapGrid(centre)} {CatNames[b.DominantCategory]} " +
                          $"heat {b.SumW:F1} actors {b.Actors.Count} spread {b.Spread:F0}m " +
                          $"exp {b.ExplosiveFraction * 100f:F0}% ttl {RemainingSeconds(b):F0}s " +
                          $"radius {b.RenderRadiusMetres:F0}m " +
                          $"markers c={circles} l={labels} alpha={b.LastAlpha:F2} " +
                          $"\"{b.LastLabel}\" {(IsPublished(b) ? "shown" : "pending")}");
            }
            if (_pending.Count > 0)
            {
                var byCat = new int[CatCount];
                var exp = 0;
                foreach (var e in _pending) { byCat[e.Cat]++; if (e.Explosive) exp++; }
                sb.Append($"\n  {_pending.Count} event(s) held by the visibility delay (explosive {exp}):");
                for (var i = 0; i < CatCount; i++) if (byCat[i] > 0) sb.Append($" {CatNames[i]}={byCat[i]}");
            }
            sb.Append($"\n  server map markers total: {MapMarker.serverMapMarkers.Count}, explosion icons: {_explosionIcons.Count}");
            arg.ReplyWith(sb.ToString());
        }

        [ConsoleCommand("mappoi.debug")]
        private void CcDebug(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;

            var hooked = _cfg.VisibilityDelay >= 0f && _cfg.Track.Count > 0;
            var tracked = _cfg.Track.Count == 0 ? "none" : string.Join(",", _cfg.Track.ToArray());

            arg.ReplyWith(
                $"OnEntityTakeDamage calls: {_dbgHookCalls}\n" +
                $"  victimKind=None : {_dbgNoVictimKind}   last: {_dbgLastNoKind}\n" +
                $"  below MinDamage : {_dbgLowDamage}\n" +
                $"  rejected        : {_dbgRejected}   last: {_dbgLastReject}\n" +
                $"  ingested        : {_dbgIngested}   last: {_dbgLastIngest}\n" +
                $"hooks should be subscribed: {hooked} (visibility={DescribeVisibility()}, track={tracked})\n" +
                $"monuments cached: {_monuments.Length}\n" +
                $"world size: {WorldSize:F0}m, grid cell {GridCell}m, " +
                $"valid grid: A0..{ToSpreadsheetColumnName(Mathf.FloorToInt(WorldSize / GridCell) - 1)}{Mathf.FloorToInt(WorldSize / GridCell) - 1}\n" +
                "players:\n  " +
                (BasePlayer.activePlayerList.Count == 0
                    ? "(none)"
                    : string.Join("\n  ", BasePlayer.activePlayerList
                        .Where(p => p != null)
                        .Select(p => $"{p.displayName}: pos=({p.transform.position.x:F0},{p.transform.position.z:F0}) " +
                                     $"grid={FormatRustMapGrid(p.transform.position)} " +
                                     $"bodyYaw={p.transform.rotation.eulerAngles.y:F0}deg " +
                                     $"eyeYaw={(p.eyes != null ? p.eyes.rotation.eulerAngles.y : -1f):F0}deg").ToArray())) + "\n" +
                $"battles: {_battles.Count}, pending: {_pending.Count}, " +
                $"publish gate: >={_cfg.MinParticipants} actors and heat >={_cfg.MinPublishHeat:F1}\n" +
                "breakdown (most frequent first):\n  " +
                (_dbgReasons.Count == 0
                    ? "(nothing recorded)"
                    : string.Join("\n  ", _dbgReasons.OrderByDescending(k => k.Value)
                        .Take(12).Select(k => $"{k.Value,6}  {k.Key}").ToArray())));
        }

        /// <summary>Classifies the NPC-ish entities currently alive on the server, so the
        /// damage-hook categorisation can be proven without waiting to catch a real fight.</summary>
        [ConsoleCommand("mappoi.selftest")]
        private void CcSelfTest(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;

            var seen = new Dictionary<string, int>();
            var verdict = new Dictionary<string, string>();

            foreach (var ent in BaseNetworkable.serverEntities)
            {
                var e = ent as BaseEntity;
                if (e == null) continue;

                var name = e.GetType().Name;
                if (!IsNpcType(e.GetType()) && name.IndexOf("cient", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                int n;
                seen[name] = seen.TryGetValue(name, out n) ? n + 1 : 1;
                if (!verdict.ContainsKey(name))
                {
                    var inMon = InMonument(e.transform.position);
                    verdict[name] = $"victim={KindOf(e, true)} attacker={KindOf(e, false)} " +
                                    $"npcType={IsNpcType(e.GetType())} atMonument={inMon} {FormatRustMapGrid(e.transform.position)}";
                }
            }

            if (seen.Count == 0) { arg.ReplyWith("No NPC-like entities found alive right now."); return; }

            var sb = new System.Text.StringBuilder("NPC classification (live entities):");
            foreach (var kv in seen.OrderByDescending(k => k.Value))
                sb.Append($"\n  {kv.Value,4}x {kv.Key}\n         {verdict[kv.Key]}");
            arg.ReplyWith(sb.ToString());
        }

        /// <summary>Reproduces the v1.2.0 marker recipe exactly (visuals applied BEFORE Spawn,
        /// parented to the viewer, circle placed near them) to prove whether the marker
        /// transport still works independently of the v2 render path. Auto-cleans after 30s.</summary>
        [ConsoleCommand("mappoi.testcircle")]
        private void CcTestCircle(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;

            var made = 0;
            var sb = new System.Text.StringBuilder();

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player?.net?.connection == null) continue;

                // v1 recipe, verbatim.
                var worldPos = LegacyNearbyPos(player.transform.position);
                var m = GameManager.server.CreateEntity(CirclePrefab, worldPos) as MapMarkerGenericRadius;
                if (m == null) continue;

                m.OwnerID = player.userID;
                m.enableSaving = false;
                m.SetParent(player, worldPositionStays: true);
                m.limitNetworking = true;
                m.color1 = new Color(0f, 1f, 0f, 1f);      // bright green, unmistakable
                m.color2 = new Color(0.6f, 1f, 0.6f, 1f);
                m.alpha = 0.6f;
                m.radius = 150f / WorldSize;
                m.Spawn();
                SendMarkerUpdate(m, player);

                made++;
                sb.Append($"\n  {player.displayName}: green circle at {FormatRustMapGrid(worldPos)} " +
                          $"(radius {m.radius:F4} normalised, worldsize {WorldSize:F0})");

                var marker = m;
                timer.Once(30f, () => { if (marker != null && !marker.IsDestroyed) marker.Kill(); });
            }

            arg.ReplyWith(made == 0
                ? "No connected players to test on."
                : $"Spawned {made} v1-style test circle(s), self-destructing in 30s:{sb}");
        }

        /// <summary>Spawns TWO parented (per-player) markers for each connected player: one at a
        /// far grid reference and one right beside them. Both use the identical recipe, so if
        /// only the near one renders it confirms that a parented marker is positioned relative
        /// to its parent and cannot be world-anchored across the map.</summary>
        [ConsoleCommand("mappoi.testperplayer")]
        private void CcTestPerPlayer(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;

            var gridArg = (arg.Args?.Length ?? 0) > 0 ? arg.GetString(0) : "C2";
            Vector3 farPos;
            if (!TryParseGrid(gridArg, out farPos))
            { arg.ReplyWith($"Could not parse grid '{gridArg}'. Try e.g. mappoi.testperplayer C2"); return; }

            var made = 0;
            var sb = new System.Text.StringBuilder();

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player?.net?.connection == null) continue;

                var nearPos = LegacyNearbyPos(player.transform.position);

                // Identical recipe for both — only the position differs.
                var far = SpawnParentedTestMarker(player, farPos, new Color(0.2f, 0.4f, 1f));    // blue = far
                var near = SpawnParentedTestMarker(player, nearPos, new Color(1f, 0f, 1f));      // magenta = near
                if (far == null || near == null) continue;

                made++;
                var dist = Vector3.Distance(player.transform.position, farPos);
                sb.Append($"\n  {player.displayName}:" +
                          $"\n     BLUE    at {FormatRustMapGrid(farPos)} ({dist:F0}m away)" +
                          $"\n     MAGENTA at {FormatRustMapGrid(nearPos)} (beside you)");

                timer.Once(45f, () =>
                {
                    if (far != null && !far.IsDestroyed) far.Kill();
                    if (near != null && !near.IsDestroyed) near.Kill();
                });
            }

            arg.ReplyWith(made == 0
                ? "No connected players to test on."
                : $"Spawned parented test markers (45s):{sb}\n" +
                  "  Expect: MAGENTA renders. If BLUE is missing or in the wrong place, a parented " +
                  "marker cannot be world-anchored.");
        }

        private MapMarkerGenericRadius SpawnParentedTestMarker(BasePlayer player, Vector3 pos, Color colour,
                                                              float radiusMetres = 150f)
        {
            var m = GameManager.server.CreateEntity(CirclePrefab, pos) as MapMarkerGenericRadius;
            if (m == null) return null;

            m.OwnerID = player.userID;
            m.enableSaving = false;
            m.SetParent(player, worldPositionStays: true);
            m.limitNetworking = true;
            m.color1 = colour;
            m.color2 = Brighten(colour, 0.4f);
            m.alpha = 0.6f;
            m.radius = radiusMetres / WorldSize;
            m.Spawn();
            SendMarkerUpdate(m, player);
            return m;
        }

        /// <summary>A/B calibration for parented markers. Spawns the same target twice: RED at
        /// the raw world position, GREEN pre-compensated by the player's position. Whichever
        /// lands on the target cell reveals how the client transforms a parented marker, which
        /// is the prerequisite for making per-player markers world-anchored.</summary>
        [ConsoleCommand("mappoi.calibrate")]
        private void CcCalibrate(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;

            var gridArg = (arg.Args?.Length ?? 0) > 0 ? arg.GetString(0) : "C2";
            Vector3 target;
            if (!TryParseGrid(gridArg, out target))
            { arg.ReplyWith($"Could not parse grid '{gridArg}'."); return; }

            var sb = new System.Text.StringBuilder();
            var made = 0;

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player?.net?.connection == null) continue;
                var p = player.transform.position;

                // Large radii so they are unmistakable, and different sizes so overlapping
                // circles can still be told apart.
                var red = SpawnParentedTestMarker(player, target, new Color(1f, 0f, 0f), 320f);
                var green = SpawnParentedTestMarker(player, target + new Vector3(p.x, 0f, p.z), new Color(1f, 1f, 0f), 220f);
                // GLOBAL control: unparented, known-good. Proves the target cell is where I think.
                var blue = GameManager.server.CreateEntity(CirclePrefab, target) as MapMarkerGenericRadius;
                if (blue != null)
                {
                    blue.enableSaving = false;
                    blue.globalBroadcast = true;
                    blue.color1 = new Color(1f, 1f, 1f);
                    blue.color2 = new Color(1f, 1f, 1f);
                    blue.alpha = 0.5f;
                    blue.radius = 110f / WorldSize;
                    blue.Spawn();
                    blue.UpdateNetworkGroup();
                    blue.SendUpdate();
                    timer.Once(60f, () => { if (blue != null && !blue.IsDestroyed) blue.Kill(); });
                }
                if (red == null || green == null) continue;

                made++;
                sb.Append($"\n  {player.displayName} standing at ({p.x:F0},{p.z:F0}) = {FormatRustMapGrid(p)}" +
                          $"\n     target {gridArg.ToUpperInvariant()} = ({target.x:F0},{target.z:F0})" +
                          $"\n     RED    (parented, BIG 320m)  spawned at ({target.x:F0},{target.z:F0})" +
                          $"\n     YELLOW (parented, med 220m)  spawned at ({target.x + p.x:F0},{target.z + p.z:F0})" +
                          $"\n     WHITE  (GLOBAL control, small 110m) at ({target.x:F0},{target.z:F0})");

                timer.Once(60f, () =>
                {
                    if (red != null && !red.IsDestroyed) red.Kill();
                    if (green != null && !green.IsDestroyed) green.Kill();
                });
            }

            arg.ReplyWith(made == 0
                ? "No connected players to test on."
                : $"Calibration markers up for 60s:{sb}\n" +
                  $"  STAND STILL and report the GRID CELL each colour actually appears in.\n" +
                  $"  WHITE (small) is the global control and should sit on {gridArg.ToUpperInvariant()}.\n" +
                  "  RED is the BIGGEST circle; YELLOW is mid-sized. Say if either is missing entirely.");
        }

        /// <summary>Spawns a small GLOBAL circle at each player's exact world position. Their own
        /// map arrow is an exact reference point, so this separates "marker rendering is offset"
        /// from "my grid maths disagrees with Rust's grid".</summary>
        [ConsoleCommand("mappoi.atme")]
        private void CcAtMe(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;

            float r;
            if ((arg.Args?.Length ?? 0) < 1 || !ArgFloat(arg, 0, out r)) r = 60f;
            r = Mathf.Clamp(r, 5f, 2000f);

            var sb = new System.Text.StringBuilder();
            var made = 0;

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player?.net?.connection == null) continue;
                var p = player.transform.position;

                var m = GameManager.server.CreateEntity(CirclePrefab, p) as MapMarkerGenericRadius;
                if (m == null) continue;

                m.enableSaving = false;
                m.globalBroadcast = true;
                m.color1 = new Color(1f, 1f, 1f);
                m.color2 = new Color(1f, 1f, 1f);
                m.alpha = 0.7f;
                m.radius = r / WorldSize;
                m.Spawn();
                m.UpdateNetworkGroup();
                m.SendUpdate();

                made++;
                sb.Append($"\n  {player.displayName}: WHITE {r:F0}m circle at exactly ({p.x:F0},{p.z:F0}) = {FormatRustMapGrid(p)}");

                var marker = m;
                timer.Once(120f, () => { if (marker != null && !marker.IsDestroyed) marker.Kill(); });
            }

            arg.ReplyWith(made == 0
                ? "No connected players."
                : $"Spawned for 120s:{sb}\n  Is the circle centred exactly on your own map arrow?");
        }

        /// <summary>Reads each player's own map notes and measures them against every live battle
        /// centre. Drop a note where a circle LOOKS like it is centred and this reports the exact
        /// discrepancy — far more precise than describing an offset by eye.</summary>
        [ConsoleCommand("mappoi.mynotes")]
        private void CcMyNotes(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;

            var sb = new System.Text.StringBuilder();
            var any = false;

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null) continue;
                var notes = player.State?.pointsOfInterest;
                if (notes == null || notes.Count == 0) continue;

                any = true;
                sb.Append($"\n{player.displayName} — {notes.Count} note(s), standing at " +
                          $"({player.transform.position.x:F0},{player.transform.position.z:F0}) {FormatRustMapGrid(player.transform.position)}");

                for (var i = 0; i < notes.Count; i++)
                {
                    var w = notes[i].worldPosition;
                    sb.Append($"\n  note {i + 1}: ({w.x:F0},{w.z:F0}) = {FormatRustMapGrid(w)}");

                    // Nearest live battle, measured in 2D — that difference is the offset.
                    Battle best = null;
                    var bestSqr = float.MaxValue;
                    foreach (var b in _battles)
                    {
                        if (!IsPublished(b)) continue;
                        var d = new Vector2(b.RenderCentre.x - w.x, b.RenderCentre.z - w.z).sqrMagnitude;
                        if (d < bestSqr) { bestSqr = d; best = b; }
                    }

                    if (best == null) { sb.Append("   (no published battle to compare)"); continue; }

                    var dx = w.x - best.RenderCentre.x;
                    var dz = w.z - best.RenderCentre.z;
                    sb.Append($"\n           nearest circle #{best.Id} {FormatRustMapGrid(best.RenderCentre)} " +
                              $"at ({best.RenderCentre.x:F0},{best.RenderCentre.z:F0})" +
                              $"\n           OFFSET note-minus-circle: dx={dx:F0}m dz={dz:F0}m  (dist {Mathf.Sqrt(bestSqr):F0}m)");
                }
            }

            arg.ReplyWith(any
                ? "Map notes:" + sb + "\n\nA consistent non-zero dx/dz across notes is a real rendering offset."
                : "No map notes found. Open the map and place a note (default: click the marker button, then click the map).");
        }

        /// <summary>Tests whether the client renders MapNote.label on a PING. Vanilla never sets
        /// it — ApplyPingStyle only touches colour and icon — so if the client does draw it we
        /// get icon+text per player at no cost to their marker slots.</summary>
        [ConsoleCommand("mappoi.pinglabel")]
        private void CcPingLabel(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;

            var text = (arg.Args?.Length ?? 0) > 0 ? arg.GetString(0) : "FIREFIGHT";
            var sb = new System.Text.StringBuilder();
            var made = 0;

            // Search Public too — the decompiled dump lists this as private but it is public here.
            var send = typeof(BasePlayer).GetMethod("SendPingsToClient",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public);
            if (send == null)
            {
                // The decompiled dump is stale in places — ask the runtime what it really has.
                var names = typeof(BasePlayer)
                    .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.NonPublic)
                    .Where(m => m.Name.IndexOf("Ping", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(m => m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name).ToArray()) + ")")
                    .Distinct().OrderBy(s => s).ToArray();
                arg.ReplyWith("SendPingsToClient not found. BasePlayer ping-related methods:\n  " +
                              string.Join("\n  ", names));
                return;
            }

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player?.net?.connection == null) continue;
                var pos = LegacyNearbyPos(player.transform.position);

                player.AddPingAtLocation(BasePlayer.PingType.Gun, pos, 60f, default(NetworkableId));

                // Patch the label onto the ping we just created, then re-push.
                var pings = player.State?.pings;
                if (pings == null || pings.Count == 0) continue;
                var note = pings[pings.Count - 1];
                note.label = text;
                player.DirtyPlayerState();
                send.Invoke(player, null);

                made++;
                sb.Append($"\n  {player.displayName}: GUN ping labelled \"{text}\" at {FormatRustMapGrid(pos)} (60s)");
            }

            arg.ReplyWith(made == 0
                ? "No connected players."
                : $"Sent:{sb}\n  Does the ping show the text \"{text}\", or just the gun icon?");
        }

        // ── Ping-sound probes ────────────────────────────────────────────────────
        // The ping sound is a CLIENT reaction to a ping it judges new; the server never
        // plays it. The wire format (ProtoBuf.MapNote: noteType, worldPosition, icon,
        // colourIndex, label, isPing, timeRemaining, totalDuration, associatedId) has no
        // silent or map-only flag, so these probes vary the fields the client can see and
        // let the viewer report what they hear. Notes are hand-built and pushed in ONE
        // Client_ReceivePings RPC — AddPingAtLocation would itself send a fresh-looking
        // ping first and spoil the test.
        //
        // Results 2026-09-01 (live): an AGED ping (timeRemaining 30 of 60) arrived SILENT;
        // isPing=false via the ping channel still made the sound; the 7-type sweep sounded on
        // every type (Hostile and Build to be re-confirmed).
        //
        // Console: mappoi.pingaged / pingnote / pingtypes / pingclear  (RCON = all players)
        // Chat:    /pingtest aged [frac] [type] | types [interval] | type <name|1-7> | note | clear

        private static readonly BasePlayer.PingType[] AllPingTypes =
        {
            BasePlayer.PingType.Hostile, BasePlayer.PingType.GoTo, BasePlayer.PingType.Dollar,
            BasePlayer.PingType.Loot, BasePlayer.PingType.Node, BasePlayer.PingType.Gun,
            BasePlayer.PingType.Build,
        };

        private static string PingTypeNames => string.Join(", ", AllPingTypes.Select((t, i) => $"{i + 1}={t}").ToArray());

        /// <summary>Accepts a type name (any case) or its 1-based index from the sweep.</summary>
        private static bool TryParsePingType(string s, out BasePlayer.PingType type)
        {
            int n;
            if (int.TryParse(s, out n) && n >= 1 && n <= AllPingTypes.Length) { type = AllPingTypes[n - 1]; return true; }
            return Enum.TryParse(s, true, out type) && AllPingTypes.Contains(type);
        }

        private static bool TryParseFloat(string s, ref float value)
        {
            float f;
            if (s == null || !float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return false;
            value = f;
            return true;
        }

        /// <summary>In-game caller only, else every connected player (RCON / server console).</summary>
        private List<BasePlayer> ProbeTargets(ConsoleSystem.Arg arg)
        {
            var caller = arg.Player();
            if (caller != null) return new List<BasePlayer> { caller };
            return BasePlayer.activePlayerList.Where(p => p?.net?.connection != null).ToList();
        }

        private static string[] ConsoleArgs(ConsoleSystem.Arg arg)
        {
            var n = arg.Args?.Length ?? 0;
            var a = new string[n];
            for (var i = 0; i < n; i++) a[i] = arg.GetString(i);
            return a;
        }

        private Vector3 ProbePos(BasePlayer p, float dist, float angleDeg)
        {
            var rad = angleDeg * Mathf.Deg2Rad;
            var x = p.transform.position.x + Mathf.Sin(rad) * dist;
            var z = p.transform.position.z + Mathf.Cos(rad) * dist;
            return new Vector3(x, TerrainHeight(new Vector3(x, 0f, z)), z);
        }

        private static ProtoBuf.MapNote BuildNote(BasePlayer.PingType type, Vector3 pos, float total,
            float remaining, string label, bool isPing, int noteType)
        {
            var style = BasePlayer.GetPingStyle(type);
            var note = Facepunch.Pool.Get<ProtoBuf.MapNote>();
            note.noteType = noteType;          // vanilla pings leave 0 (Death); a real map note is 1 (PointOfInterest)
            note.worldPosition = pos;
            note.icon = style.IconIndex;
            note.colourIndex = style.ColourIndex;
            note.label = label;
            note.isPing = isPing;
            note.totalDuration = total;
            note.timeRemaining = remaining;
            note.associatedId = default(NetworkableId);
            return note;
        }

        /// <summary>Appends to State.pings and pushes the whole list once. TickPings will age
        /// and expire it like any vanilla ping, so probes self-clean.</summary>
        private bool PushNote(BasePlayer player, ProtoBuf.MapNote note)
        {
            if (SendPingsMethod == null) return false;
            if (player.State.pings == null) player.State.pings = new List<ProtoBuf.MapNote>();
            player.State.pings.Add(note);
            player.DirtyPlayerState();
            SendPingsMethod.Invoke(player, null);
            return true;
        }

        // ── shared probe bodies (console + chat) ──

        /// <summary>Probe #2 — the ping arrives `aged` of the way through its life
        /// (timeRemaining &lt; totalDuration). Live result: silent at 0.5.</summary>
        private string RunPingAged(List<BasePlayer> targets, string[] args)
        {
            if (SendPingsMethod == null) return "SendPingsToClient not found on this build.";
            var aged = 0.5f;
            if (args.Length > 0) TryParseFloat(args[0], ref aged);
            aged = Mathf.Clamp(aged, 0f, 0.99f);
            var type = BasePlayer.PingType.Gun;
            if (args.Length > 1 && !TryParsePingType(args[1], out type)) return "Unknown ping type. One of: " + PingTypeNames;

            const float total = 60f;
            var remaining = total * (1f - aged);
            var pct = Mathf.RoundToInt(aged * 100f);
            var sb = new System.Text.StringBuilder();
            foreach (var player in targets)
            {
                var pos = ProbePos(player, 40f, 0f);
                PushNote(player, BuildNote(type, pos, total, remaining, "AGED " + pct + "%", true, 0));
                sb.Append($"\n  {player.displayName}: {type} ping at {FormatRustMapGrid(pos)}, timeRemaining {remaining:0}s of {total:0}s");
            }
            return sb.Length == 0 ? "No connected players."
                : $"Sent (one RPC, no vanilla pre-send):{sb}\n  Sound / compass flash? Icon dimmed? Ring at {100 - pct}%?";
        }

        /// <summary>Probe #3 — isPing=false through the ping channel. Live result: still sounds.</summary>
        private string RunPingNote(List<BasePlayer> targets, string[] args)
        {
            if (SendPingsMethod == null) return "SendPingsToClient not found on this build.";
            var text = args.Length > 0 ? args[0] : "NOTE";
            var noteType = 1f;
            if (args.Length > 1) TryParseFloat(args[1], ref noteType);
            var nt = noteType <= 0f ? 0 : 1;

            var sb = new System.Text.StringBuilder();
            foreach (var player in targets)
            {
                var pos = ProbePos(player, 40f, 90f);
                PushNote(player, BuildNote(BasePlayer.PingType.Gun, pos, 60f, 60f, text, false, nt));
                sb.Append($"\n  {player.displayName}: isPing=false noteType={nt} \"{text}\" at {FormatRustMapGrid(pos)} (60s)");
            }
            return sb.Length == 0 ? "No connected players."
                : $"Sent:{sb}\n  Sound? On MAP / COMPASS / WORLD widget — or not at all?";
        }

        /// <summary>Vanilla-path sweep of the given types (fresh pings — sound expected), one
        /// every `interval` seconds on a ring so each arrival can be told apart.</summary>
        private string RunPingTypes(List<BasePlayer> targets, IList<BasePlayer.PingType> types, float interval, float dist)
        {
            if (targets.Count == 0) return "No connected players.";
            interval = Mathf.Clamp(interval, 1f, 30f);
            dist = Mathf.Clamp(dist, 10f, 500f);
            var life = interval * types.Count + 60f;

            for (var i = 0; i < types.Count; i++)
            {
                var idx = i;
                var type = types[i];
                var style = BasePlayer.GetPingStyle(type);
                var slot = Array.IndexOf(AllPingTypes, type) + 1;
                timer.Once(interval * i, () =>
                {
                    foreach (var player in targets)
                    {
                        if (player == null || !player.IsConnected) continue;
                        var pos = ProbePos(player, dist, (slot - 1) * 360f / AllPingTypes.Length);
                        var before = player.State?.pings?.Count ?? 0;
                        player.AddPingAtLocation(type, pos, life, default(NetworkableId));
                        var pings = player.State?.pings;
                        if (pings != null && pings.Count > before && SendPingsMethod != null)
                        {
                            pings[pings.Count - 1].label = $"{slot} {type.ToString().ToUpperInvariant()} i{style.IconIndex} c{style.ColourIndex}";
                            player.DirtyPlayerState();
                            SendPingsMethod.Invoke(player, null);
                        }
                        else if (pings != null && pings.Count == before)
                        {
                            player.ChatMessage($"[MAPPoI] ping {slot} {type}: SKIPPED by AddPingAtLocation (same icon within 0.25m already exists) — run /pingtest clear first.");
                            continue;
                        }
                        player.ChatMessage($"[MAPPoI] ping {idx + 1}/{types.Count}: <color=#ffd479>{type}</color> (#{slot}, icon {style.IconIndex}, colour {style.ColourIndex}) — what did it sound like?");
                    }
                });
            }
            return $"Scheduling {types.Count} ping(s), one every {interval:0.#}s, {dist:0}m out: " +
                   string.Join(", ", types.Select(t => t.ToString()).ToArray()) +
                   $". Live {life:0}s; 'clear' removes them. All types: {PingTypeNames} (enum LAST=6).";
        }

        /// <summary>Wipes EVERY ping the target holds (their own too) and re-pushes the empty list.</summary>
        private string RunPingClear(List<BasePlayer> targets)
        {
            if (SendPingsMethod == null) return "SendPingsToClient not found on this build.";
            var n = 0;
            foreach (var player in targets)
            {
                _playerPings.Remove(player.userID);
                var pings = player.State?.pings;
                if (pings == null) continue;
                n += pings.Count;
                pings.Clear();
                player.DirtyPlayerState();
                SendPingsMethod.Invoke(player, null);
            }
            return $"Cleared {n} ping(s). Anything heard on REMOVAL?";
        }

        // ── console entry points ──

        [ConsoleCommand("mappoi.pingaged")]
        private void CcPingAged(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;
            arg.ReplyWith(RunPingAged(ProbeTargets(arg), ConsoleArgs(arg)));
        }

        [ConsoleCommand("mappoi.pingnote")]
        private void CcPingNote(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;
            arg.ReplyWith(RunPingNote(ProbeTargets(arg), ConsoleArgs(arg)));
        }

        [ConsoleCommand("mappoi.pingtypes")]
        private void CcPingTypes(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;
            var a = ConsoleArgs(arg);
            var interval = 5f; if (a.Length > 0) TryParseFloat(a[0], ref interval);
            var dist = 40f;    if (a.Length > 1) TryParseFloat(a[1], ref dist);
            arg.ReplyWith(RunPingTypes(ProbeTargets(arg), AllPingTypes, interval, dist));
        }

        [ConsoleCommand("mappoi.pingclear")]
        private void CcPingClear(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;
            arg.ReplyWith(RunPingClear(ProbeTargets(arg)));
        }

        // ── chat entry point ──

        /// <summary>/pingtest aged [frac] [type] | types [interval] | type &lt;name|1-7&gt; [name...] | note [text] [0|1] | clear</summary>
        [ChatCommand("pingtest")]
        private void CmdPingTest(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!IsAdmin(player)) { player.ChatMessage("You need the mappoi.admin permission."); return; }

            var me = new List<BasePlayer> { player };
            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            var rest = args.Skip(1).ToArray();
            string reply;

            switch (sub)
            {
                case "aged":
                    reply = RunPingAged(me, rest);
                    break;
                case "note":
                    reply = RunPingNote(me, rest);
                    break;
                case "types":
                {
                    var interval = 5f; if (rest.Length > 0) TryParseFloat(rest[0], ref interval);
                    reply = RunPingTypes(me, AllPingTypes, interval, 40f);
                    break;
                }
                case "type":
                {
                    var picked = new List<BasePlayer.PingType>();
                    foreach (var s in rest)
                    {
                        BasePlayer.PingType t;
                        if (TryParsePingType(s, out t)) picked.Add(t);
                        else { player.ChatMessage($"Unknown type '{s}'. Use a name or number: {PingTypeNames}"); return; }
                    }
                    if (picked.Count == 0) { player.ChatMessage("Usage: /pingtest type <name|1-7> [more...]   e.g. /pingtest type hostile build"); return; }
                    reply = RunPingTypes(me, picked, 5f, 40f);
                    break;
                }
                case "clear":
                    reply = RunPingClear(me);
                    break;
                default:
                    reply = "/pingtest aged [0-0.99] [type]  — probe #2, ping arrives that far through its life (default 0.5 Gun)\n" +
                            "/pingtest types [interval]      — all 7 types, one every N s (default 5)\n" +
                            "/pingtest type <name|1-7> ...   — just those types, e.g. /pingtest type 1 7\n" +
                            "/pingtest note [text] [0|1]     — probe #3, isPing=false via the ping channel\n" +
                            "/pingtest clear                 — wipe all your pings\n" +
                            "Types: " + PingTypeNames;
                    break;
            }
            player.ChatMessage(reply);
        }

        [ConsoleCommand("mappoi.resetcounters")]
        private void CcResetCounters(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;
            _dbgHookCalls = _dbgNoVictimKind = _dbgLowDamage = _dbgIngested = _dbgRejected = 0;
            _dbgLastReject = _dbgLastIngest = _dbgLastNoKind = "-";
            _dbgReasons.Clear();
            arg.ReplyWith("Counters reset.");
        }

        [ConsoleCommand("mappoi.config")]
        private void CcConfig(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;

            var argv = new List<string> { "config" };
            for (var i = 0; i < (arg.Args?.Length ?? 0); i++) argv.Add(arg.GetString(i));

            if (argv.Count < 2)
            {
                arg.ReplyWith(StripTags(DescribeConfig()));
                return;
            }

            var player = arg.Player();
            var reply = ApplyConfigChange(argv.ToArray());
            arg.ReplyWith(StripTags(reply) + "\n" + StripTags(DescribeConfig()));
            if (player != null) Say(player, reply);
        }

        /// <summary>Injects synthetic heat at a world position so an admin can preview the
        /// styles, colours and label wording without staging a real fight. Bypasses the
        /// visibility delay deliberately — it is a tuning aid, not a damage source.</summary>
        [ConsoleCommand("mappoi.simulate")]
        private void CcSimulate(ConsoleSystem.Arg arg)
        {
            if (!ConsoleAllowed(arg)) return;

            var n = arg.Args?.Length ?? 0;
            if (n < 2)
            {
                arg.ReplyWith("Usage: mappoi.simulate <x> <z> [heat=10] [category=pvp] [explosiveFraction=0] [actors=3] [spreadMetres=30]");
                return;
            }

            float x, z;
            if (!ArgFloat(arg, 0, out x) || !ArgFloat(arg, 1, out z))
            { arg.ReplyWith("x and z must be numbers."); return; }

            float heat, expFrac, actorsF, ring;
            if (n < 3 || !ArgFloat(arg, 2, out heat)) heat = 10f;
            var catName = n > 3 ? arg.GetString(3) : "pvp";
            if (n < 5 || !ArgFloat(arg, 4, out expFrac)) expFrac = 0f;
            if (n < 6 || !ArgFloat(arg, 5, out actorsF)) actorsF = 3f;
            if (n < 7 || !ArgFloat(arg, 6, out ring)) ring = 30f;

            expFrac = Mathf.Clamp01(expFrac);
            var actors = Mathf.Clamp(Mathf.RoundToInt(actorsF), 1, 64);
            heat = Mathf.Clamp(heat, 0.1f, 10000f);
            ring = Mathf.Clamp(ring, 0f, 2000f);

            var cat = Array.IndexOf(CatNames, (catName ?? "pvp").ToLowerInvariant());
            if (cat < 0) { arg.ReplyWith($"Unknown category. Valid: {string.Join(", ", CatNames)}"); return; }

            // Spread the synthetic weight over `actors` fake participants and a small cloud of
            // positions so the spread statistic and the participant gate both behave normally.
            var per = heat / actors;
            for (var i = 0; i < actors; i++)
            {
                var angle = i * Mathf.PI * 2f / actors;
                var p = new Vector3(x + Mathf.Cos(angle) * ring, 0f, z + Mathf.Sin(angle) * ring);
                var explosive = expFrac > 0f && i < Mathf.CeilToInt(actors * expFrac);
                Apply(p, per, cat, explosive, 1000000UL + (ulong)i, 0UL);
            }

            arg.ReplyWith($"Injected {heat:F1} heat ({CatNames[cat]}, {actors} actors) at {FormatRustMapGrid(new Vector3(x, 0f, z))}.");
        }

        // ── Legacy v1 decoy circles (default off) ────────────────────────────────
        private readonly Dictionary<ulong, MapMarkerGenericRadius> _legacyMarkers =
            new Dictionary<ulong, MapMarkerGenericRadius>();

        private void CreateLegacyMarker(BasePlayer player)
        {
            if (player?.net?.connection == null) return;
            ulong id = player.userID;

            MapMarkerGenericRadius existing;
            if (_legacyMarkers.TryGetValue(id, out existing))
            {
                _legacyMarkers.Remove(id);
                if (existing != null && !existing.IsDestroyed) existing.Kill();
            }

            var pos = LegacyNearbyPos(player.transform.position);
            var m = GameManager.server.CreateEntity(CirclePrefab, pos) as MapMarkerGenericRadius;
            if (m == null) return;

            m.OwnerID = player.userID;
            m.enableSaving = false;
            m.globalBroadcast = false;
            m.SetParent(player, worldPositionStays: true);
            m.limitNetworking = true;
            ApplyLegacyVisuals(m);
            m.Spawn();
            SendMarkerUpdate(m, player);
            _legacyMarkers[id] = m;
        }

        private void TickLegacyMarkers()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected || player.IsSleeping()) continue;
                if (player.net?.connection == null) continue;
                ulong id = player.userID;

                MapMarkerGenericRadius m;
                if (!_legacyMarkers.TryGetValue(id, out m) || m == null || m.IsDestroyed)
                {
                    CreateLegacyMarker(player);
                    continue;
                }

                m.transform.position = LegacyNearbyPos(player.transform.position);
                ApplyLegacyVisuals(m);
                m.SendNetworkUpdate();
                SendMarkerUpdate(m, player);
            }
        }

        private void ApplyLegacyVisuals(MapMarkerGenericRadius m)
        {
            m.color1 = new Color(1f, 0.1f, 0.1f, 1f);
            m.color2 = new Color(1f, 0.5f, 0.5f, 1f);
            m.alpha = 0.45f;
            m.radius = UnityEngine.Random.Range(_cfg.LegacyRadiusMin, _cfg.LegacyRadiusMax) / WorldSize;
        }

        private Vector3 LegacyNearbyPos(Vector3 centre)
        {
            var angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            var dist = UnityEngine.Random.Range(_cfg.LegacySpawnMin, _cfg.LegacySpawnMax);
            var x = centre.x + Mathf.Cos(angle) * dist;
            var z = centre.z + Mathf.Sin(angle) * dist;
            return new Vector3(x, TerrainHeight(new Vector3(x, 0f, z)), z);
        }
    }
}
