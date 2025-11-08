using Oxide.Core;
using Oxide.Plugins;
using Newtonsoft.Json;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("FuelStack Optimizer", "RogueAssassin", "1.0.13")]
    [Description("Optimize Fuel Generators with global, per-generator, and prefab overrides, plus batch processing and auto-cleanup for high performance.")]
    public class FuelStackOptimizer : RustPlugin
    {
        #region Config

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Version (DO NOT CHANGE)", Order = int.MaxValue)]
            public VersionNumber Version = new VersionNumber(1, 0, 13);

            [JsonProperty(PropertyName = "Global Max Fuel Stack Size", Order = 1)]
            public int GlobalStackMax = 1000;

            [JsonProperty(PropertyName = "Per Generator Overrides by NetID (NetID : MaxStack)", Order = 2)]
            public Dictionary<ulong, int> GeneratorOverrides = new Dictionary<ulong, int>();

            [JsonProperty(PropertyName = "Per Generator Overrides by Name (Name : MaxStack)", Order = 3)]
            public Dictionary<string, int> NameOverrides = new Dictionary<string, int>();

            [JsonProperty(PropertyName = "Per Generator Overrides by Prefab (Prefab : MaxStack)", Order = 4)]
            public Dictionary<string, int> PrefabOverrides = new Dictionary<string, int>();

            [JsonProperty(PropertyName = "Whitelist Players (Optional)", Order = 5)]
            public List<string> WhitelistPlayers = new List<string>();

            [JsonProperty(PropertyName = "Blacklist Players (Optional)", Order = 6)]
            public List<string> BlacklistPlayers = new List<string>();

            [JsonProperty(PropertyName = "Enable Batch Scanning of Generators (Reduces CPU spikes on large servers)", Order = 7)]
            public bool EnableBatchScanning = true;

            [JsonProperty(PropertyName = "Batch Size per Tick (Number of generators to process per server tick when batch scanning is enabled)", Order = 8)]
            public int BatchSize = 20;

            [JsonProperty(PropertyName = "Auto-Cleanup Interval (Seconds) - remove destroyed generators from tracking list periodically; 0 = disabled", Order = 9)]
            public float AutoCleanupInterval = 300f; // default: 5 minutes
        }

        private ConfigData _config;

        protected override void LoadDefaultConfig()
        {
            _config = new ConfigData();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<ConfigData>() ?? new ConfigData();

            if (_config.Version < Version)
            {
                MigrateConfig();
            }

            SaveConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void MigrateConfig()
        {
            PrintWarning("Outdated config detected. Migrating to version " + Version + " …");
            _config.Version = Version;
        }

        #endregion

        #region Fields

        private HashSet<FuelGenerator> _generators = new HashSet<FuelGenerator>();
        private Queue<FuelGenerator> _batchQueue = new Queue<FuelGenerator>();
        private bool _batchProcessingActive = false;

        private float _cleanupTimer = 0f;
        private const float TimerInterval = 0.1f;

        #endregion

        #region Hooks

        private void OnServerInitialized()
        {
            PrintWarning($"FuelStack Optimizer v{_config.Version} loaded!");

            Puts("Scanning for existing Fuel Generators...");
            int count = 0;

            foreach (var entity in BaseNetworkable.serverEntities)
            {
                if (entity is FuelGenerator generator && generator.inventory != null)
                {
                    if (_generators.Add(generator))
                    {
                        if (_config.EnableBatchScanning)
                            _batchQueue.Enqueue(generator);
                        else
                            ApplyStackSize(generator);
                        count++;
                    }
                }
            }

            // Start single recurring timer for batch + cleanup
            timer.Every(TimerInterval, SingleRecurringUpdate);

            Puts($"Found {count} Fuel Generators. {_batchQueue.Count} queued for batch processing.");
        }

        private void OnEntitySpawned(BaseEntity entity)
        {
            if (entity is FuelGenerator generator && generator.inventory != null)
            {
                if (_generators.Add(generator))
                {
                    // Immediate prefab override application
                    if (!string.IsNullOrEmpty(generator.PrefabName) && _config.PrefabOverrides.TryGetValue(generator.PrefabName, out int prefabStack))
                    {
                        generator.inventory.maxStackSize = prefabStack;
                        var items = generator.inventory.itemList;
                        if (items != null)
                        {
                            foreach (var item in items)
                            {
                                if (item != null && item.amount > prefabStack)
                                    item.amount = prefabStack;
                            }
                        }
                        Puts($"New Fuel Generator spawned (Prefab: {generator.PrefabName}). Prefab override applied immediately: {prefabStack}");
                    }
                    else if (_config.EnableBatchScanning)
                    {
                        _batchQueue.Enqueue(generator);
                        _batchProcessingActive = true;
                    }
                    else
                    {
                        ApplyStackSize(generator);
                    }
                }
            }
        }

        private void OnEntityKill(BaseEntity entity)
        {
            if (entity is FuelGenerator generator)
            {
                _generators.Remove(generator);
                Puts($"Fuel Generator destroyed (NetID: {(generator.net != null ? generator.net.ID.Value : 0UL)}). Removed from tracking list.");
            }
        }

        #endregion

        #region Helpers

        private void SingleRecurringUpdate()
        {
            // Batch processing
            if (_batchQueue.Count > 0)
            {
                int processed = 0;
                while (_batchQueue.Count > 0 && processed < _config.BatchSize)
                {
                    var gen = _batchQueue.Dequeue();
                    if (gen != null && !gen.IsDestroyed)
                        ApplyStackSize(gen);
                    processed++;
                }
            }

            // Auto-cleanup
            if (_config.AutoCleanupInterval > 0f)
            {
                _cleanupTimer += TimerInterval;
                if (_cleanupTimer >= _config.AutoCleanupInterval)
                {
                    CleanupGenerators();
                    _cleanupTimer = 0f;
                }
            }
        }

        private int GetMaxStack(FuelGenerator generator)
        {
            ulong netID = generator.net != null ? generator.net.ID.Value : 0UL;
            string name = generator.ShortPrefabName;
            string prefab = generator.PrefabName;

            if (_config.GeneratorOverrides.TryGetValue(netID, out int idOverride))
                return idOverride;

            if (!string.IsNullOrEmpty(name) && _config.NameOverrides.TryGetValue(name, out int nameOverride))
                return nameOverride;

            if (!string.IsNullOrEmpty(prefab) && _config.PrefabOverrides.TryGetValue(prefab, out int prefabOverride))
                return prefabOverride;

            return _config.GlobalStackMax;
        }

        private bool IsPlayerAllowed(BasePlayer player)
        {
            string name = player.displayName;
            if (_config.BlacklistPlayers.Contains(name)) return false;
            if (_config.WhitelistPlayers.Count == 0) return true;
            return _config.WhitelistPlayers.Contains(name);
        }

        private void ApplyStackSize(FuelGenerator generator)
        {
            try
            {
                int maxStack = GetMaxStack(generator);
                generator.inventory.maxStackSize = maxStack;

                var items = generator.inventory.itemList;
                if (items != null)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        var item = items[i];
                        if (item != null && item.amount > maxStack)
                            item.amount = maxStack;
                    }
                }
            }
            catch (System.Exception ex)
            {
                PrintError($"[FuelStackOptimizer] Error applying stack size to generator (NetID: {(generator.net != null ? generator.net.ID.Value : 0UL)}): {ex}");
            }
        }

        private void UpdateAllGenerators()
        {
            CleanupGenerators();

            foreach (var gen in _generators)
            {
                if (gen?.inventory != null)
                {
                    ApplyStackSize(gen);
                }
            }

            Puts($"Applied stack size update to {_generators.Count} Fuel Generators (global + overrides).");
        }

        private void CleanupGenerators()
        {
            int removed = 0;
            FuelGenerator[] generatorsCopy = new FuelGenerator[_generators.Count];
            _generators.CopyTo(generatorsCopy);

            foreach (var gen in generatorsCopy)
            {
                if (gen == null || gen.IsDestroyed)
                {
                    _generators.Remove(gen);
                    removed++;
                }
            }

            if (removed > 0)
                Puts($"Cleaned up {removed} destroyed Fuel Generators from tracking list.");
        }

        #endregion

        #region Commands

        [ChatCommand("updategenerators")]
        private void CmdUpdateGenerators(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;

            UpdateAllGenerators();
            player.ChatMessage($"[Server] All Fuel Generators updated to global max stack size {_config.GlobalStackMax} (with overrides applied).");
        }

        [ChatCommand("setgeneratorstack")]
        private void CmdSetGeneratorStack(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) { player.ChatMessage("You are not allowed to use this command."); return; }
            if (args.Length != 2) { player.ChatMessage("Usage: /setgeneratorstack <NetID|Name|Prefab> <StackSize>"); return; }

            string key = args[0];
            if (!int.TryParse(args[1], out int stack)) { player.ChatMessage("Invalid stack size. Must be a number."); return; }

            if (ulong.TryParse(key, out ulong netID))
            {
                _config.GeneratorOverrides[netID] = stack;
                SaveConfig();
                if (_generators.TryGetValue(out var genForID, g => g.net != null && g.net.ID.Value == netID)) ApplyStackSize(genForID);
                player.ChatMessage($"Override set: Generator NetID {netID} → max stack {stack}");
                Puts($"Override set: Generator NetID {netID} → max stack {stack}");
                return;
            }

            _config.NameOverrides[key] = stack;
            SaveConfig();
            foreach (var gen in _generators)
            {
                if (gen.ShortPrefabName == key) ApplyStackSize(gen);
            }
            player.ChatMessage($"Override set: Generator Name '{key}' → max stack {stack}");
            Puts($"Override set: Generator Name '{key}' → max stack {stack}");
        }

        [ChatCommand("listoverrides")]
        private void CmdListOverrides(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;

            player.ChatMessage("=== FuelStack Optimizer Overrides ===");
            player.ChatMessage($"Global max stack: {_config.GlobalStackMax}");
            foreach (var kv in _config.GeneratorOverrides) player.ChatMessage($"NetID {kv.Key} → {kv.Value}");
            foreach (var kv in _config.NameOverrides) player.ChatMessage($"Name '{kv.Key}' → {kv.Value}");
            foreach (var kv in _config.PrefabOverrides) player.ChatMessage($"Prefab '{kv.Key}' → {kv.Value}");
        }

        #endregion
    }

    public static class HashSetExtensions
    {
        public static bool TryGetValue<T>(this HashSet<T> set, out T value, System.Predicate<T> match)
        {
            foreach (var item in set)
            {
                if (match(item)) { value = item; return true; }
            }
            value = default; return false;
        }
    }
}
