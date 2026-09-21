# PitchBall 工作状态交接（2026-09-20，v1.2.1 阶段）

> 本文件供上下文压缩或新开会话时接手用。**所有数字都是本机实测**，不是估计。

## 一、版本与发布状态

| 项 | 状态 |
|---|---|
| 已发布到 GitHub | **v1.2.0**（tag + Release + 251MB exe，SHA256 `148A5597…DB9B67`） |
| 本地已完成、**尚未推送** | **v1.2.1**：提交 `44e280f`、tag `v1.2.1`、`publish/PitchBall.exe`（251.1MB，SHA256 `A9624FFD0F917A113BEFF8B7869952800A6A4C5B35DC98A7CC521F4DD5FFF4A5`）、README 已更新 |
| v1.2.1 内容 | 修复"关于面板版本号写死为 v1.0.0"（原在 `MainWindow.xaml` 里硬编码，现运行时从程序集版本生成）；`NEURAL-PITCH.md` 的 GPU/DirectML 结论标注为过时 |
| 未提交改动 | `PitchBall/App.xaml.cs`（新增 `--rmvpert` 实时链路诊断入口，已编译通过） |
| 推送阻塞原因 | 网络：Clash 已停（7897 无监听）、Steam++ 的 hosts 重定向被清，`github.com`/`api.github.com` 超时。恢复一条再推即可 |

**推送命令**（代理恢复后）：
```bash
cd "D:/音高测试工具"
T=$(GIT_TERMINAL_PROMPT=0 "/c/Program Files/GitHub CLI/gh.exe" auth token)
printf '#!/bin/sh\ncase "$1" in *sername*) echo SKPspalk;; *) echo '"'"'%s'"'"';; esac\n' "$T" > /tmp/askpass.sh && chmod +x /tmp/askpass.sh
git add -A && git commit -m "v1.2.1:..." 
GIT_TERMINAL_PROMPT=0 GIT_ASKPASS=/tmp/askpass.sh git -c credential.helper= push origin main
GIT_TERMINAL_PROMPT=0 GIT_ASKPASS=/tmp/askpass.sh git -c credential.helper= push origin v1.2.1
# Release: POST api.github.com/repos/SKPspalk/PitchBall/releases (见本轮会话里用过的 rel_v120.json 同法)
# 附件: POST uploads.github.com/.../releases/<id>/assets?name=PitchBall.exe
```

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

## 四、待办

1. **推送 v1.2.1 + 建 Release**（等网络；命令见上）。注意：v1.2.0 的 Release 里那个 exe 关于面板会显示 v1.0.0。
2. **跑一次 `--rmvpert` 验证**（已编译通过，运行被中断）：`PitchBall.exe --rmvpert <wav> 4.8 7.2 44100`，期望在 5.0~6.4s 输出 ≈520~597Hz（与离线路径一致）。
3. **实时路径的最终确认**：`--rmvpert` 只覆盖到"重采样→环形缓冲→后台推理"；真实麦克风/系统声音的端到端体验仍需用户按 10 秒验证（更新频率约 4 次/s、延迟约 0.3~0.5s）。
4. **RMVPE 的声区标注**目前统一为真声（它只输出音高+置信度）；要恢复"真声/混声/假声"需把原挂在 pYIN 候选上的判据改成频谱量移植。
5. **可选**：给设置面板的无名控件加 `AutomationProperties.AutomationId`（XAML 十几行），让 UI 自动化脚本稳定（现在只能靠"编号会漂"的索引）。
6. **R&B 素材的听感裁定**：MJ Human Nature 30–35s / 38–40s、MJ Don't Stop 20–32s（见三.B）。

## 五、环境与路径（关键事实）

- 构建：`C:\Users\Lenovo\.dotnet-sdk\dotnet.exe`（本机原本没有 SDK，是我装的；`C:\Program Files\dotnet` 只有运行时）
- 发布：`dotnet publish PitchBall/PitchBall.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish`
- 调试版（带模型、启动快，测试用）：`PitchBall/bin/Release/net8.0-windows/PitchBall.exe`
- CLI 诊断：`--selftest`（自检）、`--pyinbench <path> <algo> [--dump csv]`（生产路径，algo=Pyin/Yin/Rmvpe）、`--rmvpefile <path> [s0] [s1] [thred]`、`--rmvpert <path> [s0] [s1] [rate]`、`--pyinfile`、`--slice`
- 输出日志：`%TEMP%\pitchball_selftest.log`（所有 CLI 诊断的输出都落这里）
- Python 侧测试工具链：`D:\pb_pyin_lab\`（pYIN 复刻、A/B 脚本 `ab_rmvpe.py`、R&B 对照 `test_rnb.py`、闸门脚本 `gate_*.py`）
- 网络：YouTube/GitHub 需要代理（Clash 默认端口 7897 或 Steam++）；**Steam++ 是带证书的中间人**（curl 需 `-k`，Python 需 `REQUESTS_CA_BUNDLE=D:\pb_pyin_lab\ca_bundle.pem`）
- 用户长期约定：每次修复/发布后把 `%APPDATA%\PitchBall\settings.json` 的 `OnboardingShown` 重置为 **false**（必须先 taskkill 并等 2 秒再写，写完回读验证）——本轮已确认为 false。
