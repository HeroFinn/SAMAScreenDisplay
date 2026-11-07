using System;

namespace SamaDisplay.Core.Rendering;

/// <summary>
/// Exact reimplementation of the panel's private sparse delta codec
/// (official method 깬.ꗳ). Pixel buffers are packed BGRA, same orientation
/// as the 0xC8 keyframe (already rotated 90 degrees clockwise).
/// </summary>
public static class DeltaEncoder
{
    /// <summary>
    /// Encode the difference between <paramref name="reference"/> (what the
    /// panel currently shows) and <paramref name="current"/> (the new frame).
    /// Returns only the delta records; the caller appends EF 69.
    /// </summary>
    public static byte[] Encode(byte[] reference, byte[] current, int bpp, out int length, int maxRunPixels = 65000)
    {
        if (bpp <= 0) throw new ArgumentOutOfRangeException(nameof(bpp));
        if (reference.Length < current.Length) throw new ArgumentException("reference buffer smaller than current", nameof(reference));

        var pixelCount = current.Length / bpp;
        var dst = new byte[reference.Length];
        var w = 0;
        var hdr = 0;
        var run = 0;
        var open = false;
        var bppIs4 = bpp == 4;
        length = 0;

        for (var i = 0; i < pixelCount; i++)
        {
            var p = i * bpp;
            bool changed;
            if (reference[p] != current[p]) changed = true;
            else if (reference[p + 1] != current[p + 1]) changed = true;
            else if (reference[p + 2] != current[p + 2]) changed = true;
            else changed = bppIs4 && reference[p + 3] != current[p + 3];

            if (!changed)
            {
                if (!open) continue;

                if (run == 1)
                {
                    dst[hdr] |= 0x80;
                    dst[hdr + 3] = dst[hdr + 5];
                    dst[hdr + 4] = dst[hdr + 6];
                    dst[hdr + 5] = dst[hdr + 7];
                    w -= 2;
                }
                else
                {
                    dst[hdr + 3] = (byte)(run >> 8);
                    dst[hdr + 4] = (byte)run;
                }

                if (run > maxRunPixels)
                {
                    length = 0;
                    return null!;
                }

                open = false;
                run = 0;
                continue;
            }

            if (!open)
            {
                dst[w] = (byte)(i >> 16);
                dst[w + 1] = (byte)(i >> 8);
                dst[w + 2] = (byte)i;
                hdr = w;
                w += 5;
                open = true;
            }

            var a4 = bppIs4 ? current[p + 3] >> 4 : 0x0F;
            dst[w] = (byte)((current[p] & 0xFC) | (a4 >> 2));
            dst[w + 1] = (byte)((current[p + 1] & 0xFC) | (a4 & 3));
            dst[w + 2] = current[p + 2];
            w += 3;
            run++;
        }

        if (open)
        {
            dst[hdr + 3] = (byte)(run >> 8);
            dst[hdr + 4] = (byte)run;
        }

        length = w;
        return dst;
    }
}
