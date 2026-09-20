using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PitchBall.Audio;

/// <summary>
/// RMVPE(人声专用音高估计,RVC 社区模型)的 ONNX 推理引擎。
///
/// 与前端的 pYIN 互补:pYIN 的判据是"周期性",同伴奏更强/同频段时会锁到伴奏;
/// RMVPE 的判据是从大量人声数据学到的音色特征,伴奏更响时仍能锁人声。
/// 实测(142 分钟现场录音):漏音事件少 2~3 倍;
/// 41:10-41:12 处 pYIN 与手写 YIN 都报 115-263Hz,本模型报 C5→D5(521→597Hz) 置信 0.6-0.9。
///
/// 前端参数(逐项对齐上游实现,不可随意改动):
///   采样率 16000 / 窗长 1024 / 跳距 160(10ms) / 128 个 mel(HTK,fmin 30,fmax 8000)
///   / 周期 Hann / 居中 reflect 补零 512 / log(clip(x,1e-5))
///   模型输入 [1,128,T],时间维需补零到 32 的倍数;输出 [1,T,360] 显著度。
///   频率 bin:cents = 20*i + 1997.3794084376191,f0 = 10*2^(cents/1200);
///   每帧取显著度最大的 bin 及其 ±4 箱做加权平均(±4 箱越界处权重为 0)。
/// </summary>
public sealed class RmvpePitchEngine : IDisposable
{
    public const int SampleRate = 16000;
    private const int WinLength = 1024;
    private const int HopLength = 160;
    private const int NMels = 128;
    private const int NBins = WinLength / 2 + 1;      // 513
    private const double MelFMin = 30, MelFMax = 8000;
    private const double LogClamp = 1e-5;
    private const int NClass = 360;
    private const double CentsOffset = 1997.3794084376191;

    /// <summary>模型是否可用(资源缺失时为 false,调用方应隐藏该算法)。</summary>
    public static bool Available => ModelBytes != null;

    private static byte[]? _modelBytesCache;
    private static float[]? _melBasisCache;
    private static readonly object LoadLock = new();

    private static byte[]? ModelBytes
    {
        get
        {
            EnsureAssets();
            return _modelBytesCache;
        }
    }

    private static float[] MelBasis
    {
        get
        {
            EnsureAssets();
            return _melBasisCache!;
        }
    }

    /// <summary>把内嵌的模型/mel 滤波器组落到 %APPDATA%\PitchBall\ 一次,之后从文件加载
    /// (避免长期占用内存,也便于用户自行替换)。</summary>
    private static void EnsureAssets()
    {
        if (_modelBytesCache != null) return;
        lock (LoadLock)
        {
            if (_modelBytesCache != null) return;
            var asm = typeof(RmvpePitchEngine).Assembly;
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PitchBall");
            Directory.CreateDirectory(dir);

            // ONNX 模型:优先用缓存文件(按大小校验)
            var asmName = asm.GetName().Name;
            using var ms = asm.GetManifestResourceStream($"{asmName}.Assets.rmvpe_int8.onnx");
            if (ms == null) return;   // 构建时未内嵌模型 → 算法不可用
            string modelPath = Path.Combine(dir, "rmvpe_int8.onnx");
            if (!File.Exists(modelPath) || new FileInfo(modelPath).Length != ms.Length)
            {
                using var fs = File.Create(modelPath);
                ms.CopyTo(fs);
            }
            _modelBytesCache = File.ReadAllBytes(modelPath);

            using var mb = asm.GetManifestResourceStream($"{asmName}.Assets.rmvpe_mel_basis.f32");
            if (mb != null)
            {
                var buf = new byte[mb.Length];
                int off = 0, r;
                while (off < buf.Length && (r = mb.Read(buf, off, buf.Length - off)) > 0) off += r;
                _melBasisCache = new float[NMels * NBins];
                Buffer.BlockCopy(buf, 0, _melBasisCache, 0, buf.Length);
            }
        }
    }

    private readonly InferenceSession? _session;
    private readonly string _inputName = "input";

    public RmvpePitchEngine()
    {
        var bytes = ModelBytes;
        if (bytes == null) return;
        var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        opts.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
        _session = new InferenceSession(bytes, opts);
        _inputName = _session.InputMetadata.Keys.First();
    }

