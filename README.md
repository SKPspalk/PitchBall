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

## 常见问题

**双击没反应 / 打不开?**

1. **杀毒软件拦截**:PitchBall 是未签名的开源软件,360、腾讯电脑管家、火绒、Defender 等可能静默拦截。请到杀毒软件"隔离区/恢复区"找回 `PitchBall.exe` 并**添加信任**;Windows Defender 在"安全中心 → 保护历史记录"里允许即可。源码全部公开,可放心信任。
2. **SmartScreen 提示**:浏览器下载的文件会被 Windows 标记。若弹出"Windows 已保护你的电脑",点"**更多信息 → 仍要运行**";或右键文件 → 属性 → 勾选"**解除锁定**" → 应用。
3. **下载不完整**:v1.1.1 完整大小为 **147,104,651 字节**(约 140.3 MB),SHA256 校验值:
   ```
   16016BA248CC128EFCBCA2F60B19FF6022F9DF03A6AC7D8E3187F66EB1FA5428
   ```
   对不上就重新下载。校验方法:`certutil -hashfile PitchBall.exe SHA256`。
4. **第一次启动较慢**:单文件自包含版首次运行要把运行时解压到临时目录,机械硬盘上可能需要 10~30 秒才出现窗口,请耐心等待,可在任务管理器里确认 `PitchBall.exe` 进程已存在。
5. **系统要求**:Windows 10 1607 及以上(64 位),Windows 7/8 无法运行。

**闪退 / 报错怎么办?**

程序会把崩溃详情自动记录到 `%APPDATA%\PitchBall\crash.log`(在资源管理器地址栏粘贴该路径回车即可打开)。把该文件发给开发者或提交到 GitHub Issues,即可快速定位问题。

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
