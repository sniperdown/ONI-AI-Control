using UnityEngine;
using UnityEngine.UI;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// 探针（M4.3c-2）：只为回答"点击到底有没有到达输入框"。
    /// 挂上它以后，若能点到 ⇒ 打日志；点不到 ⇒ 什么都不会打（那就说明是射线被挡/框没有 Graphic）。
    /// 这是个**纯诊断组件**，只有 OnPointerClick，没有 Awake/OnEnable 之类的东西要初始化。
    /// </summary>
    internal sealed class InputFieldClickProbe : MonoBehaviour, UnityEngine.EventSystems.IPointerClickHandler
    {
        public void OnPointerClick(UnityEngine.EventSystems.PointerEventData e)
        {
            try { Log.Info("[设置页] 输入框收到指针点击 ⇒ 点击能到达（若仍无焦点，则是 EventSystem/选择逻辑问题）"); }
            catch { }
        }
    }
}
