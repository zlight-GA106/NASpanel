using System.Buffers.Binary;
using System.IO.Compression;

namespace StorageStation.Web.Security;

public static class AvatarImage
{
    // The browser decodes the selected image and exports a small PNG. Keep it in
    // the authenticated profile, never as an executable file in the web root.
    public static void Validate(string image)
    {
        const string prefix = "data:image/png;base64,";
        if (image.Length > 60_000 || !image.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("头像格式无效或过大，请重新选择图片");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(image[prefix.Length..]); }
        catch (FormatException) { throw new ArgumentException("头像格式无效，请重新选择图片"); }
        if (bytes.Length < 45 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new ArgumentException("头像必须是 PNG 图片");
        using var compressed = new MemoryStream();
        var width = 0; var height = 0; var channels = 0; var ended = false;
        for (var offset = 8; offset + 12 <= bytes.Length;)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (size > bytes.Length - offset - 12) throw new ArgumentException("头像图片不完整");
            var type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);
            var data = bytes.AsSpan(offset + 8, (int)size);
            if (offset == 8 && (type != "IHDR" || size != 13)) throw new ArgumentException("头像 PNG 头无效");
            if (type == "IHDR")
            {
                if (offset != 8 || size != 13) throw new ArgumentException("头像 PNG 头无效");
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(data); height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                if (width is < 1 or > 96 || height is < 1 or > 96 || data[8] != 8 || data[9] is not (2 or 6) || data[10] != 0 || data[11] != 0 || data[12] != 0) throw new ArgumentException("头像需先缩小为不超过 96 × 96 的 PNG 图片");
                channels = data[9] == 6 ? 4 : 3;
            }
            else if (type == "IDAT") compressed.Write(data);
            else if (type == "IEND") { ended = size == 0 && offset + 12 == bytes.Length; break; }
            offset += (int)size + 12;
        }
        if (!ended || compressed.Length == 0) throw new ArgumentException("头像图片不完整");
        compressed.Position = 0;
        try
        {
            using var pixels = new ZLibStream(compressed, CompressionMode.Decompress);
            var stride = width * channels + 1;
            var decoded = new byte[stride * height]; pixels.ReadExactly(decoded);
            if (pixels.ReadByte() != -1) throw new InvalidDataException();
            for (var row = 0; row < height; row++) if (decoded[row * stride] > 4) throw new InvalidDataException();
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { throw new ArgumentException("头像图片数据无效"); }
    }
}
