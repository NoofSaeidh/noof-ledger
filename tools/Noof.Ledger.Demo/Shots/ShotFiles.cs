using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Noof.Ledger.Demo.Shots;

internal static class ShotFiles
{
    // Chromium repaints a handful of pixels one colour level apart between two identical runs. That is not a
    // change to the app, and rewriting the file for it would put noise in git on every run.
    const int RenderingNoise = 2;

    public static bool WriteIfChanged(string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return true;
    }

    public static bool WriteImageIfChanged(string path, byte[] png) =>
        !(File.Exists(path) && LookTheSame(File.ReadAllBytes(path), png)) && WriteIfChanged(path, png);

    public static IReadOnlyList<(int Y, int Height)> Slices(int pageHeight, int sliceHeight) =>
        [.. Enumerable.Range(0, (pageHeight + sliceHeight - 1) / sliceHeight)
            .Select(index => (index * sliceHeight, Math.Min(sliceHeight, pageHeight - index * sliceHeight)))];

    static bool LookTheSame(byte[] existing, byte[] candidate)
    {
        if (existing.AsSpan().SequenceEqual(candidate))
            return true;

        var (existingWidth, existingHeight, existingPixels) = Decode(existing);
        var (candidateWidth, candidateHeight, candidatePixels) = Decode(candidate);
        if (existingWidth != candidateWidth || existingHeight != candidateHeight)
            return false;

        for (var index = 0; index < existingPixels.Length; index++)
        {
            if (Math.Abs(existingPixels[index] - candidatePixels[index]) > RenderingNoise)
                return false;
        }

        return true;
    }

    static (int Width, int Height, byte[] Pixels) Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        using var bitmap = new Bitmap(stream);
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return (bitmap.Width, bitmap.Height, pixels);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
