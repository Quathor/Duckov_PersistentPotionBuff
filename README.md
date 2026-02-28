# Duckov_PersistentPotionBuff

逃离鸭科夫_常驻药剂buff

## 📝 配置文件位置

配置文件会复制生成在：`游戏根目录/Duckov_Data/Mods/PersistentPotionBuff/BuffMapping.json`

## 📖 配置文件格式

```json
{
  "mappings": [
    {
      "itemId": 137,"buffId": 1011
    }
  ],
  "settings": {
    "targetContainerId": 882,
    "requiredItemCount": 3,
    "enableInBaseLevel": false
    "debugMode": false 
  }
}
```

## 🔧 配置项说明

### mappings（药剂映射列表）

每个映射条目包含：

| 字段       | 类型 | 说明           | 示例     |
| ---------- | ---- | -------------- | -------- |
| `itemId` | 整数 | 游戏中的物品ID | `137`  |
| `buffId` | 整数 | 对应的Buff ID  | `1011` |

### settings（全局设置）

| 字段                  | 类型   | 默认值    | 说明                   |
| --------------------- | ------ | --------- | ---------------------- |
| `targetContainerId` | 整数   | `882`   | 目标容器的物品ID       |
| `requiredItemCount` | 整数   | `3`     | 触发Buff所需的药剂数量 |
| `enableInBaseLevel` | 布尔值 | `false` | 是否在基地场景启用Buff |
| `debugMode` | 布尔值 | `false` | 是否打印日志 |

## 📝 默认支持的药剂列表

| 物品ID | Buff ID | Buff名称                            | 描述       |
| ------ | ------- | ----------------------------------- | ---------- |
| 0      | 1201    | 1201_Buff_NightVision               | 夜视       |
| 137    | 1011    | 1011_Buff_AddSpeed                  | 速度增强   |
| 398    | 1012    | 1012_Buff_InjectorMaxWeight         | 负重增加   |
| 408    | 1072    | 1072_Buff_ElecResistShort           | 电击抵抗   |
| 409    | 1084    | 1084_Buff_PainResistLong            | 疼痛抵抗   |
| 438    | 1092    | 1092_Buff_Injector_HotBlood_Trigger | 热血触发   |
| 438    | 2301    | 2301_Buff_ColdResist                | 抗寒       |
| 797    | 1013    | 1013_Buff_InjectorArmor             | 护甲强化   |
| 798    | 1014    | 1014_Buff_InjectorStamina           | 耐力提升   |
| 800    | 1015    | 1015_Buff_InjectorMeleeDamage       | 近战伤害   |
| 872    | 1017    | 1017_Buff_InjectorRecoilControl     | 后坐力控制 |
| 875    | 1018    | 1018_Buff_HealForWhile              | 持续治疗   |
| 856    | 1113    | 1113_Buff_StormProtection1          | 风暴防护   |
| 1070   | 1074    | 1074_Buff_FireResistShort           | 火焰抵抗   |
| 1071   | 1075    | 1075_Buff_PoisonResistShort         | 毒素抵抗   |
| 1072   | 1076    | 1076_Buff_SpaceResistShort          | 空间抵抗   |
| 1247   | 1019    | 1019_buff_Injector_BleedResist      | 流血抵抗   |
| 1400   | 1206    | 1206_Buff_Tagilla                   | Tagilla之力|
| 1401   | 1207    | 1207_Buff_Tagilla_Basaka            | 米诺陶之力 |

## 📝 医疗扩展的药剂列表

| 物品ID  | Buff ID | Buff名称                    | 描述     |
| ------- | ------- | --------------------------- | -------- |
| 999993  | 999991  | FireRateBuff_Instance       | 怒翅3    |
| 999992  | 999992  | FireRateBuff_Instance2      | 怒翅2    |
| 999991  | 999993  | FireRateBuff3_Instance      | 怒翅1    |
| 999994  | 999994  | ElementalBuff_Instance      | 元素激发 |
| 999995  | 999995  | BloodDuckBuff_Instance      | 血鸭     |
| 999996  | 999996  | HealthBuff_Instance         | 超鸭     |
| 999997  | 999997  | StealthAssaultBuff_Instance | 隐鸭     |
| 999998  | 999998  | BulletSpeedBuff_Instance    | 机动1    |
| 999999  | 999999  | BulletSpeedBuff2_Instance   | 机动2    |
| 1000000 | 1000000 | BulletSpeedBuff3_Instance   | 机动3    |
| 1000003 | 1000001 | StaminaBuff_Instance        | 体力增强 |
| 1000006 | 1000006 | FoodWaterBuff_Instance      | 饱腹补水 |
| 1000007 | 1000007 | FlameBurstBuff_Instance     | 焰火激发 |

## 📁 项目架构更新

脚本文件移到Scripts：

- **Scripts/**: 包含所有核心脚本文件（ModEntry.cs, ContainerTracker.cs, ContainerMonitor.cs, BuffManager.cs, Config.cs）
- **ModBehaviour.cs.bak**:  ModBehaviour.cs 备份。

## 📜 开源许可

本项目采用 **GPL-3.0** 开源许可。


