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

using System.Security.Cryptography;
using ShareX.Linux.Interop;

namespace ShareX.Linux.Desktop;

/// <summary>
/// At-rest protection for the credentials ShareX stores in its settings, standing in for the
/// Windows Data Protection API.
/// </summary>
/// <remarks>
/// Linux has no per-user encryption service every desktop is guaranteed to have, so this keeps a
/// random 256-bit key in a file only the user can read and encrypts with AES-GCM. That matches
/// what DPAPI's CurrentUser scope actually buys you: it stops another account, a stray backup or
/// a synced settings file from exposing tokens in plain text. It is not protection against code
/// running as the same user, which could read the key just as easily - neither was DPAPI.
/// </remarks>
public static class DataProtection
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const uint OwnerReadWrite = 0b110_000_000; // 0600

    private static readonly Lock keyLock = new();
    private static byte[]? cachedKey;

    /// <summary>Overridable so tests and portable installs can place the key elsewhere.</summary>
    public static string KeyPath { get; set; } =
        Path.Combine(LinuxEnvironment.DataHome, "ShareX", "secret.key");

    public static byte[] Protect(byte[] plaintext, byte[]? associatedData = null)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        byte[] key = GetKey();
        byte[] result = new byte[NonceSize + plaintext.Length + TagSize];

        Span<byte> nonce = result.AsSpan(0, NonceSize);
        Span<byte> ciphertext = result.AsSpan(NonceSize, plaintext.Length);
        Span<byte> tag = result.AsSpan(NonceSize + plaintext.Length, TagSize);

        RandomNumberGenerator.Fill(nonce);

        using AesGcm aes = new(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        return result;
    }

    public static byte[] Unprotect(byte[] protectedData, byte[]? associatedData = null)
    {
        ArgumentNullException.ThrowIfNull(protectedData);

        if (protectedData.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("The protected value is too short to be valid.");
        }

        byte[] key = GetKey();
        int plaintextLength = protectedData.Length - NonceSize - TagSize;
        byte[] plaintext = new byte[plaintextLength];

        ReadOnlySpan<byte> nonce = protectedData.AsSpan(0, NonceSize);
        ReadOnlySpan<byte> ciphertext = protectedData.AsSpan(NonceSize, plaintextLength);
        ReadOnlySpan<byte> tag = protectedData.AsSpan(NonceSize + plaintextLength, TagSize);

        using AesGcm aes = new(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

        return plaintext;
    }

    private static byte[] GetKey()
    {
        if (cachedKey != null) return cachedKey;

        lock (keyLock)
        {
            if (cachedKey != null) return cachedKey;

            string path = KeyPath;

            if (File.Exists(path))
            {
                byte[] existing = File.ReadAllBytes(path);

                if (existing.Length == KeySize)
                {
                    cachedKey = existing;
                    return cachedKey;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            byte[] created = RandomNumberGenerator.GetBytes(KeySize);

            // Create the file empty first so the permissions are tightened before any key bytes land in it.
            using (FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                LibC.Chmod(path, OwnerReadWrite);
                stream.Write(created);
            }

            cachedKey = created;
            return cachedKey;
        }
    }

    /// <summary>Forgets the cached key, so the next call re-reads it from disk.</summary>
    public static void ResetCache()
    {
        lock (keyLock) cachedKey = null;
    }
}
