using System.Buffers;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PitchBall.Models;

namespace PitchBall.Audio;

/// <summary>
/// 音频文件离线分析:解码(Media Foundation)→ 22050Hz 单声道 float →
/// YIN 逐帧音高 + RMS 波形峰值 + 音域统计。
/// </summary>
public static class FileAnalyzer
{
    public const int AnalysisRate = 22050;
    public const int AnalysisFrame = 1024;   // YIN 帧长(约 46ms)
    public const int AnalysisHop = 1024;     // 帧间跳距 → 约 21.5 fps
    public const int PyinHop = 512;          // pYIN 路径帧间跳距 → 约 43 fps(HMM 平滑需要更密的时间采样)
    public const int PeaksPerSecond = 50;

    public static async Task<AnalysisResult> AnalyzeAsync(
        string path, IProgress<double>? progress = null, CancellationToken ct = default,
        string? algorithm = null)
    {
        // ConfigureAwait(false):库内等待不捕获 UI 上下文,避免调用方同步等待时死锁
        return await Task.Run(
            () => (algorithm ?? "Yin") == "Pyin"
                ? AnalyzePyin(path, progress, ct)
                : Analyze(path, progress, ct),
            ct).ConfigureAwait(false);
    }

    private static AnalysisResult Analyze(string path, IProgress<double>? progress, CancellationToken ct)
    {
        var result = new AnalysisResult
        {
            FilePath = path,
            FileName = Path.GetFileName(path),
            PitchRate = AnalysisRate / (double)AnalysisHop,
            Algorithm = "YIN",
        };

        using var reader = OpenReader(path);
        result.Duration = reader.TotalTime.TotalSeconds;

        var target = new WaveFormat(AnalysisRate, 32, 1);
        var fmt = reader.WaveFormat;
        IWaveProvider stream;
        if (fmt.SampleRate == target.SampleRate && fmt.Channels == 1 &&
            fmt.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            stream = reader;
        }
        else
        {
            // 重采样用纯托管 WDL 实现(MediaFoundationResampler 在部分系统上读取会卡死)
            var samples = reader.ToSampleProvider();
            if (fmt.Channels != 1) samples = samples.ToMono();
            stream = new WdlResamplingSampleProvider(samples, AnalysisRate).ToWaveProvider();
        }

        var peaks = new List<float>(Math.Max(64, (int)(result.Duration * PeaksPerSecond)));
        var freqs = new List<double>(Math.Max(64, (int)(result.Duration * AnalysisRate / AnalysisHop)));
        var counts = new Dictionary<int, int>();

        var yin = new PitchDetector(AnalysisFrame);
        float[] frame = ArrayPool<float>.Shared.Rent(AnalysisFrame);
        float[] scratch = ArrayPool<float>.Shared.Rent(AnalysisFrame);
        try
        {
            var pending = new List<float>(AnalysisFrame * 2);
            var waveAcc = new List<float>(PeaksPerSecond);
            int peakChunk = AnalysisRate / PeaksPerSecond; // 441

            byte[] byteBuf = ArrayPool<byte>.Shared.Rent(AnalysisFrame * 8);
            float[] chunkBuf = ArrayPool<float>.Shared.Rent(AnalysisFrame * 8 / 4);
            try
            {
                double t = 0;
                int read;
                while ((read = stream.Read(byteBuf, 0, byteBuf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int sampleCount = read / 4; // float32 单声道
                    Buffer.BlockCopy(byteBuf, 0, chunkBuf, 0, read);

                    var span = chunkBuf.AsSpan(0, sampleCount);
                    foreach (var s in span)
                    {
                        waveAcc.Add(s);
                        if (waveAcc.Count >= peakChunk)
                        {
                            peaks.Add(ComputeRms(waveAcc));
                            waveAcc.Clear();
                        }
                    }

                    pending.AddRange(span);
                    while (pending.Count >= AnalysisFrame)
                    {
                        pending.CopyTo(0, frame, 0, AnalysisFrame);
                        pending.RemoveRange(0, AnalysisHop);

                        double rms = 0;
                        for (int i = 0; i < AnalysisFrame; i++)
                        {
                            scratch[i] = frame[i];
                            rms += frame[i] * frame[i];
                        }
                        rms = Math.Sqrt(rms / AnalysisFrame);

                        double freq = rms < 0.005 ? 0 : yin.GetPitch(scratch, AnalysisRate);
                        freqs.Add(freq);
                        t += AnalysisHop / (double)AnalysisRate;

                        if (freq > 0)
                        {
                            int midi = NoteNames.FrequencyToMidiNote(freq);
                            counts[midi] = counts.GetValueOrDefault(midi) + 1;
                        }
                    }

                    progress?.Report(Math.Min(1.0, t / Math.Max(0.1, result.Duration)));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(byteBuf);
                ArrayPool<float>.Shared.Return(chunkBuf);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(frame);
            ArrayPool<float>.Shared.Return(scratch);
        }

        result.WavePeaks = peaks.ToArray();
        result.PitchFreqs = freqs.ToArray();

        int voiced = 0;
        foreach (var kv in counts)
        {
            result.NoteCounts[kv.Key] = kv.Value;
            voiced += kv.Value;
            if (kv.Key < result.MinMidi) result.MinMidi = kv.Key;
            if (kv.Key > result.MaxMidi) result.MaxMidi = kv.Key;
        }
        result.VoicedRatio = freqs.Count > 0 ? voiced / (double)freqs.Count : 0;

        progress?.Report(1.0);
        return result;
    }

    private static float ComputeRms(List<float> samples)
    {
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        return (float)Math.Sqrt(sum / samples.Count);
    }

    /// <summary>
    /// pYIN 离线分析:解码 → 22050Hz 单声道 → 2048 帧 / 512 跳的 YIN 概率阶段
    /// (批量并行)→ 整文件稀疏 Viterbi 平滑 → 逐帧频率(无声为 0)。
    /// 与原 YIN 路径共用输出格式,互不影响。
    /// </summary>
    private static AnalysisResult AnalyzePyin(string path, IProgress<double>? progress, CancellationToken ct)
    {
        var result = new AnalysisResult
        {
            FilePath = path,
            FileName = Path.GetFileName(path),
            PitchRate = AnalysisRate / (double)PyinHop,
            Algorithm = "pYIN",
        };

        using var reader = OpenReader(path);
        result.Duration = reader.TotalTime.TotalSeconds;

        var target = new WaveFormat(AnalysisRate, 32, 1);
        var fmt = reader.WaveFormat;
        IWaveProvider stream;
        if (fmt.SampleRate == target.SampleRate && fmt.Channels == 1 &&
            fmt.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            stream = reader;
        }
        else
        {
            // 重采样用纯托管 WDL 实现(MediaFoundationResampler 在部分系统上读取会卡死)
            var samples = reader.ToSampleProvider();
            if (fmt.Channels != 1) samples = samples.ToMono();
            stream = new WdlResamplingSampleProvider(samples, AnalysisRate).ToWaveProvider();
        }

        const int block = PyinPitchDetector.BlockSize; // 2048
        const int hop = PyinHop;                       // 512 → 43 fps
        var peaks = new List<float>(Math.Max(64, (int)(result.Duration * PeaksPerSecond)));
        var candFrames = new List<PyinCandidate[]>(Math.Max(64, (int)(result.Duration * AnalysisRate / hop)));

        // YIN 概率阶段按批并行(每批 2048 帧 ≈ 95 秒音频)
        const int batchFrames = 2048;
        var batch = new float[batchFrames * block];
        var batchCands = new PyinCandidate[batchFrames][];
        int bi = 0;
        var pending = new List<float>(block + 1024);
        var waveAcc = new List<float>(PeaksPerSecond);
        int peakChunk = AnalysisRate / PeaksPerSecond; // 441

        void FlushBatch()
        {
            Parallel.For(0, bi, i =>
            {
                batchCands[i] = PyinPitchDetector.Shared.Process(batch.AsSpan(i * block, block), AnalysisRate);
            });
            for (int i = 0; i < bi; i++) candFrames.Add(batchCands[i] ?? []);
            bi = 0;
        }

        byte[] byteBuf = ArrayPool<byte>.Shared.Rent(block * 8);
        float[] chunkBuf = ArrayPool<float>.Shared.Rent(block * 8 / 4);
        try
        {
            double t = 0;
            int read;
            while ((read = stream.Read(byteBuf, 0, byteBuf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int sampleCount = read / 4; // float32 单声道
                Buffer.BlockCopy(byteBuf, 0, chunkBuf, 0, read);

                var span = chunkBuf.AsSpan(0, sampleCount);
                foreach (var s in span)
                {
                    waveAcc.Add(s);
                    if (waveAcc.Count >= peakChunk)
                    {
                        peaks.Add(ComputeRms(waveAcc));
                        waveAcc.Clear();
                    }
                }

                pending.AddRange(span);
                while (pending.Count >= block)
                {
                    pending.CopyTo(0, batch, bi * block, block);
                    pending.RemoveRange(0, hop);
                    if (++bi == batchFrames) FlushBatch();
                    t += hop / (double)AnalysisRate;
                }

                progress?.Report(Math.Min(0.75, 0.75 * t / Math.Max(0.1, result.Duration)));
            }
            if (bi > 0) FlushBatch();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(byteBuf);
            ArrayPool<float>.Shared.Return(chunkBuf);
        }

        // 整文件 Viterbi 改为分段解码:每 60s 一段(前后各带 1s 上下文),段间独立初始化。
        // 全局单次解码在长时间稀疏人声后 u 惯性接近 1,弱人声(候选概率 0.01-0.05)
        // 无法在有限帧内重新进入有声(实测 14:50 高音丢失);分段解码与区域分析
        // 行为一致(1s/15s/60s 上下文均检出,已对照验证)。
        // YinTrust 0.7(官方 0.5):提高弱人声候选的有声观测权重,使进入决定
        // 有稳定余量(0.5 时 14:50 弱高音在边际上抖动,随窗口尾部不同而丢失)。
        progress?.Report(0.8);
        var hmm = new PyinMonoPitch(hop / (double)AnalysisRate, 0.7);
        int nFrames = candFrames.Count;
        var statePath = new int[nFrames];
        var obsScratch = new double[PyinMonoPitch.StateCount];
        // 观测时间平滑(IIR):和声丰富的多声部段落里,单帧候选被打散且逐帧跳变,
        // 平滑后同一音高反复出现的峰值会在自己的箱上累积出稳定支撑,
        // HMM 得以锁定主导人声线(文件与实时管线均启用)
        var obsSmooth = new double[PyinMonoPitch.StateCount];
        bool firstObs = true;
        const double obsAlpha = 0.4;
        double fps = AnalysisRate / (double)hop;
        int segFrames = (int)(60 * fps);
        int ctxFrames = (int)(1.0 * fps);
        int segIndex = 0, segCount = (nFrames + segFrames - 1) / segFrames;
        for (int segStart = 0; segStart < nFrames; segStart += segFrames)
        {
            int from = Math.Max(0, segStart - ctxFrames);
            int to = Math.Min(nFrames, segStart + segFrames + ctxFrames);
            var segPath = hmm.DecodeViterbi(to - from, i =>
            {
                hmm.CalculateObsProbInto(candFrames[from + i], obsScratch);
                if (firstObs)
                {
                    Array.Copy(obsScratch, obsSmooth, PyinMonoPitch.StateCount);
                    firstObs = false;
                }
                else
                {
                    for (int s = 0; s < PyinMonoPitch.StateCount; s++)
                    {
                        obsSmooth[s] = obsAlpha * obsScratch[s] + (1 - obsAlpha) * obsSmooth[s];
                    }
                }
                return obsSmooth;
            });
            for (int i = segStart; i < Math.Min(segStart + segFrames, nFrames); i++)
            {
                statePath[i] = segPath[i - from];
            }
            progress?.Report(0.8 + 0.17 * (++segIndex) / segCount);
        }
        progress?.Report(0.97);

        var freqs = new double[nFrames];
        for (int i = 0; i < nFrames; i++)
        {
            freqs[i] = hmm.MapStateToFreq(statePath[i], candFrames[i]);
        }

        // 显示级连续性后处理(与实时引擎一致):
        // 短丢帧保持上一音高;以慢速锚点判断异常跳变/滑落(超过约 ±7 半音),
        // 异常时保持锚点(连拒 >1.2s 才接受新音高线),抹平直升直降与"触底"
        double lastF = 0;
        double anchorF = 0;
        int holdLeft = 0;
        int rejectCount = 0;
        const int holdFrames = 12;      // ≈0.28s @ 43fps
        const int maxRejectFrames = 52; // ≈1.2s @ 43fps
        const double anchorAlpha = 0.2;
        for (int i = 0; i < nFrames; i++)
        {
            double f = freqs[i];
            if (f > 0)
            {
                if (anchorF <= 0) anchorF = f;
                double ratio = f / anchorF;
                if (ratio > 1.5 || ratio < 0.667)
                {
                    rejectCount++;
                    if (rejectCount > maxRejectFrames)
                    {
                        // 连拒超过时限:接受新音高线
                        anchorF = f;
                        rejectCount = 0;
                    }
                    else
                    {
                        f = anchorF;
                    }
                }
                else
                {
                    anchorF += anchorAlpha * (f - anchorF);
                    rejectCount = 0;
                }
                lastF = f;
                holdLeft = holdFrames;
                freqs[i] = f;
            }
            else if (lastF > 0 && holdLeft > 0)
            {
                freqs[i] = lastF;
                holdLeft--;
            }
            else
            {
                lastF = 0;
                holdLeft = 0;
            }
        }

        var counts = new Dictionary<int, int>();
        for (int i = 0; i < nFrames; i++)
        {
            double f = freqs[i];
            if (f > 0)
            {
                int midi = NoteNames.FrequencyToMidiNote(f);
                counts[midi] = counts.GetValueOrDefault(midi) + 1;
            }
        }

        result.WavePeaks = peaks.ToArray();
        result.PitchFreqs = freqs;

        int voiced = 0;
        foreach (var kv in counts)
        {
            result.NoteCounts[kv.Key] = kv.Value;
            voiced += kv.Value;
            if (kv.Key < result.MinMidi) result.MinMidi = kv.Key;
            if (kv.Key > result.MaxMidi) result.MaxMidi = kv.Key;
        }
        result.VoicedRatio = freqs.Length > 0 ? voiced / (double)freqs.Length : 0;

        progress?.Report(1.0);
        return result;
    }

    internal static WaveStream OpenReader(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".wav" or ".aiff" or ".aif")
        {
            return new AudioFileReader(path); // 原生解码,不依赖 Media Foundation
        }
        try
        {
            return new MediaFoundationReader(path);
        }
        catch
        {
            return new AudioFileReader(path); // 兜底
        }
    }
}
