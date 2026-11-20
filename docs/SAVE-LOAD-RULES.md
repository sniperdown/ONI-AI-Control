# 存档装载规则（硬规则，别只看截图）

> 起因：2026-11-20 我把存档「**首都**」认成了「**首部**」（两个档都存在，一个 cycle 4、一个 cycle 34），
> 靠截图 OCR 认名字，后面所有判断都建在错的前提上。**名字必须来自字符串，不是来自像素。**

## 一、取名：只认字符串

| 用途 | 正确来源 |
|---|---|
| 有哪些殖民地 | **文件系统目录名**：`%USERPROFILE%\Documents\Klei\OxygenNotIncluded\cloud_save_files\<steamid>\<殖民地>\` |
| 有哪些存档 | **`GET /v1/saves`** —— `data.saves[].file` 是相对路径字符串，例如 `76561198174293646/首都/首都.sav` |
| 存档周期/人数 | 读 `.sav` 头（JSON 头里有 `numberOfCycles`/`numberOfDuplicants`）；`/v1/saves` 目前不解析，返回 `null` + `colonyInfoUnavailable` |

**禁止**：用 `dsh_browser_*`/截图 + 肉眼/OCR 认档名。像素只能用来核对"点开的是不是我要的那个 UI 面板"，不能用来确定名字。

## 二、点开某个档：精确匹配，匹配不到就停

1. 先取字符串列表（上面两种来源），用 **精确相等** 找目标；
2. 找不到 ⇒ **报错停下**，不许"看起来像"就点；
3. 点到列表项后，**用字符串再核一次**（例如读 `/v1/saves` 里该殖民地路径 / 读 `.sav` 头里的 `baseName`），
   对不上就取消，不 Load；
4. 只有名字确认无误才点 `LoadButton`。

## 三、现状缺口（未解决，别假装有）

* `/v1/ui/widgets` **不返回殖民地名字**（只有 `Prefab_ColonyButton#i`），而 `i` 与视觉行号**不稳定**（每次启动都可能变）。
* ⇒ 目前"按名字装载"这条路**没有字符串桥**：能拿到名字（文件系统/`/v1/saves`），但**拿不到"名字 → 哪个按钮"的映射**。
* 因此现在只能：**先按字符串确定目标存在**，再逐个点、点完用 `.sav` 头或状态里的殖民地信息核对，**不匹配就换下一个**。
* 彻底解法（待做）：给 mod 加一个只读接口，把存档列表与 UI 按钮索引**用字符串对上**（例如
  `/v1/saves` 增加 `uiIndex`，或在 `/v1/ui/widgets` 里输出殖民地按钮的 `text`）。

## 四、写归档时同样适用

日志、memory、报告中提到存档名，一律**从字符串抄**（目录名 / `/v1/saves` 的路径），不凭记忆和截图。

---

## 五、怎么找到加载入口（IL 证据，2026-11-20 已查，别重复查）

**点"加载"按钮之后的真实调用链**：

```
按钮回调 → LoadScreen.Load()                 ← 按钮处理函数（吃字段 LoadScreen::selectedSave）
            ├─ DlcManager.IsAllContentSubscribed(selectedSave.dlcIds)
            ├─ LoadScreen.ConfirmDoAction(...)
            └─ LoadingOverlay.Load(...)      ← 下一跳
底层加载器 SaveLoader：
            ├─ SaveLoader.Load(params=1)        （两个 1 参重载）
            ├─ SaveLoader.LoadHeader(params=2)  ← 读 .sav 头（周期/人数）
            ├─ SaveLoader.LoadScene()
            └─ SaveLoader.LoadFromWorldGen()
```

**`SelectedSave` 的结构（关键）**：

```
SelectedSave { string filename;  <dlcIds>;  GameObject button; }
```

⇒ **`filename` 就是存档路径字符串**，所以"按路径加载"是可行的：填 `SelectedSave.filename`
再走 `LoadingOverlay.Load` / `SaveLoader.Load` 即可，不需要 UI 按钮索引。

