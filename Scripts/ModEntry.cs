// 文件：ModEntry.cs
// 在游戏启动时初始化配置与缓存，场景初始化时连接监听器。
// 监听容器变更事件，用缓冲队列管理Buff的添加和删除。

using System;
using System.Collections;
using System.Collections.Generic;
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
        private Queue<Action> _buffQueue = new Queue<Action>();
        private Coroutine _buffQueueCoroutine;

        private bool _buffUpdateScheduled = false;

        public static bool DebugMode; 

        protected override void OnAfterSetup()
        {
            base.OnAfterSetup();
            // 游戏启动时加载配置及缓存预制体
            if (_config == null)
            {
                _config = new ConfigManager();
                _config.Initialize();
                DebugMode = _config.Settings.debugMode;
                _config.CacheAllBuffPrefabs();

                _buffManager = new BuffManager(_config);
                _containerMonitor = new ContainerMonitor(_config);
                _containerTracker = new ContainerTracker(_config, _containerMonitor);
            }
        }

        private void OnEnable()
        {
            LevelManager.OnLevelInitialized -= OnLevelInitialized;
            LevelManager.OnLevelInitialized += OnLevelInitialized;
            
            Item.onUseStatic -= OnItemUsed;
            Item.onUseStatic += OnItemUsed;

            if (_buffQueueCoroutine != null) StopCoroutine(_buffQueueCoroutine);
            _buffQueueCoroutine = StartCoroutine(ProcessBuffQueue());
        }

        private void OnDisable()
        {
            if (_buffQueueCoroutine != null) 
            {
                StopCoroutine(_buffQueueCoroutine);
                _buffQueueCoroutine = null;
            }
            _buffQueue.Clear();

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
            // 确保 Config 已加载
            if (_config == null)
            {
                 OnAfterSetup();
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

            // 3. 差异更新 (改为添加到队列)
            var currentBuffs = _buffManager.GetActiveBuffs();
            
            // 需要添加的Buff
            foreach (var buffId in desiredBuffs)
            {
                if (!currentBuffs.Contains(buffId))
                {
                    EnqueueBuffAction(buffId, () => _buffManager.AddBuff(buffId), true);
                }
            }

            // 需要移除的Buff
            var buffsToRemove = new System.Collections.Generic.List<int>();
            foreach (var buffId in currentBuffs)
            {
                if (!desiredBuffs.Contains(buffId))
                {
                    buffsToRemove.Add(buffId);
                }
            }
            
            buffsToRemove.Reverse();

            foreach (var buffId in buffsToRemove)
            {
                EnqueueBuffAction(buffId, () => _buffManager.RemoveBuff(buffId), false);
            }
        }

        private void EnqueueBuffAction(int buffId, Action action, bool isAdd)
        {
            _buffQueue.Enqueue(action);
        }

        private IEnumerator ProcessBuffQueue()
        {
            while (true)
            {
                if (_buffQueue.Count > 0)
                {
                    var action = _buffQueue.Dequeue();
                    try
                    {
                        action?.Invoke();
                    }
                    catch (Exception ex)
                    {
                       if (DebugMode) Debug.LogError($"[PersistentPotionBuff] Error executing buff action: {ex.Message}");
                    }
                }
                yield return null; // 等待下一帧
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
        }
        private void OnAnySlotChanged(Slot slot)
        {
            bool changed = false;
            try { changed = _containerTracker?.HandleOwnerChange(slot) ?? false; } catch {}
        }
    }
}
