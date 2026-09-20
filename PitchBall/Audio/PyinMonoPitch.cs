namespace PitchBall.Audio;

/// <summary>
/// pYIN 的单音高隐马尔可夫模型(按 pypYIN MonoPitchHMM / SparseHMM 移植):
/// 61.735Hz 起 345 个 20 音分箱(69 个半音 × 5),另 345 个无声状态;
/// 三角权重转移(±5 箱);稀疏 Viterbi 解码 + 固定块回溯。
/// 两处对原始实现的修正(均已用官方 pypYIN 在真实人声上对照验证):
/// 1) 转移窗口在音域两端循环回绕——原实现裁剪窗口会在边界形成质量陷阱,
///    长静音后无声质量全部沉入边界箱,再也无法进入有声(43fps 下整段输出无声);
/// 2) 自转移概率按帧步长时间重标定:hopSeconds 为官方帧步(256/44100s)时
///    与原始 pYIN 完全一致;帧步更长时按 0.99^(hop/官方步长) 折算,
///    使"进入有声"的墙钟延迟不随帧率变慢。
/// </summary>
public sealed class PyinMonoPitch
{
    public const int NBps = 5;
    public const int NPitch = 69 * NBps;   // 345 个 20 音分箱
    public const int StateCount = 2 * NPitch; // 0..344 有声,345..689 无声(负频率)
    public const double MinFreq = 61.735;

    /// <summary>官方 pYIN 帧步长(256 样本 @ 44100Hz)。</summary>
    public const double RefHopSeconds = 256.0 / 44100.0;

    private const double SelfTrans = 0.99;
    private const double DefaultYinTrust = 0.5;

    private readonly double _selfTrans;
    private readonly double _yinTrust;
    private readonly double[] _freqs = new double[StateCount];
    private readonly double[] _init = new double[StateCount];
    private readonly int[] _from;
    private readonly int[] _to;
    private readonly double[] _trans;

    // Viterbi 工作缓冲
    private readonly double[] _delta = new double[StateCount];
    private readonly double[] _oldDelta = new double[StateCount];
    private ushort[]? _psi;        // 回溯指针缓冲,跨调用复用(DecodeViterbi 不可重入)
    private ushort[]? _streamPsi;  // 流式解码的 psi 环形缓冲
    private int _streamPsiHead;

    /// <summary>状态 → 频率表(有声为正,无声为负)。</summary>
    public IReadOnlyList<double> Frequencies => _freqs;

    public PyinMonoPitch(double hopSeconds = RefHopSeconds, double yinTrust = DefaultYinTrust)
    {
        _selfTrans = Math.Pow(SelfTrans, Math.Max(1.0, hopSeconds / RefHopSeconds));
        _yinTrust = yinTrust;

        int transitionWidth = 5 * (NBps / 2) + 1; // 11 箱 ≈ ±1.1 半音
        for (int p = 0; p < NPitch; p++)
        {
            _freqs[p] = MinFreq * Math.Pow(2, p * 1.0 / (12 * NBps));
            _freqs[p + NPitch] = -_freqs[p];
        }

        // 初始分布均匀(与 pypYIN 归一化后等价)
        for (int s = 0; s < StateCount; s++) _init[s] = 1.0 / StateCount;

        // 稀疏转移表:每个 (iPitch, i) 四元组(有→有 / 有→无 / 无→无 / 无→有)
        var from = new List<int>(15180);
        var to = new List<int>(15180);
        var trans = new List<double>(15180);

        for (int iPitch = 0; iPitch < NPitch; iPitch++)
        {
            // 三角权重恒为 [1,2,3,4,5,6,5,4,3,2,1],和为 36;
            // 窗口在音域两端循环回绕,使每行转移结构完全相同——
            // 原 pYIN 的裁剪窗口会在边界形成质量陷阱(实测导致长静音后无法再进入有声)
            for (int k = 0; k < transitionWidth; k++)
            {
                int i = ((iPitch - transitionWidth / 2 + k) % NPitch + NPitch) % NPitch;
                double p = (k < transitionWidth / 2 ? k + 1 : transitionWidth - k) / 36.0;
                // 有声 → 有声
                from.Add(iPitch); to.Add(i); trans.Add(p * _selfTrans);
                // 有声 → 无声
                from.Add(iPitch); to.Add(i + NPitch); trans.Add(p * (1 - _selfTrans));
                // 无声 → 无声
                from.Add(iPitch + NPitch); to.Add(i + NPitch); trans.Add(p * _selfTrans);
                // 无声 → 有声
                from.Add(iPitch + NPitch); to.Add(i); trans.Add(p * (1 - _selfTrans));
            }
        }
        _from = [.. from];
        _to = [.. to];
        _trans = [.. trans];
    }

