using System;
using System.Collections.Generic;
using System.Reflection;
using ItemStatsSystem;
using ItemStatsSystem.Items;
using UnityEngine;

namespace PickupOptimization
{
    /// <summary>
    /// 拾取落位逻辑。
    ///
    /// 原版 SendToPlayerCharacterInventory 只处理主背包顶层：
    ///   - 只与主背包顶层的同类物品叠加；
    ///   - 只找主背包顶层的空位；
    ///   - 主背包满了就直接失败，即使背包里的容器、宠物背包还有空间。
    ///
    /// 本类把可用的存放位置扩展为（按落位优先级从高到低）：
    ///   1. 主背包本体
    ///   2. 主背包内的容器
    ///   3. 宠物背包本体
    ///   4. 宠物背包内的容器
    ///
    /// 角色的装备槽不参与搜索：装备槽里的背包 / 收纳单元也不作为落位目标。
    ///
    /// 「容器」有两种实现，都要搜索：
    ///   - Inventory 容器（带 Inventory 组件、且带容器标签 Continer / __VSE_Container 的物品）；
    ///   - 槽位容器（带 Slots 的物品，如存储拓展的收纳单元、原版钥匙包 / 注射器收纳包）。
    ///
    /// 注意：mod 用 ItemBuilder 造出来的物品普遍带一个空的 Inventory 组件，但它们并不是容器，
    /// 游戏里也没有打开它们的入口。这类物品必须排除，否则拾取物会被吞进看不见的空间。
    ///
    /// 同类物品的叠加在所有位置中优先进行（按相同的优先级顺序扫描），
    /// 叠加不进去的物品再按上面的顺序占用空位。
    /// 全部满了返回 false，由调用方走原版失败流程。
    ///
    /// 只被拾取入口调用，因此商店购买、任务奖励等其它入包路径仍保持原版行为。
    /// </summary>
    internal static class PickupUtility
    {
        /// <summary>容器递归深度上限：主背包是 0，里面的包是 1，再套一层是 2……</summary>
        private const int MaxContainerDepth = 4;

        /// <summary>存储拓展给所有自定义物品打的容器标记（需同时有槽位才算容器）。</summary>
        private const string VseContainerTag = "__VSE_Container";

        /// <summary>
        /// 原版「容器」标签（本地化键 Tag_Continer，拼写就是少个 a）。
        /// 只有带这个标签的物品才算真正的 Inventory 容器。
        /// </summary>
        private const string VanillaContainerTag = "Continer";

        /// <summary>原版钥匙包（KeyRing）的 TypeID。</summary>
        private const int VanillaKeyRingTypeId = 836;

        /// <summary>原版注射器收纳包（InjectionBag）的 TypeID。</summary>
        private const int VanillaInjectionBagTypeId = 882;

