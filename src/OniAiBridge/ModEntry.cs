using System;
using HarmonyLib;
using KMod;

namespace OniAiBridge
{
    /// <summary>
    /// mod 入口。游戏会扫描本程序集里继承 KMod.UserMod2 的类（已核实该类型存在于 Assembly-CSharp.dll）。
    /// 要点：
    ///  * 整个 OnLoad 用 try/catch 包住 —— mod 绝不能把游戏启动搞崩；
    ///  * 先装 Harmony 补丁（主线程泵），再起 HTTP 服务；
    ///  * 两者的失败都是"软失败"，且会如实反映在 /v1/meta 里（pumpReady / 端口）。
    /// </summary>
    public class ModEntry : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            try
            {
                Log.Info("OnLoad 开始 stage=" + Bridge.Stage + " version=" + Bridge.ModVersion + " apiVersion=" + Bridge.ApiVersion);

                BridgeSettings settings = BridgeSettings.Load();
                Log.Info("settings: port=" + settings.Port + " allowCheats=" + settings.AllowCheats +
                         " tokenLen=" + (settings.Token == null ? 0 : settings.Token.Length) + " path=" + BridgeSettings.ConfigPath);

                Bridge.CacheProcessInfoOnce();   // 主线程缓存进程级信息（主菜单下 /v1/meta 也能答）
                MainThread.Install(harmony);
                // M4.1：设置页入口（方案 A 反射挂官方「管理」按钮 → 失败自动降级方案 B）
                try { SettingsUi.Install(harmony, this.mod); }
                catch (System.Exception ex) { Log.Warn("SettingsUi.Install 异常（设置页可能不可用）: " + ex.Message); }
                Bridge.Start(settings);

                Log.Info("OnLoad 完成（HTTP 是否起来看上面那行 listening；未监听则 /v1/meta 不可达）");
            }
            catch (Exception e)
            {
                Log.Warn("OnLoad 失败（mod 不会加载任何功能，但游戏应继续运行）", e);
            }
        }
    }
}
