using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Duckov.UI;
using HarmonyLib;
using UnityEngine;

namespace PickupOptimization
{
    /// <summary>
    /// 拾取优化。
    ///
    /// 原版拾取时只搜索主背包顶层：
    ///   - 主背包顶层没有同类物品可叠加时，物品直接占一个新格子；
    ///   - 主背包顶层满了就判定失败，哪怕背包里的容器、宠物背包还有空间。
    ///
    /// 本 mod 只改动「拾取」这一条路径（地面物品 + 容器 / 尸体搜刮界面的双击与快速拾取），
    /// 商店购买、任务奖励等其它入包路径保持原版。
    ///
    /// 前置：必须启用「HarmonyLib」（工坊 3589088839 / HarmonyLoadMod）。
    /// </summary>
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private const string HarmonyId = "com.pickupoptimization";
        private const float RetryInterval = 2f;
        private const float PatchFailureBackoff = 10f;

        private static bool _patched;
        private static bool _patchAttempted;
        private static string _modDirectory = ".";

        private Coroutine _retryCoroutine;
        private bool _warnedMissingHarmony;

        /// <summary>mod 所在目录（诊断日志就放在这里）。</summary>
        public static string ModDirectory => _modDirectory;

        protected override void OnAfterSetup()
        {
            try
            {
                string directory = info.path;
                if (string.IsNullOrEmpty(directory))
                    directory = PathOfExecutingAssembly();
                _modDirectory = directory;
            }
            catch (Exception e)
            {
                SafeGuard.Report("初始化 mod 目录失败", e);
            }
        }

        private static string PathOfExecutingAssembly()
        {
            try
            {
                string location = Assembly.GetExecutingAssembly().Location;
                return string.IsNullOrEmpty(location)
                    ? "."
                    : System.IO.Path.GetDirectoryName(location) ?? ".";
            }
            catch
            {
                return ".";
            }
        }

        // Unity 生命周期消息：ModBehaviour 继承 MonoBehaviour，必须保留为实例方法
#pragma warning disable CA1822
        private void OnEnable()
        {
            try
            {
                TryPatch();

                // 其他 mod 可能在稍后才补丁，启动几秒后再检查一次冲突
                StartCoroutine(RecheckConflictsLater());
            }
            catch (Exception e)
            {
                SafeGuard.Report("mod 启用失败", e);
            }
        }

        private void OnDisable()
        {
            try
            {
                if (_retryCoroutine != null)
                {
                    StopCoroutine(_retryCoroutine);
                    _retryCoroutine = null;
                }

                _warnedMissingHarmony = false;
                Unpatch();
            }
            catch (Exception e)
            {
                SafeGuard.Report("mod 禁用时清理失败", e);
            }
        }
#pragma warning restore CA1822

        /// <summary>启动几秒后再查一次冲突，覆盖「其他 mod 晚于本 mod 打补丁」的情况。</summary>
        private IEnumerator RecheckConflictsLater()
        {
            yield return new WaitForSecondsRealtime(5f);

            try
            {
                if (_patched)
                    CheckCoexistingPatches();
            }
            catch (Exception e)
            {
                SafeGuard.Report("延迟检查补丁冲突失败", e);
            }
        }

        /// <summary>0Harmony 就绪就立刻打补丁；没就绪就开重试协程。</summary>
        private void TryPatch()
        {
            if (_patched)
                return;

            if (HarmonyLoad.Load0Harmony() != null)
            {
                ApplyPatches();
                if (_patched)
                    return;
            }

            if (_retryCoroutine == null)
                _retryCoroutine = StartCoroutine(WaitForHarmony());
        }

        /// <summary>
        /// 定时检查前置是否已经把 0Harmony 载入进程。
        /// 本方法刻意不引用 HarmonyLib 的类型：找到之后再调用 ApplyPatches，
        /// 避免程序集尚未存在时 JIT 解析失败。
        /// </summary>
        private IEnumerator WaitForHarmony()
        {
            float wait = RetryInterval;

            while (!_patched)
            {
                yield return new WaitForSecondsRealtime(wait);
                wait = RetryInterval;

                try
                {
                    if (HarmonyLoad.Load0Harmony() != null)
                    {
                        ApplyPatches();
                        if (_patched)
                            break;

                        // 补丁失败时 ApplyPatches 已经报告过原因，继续重试即可，
                        // 拉长间隔避免日志刷屏
                        wait = PatchFailureBackoff;
                    }
                    else if (!_warnedMissingHarmony)
                    {
                        _warnedMissingHarmony = true;
                        SafeGuard.Report(
                            "尚未找到 0Harmony，请启用前置「HarmonyLib」（工坊 3589088839）并置于本 mod 之前");
                    }
                }
                catch (Exception e)
                {
                    SafeGuard.Report("等待 Harmony 前置失败", e);
                }
            }

            _retryCoroutine = null;
        }

        /// <summary>引用 HarmonyLib 的代码集中在这里，必须在 0Harmony 加载后调用。</summary>
        private static void ApplyPatches()
        {
            try
            {
                // 从这一刻起可能已经动过补丁，禁用时必须尝试清理
                _patchAttempted = true;

                Harmony harmony = new Harmony(HarmonyId);

                // 先清掉上一次可能残留或只应用了一半的补丁，避免重复叠加
                harmony.UnpatchAll(HarmonyId);
                harmony.PatchAll(typeof(ModBehaviour).Assembly);

                _patched = true;

                // 检查是否有其他 mod 补丁了同一批方法，有冲突就弹游戏内提示
                CheckCoexistingPatches();
            }
            catch (Exception e)
            {
                SafeGuard.Report("应用 Harmony 补丁失败", e);
            }
        }

