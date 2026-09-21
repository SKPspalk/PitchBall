# PitchBall 工作状态交接（2026-09-20，v1.2.1 阶段）

> 本文件供上下文压缩或新开会话时接手用。**所有数字都是本机实测**，不是估计。

## 一、版本与发布状态

| 项 | 状态 |
|---|---|
| 已发布到 GitHub | **v1.2.1**（2026-09-21 推送完成）：`main` = `bcc811c`、tag `v1.2.1` → `bcc811c`、Release + `PitchBall.exe` **263,282,334 字节**，SHA256 `00736661787818A5…9D55`（GitHub 侧 assets.digest 已回读核对一致，已自动成为 Latest） |
| v1.2.0（上一版） | tag + Release + exe，SHA256 `148A5597…DB9B67`；注意它的关于面板会显示 v1.0.0 |
| v1.2.1 内容 | ① **修复实时 RMVPE 卡死 + 重采样失效**（详见下节）；② 修复关于面板版本号写死 v1.0.0（改为运行时取程序集版本）；③ 新增 `--rmvpert` 实时链路诊断；④ `NEURAL-PITCH.md` 的 GPU/DirectML 结论标注为过时 |
| tag 重指说明 | v1.2.1 原指向 `44e280f`（实时修复之前），推送前已重指到 **`bcc811c`**（含 `436f238` 实时修复 + README 修正），否则 checkout v1.2.1 会拿到有 bug 的源码 |
| 工作区 | 干净（`App.xaml.cs` 的 `--rmvpert` 入口已随 `436f238` 提交） |
| 推送方式（下次可复用） | `gh auth token` 生成 `/tmp/askpass.sh`，配合 `HTTPS_PROXY=http://127.0.0.1:7897`；Release 用 `gh release create <tag> publish/PitchBall.exe --notes-file …`（251MB 走热点约 1 分 45 秒） |

**推送命令**：已于 2026-09-21 执行完毕（`main` 与 `v1.2.1` 均已推送，Release 资产摘要已核对）。可复用片段见上表最后一行。

## 一之三、README / Release 说明的“受众”修正（2026-09-21）

README 与 Release 说明是给 GitHub 上的**其他用户**看的，不该出现只对开发者有意义的内部引用。本轮清理：

- README 去掉：用户素材时间点（`41:10-41:12`）、`用户指认`、`131s/1200s 处 E5/F5`、`5.11s 实时 520.6Hz` 里的测试片段偏移；去掉只影响开发机本机的条目（“新手引导状态重置为未读”“NEURAL-PITCH.md 标注过时结论”——后者是仓库内部文档变更）。相应断言改为与素材无关的通用表述（如“13 首录音室/现场 R&B 素材对照：pYIN 普遍偏低 0.5~1.5 个八度”）。
- **历史 Release 说明也已同步改**（`gh release edit --notes-file`）：v1.2.0 删掉“用户指认的 41:10-41:12 段…115-263Hz…71.5 vs 60.0”；v1.1.2 删掉“131s 处 E5 被报成 E3、1200s 处的 F5”。v1.1.1 及更早无此问题。
- **顺手发现并修正**：README 的下载校验块是旧的（写成 `263,278,238 / A9624FFD…`，那是 v1.2.0 的数字）。已改为实际发布产物 `263,282,334 / 00736661…9D55`，并与 GitHub Release 的 `assets.digest` 回读核对一致。**规则：发版后必须用本地 exe 重算 `certutil -hashfile … SHA256` 再写 README。**
- 仍未处理（待用户决定）：`STATUS.md` / `HANDOFF-CUA.md` / `NEURAL-PITCH*.md` 属内部文档且**公开在仓库里**，是否保留、移入私有仓库或加 `.gitignore`。

## 一之二、v1.2.1 修掉的两个实时 bug（自动化测试抓到的）

**v1.2.0（已发布的那个版本）的实时 RMVPE 是坏的**：一开就会卡死界面。两处根因：

1. **死循环**：实时封装用 NAudio `BufferedWaveProvider`（`ReadFully=true`，缺数据补零）+
   `WdlResamplingSampleProvider` 做 push 语义重采样；因为 `Read` 永远不返回 0，
   `while (Read(...) > 0)` 排空循环永不退出 → 卡住采集回调 → 界面冻结。
