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

#nullable enable

using Avalonia.Controls;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;
using DrawingBitmap = SkiaSharp.SKBitmap;

namespace ShareX;

public partial class AboutWindow : Window
{
    private readonly AvaloniaBitmap _logoBitmap;
    private readonly ShareXClickerControl _clicker;

    public AboutWindow()
    {
        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();

        using DrawingBitmap logo = ShareXResources.Logo;
        using Stream logoStream = logo.GetStream();
        logoStream.Position = 0;
        _logoBitmap = new AvaloniaBitmap(logoStream);
        LogoImage.Source = _logoBitmap;
        _clicker = new ShareXClickerControl(new ShareXClickerState(),
            LogoImage, ClickerOverlay, ClickerParticleOverlay, SectionsViewer, ClickerHost, AboutPanel.Background,
            [BrandText, ProductNameText, AboutDetailsPanel, CopyrightText]);

        ProductNameText.Text = ApplicationInfo.Title;
        CopyrightText.Text = Strings.AboutWindow_Copyright;
        SectionsControl.ItemsSource = CreateSections();

#if STEAM
        BuildText.Text = Strings.AboutWindow_SteamBuild;
        BuildText.IsVisible = true;
#elif MicrosoftStore
        BuildText.Text = Strings.AboutWindow_MicrosoftStoreBuild;
        BuildText.IsVisible = true;
#endif

        Closed += (_, _) =>
        {
            _clicker.Dispose();
            _logoBitmap.Dispose();
        };
    }

            private void OnLinkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is HyperlinkButton { DataContext: AboutLinkItem item })
        {
            URLHelpers.OpenURL(item.Uri.AbsoluteUri);
        }
    }

    private static IReadOnlyList<AboutSection> CreateSections()
    {
        return
        [
            new AboutSection(Strings.AboutWindow_Team,
            [
                Link("Jaex", Links.Jaex),
                Link("McoreD", Links.McoreD)
            ]),
            new AboutSection(Strings.AboutWindow_Links,
            [
                Link(Strings.AboutWindow_Website, Links.Website),
                Link(Strings.AboutWindow_ProjectPage, Links.GitHub),
                Link(Strings.AboutWindow_Changelog, Links.Changelog),
                Link(Strings.AboutWindow_PrivacyPolicy, Links.PrivacyPolicy),
                Link(Strings.AboutWindow_Donate, Links.Donate),
                Link("X", Links.X),
                Link("Discord", Links.Discord),
                Link("Reddit", Links.Reddit),
                Link("Steam", Links.Steam),
                Link("Microsoft Store", Links.MicrosoftStore)
            ])
        ];
    }

    private static AboutLinkItem Link(string label, string url) => new(label, url, new Uri(url));
}

public sealed record AboutSection(string Title, IReadOnlyList<AboutLinkItem> Items);

public sealed record AboutLinkItem(string Label, string DisplayText, Uri Uri);
