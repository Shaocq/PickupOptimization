using System;
using System.Collections.Generic;
using UnityEngine;

namespace PickupOptimization
{
    /// <summary>
    /// 异常兜底：本 mod 的异常不允许抛回游戏或其他 mod 的调用栈。
    /// 同类日志 30 秒内只写一次，避免 Harmony 高频回调刷屏。
    /// </summary>
    internal static class SafeGuard
    {
        private const float LogCooldown = 30f;

        private static readonly Dictionary<string, float> LastLogTime =
            new Dictionary<string, float>();

        public static void Report(string context, Exception exception)
        {
            try
            {
                string message = $"{context}：{exception}";

                // 限流必须同时管住异常堆栈，否则高频回调里 LogException 照样会刷屏
                if (!TryEnterCooldown(message))
                    return;

                Debug.LogError($"[拾取优化] {message}");
                Debug.LogException(exception);

                Diag.Log("[错误] " + message);
            }
            catch
            {
                // 日志本身失败就只能放弃
            }
        }

        public static void Report(string message)
        {
            try
            {
                if (!TryEnterCooldown(message))
                    return;

                Debug.LogError($"[拾取优化] {message}");
                Diag.Log("[错误] " + message);
            }
            catch
            {
                // 连日志都写不了就没办法了
            }
        }

        /// <summary>同类日志在冷却时间内返回 false。</summary>
        private static bool TryEnterCooldown(string message)
        {
            float now = Time.realtimeSinceStartup;
            if (LastLogTime.TryGetValue(message, out float last) && now - last < LogCooldown)
                return false;

            LastLogTime[message] = now;
            return true;
        }
    }
}
