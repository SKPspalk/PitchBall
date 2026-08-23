namespace PitchBall.Audio;

using PitchBall.Models;

/// <summary>pYIN 的一帧候选:(音高, 概率),音高以 MIDI 表示(69 = A4)。</summary>
public readonly record struct PyinCandidate(double Midi, double Prob);

/// <summary>
/// pYIN(Mauch &amp; Dixon, ICASSP 2014)的 YIN 概率阶段,按官方 C++ 的 Python 移植
/// pypYIN 逐行移植:FFT 快速差函数 → 累积均值归一化差(CMNDF)→ 对 100 个阈值
/// 用 Beta(2) 分布积分出每个局部谷的候选概率 → 抛物线插值得到候选频率。
/// 与原 PitchDetector(手写 YIN + 翻转校正)互不干扰,两者并存。
/// </summary>
public sealed class PyinPitchDetector
{
    public const int BlockSize = 2048;   // pYIN 默认帧长
    private const int Y = BlockSize / 2; // yinBufferSize = 1024

    /// <summary>低幅门限:rms 低于此值按 pYINmain 的公式衰减候选概率。</summary>
    public const double LowAmp = 0.1;

    /// <summary>检测频率下限(Hz),对应 yinProb 的 maxTau0。</summary>
    public double FMin { get; set; } = 40;

    /// <summary>检测频率上限(Hz),对应 yinProb 的 minTau0。</summary>
    public double FMax { get; set; } = 1600;

    // pypYIN YinUtil.betaDist2:pYIN 论文 Beta(2) 阈值概率分布,100 个阈值 0.01..1.00
    private static readonly double[] BetaDist2 =
    [
        0.012614, 0.022715, 0.030646, 0.036712, 0.041184, 0.044301, 0.046277, 0.047298, 0.047528, 0.047110,
        0.046171, 0.044817, 0.043144, 0.041231, 0.039147, 0.036950, 0.034690, 0.032406, 0.030133, 0.027898,
        0.025722, 0.023624, 0.021614, 0.019704, 0.017900, 0.016205, 0.014621, 0.013148, 0.011785, 0.010530,
        0.009377, 0.008324, 0.007366, 0.006497, 0.005712, 0.005005, 0.004372, 0.003806, 0.003302, 0.002855,
        0.002460, 0.002112, 0.001806, 0.001539, 0.001307, 0.001105, 0.000931, 0.000781, 0.000652, 0.000542,
        0.000449, 0.000370, 0.000303, 0.000247, 0.000201, 0.000162, 0.000130, 0.000104, 0.000082, 0.000065,
        0.000051, 0.000039, 0.000030, 0.000023, 0.000018, 0.000013, 0.000010, 0.000007, 0.000005, 0.000004,
        0.000003, 0.000002, 0.000001, 0.000001, 0.000001, 0.000000, 0.000000, 0.000000, 0.000000, 0.000000,
        0.000000, 0.000000, 0.000000, 0.000000, 0.000000, 0.000000, 0.000000, 0.000000, 0.000000, 0.000000,
        0.000000, 0.000000, 0.000000, 0.000000, 0.000000, 0.000000, 0.000000, 0.000000, 0.000000, 0.000000,
    ];

    // FFT 中间缓冲(实例级:检测器不是线程安全的,并行时用 PyinPitchDetector.Shared 取线程本地实例)
    private readonly double[] _re = new double[BlockSize];
    private readonly double[] _im = new double[BlockSize];
    private readonly double[] _kre = new double[BlockSize];
    private readonly double[] _kim = new double[BlockSize];
    private readonly double[] _pt = new double[Y];
    private readonly double[] _cmndf = new double[Y];
    private readonly double[] _prob = new double[Y];
    private readonly double[] _spec = new double[Y]; // 输入帧的功率谱(声区分类用)
    private double _specRate = 44100;

    private static readonly ThreadLocal<PyinPitchDetector> PerThread = new(() => new PyinPitchDetector());

    /// <summary>线程本地共享实例:批量并行分析时每个线程各取一个,避免锁。</summary>
    public static PyinPitchDetector Shared => PerThread.Value!;

    /// <summary>最近一帧的声区分类结果(处理完一帧后读取)。</summary>
    public VocalRegister LastRegister { get; private set; } = VocalRegister.Chest;
    private int _falsettoStreak; // 连续假声帧计数(迟滞用)

