// 文件：ModEntry.cs
// Mod 入口：初始化配置与 Buff 缓存，创建并连接 `ContainerTracker` / `ContainerMonitor` / `BuffManager`。
// 统一订阅并监听玩家背包、宠物背包与扩展槽位，按帧末合并触发 Buff 更新，
// 并负责物品使用事件的记录转发与订阅管理（Subscribe/UnsubscribeAllTrackedSources）。

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using Duckov.Modding;
using ItemStatsSystem;
using ItemStatsSystem.Items;

namespace PersistentPotionBuff
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private ConfigManager _config;
        private ContainerTracker _containerTracker;
        private ContainerMonitor _containerMonitor;
        private BuffManager _buffManager;


        private bool _buffUpdateScheduled = false;

        public static bool DebugMode; 

        private void OnEnable()
        {
            LevelManager.OnLevelInitialized -= OnLevelInitialized;
            LevelManager.OnLevelInitialized += OnLevelInitialized;
            
            Item.onUseStatic -= OnItemUsed;
            Item.onUseStatic += OnItemUsed;
        }

        private void OnDisable()
        {
            LevelManager.OnLevelInitialized -= OnLevelInitialized;
            Item.onUseStatic -= OnItemUsed;

            UnsubscribeAllTrackedSources();

            if (_containerTracker != null)
            {
                _containerTracker.OnContainerListChanged -= OnContainerListChanged;
                _containerTracker.Reset();
            }
            if (_containerMonitor != null)
            {
                _containerMonitor.OnContentChanged -= OnContainerContentChanged;
                _containerMonitor.Reset();
            }
        }

        private void OnDestroy()
        {
            OnDisable();
        }

        private void OnLevelInitialized()
        {
            if (_config == null)
            {
                _config = new ConfigManager();
                _buffManager = new BuffManager(_config);
                _config.Initialize();
                DebugMode = _config.Settings.debugMode;
                _config.CacheAllBuffPrefabs();
                
                _containerMonitor = new ContainerMonitor(_config);
                _containerTracker = new ContainerTracker(_config, _containerMonitor);
            }

            if (_containerTracker != null)
            {
                _containerTracker.OnContainerListChanged -= OnContainerListChanged;
                _containerTracker.OnContainerListChanged += OnContainerListChanged;
            }
            if (_containerMonitor != null)
            {
                _containerMonitor.OnContentChanged -= OnContainerContentChanged;
                _containerMonitor.OnContentChanged += OnContainerContentChanged;
            }

            _containerTracker.Reset();
            _containerMonitor.Reset();
            _buffManager.Reset();

            // 基地场景判断
            if (LevelConfig.IsBaseLevel && !_config.Settings.enableInBaseLevel)
            {
                if (DebugMode) Debug.Log("[PersistentPotionBuff] 基地场景跳过初始化");
                return;
            }

            if (DebugMode) Debug.Log("[PersistentPotionBuff] 场景初始化");
            StartCoroutine(BootstrapInitialization());
        }

        private void OnItemUsed(Item item, object user)
        {
            try 
            { 
                if (_buffManager != null) _buffManager.OnItemUsed(item);
            } 
            catch {}
        }

        private IEnumerator BootstrapInitialization()
        {
            // 1. 等待玩家背包加载
            int guard = 0;
            while ((LevelManager.Instance == null || CharacterMainControl.Main == null) && guard < 600)
            {
                guard++;
                yield return null;
            }

            if (CharacterMainControl.Main == null)
            {
                yield break;
            }

            // 2. 等待宠物背包加载
            int petGuard = 0;
            while ((PetProxy.Instance == null || PetProxy.PetInventory == null) && petGuard < 300)
            {
                petGuard++;
                yield return null;
            }

            // 3. 等待额外槽位加载
            int slotGuard = 0;
            while ((CharacterMainControl.Main.CharacterItem == null || CharacterMainControl.Main.CharacterItem.Slots == null) && slotGuard < 300)
            {
                slotGuard++;
                yield return null;
            }

            // 4. 执行一次统一的目录扫描
            _containerTracker.UpdateTrackedContainers(_config.Settings.targetContainerId, _config.Settings.additionalSlots);
            yield return new WaitForEndOfFrame();
            // _containerMonitor.RefreshAll(); 
            // try { if (DebugMode) Debug.Log("[PersistentPotionBuff] 刷新一次容器内容"); } catch {}

            SubscribeAllTrackedSources();
        }

        private void OnContainerListChanged()
        {
            ScheduleBuffUpdate();
        }

        private void OnContainerContentChanged()
        {
            ScheduleBuffUpdate();
        }

        private void ScheduleBuffUpdate()
        {
            if (_buffUpdateScheduled) return;
            _buffUpdateScheduled = true;
            StartCoroutine(ApplyBuffUpdateAtEndOfFrame());
        }

        private IEnumerator ApplyBuffUpdateAtEndOfFrame()
        {
            // buff更新放到帧末
            yield return new WaitForEndOfFrame(); 
            _buffUpdateScheduled = false;

            if (_buffManager == null || _containerMonitor == null) yield break;

            // 基地场景判断
            if (LevelConfig.IsBaseLevel && !_config.Settings.enableInBaseLevel) yield break;
            
            // 1. 获取统计数量
            var counts = _containerMonitor.GetTotalItemCounts();

            // 2. 计算期望 Buff
            System.Collections.Generic.HashSet<int> desiredBuffs = new System.Collections.Generic.HashSet<int>();
            foreach (var kvp in counts)
            {
                if (kvp.Value >= _config.Settings.requiredItemCount)
                {
                    if (_config.ItemIdToBuffIdsMap.TryGetValue(kvp.Key, out System.Collections.Generic.HashSet<int> buffIds))
                    {
                        foreach (var id in buffIds) desiredBuffs.Add(id);
                    }
                }
            }

            // 3. 差异更新
            var currentBuffs = _buffManager.GetActiveBuffs();
            
            foreach (var buffId in desiredBuffs)
            {
                if (!currentBuffs.Contains(buffId)) _buffManager.AddBuff(buffId);
            }

            foreach (var buffId in currentBuffs)
            {
                if (!desiredBuffs.Contains(buffId)) _buffManager.RemoveBuff(buffId);
            }
        }

        // 统一监听所有容器来源的内容变化（玩家背包、宠物背包、Medic等）
        private void SubscribeAllTrackedSources()
        {
            foreach (var (name, inventory, slot) in ContainerTracker.GetAllTrackedSources())
            {
                if (inventory != null)
                {
                    inventory.onContentChanged -= OnAnyInventoryChanged;
                    inventory.onContentChanged += OnAnyInventoryChanged;
                }
                if (slot != null)
                {
                    slot.onSlotContentChanged -= OnAnySlotChanged;
                    slot.onSlotContentChanged += OnAnySlotChanged;
                }
            }
        }

        private void UnsubscribeAllTrackedSources()
        {
            foreach (var (name, inventory, slot) in ContainerTracker.GetAllTrackedSources())
            {
                if (inventory != null)
                {
                    try { inventory.onContentChanged -= OnAnyInventoryChanged; } catch {}
                }
                if (slot != null)
                {
                    try { slot.onSlotContentChanged -= OnAnySlotChanged; } catch {}
                }
            }
        }

        // 玩家背包、宠物背包、Medic等槽位的内容变化处理
        private void OnAnyInventoryChanged(Inventory inv, int idx)
        {
            bool changed = false;
            try { changed = _containerTracker?.HandleOwnerChange(inv, idx) ?? false; } catch {}
            // if (changed) try { Debug.Log($"[PersistentPotionBuff] 检测到收纳包的添加或移除，进行更新"); } catch {}
        }
        private void OnAnySlotChanged(Slot slot)
        {
            bool changed = false;
            try { changed = _containerTracker?.HandleOwnerChange(slot) ?? false; } catch {}
            // if (changed) try { Debug.Log($"[PersistentPotionBuff] 检测到收纳包的添加或移除，进行更新"); } catch {}
        }
    }
}
