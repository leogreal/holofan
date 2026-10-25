namespace HoloFan;

/// <summary>
/// O formato .bin do ventilador holográfico, descoberto a partir dos arquivos que vieram no cartão
/// (detalhes em docs/FORMAT.md).
///
/// arquivo = cabeçalho + quadros (28.600 bytes cada, 20 por segundo, em múltiplo de 20) + 100.000 bytes zerados
/// quadro  = 300 linhas de imagem (90 bytes cada) + 1.600 bytes de áudio
/// linha   = uma posição da hélice: 5 planos de bits (o mais significativo primeiro),
///           cada um com 48 LEDs (do de fora para o do centro) de 3 bits, na ordem B, G, R
/// áudio   = PCM 8 bits sem sinal, mono, 32.000 Hz
/// </summary>
public static class HoloFormat
{
    public const int Lines = 300;
    public const int Leds = 48;
    public const int Planes = 5;
    public const int LineBytes = Planes * Leds * 3 / 8;            // 90
    public const int PlaneBytes = LineBytes / Planes;              // 18
    public const int ImageBytes = Lines * LineBytes;               // 27.000
    public const int AudioBytes = 1600;
    public const int FrameBytes = ImageBytes + AudioBytes;         // 28.600
    public const int Fps = 20;
    public const int SampleRate = AudioBytes * Fps;                // 32.000
    public const int FramesMultiple = 20;
    public const int TrailerBytes = 100_000;
    public const byte Silence = 0x80;

    /// <summary>Os originais têm o nome cortado em 10 bytes (UTF-8); seguimos igual por garantia.</summary>
    public const int MaxNameBytes = 10;

    /// <summary>
    /// Cabeçalho copiado do Sereiata.bin original: "ee 37", dois bytes e uma sequência fixa embaralhada com XOR.
    /// Cada original tem um tamanho diferente (124 a 896 bytes) e não achei uma regra para ele, então
    /// usamos um que o aparelho já tocou, inteiro.
    /// </summary>
    public static readonly byte[] Header = Convert.FromBase64String(
        "7jci9Dc0NvQyNDP08TTw9Dg0OfT7NPr0/jT/9D00PPQsNC307zTu9Oo06/QpNCj04DTh9CM0IvQm" +
        "NCf05TTk9AQ0BfTHNMb0wjTD9AE0APTINMn0CzQK9A40D/TNNMz03DTd9B80HvQaNBv02TTY9BA0" +
        "EfTTNNL01jTX9BU0FPRUNFX0lzSW9JI0k/RRNFD0mDSZ9Fs0WvReNF/0nTSc9Iw0jfRPNE70SjRL" +
        "9Ik0iPRANEH0gzSC9IY0h/RFNET0pDSl9Gc0ZvRiNGP0oTSg9Gg0afSrNKr0rjSv9G00bPR8NH30" +
        "vzS+9Lo0u/R5NHj0sDSx9HM0cvR2NHf0tTS0tHS1tHd0drRydHO0sXSwtHh0ebS7dLq0vnS/tH10" +
        "fLRsdG20rw==");

    public static long FileSize(int frames) => Header.Length + (long)PaddedFrames(frames) * FrameBytes + TrailerBytes;

    public static int PaddedFrames(int frames)
    {
        if (frames < 1) frames = 1;
        return (frames + FramesMultiple - 1) / FramesMultiple * FramesMultiple;
    }

    /// <summary>
    /// Grava a imagem de um quadro. <paramref name="rgb5"/> tem Lines × Leds × 3 valores de 0 a 31 (R, G, B),
    /// linha por linha, LED 0 = o de fora.
    /// </summary>
    public static void PackImage(ReadOnlySpan<byte> rgb5, Span<byte> frame)
    {
        frame[..ImageBytes].Clear();
        for (int line = 0; line < Lines; line++)
        {
            int px = line * Leds * 3;
            for (int plane = 0; plane < Planes; plane++)
            {
                int bit = 1 << (Planes - 1 - plane);
                Span<byte> dst = frame.Slice(line * LineBytes + plane * PlaneBytes, PlaneBytes);
                for (int led = 0; led < Leds; led++)
                {
                    int p = px + led * 3;
                    int pos = led * 3;
                    // Ordem gravada: B, G, R.
                    if ((rgb5[p + 2] & bit) != 0) dst[pos >> 3] |= (byte)(0x80 >> (pos & 7));
                    pos++;
                    if ((rgb5[p + 1] & bit) != 0) dst[pos >> 3] |= (byte)(0x80 >> (pos & 7));
                    pos++;
                    if ((rgb5[p] & bit) != 0) dst[pos >> 3] |= (byte)(0x80 >> (pos & 7));
                }
            }
        }
    }

    /// <summary>
    /// Nome no cartão a partir do nome do vídeo: sem extensão, até <see cref="MaxNameBytes"/> bytes,
    /// sem cortar um caractere acentuado no meio e sem espaço ou ponto no fim.
    /// </summary>
    public static string CardName(string videoName)
    {
        string name = Path.GetFileNameWithoutExtension(videoName);
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
        return FitName(name);
    }

    public static string FitName(string name)
    {
        var sb = new System.Text.StringBuilder();
        int bytes = 0;
        foreach (var rune in name.Trim().EnumerateRunes())
        {
            int n = rune.Utf8SequenceLength;
            if (bytes + n > MaxNameBytes) break;
            sb.Append(rune.ToString());
            bytes += n;
        }
        string s = sb.ToString().TrimEnd(' ', '.');
        return s.Length == 0 ? "video" : s;
    }

    public static int NameBytes(string name) => System.Text.Encoding.UTF8.GetByteCount(name);
}