    /// <summary>处理一帧(2048 样本),返回候选列表(概率已按 pYINmain 低幅规则缩放)。</summary>
    public PyinCandidate[] Process(ReadOnlySpan<float> input, double sampleRate)
    {
        // ---- 1. FFT 快速差函数(与 pypYIN fastDifference 完全一致)----
        Array.Clear(_im);
        Array.Clear(_kim);
        Array.Clear(_kre);
        for (int j = 0; j < BlockSize; j++) _re[j] = input[j];
        for (int j = 0; j < Y; j++) _kre[j] = input[Y - 1 - j];
        PyinFft.Fft(_re, _im);
        PyinFft.Fft(_kre, _kim);
        // 捕获输入帧的功率谱(声区分类与假声次谐波消歧用)
        for (int j = 0; j < Y; j++) _spec[j] = _re[j] * _re[j] + _im[j] * _im[j];
        _specRate = sampleRate;
        for (int j = 0; j < BlockSize; j++)
        {
            double tr = _re[j] * _kre[j] - _im[j] * _kim[j];
            _im[j] = _re[j] * _kim[j] + _im[j] * _kre[j];
            _re[j] = tr;
        }
        PyinFft.Ifft(_re, _im);

        _pt[0] = 0;
        for (int j = 0; j < Y; j++) _pt[0] += input[j] * input[j];
        for (int tau = 1; tau < Y; tau++)
            _pt[tau] = _pt[tau - 1] - input[tau - 1] * input[tau - 1] + input[tau + Y] * input[tau + Y];

        for (int j = 0; j < Y; j++)
            _cmndf[j] = _pt[0] + _pt[j] - 2 * _re[j + Y - 1];

        // ---- 2. 累积均值归一化差(CMNDF, pypYIN cumulativeDifference)----
        _cmndf[0] = 1.0;
        double runningSum = 0;
        for (int tau = 1; tau < Y; tau++)
        {
            runningSum += _cmndf[tau];
            _cmndf[tau] = runningSum == 0 ? 1 : _cmndf[tau] * tau / runningSum;
        }

        // ---- 3. 阈值概率(yinProb, prior=2 → betaDist2;fmin/fmax 限制 tau 区间)----
        int minTau = 2, maxTau = Y;
        int minTau0 = (int)(sampleRate / FMax);
        int maxTau0 = (int)(sampleRate / FMin);
        if (minTau0 > 0 && minTau0 < maxTau0) minTau = minTau0;
        if (maxTau0 > 0 && maxTau0 < Y && maxTau0 > minTau) maxTau = maxTau0;

        Array.Clear(_prob, 0, Y);
        const double minWeight = 0.01;
        int minInd = 0;
        double minVal = 42.0;
        double sumProb = 0;

        int ti = minTau;
        while (ti + 1 < maxTau)
        {
            if (_cmndf[ti] < 1.0 && _cmndf[ti + 1] < _cmndf[ti])
            {
                // 走到局部谷底
                while (ti + 1 < maxTau && _cmndf[ti + 1] < _cmndf[ti]) ti++;
                if (_cmndf[ti] < minVal && ti > 2)
                {
                    minVal = _cmndf[ti];
                    minInd = ti;
                }
                // P(候选) = 对阈值分布自顶向下累计到 d'(tau) 处
                int ci = 99;
                while (ci > -1 && 0.01 + ci * 0.01 > _cmndf[ti])
                {
                    _prob[ti] += BetaDist2[ci];
                    ci--;
                }
                sumProb += _prob[ti];
                ti++;
            }
            else
            {
                ti++;
            }
        }

        if (_prob[minInd] > 1)
        {
            // pypYIN:概率异常时返回空
            return [];
        }

        // 归一化:峰值保持为全局最小谷的概率,余量按 minWeight 补给它
        double nonPeakProb = 1.0;
        if (sumProb > 0)
        {
            for (int i = minTau; i < maxTau; i++)
            {
                _prob[i] = _prob[i] / sumProb * _prob[minInd];
                nonPeakProb -= _prob[i];
            }
        }
        if (minInd > 0) _prob[minInd] += nonPeakProb * minWeight;

        // ---- 4. 抛物线插值生成候选(按 tau 升序 = 频率降序,与 pypYIN 一致)----
        double rms = 0;
        for (int j = 0; j < BlockSize; j++) rms += input[j] * input[j];
        rms = Math.Sqrt(rms / BlockSize);
        double ampFactor = rms < LowAmp ? (rms + 0.01 * LowAmp) / (1.01 * LowAmp) : 1.0;

        var cands = new List<PyinCandidate>(32);
        for (int tau = minTau; tau < maxTau; tau++)
        {
            if (_prob[tau] <= 0) continue;
            double betterTau = ParabolicInterpolation(tau);
            double f0 = sampleRate / betterTau;
            double midi = 12 * Math.Log(f0 / 440.0, 2) + 69;
            cands.Add(new PyinCandidate(midi, _prob[tau] * ampFactor * VocalWeight(f0)));
        }

        ClassifyRegisterAndFixFalsetto(cands);
        return [.. cands];
    }

