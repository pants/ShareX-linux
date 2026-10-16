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

using System.Diagnostics;
using System.Globalization;
using ShareX.HelpersLib;
using ShareX.Linux;
using Bitmap = SkiaSharp.SKBitmap;
using ImageFormat = SkiaSharp.SKEncodedImageFormat;

namespace ShareX.Tools;

/// <summary>
/// Text recognition backed by Tesseract, the OCR engine Linux desktops ship.
/// </summary>
/// <remarks>
/// Each installed language is a traineddata file, so the language list comes from asking
/// Tesseract what it has rather than from a system service. Install the engine and the languages
/// through the distribution, for example "tesseract" plus "tesseract-data-eng".
/// </remarks>
public static class OCRHelper
{
    private const string Executable = "tesseract";

    /// <summary>ISO 639-2/T codes Tesseract uses, mapped to the BCP 47 tags ShareX stores.</summary>
    private static readonly Dictionary<string, string> languageTagsByTesseractCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["afr"] = "af", ["ara"] = "ar", ["bel"] = "be", ["ben"] = "bn", ["bul"] = "bg", ["cat"] = "ca",
        ["ces"] = "cs", ["chi_sim"] = "zh-Hans", ["chi_tra"] = "zh-Hant", ["dan"] = "da", ["deu"] = "de",
        ["ell"] = "el", ["eng"] = "en", ["est"] = "et", ["fas"] = "fa", ["fin"] = "fi", ["fra"] = "fr",
        ["heb"] = "he", ["hin"] = "hi", ["hrv"] = "hr", ["hun"] = "hu", ["ind"] = "id", ["isl"] = "is",
        ["ita"] = "it", ["jpn"] = "ja", ["kor"] = "ko", ["lav"] = "lv", ["lit"] = "lt", ["nld"] = "nl",
        ["nor"] = "no", ["pol"] = "pl", ["por"] = "pt", ["ron"] = "ro", ["rus"] = "ru", ["slk"] = "sk",
        ["slv"] = "sl", ["spa"] = "es", ["sqi"] = "sq", ["srp"] = "sr", ["swe"] = "sv", ["tha"] = "th",
        ["tur"] = "tr", ["ukr"] = "uk", ["vie"] = "vi"
    };

    private static readonly Lazy<string?> executablePath = new(() => LinuxEnvironment.FindExecutable(Executable));

    public static bool IsSupported => executablePath.Value != null;

    public static OCRLanguageOption[] AvailableLanguages
    {
        get
        {
            ThrowIfNotSupported();

            return ListInstalledLanguages()
                .Select(code => new OCRLanguageOption(DescribeLanguage(code), ToLanguageTag(code)))
                .OrderBy(option => option.DisplayName, StringComparer.CurrentCulture)
                .ToArray();
        }
    }

    public static void ThrowIfNotSupported()
    {
        if (!IsSupported)
        {
            throw new InvalidOperationException(Localization.Strings.OCRHelper_Engine_missing);
        }
    }

    public static async Task<string> OCR(Bitmap bitmap, string languageTag = "en", float scaleFactor = 1f,
        bool singleLine = false)
    {
        ThrowIfNotSupported();
        scaleFactor = Math.Max(scaleFactor, 1f);

        return await Task.Run(() =>
        {
            using Bitmap scaledBitmap = SkiaImageHelpers.ScaleImageFast(bitmap, scaleFactor);
            return Recognize(scaledBitmap, languageTag, singleLine);
        });
    }

    private static string Recognize(Bitmap bitmap, string languageTag, bool singleLine)
    {
        string code = ToTesseractCode(languageTag);

        if (!ListInstalledLanguages().Contains(code, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                string.Format(Localization.Strings.OCRHelper_Language_unavailable, DescribeLanguage(code)));
        }

        string imagePath = Path.Combine(Path.GetTempPath(), $"sharex-ocr-{Guid.NewGuid():N}.png");

        try
        {
            using (FileStream file = File.Create(imagePath))
            {
                bitmap.Encode(file, ImageFormat.Png, 100);
            }

            ProcessStartInfo startInfo = new(executablePath.Value!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            startInfo.ArgumentList.Add(imagePath);
            startInfo.ArgumentList.Add("stdout");
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add(code);

            // Page segmentation 6 treats the crop as one uniform block, which suits screenshots;
            // 7 tells Tesseract the image holds a single line of text.
            startInfo.ArgumentList.Add("--psm");
            startInfo.ArgumentList.Add(singleLine ? "7" : "6");

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Starting Tesseract failed.");

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(120000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw new TimeoutException("Tesseract did not finish in time.");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Tesseract failed: {error.Trim()}");
            }

            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();

            return string.Join(singleLine ? " " : Environment.NewLine, lines);
        }
        finally
        {
            try
            {
                if (File.Exists(imagePath)) File.Delete(imagePath);
            }
            catch (Exception exception)
            {
                DebugHelper.WriteException(exception, "Removing the temporary OCR image failed.");
            }
        }
    }

    private static IReadOnlyList<string> ListInstalledLanguages()
    {
        string? output = LinuxEnvironment.RunCommand(Executable, "--list-langs");

        if (string.IsNullOrWhiteSpace(output)) return [];

        // The first line is a header; the rest are language codes, one per line.
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Contains(':') && line != "osd")
            .ToArray();
    }

    private static string ToTesseractCode(string languageTag)
    {
        foreach ((string code, string tag) in languageTagsByTesseractCode)
        {
            if (string.Equals(tag, languageTag, StringComparison.OrdinalIgnoreCase)) return code;
        }

        // Fall back to the primary subtag, which already matches for most three-letter codes.
        string primary = languageTag.Split('-')[0];

        foreach ((string code, string tag) in languageTagsByTesseractCode)
        {
            if (string.Equals(tag, primary, StringComparison.OrdinalIgnoreCase)) return code;
        }

        return languageTag;
    }

    private static string ToLanguageTag(string tesseractCode) =>
        languageTagsByTesseractCode.TryGetValue(tesseractCode, out string? tag) ? tag : tesseractCode;

    private static string DescribeLanguage(string tesseractCode)
    {
        try
        {
            return CultureInfo.GetCultureInfo(ToLanguageTag(tesseractCode)).DisplayName;
        }
        catch (CultureNotFoundException)
        {
            return tesseractCode;
        }
    }
}
