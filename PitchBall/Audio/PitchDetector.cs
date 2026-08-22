namespace PitchBall.Audio;

/// <summary>
/// 基频检测:YIN 候选谷 + 频谱谐波能量打分(见下方说明)。
/// 输入单声道浮点采样,输出基频 Hz;返回 0 表示未检测到音高。
/// 检测前先做双向带通滤波(人声频段),提升复杂伴奏下的稳定性。
///
/// 选音策略(针对「歌曲成品,主唱音量最大」的场景):
/// 1. YIN 差值函数 + 累计均值归一化(CMNDF),收集检测频段内所有低于
///    宽阈值的局部谷作为候选周期(不按频率高低取舍);
/// 2. 对每个候选基频 f 用 Goertzel 求其谐波串的能量
///    (2×f 权重最高,再加 2~6 次谐波),乘以谷深因子 (1-CMNDF);
/// 3. 得分最高者当选——主唱混音里最响,其基频与谐波能量最大,
///    和声、伴奏贝斯/钢琴谐波能量相对小,自然落选。
/// </summary>
public sealed class PitchDetector
{
    private readonly int _bufferSize;
    private readonly double[] _yinBuffer;
    private readonly double[] _work;
    private readonly double[] _raw;

    /// <summary>候选谷阈值(宽):CMNDF 低于此值的谷才进入谐波打分。默认 0.55。</summary>
    public double Threshold { get; set; } = 0.55;

    /// <summary>防偏高逻辑(去超谐波降级 + 打分谐波周期拒绝)。默认开。</summary>
    public bool AntiOvershoot { get; set; } = true;

    /// <summary>上一帧在检测频段内 CMNDF 的最小值(诊断用)。</summary>
    public double LastMinCmndf { get; private set; }

    /// <summary>上一帧经过次谐波升级后的候选列表(周期, 谷深)(诊断用)。</summary>
    public List<(int Tau, double V)> LastCandidates { get; } = [];

    /// <summary>高音区 2 次谐波反超校正的最低频率(Hz)。低于此频率的检出不做半频校正。</summary>
    public double FlipFloor { get; set; } = 392;

    /// <summary>上一帧 2 次谐波反超校正的诊断数据(F=0 表示未评估)(诊断用)。</summary>
    public (double F, double X1, double P1, double EdgeMax, double BgMed, double P3, double P5, double A3, double A5,
        bool PeakOk, bool RatioOk, bool OddOk, bool ValleyOk, double VWin, double VHalf,
        double PsWin, double PsHalf, bool Flipped, bool Warm, bool FlipActive, bool FlipFinal) LastFlip;

    // 时域确认状态(pYIN 思路):八度翻转不应是单帧孤立事件——
    // 暖证据须连续出现才进入,活跃期容忍少数缺口,直到频率偏离锚点(真实八度跳变)才退出。
    private bool _flipActive;
    private int _warmStreak;
    private double _flipAnchor = -1;
    private readonly bool[] _evRing = new bool[5];
    private int _evRingIdx;

    /// <summary>检测下限(Hz)。低于 90Hz 属于伴奏贝斯区,不参与候选。</summary>
    public double MinFrequency { get; set; } = 90;

    /// <summary>检测上限(Hz),覆盖高假声,并排除高频谐波误检。</summary>
    public double MaxFrequency { get; set; } = 1500;

    // 带通滤波系数,按采样率缓存
    private double _cachedRate;
    private (double b0, double b1, double b2, double a1, double a2)[] _filters = [];

    public PitchDetector(int bufferSize)
    {
        _bufferSize = bufferSize;
        _yinBuffer = new double[bufferSize / 2];
        _work = new double[bufferSize];
        _raw = new double[bufferSize];
    }

