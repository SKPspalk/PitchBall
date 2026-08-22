using System.Buffers;
using System.Runtime.InteropServices;

namespace PitchBall.Audio;

/// <summary>把 PCM / IEEE 浮点交错采样转换为单声道浮点。</summary>
internal static class MonoConverter
{
    public static float[] FromBytes(byte[] bytes, int channels, int bits, bool isFloat)
    {
        int samplesPerChannel = bytes.Length / Math.Max(1, bits / 8);
        int frames = samplesPerChannel / Math.Max(1, channels);
        var result = new float[frames];

        if (isFloat && bits == 32)
        {
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    sum += BitConverter.ToSingle(bytes, (i * channels + c) * 4);
                }
                result[i] = sum / channels;
            }
        }
        else if (!isFloat && bits == 16)
        {
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    short v = BitConverter.ToInt16(bytes, (i * channels + c) * 2);
                    sum += v / 32768f;
                }
                result[i] = sum / channels;
            }
        }
        else if (!isFloat && bits == 32)
        {
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    int v = BitConverter.ToInt32(bytes, (i * channels + c) * 4);
                    sum += v / 2147483648f;
                }
                result[i] = sum / channels;
            }
        }
        else if (!isFloat && bits == 24)
        {
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    int off = (i * channels + c) * 3;
                    int v = (bytes[off] | (bytes[off + 1] << 8) | (bytes[off + 2] << 16)) << 8;
                    sum += v / 2147483648f;
                }
                result[i] = sum / channels;
            }
        }
        return result;
    }

    public static float[] FromIntPtr(IntPtr data, uint frames, int channels, int bits, bool isFloat)
    {
        int byteCount = (int)frames * channels * Math.Max(1, bits / 8);
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Marshal.Copy(data, rented, 0, byteCount);
            return FromBytes(rented.AsSpan(0, byteCount).ToArray(), channels, bits, isFloat);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
