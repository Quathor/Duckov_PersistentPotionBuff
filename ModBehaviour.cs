using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Duckov.Modding;
using Duckov.Buffs;
using Duckov.Utilities;
using ItemStatsSystem;
using ItemStatsSystem.Items;

namespace PersistentPotionBuff
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        // 物品ID与Buff名称映射
        private Dictionary<int, string> _itemIdToBuffNameMap = new Dictionary<int, string>();
        // Buff名称到Buff对象缓存
        private Dictionary<string, Buff> _buffPrefabCache = new Dictionary<string, Buff>();

        // 已激活Buff ID
        private HashSet<int> activeModBuffIDs = new HashSet<int>();

        // 已订阅的Inventory
        private HashSet<Inventory> subscribedInventories = new HashSet<Inventory>();
        // 追踪的容器列表
        private HashSet<Item> trackedContainers = new HashSet<Item>();

        // Debug模式
        public static bool DebugMode = false;

        private void Start()
        {
            InitializeBuffMap();
            
            // 缓存Buff
            CacheBuffs();

            // 注册关卡初始化
            LevelManager.OnLevelInitialized += OnLevelInitialized;
            
            // 若关卡已初始化则直接执行
            if (LevelManager.Instance != null && LevelManager.Instance.MainCharacter != null)
            {
                OnLevelInitialized();
            }
        }

        private void OnEnable()
        {
            LevelManager.OnLevelInitialized -= OnLevelInitialized;
            LevelManager.OnLevelInitialized += OnLevelInitialized;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDisable()
        {
            LevelManager.OnLevelInitialized -= OnLevelInitialized;
            CharacterMainControl.OnMainCharacterInventoryChangedEvent -= OnMainCharacterInventoryChanged;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ResetTrackingState();
            StartCoroutine(WaitForMainAndPetThenResubscribe());
        }

        private void ResetTrackingState()
        {
            foreach (var inv in subscribedInventories.ToList())
            {
                if (inv != null)
                    inv.onContentChanged -= OnContainerContentChanged;
            }
            subscribedInventories.Clear();
            trackedContainers.Clear();
        }

        private void InitializeBuffMap()
        {
            _itemIdToBuffNameMap.Clear();
            _itemIdToBuffNameMap[0] = "1201_Buff_NightVision";
            _itemIdToBuffNameMap[137] = "1011_Buff_AddSpeed";
            _itemIdToBuffNameMap[398] = "1012_Buff_InjectorMaxWeight";
            _itemIdToBuffNameMap[408] = "1072_Buff_ElecResistShort";
            _itemIdToBuffNameMap[409] = "1084_Buff_PainResistLong";
            _itemIdToBuffNameMap[438] = "1092_Buff_Injector_HotBlood_Trigger";
            _itemIdToBuffNameMap[797] = "1013_Buff_InjectorArmor";
            _itemIdToBuffNameMap[798] = "1014_Buff_InjectorStamina";
            _itemIdToBuffNameMap[800] = "1015_Buff_InjectorMeleeDamage";
            _itemIdToBuffNameMap[872] = "1017_Buff_InjectorRecoilControl";
            _itemIdToBuffNameMap[875] = "1018_Buff_HealForWhile";
            _itemIdToBuffNameMap[856] = "1113_Buff_StormProtection1";
            _itemIdToBuffNameMap[1070] = "1074_Buff_FireResistShort";
            _itemIdToBuffNameMap[1071] = "1075_Buff_PoisonResistShort";
            _itemIdToBuffNameMap[1072] = "1076_Buff_SpaceResistShort";
            _itemIdToBuffNameMap[1247] = "1019_buff_Injector_BleedResist";
        }

        private void CacheBuffs()
        {
            try 
            {
                var allBuffs = Resources.FindObjectsOfTypeAll<Buff>();
                _buffPrefabCache.Clear();
                foreach (var buff in allBuffs)
                {
                    if (buff != null && !string.IsNullOrEmpty(buff.name))
                    {
                        _buffPrefabCache[buff.name] = buff;
                    }
                }
                // 已缓存 buff
            }
            catch (Exception e)
            {
                // 忽略缓存错误
            }
        }

        private void OnDestroy()
        {
            LevelManager.OnLevelInitialized -= OnLevelInitialized;
            CharacterMainControl.OnMainCharacterInventoryChangedEvent -= OnMainCharacterInventoryChanged;
            
            // 清理所有订阅
            foreach (var inv in subscribedInventories)
            {
                if (inv != null)
                {
                    inv.onContentChanged -= OnContainerContentChanged;
                }
            }
            subscribedInventories.Clear();
        }

        private void OnLevelInitialized()
        {
            Log("关卡初始化事件触发");
            if (LevelConfig.IsBaseLevel)
            {
                Log("当前为基地场景，Mod已禁用");
                // 基地场景：移除所有由本mod添加的buff
                CharacterMainControl player = CharacterMainControl.Main;
                var buffManager = player != null ? player.GetBuffManager() : null;
                if (buffManager != null)
                {
                    foreach (var kvp in _itemIdToBuffNameMap)
                    {
                        string buffName = kvp.Value;
                        if (_buffPrefabCache.TryGetValue(buffName, out Buff buffPrefab))
                        {
                            if (buffManager.HasBuff(buffPrefab.ID))
                            {
                                player.RemoveBuff(buffPrefab.ID, false);
                                Log($"基地场景移除Buff:{buffName}");
                            }
                        }
                    }
                }
                ResetTrackingState();
                return;
            }
            CharacterMainControl.OnMainCharacterInventoryChangedEvent -= OnMainCharacterInventoryChanged;
            CharacterMainControl.OnMainCharacterInventoryChangedEvent += OnMainCharacterInventoryChanged;

            StartCoroutine(InitialCheckRoutine());
            StartCoroutine(WaitForPetInventoryAndResubscribe());
        }

        private IEnumerator InitialCheckRoutine()
        {
            // 初始检查
            yield return new WaitForSeconds(1.0f);
            // 执行初始容器扫描和Buff更新
            UpdateTrackedContainers();
            yield return new WaitForEndOfFrame();
            UpdateBuffs();
        }

        private IEnumerator WaitForPetInventoryAndResubscribe()
        {
            int safeCounter = 0;
            while ((PetProxy.Instance == null || PetProxy.PetInventory == null) && safeCounter < 300)
            {
                safeCounter++;
                yield return null;
            }
            UpdateTrackedContainers();
            yield return new WaitForEndOfFrame();
            UpdateBuffs();
        }

        private IEnumerator WaitForMainAndPetThenResubscribe()
        {
            int guard = 0;
            while ((LevelManager.Instance == null || CharacterMainControl.Main == null) && guard < 600)
            {
                guard++;
                yield return null;
            }
            yield return WaitForPetInventoryAndResubscribe();
            // 玩家背包事件
            CharacterMainControl.OnMainCharacterInventoryChangedEvent -= OnMainCharacterInventoryChanged;
            CharacterMainControl.OnMainCharacterInventoryChangedEvent += OnMainCharacterInventoryChanged;
        }

        // 玩家背包变化
        private void OnMainCharacterInventoryChanged(CharacterMainControl character, Inventory inventory, int index)
        {
            if (character == CharacterMainControl.Main)
            {
                Log($"玩家背包变化，索引:{index}");
                StartCoroutine(DeferredUpdate());
            }
        }

        // 检查背包变化
        private void CheckInventoryChange(Inventory inventory, int index)
        {
            bool isRelevant = false;
            if (index >= 0 && index < inventory.Capacity)
            {
                Item item = inventory[index];
                if (item == null) 
                {
                    isRelevant = true; 
                }
                else 
                {
                    if (IsOrContainsTarget(item, 882))
                    {
                        isRelevant = true;
                    }
                }
            }
            
            if (isRelevant)
            {
                // 背包变化，更新容器
                UpdateTrackedContainers();
                UpdateBuffs();
            }
        }

        // 容器物品变化
        private void OnContainerContentChanged(Inventory inventory, int index)
        {
            // 容器内容变化
            StartCoroutine(DeferredUpdate(false));
        }

        private IEnumerator DeferredUpdate(bool rescan = true)
        {
            yield return new WaitForEndOfFrame();
            if (rescan)
            {
                UpdateTrackedContainers();
            }
            UpdateBuffs();
        }

        private bool IsOrContainsTarget(Item item, int targetID)
        {
            if (item == null) return false;
            if (item.TypeID == targetID) return true;
            
            if (item.Inventory != null)
            {
                foreach(var child in item.Inventory)
                {
                    if (IsOrContainsTarget(child, targetID)) return true;
                }
            }
            return false;
        }

        private void UpdateTrackedContainers()
        {
            HashSet<Item> newContainers = GetAllTargetContainers();
            // 容器已更新
            foreach (var container in newContainers)
            {
                if (!trackedContainers.Contains(container))
                {
                    SubscribeToContainer(container);
                    // 已订阅新容器
                }
            }
            foreach (var container in trackedContainers)
            {
                if (!newContainers.Contains(container))
                {
                    UnsubscribeFromContainer(container);
                    // 已取消订阅容器
                }
            }
            trackedContainers = newContainers;
        }

        private void SubscribeToContainer(Item container)
        {
             // 1. 订阅 Item.Inventory 属性
             if (container.Inventory != null)
             {
                 SubscribeToInventory(container.Inventory, container);
             }

             // 2. 订阅 GetComponent<Inventory>()
             Inventory compInv = container.GetComponent<Inventory>();
             if (compInv != null && compInv != container.Inventory) // 避免重复订阅同一个实例
             {
                 SubscribeToInventory(compInv, container);
             }
        }

        private void SubscribeToInventory(Inventory inv, Item container)
        {
             if (inv != null && !subscribedInventories.Contains(inv))
             {
                 inv.onContentChanged += OnContainerContentChanged;
                 subscribedInventories.Add(inv);
             }
        }

        private void UnsubscribeFromContainer(Item container)
        {
             // 1. 取消订阅 Item.Inventory 属性
             if (container.Inventory != null)
             {
                 UnsubscribeFromInventory(container.Inventory, container);
             }

             // 2. 取消订阅 GetComponent<Inventory>()
             Inventory compInv = container.GetComponent<Inventory>();
             if (compInv != null && compInv != container.Inventory)
             {
                 UnsubscribeFromInventory(compInv, container);
             }
        }

        private void UnsubscribeFromInventory(Inventory inv, Item container)
        {
             if (inv != null && subscribedInventories.Contains(inv))
             {
                 inv.onContentChanged -= OnContainerContentChanged;
                 subscribedInventories.Remove(inv);
             }
        }

        private void UpdateBuffs()
        {
            // 基地场景不生效
            if (LevelConfig.IsBaseLevel)
            {
                Log("基地场景，不进行Buff更新");
                return;
            }
            // 开始更新 Buff
            CharacterMainControl player = CharacterMainControl.Main;
            if (player == null) { Log("未找到玩家"); return; }
            CharacterBuffManager buffManager = player.GetBuffManager();
            if (buffManager == null) { Log("未找到Buff管理器"); return; }
            if (trackedContainers == null) trackedContainers = new HashSet<Item>();
            // 统计所有容器物品
            Dictionary<int, int> itemCounts = new Dictionary<int, int>();
            foreach (var container in trackedContainers)
            {
                if (container == null) continue;
                List<Item> containerItems = new List<Item>();
                if (container.Inventory != null)
                {
                    foreach(var item in container.Inventory)
                        if(item != null) containerItems.Add(item);
                }
                else if (container.Slots != null)
                {
                    foreach(var slot in container.Slots)
                        if(slot != null && slot.Content != null) containerItems.Add(slot.Content);
                }
                else
                {
                    var invComp = container.GetComponent<Inventory>();
                    if (invComp != null)
                        foreach(var item in invComp)
                            if(item != null) containerItems.Add(item);
                }
                foreach (var item in containerItems)
                {
                    int count = item.StackCount;
                    if (itemCounts.ContainsKey(item.TypeID))
                        itemCounts[item.TypeID] += count;
                    else
                        itemCounts[item.TypeID] = count;
                }
            }
            // 应用或移除Buff
            foreach (var kvp in _itemIdToBuffNameMap)
            {
                int itemTypeID = kvp.Key;
                string buffName = kvp.Value;
                int count = itemCounts.ContainsKey(itemTypeID) ? itemCounts[itemTypeID] : 0;
                if (_buffPrefabCache.TryGetValue(buffName, out Buff buffPrefab))
                {
                    if (count >= 3)
                    {
                        // 应用Buff
                        player.AddBuff(buffPrefab, player);
                        SetBuffInfinite(buffManager.Buffs.FirstOrDefault(b=>b.ID==buffPrefab.ID));
                    }
                    else
                    {
                        if (buffManager.HasBuff(buffPrefab.ID))
                        {
                            // 移除Buff
                            player.RemoveBuff(buffPrefab.ID, false);
                        }
                    }
                }
            }
        }

        private void ApplyInfiniteBuff(CharacterMainControl player, CharacterBuffManager buffManager, Buff buffPrefab)
        {
            if (buffPrefab == null) return;

            // 查找是否已有Buff
            Buff activeBuff = null;
            foreach (var b in buffManager.Buffs)
            {
                if (b.ID == buffPrefab.ID)
                {
                    activeBuff = b;
                    break;
                }
            }

            if (activeBuff == null)
            {
                // 添加Buff
                player.AddBuff(buffPrefab, player);
                foreach (var b in buffManager.Buffs)
                {
                    if (b.ID == buffPrefab.ID)
                    {
                        activeBuff = b;
                        break;
                    }
                }
            }

            if (activeBuff != null)
            {
                // 设置为无限
                SetBuffInfinite(activeBuff);
            }
        }

        // 获取所有目标容器
        private HashSet<Item> GetAllTargetContainers()
        {
            HashSet<Item> result = new HashSet<Item>();
            int targetContainerID = 882;

            // 检查人物背包
            if (CharacterMainControl.Main != null && CharacterMainControl.Main.CharacterItem != null)
            {
                CollectItemsRecursive(CharacterMainControl.Main.CharacterItem, targetContainerID, result);
            }

            // 检查宠物背包
            Inventory petInventory = null;
            try { petInventory = PetProxy.PetInventory; } catch { petInventory = null; }
            if (petInventory != null)
            {
                for (int i = 0; i < petInventory.Capacity; i++)
                {
                    Item item = petInventory.GetItemAt(i);
                    CollectItemsRecursive(item, targetContainerID, result);
                }
            }

            // 可扩展：其他容器

            return result;
        }

        // 递归查找目标容器
        private void CollectItemsRecursive(Item currentItem, int targetID, HashSet<Item> result)
        {
            if (currentItem == null) return;

            if (currentItem.TypeID == targetID)
            {
                result.Add(currentItem);
            }

            // 检查Item.Inventory属性
            if (currentItem.Inventory != null)
            {
                foreach (var childItem in currentItem.Inventory)
                {
                    CollectItemsRecursive(childItem, targetID, result);
                }
            }

            // 检查组件Inventory
            Inventory compInv = currentItem.GetComponent<Inventory>();
            if (compInv != null && compInv != currentItem.Inventory)
            {
                foreach (var childItem in compInv)
                {
                    CollectItemsRecursive(childItem, targetID, result);
                }
            }
        }



        private void SetBuffInfinite(Buff buff)
        {
            // 反射修改Buff字段
            Type type = typeof(Buff);
            
            // 设置无限寿命
            FieldInfo limitedLifeTimeField = type.GetField("limitedLifeTime", BindingFlags.NonPublic | BindingFlags.Instance);
            if (limitedLifeTimeField != null)
            {
                limitedLifeTimeField.SetValue(buff, false);
            }
            
            FieldInfo totalLifeTimeField = type.GetField("totalLifeTime", BindingFlags.NonPublic | BindingFlags.Instance);
            if (totalLifeTimeField != null)
            {
                totalLifeTimeField.SetValue(buff, 999999f);
            }
        }

        private void Log(string msg)
        {
            // 日志已禁用
        }
    }
}