        /// <summary>冲突提示只弹一次。</summary>
        private static bool _conflictNoticeShown;

        /// <summary>一处冲突：哪个 mod、冲突在哪个拾取入口。</summary>
        private struct ConflictInfo
        {
            public string ModName;
            public string PatchName;
        }

        /// <summary>
        /// 检查三个拾取入口上是否还有别的 mod 的补丁。
        ///
        /// 本 mod 的补丁返回 false 时，同一方法上优先级更低的前缀不会再执行；
        /// 若别的 mod 抢在前面返回 false，本 mod 也不会执行。
        /// 检测到冲突时直接在游戏内弹出提示，不写日志。
        /// </summary>
        private static void CheckCoexistingPatches()
        {
            try
            {
                List<ConflictInfo> conflicts = new List<ConflictInfo>();

                CollectConflicts(conflicts, "地面拾取",
                    AccessTools.Method(typeof(CharacterItemControl), "PickupItem"));

                CollectConflicts(conflicts, "容器拾取",
                    AccessTools.Method(typeof(LootView), "OnLootTargetItemDoubleClicked"));

                CollectConflicts(conflicts, "快速拾取",
                    AccessTools.Method(typeof(ItemUtilities), "SendToPlayerCharacterInventory"));

                if (conflicts.Count == 0 || _conflictNoticeShown)
                    return;

                _conflictNoticeShown = true;

                HashSet<string> shown = new HashSet<string>();
                for (int i = 0; i < conflicts.Count; i++)
                {
                    ConflictInfo conflict = conflicts[i];
                    string key = conflict.ModName + "|" + conflict.PatchName;
                    if (!shown.Add(key))
                        continue;

                    // 用游戏自带的顶部通知显示，避免自建 UI
                    NotificationText.Push($"拾取优化：与 mod「{conflict.ModName}」存在冲突（{conflict.PatchName}）");
                }
            }
            catch (Exception e)
            {
                SafeGuard.Report("检查补丁冲突失败", e);
            }
        }

        private static void CollectConflicts(List<ConflictInfo> conflicts, string patchName, MethodBase method)
        {
            if (method == null)
                return;

            Patches patchInfo = Harmony.GetPatchInfo(method);
            if (patchInfo?.Owners == null)
                return;

            foreach (string owner in patchInfo.Owners)
            {
                if (string.IsNullOrEmpty(owner) || owner == HarmonyId)
                    continue;

                conflicts.Add(new ConflictInfo
                {
                    ModName = ResolveModName(owner, patchInfo),
                    PatchName = patchName
                });
            }
        }

        /// <summary>
        /// 把 Harmony 补丁来源映射成 mod 的显示名：
        /// 用补丁方法所在程序集去匹配已加载的 ModBehaviour，匹配不到就退回 Harmony ID。
        /// </summary>
        private static string ResolveModName(string harmonyId, Patches patchInfo)
        {
            try
            {
                Assembly assembly = FindPatchAssembly(harmonyId, patchInfo);
                if (assembly != null)
                {
                    Duckov.Modding.ModBehaviour[] mods =
                        Resources.FindObjectsOfTypeAll<Duckov.Modding.ModBehaviour>();

                    for (int i = 0; i < mods.Length; i++)
                    {
                        Duckov.Modding.ModBehaviour mod = mods[i];
                        if (mod == null || mod.GetType().Assembly != assembly)
                            continue;

                        string displayName = mod.info.displayName;
                        if (!string.IsNullOrEmpty(displayName))
                            return displayName;

                        string name = mod.info.name;
                        if (!string.IsNullOrEmpty(name))
                            return name;
                    }
                }
            }
            catch (Exception e)
            {
                SafeGuard.Report("解析冲突 mod 名称失败", e);
            }

            return harmonyId;
        }

        private static Assembly FindPatchAssembly(string owner, Patches patchInfo)
        {
            Assembly assembly = FindPatchAssembly(owner, patchInfo.Prefixes);
            if (assembly == null)
                assembly = FindPatchAssembly(owner, patchInfo.Postfixes);
            if (assembly == null)
                assembly = FindPatchAssembly(owner, patchInfo.Transpilers);
            if (assembly == null)
                assembly = FindPatchAssembly(owner, patchInfo.Finalizers);
            return assembly;
        }

        private static Assembly FindPatchAssembly(string owner, IList<Patch> patches)
        {
            if (patches == null)
                return null;

            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                if (patch == null || patch.owner != owner)
                    continue;

                try
                {
                    return patch.PatchMethod?.DeclaringType?.Assembly;
                }
                catch
                {
                    // 补丁方法已失效时跳过，退回 Harmony ID 显示
                }
            }

            return null;
        }

        private static void Unpatch()
        {
            if (!_patchAttempted)
                return;

            try
            {
                new Harmony(HarmonyId).UnpatchAll(HarmonyId);
                _patchAttempted = false;
            }
            catch (Exception e)
            {
                SafeGuard.Report("移除 Harmony 补丁失败", e);
            }
            finally
            {
                _patched = false;
            }
        }
    }
}