        /// <summary>
        /// 原版 SendToPlayerCharacterInventory 成功后会触发这两个事件（UI 刷新、灯效）。
        /// 事件只能在声明类内部 Invoke，这里通过 backing field 触发，保持和原版一致。
        /// </summary>
        private static readonly FieldInfo PlayerItemOperationField = typeof(ItemUtilities).GetField(
            "OnPlayerItemOperation",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly FieldInfo ItemSentToPlayerInventoryField = typeof(ItemUtilities).GetField(
            "OnItemSentToPlayerInventory",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>一个可落位的目标：要么是背包（Inventory），要么是槽位容器（Container）。</summary>
        private sealed class PlacementTarget
        {
            /// <summary>背包目标：用 AddAt 占空位。</summary>
            public Inventory Inventory;

            /// <summary>槽位容器目标：用 Slot.Plug 占空槽。</summary>
            public Item Container;
        }

        /// <summary>一次拾取涉及的全部可存放位置，已按落位优先级排好序。</summary>
        private sealed class SearchScope
        {
            /// <summary>
            /// 按优先级排列：主背包 → 主背包容器 → 宠物背包 → 宠物背包容器。
            /// </summary>
            public readonly List<PlacementTarget> Targets = new List<PlacementTarget>();
        }

        /// <summary>
        /// 把一件刚拾取的物品放进玩家的背包 / 容器。
        /// 返回 true 表示物品已经被放下（合并或占位），false 表示玩家没有任何空位。
        /// </summary>
        public static bool SendToPlayerInventory(Item item, bool dontMerge = false)
        {
            try
            {
                if (item == null)
                    return false;

                CharacterMainControl mainCharacter = LevelManager.Instance?.MainCharacter;
                Item characterItem = mainCharacter != null ? mainCharacter.CharacterItem : null;
                Inventory mainInventory = characterItem != null ? characterItem.Inventory : null;
                if (mainInventory == null)
                    return false;

                SearchScope scope = CollectTargets(mainInventory, item);

                // 1. 叠加到所有位置上的同类物品（按优先级顺序扫描）
                if (!dontMerge && MergeIntoExisting(item, scope))
                {
                    NotifyItemSent(item);
                    return true;
                }

                // 合并可能已经把物品消耗光（Combine 内部会销毁物品）
                if (item == null || item.StackCount <= 0)
                {
                    NotifyItemSent(item);
                    return true;
                }

                // 2. 按优先级依次占用空位：
                //    主背包 → 主背包容器 → 宠物背包 → 宠物背包容器
                for (int i = 0; i < scope.Targets.Count; i++)
                {
                    PlacementTarget target = scope.Targets[i];
                    bool placed = target.Inventory != null
                        ? TryPlace(item, target.Inventory)
                        : TryPlaceInSlots(item, target.Container);

                    if (!placed)
                        continue;

                    NotifyItemSent(item);
                    return true;
                }

                return false;
            }
            catch (Exception e)
            {
                SafeGuard.Report("把拾取物品放入玩家背包时发生异常", e);
                return false;
            }
        }

        /// <summary>
        /// 判断某个背包是否属于玩家（主背包 / 主背包内容器 / 宠物背包 / 宠物背包内容器）。
        /// 用于区分「从外部搜刮容器取出的物品」和「玩家背包内部的移动」。
        /// </summary>
        internal static bool IsPlayerInventory(Inventory inventory)
        {
            if (inventory == null)
                return false;

            try
            {
                CharacterMainControl mainCharacter = LevelManager.Instance?.MainCharacter;
                Item characterItem = mainCharacter != null ? mainCharacter.CharacterItem : null;
                Inventory mainInventory = characterItem != null ? characterItem.Inventory : null;

                if (inventory == mainInventory || IsInside(inventory, mainInventory))
                    return true;

                Inventory petInventory = null;
                try
                {
                    petInventory = PetProxy.PetInventory;
                }
                catch
                {
                    // 宠物未生成时按「不是玩家背包」处理即可
                }

                return inventory == petInventory || IsInside(inventory, petInventory);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 收集玩家的所有可用存放位置，并按用户指定的优先级排序：
        /// 主背包 → 主背包容器 → 宠物背包 → 宠物背包容器。
        /// 被拾取物品自身的容器会被排除（不能把物品放进自己里面）。
        /// </summary>
        private static SearchScope CollectTargets(Inventory mainInventory, Item pickedItem)
        {
            SearchScope scope = new SearchScope();

            HashSet<Inventory> excludedInventories = new HashSet<Inventory>();
            HashSet<Item> excludedContainers = new HashSet<Item>();
            CollectExcluded(pickedItem, excludedInventories, excludedContainers, 0);

            // 第 1 优先级：主背包本体
            scope.Targets.Add(new PlacementTarget
            {
                Inventory = mainInventory
            });

            // 第 2 优先级：主背包内的所有容器（含嵌套容器）
            HashSet<Inventory> mainVisitedInventories = new HashSet<Inventory>();
            HashSet<Item> mainVisitedContainers = new HashSet<Item>();

            AddChildTargets(mainInventory, 0, scope.Targets,
                mainVisitedInventories, mainVisitedContainers, excludedInventories, excludedContainers);

            // 第 3、4 优先级：宠物背包本体 → 宠物背包容器
            // SavePet 为假的关卡不会加载 / 保存宠物背包，往里面放东西会丢，
            // 这种关卡直接不参与搜索与落位
            if (LevelConfig.SavePet)
            {
                Inventory petInventory = null;
                try
                {
                    petInventory = PetProxy.PetInventory;
                }
                catch (Exception e)
                {
                    SafeGuard.Report("读取宠物背包失败", e);
                }

                if (petInventory != null && !petInventory.Loading)
                {
                    // 主背包和它的容器都排完之后，才是宠物背包本体
                    scope.Targets.Add(new PlacementTarget
                    {
                        Inventory = petInventory
                    });

                    HashSet<Inventory> visitedInventories = new HashSet<Inventory>();
                    HashSet<Item> visitedContainers = new HashSet<Item>();

                    // 宠物背包内的所有容器（含嵌套）
                    AddChildTargets(petInventory, 0, scope.Targets,
                        visitedInventories, visitedContainers, excludedInventories, excludedContainers);
                }
            }

            return scope;
        }

        /// <summary>遍历某个背包里的物品，把其中的容器（含嵌套）按遇到顺序加入目标列表。</summary>
        private static void AddChildTargets(
            Inventory inventory,
            int depth,
            List<PlacementTarget> targets,
            HashSet<Inventory> visitedInventories,
            HashSet<Item> visitedContainers,
            HashSet<Inventory> excludedInventories,
            HashSet<Item> excludedContainers)
        {
            if (inventory == null || depth > MaxContainerDepth)
                return;

            // 正在加载的背包内容不可靠（宠物背包异步读档），不参与搜索和落位
            if (inventory.Loading)
                return;

            List<Item> content = inventory.Content;
            for (int i = 0; i < content.Count; i++)
            {
                Item child = content[i];
                if (child != null)
                    AddItemTargets(child, depth + 1, targets,
                        visitedInventories, visitedContainers, excludedInventories, excludedContainers);
            }
        }

        /// <summary>递归处理一个物品：它可能是 Inventory 容器，也可能是槽位容器，两者都要下钻。</summary>
        private static void AddItemTargets(
            Item item,
            int depth,
            List<PlacementTarget> targets,
            HashSet<Inventory> visitedInventories,
            HashSet<Item> visitedContainers,
            HashSet<Inventory> excludedInventories,
            HashSet<Item> excludedContainers)
        {
            if (item == null || depth > MaxContainerDepth)
                return;

            // 只有真正的容器才允许作为 Inventory 落位目标。
            // 不能只看 item.Inventory 是否为 null：ItemBuilder.Instantiate 会给它造的每个物品
            // 无条件挂一个 Inventory 组件（Duckov.ItemBuilders.ItemBuilder.cs），
            // 于是「装备词缀」的词缀卡之类的非容器物品也带着一个 64 格空 Inventory，
            // 而游戏没有任何 UI 能打开它 —— 物品会被吞进去、看得见重量看不见物品。
            Inventory inventory = IsInventoryContainer(item) ? item.Inventory : null;
            if (inventory != null
                && !excludedInventories.Contains(inventory)
                && !inventory.Loading
                && visitedInventories.Add(inventory))
            {
                targets.Add(new PlacementTarget
                {
                    Inventory = inventory
                });

                AddChildTargets(inventory, depth, targets,
                    visitedInventories, visitedContainers, excludedInventories, excludedContainers);
            }

            if (!IsSlotContainer(item)
                || excludedContainers.Contains(item)
                || !visitedContainers.Add(item))
            {
                return;
            }

            targets.Add(new PlacementTarget
            {
                Container = item
            });

            SlotCollection slots = item.Slots;
            foreach (Slot slot in slots)
            {
                if (slot == null)
                    continue;
                Item child = slot.Content;
                if (child != null)
                    AddItemTargets(child, depth + 1, targets,
                        visitedInventories, visitedContainers, excludedInventories, excludedContainers);
            }
        }

        /// <summary>把被拾取物品自身及其所有子容器标记为不可用目标。</summary>
        private static void CollectExcluded(
            Item item,
            HashSet<Inventory> excludedInventories,
            HashSet<Item> excludedContainers,
            int depth)
        {
            if (item == null || depth > MaxContainerDepth)
                return;

            Inventory inventory = item.Inventory;
            if (inventory != null && excludedInventories.Add(inventory))
            {
                List<Item> content = inventory.Content;
                for (int i = 0; i < content.Count; i++)
                {
                    Item child = content[i];
                    if (child != null)
                        CollectExcluded(child, excludedInventories, excludedContainers, depth + 1);
                }
            }

            SlotCollection slots = item.Slots;
            if (slots != null && slots.Count > 0 && excludedContainers.Add(item))
            {
                foreach (Slot slot in slots)
                {
                    if (slot == null)
                        continue;
                    Item child = slot.Content;
                    if (child != null)
                        CollectExcluded(child, excludedInventories, excludedContainers, depth + 1);
                }
            }
        }

        /// <summary>
        /// 在所有可用位置里找同类且还没堆满的物品，把 item 合并进去。
        /// 逐次重新查找：Combine 在物品被合并光时会 Detach 并销毁 item，
        /// 会改动原容器的列表，不能一边遍历一边合并。
        /// </summary>
        private static bool MergeIntoExisting(Item item, SearchScope scope)
        {
            if (item == null || !item.Stackable)
                return false;

            while (item != null && item.StackCount > 0)
            {
                Item mergeTarget = FindMergeTarget(item, scope);
                if (mergeTarget == null)
                    break;

                mergeTarget.Combine(item);
            }

            return item == null || item.StackCount <= 0;
        }

        /// <summary>按落位优先级顺序查找可叠加的同类物品。</summary>
        private static Item FindMergeTarget(Item item, SearchScope scope)
        {
            for (int i = 0; i < scope.Targets.Count; i++)
            {
                PlacementTarget target = scope.Targets[i];

                if (target.Inventory != null)
                {
                    List<Item> content = target.Inventory.Content;
                    for (int j = 0; j < content.Count; j++)
                    {
                        Item existing = content[j];
                        if (IsMergeCandidate(existing, item))
                            return existing;
                    }
                    continue;
                }

                SlotCollection slots = SafeSlots(target.Container);
                if (slots == null)
                    continue;

                foreach (Slot slot in slots)
                {
                    if (slot == null)
                        continue;
                    Item existing = slot.Content;
                    if (IsMergeCandidate(existing, item))
                        return existing;
                }
            }

            return null;
        }

        private static bool IsMergeCandidate(Item existing, Item item)
        {
            if (existing == null || item == null || existing == item)
                return false;
            if (existing.TypeID != item.TypeID)
                return false;
            return existing.MaxStackCount > existing.StackCount;
        }

        /// <summary>把物品放进指定背包的第一个空位。失败返回 false。</summary>
        private static bool TryPlace(Item item, Inventory inventory)
        {
            if (item == null || inventory == null)
                return false;
            if (inventory.Loading)
                return false;

            // 已经在目标容器里（理论上不会发生）：视为成功，避免重复 AddAt
            if (inventory.GetIndex(item) >= 0)
                return true;

            int position = inventory.GetFirstEmptyPosition();
            if (position < 0 || position >= inventory.Capacity)
                return false;

            item.Detach();
            return inventory.AddAt(item, position);
        }

        /// <summary>
        /// 把物品放进槽位容器的第一个可用空槽。
        /// Slot.Plug 会自行处理标签限制、同类叠加与从原背包 / 原槽位分离。
        /// </summary>
        private static bool TryPlaceInSlots(Item item, Item container)
        {
            SlotCollection slots = SafeSlots(container);
            if (slots == null || item == null)
                return false;

            foreach (Slot slot in slots)
            {
                // Master 为空说明槽位集合尚未初始化，CanPlug 会空引用，直接跳过
                if (slot == null || slot.Content != null || slot.Master == null)
                    continue;
                if (!slot.CanPlug(item))
                    continue;

                if (slot.Plug(item, out _))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 判断物品是否是「槽位容器」：
        /// 存储拓展的收纳单元（带 __VSE_Container 标记且有槽位），
        /// 或原版钥匙包 / 注射器收纳包。
        /// 武器、护甲等也有槽位，但那是配件槽，不能用来存放拾取物。
        /// </summary>
        private static bool IsSlotContainer(Item item)
        {
            if (item == null)
                return false;

            try
            {
                SlotCollection slots = item.Slots;
                if (slots == null || slots.Count <= 0)
                    return false;

                if (item.Tags != null && item.Tags.Contains(VseContainerTag))
                    return true;

                int typeId = item.TypeID;
                return typeId == VanillaKeyRingTypeId || typeId == VanillaInjectionBagTypeId;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 判断物品是否是可以用来存放拾取物的「Inventory 容器」。
        ///
        /// 不能只判断 item.Inventory 是否为 null：原版 ItemBuilder.Instantiate() 会给它造出的
        /// 每一个物品都无条件调用 CreateInventoryComponent()，于是 mod 自建的非容器物品
        /// （例如「装备词缀」的词缀卡）也挂着一个 64 格空 Inventory。这类 Inventory 游戏里
        /// 没有任何 UI 入口可以打开，物品塞进去就等于消失（重量却照算）。
        ///
        /// 因此要求物品带有容器标记：
        ///   - 原版容器标签 Continer（本地化键 Tag_Continer，原样拼写）；
        ///   - 存储拓展自定义容器标记 __VSE_Container；
        ///   - 原版钥匙包 / 注射器收纳包（它们通常走槽位路径，这里一并放行）。
        /// </summary>
        private static bool IsInventoryContainer(Item item)
        {
            if (item == null)
                return false;

            try
            {
                if (item.Inventory == null)
                    return false;

                var tags = item.Tags;
                if (tags != null
                    && (tags.Contains(VanillaContainerTag) || tags.Contains(VseContainerTag)))
                {
                    return true;
                }

                int typeId = item.TypeID;
                return typeId == VanillaKeyRingTypeId || typeId == VanillaInjectionBagTypeId;
            }
            catch
            {
                return false;
            }
        }

        private static SlotCollection SafeSlots(Item item)
        {
            try
            {
                return item != null ? item.Slots : null;
            }
            catch
            {
                return null;
            }
        }

        private static void NotifyItemSent(Item item)
        {
            try
            {
                Action operation = PlayerItemOperationField?.GetValue(null) as Action;
                operation?.Invoke();
            }
            catch (Exception e)
            {
                SafeGuard.Report("触发玩家物品操作事件失败", e);
            }

            try
            {
                Action<Item> sent = ItemSentToPlayerInventoryField?.GetValue(null) as Action<Item>;
                sent?.Invoke(item);
            }
            catch (Exception e)
            {
                SafeGuard.Report("触发物品进入背包事件失败", e);
            }
        }

        /// <summary>判断某个背包是否在 root 背包的子树内（root 自身返回 false）。</summary>
        private static bool IsInside(Inventory inventory, Inventory root)
        {
            if (inventory == null || root == null)
                return false;

            try
            {
                Item containerItem = inventory.AttachedToItem;
                int guard = 0;
                while (containerItem != null && guard++ < 8)
                {
                    Inventory parent = containerItem.InInventory;
                    if (parent == null)
                        return false;
                    if (parent == root)
                        return true;
                    containerItem = parent.AttachedToItem;
                }
            }
            catch
            {
                // 回溯失败就按「其他」处理
            }

            return false;
        }

    }
}