    /// <summary>
    /// 把一帧候选转成观测概率向量(长度 StateCount)。
    /// 候选先归入最近的 20 音分箱,箱概率 × yinTrust(0.5);
    /// 无声箱均分剩余概率。与 pypYIN calculatedObsProb 一致。
    /// </summary>
    public double[] CalculateObsProb(IReadOnlyList<PyinCandidate> cands)
    {
        var obs = new double[StateCount];
        CalculateObsProbInto(cands, obs);
        return obs;
    }

    /// <summary>同 <see cref="CalculateObsProb"/>,但填入调用方提供的缓冲(避免长序列解码反复分配)。</summary>
    public void CalculateObsProbInto(IReadOnlyList<PyinCandidate> cands, double[] obs)
    {
        Array.Clear(obs, 0, StateCount);
        double probYinPitched = 0;

        foreach (var c in cands)
        {
            double freq = 440.0 * Math.Pow(2, (c.Midi - 69) / 12.0);
            if (freq <= MinFreq) continue;
            // 二分查找最近的箱(pypYIN 为线性扫描提前退出,结果相同)
            int lo = 0, hi = NPitch - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (_freqs[mid] <= freq) lo = mid; else hi = mid - 1;
            }
            int bin = lo;
            if (bin + 1 < NPitch && Math.Abs(freq - _freqs[bin + 1]) < Math.Abs(freq - _freqs[bin])) bin++;
            obs[bin] = c.Prob;          // 同箱多候选时后者覆盖,与 pypYIN 一致
            probYinPitched += c.Prob;
        }

