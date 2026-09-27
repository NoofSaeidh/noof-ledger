using AwesomeAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramReceiptPhotoSourceTests
{
    static readonly byte[] SyntheticJpeg = "JFIF-synthetic-receipt-photo"u8.ToArray();

    static ITelegramBotClient ClientServing(byte[] bytes, string filePath = "photos/file_1.jpg", long? fileSize = null)
    {
        var client = Substitute.For<ITelegramBotClient>();
        var file = new TGFile { FileId = "photo-1", FileUniqueId = "unique-1", FilePath = filePath, FileSize = fileSize ?? bytes.Length };
        client.SendRequest(Arg.Is<GetFileRequest>(r => r.FileId == "photo-1"), Arg.Any<CancellationToken>()).Returns(file);
        client.DownloadFile(Arg.Any<TGFile>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Stream>().WriteAsync(bytes).AsTask());
        return client;
    }

    [Fact]
    public async Task Downloads_the_photo_by_its_file_id_and_reports_its_media_type()
    {
        var source = new TelegramReceiptPhotoSource(new TelegramClientHandle { Current = ClientServing(SyntheticJpeg) });

        var photo = await source.DownloadAsync("photo-1", TestContext.Current.CancellationToken);

        photo.Bytes.ToArray().Should().Equal(SyntheticJpeg);
        photo.MediaType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task A_png_file_path_reports_the_png_media_type()
    {
        var source = new TelegramReceiptPhotoSource(
            new TelegramClientHandle { Current = ClientServing(SyntheticJpeg, filePath: "documents/file_2.png") });

        var photo = await source.DownloadAsync("photo-1", TestContext.Current.CancellationToken);

        photo.MediaType.Should().Be("image/png");
    }

    [Fact]
    public async Task A_gif_file_path_reports_the_gif_media_type()
    {
        var source = new TelegramReceiptPhotoSource(
            new TelegramClientHandle { Current = ClientServing(SyntheticJpeg, filePath: "documents/file_3.gif") });

        var photo = await source.DownloadAsync("photo-1", TestContext.Current.CancellationToken);

        photo.MediaType.Should().Be("image/gif");
    }

    [Fact]
    public async Task An_upper_case_png_file_path_reports_the_png_media_type()
    {
        var source = new TelegramReceiptPhotoSource(
            new TelegramClientHandle { Current = ClientServing(SyntheticJpeg, filePath: "documents/file_4.PNG") });

        var photo = await source.DownloadAsync("photo-1", TestContext.Current.CancellationToken);

        photo.MediaType.Should().Be("image/png");
    }

    [Fact]
    public async Task A_mixed_case_webp_file_path_reports_the_webp_media_type()
    {
        var source = new TelegramReceiptPhotoSource(
            new TelegramClientHandle { Current = ClientServing(SyntheticJpeg, filePath: "documents/file_5.WebP") });

        var photo = await source.DownloadAsync("photo-1", TestContext.Current.CancellationToken);

        photo.MediaType.Should().Be("image/webp");
    }

    [Fact]
    public async Task An_upper_case_gif_file_path_reports_the_gif_media_type()
    {
        var source = new TelegramReceiptPhotoSource(
            new TelegramClientHandle { Current = ClientServing(SyntheticJpeg, filePath: "documents/file_6.GIF") });

        var photo = await source.DownloadAsync("photo-1", TestContext.Current.CancellationToken);

        photo.MediaType.Should().Be("image/gif");
    }

    [Fact]
    public async Task Throws_when_no_client_is_ready_yet()
    {
        var source = new TelegramReceiptPhotoSource(new TelegramClientHandle());

        var act = () => source.DownloadAsync("photo-1", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_file_over_the_limit_is_refused_before_downloading()
    {
        var client = ClientServing(SyntheticJpeg, fileSize: 11 * 1024 * 1024);
        var source = new TelegramReceiptPhotoSource(new TelegramClientHandle { Current = client });

        var act = () => source.DownloadAsync("photo-1", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await client.DidNotReceiveWithAnyArgs().DownloadFile(default(TGFile)!, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refused_download_propagates_for_the_worker_to_retry()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetFileRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException("Bad Request: wrong file_id or the file is temporarily unavailable", 400));
        var source = new TelegramReceiptPhotoSource(new TelegramClientHandle { Current = client });

        var act = () => source.DownloadAsync("photo-1", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ApiRequestException>();
    }
}
