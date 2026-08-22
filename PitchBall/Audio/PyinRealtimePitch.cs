namespace PitchBall.Audio;

using PitchBall.Models;

/// <summary>
/// pYIN 的因果实时版(流式固定滞后平滑,psi 环形直读回溯):
/// 每次推入一个 256 样本跳距的子帧(取末尾 2048 作为 YIN 帧,@44100 ≈ 172 fps,
/// 与官方 pYIN 的帧率一致,帧间重叠 87.5%,候选漂移小、音高线连续);
/// 前向过程持续滚动、永不重置(与离线整文件 Viterbi 的历史惯性一致,
/// 不会像窗口重启那样让微弱伴奏音免费启动音高路径);
/// 输出 = 从当前最优状态沿 psi 环回走 LagFrames 帧后的状态(与
/// 快照重前向回溯在数学上等价,但每帧仅一次转移循环,成本极低)。
/// 无声(状态落在 HMM 无声侧)返回 0。HMM 转移概率按子帧时长(256/采样率)重标定。
/// </summary>
public sealed class PyinRealtimePitch
{
    /// <summary>子帧跳距:引擎每 2048 样本跳进 8 个子帧(172fps,官方 pYIN 帧率)。</summary>
    public const int SubHop = 256;

    private const int LagFrames = 96;  // 输出滞后(子帧数,@172fps ≈ 0.56s)
    private const int PsiRingFrames = 160; // psi 环容量(≥ 滞后 + 余量)

    private readonly PyinPitchDetector _yin = new();
    private readonly List<PyinCandidate[]> _candRing = new(PsiRingFrames);
    private PyinMonoPitch _hmm = new(PyinMonoPitch.RefHopSeconds);
    private double _hmmRate = 44100;
    private long _frameCount;   // 已前向的观测帧数
    private long _candBase;     // _candRing[0] 的绝对帧序
    private float[] _block = new float[PyinPitchDetector.BlockSize];

    /// <summary>最近一帧的声区分类(与 Push 返回同帧)。</summary>
    public VocalRegister LastRegister => _yin.LastRegister;

    /// <summary>推入一个子帧(至少 2048 样本,取末尾 2048),返回固定滞后位置的平滑频率(0 = 无声)。</summary>
    public double Push(ReadOnlySpan<float> frame, double sampleRate)
    {
        if (sampleRate != _hmmRate)
        {
            _hmm = new PyinMonoPitch(SubHop / sampleRate);
            _hmmRate = sampleRate;
            Reset();
        }

        // 取最新 2048 样本作为 YIN 帧(与 pYIN 的 2048 帧长一致)
        var block = frame[^PyinPitchDetector.BlockSize..];
        if (_block.Length != block.Length) _block = new float[block.Length];
        block.CopyTo(_block);

        var cands = _yin.Process(_block, sampleRate);
        var obs = _hmm.CalculateObsProb(cands);

        // 人声帧间候选抖动时,给有声箱加微量地板概率,让路径可以滑过 1-3 个
        // 无支持帧,避免频繁退出/进入导致显示断断续续。
        // 地板 ≈ 无声箱观测的 5%,远低于无声箱,不会引入虚假音高。
        for (int p = 0; p < PyinMonoPitch.NPitch; p++)
        {
            if (obs[p] < 1e-4) obs[p] = 1e-4;
        }

        _candRing.Add(cands);
        if (_candRing.Count > PsiRingFrames)
        {
            _candRing.RemoveAt(0);
            _candBase++;
        }

        // 滚动前向:首帧初始化,之后每帧推进一步
        if (_frameCount == 0)
        {
            _hmm.InitForward(obs, PsiRingFrames);
            _frameCount = 1;
        }
        else
        {
            _hmm.StepForward(obs);
            _frameCount++;
        }

        long targetAbs = _frameCount - 1 - LagFrames; // 输出观测帧(绝对帧序)
        if (targetAbs < 0) return 0;

        int targetIdx = (int)(targetAbs - _candBase);
        if (targetIdx < 0 || targetIdx >= _candRing.Count) return 0;

        int state = _hmm.BacktraceCurrentBest(LagFrames);
        return _hmm.MapStateToFreq(state, _candRing[targetIdx]);
    }

    public void Reset()
    {
        _candRing.Clear();
        _frameCount = 0;
        _candBase = 0;
    }
}
