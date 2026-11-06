using System.IO;
using System.Text;

namespace YinggeVideoLibrary;

/// <summary>Creates a tiny self-contained uncompressed AVI for isolated UI
/// acceptance. No downloaded footage or private media enters the fixture.</summary>
internal static class FixtureVideoWriter
{
    internal static void WriteAvi(string path)
    {
        const int width = 64, height = 64, frames = 2, fps = 2;
        const int frameBytes = width * height * 3;
        static byte[] FourCc(string value) => Encoding.ASCII.GetBytes(value);
        static byte[] Chunk(string id, byte[] content)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(FourCc(id));
            writer.Write(content.Length);
            writer.Write(content);
            if ((content.Length & 1) != 0) writer.Write((byte)0);
            return stream.ToArray();
        }
        static byte[] List(string type, params byte[][] chunks)
        {
            using var stream = new MemoryStream();
            stream.Write(FourCc(type));
            foreach (var chunk in chunks) stream.Write(chunk);
            return Chunk("LIST", stream.ToArray());
        }
        byte[] Header()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(1_000_000 / fps); // microseconds per frame
            writer.Write(frameBytes * fps); // maximum bytes per second
            writer.Write(0); // padding
            writer.Write(0); // sequential playback, no index required
            writer.Write(frames);
            writer.Write(0); // initial frames
            writer.Write(1); // streams
            writer.Write(frameBytes);
            writer.Write(width);
            writer.Write(height);
            for (var i = 0; i < 4; i++) writer.Write(0);
            return stream.ToArray();
        }
        byte[] StreamHeader()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(FourCc("vids"));
            writer.Write(FourCc("DIB "));
            writer.Write(0); // flags
            writer.Write((short)0); // priority
            writer.Write((short)0); // language
            writer.Write(0); // initial frames
            writer.Write(1); // scale
            writer.Write(fps); // rate
            writer.Write(0); // start
            writer.Write(frames); // length
            writer.Write(frameBytes);
            writer.Write(-1); // quality
            writer.Write(0); // sample size
            writer.Write((short)0); writer.Write((short)0);
            writer.Write((short)width); writer.Write((short)height);
            return stream.ToArray();
        }
        byte[] Format()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(40); writer.Write(width); writer.Write(height);
            writer.Write((short)1); writer.Write((short)24);
            writer.Write(0); writer.Write(frameBytes);
            writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            return stream.ToArray();
        }
        var hdrl = List("hdrl", Chunk("avih", Header()),
            List("strl", Chunk("strh", StreamHeader()), Chunk("strf", Format())));
        var pictures = new byte[frames][];
        for (var frame = 0; frame < frames; frame++)
        {
            var pixels = new byte[frameBytes];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 3;
                pixels[offset] = (byte)(40 + x * 2); // blue
                pixels[offset + 1] = (byte)(55 + y * 2); // green
                pixels[offset + 2] = frame == 0 ? (byte)170 : (byte)225; // red
            }
            pictures[frame] = Chunk("00db", pixels);
        }
        var movi = List("movi", pictures);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var output = File.Create(path);
        using var binary = new BinaryWriter(output);
        binary.Write(FourCc("RIFF"));
        binary.Write(4 + hdrl.Length + movi.Length);
        binary.Write(FourCc("AVI "));
        binary.Write(hdrl);
        binary.Write(movi);
    }
}
