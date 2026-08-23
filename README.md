# 音高球 PitchBall

实时人声音高测量工具(Windows,WPF / .NET 8,单文件便携版)。

- 🎯 **实时音高检测**:悬浮小球实时显示音名、频率、音分(麦克风 / 系统声音 / 正在播放的应用三种音源);
- 🎤 **声区识别**:自动区分并标注 真声 / 混声 / 假声,假声高音不丢失、不跳八度;
- 📊 **离线分析**:拖入音频文件即得整曲音高曲线(波形图悬停查看任意时刻音高,滚轮缩放、拖动平移);
- 🎛 **人声场景三档**:通用 / 纯净CD版 / 演唱会版——按素材选择低音抑制策略;
- 🧭 **和声/多声部鲁棒**:观测概率时间平滑 + 锚点式防跳变,和声丰富的段落也能锁定主旋律;
- 🎨 **个性外观**:背景图(带虚化)、强调色(可跟随背景自动取色)、小球配色(按音级 / 按声区 / 按声部·男/女低中高);
- 🎬 **新手引导**:首次启动自动演示核心界面,设置里可随时重新查看;
- ⚙️ **分析选项**:拖入文件时可选择检测算法与人声场景(可记住选择)。

## 下载

- 最新版见 [Releases](https://github.com/SKPspalk/PitchBall/releases),直接运行 `PitchBall.exe`(单文件便携,免安装,已自包含 .NET 运行时)。

## 使用

1. 双击启动 → 首次启动有新手引导(真实界面演示);
2. 右键小球选择音源(麦克风 / 系统声音 / 正在播放的应用),开唱即显示音高;
3. 把音频文件拖到主界面或小球上,选择算法与场景后离线分析;
4. 设置面板:人声场景、检测算法、主题、背景图与虚化、强调色、小球颜色模式。

## 算法

基于 [pYIN](https://code.soundsoftware.ac.uk/projects/pyin)(YIN 概率阶段 + 隐马尔可夫平滑),C# 独立实现;针对真实使用场景做了以下增强(见 `PitchBall/Audio/` 源码注释):

- 假声(近似纯音)的次谐波消歧:次谐波求和(SHS)在候选谐波链内按谱能量选基频,并有连续帧迟滞防误触发;
- 真声/混声/假声分类:H1–H2 谐波能量比 + 次谐波链结构判据;
- 伴奏贝斯抑制:分场景的频率先验(通用/纯净CD版/演唱会版);
- 和声与多声部鲁棒:观测概率 IIR 时间平滑 + 显示级锚点式防跳变/防触底;
- 检测算法可在设置中切换回原手写 YIN。

## 参考文献 / 致谢

- M. Mauch and S. Dixon, "pYIN: A Fundamental Frequency Estimator Using Probabilistic Threshold Distributions", ICASSP 2014;
- D. J. Hermes, "Measurement of pitch by subharmonic summation", JASA 83(1), 1988;
- X. Sun, "Pitch determination and voice quality analysis using Subharmonic-to-Harmonic Ratio", ICASSP 2002;
- librosa 的 pYIN 实现与[最小点先验讨论](https://github.com/librosa/librosa/pull/1063)(仅借鉴思路)。

## 构建

```bash
dotnet build PitchBall/PitchBall.csproj -c Release
dotnet publish PitchBall/PitchBall.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## 许可证

[GPL-3.0](LICENSE)