    private void EnsureFilters(double sampleRate)
    {
        if (Math.Abs(sampleRate - _cachedRate) < 0.1) return;
        _cachedRate = sampleRate;
        // 2 级高通 + 2 级低通,把人声频段以外的伴奏能量(鼓、贝斯、镲)压下去
        _filters =
        [
            Hpf(sampleRate, MinFrequency * 1.15),
            Hpf(sampleRate, MinFrequency * 1.15),
            Lpf(sampleRate, MaxFrequency * 0.85),
            Lpf(sampleRate, MaxFrequency * 0.85),
        ];
    }

    private static (double, double, double, double, double) Hpf(double sr, double freq)
    {
        double w = 2 * Math.PI * freq / sr;
        double cos = Math.Cos(w);
        double alpha = Math.Sin(w) / (2 * 0.707);
        double a0 = 1 + alpha;
        return ((1 + cos) / 2 / a0, -(1 + cos) / a0, (1 + cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
    }

    private static (double, double, double, double, double) Lpf(double sr, double freq)
    {
        double w = 2 * Math.PI * freq / sr;
        double cos = Math.Cos(w);
        double alpha = Math.Sin(w) / (2 * 0.707);
        double a0 = 1 + alpha;
        return ((1 - cos) / 2 / a0, (1 - cos) / a0, (1 - cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0);
    }

    private static void ApplyBiquad(double[] x, (double b0, double b1, double b2, double a1, double a2) f)
    {
        double z1 = 0, z2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = x[i];
            double y = f.b0 * v + z1;
            z1 = f.b1 * v - f.a1 * y + z2;
            z2 = f.b2 * v - f.a2 * y;
            x[i] = y;
        }
    }

    /// <summary>检测一帧的基频。samples 长度需不小于构造时的 bufferSize。</summary>
    public double GetPitch(ReadOnlySpan<float> samples, double sampleRate)
    {
        if (samples.Length < _bufferSize) return 0;
        int half = _bufferSize / 2;

        // 带通预处理:双向滤波(零相位),滤掉伴奏频段,保留人声周期
        // 原始信号单独保留——谐波能量打分必须在未滤波的信号上进行:
        // 窄带低通(1275Hz)会把高音候选的 2 次以上谐波全部削掉,让低音候选
        // 结构性占优(还能借主唱基频当自己的 4 次谐波),导致高音被检成低八度。
        for (int i = 0; i < _bufferSize; i++) _raw[i] = samples[i];
        for (int i = 0; i < _bufferSize; i++) _work[i] = samples[i];
        EnsureFilters(sampleRate);
        foreach (var f in _filters) ApplyBiquad(_work, f);
        Array.Reverse(_work);
        foreach (var f in _filters) ApplyBiquad(_work, f);
        Array.Reverse(_work);

        // 频率范围 → tau 范围
        int tauMax = Math.Min(half - 1, (int)(sampleRate / MinFrequency));
        int tauMin = Math.Max(1, (int)(sampleRate / MaxFrequency));

        // 1. 差值函数 + 累计均值归一化(CMNDF)
        _yinBuffer[0] = 1.0;
        double runningSum = 0.0;
        for (int tau = 1; tau <= tauMax; tau++)
        {
            double sum = 0.0;
            for (int i = 0; i < half; i++)
            {
                double d = _work[i] - _work[i + tau];
                sum += d * d;
            }
            runningSum += sum;
            // 防止除以 0(纯静音帧)
            _yinBuffer[tau] = runningSum <= 0 ? 1.0 : sum * tau / runningSum;
        }

        // 2. 全频段 CMNDF 最小值(诊断用,与阈值无关)
        LastMinCmndf = 1.0;
        for (int tau = tauMin; tau < tauMax - 1; tau++)
        {
            if (_yinBuffer[tau] < LastMinCmndf) LastMinCmndf = _yinBuffer[tau];
        }

        // 3. 收集候选谷:局部最小且低于宽阈值,按谷深排序保留前 12
        var candTau = new List<int>(12);
        for (int tau = tauMin + 1; tau < tauMax - 1; tau++)
        {
            if (_yinBuffer[tau] < Threshold &&
                _yinBuffer[tau] <= _yinBuffer[tau - 1] &&
                _yinBuffer[tau] < _yinBuffer[tau + 1])
            {
                candTau.Add(tau);
            }
        }
        if (candTau.Count == 0)
        {
            // 兜底:整帧没有低于阈值的谷(高音混响/和声叠加会让谷变浅)。
            // 取全频段最深谷作为唯一候选,只要不是太浅(<0.75)仍参与打分,
            // 打分不过关自然落选,不会凭空引入噪声。
            int bestTau = tauMin;
            double bestV = double.MaxValue;
            for (int tau = tauMin; tau < tauMax - 1; tau++)
            {
                if (_yinBuffer[tau] < bestV) { bestV = _yinBuffer[tau]; bestTau = tau; }
            }
            if (bestV < 0.75) candTau.Add(bestTau);
        }
        if (candTau.Count == 0) return 0;
        candTau.Sort((a, b) => _yinBuffer[a].CompareTo(_yinBuffer[b]));
        if (candTau.Count > 12) candTau.RemoveRange(12, candTau.Count - 12);

        // 3.5 去次谐波:倍周期谷(1/2、1/3、1/4 周期处)若存在明显更深的谷,
        // 说明候选是真实基频的次谐波误检(如把 A5 检成 A3)——升级到更短周期。
        // 升级条件取严格谷深(<0.25),半周期谷(强 2 次谐波)通常只有 0.3~0.5,不会误升级。
        for (int i = 0; i < candTau.Count; i++)
        {
            int tau = candTau[i];
            foreach (int m in new[] { 4, 3, 2 })
            {
                int t2 = (int)Math.Round(tau / (double)m);
                if (t2 < tauMin || t2 >= tau) continue;
                double v0 = _yinBuffer[Math.Max(tauMin, t2 - 1)];
                double v1 = _yinBuffer[t2];
                double v2 = _yinBuffer[Math.Min(tauMax - 1, t2 + 1)];
                if (Math.Min(v0, Math.Min(v1, v2)) < 0.25)
                {
                    tau = t2;
                }
            }
            candTau[i] = tau;
        }

        // 诊断:记录升级后的候选(周期, 谷深)
        LastCandidates.Clear();
        foreach (int tau in candTau)
            LastCandidates.Add((tau, _yinBuffer[tau]));

        // 4. 谐波能量打分:主唱混音最响,其基频与谐波能量和最大。
        //    先对每个候选做抛物线插值得到精确频率再打分——
        //    整数 tau 的量化误差在高频可达 ±8Hz,直接打分会让高频候选被低估。
        double bestFreq = 0;
        double bestScore = double.MinValue;
        double bestX1 = 0;
        double winV = 0;
        foreach (int tau in candTau)
        {
            double a0 = _yinBuffer[tau - 1];
            double a1 = _yinBuffer[tau];
            double a2 = _yinBuffer[tau + 1];
            double betterTau = (a1 < a0 && a1 < a2)
                ? tau + (a2 - a0) / (2.0 * (2.0 * a1 - a2 - a0))
                : tau;
            double f = sampleRate / betterTau;
            double x1 = GoertzelAmplitude(f, sampleRate);

            // 4.1 谐波周期拒绝(仅高音区):若 f/2 处存在真实周期谷(CMNDF<0.55)
            // 且 f/2 处能量接近 f 处(>0.45×),说明 f 是 2 次谐波误检——跳过该候选。
            // 人声高音(假声/头声)常见 2 次谐波反超基频 6~10dB,此时真基频恰为 f/2;
            // 低八度和声/伴奏一般明显弱于主唱,不会达到 0.45 倍幅度,误伤风险低。
            bool halfIsFundamental = false;
            if (AntiOvershoot && f >= 500)
            {
                double fHalf = f / 2.0;
                if (fHalf >= MinFrequency)
                {
                    int tHalf = (int)Math.Round(sampleRate / fHalf);
                    if (tHalf < tauMax)
                    {
                        double h0 = _yinBuffer[Math.Max(tauMin, tHalf - 1)];
                        double h1 = _yinBuffer[tHalf];
                        double h2 = _yinBuffer[Math.Min(tauMax - 1, tHalf + 1)];
                        if (Math.Min(h0, Math.Min(h1, h2)) < 0.55)
                        {
                            double aHalf = GoertzelAmplitude(fHalf, sampleRate);
                            halfIsFundamental = aHalf > 0.45 * x1;
                        }
                    }
                }
            }
            if (halfIsFundamental) continue;

            // 4.2 三次谐波周期拒绝(仅高音区):若 f/3 处存在真实周期谷(CMNDF<0.55)
            // 且 f/3 处能量反超 f 处(>0.8×),说明 f 是 3 次谐波误检——跳过该候选。
            // 人声 3 次谐波极少反超基频,阈值高,误伤风险极小。
            bool thirdIsFundamental = false;
            if (AntiOvershoot && f >= 600)
            {
                double fThird = f / 3.0;
                if (fThird >= MinFrequency)
                {
                    int tThird = (int)Math.Round(sampleRate / fThird);
                    if (tThird < tauMax)
                    {
                        double h0 = _yinBuffer[Math.Max(tauMin, tThird - 1)];
                        double h1 = _yinBuffer[tThird];
                        double h2 = _yinBuffer[Math.Min(tauMax - 1, tThird + 1)];
                        if (Math.Min(h0, Math.Min(h1, h2)) < 0.55)
                        {
                            double aThird = GoertzelAmplitude(fThird, sampleRate);
                            thirdIsFundamental = aThird > 0.8 * x1;
                        }
                    }
                }
            }
            if (thirdIsFundamental) continue;

            double score = 3.0 * x1 * x1; // 基频高权重,防止倍周期候选借主唱基频当谐波反超
            for (int k = 2; k <= 6; k++)
            {
                double fk = f * k;
                if (fk > 5500) break;
                double x = GoertzelAmplitude(fk, sampleRate);
                score += x * x;
            }
            score *= 1.0 - _yinBuffer[tau]; // 谷深因子:周期性越强权重越大
            if (score > bestScore)
            {
                bestScore = score;
                bestFreq = f;
                bestX1 = x1;
                winV = _yinBuffer[tau];
            }
        }

        // 5. 高音区 2 次谐波反超校正。
        // 假声/头声高音常见 2 次谐波反超基频 10~20dB,此时打分自然选中 2f;
        // 而基频(≈f/2)的 CMNDF 谷被 τ 线性抬升淹没,通常不构成局部谷、进不了候选,
        // 单靠候选拒绝无法找回——这里直接对胜出频率做半频谱峰校验:
        //   a) f/2 处存在真实谱峰(±5% 五点搜索,内部最大且比两侧边缘强 ≥1.5 倍,
        //      排除噪声起伏与相邻峰旁瓣);
        //   b) 峰强 > 0.08×f 处能量(基频确实存在,哪怕弱);
        //   c) f/2 的奇次谐波(1.5f、2.5f)能量 > f 的奇次谐波(3f、5f)能量,
        //      且 > (0.1×f)²——若真基频是 f,其谐波串恰是 f/2 谐波串的子集,
        //      两者差异只在奇次谐波;奇次谐波整体归属 f/2 才说明整条谐波串以 f/2 为基。
        // 三者同时成立才把 f 判为 f/2 的 2 次谐波误检,输出 f/2。
        LastFlip = default;
        if (AntiOvershoot && bestFreq >= FlipFloor)
        {
            double f = bestFreq;
            double x1 = bestX1;

            var pts = new double[5];
            for (int i = 0; i < 5; i++)
                pts[i] = GoertzelAmplitude(f / 2 * (0.95 + i * 0.025), sampleRate);
            int bi = 0;
            for (int i = 1; i < 5; i++) if (pts[i] > pts[bi]) bi = i;
            double p1 = pts[bi];
            double edgeMax = Math.Max(pts[0], pts[4]);

            // 局部背景估计:取 f/2 两侧 ±8%~±18% 处 6 点的中位数(避开峰裙边)。
            // 真基频峰应显著高于背景(≥2.5 倍);纯噪声起伏或相邻峰旁瓣做不到。
            var bg = new double[6];
            var bgRatio = new double[] { 0.82, 0.86, 0.90, 1.10, 1.14, 1.18 };
            for (int i = 0; i < 6; i++)
                bg[i] = GoertzelAmplitude(f / 2 * bgRatio[i], sampleRate);
            Array.Sort(bg);
            double bgMed = (bg[2] + bg[3]) / 2;

            // p1 绝对下限:极弱帧里 p1 与 bg 同为噪声级,比值会虚高;
            // 真翻转帧 p1≥0.009,弱帧噪声鼓包仅 0.005 以下,0.005 一刀切开。
            bool peakOk = bi > 0 && bi < 4 && p1 > 1.5 * edgeMax && p1 > 2.5 * bgMed && p1 > 0.005;
            bool ratioOk = p1 > 0.08 * x1;

            double p3 = MaxG(f * 1.5, sampleRate);
            double p5 = MaxG(f * 2.5, sampleRate);
            double a3 = MaxG(f * 3.0, sampleRate);
            double a5 = MaxG(f * 5.0, sampleRate);
            double oddLower = p3 * p3 + p5 * p5;
            double oddUpper = a3 * a3 + a5 * a5;
            // p5 单独约束:若 f/2 真是基频,其 5 次谐波(2.5f)应为真实存在的峰。
            // 伴奏低音(如 170Hz)的 3、9 次谐波恰落在 0.5f、1.5f 上能骗过 p3,
            // 但 15 次谐波落在 2.5f 上需要极端富谐波,且强度必然衰减——不满足此条件。
            bool oddOk = oddLower > oddUpper && oddLower > 0.003 * x1 * x1 && p5 > 0.03 * x1;

            // 谷值证据:f/2 处存在周期候选谷、谷值 <0.5、且比胜出候选 f 处的谷更深,
            // 说明基频周期性在 f/2 更强——谱峰门槛放宽到 1.5×背景。
            // 谱峰 gates 的 rat/odd 对假声很苛刻(基频能量可能只有 2f 的 5%~25%),
            // 而周期谷是"信号是否真的以 f/2 为基"的直接证据:
            // 真 C6 帧(如 1030.4s)的候选列表里根本没有 f/2 谷,此规则天然免疫。
            double vHalf = 0;
            bool valleyOk = false;
            foreach (var (t, v) in LastCandidates)
            {
                double hz = sampleRate / t;
                if (hz >= f / 2 * 0.97 && hz <= f / 2 * 1.03 && v < 0.5 && v < winV)
                {
                    vHalf = v;
                    valleyOk = true;
                    break;
                }
            }

            // 谷值路径的谐波一致性:p3>a3 即 f/2 的 3 次谐波强于 f 的 6 次谐波——
            // 若 f/2 的谷来自伴奏低音(如 4704.98s),低音上谐波弱,该条件不成立。
            bool flipped = (peakOk && ratioOk && oddOk) || (valleyOk && p1 > 1.5 * bgMed && p1 > 0.005 && p3 > a3);

            // 素数谐波模板打分(SWIPE' 思路,诊断用):对 f 与 f/2 两个八度变体分别求
            // Σ_{k∈{1,2,3,5,7,11}} MaxG(k·g)/k(锯齿波谱包络 1/k 加权)。
            // 实测(整场演唱会多区域采样):假声 2 次谐波反超 20dB 时,能量集中在 2f,
            // f 模板分反而更高——单看模板分不能定八度,故仅保留作诊断参考,
            // 八度归属改由奇次谐波绝对强度(p3/p5)+ 时域持续性决定。
            double psWin = 0, psHalf = 0;
            {
                var ks = new[] { 1, 2, 3, 5, 7, 11 };
                foreach (int k in ks)
                {
                    double fk = f * k;
                    if (fk <= 5500) psWin += MaxG(fk, sampleRate) / k;
                    double hk = f / 2 * k;
                    if (hk <= 5500) psHalf += MaxG(hk, sampleRate) / k;
                }
            }

            // 暖证据:硬门控(峰形/候选谷)之外的第二条翻转路径。
            // 弱假声帧里 f/2 常无峰形、也无候选谷(如结尾淡出 8505s),但整条奇次
            // 谐波串仍归属 f/2(p3、p5 显著强于 a3、a5)。绝对下限把近静音帧的
            // 噪声级"比值虚高"(如 1178.7s)挡在门外;持续性要求(见下)再兜一层底。
            bool warm = !flipped && ratioOk && oddOk && p3 > 0.008 && p5 > 0.002;

            // pYIN 式时域确认:八度翻转须在时间上持续——
            // 暖证据连续 3 帧才进入;活跃期 5 帧内 ≥2 帧证据保持;
            // f/2 偏离锚点超 ±2 个半音(真实八度跳变)立即退出,防止锁死低八度。
            // 注意:硬证据(峰形/谷值路径,如 4706.261s 的 f/2 周期谷比 f 更深)
            // 已通过严苛门控,单帧孤立也不能被时域层撤销——撤销会把
            // "假声 2 次谐波反超"的高八度错误重新放回来,故硬证据始终生效。
            bool ev = flipped || warm;
            _evRing[_evRingIdx] = ev;
            _evRingIdx = (_evRingIdx + 1) % _evRing.Length;
            int evCount = 0;
            foreach (bool b in _evRing) if (b) evCount++;

            _warmStreak = warm ? _warmStreak + 1 : 0;
            double halfF = f / 2;
            if (_flipActive && _flipAnchor > 0 &&
                (halfF < _flipAnchor * 0.891 || halfF > _flipAnchor * 1.122))
            {
                _flipActive = false;
                _warmStreak = 0;
                Array.Clear(_evRing, 0, _evRing.Length);
                evCount = 0;
            }

            bool flipNow;
            if (!_flipActive)
            {
                flipNow = flipped || _warmStreak >= 3;
                if (flipNow) { _flipActive = true; _flipAnchor = halfF; }
            }
            else
            {
                flipNow = flipped || evCount >= 2;
                if (flipNow) _flipAnchor = _flipAnchor * 0.5 + halfF * 0.5;
                else _flipActive = false;
            }

            LastFlip = (f, x1, p1, edgeMax, bgMed, p3, p5, a3, a5, peakOk, ratioOk, oddOk, valleyOk, winV, vHalf, psWin, psHalf, flipped, warm, _flipActive, flipNow);
            if (flipNow) bestFreq = f / 2;
        }

        return bestFreq;
    }

    /// <summary>Goertzel 单频 DFT 幅度在 ±4% 范围内的最大值(容忍轻微频率偏移/颤音)。</summary>
    private double MaxG(double freq, double sampleRate)
    {
        double m = GoertzelAmplitude(freq, sampleRate);
        double lo = GoertzelAmplitude(freq * 0.96, sampleRate);
        double hi = GoertzelAmplitude(freq * 1.04, sampleRate);
        return Math.Max(m, Math.Max(lo, hi));
    }

    /// <summary>Goertzel 单频 DFT,返回频率 freq 处的归一化幅度(纯正弦时≈峰值)。
    /// 在原始信号(_raw)上计算,保证高音候选的谐波能量不被带通滤波削掉。</summary>
    private double GoertzelAmplitude(double freq, double sampleRate)
    {
        double w = 2 * Math.PI * freq / sampleRate;
        double coeff = 2 * Math.Cos(w);
        double s0 = 0, s1 = 0, s2 = 0;
        for (int i = 0; i < _bufferSize; i++)
        {
            s0 = _raw[i] + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
        if (power < 0) power = 0;
        return Math.Sqrt(power) / (_bufferSize / 2.0);
    }
}
