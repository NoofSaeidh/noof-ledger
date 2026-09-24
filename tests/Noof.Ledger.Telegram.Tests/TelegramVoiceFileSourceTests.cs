using AwesomeAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace Noof.Ledger.Telegram.Tests;

public class TelegramVoiceFileSourceTests
{
    static readonly byte[] SyntheticAudio = "OggS-synthetic-voice-note"u8.ToArray();

    static ITelegramBotClient ClientServing(byte[] audio)
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Is<GetFileRequest>(r => r.FileId == "voice-file-1"), Arg.Any<CancellationToken>())
            .Returns(new TGFile { FileId = "voice-file-1", FileUniqueId = "unique-1", FilePath = "voice/file_1.oga" });
        // GetInfoAndDownloadFile may reach either overload; both write the same bytes.
        client.DownloadFile(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Stream>().WriteAsync(audio).AsTask());
        client.DownloadFile(Arg.Any<TGFile>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Stream>().WriteAsync(audio).AsTask());
        return client;
    }

    [Fact]
    public async Task Downloads_the_note_by_its_file_id_into_a_stream_at_its_start()
    {
        var source = new TelegramVoiceFileSource(new TelegramClientHandle { Current = ClientServing(SyntheticAudio) });

        await using var audio = await source.DownloadAsync("voice-file-1", TestContext.Current.CancellationToken);

        audio.Position.Should().Be(0);
        using var copy = new MemoryStream();
        await audio.CopyToAsync(copy, TestContext.Current.CancellationToken);
        copy.ToArray().Should().Equal(SyntheticAudio);
    }

    [Fact]
    public async Task Throws_when_no_client_is_ready_yet()
    {
        var source = new TelegramVoiceFileSource(new TelegramClientHandle());

        var act = () => source.DownloadAsync("voice-file-1", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_refused_download_propagates_for_the_worker_to_retry()
    {
        var client = Substitute.For<ITelegramBotClient>();
        client.SendRequest(Arg.Any<GetFileRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException("Bad Request: wrong file_id or the file is temporarily unavailable", 400));
        var source = new TelegramVoiceFileSource(new TelegramClientHandle { Current = client });

        var act = () => source.DownloadAsync("voice-file-1", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ApiRequestException>();
    }
}
