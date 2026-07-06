using NetSync.Diagnostics;
using UnityEngine;

namespace NetSync.Unity {
    /// <summary>
    /// Routes core log messages into the Unity console. Debug.Log is thread-safe in
    /// Unity, so network threads may log directly — no main-thread queue needed
    /// (the 1.x LogWhenMonoUpdate workaround is obsolete).
    /// </summary>
    public sealed class UnityNetLogger : INetLogger {
        public static readonly UnityNetLogger Instance = new UnityNetLogger();

        public NetLogLevel MinLevel { get; set; } = NetLogLevel.Info;

        public void Log(NetLogLevel level, string message) {
            if (level < MinLevel) {
                return;
            }
            switch (level) {
                case NetLogLevel.Error:
                    Debug.LogError($"[NetSync] {message}");
                    break;
                case NetLogLevel.Warning:
                    Debug.LogWarning($"[NetSync] {message}");
                    break;
                default:
                    Debug.Log($"[NetSync] {message}");
                    break;
            }
        }
    }
}
