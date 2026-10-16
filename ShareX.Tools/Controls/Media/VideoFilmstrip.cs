#nullable enable

using Avalonia.Media.Imaging;

namespace ShareX.Tools;

/// <summary>
/// Builds the sparse filmstrip shown under the trimmer's timeline by pulling single frames out
/// of the file with ffmpeg, independently of whatever the player is doing.
/// </summary>
internal static class VideoFilmstrip
{
    private const int ThumbnailCount = 12;
    private const int ThumbnailWidth = 160;
    private const int ThumbnailHeight = 90;

    public static Task<IReadOnlyList<VideoTrimmerThumbnail>> CreateAsync(string ffmpegPath, string path,
        double duration, CancellationToken token) =>
        Task.Run<IReadOnlyList<VideoTrimmerThumbnail>>(() =>
        {
            List<VideoTrimmerThumbnail> thumbnails = [];

            try
            {
                for (int i = 0; i < ThumbnailCount; i++)
                {
                    token.ThrowIfCancellationRequested();

                    double position = duration * i / ThumbnailCount;
                    Bitmap image = FFmpegVideoDecoder.DecodeFrame(ffmpegPath, path, position,
                        ThumbnailWidth, ThumbnailHeight, token);

                    thumbnails.Add(new VideoTrimmerThumbnail(position, image));
                }

                return thumbnails;
            }
            catch
            {
                foreach (VideoTrimmerThumbnail thumbnail in thumbnails) thumbnail.Image.Dispose();
                throw;
            }
        }, token);
}
