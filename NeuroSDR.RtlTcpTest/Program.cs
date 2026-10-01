using System.Net;
using System.Net.Sockets;

namespace NeuroSDR.RtlTcpTest;

/// <summary>
/// Minimal Osmocom-compatible rtl_tcp stand-in for testing NeuroSDR's rtl_tcp client.
/// Serves CU8 IQ (unsigned interleaved) after an "RTL0" dongle header.
/// </summary>
internal static class Program
{
    private static int _port = 1234;
    private static int _sampleRate = 2_048_000;
    private static long _frequency = 100_000_000;
    private static int _gainTenths;
    private static bool _manualGain = true;

    private static int Main(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "-p" or "--port" && i + 1 < args.Length && int.TryParse(args[++i], out var p))
                _port = Math.Clamp(p, 1, 65535);
            else if (args[i] is "-f" or "--freq" && i + 1 < args.Length && long.TryParse(args[++i], out var f))
                _frequency = Math.Clamp(f, 1_000_000, 1_800_000_000);
            else if (args[i] is "-s" or "--rate" && i + 1 < args.Length && int.TryParse(args[++i], out var r))
                _sampleRate = Math.Clamp(r, 225_001, 3_200_000);
            else if (args[i] is "-h" or "--help")
            {
                PrintHelp();
                return 0;
            }
        }

        Console.WriteLine("NeuroSDR · rtl_tcp test server");
        Console.WriteLine($"  listening  127.0.0.1:{_port}");
        Console.WriteLine($"  frequency  {_frequency} Hz");
        Console.WriteLine($"  sampleRate {_sampleRate} Hz");
        Console.WriteLine();
        Console.WriteLine("In NeuroSDR: select source \"rtl_tcp (127.0.0.1:1234)\"");
        Console.WriteLine("(or set SETUP → rtl_tcp endpoint), then RX START.");
        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();

        using var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { listener.Stop(); } catch { }
        };

        while (true)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] client connected · {client.Client.RemoteEndPoint}");
            try
            {
                ServeClient(client);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] client ended: {ex.GetBaseException().Message}");
            }
            finally
            {
                try { client.Dispose(); } catch { }
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] client disconnected");
            }
        }

        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            NeuroSDR.RtlTcpTest — synthetic rtl_tcp IQ server

              NeuroSDR.RtlTcpTest.exe [-p port] [-f freqHz] [-s sampleRate]

            Defaults: port 1234, 100 MHz, 2.048 MSPS, CU8 IQ with a tone near DC.
            """);
    }

    private static void ServeClient(TcpClient client)
    {
        client.NoDelay = true;
        client.SendBufferSize = 1 << 20;
        using var stream = client.GetStream();

        // Dongle info header: "RTL0" + tuner type + tuner gain count (big-endian u32 each after magic).
        Span<byte> header = stackalloc byte[12];
        header[0] = (byte)'R';
        header[1] = (byte)'T';
        header[2] = (byte)'L';
        header[3] = (byte)'0';
        WriteU32Be(header[4..], 5); // R820T
        WriteU32Be(header[8..], 29);
        stream.Write(header);

        using var cts = new CancellationTokenSource();
        var commandTask = Task.Run(() => ReadCommands(stream, cts.Token), cts.Token);
        try
        {
            StreamIq(stream, cts.Token);
        }
        finally
        {
            cts.Cancel();
            try { commandTask.Wait(500); } catch { }
        }
    }

    private static void ReadCommands(NetworkStream stream, CancellationToken token)
    {
        var packet = new byte[5];
        while (!token.IsCancellationRequested)
        {
            var offset = 0;
            while (offset < 5)
            {
                var n = stream.Read(packet, offset, 5 - offset);
                if (n <= 0) return;
                offset += n;
            }

            var cmd = packet[0];
            var param = ((uint)packet[1] << 24) | ((uint)packet[2] << 16) | ((uint)packet[3] << 8) | packet[4];
            switch (cmd)
            {
                case 0x01:
                    _frequency = Math.Clamp(param, 1_000_000u, 1_800_000_000u);
                    Console.WriteLine($"  set_freq {_frequency}");
                    break;
                case 0x02:
                    _sampleRate = (int)Math.Clamp(param, 225_001u, 3_200_000u);
                    Console.WriteLine($"  set_sample_rate {_sampleRate}");
                    break;
                case 0x03:
                    _manualGain = param != 0;
                    Console.WriteLine($"  set_gain_mode {(_manualGain ? "manual" : "auto")}");
                    break;
                case 0x04:
                    _gainTenths = (int)param;
                    Console.WriteLine($"  set_gain {_gainTenths / 10.0:0.0} dB");
                    break;
                case 0x05:
                    Console.WriteLine($"  set_freq_correction {param}");
                    break;
                default:
                    Console.WriteLine($"  cmd 0x{cmd:X2} param={param}");
                    break;
            }
        }
    }

    private static void StreamIq(NetworkStream stream, CancellationToken token)
    {
        // ~16k complex samples per write ≈ 32 KiB CU8.
        const int complexSamples = 16_384;
        var buffer = new byte[complexSamples * 2];
        var phase = 0.0;
        var lastRate = _sampleRate;
        var lastFreq = _frequency;

        while (!token.IsCancellationRequested)
        {
            var rate = Volatile.Read(ref _sampleRate);
            var freq = Interlocked.Read(ref _frequency);
            if (rate != lastRate || freq != lastFreq)
            {
                lastRate = rate;
                lastFreq = freq;
            }

            // Weak tone ~5 kHz offset from DC so spectrum shows energy; amplitude scales with gain.
            var toneHz = 5_000.0;
            var gain = 0.15 + Math.Clamp(_gainTenths, 0, 496) / 496.0 * 0.55;
            var dPhi = 2.0 * Math.PI * toneHz / rate;

            for (var i = 0; i < complexSamples; i++)
            {
                var iSample = Math.Cos(phase) * gain;
                var qSample = Math.Sin(phase) * gain;
                // tiny noise
                iSample += (Random.Shared.NextDouble() - 0.5) * 0.02;
                qSample += (Random.Shared.NextDouble() - 0.5) * 0.02;
                buffer[i * 2] = FloatToCu8(iSample);
                buffer[i * 2 + 1] = FloatToCu8(qSample);
                phase += dPhi;
                if (phase > Math.PI * 2) phase -= Math.PI * 2;
            }

            try
            {
                stream.Write(buffer, 0, buffer.Length);
            }
            catch (IOException) { return; }
            catch (ObjectDisposedException) { return; }

            // Pace roughly to realtime (block duration).
            var blockMs = complexSamples * 1000.0 / Math.Max(1, rate);
            var sleep = (int)Math.Clamp(blockMs - 1, 1, 50);
            Thread.Sleep(sleep);
        }
    }

    private static byte FloatToCu8(double v)
    {
        var scaled = (v * 127.0) + 127.5;
        if (scaled < 0) return 0;
        if (scaled > 255) return 255;
        return (byte)scaled;
    }

    private static void WriteU32Be(Span<byte> dest, uint value)
    {
        dest[0] = (byte)(value >> 24);
        dest[1] = (byte)(value >> 16);
        dest[2] = (byte)(value >> 8);
        dest[3] = (byte)value;
    }
}