    /// <summary>
    /// 声区分类 + 假声次谐波消歧。
    /// 假声接近纯音:CMNDF 在 f、f/2、f/4…处都为零,yinProb 沿次谐波链打平,
    /// Viterbi 平手会取最低频,导致假声高音显示为低八度/次谐波。
    /// 检测"次谐波链"(存在概率接近的 f/2 候选)判为假声,再按功率谱能量
    /// 在候选间重新加权——真实基频处谱峰能量最大,即可锁定正确音高。
    /// 真声/混声谐波丰富,无此歧义,保持标准 pYIN 概率不变。
    /// </summary>
    private void ClassifyRegisterAndFixFalsetto(List<PyinCandidate> cands)
    {
        LastRegister = VocalRegister.Chest;
        if (cands.Count == 0) return;

        PyinCandidate top = cands[0];
        foreach (var c in cands)
        {
            if (c.Prob > top.Prob) top = c;
        }
        double fTop = 440.0 * Math.Pow(2, (top.Midi - 69) / 12.0);

        bool HasNear(double f)
        {
            if (f < FMin) return false;
            foreach (var c in cands)
            {
                double cf = 440.0 * Math.Pow(2, (c.Midi - 69) / 12.0);
                if (Math.Abs(cf - f) / f < 0.03) return true;
            }
            return false;
        }

        // 次谐波链 + 最高候选 ≥150Hz = 假声特征(低音也有次谐波链,但那是贝斯而非假声)
        bool falsettoLike = fTop >= 150 && HasNear(fTop / 2) && fTop > FMin * 2;

        // 迟滞:连续 ≥2 帧假声才启用增强,避免鼓点/噪声单帧误触发
        // 造成谐波链内候选跳变(曲线直升直降)
        _falsettoStreak = falsettoLike ? _falsettoStreak + 1 : 0;
        bool applyFalsetto = _falsettoStreak >= 2;
        if (falsettoLike)
        {
            LastRegister = VocalRegister.Falsetto;
        }

        if (applyFalsetto)
        {
            // 只处理顶候选自身的谐波链 {f/2, f, 2f, 4f}。
            // 用次谐波求和(SHS,Hermes 1988)为链成员打分:
            // score(m) = Σ 0.84^(n-1)·P(n·m),n=1..5。
            // 基频的序列包含自身的基波能量,高八度的序列缺少该项,
            // 因此对"倍频错误"(如 F5 被识成 F6)天然免疫——
            // 只有基波几乎完全缺失时才可能选到高八度。
            // 链外候选(贝斯、其他乐器)概率保持不变,不被连带放大。
            double[] chain = [fTop / 2, fTop, fTop * 2, fTop * 4];
            double sMax = 1e-30;
            for (int i = 0; i < cands.Count; i++)
            {
                double cf = CandidateFreq(cands[i]);
                foreach (var m in chain)
                {
                    if (Math.Abs(cf - m) / m < 0.03) sMax = Math.Max(sMax, ChainScore(cf));
                }
            }
            if (sMax > 1e-30)
            {
                double maxP = 0;
                for (int i = 0; i < cands.Count; i++)
                {
                    double cf = CandidateFreq(cands[i]);
                    foreach (var m in chain)
                    {
                        if (Math.Abs(cf - m) / m < 0.03)
                        {
                            cands[i] = cands[i] with { Prob = cands[i].Prob * (ChainScore(cf) / sMax) };
                            break;
                        }
                    }
                    if (cands[i].Prob > maxP) maxP = cands[i].Prob;
                }
                if (maxP > 1e-6)
                {
                    // 假声增强:谱峰成员归一到 0.85,短促假声也能在少量帧内进入有声
                    double boost = 0.85 / maxP;
                    for (int i = 0; i < cands.Count; i++)
                    {
                        double cf = CandidateFreq(cands[i]);
                        foreach (var m in chain)
                        {
                            if (Math.Abs(cf - m) / m < 0.03)
                            {
                                cands[i] = cands[i] with { Prob = cands[i].Prob * boost };
                                break;
                            }
                        }
                    }
                }
            }
        }
        else if (!falsettoLike)
        {
            // 谐波丰富度:2-8 次谐波能量 / 基频能量,粗略区分真声与混声
            double sFund = SpecAt(fTop);
            double sHarm = 0;
            for (int k = 2; k <= 8; k++) sHarm += SpecAt(fTop * k);
            LastRegister = sFund > 1e-30 && sHarm / sFund < 1.0
                ? VocalRegister.Mixed : VocalRegister.Chest;
        }
    }

