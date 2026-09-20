# PitchBall 引入神经网络能力：调研结论与接入方案

> 调研日期：2026-09-20
> 内容：音高检测引入神经网络模型的可行性分析、接入路线与验证实验方案。
> 本文件不涉及代码改动，是决策与实施参考。

## 背景与要解决的问题

PitchBall 现有音高检测是手写的 pYIN 实现（`PitchBall/Audio/PyinMonoPitch.cs`、
`PyinPitchDetector.cs`、`PyinRealtimePitch.cs`），YIN 的差分函数 + CMNDF，
另加 HMM 时间平滑、±1 半音转移窗口、候选概率/观测概率，配 `yinTrust`、
三档人声场景、锚点防跳变。

README 里已记录的已知边界：

> 在密集混音中（典型是现场演唱会），若伴奏或和声占据了与人声相同的频段，基于周期性的
> 基频检测器只能锁住"最强的周期源"——当人声不是最强时会被伴奏抢线。

**这是原理天花板，不是调参问题。** YIN/pYIN 的判据是"周期性"（找最强的周期成分）；
伴奏更强、频段重叠时必然被抢线。`yinTrust`、人声场景档位、防跳变都属于同一原理内的
工程优化，无法突破这个上限。神经网络的判据是"音色特征"（共振峰结构、谐波包络、颤音），
在大量人声数据上训练后，伴奏更响时仍能锁定人声。

## 两条纠正（避免走弯路）

1. **不要用 GPU 加速现有 pYIN。** 差分函数 + CMNDF + HMM 维特比是顺序累积运算，
   不是大规模矩阵乘法，GPU 帮不上忙；单声道逐帧计算量本来就小。
   瓶颈在算法原理，不在算力。
2. **不要把 librosa 作为生产依赖。** 现有手写实现已覆盖并超过 librosa 的水平
   （librosa 的 pyin 也是 Mauch & Dixon 2014 同一篇论文的实现）。
   唯一有价值的用途是作为**交叉验证的参考实现**：同一段音频，
   C# 输出与 librosa pyin 输出对比。

## 方案 A（推荐，改动最小）：神经分轨做前处理

**核心思路：算法一行不改，只换上游输入。** 先用神经网络把混音拆成人声轨，
再把分离后的人声轨喂给现有 pYIN 管线。

- 模型：`facebookresearch/demucs`（10.4k star，MIT，Meta 出品），
  `htdemucs` 拆人声/鼓/贝斯/其他四轨
- **这正是 Melodyne 官方建议的做法（"先做分轨"），且有免费开源实现**
- **需要 GPU**：Demucs 在 CPU 上非常慢，这是 GPU 对本项目的真正价值所在
- 优点：完全保留现有的置信度、平滑、声区判定（真声/混声/假声）逻辑积累

## 方案 B：换成神经网络做音高估计本身

- `maxrmorrison/torchcrepe`（524 star，MIT）/ `marl/crepe`（1.4k star，MIT）— CREPE
- `spotify/basic-pitch`（5.6k star，Apache-2.0）— 音频转 MIDI，支持复音
- 人声场景优先考虑 RMVPE 一类**人声专用**模型，效果优于通用 CREPE
- 代价：不再是现有 pYIN，整套置信度/平滑/声区逻辑需要重新对齐，**现有积累损失大**

## 接入路线（关键：项目是 C#/.NET 8 单文件）

项目现状约束：

- `net8.0-windows` + WPF，唯一 NuGet 依赖是 NAudio 2.2.1，零 ML 依赖
- 单文件自包含发布约 140 MB，`IncludeNativeLibrariesForSelfExtract=true`
- 卖点是"单文件便携、免安装、已自包含运行时"——**这个定位决定了路线选择**

| 路线 | 做法 | 评价 |
|---|---|---|
| **ONNX Runtime + DirectML** | 模型导出 ONNX，用 `Microsoft.ML.OnnxRuntime` + DirectML 后端在 C# 内推理 | **推荐**。DirectML 走 DX12，RTX 5060 直接可用，**无需安装 CUDA 运行库**，体积仅加几十 MB |
| ONNX Runtime + CUDA EP | 同上但用 CUDA 后端 | 需捆 CUDA/cuDNN，单文件体积暴涨数 GB，**破坏便携单文件卖点** |
| Python 边车进程 | 打包 Python + 模型，C# 调子进程 | 需捆 Python 运行时，140 MB → 数百 MB，分发麻烦 |

模型 ONNX 可用性：Demucs 有社区 ONNX 导出；Basic Pitch 官方提供 ONNX。
方案 A 经此路线可行。

## 最小验证路径（建议先做这个）

现有素材可直接用：`test_vox.wav`、`test_mix.wav`、`test_acc.wav`，
以及 README 提到的 142 分钟现场录音。

**实验**：用 Demucs 对混音素材做分轨 → 对比分轨前后的 pYIN 输出 →
判断方案 A 是否消除 README 记录的"伴奏抢线"边界。

**更轻的起步方式**：先在**离线分析**（非实时）加一个"分轨"按钮，
调用外部 Demucs（不嵌入模型、不动核心代码），验证价值后再决定是否深度集成。

## 本机环境事实

- GPU：RTX 5060 Laptop **8 GB**，驱动 610.88（Blackwell 架构 sm_120）
- CPU / 内存：i9-14900HX / 15.7 GB
- 磁盘：C 盘剩 176 GB，D 盘剩 159 GB
- **现有 torch 是 `2.14.0+cpu`（纯 CPU 版，`torch.cuda.is_available()` 为 False，
  连显卡都看不见），需换成 CUDA 版**
- RTX 50 系需要 CUDA 12.8+；官方索引 `cu130` 提供 `torch 2.14.0+cu130`
  （版本号与现有一致）

  ```
  pip install --index-url https://download.pytorch.org/whl/cu130 torch torchaudio --upgrade
  ```

- 已装：soundfile 0.14.0、numpy 2.5.2、scipy 1.18.0
- 未装：transformers、faster-whisper、librosa、datasets

## 可用于算法精度验证的数据集

- **MAESTRO v3.0.0**：约 200 小时钢琴演奏，音符标注精度约 3 ms（MIDI + WAV 配对）。
  完整 zip 101 GB / 解压 120 GB；MIDI-only 版仅 58 MB。
  注意硬盘：D 盘剩 159 GB，zip 与解压文件无法共存，需解压后删 zip 或只取部分年份。
  官方页：https://magenta.tensorflow.org/datasets/maestro
- **NSynth**：30 万+ 单音符样本，每个带精确音高标签，适合基频提取算法的准确率验证。

## 网络环境提示

- Google 托管资源（MAESTRO、NSynth、Hugging Face）**需要代理**；
  本机有代理额度且月底到期，适合现在下载
- GitHub `git clone` / API 畅通，但 **releases 二进制下载时通时不通**
- PyPI / npm 稳定，无需代理
- YouTube 直连不可达

## 与主项目功能的关系

本方案是**检测质量改进**，不改变 PitchBall 的产品形态：
仍是 Windows 桌面单文件工具，仍是实时 + 离线两条管线。
区别只在混音素材场景下，前置多一步人声分离。
`test_*` 系列素材与现场录音可用于回归对比，确认纯净素材场景无退化。
