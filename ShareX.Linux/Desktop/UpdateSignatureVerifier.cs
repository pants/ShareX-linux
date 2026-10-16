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

namespace ShareX.Linux.Desktop;

/// <summary>
/// Checks downloaded files against a detached OpenPGP signature, the Linux counterpart of the
/// Authenticode check ShareX ran on Windows installers.
/// </summary>
/// <remarks>
/// A download is trusted when a ".sig" or ".asc" file sits next to it and gpg verifies it against
/// a key the user already trusts. Downloads published without a signature are accepted: ShareX
/// fetches them over HTTPS from a pinned host, and on Linux the supported way to update ShareX
/// itself is the distribution package, not a self-installing download.
/// </remarks>
public static class UpdateSignatureVerifier
{
    private static readonly string[] signatureExtensions = [".sig", ".asc"];

    public static Action<Exception, string>? ExceptionLogger { get; set; }

    public static bool IsTrusted(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;

        string? signaturePath = signatureExtensions
            .Select(extension => filePath + extension)
            .FirstOrDefault(File.Exists);

        if (signaturePath == null) return true;

        return VerifyDetachedSignature(signaturePath, filePath);
    }

    private static bool VerifyDetachedSignature(string signaturePath, string filePath)
    {
        string? gpg = LinuxEnvironment.FindExecutable("gpg") ?? LinuxEnvironment.FindExecutable("gpgv");

        if (gpg == null)
        {
            ExceptionLogger?.Invoke(new FileNotFoundException("gpg was not found."),
                $"\"{filePath}\" ships a signature but gpg is not installed, so it cannot be verified.");
            return false;
        }

        try
        {
            ProcessStartInfo startInfo = new(gpg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            if (Path.GetFileName(gpg) == "gpg")
            {
                startInfo.ArgumentList.Add("--verify");
            }

            startInfo.ArgumentList.Add(signaturePath);
            startInfo.ArgumentList.Add(filePath);

            using Process? process = Process.Start(startInfo);

            if (process == null) return false;

            process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(30000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            if (process.ExitCode == 0) return true;

            ExceptionLogger?.Invoke(new InvalidOperationException(error.Trim()),
                $"The signature on \"{filePath}\" could not be verified.");
            return false;
        }
        catch (Exception exception)
        {
            ExceptionLogger?.Invoke(exception, $"Verifying the signature on \"{filePath}\" failed.");
            return false;
        }
    }
}
