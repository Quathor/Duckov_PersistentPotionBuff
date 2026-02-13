// 文件：ContainerTracker.cs
// 负责在玩家背包、宠物背包与扩展槽位（Medic）中发现目标收纳包并管理其生命周期（添加/移除）；
// 向 `ContainerMonitor` 注册/注销容器，并提供 `GetAllTrackedSources()` 以统一枚举可订阅的来源。

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ItemStatsSystem;
using ItemStatsSystem.Items;
using Duckov.Utilities;

namespace PersistentPotionBuff
{
    public class ContainerTracker
    {
        private ConfigManager _config;
        private ContainerMonitor _monitor;

        public event Action OnContainerListChanged;

        // 玩家背包、宠物背包与扩展槽位集合
        private struct ParentOwner {
            public Inventory Inventory;
            public Slot Slot;
        }

        // 记录当前追踪的容器及其来源信息
        private Dictionary<Item, ParentOwner> _trackedContainers = new Dictionary<Item, ParentOwner>();
        
        // 暴露当前追踪的容器列表（只读）
        public HashSet<Item> TrackedContainers => new HashSet<Item>(_trackedContainers.Keys);

        // 追踪的 Medic 槽位
        private Dictionary<string, Slot> _trackedSlots = new Dictionary<string, Slot>();
        private int _targetContainerId = -1;

        public ContainerTracker(ConfigManager config, ContainerMonitor monitor) 
        { 
            _config = config; 
            _monitor = monitor;
        }

        public void Reset()
        {
            _trackedContainers.Clear();
            foreach (var kvp in _trackedSlots)
            {
                if (kvp.Value != null)
                {
                    try { kvp.Value.onSlotContentChanged -= OnTrackedSlotChanged; } catch {}
                }
            }
            _trackedSlots.Clear();
        }

        private struct ContainerLocation
        {
            public Item Container;
            public ParentOwner ParentOwner;
        }

        // 场景初始化时完整扫描
        public void UpdateTrackedContainers(int targetContainerId, List<string> additionalSlots)
        {
            _targetContainerId = targetContainerId;
            // 1. 在玩家/宠物/以及指定的 Medic 槽位 中查找目标收纳包容器
            List<ContainerLocation> foundLocations = FindContainersOnPlayer(targetContainerId, additionalSlots);
            HashSet<Item> newContainers = new HashSet<Item>(foundLocations.Select(x => x.Container));

            bool changed = false;

            // 2. 添加新容器
            foreach (var loc in foundLocations)
            {
                if (!_trackedContainers.ContainsKey(loc.Container))
                {
                    AddTrackedContainer(loc.Container, loc.ParentOwner);
                    changed = true;
                }
                else
                {
                    // 如果父级信息变更则更新记录
                    var currentOwner = _trackedContainers[loc.Container];
                    if (currentOwner.Inventory != loc.ParentOwner.Inventory || currentOwner.Slot != loc.ParentOwner.Slot)
                    {
                        _trackedContainers[loc.Container] = loc.ParentOwner;
                    }
                }
            }

            // 3. 移除不再存在的容器
            var toRemove = _trackedContainers.Keys.Where(k => !newContainers.Contains(k)).ToList();
            foreach (var c in toRemove)
            {
                RemoveTrackedContainer(c);
                changed = true;
            }

            if (changed) OnContainerListChanged?.Invoke();
        }

        private List<ContainerLocation> FindContainersOnPlayer(int targetID, List<string> additionalSlots)
        {
            List<ContainerLocation> result = new List<ContainerLocation>();

            // 玩家背包
            if (CharacterMainControl.Main != null && CharacterMainControl.Main.CharacterItem != null && CharacterMainControl.Main.CharacterItem.Inventory != null)
            {
                    var inv = CharacterMainControl.Main.CharacterItem.Inventory;
                    for (int i = 0; i < inv.Capacity; i++)
                    {
                        var it = inv.GetItemAt(i);
                        ScanItemForContainer(it, targetID, result, parentOwner: new ParentOwner { Inventory = inv });
                    }
            }

            // 宠物背包
            if (PetProxy.Instance != null && PetProxy.PetInventory != null)
            {
                for (int i = 0; i < PetProxy.PetInventory.Capacity; i++)
                {
                    var it = PetProxy.PetInventory.GetItemAt(i);
                    ScanItemForContainer(it, targetID, result, parentOwner: new ParentOwner { Inventory = PetProxy.PetInventory });
                }
            }

            // 角色额外槽位
            if (additionalSlots != null)
            {
                foreach (var slotName in additionalSlots)
                {
                    var slot = GetSlotByName(slotName);
                    if (slot != null)
                    {
                        if (!_trackedSlots.ContainsKey(slotName))
                        {
                            _trackedSlots[slotName] = slot;
                            slot.onSlotContentChanged += OnTrackedSlotChanged;
                        }

                        if (slot.Content != null)
                        {
                            ScanItemForContainer(slot.Content, targetID, result, parentOwner: new ParentOwner { Slot = slot });
                        }
                    }
                }
            }

            return result;
        }

        private void ScanItemForContainer(Item item, int targetID, List<ContainerLocation> result, ParentOwner parentOwner = default)
        {
            if (item == null) return;
            if (item.TypeID == targetID)
            {
                result.Add(new ContainerLocation { Container = item, ParentOwner = parentOwner });
            }
        }

