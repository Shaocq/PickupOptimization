using System;
using System.Reflection;
using Duckov;
using Duckov.UI;
using HarmonyLib;
using ItemStatsSystem;
using UnityEngine;
using UnityEngine.EventSystems;

namespace PickupOptimization
{
    /// <summary>
    /// 兜底接管：直接调用 ItemUtilities.SendToPlayerCharacterInventory 的拾取路径。
    ///
    /// 典型调用方是「快速自动拾取」这类 mod：它们自己筛选物品，然后绕开
    /// CharacterItemControl.PickupItem / LootView.OnLootTargetItemDoubleClicked，
    /// 直接把物品交给原版落位（只查主背包顶层）。
    ///
    /// 这里只在「物品此刻位于外部搜刮容器」时接管：
    ///   - 商店购买 / 任务奖励等新建物品：InInventory 为空 → 原版；
    ///   - 拆分归位 / 玩家背包内部移动：来源属于玩家 → 原版；
    ///   - 搜刮箱、尸体、地面容器里取出的物品：走本 mod 的多容器落位。
    /// </summary>
    [HarmonyPatch(typeof(ItemUtilities), "SendToPlayerCharacterInventory")]
    internal static class ItemUtilitiesSendToPlayerCharacterInventoryPatch
    {
        private static bool Prefix(Item item, bool dontMerge, ref bool __result)
        {
            try
            {
                if (item == null)
                    return true;

                Inventory source = item.InInventory;
                if (source == null)
                    return true;

                // 玩家自己背包/容器里的移动仍交给原版，避免影响拆分、整理等操作
                if (PickupUtility.IsPlayerInventory(source))
                    return true;

                if (source.Loading)
                    return true;

                __result = PickupUtility.SendToPlayerInventory(item, dontMerge);
                return false;
            }
            catch (Exception e)
            {
                // 出错就交回原版，保证物品不会因为本 mod 异常而卡住
                SafeGuard.Report("接管外部容器取物的落位时发生异常", e);
                return true;
            }
        }
    }

    /// <summary>
    /// 地面物品拾取。
    ///
    /// 原版 CharacterItemControl.PickupItem：装备空槽 → 主背包 → 失败扔在脚下。
    /// 这里保留「装备空槽优先」，把「主背包」换成扩展后的落位逻辑：
    /// 主背包内的容器、宠物背包、宠物背包内的容器都会参与。
    ///
    /// 非玩家角色（AI / 宠物）仍走原版，避免改变它们的行为。
    /// </summary>
    [HarmonyPatch(typeof(CharacterItemControl), "PickupItem")]
    internal static class CharacterItemControlPickupPatch
    {
        private static bool Prefix(CharacterItemControl __instance, Item item, ref bool __result)
        {
            try
            {
                CharacterMainControl character = __instance != null ? __instance.characterMainControl : null;
                if (character == null || !character.IsMainCharacter)
                    return true;

                if (item == null)
                {
                    __result = false;
                    return false;
                }

                Inventory inventory = character.CharacterItem != null ? character.CharacterItem.Inventory : null;
                if (inventory != null)
                {
                    item.AgentUtilities.ReleaseActiveAgent();
                    item.Detach();

                    // 与原版一致：先尝试角色身上的空装备槽（武器 / 护甲 / 子弹等）
                    if (character.CharacterItem.TryPlug(item, emptyOnly: true))
                    {
                        __result = true;
                        return false;
                    }

                    if (PickupUtility.SendToPlayerInventory(item))
                    {
                        __result = true;
                        return false;
                    }
                }

                // 所有背包 / 容器都满了（或角色没有背包）：与原版一样把物品扔在脚下
                item.Drop(__instance.transform.position, createRigidbody: true, Vector3.forward, 360f);
                __result = false;
                return false;
            }
            catch (Exception e)
            {
                // 交给原版兜底：物品至少不会因为本 mod 出错而悬空
                SafeGuard.Report("拾取（地面物品）时发生异常", e);
                return true;
            }
        }
    }

    /// <summary>
    /// 容器 / 尸体搜刮界面里的双击拾取（悬停 + 交互键走的是同一个方法）。
    ///
    /// 原版 OnLootTargetItemDoubleClicked：装备空槽 → 主背包 → 失败静默。
    /// 这里保留「装备空槽优先」，把「主背包」换成扩展后的落位逻辑。
    /// 没有可用位置时返回 true，让原版照常走它自己的失败流程。
    /// </summary>
    [HarmonyPatch(typeof(LootView), "OnLootTargetItemDoubleClicked")]
    internal static class LootViewPickupPatch
    {
        private static readonly FieldInfo TargetLootBoxField =
            AccessTools.Field(typeof(LootView), "targetLootBox");

        private static readonly MethodInfo RefreshDetailsMethod =
            AccessTools.Method(typeof(LootView), "RefreshDetails");

        private static bool Prefix(LootView __instance, InventoryEntry entry, PointerEventData data)
        {
            try
            {
                Item item = entry != null ? entry.Item : null;
                if (item == null || item.IsInPlayerCharacter())
                    return true;

                // 未检视的物品原版也不允许拾取（双击只会消费掉点击事件）
                InteractableLootbox lootBox = TargetLootBoxField?.GetValue(__instance) as InteractableLootbox;
                if (lootBox != null && lootBox.needInspect && !item.Inspected)
                    return true;

                data?.Use();

                CharacterMainControl mainCharacter = LevelManager.Instance?.MainCharacter;
                Item characterItem = mainCharacter != null ? mainCharacter.CharacterItem : null;
                if (characterItem == null)
                    return true;

                // 与原版一致：先尝试角色身上的空装备槽
                bool moved = characterItem.TryPlug(item, emptyOnly: true)
                             || PickupUtility.SendToPlayerInventory(item);

                if (!moved)
                    return true;

                AudioManager.PlayPutItemSFX(item);
                try
                {
                    RefreshDetailsMethod?.Invoke(__instance, null);
                }
                catch (Exception e)
                {
                    // 详情面板刷新失败不影响物品已经放进背包
                    SafeGuard.Report("刷新物品详情失败", e);
                }

                return false;
            }
            catch (Exception e)
            {
                // 交给原版兜底
                SafeGuard.Report("拾取（容器双击）时发生异常", e);
                return true;
            }
        }
    }
}
