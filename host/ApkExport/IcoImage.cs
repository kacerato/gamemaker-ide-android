using System.Buffers.Binary;

namespace GmIde;

internal static class IcoImage
{
    internal sealed record Image(int Width, int Height, byte[]? Png, int[]? Pixels);

    internal static Image? Read(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[6];
        if (stream.Read(header) < 6 || !header[..4].SequenceEqual(new byte[] { 0, 0, 1, 0 })) return null;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (count == 0 || count > 256 || stream.Length > 24 * 1024 * 1024) throw new InvalidDataException("Icone ICO invalido.");
        byte[] data = new byte[stream.Length]; stream.Position = 0; stream.ReadExactly(data);
        int U16(int p) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p, 2));
        int I32(int p) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p, 4));
        int selected = 6, best = 0;
        for (int n = 0; n < count; n++)
        {
            int p = 6 + n * 16;
            int size = (data[p] == 0 ? 256 : data[p]) * (data[p + 1] == 0 ? 256 : data[p + 1]);
            if (size > best) { selected = p; best = size; }
        }
        int length = I32(selected + 8), offset = I32(selected + 12);
        if (length < 8 || offset < 6 + count * 16 || offset > data.Length - length) throw new InvalidDataException("Dados do ICO fora do arquivo.");
        if (data.AsSpan(offset, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return new Image(data[selected] == 0 ? 256 : data[selected], data[selected + 1] == 0 ? 256 : data[selected + 1], data.AsSpan(offset, length).ToArray(), null);
        int headerSize = I32(offset), width = I32(offset + 4), rawHeight = I32(offset + 8), height = Math.Abs(rawHeight) / 2, bits = U16(offset + 14);
        if (headerSize < 40 || width < 1 || width > 256 || height < 1 || height > 256 || I32(offset + 16) != 0 || bits is not (1 or 4 or 8 or 24 or 32))
            throw new InvalidDataException("Formato ICO nao suportado. Escolha uma imagem PNG.");
        int colors = bits <= 8 ? I32(offset + 32) == 0 ? 1 << bits : I32(offset + 32) : 0;
        if (colors < 0 || colors > 256) throw new InvalidDataException("Paleta ICO invalida.");
        int pixels = checked(offset + headerSize + colors * 4), stride = (width * bits + 31) / 32 * 4;
        int mask = checked(pixels + height * stride), maskStride = (width + 31) / 32 * 4;
        if (mask > offset + length) throw new InvalidDataException("Bitmap ICO incompleto.");
        bool alpha = bits == 32 && Enumerable.Range(0, width * height).Any(i => data[pixels + i / width * stride + i % width * 4 + 3] != 0);
        var result = new int[width * height];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int row = rawHeight > 0 ? height - 1 - y : y, p = pixels + row * stride, color;
            if (bits >= 24)
            {
                p += x * (bits / 8); color = data[p] | data[p + 1] << 8 | data[p + 2] << 16;
                color |= (alpha ? data[p + 3] : 255) << 24;
            }
            else
            {
                int value = data[p + x * bits / 8] >> (8 - bits - x * bits % 8) & ((1 << bits) - 1);
                if (value >= colors) throw new InvalidDataException("Indice ICO fora da paleta.");
                p = offset + headerSize + value * 4; color = unchecked((int)0xff000000) | data[p] | data[p + 1] << 8 | data[p + 2] << 16;
            }
            if (!alpha && mask + row * maskStride + x / 8 < offset + length && (data[mask + row * maskStride + x / 8] & (128 >> (x % 8))) != 0)
                color &= 0x00ffffff;
            result[y * width + x] = color;
        }
        return new Image(width, height, null, result);
    }
}