    /// <summary>频率处的功率谱(线性插值)。</summary>
    private double SpecAt(double f)
    {
        double bin = f * BlockSize / _specRate;
        int b0 = (int)bin;
        if (b0 < 1 || b0 >= Y - 1) return 0;
        double frac = bin - b0;
        return _spec[b0] * (1 - frac) + _spec[b0 + 1] * frac;
    }

    private static double CandidateFreq(PyinCandidate c)
        => 440.0 * Math.Pow(2, (c.Midi - 69) / 12.0);

    /// <summary>SHS 谐波序列求和(Hermes 1988):Σ 0.84^(n-1)·P(n·f)。</summary>
    private double ChainScore(double f)
    {
        double s = 0, w = 1;
        for (int n = 1; n <= 5; n++)
        {
            s += w * SpecAt(n * f);
            w *= 0.84;
        }
        return s;
    }

    /// <summary>
    /// 人声场景先验:
    /// - Clean(纯净CD版):不抑制,保留完整人声音域(含男低音/男中音);
    /// - Live(演唱会版):220Hz 以下四次方强抑制,压制现场伴奏贝斯,
    ///   代价是低音人声被削弱;
    /// - Balanced(通用,默认):仅抑制 55-130Hz(伴奏贝斯区),男中音基本保留,
    ///   兼顾两类素材。
    /// </summary>
    public enum VocalProfileType { Balanced, Clean, Live }

    /// <summary>当前人声场景先验(由设置驱动,全管线生效)。</summary>
    public static VocalProfileType VocalProfile { get; set; } = VocalProfileType.Balanced;

    /// <summary>按当前场景先验计算候选权重。</summary>
    public static double VocalWeight(double freq)
        => VocalProfile switch
        {
            // 纯净CD版:仅抑制 100Hz 以下亚低音(伴奏/低音声部),人声 ≥100Hz 基本不衰减
            VocalProfileType.Clean => freq >= 100 ? 1.0 : Math.Pow(freq / 100.0, 6),
            VocalProfileType.Live => freq >= 220 ? 1.0 : Math.Pow(freq / 220.0, 4),
            _ => freq >= 130 ? 1.0 : Math.Pow(Math.Max(0, (freq - 55) / 75.0), 4),
        };

    private double ParabolicInterpolation(int tau)
    {
        if (tau > 0 && tau < Y - 1)
        {
            double s0 = _cmndf[tau - 1], s1 = _cmndf[tau], s2 = _cmndf[tau + 1];
            double adj = (s2 - s0) / (2 * (2 * s1 - s2 - s0));
            if (Math.Abs(adj) > 1) adj = 0;
            return tau + adj;
        }
        return tau;
    }
}

/// <summary>2048 点迭代 radix-2 FFT(静态查找表,线程安全只读)。</summary>
internal static class PyinFft
{
    private const int N = 2048;

    private static readonly int[] Rev = BuildReverse();
    private static readonly double[] Cos = new double[N / 2];
    private static readonly double[] Sin = new double[N / 2];

    static PyinFft()
    {
        for (int k = 0; k < N / 2; k++)
        {
            double a = 2 * Math.PI * k / N;
            Cos[k] = Math.Cos(a);
            Sin[k] = Math.Sin(a);
        }
    }

    private static int[] BuildReverse()
    {
        var rev = new int[N];
        for (int i = 0; i < N; i++)
        {
            int r = 0;
            for (int b = 0; b < 11; b++)
                if ((i & (1 << b)) != 0) r |= 1 << (10 - b);
            rev[i] = r;
        }
        return rev;
    }

    public static void Fft(double[] re, double[] im)
    {
        for (int i = 0; i < N; i++)
        {
            int j = Rev[i];
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= N; len <<= 1)
        {
            int half = len >> 1;
            int step = N / len;
            for (int i = 0; i < N; i += len)
            {
                for (int j = 0; j < half; j++)
                {
                    int k = j * step;
                    double wr = Cos[k], wi = Sin[k];
                    double tr = re[i + j + half] * wr - im[i + j + half] * wi;
                    double ti = re[i + j + half] * wi + im[i + j + half] * wr;
                    re[i + j + half] = re[i + j] - tr;
                    im[i + j + half] = im[i + j] - ti;
                    re[i + j] += tr;
                    im[i + j] += ti;
                }
            }
        }
    }

    /// <summary>IFFT(x) = conj(FFT(conj(x))) / N。</summary>
    public static void Ifft(double[] re, double[] im)
    {
        for (int i = 0; i < N; i++) im[i] = -im[i];
        Fft(re, im);
        for (int i = 0; i < N; i++)
        {
            re[i] /= N;
            im[i] = -im[i] / N;
        }
    }
}