        double probReallyPitched = _yinTrust * probYinPitched;
        if (probYinPitched > 0)
        {
            double scale = probReallyPitched / probYinPitched;
            for (int p = 0; p < NPitch; p++) obs[p] *= scale;
        }
        double unvoicedObs = (1 - probReallyPitched) / NPitch;
        for (int p = 0; p < NPitch; p++) obs[p + NPitch] = unvoicedObs;
    }

    /// <summary>帧观测已全部在内存时的重载(实时窗口解码)。</summary>
    public int[] DecodeViterbi(int nFrames, IReadOnlyList<double[]> obsFrames)
        => DecodeViterbi(nFrames, i => obsFrames[i]);

    /// <summary>
    /// 稀疏 Viterbi 解码(带缩放),返回每帧最可能状态。
    /// 对长序列按 4096 帧分块:前向一遍只存每块起点快照,回溯时逐块重算 psi,
    /// 内存与总帧数无关。progress 报告已完成工作单元数/总单元数(前向+回溯各一半)。
    /// </summary>
    public int[] DecodeViterbi(int nFrames, Func<int, double[]> obsAt, Action<int, int>? progress = null)
    {
        var path = new int[nFrames];
        if (nFrames < 1) return path;
        if (nFrames == 1)
        {
            var obs0 = obsAt(0);
            int best = 0;
            double bestV = -1;
            for (int s = 0; s < StateCount; s++)
            {
                double v = _init[s] * obs0[s];
                if (v > bestV) { bestV = v; best = s; }
            }
            path[0] = best;
            return path;
        }

        const int chunk = 4096;
        int nChunks = (nFrames + chunk - 1) / chunk;
        var snapshots = new double[nChunks][]; // snapshots[c] = 块 c 首帧之前的状态分布

        // ---- 前向:不存 psi,只存块起点快照与最终状态分布 ----
        Array.Clear(_oldDelta);
        Array.Clear(_delta);
        for (int f = 0; f < nFrames; f++)
        {
            if (f > 0 && f % chunk == 0)
            {
                snapshots[f / chunk] = (double[])_oldDelta.Clone();
                progress?.Invoke(f / chunk, 2 * nChunks);
            }
            double[] obs = obsAt(f);
            if (f == 0)
            {
                double sum = 0;
                for (int s = 0; s < StateCount; s++)
                {
                    _oldDelta[s] = _init[s] * obs[s];
                    sum += _oldDelta[s];
                }
                for (int s = 0; s < StateCount; s++) _oldDelta[s] /= sum;
                continue;
            }
            for (int t = 0; t < _trans.Length; t++)
            {
                double v = _oldDelta[_from[t]] * _trans[t];
                int toState = _to[t];
                if (v > _delta[toState]) _delta[toState] = v;
            }
            double dSum = 0;
            for (int s = 0; s < StateCount; s++)
            {
                _delta[s] *= obs[s];
                dSum += _delta[s];
            }
            if (dSum > 0)
            {
                for (int s = 0; s < StateCount; s++)
                {
                    _oldDelta[s] = _delta[s] / dSum;
                    _delta[s] = 0;
                }
            }
            else
            {
                // pypYIN:全零概率时退化为均匀分布
                for (int s = 0; s < StateCount; s++)
                {
                    _oldDelta[s] = 1.0 / StateCount;
                    _delta[s] = 0;
                }
            }
        }

        // ---- 回溯 ----
        int cur = 0;
        double bestVal = -1;
        for (int s = 0; s < StateCount; s++)
        {
            if (_oldDelta[s] > bestVal) { bestVal = _oldDelta[s]; cur = s; }
        }
        path[nFrames - 1] = cur;

        if (_psi == null || _psi.Length < chunk * StateCount) _psi = new ushort[chunk * StateCount];
        var psi = _psi;
        for (int c = nChunks - 1; c >= 0; c--)
        {
            int start = c * chunk;
            int end = Math.Min(start + chunk, nFrames);
            RunChunkForward(start, end, c == 0 ? null : snapshots[c], psi, obsAt);
            progress?.Invoke(2 * nChunks - 1 - c, 2 * nChunks);

            // 块内回退:path[f] = psi[f+1][path[f+1]],消费 psi 帧 [start+1, end-1]
            for (int f = end - 2; f >= start; f--)
            {
                cur = psi[(f + 1 - start) * StateCount + cur];
                path[f] = cur;
            }
            // 跨块边界:再消费一次本块首帧 psi,得到 path[start-1]
            if (c > 0) cur = psi[cur];
        }
        return path;
    }

    /// <summary>从快照 oldDelta 起算块内前向,并记录每帧每个状态的最佳来源状态(psi)。</summary>
    private void RunChunkForward(int start, int end, double[]? snapshot, ushort[] psi, Func<int, double[]> obsAt)
    {
        Array.Clear(psi, 0, (end - start) * StateCount);
        Array.Clear(_delta);
        if (snapshot != null) Array.Copy(snapshot, _oldDelta, StateCount);
        else Array.Clear(_oldDelta);

        for (int f = start; f < end; f++)
        {
            double[] obs = obsAt(f);
            if (f == 0 && snapshot == null)
            {
                double sum = 0;
                for (int s = 0; s < StateCount; s++)
                {
                    _oldDelta[s] = _init[s] * obs[s];
                    sum += _oldDelta[s];
                }
                for (int s = 0; s < StateCount; s++) _oldDelta[s] /= sum;
                continue;
            }
            int psiBase = (f - start) * StateCount;
            for (int t = 0; t < _trans.Length; t++)
            {
                double v = _oldDelta[_from[t]] * _trans[t];
                int toState = _to[t];
                if (v > _delta[toState])
                {
                    _delta[toState] = v;
                    psi[psiBase + toState] = (ushort)_from[t];
                }
            }
            double dSum = 0;
            for (int s = 0; s < StateCount; s++)
            {
                _delta[s] *= obs[s];
                dSum += _delta[s];
            }
            if (dSum > 0)
            {
                for (int s = 0; s < StateCount; s++)
                {
                    _oldDelta[s] = _delta[s] / dSum;
                    _delta[s] = 0;
                }
            }
            else
            {
                for (int s = 0; s < StateCount; s++)
                {
                    _oldDelta[s] = 1.0 / StateCount;
                    _delta[s] = 0;
                }
            }
        }
    }

    /// <summary>前向初始化(首帧):init × 观测后归一化,并分配 psi 环形缓冲。流式解码用。</summary>
    public void InitForward(double[] obs, int psiRingCapacity)
    {
        if (_streamPsi == null || _streamPsi.Length < psiRingCapacity * StateCount)
            _streamPsi = new ushort[psiRingCapacity * StateCount];
        _streamPsiHead = 0;
        double sum = 0;
        for (int s = 0; s < StateCount; s++)
        {
            _oldDelta[s] = _init[s] * obs[s];
            sum += _oldDelta[s];
        }
        for (int s = 0; s < StateCount; s++) _oldDelta[s] /= sum;
    }

    /// <summary>前向推进一步(转移 + 乘观测 + 归一化),并把每状态的最佳来源写入 psi 环。流式解码用。</summary>
    public void StepForward(double[] obs)
    {
        if (_streamPsi == null) return;
        int capacity = _streamPsi.Length / StateCount;
        var psiFrame = _streamPsi.AsSpan(_streamPsiHead * StateCount, StateCount);
        psiFrame.Clear();
        for (int t = 0; t < _trans.Length; t++)
        {
            double v = _oldDelta[_from[t]] * _trans[t];
            int toState = _to[t];
            if (v > _delta[toState])
            {
                _delta[toState] = v;
                psiFrame[toState] = (ushort)_from[t];
            }
        }
        double dSum = 0;
        for (int s = 0; s < StateCount; s++)
        {
            _delta[s] *= obs[s];
            dSum += _delta[s];
        }
        if (dSum > 0)
        {
            for (int s = 0; s < StateCount; s++)
            {
                _oldDelta[s] = _delta[s] / dSum;
                _delta[s] = 0;
            }
        }
        else
        {
            for (int s = 0; s < StateCount; s++)
            {
                _oldDelta[s] = 1.0 / StateCount;
                _delta[s] = 0;
            }
        }
        _streamPsiHead = (_streamPsiHead + 1) % capacity;
    }

    /// <summary>从当前最优状态沿 psi 环回走 stepsBack 帧,返回该帧的状态。流式解码用。</summary>
    public int BacktraceCurrentBest(int stepsBack)
    {
        if (_streamPsi == null) return 0;
        int capacity = _streamPsi.Length / StateCount;
        int cur = 0;
        double bestVal = -1;
        for (int s = 0; s < StateCount; s++)
        {
            if (_oldDelta[s] > bestVal) { bestVal = _oldDelta[s]; cur = s; }
        }
        for (int i = 0; i < stepsBack; i++)
        {
            int slot = (_streamPsiHead - 1 - i) % capacity;
            if (slot < 0) slot += capacity;
            cur = _streamPsi[slot * StateCount + cur];
        }
        return cur;
    }

    /// <summary>谱支撑度 Sup 低于此值视为"该音高在谱上几乎不存在"(次谐波幻音)。
    /// 实测:正确帧约 0.71~0.79,幻音帧约 0.07,取 0.15 作分界留足余量。</summary>
    private const double SupAbsent = 0.15;

    /// <summary>替身候选"自身基波"所需的最低谱支撑度(真基频实测 0.48~1.0;低一个
    /// 八度的次谐波候选虽然 Sup 被虚高,但自身基波接近零,会在这里被排除)。</summary>
    private const double FndPresent = 0.40;

    /// <summary>
    /// 状态 → 频率(与 pypYIN MonoPitch.process 一致):
    /// 有声状态取该帧候选中最接近 HMM 箱频率者,无声状态返回 0。
    ///
    /// 兜底(次谐波幻音):HMM 的转移窗口只有 ±5 箱(±1 半音),跨八度要在十几个
    /// 中间箱上连续转移,而中间箱没有候选(观测为 0)会让路径归零——所以状态一旦
    /// 落在次谐波上就再也回不来,即使观测概率完全支持真基频(实测 645Hz 候选
    /// 0.850 对 162Hz 幻音 0.005,输出仍是 162Hz),整段高音都被显示成低 2~4 个
    /// 八度(实测 142 分钟现场录音中 131s 段:E5 被报成 E3,与独立的手写 YIN 路径
    /// 判定不符,频谱上 162/324/486Hz 三处也确实没有能量)。
    ///
    /// 触发条件刻意收得很窄——状态对应的候选在谱上**连低阶谐波都没有**
    /// (Sup &lt; 0.15),说明本帧根本没有这个音高;此时改报"自身基波在谱上立得住"
    /// (Fnd &gt; 0.40)、概率不低于最强候选 1/4、相距 ≥3 半音的候选。
    /// 真基频弱(假声/头声)的帧不会触发:它们的 Sup 达 0.7+(2 次谐波就是那个强谱峰),
    /// 实测 1200s 段的 F5 假声 Sup≈0.79,规则不介入。
    /// </summary>
    public double MapStateToFreq(int state, IReadOnlyList<PyinCandidate> candsOfFrame)
    {
        if (state >= NPitch) return 0;
        double hmmFreq = _freqs[state];
        double bestFreq = 0;
        double leastDist = 10000.0;
        int nearIdx = -1;
        for (int i = 0; i < candsOfFrame.Count; i++)
        {
            double freq = 440.0 * Math.Pow(2, (candsOfFrame[i].Midi - 69) / 12.0);
            double dist = Math.Abs(hmmFreq - freq);
            if (dist < leastDist) { leastDist = dist; bestFreq = freq; nearIdx = i; }
        }

        if (nearIdx >= 0 && candsOfFrame.Count > 1 && candsOfFrame[nearIdx].Sup < SupAbsent)
        {
            double maxProb = 0;
            for (int i = 0; i < candsOfFrame.Count; i++)
            {
                if (candsOfFrame[i].Prob > maxProb) maxProb = candsOfFrame[i].Prob;
            }
            if (maxProb > 0)
            {
                int altIdx = -1;
                double altFnd = 0;
                for (int i = 0; i < candsOfFrame.Count; i++)
                {
                    if (i == nearIdx) continue;
                    if (candsOfFrame[i].Prob < 0.25 * maxProb) continue;
                    if (candsOfFrame[i].Fnd > altFnd)
                    {
                        altFnd = candsOfFrame[i].Fnd;
                        altIdx = i;
                    }
                }
                if (altIdx >= 0 && altFnd > FndPresent)
                {
                    double fAlt = 440.0 * Math.Pow(2, (candsOfFrame[altIdx].Midi - 69) / 12.0);
                    if (Math.Abs(12 * Math.Log2(fAlt / hmmFreq)) >= 3)
                    {
                        bestFreq = fAlt;
                    }
                }
            }
        }
        return bestFreq;
    }
}
