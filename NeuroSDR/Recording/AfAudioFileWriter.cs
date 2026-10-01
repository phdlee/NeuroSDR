using System.Runtime.InteropServices;
using System.Text;

namespace NeuroSDR.Recording;

internal static class AfAudioFileWriter
{
    public static void WriteWav(string path, int sampleRate, ReadOnlySpan<short> pcm)
    {
        File.WriteAllBytes(path, ToWavBytes(sampleRate, pcm));
    }

    public static byte[] ToWavBytes(int sampleRate, ReadOnlySpan<short> pcm)
    {
        var dataBytes = pcm.Length * 2;
        using var stream = new MemoryStream(44 + dataBytes);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36u + (uint)dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((uint)sampleRate);
        writer.Write((uint)(sampleRate * 2));
        writer.Write((ushort)2);
        writer.Write((ushort)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write((uint)dataBytes);
        foreach (var sample in pcm) writer.Write(sample);
        return stream.ToArray();
    }

    public static void WriteMp3(string path, int sampleRate, short[] pcm)
    {
        if (!MediaFoundationMp3.TryWrite(path, sampleRate, pcm, out var error))
            throw new InvalidOperationException(error ?? "The MP3 encoder is unavailable.");
    }
}

internal static class MediaFoundationMp3
{
    private const int MfVersion = 0x20070;
    private static readonly Guid MfMediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfAudioFormatPcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfAudioFormatMp3 = new("00000055-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfMtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MfMtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MfMtAudioNumChannels = new("37e48bf3-690e-4c4c-ad3f-2f74b27adb80");
    private static readonly Guid MfMtAudioSamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid MfMtAudioBitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid MfMtAudioBlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid MfMtAudioAvgBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");

    public static bool TryWrite(string path, int sampleRate, short[] pcm, out string? error)
    {
        error = null;
        var started = false;
        try
        {
            var hr = MFStartup(MfVersion, 0);
            if (hr < 0) { error = $"MFStartup 0x{hr:X8}"; return false; }
            started = true;
            hr = MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out var writerUnk);
            if (hr < 0) { error = $"SinkWriter 0x{hr:X8}"; return false; }
            var writer = (IMFSinkWriter)Marshal.GetObjectForIUnknown(writerUnk);
            Marshal.Release(writerUnk);
            try
            {
                var output = CreateAudioType(MfAudioFormatMp3, sampleRate, 1, 16, sampleRate * 16000 / 8 / sampleRate);
                SetUint32(output, MfMtAudioAvgBytesPerSecond, 16_000);
                hr = writer.AddStream(output, out var streamIndex);
                Marshal.ReleaseComObject(output);
                if (hr < 0) { error = $"AddStream 0x{hr:X8}"; return false; }

                var input = CreateAudioType(MfAudioFormatPcm, sampleRate, 1, 16, sampleRate * 2);
                hr = writer.SetInputMediaType(streamIndex, input, IntPtr.Zero);
                Marshal.ReleaseComObject(input);
                if (hr < 0) { error = $"SetInputMediaType 0x{hr:X8}"; return false; }
                hr = writer.BeginWriting();
                if (hr < 0) { error = $"BeginWriting 0x{hr:X8}"; return false; }

                var offset = 0;
                long time = 0;
                var chunk = Math.Max(sampleRate, 1);
                while (offset < pcm.Length)
                {
                    var count = Math.Min(chunk, pcm.Length - offset);
                    var bytes = count * 2;
                    hr = MFCreateMemoryBuffer(bytes, out var bufferUnk);
                    if (hr < 0) { error = $"MemoryBuffer 0x{hr:X8}"; return false; }
                    var buffer = (IMFMediaBuffer)Marshal.GetObjectForIUnknown(bufferUnk);
                    Marshal.Release(bufferUnk);
                    try
                    {
                        hr = buffer.Lock(out var data, out _, out _);
                        if (hr < 0) { error = $"Lock 0x{hr:X8}"; return false; }
                        Marshal.Copy(pcm, offset, data, count);
                        buffer.Unlock();
                        buffer.SetCurrentLength(bytes);
                        hr = MFCreateSample(out var sampleUnk);
                        if (hr < 0) { error = $"Sample 0x{hr:X8}"; return false; }
                        var sample = (IMFSample)Marshal.GetObjectForIUnknown(sampleUnk);
                        Marshal.Release(sampleUnk);
                        try
                        {
                            sample.AddBuffer(buffer);
                            sample.SetSampleTime(time);
                            var duration = count * 10_000_000L / sampleRate;
                            sample.SetSampleDuration(duration);
                            hr = writer.WriteSample(streamIndex, sample);
                            if (hr < 0) { error = $"WriteSample 0x{hr:X8}"; return false; }
                            time += duration;
                        }
                        finally { Marshal.ReleaseComObject(sample); }
                    }
                    finally { Marshal.ReleaseComObject(buffer); }
                    offset += count;
                }

                hr = writer.Finalize();
                if (hr < 0) { error = $"Finalize 0x{hr:X8}"; return false; }
                return true;
            }
            finally { Marshal.ReleaseComObject(writer); }
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return false;
        }
        finally
        {
            if (started) MFShutdown();
        }
    }

