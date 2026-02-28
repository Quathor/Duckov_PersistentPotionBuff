// 文件：ContainerMonitor.cs
// 维护所有被追踪容器内的物品计数，
// 在物品增删时触发局部或全局的统计刷新。

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ItemStatsSystem;
using ItemStatsSystem.Items;

namespace PersistentPotionBuff
{
    public class ContainerMonitor
    {
        private ConfigManager _config;
        public event Action OnContentChanged;

        private class TrackedInfo {
            public Item Container;
            public Dictionary<Item, Action<Item>> ChildItemHandlers = new Dictionary<Item, Action<Item>>();
            public Dictionary<Slot, Action<Slot>> ChildSlotHandlers = new Dictionary<Slot, Action<Slot>>();
        }

        private Dictionary<Item, TrackedInfo> _tracked = new Dictionary<Item, TrackedInfo>();
        public HashSet<Item> TrackedContainers => new HashSet<Item>(_tracked.Keys);

        // 存储每个被追踪容器上次统计的内容计数，用于检测是否真正变更
        private Dictionary<Item, Dictionary<int, int>> _containerItemCounts = new Dictionary<Item, Dictionary<int,int>>();

        public ContainerMonitor(ConfigManager config)
        {
            _config = config;
        }

        public void Reset()
        {
            foreach(var container in _tracked.Keys.ToList())
            {
                UnsubscribeFromContainer(container);
            }
            _tracked.Clear();
            _containerItemCounts.Clear();
        }

        public void AddContainer(Item container)
        {
            if (container == null || _tracked.ContainsKey(container)) return;
            
            var info = new TrackedInfo { Container = container };
            _tracked[container] = info;

            // 初始化快照
            _containerItemCounts[container] = CountItemsInContainer(container);

            // 订阅内部变化
            UpdateChildItemSubscriptions(container);
        }

        public void RemoveContainer(Item container)
        {
            if (container == null || !_tracked.ContainsKey(container)) return;
            
            UnsubscribeFromContainer(container);
            _tracked.Remove(container);
            _containerItemCounts.Remove(container);
        }

        public void RefreshAll()
        {
            bool changed = false;
            foreach (var container in _tracked.Keys.ToList())
            {
                if (container == null) continue;
                UpdateChildItemSubscriptions(container);
                if (CheckAndUpdateContainerSnapshot(container)) 
                {
                    changed = true;
                }
            }
            if (changed) OnContentChanged?.Invoke();
        }

        public Dictionary<int, int> GetTotalItemCounts()
        {
            Dictionary<int, int> totalCounts = new Dictionary<int, int>();
            foreach (var container in _tracked.Keys)
            {
                var counts = CountItemsInContainer(container);
                foreach(var kvp in counts)
                {
                    if (!totalCounts.ContainsKey(kvp.Key)) totalCounts[kvp.Key] = 0;
                    totalCounts[kvp.Key] += kvp.Value;
                }
            }
            return totalCounts;
        }

        private Dictionary<int, int> CountItemsInContainer(Item container)
        {
            Dictionary<int, int> counts = new Dictionary<int, int>();
            if (container == null) return counts;
            if (container.Slots != null)
            {
                foreach (var slot in container.Slots)
                {
                    if (slot == null || slot.Content == null) continue;
                    var item = slot.Content;
                    if (!counts.ContainsKey(item.TypeID)) counts[item.TypeID] = 0;
                    counts[item.TypeID] += item.StackCount;
                }
            }
            return counts;
        }

        private bool CheckAndUpdateContainerSnapshot(Item container)
        {
            try
            {
                var newCounts = CountItemsInContainer(container);
                if (!_containerItemCounts.TryGetValue(container, out var oldCounts))
                {
                    _containerItemCounts[container] = newCounts;
                    return true;
                }

                bool changed = false;
                if (oldCounts.Count != newCounts.Count) changed = true;
                else
                {
                    foreach (var kv in newCounts)
                    {
                        if (!oldCounts.TryGetValue(kv.Key, out int oldValue) || oldValue != kv.Value)
                        {
                            changed = true;
                            break;
                        }
                    }
                }

                if (changed)
                {
                    _containerItemCounts[container] = newCounts;
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private void UnsubscribeFromContainer(Item container)
        {
            if (_tracked.TryGetValue(container, out var info))
            {
                foreach (var kv in info.ChildItemHandlers.ToList())
                {
                    try { kv.Key.onItemTreeChanged -= kv.Value; } catch {}
                    try { kv.Key.onSetStackCount -= kv.Value; } catch {}
                }
                info.ChildItemHandlers.Clear();

                foreach (var kv in info.ChildSlotHandlers.ToList())
                {
                    try { kv.Key.onSlotContentChanged -= kv.Value; } catch {}
                }
                info.ChildSlotHandlers.Clear();
            }
        }

        private void UpdateChildItemSubscriptions(Item container)
        {
            if (container == null || !_tracked.ContainsKey(container)) return;
            var info = _tracked[container];

            HashSet<Item> currentItems = new HashSet<Item>();

            if (container.Slots != null)
            {
                foreach (var slot in container.Slots)
                {
                    if (slot == null) continue;

                    if (!info.ChildSlotHandlers.ContainsKey(slot))
                    {
                        Action<Slot> sh = (s) =>
                        {
                            UpdateChildItemSubscriptions(container);
                            if (CheckAndUpdateContainerSnapshot(container)) OnContentChanged?.Invoke();
                        };
                        info.ChildSlotHandlers[slot] = sh;
                        try { slot.onSlotContentChanged -= sh; } catch { }
                        try { slot.onSlotContentChanged += sh; } catch { }
                    }

                    if (slot.Content == null) continue;
                    var it = slot.Content;
                    if (!currentItems.Contains(it))
                    {
                        currentItems.Add(it);
                        if (_config == null || !_config.ItemIdToBuffIdsMap.ContainsKey(it.TypeID)) continue;
                        if (!info.ChildItemHandlers.ContainsKey(it))
                        {
                            Action<Item> handler = (changed) => OnChildItemTreeChanged(container, it, changed);
                            info.ChildItemHandlers[it] = handler;
                            try { it.onItemTreeChanged -= handler; } catch { }
                            try { it.onItemTreeChanged += handler; } catch { }
                            try { it.onSetStackCount -= handler; } catch { }
                            try { it.onSetStackCount += handler; } catch { }
                        }
                    }
                }
            }

            var toRemove = info.ChildItemHandlers.Keys.Where(i => !currentItems.Contains(i)).ToList();
            foreach (var i in toRemove)
            {
                try { i.onItemTreeChanged -= info.ChildItemHandlers[i]; } catch {}
                try { i.onSetStackCount -= info.ChildItemHandlers[i]; } catch {}
                info.ChildItemHandlers.Remove(i);
            }
        }

        private void OnChildItemTreeChanged(Item container, Item child, Item changed)
        {
            if (container == null || child == null) return;

            if (CheckAndUpdateContainerSnapshot(container))
            {
                OnContentChanged?.Invoke();
            }
        }
    }
}
