using System.Drawing;
using System.Drawing.Imaging;
using AwesomeAssertions;
using Noof.Ledger.Demo.Shots;

namespace Noof.Ledger.Demo.Tests;

public sealed class ShotFilesTests
{
    [Fact]
    public void An_identical_image_is_not_rewritten()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("noof-shots-").FullName, "app", "dashboard-phone.png");

        ShotFiles.WriteIfChanged(file, [1, 2, 3]).Should().BeTrue();
        var written = File.GetLastWriteTimeUtc(file);

        ShotFiles.WriteIfChanged(file, [1, 2, 3]).Should().BeFalse();
        File.GetLastWriteTimeUtc(file).Should().Be(written);
    }

    [Fact]
    public void A_changed_image_is_rewritten()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("noof-shots-").FullName, "a.png");
        ShotFiles.WriteIfChanged(file, [1, 2, 3]);

        ShotFiles.WriteIfChanged(file, [1, 2, 4]).Should().BeTrue();
        File.ReadAllBytes(file).Should().Equal(1, 2, 4);
    }

    [Fact]
    public void An_image_differing_only_by_rendering_noise_is_not_rewritten()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("noof-shots-").FullName, "a.png");
        ShotFiles.WriteImageIfChanged(file, Png(Color.FromArgb(40, 40, 40))).Should().BeTrue();
        var written = File.ReadAllBytes(file);

        ShotFiles.WriteImageIfChanged(file, Png(Color.FromArgb(40, 40, 40), noisyPixel: Color.FromArgb(41, 40, 40))).Should().BeFalse();
        File.ReadAllBytes(file).Should().Equal(written);
    }

    [Fact]
    public void An_image_with_a_visible_change_is_rewritten()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("noof-shots-").FullName, "a.png");
        ShotFiles.WriteImageIfChanged(file, Png(Color.FromArgb(40, 40, 40)));

        ShotFiles.WriteImageIfChanged(file, Png(Color.FromArgb(40, 40, 40), noisyPixel: Color.FromArgb(200, 40, 40))).Should().BeTrue();
    }

    [Fact]
    public void An_image_of_another_size_is_rewritten()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("noof-shots-").FullName, "a.png");
        ShotFiles.WriteImageIfChanged(file, Png(Color.FromArgb(40, 40, 40)));

        ShotFiles.WriteImageIfChanged(file, Png(Color.FromArgb(40, 40, 40), height: 9)).Should().BeTrue();
    }

    static byte[] Png(Color fill, Color? noisyPixel = null, int height = 8)
    {
        using var bitmap = new Bitmap(8, height);
        for (var x = 0; x < 8; x++)
            for (var y = 0; y < height; y++)
                bitmap.SetPixel(x, y, fill);

        if (noisyPixel is { } noise)
            bitmap.SetPixel(3, 3, noise);

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    [Theory]
    [InlineData(1500, 2000, new[] { 0 }, new[] { 1500 })]
    [InlineData(2000, 2000, new[] { 0 }, new[] { 2000 })]
    [InlineData(4500, 2000, new[] { 0, 2000, 4000 }, new[] { 2000, 2000, 500 })]
    public void A_tall_page_is_cut_into_slices_no_taller_than_the_limit(int height, int slice, int[] ys, int[] heights)
    {
        var slices = ShotFiles.Slices(height, slice);

        slices.Select(part => part.Y).Should().Equal(ys);
        slices.Select(part => part.Height).Should().Equal(heights);
    }
}
