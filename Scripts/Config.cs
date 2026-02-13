// 文件：Config.cs
// 负责加载并解析 `BuffMapping.json` 配置，将物品 ID 映射到 Mod Buff ID；
// 运行时设置（目标收纳包 ID、触发所需物品计数、是否在基地生效、以及额外需要监听的槽位如 Medic）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using Duckov.Buffs;
using Newtonsoft.Json;

namespace PersistentPotionBuff
{
    [Serializable]
    public class BuffMappingConfig
    {
        public List<BuffMappingEntry> mappings;
        public ConfigSettings settings;
    }

    [Serializable]
    public class BuffMappingEntry
    {
        public int itemId;
        public int buffId;
    }

    [Serializable]
    public class ConfigSettings
    {
        public int targetContainerId = 882;
        public int requiredItemCount = 3;
        public bool enableInBaseLevel = false;
        // 扩展槽位mod的位置（例如 Medic）
        public List<string> additionalSlots = new List<string> { "Medic" };
        public bool debugMode = false;
    }

    public class ConfigManager
    {
        public ConfigSettings Settings { get; private set; } = new ConfigSettings();
        public Dictionary<int, HashSet<int>> ItemIdToBuffIdsMap { get; private set; } = new Dictionary<int, HashSet<int>>();

        private readonly Dictionary<int, Buff> _buffPrefabCache = new Dictionary<int, Buff>();
        private bool _buffPrefabCacheReady = false;

        private string ConfigFilePath => Path.Combine(Application.dataPath, "..", "Duckov_Data", "Mods", "PersistentPotionBuff", "BuffMapping.json");

        public void Initialize()
        {
            ItemIdToBuffIdsMap.Clear();
            if (LoadConfigFromFile())
            {
                LoadDefaultConfig();
                if (Settings.debugMode) Debug.Log($"[PersistentPotionBuff] 成功加载配置，共 {ItemIdToBuffIdsMap.Count} 个物品映射");
            }
            else
            {
                Debug.LogWarning("[PersistentPotionBuff] 配置文件加载失败，使用默认配置");
                LoadDefaultConfig();
            }
        }

        public void CacheAllBuffPrefabs()
        {
            if (_buffPrefabCacheReady) return;

            _buffPrefabCache.Clear();
            try
            {
                foreach (var buff in Resources.FindObjectsOfTypeAll<Buff>())
                {
                    if (buff == null) continue;
                    if (buff.ID <= 0) continue;
                    if (_buffPrefabCache.ContainsKey(buff.ID)) continue;
                    _buffPrefabCache[buff.ID] = buff;
                }

                _buffPrefabCacheReady = true;
                if (Settings.debugMode) Debug.Log($"[PersistentPotionBuff] Buff 预制体缓存完成，共 {_buffPrefabCache.Count} 个");
            }
            catch (Exception e)
            {
                Debug.LogError($"[PersistentPotionBuff] Buff 预制体缓存失败: {e.Message}");
            }

            // 初始化 Buff 字段反射信息（缓存 FieldInfo，减少后续反射开销）
        }

        public Buff GetBuffPrefab(int buffId)
        {
            if (!_buffPrefabCacheReady) CacheAllBuffPrefabs();
            _buffPrefabCache.TryGetValue(buffId, out var prefab);
            return prefab;
        }

        private bool LoadConfigFromFile()
        {
            try
            {
                if (!File.Exists(ConfigFilePath))
                {
                    if (CopyTemplateConfigFromDllDirectory())
                    {
                        if (Settings.debugMode) Debug.Log("[PersistentPotionBuff] 已从DLL目录复制模板配置文件");
                    }
                    else
                    {
                        return false;
                    }
                }

                string json = File.ReadAllText(ConfigFilePath);
                BuffMappingConfig config = JsonConvert.DeserializeObject<BuffMappingConfig>(json);

                if (config == null || config.mappings == null) return false;

                foreach (var entry in config.mappings)
                {
                    if (entry.buffId > 0)
                    {
                        if (!ItemIdToBuffIdsMap.ContainsKey(entry.itemId))
                            ItemIdToBuffIdsMap[entry.itemId] = new HashSet<int>();
                        ItemIdToBuffIdsMap[entry.itemId].Add(entry.buffId);
                    }
                }

                if (config.settings != null) Settings = config.settings;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[PersistentPotionBuff] 加载配置文件失败: {e.Message}");
                return false;
            }
        }

        private bool CopyTemplateConfigFromDllDirectory()
        {
            try
            {
                string dllPath = Assembly.GetExecutingAssembly().Location;
                string dllDirectory = Path.GetDirectoryName(dllPath);
                string templatePath = Path.Combine(dllDirectory, "BuffMapping.example.json");

                if (!File.Exists(templatePath)) return false;

                string targetDirectory = Path.GetDirectoryName(ConfigFilePath);
                if (!Directory.Exists(targetDirectory)) Directory.CreateDirectory(targetDirectory);

                File.Copy(templatePath, ConfigFilePath, false);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[PersistentPotionBuff] 复制模板文件失败: {e.Message}");
                return false;
            }
        }

        private void LoadDefaultConfig()
        {
            void AddMapping(int itemId, int buffId)
            {
                if (!ItemIdToBuffIdsMap.ContainsKey(itemId))
                {
                    ItemIdToBuffIdsMap[itemId] = new HashSet<int>();
                    ItemIdToBuffIdsMap[itemId].Add(buffId);
                }
            }

            AddMapping(0, 1201);      // 夜视
            AddMapping(137, 1011);    // 加速
            AddMapping(398, 1012);    // 负重
            AddMapping(408, 1072);    // 电抗
            AddMapping(409, 1084);    // 痛觉抗性
            AddMapping(438, 1092);    // 热血
            AddMapping(797, 1013);    // 护甲
            AddMapping(798, 1014);    // 耐力
            AddMapping(800, 1015);    // 近战伤害
            AddMapping(872, 1017);    // 后坐力控制
            AddMapping(875, 1018);    // 持续治疗
            AddMapping(856, 1113);    // 风暴保护
            AddMapping(1070, 1074);   // 火焰抗性
            AddMapping(1071, 1075);   // 毒抗
            AddMapping(1072, 1076);   // 空间抗性
            AddMapping(1247, 1019);   // 出血抗性
            AddMapping(1400, 1206);   // 
            AddMapping(1401, 1207);   // 
        }
    }
}
