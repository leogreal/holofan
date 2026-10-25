using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HoloFan;

public enum Framing
{
    /// <summary>Corta o vídeo num quadrado do meio e o círculo fica cheio (como os originais).</summary>
    Fill,
    /// <summary>O vídeo inteiro cabe dentro do círculo, com preto em volta.</summary>
    Whole,
}

public sealed record ConvertOptions(Framing Framing, bool Vivid);

public sealed record VideoInfo(TimeSpan? Duration, bool HasVideo, bool HasAudio, string? Error);

/// <summary>Quem lê os vídeos é o ffmpeg (qualquer formato que ele abrir serve).</summary>
public static class Ffmpeg
{
    /// <summary>Procura o ffmpeg ao lado do programa e depois no PATH.</summary>
    public static string? Find()
    {
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string local = Path.Combine(AppContext.BaseDirectory, exe);
        if (File.Exists(local)) return local;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                string p = Path.Combine(dir.Trim(), exe);
                if (dir.Trim().Length > 0 && File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public static async Task<VideoInfo> ProbeAsync(string ffmpeg, string file, CancellationToken ct = default)
    {
        var psi = Start(ffmpeg, "-hide_banner", "-nostdin", "-i", file);
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => Kill(p));
        Task<string> err = p.StandardError.ReadToEndAsync(ct);
        await p.StandardOutput.ReadToEndAsync(ct);
        string text = await err;
        await p.WaitForExitAsync(ct);

        bool video = Regex.IsMatch(text, @"Stream #\d+:\d+.*: Video:");
        bool audio = Regex.IsMatch(text, @"Stream #\d+:\d+.*: Audio:");
        TimeSpan? duration = null;
        var m = Regex.Match(text, @"Duration: (\d+):(\d\d):(\d\d(?:\.\d+)?)");
        if (m.Success)
            duration = TimeSpan.FromHours(int.Parse(m.Groups[1].Value))
                     + TimeSpan.FromMinutes(int.Parse(m.Groups[2].Value))
                     + TimeSpan.FromSeconds(double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        string? error = video ? null : "não é um vídeo que o ffmpeg consiga abrir";
        return new VideoInfo(duration, video, audio, error);
    }

    internal static ProcessStartInfo Start(string ffmpeg, params string[] args)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    internal static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}

public static class VideoConverter
{
    /// <summary>
    /// Converte <paramref name="input"/> no .bin <paramref name="output"/>. Grava primeiro em "output.tmp"
    /// e só troca o nome no fim, então um erro ou o Parar não deixam .bin pela metade no cartão.
    /// </summary>
    /// <param name="progress">Fração pronta, de 0 a 1 (só quando a duração é conhecida).</param>
    public static async Task ConvertAsync(string ffmpeg, string input, string output, ConvertOptions options,
        VideoInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        byte[] audio = info.HasAudio ? await ReadAudioAsync(ffmpeg, input, ct) : [];
        int expected = info.Duration is { } d ? Math.Max(1, (int)Math.Ceiling(d.TotalSeconds * HoloFormat.Fps)) : 0;

        string tmp = output + ".tmp";
        bool done = false;
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                await fs.WriteAsync(HoloFormat.Header, ct);
                int lastPercent = -1;
                int frames = await WriteFramesAsync(ffmpeg, input, options, audio, fs, frames =>
                {
                    if (expected == 0) return;
                    double f = Math.Min(1.0, (double)frames / expected);
                    if ((int)(f * 100) == lastPercent) return;
                    lastPercent = (int)(f * 100);
                    progress?.Report(f);
                }, ct);
                if (frames == 0) throw new InvalidOperationException("o ffmpeg não entregou nenhuma imagem do vídeo");

                // O aparelho trabalha com segundos inteiros: completa com quadros pretos e silêncio.
                byte[] blank = new byte[HoloFormat.FrameBytes];
                blank.AsSpan(HoloFormat.ImageBytes).Fill(HoloFormat.Silence);
                for (int i = frames; i < HoloFormat.PaddedFrames(frames); i++) await fs.WriteAsync(blank, ct);
                await fs.WriteAsync(new byte[HoloFormat.TrailerBytes], ct);
            }
            File.Move(tmp, output);
            done = true;
            progress?.Report(1.0);
        }
        finally
        {
            if (!done) try { File.Delete(tmp); } catch { }
        }
    }

