using UnityEngine;
using UnityEngine.UI;

// ⚠️ 不要 using System 之后直接用 Action/DateTime：Assembly-CSharp 里的全局 Action 枚举会遮蔽 System.Action。
namespace OniAiBridge
{
    /// <summary>
    /// 极简勾选框 —— **照抄 PLib 的 `PCheckBox` 做法**（PLib 本身也不是克隆官方 prefab，而是自己造对象）。
    ///
    /// M4.2g 修正（实机探针结论）：
    ///   * 位置其实一直是对的（`worldPos` 落在 dialog 可见区域内、anchors/pivot 都对），
    ///     真正的问题是**看不见** —— 之前把 state 的 `color` 照抄成 PLib 的 `Colors.Transparent`，
    ///     而 PLib 之所以敢透明，是因为它的勾选图自带颜色；我们的主 Image 也透明就什么都渲染不出来。
    ///   ⇒ 现在：state 的 `color`/`color_on_hover` 一律 **`Color.white`**，`use_color_on_hover = false`；
    ///     创建后再**强制**给主 Image 与勾图各赋一次不透明色（防某些 prefab 初始化把 alpha 归零）。
    ///
    /// 其它已核实的依据：
    ///  * 精灵是 ONI 自己的：`Assets.GetSprite("overview_jobs_skill_box")`（外框）、`"overview_jobs_icon_checkmark"`（对勾）、
    ///    `"overview_jobs_icon_mixed"`（半选）。`HashedString` 有 `op_Implicit(string)`。
    ///  * `MultiToggle.ChangeState`：`toggle_image.sprite/color = states[i].sprite/color`，再套 `additional_display_settings`。
    ///  * `MultiToggle.OnPointerClick`：**先 `NextState()` 再 `onClick.Invoke()`** ⇒ 回调里只读 `CurrentState`。
    ///  * 尺寸：PLib 用 ×4f（IL 里 0x40800000 = 4.0f），照抄。
    /// </summary>
    internal static class PCheckboxLite
    {
        public const int Unchecked = 0;
        public const int Checked = 1;

        const float SizeMultiplier = 4f;
        static readonly Color Hidden = new Color(0f, 0f, 0f, 0f);

