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
using ShareX.Linux.Portals;
using SkiaSharp;

namespace ShareX.Linux.Desktop;

/// <summary>
/// Printing for Linux desktops. Pages are rasterised by the caller, wrapped in a PDF and handed
/// to the desktop's print dialog through the portal, which already knows the user's CUPS queues.
/// When no portal is available the PDF is spooled straight to CUPS with lp.
/// </summary>
public static class PrintingService
{
    /// <summary>A rendered page plus the paper size it was laid out for, in PostScript points.</summary>
    public readonly record struct PrintPage(SKBitmap Image, float WidthPoints, float HeightPoints);

    public static Action<Exception, string>? ExceptionLogger { get; set; }

    /// <summary>
    /// Sends the given pages to a printer. With <paramref name="printerName"/> set the job skips
    /// the dialog and goes straight to that CUPS queue. Returns false when the user cancels.
    /// </summary>
    public static bool Print(IReadOnlyList<PrintPage> pages, string documentTitle, string? printerName = null,
        bool showDialog = true)
    {
        if (pages.Count == 0) return false;

        string pdfPath = Path.Combine(Path.GetTempPath(), $"sharex-print-{Guid.NewGuid():N}.pdf");

        try
        {
            WritePdf(pages, pdfPath, documentTitle);

            if (showDialog && string.IsNullOrEmpty(printerName) && TryPrintWithPortal(pdfPath, documentTitle, out bool printed))
            {
                return printed;
            }

            return PrintWithCups(pdfPath, documentTitle, printerName);
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, "Printing failed.");
            return false;
        }
        finally
        {
            TryDelete(pdfPath);
        }
    }

    /// <summary>Writes the pages out as a PDF, which is also how "print to file" is served.</summary>
    public static void WritePdf(IReadOnlyList<PrintPage> pages, string path, string documentTitle)
    {
        using FileStream stream = File.Create(path);
        using SKDocument document = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata
        {
            Title = documentTitle,
            Creator = "ShareX",
            Producer = "ShareX",
            Creation = DateTime.Now
        }) ?? throw new InvalidOperationException("Skia could not create a PDF document.");

        foreach (PrintPage page in pages)
        {
            using SKCanvas canvas = document.BeginPage(page.WidthPoints, page.HeightPoints);
            canvas.DrawBitmap(page.Image, SKRect.Create(page.WidthPoints, page.HeightPoints));
            document.EndPage();
        }

        document.Close();
    }

    /// <summary>Names of the printers CUPS knows about, for the printer override setting.</summary>
    public static IReadOnlyList<string> GetPrinterNames()
    {
        string? output = LinuxEnvironment.RunCommand("lpstat", "-e");

        if (string.IsNullOrWhiteSpace(output)) return [];

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static string? GetDefaultPrinterName()
    {
        string? output = LinuxEnvironment.RunCommand("lpstat", "-d");

        // "system default destination: Brother_HL" or "no system default destination"
        int separator = output?.IndexOf(':') ?? -1;

        return separator >= 0 ? output![(separator + 1)..].Trim() : null;
    }

    public static bool IsPrintingAvailable => LinuxEnvironment.HasExecutable("lp") || PortalConnection.IsAvailable;

    private static bool TryPrintWithPortal(string pdfPath, string documentTitle, out bool printed)
    {
        printed = false;

        try
        {
            PrintPortal portal = new();

            if (portal.GetVersionAsync().GetAwaiter().GetResult() == 0) return false;

            printed = portal.PrintPdfAsync(pdfPath, documentTitle).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, "The print portal failed; falling back to CUPS.");
            return false;
        }
    }

    private static bool PrintWithCups(string pdfPath, string documentTitle, string? printerName)
    {
        string? lp = LinuxEnvironment.FindExecutable("lp");

        if (lp == null)
        {
            throw new InvalidOperationException("No print portal is available and the CUPS 'lp' command was not found.");
        }

        ProcessStartInfo startInfo = new(lp) { RedirectStandardError = true, UseShellExecute = false };

        if (!string.IsNullOrEmpty(printerName))
        {
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(printerName);
        }

        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(documentTitle);
        startInfo.ArgumentList.Add(pdfPath);

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Starting lp failed.");

        string error = process.StandardError.ReadToEnd();
        process.WaitForExit(30000);

        if (process.ExitCode == 0) return true;

        throw new InvalidOperationException($"lp exited with code {process.ExitCode}: {error.Trim()}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, $"Removing the temporary print file \"{path}\" failed.");
        }
    }
}
