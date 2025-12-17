// 文件：BuffManager.cs
// 管理 Mod 自身激活的 Buff（添加/移除），记录最近使用的物品以区分原版Buff；
// 在移除 Buff 时检查（移除 Mod 的无限时长 Buff，或在玩家使用药剂时重置为有限时长）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Duckov.Buffs;
using ItemStatsSystem;

namespace PersistentPotionBuff
{
    public class BuffManager
    {
        private HashSet<int> _activeModBuffIDs = new HashSet<int>();
        private ConfigManager _config;

        // 记录最近使用的物品信息
        private int _lastUsedItemId = -1;
        private float _lastUsedTime = -1f;
        private HashSet<int> _lastUsedVanillaBuffIds = new HashSet<int>();

        // 缓存反射字段
        private static FieldInfo _limitedLifeTimeField;
        private static FieldInfo _totalLifeTimeField;
        private static bool _fieldInfoInitialized = false;

        public BuffManager(ConfigManager config)
        {
            _config = config;
            InitializeBuffFieldInfos();
        }

        private void InitializeBuffFieldInfos()
        {
            if (_fieldInfoInitialized) return;
            try
            {
                var buffType = typeof(Buff);
                _limitedLifeTimeField = buffType.GetField("limitedLifeTime", BindingFlags.NonPublic | BindingFlags.Instance);
                _totalLifeTimeField = buffType.GetField("totalLifeTime", BindingFlags.NonPublic | BindingFlags.Instance);
                _fieldInfoInitialized = true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PersistentPotionBuff] 初始化 Buff 字段反射信息失败: {e.Message}");
            }
        }

        public void SetBuffInfiniteTime(Buff buff)
        {
            if (buff == null) return;
            InitializeBuffFieldInfos();
            try
            {
                _limitedLifeTimeField?.SetValue(buff, false);
                _totalLifeTimeField?.SetValue(buff, 999999f);
            }
            catch (Exception e)
            {
                Debug.LogError($"[PersistentPotionBuff] 设置 Buff 无限时间失败: {e.Message}");
            }
        }

        public void OnItemUsed(Item item)
        {
            if (item == null) return;
            _lastUsedItemId = item.TypeID;
            _lastUsedTime = Time.time;
            _lastUsedVanillaBuffIds = GetVanillaBuffIds(item);
        }

        public void Reset()
        {
            _activeModBuffIDs.Clear();
            _lastUsedItemId = -1;
            _lastUsedTime = -1f;
            _lastUsedVanillaBuffIds.Clear();
        }

        public HashSet<int> GetActiveBuffs()
        {
            return new HashSet<int>(_activeModBuffIDs);
        }

        public void AddBuff(int buffId)
        {
            if (_activeModBuffIDs.Contains(buffId)) return;

            CharacterMainControl player = CharacterMainControl.Main;
            if (player == null) return;

            Buff buffPrefab = _config.GetBuffPrefab(buffId);
            if (buffPrefab != null)
            {
                player.AddBuff(buffPrefab, player);
                _activeModBuffIDs.Add(buffId);

                // 设置无限时长
                var buffManager = player.GetBuffManager();
                if (buffManager != null)
                {
                    var addedBuff = buffManager.Buffs.FirstOrDefault(b => b.ID == buffPrefab.ID);
                    if (addedBuff != null)
                    {
                        this.SetBuffInfiniteTime(addedBuff);
                    }
                }
                Debug.Log($"[PersistentPotionBuff] 添加Mod Buff: {buffId}");
            }
        }

        public void RemoveBuff(int buffId)
        {
            if (!_activeModBuffIDs.Contains(buffId)) return;

            CharacterMainControl player = CharacterMainControl.Main;
            if (player == null) 
            {
                _activeModBuffIDs.Remove(buffId);
                return;
            }

            var buffManager = player.GetBuffManager();
            if (buffManager == null) 
            {
                _activeModBuffIDs.Remove(buffId);
                return;
            }

            var currentBuff = buffManager.Buffs.FirstOrDefault(b => b.ID == buffId);
            if (currentBuff != null)
            {
                // 是否为 Mod 设置的无限时长 Buff
                if (IsInfiniteBuff(currentBuff))
                {
                    // 是否因为使用药剂导致数量减少
                    bool isVanillaUsage = false;
                    
                    // 是否是最近使用的物品类型
                    if (_config.ItemIdToBuffIdsMap.TryGetValue(_lastUsedItemId, out HashSet<int> mappedBuffs) && mappedBuffs.Contains(buffId))
                    {
                        // 1.0秒内使用的物品
                        if (Time.time - _lastUsedTime < 1.0f)
                        {
                            // 是否是原版 Buff
                            if (_lastUsedVanillaBuffIds.Contains(buffId))
                            {
                                isVanillaUsage = true;
                            }
                        }
                    }

                    if (isVanillaUsage)
                    {
                        // 重置为有限时长
                        ResetBuffToFinite(currentBuff, buffId);
                        Debug.Log($"[PersistentPotionBuff] 使用药剂， Buff {buffId} 重置为有限时长");
                    }
                    else
                    {
                        // 移除 Mod Buff
                        player.RemoveBuff(buffId, false);
                        Debug.Log($"[PersistentPotionBuff] 移除Mod Buff: {buffId}");
                    }
                }
                else
                {
                    Debug.Log($"[PersistentPotionBuff] 跳过移除 Buff {buffId}");
                }
            }
            
            //  从 Mod 维护列表中删除
            _activeModBuffIDs.Remove(buffId);
        }

        private bool IsInfiniteBuff(Buff buff)
        {
            if (buff == null) return false;
            try
            {
                // 检查 limitedLifeTime 是否为 false
                bool limited = (bool)_limitedLifeTimeField.GetValue(buff);
                if (limited) return false;

                // 检查 totalLifeTime 是否足够大
                float totalTime = (float)_totalLifeTimeField.GetValue(buff);
                return totalTime > 900000f;
            }
            catch
            {
                return false;
            }
        }

        private HashSet<int> GetVanillaBuffIds(Item item)
        {
            HashSet<int> ids = new HashSet<int>();
            if (item == null) return ids;
            try
            {
                var components = item.GetComponents<MonoBehaviour>();
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    Type type = comp.GetType();
                    if (type.FullName == "Duckov.ItemUsage.AddBuff")
                    {
                        var field = type.GetField("buffPrefab", BindingFlags.Public | BindingFlags.Instance);
                        if (field != null)
                        {
                            Buff buff = field.GetValue(comp) as Buff;
                            if (buff != null) ids.Add(buff.ID);
                        }
                    }
                }
            }
            catch {}
            return ids;
        }

        private void ResetBuffToFinite(Buff buff, int buffId)
        {
            if (buff == null) return;
            
            // 获取 Prefab 的默认值
            float defaultLifeTime = 60f; 
            bool defaultLimited = true;
            
            Buff prefab = _config.GetBuffPrefab(buffId);
            if (prefab != null)
            {
                try {
                    if (_totalLifeTimeField != null) defaultLifeTime = (float)_totalLifeTimeField.GetValue(prefab);
                    if (_limitedLifeTimeField != null) defaultLimited = (bool)_limitedLifeTimeField.GetValue(prefab);
                } catch {}
            }
            
            try {
                if (_limitedLifeTimeField != null) _limitedLifeTimeField.SetValue(buff, defaultLimited);
                if (_totalLifeTimeField != null) _totalLifeTimeField.SetValue(buff, defaultLifeTime);
            } catch {}
        }
    }
}
