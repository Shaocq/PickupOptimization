namespace PickupOptimization
{
    /// <summary>
    /// 诊断日志。
    ///
    /// 按用户要求已关闭写日志：不再写 &lt;mod 目录&gt;/PickupOptimization.log，
    /// Log / LogException 均为空操作，也不产生字符串拼接之外的额外开销
    /// （高频路径上的日志调用已一并从 PickupUtility 移除）。
    ///
    /// 需要排查问题时，把 Enabled 改为 true 并恢复文件写入实现即可。
    /// </summary>
    internal static class Diag
    {
        /// <summary>日志总开关。关闭时不写任何文件。</summary>
        internal const bool Enabled = false;

        public static void Log(string message)
        {
            // 日志已关闭：保持空实现，避免调用方到处加判断
        }

        public static void LogException(string context, System.Exception exception)
        {
            // 日志已关闭
        }
    }
}
