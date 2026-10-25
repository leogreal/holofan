namespace HoloFan;

/// <summary>
/// Leva um quadro quadrado (RGB, Size × Size) para as 300 posições × 48 LEDs da hélice.
/// Cada LED aceso numa posição pinta um pedaço de anel; a cor dele é a média de Sub × Sub pontos
/// espalhados nesse pedaço. A linha 0 fica às 3 horas e as seguintes vão no sentido anti-horário
/// (é assim que o texto "NETFLIX" dos originais aparece certo); o LED 0 é o de fora.
/// </summary>
public sealed class PolarSampler
{
    public const int Size = 480;
    const int Sub = 4;

    readonly int[] _lut;          // para cada LED de cada linha, Sub × Sub posições no quadro (já × 3)
    readonly byte[] _curve;       // 0..255 -> 0..31, com o realce de cor

    public PolarSampler(bool vivid)
    {
        _lut = new int[HoloFormat.Lines * HoloFormat.Leds * Sub * Sub];
        double radius = Size / 2.0;
        int k = 0;
        for (int line = 0; line < HoloFormat.Lines; line++)
            for (int led = 0; led < HoloFormat.Leds; led++)
                for (int a = 0; a < Sub; a++)
                {
                    double theta = 2 * Math.PI * (line + (a + 0.5) / Sub) / HoloFormat.Lines;
                    for (int b = 0; b < Sub; b++)
                    {
                        double r = (HoloFormat.Leds - 1 - led + (b + 0.5) / Sub) / HoloFormat.Leds * radius;
                        int x = Math.Clamp((int)Math.Floor(radius + r * Math.Cos(theta)), 0, Size - 1);
                        int y = Math.Clamp((int)Math.Floor(radius - r * Math.Sin(theta)), 0, Size - 1);
                        _lut[k++] = (y * Size + x) * 3;
                    }
                }

        // Os originais têm contraste puxado: o vermelho da Netflix (229, 9, 20) está gravado como (30, 0, 0).
        // "Vivas" imita isso (contraste 1,25 em torno do meio); "naturais" só reduz de 8 para 5 bits.
        _curve = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            double c = vivid ? (v - 128) * 1.25 + 128 : v;
            c = Math.Clamp(c, 0, 255);
            _curve[v] = (byte)Math.Round(c * 31 / 255);
        }
    }

    /// <summary>rgb: Size × Size × 3. rgb5: Lines × Leds × 3 (0..31).</summary>
    public void Sample(ReadOnlySpan<byte> rgb, Span<byte> rgb5)
    {
        const int n = Sub * Sub;
        int k = 0;
        for (int cell = 0; cell < HoloFormat.Lines * HoloFormat.Leds; cell++)
        {
            int r = 0, g = 0, b = 0;
            for (int s = 0; s < n; s++)
            {
                int p = _lut[k++];
                r += rgb[p];
                g += rgb[p + 1];
                b += rgb[p + 2];
            }
            rgb5[cell * 3] = _curve[(r + n / 2) / n];
            rgb5[cell * 3 + 1] = _curve[(g + n / 2) / n];
            rgb5[cell * 3 + 2] = _curve[(b + n / 2) / n];
        }
    }
}