        private void AddTrackedContainer(Item container, ParentOwner parentOwner)
        {
            if (container == null || _trackedContainers.ContainsKey(container)) return;
            _trackedContainers[container] = parentOwner;
            _monitor.AddContainer(container);
            try { if (ModBehaviour.DebugMode) Debug.Log($"[PersistentPotionBuff] 追踪容器添加: {container.TypeID}"); } catch {}
        }

        private void RemoveTrackedContainer(Item container)
        {
            if (container == null || !_trackedContainers.ContainsKey(container)) return;
            _trackedContainers.Remove(container);
            _monitor.RemoveContainer(container);
            try { if (ModBehaviour.DebugMode) Debug.Log($"[PersistentPotionBuff] 追踪容器移除: {container.TypeID}"); } catch {}
        }

        private void OnTrackedSlotChanged(Slot slot)
        {
            bool changed = HandleOwnerChange(slot);
            if (changed) OnContainerListChanged?.Invoke();
        }

        public bool HandleOwnerChange(Inventory inventory, int index)
        {
            return HandleInventoryChanged(inventory, index, _targetContainerId);
        }

        public bool HandleOwnerChange(Slot slot)
        {
            return HandleSlotChanged(slot, _targetContainerId);
        }

        public bool HandleInventoryChanged(Inventory inventory, int index, int targetID)
        {
            try
            {
                if (inventory == null) return false;
                HashSet<Item> found = new HashSet<Item>();
                for (int i = 0; i < inventory.Capacity; i++)
                {
                    var it = inventory[i];
                    if (it != null && it.TypeID == targetID) found.Add(it);
                }

                bool changed = false;

                // 移除
                var toRemove = _trackedContainers.Where(kv => kv.Value.Inventory == inventory && !found.Contains(kv.Key)).Select(kv => kv.Key).ToList();
                foreach (var c in toRemove) { RemoveTrackedContainer(c); changed = true; }

                // 添加
                foreach (var c in found)
                {
                    if (!_trackedContainers.ContainsKey(c)) 
                    { 
                        AddTrackedContainer(c, new ParentOwner { Inventory = inventory }); 
                        changed = true; 
                    }
                    else
                    {
                        var owner = _trackedContainers[c];
                        if (owner.Inventory == null) 
                        { 
                            _trackedContainers[c] = new ParentOwner { Inventory = inventory }; 
                            changed = true; 
                        }
                    }
                }

                if (changed) OnContainerListChanged?.Invoke();
                return changed;
            }
            catch { return false; }
        }

        public bool HandleSlotChanged(Slot slot, int targetID)
        {
            try
            {
                if (slot == null) return false;
                Item content = slot.Content;
                bool changed = false;

                if (content != null && content.TypeID == targetID)
                {
                    if (!_trackedContainers.ContainsKey(content)) 
                    { 
                        AddTrackedContainer(content, new ParentOwner { Slot = slot }); 
                        changed = true; 
                    }
                    else
                    {
                        var owner = _trackedContainers[content];
                        if (owner.Slot == null) 
                        { 
                            _trackedContainers[content] = new ParentOwner { Slot = slot }; 
                            changed = true; 
                        }
                    }
                }
                else
                {
                    var toRemove = _trackedContainers.Where(kv => kv.Value.Slot == slot).Select(kv => kv.Key).ToList();
                    foreach (var c in toRemove) { RemoveTrackedContainer(c); changed = true; }
                }

                if (changed) OnContainerListChanged?.Invoke();
                return changed;
            }
            catch { return false; }
        }

        private Slot GetSlotByName(string slotName)
        {
            try
            {
                var main = CharacterMainControl.Main;
                if (main == null || main.CharacterItem == null) return null;
                var slots = main.CharacterItem.Slots;
                if (slots == null) return null;
                
                Slot slot = null;
                try { slot = slots[slotName]; } catch { slot = null; }
                
                if (slot == null)
                {
                    foreach (var s in slots)
                    {
                        if (s != null && string.Equals(s.Key, slotName, StringComparison.OrdinalIgnoreCase))
                        {
                            slot = s; break;
                        }
                    }
                }
                return slot;
            }
            catch { return null; }
        }

        // 统一返回所有需要监听的容器来源（玩家背包、宠物背包、Medic等额外槽位）
        public static IEnumerable<(string Name, Inventory Inventory, Slot Slot)> GetAllTrackedSources()
        {
            // 玩家背包
            if (CharacterMainControl.Main != null && CharacterMainControl.Main.CharacterItem != null && CharacterMainControl.Main.CharacterItem.Inventory != null)
            {
                yield return ("PlayerInventory", CharacterMainControl.Main.CharacterItem.Inventory, null);
            }
            // 宠物背包
            if (PetProxy.Instance != null && PetProxy.PetInventory != null)
            {
                yield return ("PetInventory", PetProxy.PetInventory, null);
            }
            // Medic等额外槽位
            if (CharacterMainControl.Main != null && CharacterMainControl.Main.CharacterItem != null && CharacterMainControl.Main.CharacterItem.Slots != null)
            {
                foreach (var slot in CharacterMainControl.Main.CharacterItem.Slots)
                {
                    if (slot != null && !string.IsNullOrEmpty(slot.Key))
                    {
                        yield return ($"Slot:{slot.Key}", null, slot);
                    }
                }
            }
        }
    }
}