2. **重采样失效**：即使绕开死循环，该链路输出的音频也无法被模型识别（窗内最大置信度只有
   **0.002**，而离线同一时刻是 0.85）。

**修法**：自写 `PitchBall/Audio/SincResampler.cs`（Hann 窗 sinc 分数重采样，截止取输出奈奎斯特，
自带抗混叠；状态明确、可验证），并按"本次写入量"精确排空。
**验证**（`--rmvpert`）：实时与离线输出一致（5.11s 实时 520.6Hz / 离线 520.8Hz），
窗内置信度 0.002 → **0.966**；发布版 exe 也复测通过。

遗留的小改进：实时取"距窗尾 5 帧"的结果，快速音型（如 6.4s 处 D5 的跳进）会慢一拍；
可改成窗长 2s + 取距尾 ~15 帧，或融合置信度最高的帧。

## 二、v1.2.0 做了什么（已发布）

**接入 RMVPE 神经人声模型为第三种算法**（pYIN / 原 YIN / RMVPE）：

- 引擎 `PitchBall/Audio/RmvpePitchEngine.cs`：mel 前端（16kHz / 窗 1024 / 跳 160 / 128 mel HTK fmin30 fmax8000 / 居中 reflect 补零 / log-clip 1e-5 / 时间维补零到 32 的倍数）+ ONNX 推理 + 9 箱加权解码。**与参考实现逐点比对偏差 ≤0.1 音分**。
- 离线 `FileAnalyzer.AnalyzeRmvpe`：16kHz 流式、60s 分块（1s 重叠）、100fps，复用显示后处理。
- 实时 `RmvpeRealtimePitch`：后台线程每 0.25s 对最近 1s 音频推理，环形缓冲 + WDL 重采样，**不阻塞采集回调**。
- 模型 **INT8 动态量化 94MB（fp32 345MB，3.7 倍）**，精度：中位偏差 −0.4 音分、与 fp32 一致率 99.3~99.6%、定标 ≤3 音分。
  **已否决**：静态 INT8（89.5MB 但 220Hz 偏 +88 音分 ✗）。**DirectML 比 CPU 慢 8.6 倍**，用纯 CPU。
- 模型不入库（`PitchBall/Assets/rmvpe_int8.onnx` 已 gitignore），构建时存在才内嵌，缺失则该算法自动不可用。

## 三、测试结论（可直接引用）

**A. 5 个流行歌素材（Python 侧 A/B）**：RMVPE 漏音事件少 2~3 倍、中断总时长少 60~70%、有声率 90~96%（pYIN 69~88%）；CPU 实时率 0.006~0.07（比 pYIN 更省）。

**B. 13 首 R&B 经典（MJ 5 / Stevie Wonder 4 / Smokey / Al Green / Whitney / Boyz II Men，录音室+现场，42.2MB，在 `D:\pb_pyin_lab\rnb`）**：
- RMVPE 覆盖率与漏音**全面更好**（如 MJ Human Nature 录音室：47次/52.7s → **1次/0.4s**）；
- **pYIN 在这些曲子上普遍偏低 0.5~1.5 个八度**："RMVPE 报高音而 pYIN 报低音"时长 18~62s，反向仅 0~8.6s；
- 最有力例子：MJ《Human Nature》录音室 **30–35s**，旋律 B3–C4，**pYIN 报 A2/G2（低约 1.5 个八度）**，RMVPE 报 D4/C#4；
- 反例（诚实）：MJ《Don't Stop》录音室 20–32s，RMVPE 反而偏低 2–7 半音 → **需用户听感裁定**；
- 完整报告：`D:\pb_pyin_lab\rnb_report.md`。

