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
using System.Text;
using ShareX.Linux.Desktop;

namespace ShareX.HelpersLib
{
    /// <summary>
    /// Keeps stored credentials out of plain text in the settings files. Historically this wrapped
    /// the Windows Data Protection API; on Linux it is backed by <see cref="DataProtection"/>.
    /// </summary>
    public static class DPAPI
    {
        public static string Encrypt(string stringToEncrypt, string optionalEntropy = null)
        {
            byte[] data = Encoding.UTF8.GetBytes(stringToEncrypt);
            return Encrypt(data, optionalEntropy);
        }

        public static string Encrypt(byte[] data, string optionalEntropy = null)
        {
            return Encrypt(data, optionalEntropy == null ? null : Encoding.UTF8.GetBytes(optionalEntropy));
        }

        public static string Encrypt(byte[] data, byte[] optionalEntropy)
        {
            return Convert.ToBase64String(DataProtection.Protect(data, optionalEntropy));
        }

        public static string Decrypt(string encryptedString, string optionalEntropy = null)
        {
            return Decrypt(Convert.FromBase64String(encryptedString), optionalEntropy);
        }

        public static string Decrypt(byte[] encryptedData, string optionalEntropy = null)
        {
            return Decrypt(encryptedData, optionalEntropy == null ? null : Encoding.UTF8.GetBytes(optionalEntropy));
        }

        public static string Decrypt(byte[] encryptedData, byte[] optionalEntropy)
        {
            return Encoding.UTF8.GetString(DataProtection.Unprotect(encryptedData, optionalEntropy));
        }
    }
}