    /// <summary>对 16kHz 单声道音频做一次推理,返回每 10ms 一帧的基频(Hz,0=未检出)与置信度。</summary>
    public (double[] Freq, double[] Conf) Infer(float[] audio16k)
    {
        if (_session == null || audio16k.Length < WinLength) return ([], []);

        // ---- 居中 reflect 补零 + STFT ----
        int pad = WinLength / 2;
        var padded = new float[audio16k.Length + 2 * pad];
        for (int i = 0; i < pad; i++) padded[i] = audio16k[pad - i];                       // 前端镜像
        Array.Copy(audio16k, 0, padded, pad, audio16k.Length);
        for (int i = 0; i < pad; i++) padded[pad + audio16k.Length + i] = audio16k[audio16k.Length - 2 - i];

        int frames = (padded.Length - WinLength) / HopLength + 1;
        var window = new float[WinLength];
        for (int i = 0; i < WinLength; i++)
            window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / WinLength));         // 周期 Hann

        var mel = new float[NMels * frames];
        var re = new double[WinLength];
        var im = new double[WinLength];
        var mag = new double[NBins];
        var basis = MelBasis;
        for (int f = 0; f < frames; f++)
        {
            int start = f * HopLength;
            for (int i = 0; i < WinLength; i++) { re[i] = padded[start + i] * window[i]; im[i] = 0; }
            Fft1024(re, im);
            for (int b = 0; b < NBins; b++) mag[b] = Math.Sqrt(re[b] * re[b] + im[b] * im[b]);
            for (int m = 0; m < NMels; m++)
            {
                double acc = 0;
                int row = m * NBins;
                for (int b = 0; b < NBins; b++)
                {
                    float w = basis[row + b];
                    if (w != 0) acc += w * mag[b];
                }
                mel[m * frames + f] = acc > LogClamp ? (float)Math.Log(acc) : (float)Math.Log(LogClamp);
            }
        }

        // ---- 时间维补零到 32 的倍数后推理 ----
        int nPad = 32 * ((frames - 1) / 32 + 1) - frames;
        int total = frames + nPad;
        var input = new DenseTensor<float>(new[] { 1, NMels, total });
        var span = input.Buffer.Span;
        for (int m = 0; m < NMels; m++)
            for (int f = 0; f < frames; f++)
                span[m * total + f] = mel[m * frames + f];
        // 补零区保持 0(与上游 np.pad(...,0) 一致)

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
        var outT = results.First().AsTensor<float>();
        var dims = outT.Dimensions.ToArray();          // [1, total, 360]

        var freq = new double[frames];
        var conf = new double[frames];
        var sal = new float[NClass];
        for (int f = 0; f < frames; f++)
        {
            double best = double.MinValue; int center = 0;
            for (int b = 0; b < NClass; b++)
            {
                float v = outT[0, f, b];
                sal[b] = v;
                if (v > best) { best = v; center = b; }
            }
            // 9 箱加权平均(越界权重 0)
            double num = 0, den = 0;
            for (int k = -4; k <= 4; k++)
            {
                int idx = center + k;
                if (idx < 0 || idx >= NClass) continue;
                double s = sal[idx];
                if (s <= 0) continue;
                num += s * (20.0 * idx + CentsOffset);
                den += s;
            }
            conf[f] = best;
            freq[f] = den > 0 ? 10.0 * Math.Pow(2, num / den / 1200.0) : 0.0;
        }
        return (freq, conf);
    }

    // 1024 点迭代基-2 FFT(与 PyinFft 同构,规模不同)
    private static readonly int[] Rev1024 = BuildRev();
    private static readonly double[] CosT = BuildCos();
    private static readonly double[] SinT = BuildSin();

    private static int[] BuildRev()
    {
        var rev = new int[WinLength];
        for (int i = 0; i < WinLength; i++)
        {
            int r = 0;
            for (int b = 0; b < 10; b++) if ((i & (1 << b)) != 0) r |= 1 << (9 - b);
            rev[i] = r;
        }
        return rev;
    }

    private static double[] BuildCos()
    {
        var a = new double[WinLength / 2];
        for (int k = 0; k < a.Length; k++) a[k] = Math.Cos(2 * Math.PI * k / WinLength);
        return a;
    }

    private static double[] BuildSin()
    {
        var a = new double[WinLength / 2];
        for (int k = 0; k < a.Length; k++) a[k] = Math.Sin(2 * Math.PI * k / WinLength);
        return a;
    }

    private static void Fft1024(double[] re, double[] im)
    {
        for (int i = 0; i < WinLength; i++)
        {
            int j = Rev1024[i];
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= WinLength; len <<= 1)
        {
            int half = len >> 1, step = WinLength / len;
            for (int i = 0; i < WinLength; i += len)
            {
                for (int j = 0; j < half; j++)
                {
                    int k = j * step;
                    double wr = CosT[k], wi = SinT[k];
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

    public void Dispose() => _session?.Dispose();
}