**C. UI 层自动化（本轮跑通）**：
- **关于面板版本号已修好并验证**：无障碍树实测读到 `音高球 PitchBall v1.2.1` ✓（改前是写死的 v1.0.0）；
- **接缝测试 PASS**：设置面板把算法切到 RMVPE 后，`%APPDATA%\PitchBall\settings.json` 的 `PitchAlgorithm` 正确变为 `'Rmvpe'`（切回 pYIN 也已验证）✓ —— 说明"UI 选择 → 落盘 → 引擎"这条链路是通的；
- 工具限制（交接文件已记录，本轮再次确认）：
  - **AXPress 对 ComboBoxItem 无效**（点了不选中）→ 必须用键盘（↓/↑ + Enter）或真实鼠标事件；
  - **真实鼠标事件会被像素归属检查拒绝**（下拉是独立窗口，几何上被 ZCode 窗口覆盖）→ 键盘是可用的稳定路径；
  - a11y 树上引导/隐藏控件会残留为 `bounds=[0,0,0,0]` 的节点，索引会漂；
  - 画布（波形/音高曲线）与悬浮小球本体对 a11y 不可见，只能靠截图。

## 四、待办（2026-09-21 更新）

1. ~~推送 v1.2.1 + 建 Release~~ **已完成**（见第一节）。v1.2.0 的 Release 说明与 exe 保留原样，仅说明文字改了受众表述；那个 exe 的关于面板会显示 v1.0.0。
2. ~~跑一次 `--rmvpert` 验证~~ **已完成**：实测实时 520.6Hz vs 离线 520.8Hz ✓（并借此发现并修掉了两个实时 bug，见上）。
3. **实时路径的最终确认（需用户上手）**：`--rmvpert` 只覆盖到"重采样→环形缓冲→后台推理"；真实麦克风/系统声音的端到端体验仍需用户实测（更新频率约 4 次/s、延迟约 0.3~0.5s 是否可接受）。
4. **实时结果取帧的小改进（可自主做）**：现在取"距窗尾 5 帧"的结果，快速音型会慢一拍；可改窗长 2s + 取距尾 ~15 帧，或融合窗内置信度最高的帧。
5. **RMVPE 的声区标注**目前统一为真声（它只输出音高+置信度）；要恢复"真声/混声/假声"需把原挂在 pYIN 候选上的判据改成频谱量移植。
6. **R&B 素材的听感裁定（需用户耳朵，pYIN 优化方向的关键）**：MJ Human Nature 录音室 30–35s / 38–40s、MJ Don't Stop 录音室 20–32s（见三.B）。这是"两个算法谁对"的唯一 ground truth。
7. **可选**：给设置面板的无名控件加 `AutomationProperties.AutomationId`（XAML 十几行），让 UI 自动化脚本稳定（现在只能靠"编号会漂"的索引）。
8. **仓库内部文档是否公开**：`STATUS.md` / `HANDOFF-CUA.md` / `NEURAL-PITCH*.md` 目前都在公开仓库里（内容偏开发过程记录），待用户决定保留 / 移私有 / 加 `.gitignore`。

## 五、环境与路径（关键事实）

- 构建：`C:\Users\Lenovo\.dotnet-sdk\dotnet.exe`（本机原本没有 SDK，是我装的；`C:\Program Files\dotnet` 只有运行时）
- 发布：`dotnet publish PitchBall/PitchBall.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish`
- 调试版（带模型、启动快，测试用）：`PitchBall/bin/Release/net8.0-windows/PitchBall.exe`
- CLI 诊断：`--selftest`（自检）、`--pyinbench <path> <algo> [--dump csv]`（生产路径，algo=Pyin/Yin/Rmvpe）、`--rmvpefile <path> [s0] [s1] [thred]`、`--rmvpert <path> [s0] [s1] [rate]`、`--pyinfile`、`--slice`
- 输出日志：`%TEMP%\pitchball_selftest.log`（所有 CLI 诊断的输出都落这里）
- Python 侧测试工具链：`D:\pb_pyin_lab\`（pYIN 复刻、A/B 脚本 `ab_rmvpe.py`、R&B 对照 `test_rnb.py`、闸门脚本 `gate_*.py`）
- 网络：YouTube/GitHub 需要代理（Clash 默认端口 7897 或 Steam++）；**Steam++ 是带证书的中间人**（curl 需 `-k`，Python 需 `REQUESTS_CA_BUNDLE=D:\pb_pyin_lab\ca_bundle.pem`）
- 用户长期约定：每次修复/发布后把 `%APPDATA%\PitchBall\settings.json` 的 `OnboardingShown` 重置为 **false**（必须先 taskkill 并等 2 秒再写，写完回读验证）——本轮已确认为 false。