    static async Task<byte[]> ReadAudioAsync(string ffmpeg, string input, CancellationToken ct)
    {
        var psi = Ffmpeg.Start(ffmpeg, "-hide_banner", "-nostdin", "-v", "error", "-i", input,
            "-map", "0:a:0", "-vn", "-ac", "1", "-ar", HoloFormat.SampleRate.ToString(CultureInfo.InvariantCulture),
            "-f", "u8", "pipe:1");
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => Ffmpeg.Kill(p));
        Task<string> err = p.StandardError.ReadToEndAsync(ct);
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        await p.WaitForExitAsync(ct);
        string errText = await err;
        if (p.ExitCode != 0 && ms.Length == 0) throw new InvalidOperationException("som: " + LastLine(errText));
        return ms.ToArray();
    }

    static async Task<int> WriteFramesAsync(string ffmpeg, string input, ConvertOptions options, byte[] audio,
        Stream output, Action<int> onFrame, CancellationToken ct)
    {
        int s = PolarSampler.Size;
        // O primeiro scale corrige vídeos de pixel não quadrado.
        string common = $"fps={HoloFormat.Fps},scale='trunc(iw*sar)':ih,setsar=1,";
        string vf = options.Framing == Framing.Fill
            ? common + $"crop='min(iw,ih)':'min(iw,ih)',scale={s}:{s}:flags=area"
            : common + $"scale='max(2,trunc({s}*iw/hypot(iw,ih)))':'max(2,trunc({s}*ih/hypot(iw,ih)))':flags=area," +
                       $"pad={s}:{s}:'(ow-iw)/2':'(oh-ih)/2':color=black";
        var psi = Ffmpeg.Start(ffmpeg, "-hide_banner", "-nostdin", "-v", "error", "-i", input,
            "-map", "0:v:0", "-an", "-sn", "-vf", vf, "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1");
        using var p = Process.Start(psi)!;
        using var reg = ct.Register(() => Ffmpeg.Kill(p));
        Task<string> err = p.StandardError.ReadToEndAsync(ct);

        var sampler = new PolarSampler(options.Vivid);
        byte[] rgb = new byte[s * s * 3];
        byte[] rgb5 = new byte[HoloFormat.Lines * HoloFormat.Leds * 3];
        byte[] frame = new byte[HoloFormat.FrameBytes];
        Stream src = p.StandardOutput.BaseStream;
        int frames = 0;
        while (await ReadFullAsync(src, rgb, ct))
        {
            BuildFrame(sampler, rgb, rgb5, audio, frames, frame);
            await output.WriteAsync(frame, ct);
            frames++;
            onFrame(frames);
        }
        await p.WaitForExitAsync(ct);
        string errText = await err;
        if (p.ExitCode != 0) throw new InvalidOperationException("imagem: " + LastLine(errText));
        return frames;
    }

    static void BuildFrame(PolarSampler sampler, byte[] rgb, byte[] rgb5, byte[] audio, int index, byte[] frame)
    {
        sampler.Sample(rgb, rgb5);
        HoloFormat.PackImage(rgb5, frame);
        Span<byte> snd = frame.AsSpan(HoloFormat.ImageBytes, HoloFormat.AudioBytes);
        long at = (long)index * HoloFormat.AudioBytes;
        int have = (int)Math.Clamp(audio.Length - at, 0, HoloFormat.AudioBytes);
        if (have > 0) audio.AsSpan((int)at, have).CopyTo(snd);
        snd[have..].Fill(HoloFormat.Silence);
    }

    static async Task<bool> ReadFullAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(got), ct);
            if (n == 0) return false; // fim (um quadro incompleto no fim é descartado)
            got += n;
        }
        return true;
    }

    static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : "o ffmpeg falhou sem dizer por quê";
    }
}