**周期/人数不用走 `LoadHeader`**：`.sav` 是「16 字节二进制头 + 明文 JSON 头 + Klei 二进制体」，
自己读前 64 KB 正则抓 `numberOfCycles` / `numberOfDuplicants` / `baseName` 就行（0.103.0 已这么做，
`/v1/saves` 现在直接给 `cycle` / `dups` / `baseName` 字符串）。好处：不依赖任何游戏 API 签名。

**UI 根对象名的坑**：装载界面的类是 `LoadScreen`（`KScreen` 子类），但运行时实例名是
**`LoadScreen(Clone)`** —— `GameObject.Find("LoadScreen")` 返回 null 就是这个原因
（`SaveUi.List()` 因此拿到 0 个按钮）。查这类界面要按**前缀/类型**扫，不要用精确名 `Find`。

## 六、`Name#i` 是**路径语法**，不是 GameObject 命名

`/v1/ui/click` 的路径形如 `Prefab_ColonyButton#3/Header`，其中 **`#3` 是"同名对象序号"**
（第 4 个叫 `Prefab_ColonyButton` 的对象），**不是对象名字的一部分** —— 实测对象名就叫
`Prefab_ColonyButton`（父 `Content`）。

踩坑代价：`SaveUi.List()` 曾用 `name.LastIndexOf('#')` 解析索引 ⇒ 一个按钮都匹配不到
（`uiButtonCount=0`），查了四轮才发现。

**两层索引空间互相独立，别混**：
* 第一层 `Prefab_ColonyButton#i` —— 选哪个殖民地
* 第二层 `SaveTemplate#k/LoadButton` —— 选哪一行存档再点加载

## 七、跨坐标/尺寸/命名空间：先把两边的实际值打出来

**屏幕 3840×2160 vs `/v1/shot` 1280×720（3 倍缩放）** —— 截图上量的坐标必须 ×3 才能当屏幕坐标；
`Input.mousePosition` 还是**左下原点**，要 `y = Screen.height - y`。这层转换不写出来就必错
（实测：直接点截图坐标 → 命中 `background`，点到画面外）。

**同类"看起来对、实际错一层"已踩三次**：主线程（Unity API 返回空）、`#i`（路径语法当对象名）、
屏幕/截图缩放。共同点：**中间有一层转换，而那层没被写出来**。
⇒ 凡是跨两个坐标/尺寸/命名空间，**第一件事把两边的实际值都打出来对比**，别假设一样。

## 八、加载界面按 `savedAt` 倒序（主人确认，唯一排序依据）

要定位某一行：先按 `savedAt` 倒序排序算行号，再换算坐标。
**不要试图从对象枚举顺序推 UI 顺序** —— UI 顺序稳定，`Resources.FindObjectsOfTypeAll` 的顺序每次启动都不同。

## 九、ONI 的按钮不是 UGUI `Button`

ONI 用自己的 **`KButton` / `MultiToggle`**；`GetComponentInParent<UnityEngine.UI.Button>()` 返回 null。
点任何 ONI 界面控件都会遇到这条。

**`ExecuteEvents.Execute` 返回成功 ≠ 游戏收到了**：它走 UGUI 的 `IPointerClickHandler` 体系，
ONI 大概率不实现 ⇒ `clicked=true` 但游戏侧毫无反应（实测「首都」行点了没展开）。
正确路子是**模拟真实输入**：`KInputManager` 的输入入口，或 `PointerInputModule.ProcessMousePress`
（"假装鼠标在这里按了一下"），而不是"直接告诉控件你被点了"。

**字段语义**：`clicked` 这种乐观命名要改成 `dispatched` + `dispatchMethod`，
让人一眼看出"用了什么方式派的"——别让字段名骗了下一轮的自己。

## 十、`hitText` / `hitAncestry` 是最便宜的定位反馈

点一下就能知道"命中的对象上写着什么、沿途挂了哪些交互组件"，比截图比对快一轮。
实测：`fy=0.3569` → `hitText="1个存档 3个自动存档 3.04 MB"`（副标题，错了）；
`fy=0.345` → `hitText="首都"`（名称行，对了）。
