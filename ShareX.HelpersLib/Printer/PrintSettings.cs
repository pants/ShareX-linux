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
using System.Drawing;
using System.Globalization;

namespace ShareX.HelpersLib
{
    /// <summary>Paper sizes offered for printing, in hundredths of an inch.</summary>
    public enum PaperSize
    {
        A4,
        Letter,
        Legal,
        A3,
        A5
    }

    [Serializable]
    public class PrintSettings
    {
        public int Margin { get; set; }
        public bool AutoRotateImage { get; set; }
        public bool AutoScaleImage { get; set; }
        public bool AllowEnlargeImage { get; set; }
        public bool CenterImage { get; set; }
        public XmlFont TextFont { get; set; }
        public bool ShowPrintDialog { get; set; }
        public string DefaultPrinterOverride { get; set; }
        public PaperSize PaperSize { get; set; }

        /// <summary>Margin around text pages, in hundredths of an inch.</summary>
        public int TextMargin { get; set; }

        public PrintSettings()
        {
            PaperSize = GetRegionalDefaultPaperSize();
            TextMargin = 100;
            Margin = 5;
            AutoRotateImage = true;
            AutoScaleImage = true;
            AllowEnlargeImage = false;
            CenterImage = false;
            TextFont = new XmlFont("Arial", 10);
            ShowPrintDialog = true;
            DefaultPrinterOverride = "";
        }

        /// <summary>The selected paper size in hundredths of an inch, portrait.</summary>
        public Size GetPaperSize() => PaperSize switch
        {
            PaperSize.Letter => new Size(850, 1100),
            PaperSize.Legal => new Size(850, 1400),
            PaperSize.A3 => new Size(1169, 1654),
            PaperSize.A5 => new Size(583, 827),
            _ => new Size(827, 1169)
        };

        public Rectangle GetTextMarginBounds(Size paper)
        {
            int margin = Math.Max(0, TextMargin);
            return new Rectangle(margin, margin,
                Math.Max(1, paper.Width - margin * 2), Math.Max(1, paper.Height - margin * 2));
        }

        /// <summary>Letter in the handful of countries that use it, A4 everywhere else.</summary>
        private static PaperSize GetRegionalDefaultPaperSize()
        {
            try
            {
                string region = new RegionInfo(CultureInfo.CurrentCulture.Name).TwoLetterISORegionName;

                return region is "US" or "CA" or "MX" or "PH" or "CL" or "CO" ? PaperSize.Letter : PaperSize.A4;
            }
            catch
            {
                return PaperSize.A4;
            }
        }
    }
}