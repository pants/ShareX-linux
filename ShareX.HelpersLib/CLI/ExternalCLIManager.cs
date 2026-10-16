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

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ShareX.Linux.Interop;

namespace ShareX.HelpersLib
{
    public abstract class ExternalCLIManager : IDisposable
    {
        public event DataReceivedEventHandler OutputDataReceived;
        public event DataReceivedEventHandler ErrorDataReceived;

        public bool IsProcessRunning { get; private set; }

        /// <summary>
        /// Data to feed the process on its standard input, used to pipe captured video into
        /// ffmpeg. While this is set, standard input belongs to the pipe, so the process has to
        /// be asked to stop with a signal rather than by typing a command at it.
        /// </summary>
        public Func<Stream> StandardInputProvider { get; set; }

        protected Process process;

        private CancellationTokenSource inputCancellation;

        public virtual int Open(string path, string args = null)
        {
            if (File.Exists(path))
            {
                using (process = new Process())
                {
                    ProcessStartInfo psi = new ProcessStartInfo()
                    {
                        FileName = path,
                        WorkingDirectory = Path.GetDirectoryName(path),
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };

                    process.EnableRaisingEvents = true;
                    if (psi.RedirectStandardOutput) process.OutputDataReceived += cli_OutputDataReceived;
                    if (psi.RedirectStandardError) process.ErrorDataReceived += cli_ErrorDataReceived;
                    process.StartInfo = psi;

                    DebugHelper.WriteLine($"CLI: \"{psi.FileName}\" {psi.Arguments}");
                    process.Start();

                    if (psi.RedirectStandardOutput) process.BeginOutputReadLine();
                    if (psi.RedirectStandardError) process.BeginErrorReadLine();

                    Task inputPump = StartInputPump();

                    try
                    {
                        IsProcessRunning = true;
                        process.WaitForExit();
                    }
                    finally
                    {
                        IsProcessRunning = false;
                        StopInputPump(inputPump);
                    }

                    return process.ExitCode;
                }
            }

            return -1;
        }

        private void cli_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                OutputDataReceived?.Invoke(sender, e);
            }
        }

        private void cli_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                ErrorDataReceived?.Invoke(sender, e);
            }
        }

        /// <summary>Copies <see cref="StandardInputProvider"/> into the process until either end closes.</summary>
        private Task StartInputPump()
        {
            Func<Stream> provider = StandardInputProvider;

            if (provider == null) return null;

            Stream source = provider();

            if (source == null) return null;

            inputCancellation = new CancellationTokenSource();
            CancellationToken token = inputCancellation.Token;
            Stream destination = process.StandardInput.BaseStream;

            return Task.Run(async () =>
            {
                try
                {
                    await source.CopyToAsync(destination, token);
                    await destination.FlushAsync(token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException
                    or ObjectDisposedException)
                {
                    // Expected: the recording was stopped, or the encoder exited first.
                }
                catch (Exception exception)
                {
                    DebugHelper.WriteException(exception, "Feeding captured video to the encoder failed.");
                }
                finally
                {
                    try { destination.Close(); } catch { /* the encoder already closed it */ }
                }
            }, token);
        }

        private void StopInputPump(Task inputPump)
        {
            if (inputPump == null) return;

            try
            {
                inputCancellation?.Cancel();
                inputPump.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception)
            {
                DebugHelper.WriteException(exception, "Stopping the encoder input pump failed.");
            }
            finally
            {
                inputCancellation?.Dispose();
                inputCancellation = null;
            }
        }

        /// <summary>
        /// Asks the process to finish the way a user pressing Ctrl+C would. ffmpeg responds by
        /// flushing and closing the output file, which a kill would not do.
        /// </summary>
        public bool Interrupt()
        {
            if (!IsProcessRunning || process == null) return false;

            try
            {
                return LibC.SendSignal(process.Id, LibC.SIGINT) == 0;
            }
            catch (Exception exception)
            {
                DebugHelper.WriteException(exception, "Interrupting the external process failed.");
                return false;
            }
        }

        public void WriteInput(string input)
        {
            if (IsProcessRunning && process != null && process.StartInfo != null && process.StartInfo.RedirectStandardInput)
            {
                process.StandardInput.WriteLine(input);
            }
        }

        public virtual void Close()
        {
            if (IsProcessRunning && process != null)
            {
                process.CloseMainWindow();
            }
        }

        public void Dispose()
        {
            if (process != null)
            {
                process.Dispose();
            }
        }
    }
}