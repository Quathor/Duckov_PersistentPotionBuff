using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using Duckov.Modding;
using Duckov.Buffs;
using Duckov.Utilities;
using ItemStatsSystem;
using ItemStatsSystem.Items;
using Newtonsoft.Json;

namespace PersistentPotionBuff
{
    // 配置文件数据结构
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
        public List<string> additionalSlots = new List<string> { "Medic" };
    }

    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        // 物品ID与BuffID映射
        private Dictionary<int, int> _itemIdToBuffIdMap = new Dictionary<int, int>();
        // BuffID到Buff对象缓存
        private Dictionary<int, Buff> _buffPrefabCache = new Dictionary<int, Buff>();

        // 已激活Buff ID
        private HashSet<int> activeModBuffIDs = new HashSet<int>();
        
        // 保存Mod添加的Buff实例引用
        private Dictionary<int, Buff> activeModBuffInstances = new Dictionary<int, Buff>();

        // 已订阅的Inventory
        private HashSet<Inventory> subscribedInventories = new HashSet<Inventory>();
        // 追踪的容器列表
        private HashSet<Item> trackedContainers = new HashSet<Item>();

        // 追踪的额外槽位 (SlotName -> SlotObj)
        private Dictionary<string, ItemStatsSystem.Items.Slot> _trackedSlots = new Dictionary<string, ItemStatsSystem.Items.Slot>();

        // Debug模式
        public static bool DebugMode = false;

        // 配置
        private ConfigSettings _settings = new ConfigSettings();
        private string ConfigFilePath => Path.Combine(Application.dataPath, "..", "PersistentPotionBuff", "BuffMapping.json");

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

            // 取消额外槽位事件订阅
            foreach (var kvp in _trackedSlots)
            {
                if (kvp.Value != null)
                {
                    try { kvp.Value.onSlotContentChanged -= OnTrackedSlotChanged; } catch {}
                }
            }
            _trackedSlots.Clear();
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
            activeModBuffIDs.Clear();
            activeModBuffInstances.Clear();

            // 解除额外槽位订阅
            foreach (var kvp in _trackedSlots)
            {
                if (kvp.Value != null)
                {
                    try { kvp.Value.onSlotContentChanged -= OnTrackedSlotChanged; } catch {}
                }
            }
            _trackedSlots.Clear();
        }

        private void InitializeBuffMap()
        {
            _itemIdToBuffIdMap.Clear();
            
            // 尝试从配置文件加载（如果不存在会自动生成默认配置）
            if (LoadConfigFromFile())
            {
                Debug.Log($"[PersistentPotionBuff] 成功加载配置，共 {_itemIdToBuffIdMap.Count} 个映射");
            }
            else
            {
                // 如果加载失败（如解析错误等），使用默认配置
                Debug.LogWarning("[PersistentPotionBuff] 配置文件加载失败，使用默认配置");
                LoadDefaultConfig();
            }
        }

        private bool LoadConfigFromFile()
        {
            try
            {
                if (!File.Exists(ConfigFilePath))
                {
                    Debug.LogWarning($"[PersistentPotionBuff] 配置文件不存在: {ConfigFilePath}");
                    
                    // 尝试从DLL所在目录复制模板文件
                    if (CopyTemplateConfigFromDllDirectory())
                    {
                        Debug.Log("[PersistentPotionBuff] 已从DLL目录复制模板配置文件");
                        // 复制成功后继续读取文件
                    }
                    else
                    {
                        // 如果模板文件不存在，返回false使用内存中的默认配置
                        Debug.LogWarning("[PersistentPotionBuff] DLL目录下未找到模板文件，将使用内存中的默认配置");
                        return false;
                    }
                }

                string json = File.ReadAllText(ConfigFilePath);
                BuffMappingConfig config = JsonConvert.DeserializeObject<BuffMappingConfig>(json);

                if (config == null || config.mappings == null)
                {
                    Debug.LogError("[PersistentPotionBuff] 配置文件格式错误");
                    return false;
                }

                // 加载映射
                foreach (var entry in config.mappings)
                {
                    if (entry.buffId > 0)
                    {
                        _itemIdToBuffIdMap[entry.itemId] = entry.buffId;
                    }
                }

                // 加载设置
                if (config.settings != null)
                {
                    _settings = config.settings;
                }

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
                // 获取当前DLL的路径
                string dllPath = Assembly.GetExecutingAssembly().Location;
                string dllDirectory = Path.GetDirectoryName(dllPath);
                string templatePath = Path.Combine(dllDirectory, "BuffMapping.json");

                Debug.Log($"[PersistentPotionBuff] DLL路径: {dllPath}");
                Debug.Log($"[PersistentPotionBuff] 模板文件路径: {templatePath}");

                if (!File.Exists(templatePath))
                {
                    Debug.LogWarning($"[PersistentPotionBuff] 模板文件不存在: {templatePath}");
                    return false;
                }

                // 确保目标目录存在
                string targetDirectory = Path.GetDirectoryName(ConfigFilePath);
                if (!Directory.Exists(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                    Debug.Log($"[PersistentPotionBuff] 已创建目录: {targetDirectory}");
                }

                // 复制文件
                File.Copy(templatePath, ConfigFilePath, false);
                Debug.Log($"[PersistentPotionBuff] 已复制模板文件到: {ConfigFilePath}");
                
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
            _itemIdToBuffIdMap[0] = 1201;      // 夜视
            _itemIdToBuffIdMap[137] = 1011;    // 加速
            _itemIdToBuffIdMap[398] = 1012;    // 负重
            _itemIdToBuffIdMap[408] = 1072;    // 电抗
            _itemIdToBuffIdMap[409] = 1084;    // 痛觉抗性
            _itemIdToBuffIdMap[438] = 1092;    // 热血
            _itemIdToBuffIdMap[797] = 1013;    // 护甲
            _itemIdToBuffIdMap[798] = 1014;    // 耐力
            _itemIdToBuffIdMap[800] = 1015;    // 近战伤害
            _itemIdToBuffIdMap[872] = 1017;    // 后坐力控制
            _itemIdToBuffIdMap[875] = 1018;    // 持续治疗
            _itemIdToBuffIdMap[856] = 1113;    // 风暴保护
            _itemIdToBuffIdMap[1070] = 1074;   // 火焰抗性
            _itemIdToBuffIdMap[1071] = 1075;   // 毒抗
            _itemIdToBuffIdMap[1072] = 1076;   // 空间抗性
            _itemIdToBuffIdMap[1247] = 1019;   // 出血抗性
        }

        private void CacheBuffs()
        {
            try 
            {
                var allBuffs = Resources.FindObjectsOfTypeAll<Buff>();
                _buffPrefabCache.Clear();
                foreach (var buff in allBuffs)
                {
                    if (buff != null)
                    {
                        _buffPrefabCache[buff.ID] = buff;
                    }
                }
                Debug.Log($"[PersistentPotionBuff] 已缓存 {_buffPrefabCache.Count} 个Buff");
            }
            catch (Exception e)
            {
                Debug.LogError($"[PersistentPotionBuff] 缓存Buff失败: {e.Message}");
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
            CacheBuffs();
            if (LevelConfig.IsBaseLevel)
            {
                Log("当前为基地场景，Mod已禁用");
                // 基地场景：只移除由本mod添加的无限时长buff
                CharacterMainControl player = CharacterMainControl.Main;
                var buffManager = player != null ? player.GetBuffManager() : null;
                if (buffManager != null)
                {
                    foreach (var buffId in activeModBuffIDs.ToList())
                    {
                        var currentBuff = buffManager.Buffs.FirstOrDefault(b => b.ID == buffId);
                        if (currentBuff != null && IsBuffInfinite(currentBuff))
                        {
                            // 是无限时长的buff，移除它
                            if (activeModBuffInstances.TryGetValue(buffId, out Buff buffInstance) && 
                                buffInstance == currentBuff && 
                                TryRemoveSpecificBuff(buffManager, buffInstance))
                            {
                                Log($"基地场景移除Mod Buff实例: {buffId}");
                            }
                            else if (_buffPrefabCache.TryGetValue(buffId, out Buff buffPrefab))
                            {
                                player.RemoveBuff(buffPrefab.ID, false);
                                Log($"基地场景移除无限时长Buff: {buffId}");
                            }
                        }
                        else
                        {
                            Log($"基地场景跳过有限时长Buff: {buffId}");
                        }
                    }
                    activeModBuffIDs.Clear();
                    activeModBuffInstances.Clear();
                }
                ResetTrackingState();
                return;
            }
            CharacterMainControl.OnMainCharacterInventoryChangedEvent -= OnMainCharacterInventoryChanged;
            CharacterMainControl.OnMainCharacterInventoryChangedEvent += OnMainCharacterInventoryChanged;

            StartCoroutine(InitialCheckRoutine());
            StartCoroutine(WaitForPetInventoryAndResubscribe());
            StartCoroutine(WaitForSlotsAndSubscribe());
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
            // 订阅额外槽位变化
            yield return WaitForSlotsAndSubscribe();
        }

        // 等待并订阅额外槽位变化
        private IEnumerator WaitForSlotsAndSubscribe()
        {
            int guard = 0;
            while ((CharacterMainControl.Main == null || CharacterMainControl.Main.CharacterItem == null || CharacterMainControl.Main.CharacterItem.Slots == null) && guard < 600)
            {
                guard++;
                yield return null;
            }

            if (_settings.additionalSlots == null) yield break;

            foreach (var slotName in _settings.additionalSlots)
            {
                var slot = GetSlotByName(slotName);
                if (slot != null)
                {
                    // 如果之前已经订阅过，先取消
                    if (_trackedSlots.TryGetValue(slotName, out var oldSlot) && oldSlot != null)
                    {
                        try { oldSlot.onSlotContentChanged -= OnTrackedSlotChanged; } catch {}
                    }

                    _trackedSlots[slotName] = slot;
                    try 
                    { 
                        slot.onSlotContentChanged += OnTrackedSlotChanged; 
                        Log($"已订阅槽位变化事件: {slotName}"); 
                    } 
                    catch {}
                }
            }

            // 做一次扫描与更新
            UpdateTrackedContainers();
            yield return new WaitForEndOfFrame();
            UpdateBuffs();
        }

        // 额外槽位内容变化事件
        private void OnTrackedSlotChanged(ItemStatsSystem.Items.Slot slot)
        {
            Log($"追踪槽位变化，内容:{(slot?.Content != null ? slot.Content.TypeID.ToString() : "空")}");
            StartCoroutine(DeferredUpdate(rescan: true));
        }

        // 根据名称获取玩家的槽位
        private ItemStatsSystem.Items.Slot GetSlotByName(string slotName)
        {
            try
            {
                var main = CharacterMainControl.Main;
                if (main == null || main.CharacterItem == null) return null;
                var slots = main.CharacterItem.Slots;
                if (slots == null) return null;
                
                ItemStatsSystem.Items.Slot slot = null;
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
                    if (IsOrContainsTarget(item, _settings.targetContainerId))
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

        // --- 容器管理方法 ---

        // 获取物品上的所有 Inventory 对象
        private IEnumerable<Inventory> GetInventoriesOn(Item item)
        {
            if (item == null) yield break;
            if (item.Inventory != null) yield return item.Inventory;
            var compInv = item.GetComponent<Inventory>();
            if (compInv != null && compInv != item.Inventory) yield return compInv;
        }

        private void SubscribeToContainer(Item container)
        {
            foreach (var inv in GetInventoriesOn(container))
            {
                if (inv != null && subscribedInventories.Add(inv))
                {
                    inv.onContentChanged += OnContainerContentChanged;
                }
            }
        }

        private void UnsubscribeFromContainer(Item container)
        {
            foreach (var inv in GetInventoriesOn(container))
            {
                if (inv != null && subscribedInventories.Remove(inv))
                {
                    inv.onContentChanged -= OnContainerContentChanged;
                }
            }
        }

        private void UpdateBuffs()
        {
            // 基地场景不生效（除非配置允许）
            if (LevelConfig.IsBaseLevel && !_settings.enableInBaseLevel)
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
                
                // 统计 Inventory 中的物品
                foreach (var inv in GetInventoriesOn(container))
                {
                    foreach (var item in inv)
                    {
                        if (item != null)
                        {
                            int count = item.StackCount;
                            if (itemCounts.ContainsKey(item.TypeID))
                                itemCounts[item.TypeID] += count;
                            else
                                itemCounts[item.TypeID] = count;
                        }
                    }
                }

                // 统计 Slots 中的物品
                if (container.Slots != null)
                {
                    foreach(var slot in container.Slots)
                    {
                        if(slot != null && slot.Content != null) 
                        {
                            var item = slot.Content;
                            int count = item.StackCount;
                            if (itemCounts.ContainsKey(item.TypeID))
                                itemCounts[item.TypeID] += count;
                            else
                                itemCounts[item.TypeID] = count;
                        }
                    }
                }
            }
            // 应用或移除Buff
            foreach (var kvp in _itemIdToBuffIdMap)
            {
                int itemTypeID = kvp.Key;
                int buffId = kvp.Value;
                int count = itemCounts.ContainsKey(itemTypeID) ? itemCounts[itemTypeID] : 0;
                if (_buffPrefabCache.TryGetValue(buffId, out Buff buffPrefab))
                {
                    if (count >= _settings.requiredItemCount)
                    {
                        // 条件满足，需要添加buff
                        if (!activeModBuffIDs.Contains(buffId))
                        {
                            // 首次添加，添加buff并标记
                            player.AddBuff(buffPrefab, player);
                            activeModBuffIDs.Add(buffId);
                            
                            // 保存实例引用
                            var addedBuff = buffManager.Buffs.FirstOrDefault(b => b.ID == buffPrefab.ID);
                            if (addedBuff != null)
                            {
                                activeModBuffInstances[buffId] = addedBuff;
                            }
                            Log($"添加Mod Buff: {buffId}");
                        }
                        // 设置为无限时长
                        if (activeModBuffInstances.TryGetValue(buffId, out Buff modBuff) && modBuff != null)
                        {
                            SetBuffInfinite(modBuff);
                        }
                    }
                    else
                    {
                        // 条件不满足，需要检查是否应该移除buff
                        if (activeModBuffIDs.Contains(buffId))
                        {
                            // 首先检查当前的buff是否为无限时长（由mod添加）
                            var currentBuff = buffManager.Buffs.FirstOrDefault(b => b.ID == buffId);
                            
                            if (currentBuff != null)
                            {
                                // 判断buff是否为无限时长
                                bool isInfiniteBuff = IsBuffInfinite(currentBuff);
                                
                                if (isInfiniteBuff)
                                {
                                    // 是无限时长的buff，说明是mod添加的或还在mod控制下，可以移除
                                    if (activeModBuffInstances.TryGetValue(buffId, out Buff modBuffInstance) && modBuffInstance == currentBuff)
                                    {
                                        // 实例匹配，精确移除
                                        if (TryRemoveSpecificBuff(buffManager, modBuffInstance))
                                        {
                                            Log($"移除Mod Buff实例: {buffId}");
                                        }
                                        else
                                        {
                                            player.RemoveBuff(buffPrefab.ID, false);
                                            Log($"移除Mod Buff: {buffId}");
                                        }
                                    }
                                    else
                                    {
                                        // 是无限buff但实例不匹配，直接移除
                                        player.RemoveBuff(buffPrefab.ID, false);
                                        Log($"移除无限时长Buff: {buffId}");
                                    }
                                    activeModBuffIDs.Remove(buffId);
                                    activeModBuffInstances.Remove(buffId);
                                }
                                else
                                {
                                    // 是有限时长的buff，可能是玩家使用药剂后覆盖的，不要移除
                                    // 只清除mod的追踪记录
                                    Log($"检测到有限时长Buff({buffId})，可能是玩家使用药剂，不移除");
                                    activeModBuffIDs.Remove(buffId);
                                    activeModBuffInstances.Remove(buffId);
                                }
                            }
                            else
                            {
                                // buff已经不存在了，清除追踪记录
                                activeModBuffIDs.Remove(buffId);
                                activeModBuffInstances.Remove(buffId);
                            }
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
            int targetContainerID = _settings.targetContainerId;

            // 检查玩家背包
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

            // 检查额外槽位
            foreach (var kvp in _trackedSlots)
            {
                var slot = kvp.Value;
                if (slot != null && slot.Content != null)
                {
                    CollectItemsRecursive(slot.Content, targetContainerID, result);
                }
            }

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

            foreach (var inv in GetInventoriesOn(currentItem))
            {
                foreach (var childItem in inv)
                {
                    CollectItemsRecursive(childItem, targetID, result);
                }
            }
        }



        // 判断buff是否为无限时长
        private bool IsBuffInfinite(Buff buff)
        {
            if (buff == null) return false;
            
            try
            {
                Type buffType = typeof(Buff);
                FieldInfo limitedLifeTimeField = buffType.GetField("limitedLifeTime", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo totalLifeTimeField = buffType.GetField("totalLifeTime", BindingFlags.NonPublic | BindingFlags.Instance);
                
                // 检查是否为无限寿命
                bool isLimited = limitedLifeTimeField != null ? (bool)limitedLifeTimeField.GetValue(buff) : true;
                float totalLife = totalLifeTimeField != null ? (float)totalLifeTimeField.GetValue(buff) : 0f;
                
                // 无限寿命的buff：limitedLifeTime=false 或 totalLifeTime很大（超过99999秒）
                return !isLimited || totalLife > 99999f;
            }
            catch (Exception e)
            {
                Log($"判断buff时长失败: {e.Message}");
                return false;
            }
        }

        // 查找无限寿命的buff（mod添加的）
        private Buff FindInfiniteLifetimeBuff(IEnumerable<Buff> buffs, int buffId)
        {
            try
            {
                foreach (var buff in buffs)
                {
                    if (buff != null && buff.ID == buffId && IsBuffInfinite(buff))
                    {
                        return buff;
                    }
                }
            }
            catch (Exception e)
            {
                Log($"查找无限寿命buff失败: {e.Message}");
            }
            return null;
        }

        // 尝试移除特定的buff实例
        private bool TryRemoveSpecificBuff(CharacterBuffManager buffManager, Buff buffToRemove)
        {
            try
            {
                // 通过反射访问buff列表
                Type managerType = buffManager.GetType();
                FieldInfo buffsField = managerType.GetField("buffs", BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (buffsField == null)
                {
                    // 尝试其他可能的字段名
                    buffsField = managerType.GetField("_buffs", BindingFlags.NonPublic | BindingFlags.Instance);
                }
                
                if (buffsField != null)
                {
                    var buffsList = buffsField.GetValue(buffManager) as System.Collections.IList;
                    if (buffsList != null && buffsList.Contains(buffToRemove))
                    {
                        buffsList.Remove(buffToRemove);
                        
                        // 尝试调用buff的清理方法
                        try
                        {
                            if (buffToRemove != null)
                            {
                                // 销毁buff对象
                                UnityEngine.Object.Destroy(buffToRemove.gameObject);
                            }
                        }
                        catch { }
                        
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                Log($"移除特定buff实例失败: {e.Message}");
            }
            return false;
        }

        private void SetBuffInfinite(Buff buff)
        {
            if (buff == null) return;
            
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