# 交接：外部实测发现（电脑控制 / 无人化验证）

> 来源：另一个 ZCode 会话（`sess_2975f60b`），通过 Windows 无障碍树 + 截图对运行中的
> PitchBall v1.2.0 做了实测。目标会话：`sess_a7575c11`（"优化 pyin 高音漏音并查 GitHub 问题"）。
> 日期：2026-09-20

---

## 0. 先更正我自己给过的错误建议

**我上一轮提出的两条建议是错的，以 `NEURAL-PITCH-实测补遗.md` 为准，不要采信 `NEURAL-PITCH.md`
第 4、5 节关于 GPU 的部分：**

1. ❌ 我建议"ONNX Runtime + DirectML，把 RTX 5060 用起来"。**你们已实测 DirectML 慢 8.6 倍**，
   CPU 路径已是 8–13 ms/秒音频（12–20 倍实时），**结论是无需 GPU**。我提出这条时不知道你们已经测过。
2. ❌ 我把 `RmvpePitchEngine.cs:103` 没有 `AppendExecutionProvider` 描述为"缺陷、显卡闲置"。
   实际上默认 CPU EP 就是经实测确认的最优选择，不是疏漏。**不要改。**

另外我误判了一点：我以为你们走的是"方案 B（直接神经估计）"偏离了文档推荐的"方案 A（Demucs 分轨）"。
实际上 **RMVPE 是人声专用模型，直接估计人声比 Demucs 分轨 + pYIN 更对症**，你们的选择更好。

**建议动作**：把 `NEURAL-PITCH.md` 的 GPU/DirectML 段落改成指向 `NEURAL-PITCH-实测补遗.md`，
避免以后有人照着错的那份做。这份交接文件本身就是给 `NEURAL-PITCH.md` 打的补丁。

---

## 1. 新发现：关于面板版本号显示异常（你们标为 NOT COVERED）

**现象**：启动 PitchBall → 点右上角 `⚙` → 面板里"关于"区显示

```
音高球 PitchBall v1.0.0
实时人声音高测量工具
右键小球切换音源,拖入音频文件离线分析
```

**实际版本是 `1.2.0`**（`PitchBall/PitchBall.csproj` 第 12 行 `<Version>1.2.0</Version>`，
HEAD 是 `faa7c87`）。

**已排除的可能**：源码里 grep 不到硬编码的 `1.0.0`——`grep -rnE "v1\.0\.0|1\.0\.0" --include="*.xaml" --include="*.cs"`
只命中 `obj/` 下的构建产物，没有任何源码命中。

**可疑点**：`obj/Debug/net8.0-windows/PitchBall.AssemblyInfo.cs` 里仍是

```csharp
[assembly: AssemblyFileVersionAttribute("1.0.0.0")]
[assembly: AssemblyInformationalVersionAttribute("1.0.0")]
[assembly: AssemblyVersionAttribute("1.0.0.0")]
```

即 **obj 里的程序集版本信息是陈旧的 1.0.0，没跟上 csproj 的 1.2.0**。注意
`App.xaml.cs:340` 用 `typeof(App).Assembly.GetName().Version`，但那是**崩溃日志**那条路径
（`版本: {...}`），不是关于面板。**关于面板的字符串来源尚未定位**，需要顺着 XAML 找那个
TextBlock 的绑定/赋值。

**复现成本极低**（不需要音频素材），建议优先查：这是"只有看界面才能发现"的典型，
所有数值测试都不会碰到它。

---

## 2. 新发现：UI 可无人化验证（直接对应你们的 TODO #2）

你们 TODO #2 是"实时路径未在用户机器上实测，需要麦克风/系统声音测试，检查 ~4 次/秒更新与延迟"。
**这件事可以自动化，不需要人坐在那儿听。**

### 2.1 无障碍树可读的表面（已实测）

- **引导状态机完全可读**：4 步，每步标题 + 要点都以 `text` 暴露；按钮
  `下一步` / `上一步` / `跳过引导` / `开始使用` 都是具名 `pressable` 元素。实际点着走完了 4 步，内容逐步变化。
- **设置面板 128 个元素全可读，且能读出当前值**：
  `音高平滑度:3`、`A4 基准频率:440 Hz`、`小球大小:130`、`虚化程度:20`、
  `显示内容` 三选一 radio、`外观` 三选一 radio（跟随系统/深色/浅色）、
  `小球窗口置顶` / `强调色跟随背景图自动变化` 复选框状态。
- **历史分析面板是 ListBox，条目可读**：实测读到 `2:21:48 · 08-21 12:35` 与 `lovecan陶喆.m4a`。
  即"拖文件分析完成"这件事可以断言，**不需要碰像素**。
