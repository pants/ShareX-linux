#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ShareX.ScreenCaptureLib
{
    public class ScreenRecordingOptions
    {
        public bool IsRecording { get; set; }
        public bool IsLossless { get; set; }
        public string InputPath { get; set; }
        public string OutputPath { get; set; }
        public int FPS { get; set; }
        public Rectangle CaptureArea { get; set; }
        public float Duration { get; set; }
        public bool DrawCursor { get; set; }

        /// <summary>
        /// The part of the captured PipeWire stream to keep, in stream coordinates. The portal
        /// hands over a whole monitor or window, so a user-selected region is cropped out here.
        /// Null records the stream as it arrives.
        /// </summary>
        public Rectangle? StreamCrop { get; set; }

        /// <summary>Webcam picture-in-picture settings for this recording.</summary>
        public CameraOverlayOptions Camera { get; set; } = new CameraOverlayOptions();
        public FFmpegOptions FFmpeg { get; set; } = new FFmpegOptions();

        public string GetFFmpegCommands()
        {
            string commands;

            if (!IsLossless && FFmpeg.UseCustomCommands && !string.IsNullOrEmpty(FFmpeg.CustomCommands))
            {
                commands = FFmpeg.CustomCommands.
                    Replace("$fps$", FPS.ToString(), StringComparison.OrdinalIgnoreCase).
                    Replace("$area_x$", CaptureArea.X.ToString(), StringComparison.OrdinalIgnoreCase).
                    Replace("$area_y$", CaptureArea.Y.ToString(), StringComparison.OrdinalIgnoreCase).
                    Replace("$area_width$", CaptureArea.Width.ToString(), StringComparison.OrdinalIgnoreCase).
                    Replace("$area_height$", CaptureArea.Height.ToString(), StringComparison.OrdinalIgnoreCase).
                    Replace("$cursor$", DrawCursor ? "1" : "0", StringComparison.OrdinalIgnoreCase).
                    Replace("$duration$", Duration.ToString("0.0", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase).
                    Replace("$output$", Path.ChangeExtension(OutputPath, FFmpeg.Extension), StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                commands = GetFFmpegArgs();
            }

            return commands.Trim();
        }

        public string GetFFmpegArgs(bool isCustom = false)
        {
            if (IsRecording && !FFmpeg.IsVideoSourceSelected && !FFmpeg.IsAudioSourceSelected)
            {
                return null;
            }

            StringBuilder args = new StringBuilder();

            string framerate = isCustom ? "$fps$" : FPS.ToString();

            if (IsRecording)
            {
                // Input order is fixed so the filter graph can address streams by index:
                // 0 is the screen, 1 is the camera when it is enabled, audio comes last.
                if (FFmpeg.IsVideoSourceSelected)
                {
                    if (FFmpeg.VideoSource.Equals(FFmpegCaptureDevice.PipeWire.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        // Frames arrive on stdin as yuv4mpegpipe, produced from the portal's
                        // PipeWire stream by WaylandRecordingSource. The y4m header carries the
                        // size and frame rate, so no -video_size is needed here.
                        args.Append("-f yuv4mpegpipe ");
                        args.Append("-thread_queue_size 1024 ");
                        args.Append("-i - ");
                    }
                    else
                    {
                        // Anything else is a V4L2 node, i.e. a webcam used as the main picture.
                        args.Append("-f v4l2 ");
                        args.Append("-thread_queue_size 1024 ");
                        args.Append($"-framerate {framerate} ");
                        args.Append($"-i \"{FFmpeg.VideoSource}\" ");
                    }

                    if (Camera.IsUsable)
                    {
                        (int cameraWidth, int cameraHeight) = Camera.GetCaptureSize();
                        args.Append("-f v4l2 ");
                        args.Append("-thread_queue_size 1024 ");
                        args.Append($"-framerate {Camera.FPS} ");
                        args.Append($"-video_size {cameraWidth}x{cameraHeight} ");
                        args.Append($"-i \"{Camera.DevicePath}\" ");
                    }
                }

                if (FFmpeg.IsAudioSourceSelected)
                {
                    AppendPulseInput(args, FFmpeg.AudioSource);
                }
            }
            else
            {
                args.Append($"-i \"{InputPath}\" ");
            }

            if (!string.IsNullOrEmpty(FFmpeg.UserArgs))
            {
                args.Append(FFmpeg.UserArgs + " ");
            }

            if (FFmpeg.IsVideoSourceSelected)
            {
                if (IsLossless || FFmpeg.VideoCodec != FFmpegVideoCodec.apng)
                {
                    string videoCodec;

                    if (IsLossless)
                    {
                        videoCodec = FFmpegVideoCodec.libx264.ToString();
                    }
                    else if (FFmpeg.VideoCodec == FFmpegVideoCodec.libvpx_vp9)
                    {
                        videoCodec = "libvpx-vp9";
                    }
                    else
                    {
                        videoCodec = FFmpeg.VideoCodec.ToString();
                    }

                    args.Append($"-c:v {videoCodec} ");
                    args.Append($"-r {framerate} "); // output FPS
                }

                if (IsLossless)
                {
                    args.Append($"-preset {FFmpegPreset.ultrafast} ");
                    args.Append($"-tune {FFmpegTune.zerolatency} ");
                    args.Append("-qp 0 ");
                }
                else
                {
                    switch (FFmpeg.VideoCodec)
                    {
                        case FFmpegVideoCodec.libx264: // https://trac.ffmpeg.org/wiki/Encode/H.264
                        case FFmpegVideoCodec.libx265: // https://trac.ffmpeg.org/wiki/Encode/H.265
                            args.Append($"-preset {FFmpeg.x264_Preset} ");
                            if (IsRecording) args.Append($"-tune {FFmpegTune.zerolatency} ");
                            if (FFmpeg.x264_Use_Bitrate)
                            {
                                args.Append($"-b:v {FFmpeg.x264_Bitrate}k ");
                            }
                            else
                            {
                                args.Append($"-crf {FFmpeg.x264_CRF} ");
                            }
                            args.Append("-pix_fmt yuv420p "); // -pix_fmt yuv420p required otherwise can't stream in Chrome
                            args.Append("-movflags +faststart "); // This will move some information to the beginning of your file and allow the video to begin playing before it is completely downloaded by the viewer
                            break;
                        case FFmpegVideoCodec.libvpx: // https://trac.ffmpeg.org/wiki/Encode/VP8
                        case FFmpegVideoCodec.libvpx_vp9: // https://trac.ffmpeg.org/wiki/Encode/VP9
                            if (IsRecording) args.Append("-deadline realtime ");
                            args.Append($"-b:v {FFmpeg.VPx_Bitrate}k ");
                            args.Append("-pix_fmt yuv420p "); // -pix_fmt yuv420p required otherwise causing issues in Chrome related to WebM transparency support
                            break;
                        case FFmpegVideoCodec.libxvid: // https://trac.ffmpeg.org/wiki/Encode/MPEG-4
                            args.Append($"-qscale:v {FFmpeg.XviD_QScale} ");
                            break;
                        case FFmpegVideoCodec.h264_nvenc: // https://trac.ffmpeg.org/wiki/HWAccelIntro#NVENC
                        case FFmpegVideoCodec.hevc_nvenc:
                            args.Append($"-preset {FFmpeg.NVENC_Preset} ");
                            args.Append($"-tune {FFmpeg.NVENC_Tune} ");
                            args.Append($"-b:v {FFmpeg.NVENC_Bitrate}k ");
                            args.Append("-movflags +faststart "); // This will move some information to the beginning of your file and allow the video to begin playing before it is completely downloaded by the viewer
                            break;
                        case FFmpegVideoCodec.h264_amf:
                        case FFmpegVideoCodec.hevc_amf:
                            args.Append($"-usage {FFmpeg.AMF_Usage} ");
                            args.Append($"-quality {FFmpeg.AMF_Quality} ");
                            args.Append($"-b:v {FFmpeg.AMF_Bitrate}k ");
                            args.Append("-pix_fmt yuv420p ");
                            break;
                        case FFmpegVideoCodec.h264_qsv: // https://trac.ffmpeg.org/wiki/Hardware/QuickSync
                        case FFmpegVideoCodec.hevc_qsv:
                            args.Append($"-preset {FFmpeg.QSV_Preset} ");
                            args.Append($"-b:v {FFmpeg.QSV_Bitrate}k ");
                            break;
                        case FFmpegVideoCodec.libwebp: // https://www.ffmpeg.org/ffmpeg-codecs.html#libwebp
                            args.Append("-lossless 0 ");
                            args.Append("-preset default ");
                            args.Append("-loop 0 ");
                            break;
                        case FFmpegVideoCodec.apng:
                            args.Append("-f apng ");
                            args.Append("-plays 0 ");
                            break;
                    }

                    switch (FFmpeg.VideoCodec)
                    {
                        case FFmpegVideoCodec.libx265:
                        case FFmpegVideoCodec.hevc_nvenc:
                        case FFmpegVideoCodec.hevc_amf:
                        case FFmpegVideoCodec.hevc_qsv:
                            args.Append("-tag:v hvc1 "); // https://trac.ffmpeg.org/wiki/Encode/H.265#FinalCutandApplestuffcompatibility
                            break;
                    }
                }
            }

            if (IsRecording && Camera.IsUsable)
            {
                AppendCameraOverlay(args);
            }

            if (IsRecording && !Camera.IsUsable && StreamCrop is { } crop && crop.Width > 0 && crop.Height > 0)
            {
                // Even dimensions keep the 4:2:0 encoders happy.
                int width = crop.Width - (crop.Width & 1);
                int height = crop.Height - (crop.Height & 1);
                args.Append($"-vf crop={width}:{height}:{crop.X}:{crop.Y} ");
            }

            if (FFmpeg.IsAudioSourceSelected)
            {
                switch (FFmpeg.AudioCodec)
                {
                    case FFmpegAudioCodec.libvoaacenc: // http://trac.ffmpeg.org/wiki/Encode/AAC
                        args.Append($"-c:a aac -ac 2 -b:a {FFmpeg.AAC_Bitrate}k "); // -ac 2 required otherwise failing with 7.1
                        break;
                    case FFmpegAudioCodec.libopus: // https://www.ffmpeg.org/ffmpeg-codecs.html#libopus-1
                        args.Append($"-c:a libopus -b:a {FFmpeg.Opus_Bitrate}k ");
                        break;
                    case FFmpegAudioCodec.libvorbis: // http://trac.ffmpeg.org/wiki/TheoraVorbisEncodingGuide
                        args.Append($"-c:a libvorbis -qscale:a {FFmpeg.Vorbis_QScale} ");
                        break;
                    case FFmpegAudioCodec.libmp3lame: // http://trac.ffmpeg.org/wiki/Encode/MP3
                        args.Append($"-c:a libmp3lame -qscale:a {FFmpeg.MP3_QScale} ");
                        break;
                }
            }

            if (Duration > 0)
            {
                string duration = isCustom ? "$duration$" : Duration.ToString("0.0", CultureInfo.InvariantCulture);
                args.Append($"-t {duration} "); // duration limit
            }

            args.Append("-y "); // overwrite file

            string output = isCustom ? "$output$" : Path.ChangeExtension(OutputPath, IsLossless ? "mp4" : FFmpeg.Extension);
            args.Append($"\"{output}\"");

            return args.ToString();
        }

        /// <summary>
        /// Builds the filter graph that composites the webcam over the recording and returns the
        /// label of its output, so the caller can map it.
        /// </summary>
        /// <remarks>
        /// Any crop is applied inside the graph, before the overlay, so the picture-in-picture is
        /// positioned against what is actually kept. A circular overlay gets its alpha from geq
        /// rather than from a mask image, which keeps the whole thing to one ffmpeg invocation.
        /// </remarks>
        private void AppendCameraOverlay(StringBuilder args)
        {
            Rectangle output = StreamCrop ?? CaptureArea;

            if (output.Width <= 0 || output.Height <= 0) return;

            int margin = Math.Max(0, Math.Min(Camera.Margin, (Math.Min(output.Width, output.Height) / 2) - 1));
            int availableWidth = Math.Max(2, output.Width - (margin * 2));
            int availableHeight = Math.Max(2, output.Height - (margin * 2));

            (int captureWidth, int captureHeight) = Camera.GetCaptureSize();
            double aspect = captureWidth / (double)captureHeight;
            bool circle = Camera.Shape == CameraOverlayShape.Circle;

            int width = Math.Clamp(output.Width * Camera.WidthPercent / 100, 2, availableWidth);
            int height = circle ? width : Math.Max(2, (int)(width / aspect));

            if (height > availableHeight)
            {
                height = availableHeight;
                width = circle ? height : Math.Clamp((int)(height * aspect), 2, availableWidth);
            }

            // Even dimensions keep the 4:2:0 encoders happy.
            width -= width & 1;
            height -= height & 1;

            string x = Camera.Position is CameraOverlayPosition.TopLeft or CameraOverlayPosition.BottomLeft
                ? margin.ToString(CultureInfo.InvariantCulture)
                : $"main_w-overlay_w-{margin.ToString(CultureInfo.InvariantCulture)}";
            string y = Camera.Position is CameraOverlayPosition.TopLeft or CameraOverlayPosition.TopRight
                ? margin.ToString(CultureInfo.InvariantCulture)
                : $"main_h-overlay_h-{margin.ToString(CultureInfo.InvariantCulture)}";

            StringBuilder filter = new StringBuilder();
            filter.Append("[0:v]");

            if (StreamCrop is Rectangle crop && crop.Width > 0 && crop.Height > 0)
            {
                int cropWidth = crop.Width - (crop.Width & 1);
                int cropHeight = crop.Height - (crop.Height & 1);
                filter.Append($"crop={cropWidth}:{cropHeight}:{crop.X}:{crop.Y},");
            }

            filter.Append("null[bg];");
            filter.Append($"[1:v]scale={width}:{height}");

            if (circle)
            {
                int radius = Math.Min(width, height) / 2;
                filter.Append(",format=rgba");
                filter.Append($",geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='if(lte(hypot(X-{width / 2},Y-{height / 2}),{radius}),255,0)'");
            }

            filter.Append("[cam];");
            filter.Append($"[bg][cam]overlay={x}:{y}:format=auto,format=yuv420p[out]");

            args.Append($"-filter_complex \"{filter}\" ");
            args.Append("-map \"[out]\" ");

            if (FFmpeg.IsAudioSourceSelected)
            {
                // Audio is the last input, after the screen and the camera.
                args.Append("-map 2:a ");
            }
        }


        /// <summary>
        /// Appends a PulseAudio input. PipeWire serves the same protocol through pipewire-pulse,
        /// so one code path covers both.
        /// </summary>
        private void AppendPulseInput(StringBuilder args, string source)
        {
            args.Append("-f pulse ");
            args.Append("-thread_queue_size 1024 ");
            args.Append("-fragment_size 1024 ");
            args.Append($"-i \"{source}\" ");
        }
    }
}