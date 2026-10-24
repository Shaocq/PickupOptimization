# 拾取优化（PickupOptimization）

[![游戏](https://img.shields.io/badge/游戏-Escape%20from%20Duckov-blue)](https://store.steampowered.com/app/3167020/)
[![协议](https://img.shields.io/badge/协议-MIT-yellow)](LICENSE)
[![版本](https://img.shields.io/badge/版本-1.0.2-brightgreen)](CHANGELOG.md)

> 拾取物品时不再只往主背包里塞：按「主背包 → 主背包容器 → 宠物背包 → 宠物背包容器」的顺序存放。

| 项目 | 信息 |
| --- | --- |
| 游戏 | 逃离鸭科夫（Escape from Duckov，AppID `3167020`） |
| 框架 | `Duckov.Modding.ModBehaviour` |
| 前置 | HarmonyLib（工坊 `3589088839`），必须置于本 mod **之前**加载 |
| 创意工坊 | `3822260246` |
| 当前版本 | 1.0.2 |
| 开源协议 | [MIT](LICENSE) |

## 为什么需要它

原版拾取只认**主背包的 12 个顶层格子**。一旦主背包塞满，即使你身上背着大容量容器、宠物背包也还有空位，拾取依然失败 —— 只能手动一层层拖。

本 mod 把拾取目标扩展成一条**四级回退链**，并优先把同类物品叠到已有堆叠上，少占用新格子。

## 功能特性

| 功能 | 说明 |
| --- | --- |
| 四级回退链 | 主背包 → 主背包内的容器 → 宠物背包 → 宠物背包内的容器，逐级尝试 |
| 优先叠加 | 先搜索同 TypeID 的已有堆叠，叠满再占用空位，减少碎片格子 |
| 自动启用容器 | 主背包顶层满了会自动往容器 / 宠物背包里放，不需要先手动打开 |
| 与原版一致 | 最终落位仍走原版 `Inventory.AddItem` 路径，物品归属、存档与拾取提示保持一致 |

## 安装

1. 订阅前置 **HarmonyLib**（工坊 `3589088839`），并确保它排在本 mod **之前**加载；
2. 在创意工坊订阅「拾取优化」，或把 `PickupOptimization` 文件夹放入 `Escape from Duckov\Duckov_Data\Mods\`。

## 兼容性

- 与「快捷栏可使用宠物背包」（QuickSlotPetBackpack）、「战场自动分解」（BattlefieldAutoDecompose）有意配合：后者的分解产物走同一个拾取入口，会按本 mod 的四级链落位；
- 不修改存档结构，卸载后已拾取的物品不受影响。

## 从源码构建

```bash
dotnet build PickupOptimization.csproj -c Release
```

游戏不在默认路径时加 `-p:DuckovPath="你的游戏目录"`。

> `lib/0Harmony.dll` 不在仓库内。编译前请从创意工坊前置 HarmonyLib 目录复制一份到 `lib/`。

## 更新日志

完整版本记录见 [CHANGELOG.md](CHANGELOG.md)。

## 许可证

本项目以 [MIT 协议](LICENSE) 开源，可自由使用、修改与再发布，保留版权声明即可。

## 致谢

- 游戏与 mod 框架：TeamSoda《逃离鸭科夫》
- Harmony 补丁框架：[Harmony](https://github.com/pardeike/Harmony)（MIT）
- 感谢所有在创意工坊反馈问题与建议的玩家