    private static object CreateAudioType(Guid subtype, int sampleRate, int channels, int bits, int avgBytes)
    {
        var hr = MFCreateMediaType(out var unk);
        if (hr < 0) throw new InvalidOperationException($"MFCreateMediaType 0x{hr:X8}");
        var type = Marshal.GetObjectForIUnknown(unk);
        Marshal.Release(unk);
        var attrs = (IMFAttributes)type;
        SetGuid(attrs, MfMtMajorType, MfMediaTypeAudio);
        SetGuid(attrs, MfMtSubtype, subtype);
        SetUint32(attrs, MfMtAudioNumChannels, (uint)channels);
        SetUint32(attrs, MfMtAudioSamplesPerSecond, (uint)sampleRate);
        SetUint32(attrs, MfMtAudioBitsPerSample, (uint)bits);
        SetUint32(attrs, MfMtAudioBlockAlignment, (uint)(channels * bits / 8));
        SetUint32(attrs, MfMtAudioAvgBytesPerSecond, (uint)avgBytes);
        return type;
    }

    private static void SetGuid(IMFAttributes attrs, Guid key, Guid value)
    {
        var hr = attrs.SetGUID(ref key, ref value);
        if (hr < 0) throw new InvalidOperationException($"SetGUID 0x{hr:X8}");
    }