- **面板开关方向可读**：`❯` 收起 / `❮` 展开。
- **悬浮小球是独立窗口**（不是主窗口子元素）：实测 `bounds=[1567,267,163,163]`，
  主窗口 `bounds=[192,192,1350,850]`。拖拽目标必须指向小球窗口。
  （`settings.json` 里 `BallX/BallY` 与窗口坐标成 1.25 倍关系 → 该机 DPI 缩放 125%。）

### 2.2 a11y 读不到的（必须走像素兜底）

- **波形/音高曲线画布**：对无障碍层不可见，只能读到它的标签文字（如 `等待音频…`）。
- **引导进度点**：视觉 4 个，a11y 只暴露 1 个 `●`。
- **悬浮小球本体**：自绘控件。

所以实时路径验证里，"当前显示什么音名"这类要看小球本体的，需要截图；而设置项与历史记录
可以纯 a11y。

### 2.3 两个会让脚本变脆的坑（实测）

1. **AXPress 不是万能的**：`下一步` / `上一步` 无障碍激活成功，但 **`开始使用` 失败**——
   连续两次 AXPress 都没关掉引导，改用真实鼠标事件才成功。脚本必须对关键按钮准备
   "真实事件"兜底路径。
2. **大量控件无名字且编号会漂**：`button 15/17/18/19/32/33/37/38/62/63/80/81/122`，
   同一个控件在状态变化中从 `button 30` → `32` → `35`。**只能靠索引定位，状态一变就失效。**

**建议动作**：给设置面板那十几个自绘/无名控件加 `AutomationProperties.AutomationId`
（XAML 里几行）。这是 UI 自动化能稳定跑起来的前提，不加就只能写很脆的脚本。

### 2.4 可断言的持久化字段（`%APPDATA%\PitchBall\settings.json`，共 27 项）

实测确认存在的字段名（写脚本做"UI 改动是否真的落盘"断言时用）：

```
PitchAlgorithm      'Pyin'          ← 算法选择
VocalProfile        'Balanced'      ← 人声场景
Smoothing           3               ← 音高平滑度
A4Frequency         440
BallDisplayMode     'NoteFreqCents'
BallColorMode       'PitchClass'
BallSize            130.4
BallTopmost         true
AskOnAnalyze        true
OnboardingShown     true
SidebarCollapsed    true
LastViewMode        'Main'
BallX / BallY / MainW / MainH / Theme / ...
```

这组字段正是"接缝测试"的理想靶子。**先例**：changelog 里那条 `yinTrust` 离线已改 0.7、
实时没同步导致的漏音差异，就属于这类"选的东西没真的传到引擎/没落盘"的 bug，
数值测试抓不到（数值测试直接调 `FileAnalyzer`，不经过 UI），但这条路能抓。

---

## 3. 状态改动披露

- 我走完了新手引导，`OnboardingShown` 已由 `false` 变为 **`true`**（已在 settings.json 确认）。
  即用户的引导被消耗掉了。重看方式：设置里的 `查看新手引导`，或按 AGENTS.md 重置该字段。
- 未修改任何源文件、未触发分析、未改动任何设置值。
- 实测期间 PitchBall 以 pid 19964 运行（来自 `publish/PitchBall.exe`）。

---

## 4. 对"失败下载待重试"这条待办的补充（网络环境实测）

你们已知 YouTube 需要代理 `http://127.0.0.1:7897`。本机其他线路实测：

- **GitHub `git clone` / API / raw：畅通**（本次拉了 3 个仓库无异常）
- **GitHub releases 二进制下载：时通时不通**——同一天里 ffmpeg 的百兆包从 GitHub releases
  下载成功，ImageMagick 的安装包却报 `InternetOpenUrl() failed 0x80072efd`（连接失败）
- **PyPI / npm：稳定**，全程无需代理
- YouTube 直连不可达（yt-dlp 抓取超时重试 3 次后放弃）——与你们已知一致

所以"部分音轨下载失败"如果伴随超时，可能是这条线路的波动而非音源问题；
值得在重试前先用 `yt-dlp --simulate` 探一下。

---

## 5. 命令与环境（供参考）

- PitchBall exe：`D:\音高测试工具\publish\PitchBall.exe`（本次实测所用）
- 你们已确认的 bench 路径：`PitchBall.exe --pyinbench <path> <algo> --dump <csv>`
- 相关 CLI：`--rmvpefile <path> [startSec] [endSec] [thred]`、`--pyinfile`、`--dumpmenu`
- 环境：RTX 5060 Laptop 8GB / i9-14900HX / 15.7GB RAM；C 盘剩 176GB、D 盘剩 159GB
