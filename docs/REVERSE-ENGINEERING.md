# 逆向参考：整套反编译怎么来、怎么用、别踩什么

2026-11-21 起，我们不再"卡一次 dump 一个方法"，而是**反编译整个游戏程序集**当参考书查。

## 这份参考在哪（不在 git 仓库里）

```
H:\deepseek-harness\workspace\shared\refs\oni-U59-744825\
  src\             Assembly-CSharp.dll 反编译结果（4497 个 .cs，25.1 MB）
  src-firstpass\   Assembly-CSharp-firstpass.dll（3558 个 .cs）
```

- 目录名里的 `U59-744825` 是**游戏构建号**（主菜单角落显示 `U59-744825-S`）：游戏一更新就作废，要重新生成
- 生成命令（`ilspycmd` 装成全局工具）：

```powershell
dotnet tool install --global ilspycmd --version 7.2.1.6856   # 最新版要 .NET 8，本机是 6.0.418
ilspycmd -p -o <目标目录> "<游戏>\OxygenNotIncluded_Data\Managed\Assembly-CSharp.dll"
ilspycmd -p -o <目标目录> "<游戏>\OxygenNotIncluded_Data\Managed\Assembly-CSharp-firstpass.dll"
```

- 用时参考：主程序集约 **85 秒**、firstpass 约 **31 秒**

## 纪律（必须遵守）

1. **不提交、不打包、不随发布物走。** 它是 Klei 的代码，`.gitignore` 之外也不要往仓库目录里放。
2. **只读参考，不抄写。** 我们的实现必须是"按 API 形状自己写"，不能把反编译片段搬进 `src/`。
3. **版本绑定。** 参考文件头的构建号与游戏版本必须对得上；升级游戏后重新反编译，别拿旧参考改新版本。
4. **反编译 ≠ 源码。** 局部变量名是推断的、注释全丢、`async`/迭代器会展开成状态机；遇到看不懂的先信 IL 工具（`shared/tmp/ilscan`）。

## 怎么用（比 IL 快得多的查法）

```powershell
$ref = 'H:\deepseek-harness\workspace\shared\refs\oni-U59-744825\src'
Select-String -Path "$ref\Constructable.cs" -Pattern 'OnCancel' -Context 0,20
Select-String -Path "$ref\*.cs" -Pattern 'MoveToSafety' | Select-Object -First 10
```

查游戏内中文名对应哪个类：先搜 `STRINGS`（例如冰息萝卜 = `Coldbreather`）；
数值平衡在 `TUNING`；哈希常量在 `GameHashes` / `SimHashes` / `SimMessageHashes`。

## 第一次用它纠正了什么（值得记住的教训）

| 我们原本的认知 | 反编译后的事实 |
|---|---|
| `Constructable.OnCancel` 会销毁蓝图 | 它只做三件事：关详情窗、`ClearMaterialNeeds()`、`ClearPendingUproots()`；蓝图消失是**后续链条**的效果（实测确实会消失，但机制不是"Cancel 里销毁"） |
| door 的 instant 写 `controlState` + `ApplyControlState()` 就够 | 游戏自己的规范路径是 **`ApplyRequestedControlState()`**：内部做 `controlState = requestedState; ApplyControlState(); Open(); Close();` —— 少了 `Open()/Close()` 门可能只改了状态、动画/物理没跟上。0.116.1 已改走规范路径 |
| 我们的 `cancel` 返回 `cleared=true` 就代表清干净了 | **错**：它只查 `Constructable`，于是"已建成的建筑"被算成 `buildingStillThere=false` ⇒ 谎报 `cleared=true`。实测 `(122,209)` 的 `PlasticTile` 取消了却还在。0.116.1 已拆成 `blueprint` / `buildingStillThere` / `nothingToCancel` 三个字段 |

**结论**：以后凡是"我们靠行为猜出来的机制"，一律先到这份参考里核一遍，再写进文档。

## 已用它查清的三件事（详见 [GAME-MECHANICS.md](GAME-MECHANICS.md)）

1. **作业范围**：`OffsetGroups.Standard = InitGrid(-2,2,-3,3)` ⇒ 5×7（我们实测的 5×6 是它的子集）
2. **`MoveToSafety`**：是 `SafeCellMonitor` 状态机 + `SafeCellSensor` 驱动的 chore，**不是"干完活回撤"**
3. **取消待办**：真正删对象的是 `Cancellable.OnCancel → DeleteObject()`；`Constructable.OnCancel` 只清材料需求