    private static void SetUint32(object attrs, Guid key, uint value)
    {
        var hr = ((IMFAttributes)attrs).SetUINT32(ref key, value);
        if (hr < 0) throw new InvalidOperationException($"SetUINT32 0x{hr:X8}");
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(out IntPtr ppMFType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMemoryBuffer(int cbMaxLength, out IntPtr ppBuffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateSample(out IntPtr ppIMFSample);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFCreateSinkWriterFromURL(string pwszOutputURL, IntPtr pByteStream, IntPtr pAttributes,
        out IntPtr ppSinkWriter);

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        int GetItem(ref Guid guidKey, IntPtr pValue);
        int GetItemType(ref Guid guidKey, out int pType);
        int CompareItem(IntPtr a, IntPtr b, out int c);
        int Compare(IntPtr a, int b, out int c);
        int GetUINT32(ref Guid guidKey, out uint punValue);
        int GetUINT64(ref Guid guidKey, out ulong punValue);
        int GetDouble(ref Guid guidKey, out double pfValue);
        int GetGUID(ref Guid guidKey, out Guid pguidValue);
        int GetStringLength(ref Guid guidKey, out int pcchLength);
        int GetString(ref Guid guidKey, IntPtr pwszValue, int cchBufSize, IntPtr pcchLength);
        int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out int pcchLength);
        int GetBlobSize(ref Guid guidKey, out int pcbBlobSize);
        int GetBlob(ref Guid guidKey, IntPtr pBuf, int cbBufSize, IntPtr pcbBlobSize);
        int GetAllocatedBlob(ref Guid guidKey, out IntPtr ip, out int pcbSize);
        int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        int SetItem(ref Guid guidKey, IntPtr value);
        int DeleteItem(ref Guid guidKey);
        int DeleteAllItems();
        int SetUINT32(ref Guid guidKey, uint unValue);
        int SetUINT64(ref Guid guidKey, ulong unValue);
        int SetDouble(ref Guid guidKey, double fValue);
        int SetGUID(ref Guid guidKey, ref Guid guidValue);
        int SetString(ref Guid guidKey, string wszValue);
        int SetBlob(ref Guid guidKey, IntPtr pBuf, int cbBufSize);
        int SetUnknown(ref Guid guidKey, IntPtr pUnknown);
        int LockStore();
        int UnlockStore();
        int GetCount(out int pcItems);
        int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
        int CopyAllItems(IMFAttributes pDest);
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSinkWriter
    {
        int AddStream(object pTargetMediaType, out int pdwStreamIndex);
        int SetInputMediaType(int dwStreamIndex, object pInputMediaType, IntPtr pEncodingParameters);
        int BeginWriting();
        int WriteSample(int dwStreamIndex, IMFSample pSample);
        int SendStreamTick(int dwStreamIndex, long llTimestamp);
        int PlaceMarker(int dwStreamIndex, IntPtr pvContext);
        int NotifyEndOfSegment(int dwStreamIndex);
        int Flush(int dwStreamIndex);
        int Finalize();
        int GetServiceForStream(int dwStreamIndex, ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
        int GetStatistics(int dwStreamIndex, IntPtr pStats);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
        int Unlock();
        int GetCurrentLength(out int pcbCurrentLength);
        int SetCurrentLength(int cbCurrentLength);
        int GetMaxLength(out int pcbMaxLength);
    }

    [ComImport, Guid("c40a00f2-b58f-4f4e-a59e-ae0b7e0b5c04"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        int GetItem(ref Guid guidKey, IntPtr pValue);
        int GetItemType(ref Guid guidKey, out int pType);
        int CompareItem(IntPtr a, IntPtr b, out int c);
        int Compare(IntPtr a, int b, out int c);
        int GetUINT32(ref Guid guidKey, out uint punValue);
        int GetUINT64(ref Guid guidKey, out ulong punValue);
        int GetDouble(ref Guid guidKey, out double pfValue);
        int GetGUID(ref Guid guidKey, out Guid pguidValue);
        int GetStringLength(ref Guid guidKey, out int pcchLength);
        int GetString(ref Guid guidKey, IntPtr pwszValue, int cchBufSize, IntPtr pcchLength);
        int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out int pcchLength);
        int GetBlobSize(ref Guid guidKey, out int pcbBlobSize);
        int GetBlob(ref Guid guidKey, IntPtr pBuf, int cbBufSize, IntPtr pcbBlobSize);
        int GetAllocatedBlob(ref Guid guidKey, out IntPtr ip, out int pcbSize);
        int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
        int SetItem(ref Guid guidKey, IntPtr value);
        int DeleteItem(ref Guid guidKey);
        int DeleteAllItems();
        int SetUINT32(ref Guid guidKey, uint unValue);
        int SetUINT64(ref Guid guidKey, ulong unValue);
        int SetDouble(ref Guid guidKey, double fValue);
        int SetGUID(ref Guid guidKey, ref Guid guidValue);
        int SetString(ref Guid guidKey, string wszValue);
        int SetBlob(ref Guid guidKey, IntPtr pBuf, int cbBufSize);
        int SetUnknown(ref Guid guidKey, IntPtr pUnknown);
        int LockStore();
        int UnlockStore();
        int GetCount(out int pcItems);
        int GetItemByIndex(int unIndex, out Guid pguidKey, IntPtr pValue);
        int CopyAllItems(IMFAttributes pDest);
        int GetFlags(out int pdwSampleFlags);
        int SetFlags(int dwSampleFlags);
        int GetSampleTime(out long phnsSampleTime);
        int SetSampleTime(long hnsSampleTime);
        int GetSampleDuration(out long phnsSampleDuration);
        int SetSampleDuration(long hnsSampleDuration);
        int GetBufferCount(out int pdwBufferCount);
        int GetBufferByIndex(int dwIndex, out IMFMediaBuffer ppBuffer);
        int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
        int AddBuffer(IMFMediaBuffer pBuffer);
        int RemoveBufferByIndex(int dwIndex);
        int RemoveAllBuffers();
        int GetTotalLength(out int pcbTotalLength);
        int CopyToBuffer(IMFMediaBuffer pBuffer);
    }
}
