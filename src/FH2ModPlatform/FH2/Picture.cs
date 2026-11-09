using System.Buffers.Binary;
using System.IO.Compression;

namespace FH2;

/// <summary>
/// A picture in memory (BGRA, colour premultiplied by alpha, as the game's UI textures are): read from a PNG,
/// resized, and written as the game's texture file (<see cref="ToSwatchbin"/>) for <see cref="FH2ModPlatform.Mod.SetFile(string, byte[])"/>.
/// </summary>
public sealed class Picture
{
    public Picture(int width, int height, byte[]? bgra = null)
    {
        Width = width;
        Height = height;
        Bgra = bgra ?? new byte[width * height * 4];
        if (Bgra.Length != width * height * 4) throw new ArgumentException("The pixels don't fit the size.", nameof(bgra));
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The pixels, in rows from the top: blue, green, red, alpha, the colour premultiplied by alpha.</summary>
    public byte[] Bgra { get; }

    /// <summary>A PNG (8 bits a channel or palette; not interlaced), or null if it can't be read.</summary>
    public static Picture? FromPng(byte[] png)
    {
        try { return DecodePng(png); }
        catch { return null; }
    }

    /// <summary>
    /// The picture at another size: each pixel the average of what it covers when smaller, smoothly stretched
    /// when larger.
    /// </summary>
    public Picture Resize(int width, int height)
    {
        var o = new Picture(width, height);
        if (width <= Width && height <= Height)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int x0 = x * Width / width, x1 = Math.Max(x0 + 1, (x + 1) * Width / width);
                    int y0 = y * Height / height, y1 = Math.Max(y0 + 1, (y + 1) * Height / height);
                    int n = (x1 - x0) * (y1 - y0);
                    for (int c = 0; c < 4; c++)
                    {
                        int sum = 0;
                        for (int v = y0; v < y1; v++)
                            for (int u = x0; u < x1; u++) sum += Bgra[(v * Width + u) * 4 + c];
                        o.Bgra[(y * width + x) * 4 + c] = (byte)((sum + n / 2) / n);
                    }
                }
            return o;
        }
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float sx = Math.Clamp((x + 0.5f) * Width / width - 0.5f, 0, Width - 1);
                float sy = Math.Clamp((y + 0.5f) * Height / height - 0.5f, 0, Height - 1);
                int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
                float fx = sx - x0, fy = sy - y0;
                for (int c = 0; c < 4; c++)
                {
                    float a = Bgra[(y0 * Width + x0) * 4 + c] * (1 - fx) + Bgra[(y0 * Width + x1) * 4 + c] * fx;
                    float b = Bgra[(y1 * Width + x0) * 4 + c] * (1 - fx) + Bgra[(y1 * Width + x1) * 4 + c] * fx;
                    o.Bgra[(y * width + x) * 4 + c] = (byte)Math.Clamp(MathF.Round(a * (1 - fy) + b * fy), 0, 255);
                }
            }
        return o;
    }

    /// <summary>
    /// The game's texture file (.swatchbin) of it, uncompressed as the game's own 32 x 32 buttons are (format 0xAD,
    /// in 8 x 8 tiles): for ui/textures/... with <see cref="FH2ModPlatform.Mod.SetFile(string, byte[])"/>, and shown in XAML as
    /// "/ForzaUI;component/Textures/....png". Both sides must be multiples of 8.
    /// </summary>
    public byte[] ToSwatchbin()
    {
        if (Width % 8 != 0 || Height % 8 != 0) throw new InvalidOperationException("A texture's sides are multiples of 8.");
        int size = Width * Height * 4;
        var f = new byte[0x53 + size];
        void Put(int at, int v, int n) { for (int i = 0; i < n; i++) f[at + i] = (byte)(v >> (8 * i)); }
        // Plain bytes: in a u8 string "\xAD" is the character U+00AD, two bytes in UTF-8.
        new byte[] { (byte)'b', (byte)'u', (byte)'r', (byte)'G', 1, 0, 1, 0 }.CopyTo(f, 0x00);
        Put(0x08, 0x53, 4); Put(0x0C, 0x53 + size, 4);
        new byte[] { (byte)'B', (byte)'C', (byte)'X', (byte)'T', 2, 0, 1, 0 }.CopyTo(f, 0x10);
        Put(0x18, 0x28, 4); Put(0x1C, 0x53, 4); Put(0x20, size, 4); Put(0x24, size, 4);
        new byte[] { (byte)'H', (byte)'C', (byte)'X', (byte)'T', 0x30, 2, 8, 0 }.CopyTo(f, 0x28);
        f[0x3F] = 1;
        Put(0x40, Width, 2); Put(0x42, Height, 2); Put(0x44, 1, 2); Put(0x46, Width, 2); Put(0x48, Height, 2); Put(0x4A, 1, 2);
        new byte[] { 1, 1, 0, 0xAD, 1, 0, 0x48 }.CopyTo(f, 0x4C);   // one level, format 0xAD
        if ((Width & (Width - 1)) != 0 || (Height & (Height - 1)) != 0) f[0x52] = 0x40;   // as the game's non-power-of-two ones
        for (int i = 0; i < Width * Height; i++)
        {
            // In 8 x 8 tiles, in rows; the pixels in each in Morton order (x in the even bits, y in the odd).
            int tile = i >> 6, j = i & 63;
            int x = (j & 1) | ((j >> 1) & 2) | ((j >> 2) & 4), y = ((j >> 1) & 1) | ((j >> 2) & 2) | ((j >> 3) & 4);
            int px = tile % (Width / 8) * 8 + x, py = tile / (Width / 8) * 8 + y;
            Buffer.BlockCopy(Bgra, (py * Width + px) * 4, f, 0x53 + i * 4, 4);
        }
        return f;
    }

    // ---- PNG -------------------------------------------------------------------------------------

    private static Picture? DecodePng(byte[] png)
    {
        if (png.Length < 8 || BinaryPrimitives.ReadUInt64BigEndian(png) != 0x89504E470D0A1A0A) return null;
        int w = 0, h = 0, depth = 0, colour = 0, interlace = 0;
        byte[]? palette = null, trns = null;
        using var idat = new MemoryStream();
        for (int at = 8; at + 8 <= png.Length;)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, len);
            switch (type)
            {
                case "IHDR":
                    w = BinaryPrimitives.ReadInt32BigEndian(data);
                    h = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    depth = data[8];
                    colour = data[9];
                    interlace = data[12];
                    break;
                case "PLTE": palette = data.ToArray(); break;
                case "tRNS": trns = data.ToArray(); break;
                case "IDAT": idat.Write(data); break;
            }
            if (type == "IEND") break;
            at += 12 + len;
        }
        if (w <= 0 || h <= 0 || interlace != 0) return null;
        int channels = colour switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0 || (depth != 8 && !(colour == 3 && depth is 1 or 2 or 4))) return null;
        int bitsPerPixel = channels * depth, stride = (w * bitsPerPixel + 7) / 8, bpp = Math.Max(1, bitsPerPixel / 8);
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new byte[(stride + 1) * h];
        z.ReadExactly(raw);
        var cur = new byte[stride];
        var prev = new byte[stride];
        var pic = new Picture(w, h);
        for (int y = 0; y < h; y++)
        {
            int filter = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, cur, 0, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                cur[i] = (byte)(cur[i] + filter switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => 0,
                });
            }
            for (int x = 0; x < w; x++)
            {
                byte r, g, bl, al = 255;
                switch (colour)
                {
                    case 0: r = g = bl = cur[x]; if (trns is { Length: >= 2 } && cur[x] == trns[1]) al = 0; break;
                    case 2: r = cur[x * 3]; g = cur[x * 3 + 1]; bl = cur[x * 3 + 2]; break;
                    case 3:
                        int bit = x * depth, idx = (cur[bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1);
                        r = palette?[idx * 3] ?? 0; g = palette?[idx * 3 + 1] ?? 0; bl = palette?[idx * 3 + 2] ?? 0;
                        if (trns is not null && idx < trns.Length) al = trns[idx];
                        break;
                    case 4: r = g = bl = cur[x * 2]; al = cur[x * 2 + 1]; break;
                    default: r = cur[x * 4]; g = cur[x * 4 + 1]; bl = cur[x * 4 + 2]; al = cur[x * 4 + 3]; break;
                }
                int o = (y * w + x) * 4;
                pic.Bgra[o] = (byte)((bl * al + 127) / 255);
                pic.Bgra[o + 1] = (byte)((g * al + 127) / 255);
                pic.Bgra[o + 2] = (byte)((r * al + 127) / 255);
                pic.Bgra[o + 3] = al;
            }
            (prev, cur) = (cur, prev);
        }
        return pic;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