        public static MultiToggle Create(Transform parent, string name, float boxSize, bool initialChecked,
                                         System.Action<bool> onChanged, System.Text.StringBuilder diag)
        {
            Sprite frameSprite = Assets.GetSprite("overview_jobs_skill_box");
            string frameSrc = "overview_jobs_skill_box";
            if (frameSprite == null)                        // 兜底：换一张肯定存在的面板图
            {
                frameSprite = Assets.GetSprite("web_box");
                frameSrc = "web_box";
            }
            Sprite checkSprite = Assets.GetSprite("overview_jobs_icon_checkmark");

            // ---- 外框（同时作为 MultiToggle.toggle_image）----
            GameObject box = NewUIObject(name, parent, out RectTransform boxRt);
            boxRt.sizeDelta = new Vector2(boxSize * SizeMultiplier, boxSize * SizeMultiplier);

            var frame = box.AddComponent<Image>();
            frame.sprite = frameSprite;
            frame.type = Image.Type.Sliced;
            frame.preserveAspect = false;
            frame.raycastTarget = true;          // 必须：要接点击
            frame.color = Color.white;           // 不透明

            // ---- 勾（per-state 由 MultiToggle 套用）----
            GameObject mark = NewUIObject("CheckMark", box.transform, out RectTransform markRt);
            markRt.sizeDelta = new Vector2(boxSize * SizeMultiplier, boxSize * SizeMultiplier);
            markRt.anchoredPosition = Vector2.zero;
            var markImg = mark.AddComponent<Image>();
            markImg.sprite = checkSprite;
            markImg.preserveAspect = true;
            markImg.raycastTarget = false;
            markImg.color = Color.white;

            // ---- 状态表：颜色一律不透明，避免"渲染了但看不见" ----
            var states = new ToggleState[2];
            states[Unchecked] = MakeState("Unchecked", frameSprite, markImg, null, Hidden);
            states[Checked] = MakeState("Checked", frameSprite, markImg, checkSprite, Color.white);

            var toggle = box.AddComponent<MultiToggle>();
            toggle.states = states;
            toggle.toggle_image = frame;
            toggle.play_sound_on_click = false;
            toggle.play_sound_on_release = false;

            // ★ 官方语义（IL 实证，见 DESIGN §10.6g）：
            //   `MultiToggle.OnPointerClick` 的顺序是 **先 onClick.Invoke()，最后才 `NextState()`**
            //   （98 字节 IL 的末尾：`ldarg.0; call NextState; ret`）。
            //   ⇒ 回调里读到的 `CurrentState` 一定是**点击之前**的值，不能等它翻；
            //   正确做法是：回调里只"算目标值并写盘"，把翻状态交给官方 `NextState()`（它在本回调返回后立刻执行），
            //   下一帧再**断言**实际状态是否等于目标（不静默绕过）。
            toggle.onClick = () =>
            {
                // ⚠️ 结论（M4.2o，实机 + patch 打点）：**官方的 `OnPointerClick` 不会推进状态** ——
                //   `OnPointerClick` Prefix/Postfix 都打了、我们的回调完整跑完（②.5/②.6/②.7 全有、无异常），
                //   但返回时 `state` 没变，而且我们 patch 的 `MultiToggle.NextState` **一次都没被调用**。
                //   所以必须自己翻；顺序按"先翻状态 → 读回真值 → 再写盘"，这样写盘写的就是界面真实状态。
                //   （`NextState` 的 patch 仍保留：万一哪天官方真的会调它，日志会告诉我们，那时再去掉手动翻。）
                try
                {
                    int statesLen = toggle.states != null ? toggle.states.Length : -1;
                    int cur = toggle.CurrentState;
                    int target = statesLen > 0 ? (cur + 1) % statesLen : cur;
                    Log.Info("[probe] toggle click: statesLen=" + statesLen + " current=" + cur + " => 目标=" + target);

                    // 照抄 PLib `PCheckBox.SetCheckState(GameObject,int)` 的语义：
                    //   `if (CURRENT_STATE.Get(toggle) != state) CHANGE_STATE.Invoke(toggle, state);`
                    // 也就是「先比较、不等才 ChangeState(int)」——不依赖官方 NextState，也不碰 stateDirty。
                    if (target != cur) toggle.ChangeState(target);
                    Log.Info("[probe] 手动翻后 CurrentState=" + toggle.CurrentState + "（期望=" + target + "）");

                    if (onChanged != null) onChanged(target == Checked);
                }
                catch (System.Exception e)
                {
                    Log.Warn("[设置页] onClick 回调异常（已捕获，避免被 Unity 事件系统静默吞掉）: " + e);
                }
            };

            // 先设初始态，再强制一次不透明（防 prefab 初始化把 alpha 归零）
            toggle.ChangeState(initialChecked ? Checked : Unchecked);
            frame.color = Color.white;
            markImg.color = initialChecked ? Color.white : Hidden;

            Log.Info("[probe] toggle created: statesLen=" + (toggle.states != null ? toggle.states.Length : -1)
                     + " currentState=" + toggle.CurrentState
                     + " initialStateIndex=" + (initialChecked ? Checked : Unchecked)
                     + " toggle_image=" + (toggle.toggle_image != null ? toggle.toggle_image.name : "null"));

            if (diag != null)
            {
                diag.Append("frame=").Append(frameSprite != null ? frameSrc : "NULL")
                    .Append(" check=").Append(checkSprite != null ? "OK" : "NULL")
                    .Append(" states=2 initial=").Append(toggle.CurrentState)
                    .Append(" frameColor=").Append(ColorStr(frame.color))
                    .Append(" markColor=").Append(ColorStr(markImg.color));
            }
            Log.Info("[probe] states: 0(unchecked)=" + ColorStr(states[Unchecked].color)
                     + " sprite=" + SpriteName(states[Unchecked].sprite)
                     + " / 1(checked)=" + ColorStr(states[Checked].color)
                     + " sprite=" + SpriteName(states[Checked].sprite));
            Log.Info("[probe] frameImage: color=" + ColorStr(frame.color) + " sprite=" + SpriteName(frame.sprite)
                     + " enabled=" + frame.enabled + " raycast=" + frame.raycastTarget
                     + " rectSize=" + boxRt.sizeDelta + " canvasGroup?=" + (box.GetComponent<CanvasGroup>() != null));
            return toggle;
        }

        static ToggleState MakeState(string name, Sprite frameSprite, Image markTarget, Sprite markSprite, Color markColor)
        {
            return new ToggleState
            {
                Name = name,
                sprite = frameSprite,
                color = Color.white,              // ← 关键修正：不再透明
                color_on_hover = Color.white,
                use_color_on_hover = false,       // 先关掉，减少变量
                use_rect_margins = false,
                rect_margins = Vector2.zero,
                on_click_override_sound_path = null,
                on_release_override_sound_path = null,
                sound_parameter_name = null,
                sound_parameter_value = 0f,
                has_sound_parameter = false,
                additional_display_settings = new[]
                {
                    new StatePresentationSetting
                    {
                        name = "CheckMark",
                        image_target = markTarget,
                        sprite = markSprite,
                        color = markColor,
                        color_on_hover = markColor,
                        use_color_on_hover = false,
                    }
                }
            };
        }

        static string ColorStr(Color c)
        {
            return "rgba(" + c.r.ToString("0.00") + "," + c.g.ToString("0.00") + ","
                 + c.b.ToString("0.00") + "," + c.a.ToString("0.00") + ")";
        }

        static string SpriteName(Sprite s)
        {
            if (s == null) return "null";
            return s.name;
        }

        static GameObject NewUIObject(string name, Transform parent, out RectTransform rt)
        {
            var go = new GameObject(name, typeof(RectTransform));
            rt = (RectTransform)go.transform;
            if (parent != null) rt.SetParent(parent, false);
            rt.localScale = Vector3.one;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer >= 0) go.layer = uiLayer;
            return go;
        }
    }
}
