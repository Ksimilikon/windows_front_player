// Small helpers for x:Bind function bindings and code-behind: palette brushes,
// formatting, dialogs, pickers.

using DreamPlayer.Core;
using DreamPlayer.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DreamPlayer.Views;

public static class Ui
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static Brush Accent => Res("DpAccent");
    public static Brush Text => Res("DpText");
    public static Brush Muted => Res("DpTextMuted");
    public static Brush Error => Res("DpError");
    public static Brush Warning => Res("DpWarning");

    // ── x:Bind functions ──

    /// the playing row is green.
    public static Brush PlayingBrush(bool playing) => playing ? Accent : Text;

    /// invalid tracks (file missing / unreadable) are red.
    public static Brush TitleBrush(bool invalid) => invalid ? Error : Text;

    public static Brush LevelBrush(LogLevel level) => level switch
    {
        LogLevel.Error => Error,
        LogLevel.Warn => Warning,
        LogLevel.Ok => Accent,
        LogLevel.Debug => Muted,
        _ => Text,
    };

    /// the user color label of a track; transparent without one.
    public static Brush ColorBrush(string? hex) =>
        ParseColor(hex) is { } c ? new SolidColorBrush(c) : new SolidColorBrush(Colors.Transparent);

    public static Visibility Visible(bool v) => v ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Hidden(bool v) => v ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleText(string? s) => Visible(!string.IsNullOrEmpty(s));

    public static string Number(int n) => n.ToString();
    public static string Dash(string? s) => string.IsNullOrEmpty(s) ? "—" : s;

    // ── colors ──

    /// `#RRGGBB` / `#AARRGGBB` -> color.
    public static Windows.UI.Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 6)
            s = "FF" + s;
        if (s.Length != 8 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v))
            return null;
        return Windows.UI.Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string ToHex(Windows.UI.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ── dialogs ──

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string text, string yes, bool destructive = false)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = yes,
            CloseButtonText = L.T("common.cancel"),
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
        if (destructive)
            dialog.PrimaryButtonStyle = DestructiveButtonStyle();
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task ShowTextAsync(XamlRoot root, string title, string text)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 480,
                Content = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    FontFamily = (FontFamily)Application.Current.Resources["DpMono"],
                    FontSize = 12,
                },
            },
            CloseButtonText = L.T("common.close"),
            RequestedTheme = ElementTheme.Dark,
        };
        await dialog.ShowAsync();
    }

    private static Style DestructiveButtonStyle()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Error));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Colors.White)));
        return style;
    }

    // ── context menus ──

    /// a context menu row; `null` in a list is a separator.
    public sealed record Item(string Text, string Glyph, Action Action, bool Enabled = true, bool Destructive = false);

    public static MenuFlyout Menu(params Item?[] items)
    {
        var menu = new MenuFlyout();
        foreach (var item in items)
        {
            if (item is null)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                continue;
            }
            var row = new MenuFlyoutItem
            {
                Text = item.Text,
                Icon = new FontIcon { Glyph = item.Glyph },
                IsEnabled = item.Enabled,
            };
            if (item.Destructive)
                row.Foreground = Error;
            var action = item.Action;
            row.Click += (_, _) => action();
            menu.Items.Add(row);
        }
        return menu;
    }

    /// the data item a context request came from (the row's DataContext).
    public static T? ContextItem<T>(ContextRequestedEventArgs e) where T : class =>
        (e.OriginalSource as FrameworkElement)?.DataContext as T;

    /// shows the menu at the pointer (or at the element for the keyboard).
    public static void ShowMenu(MenuFlyout menu, UIElement target, ContextRequestedEventArgs e)
    {
        if (e.TryGetPosition(target, out var point))
            menu.ShowAt(target, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
        else
            menu.ShowAt(e.OriginalSource as FrameworkElement ?? (FrameworkElement)target);
        e.Handled = true;
    }

    /// the data item under a double tap.
    public static T? TappedItem<T>(Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) where T : class =>
        (e.OriginalSource as FrameworkElement)?.DataContext as T;

    // ── pickers (desktop: bound to the window handle) ──

    private static T Bind<T>(T picker) where T : class
    {
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Window));
        return picker;
    }

    public static async Task<string?> PickFileAsync(params string[] extensions)
    {
        var picker = Bind(new FileOpenPicker { ViewMode = PickerViewMode.List });
        foreach (var e in extensions.DefaultIfEmpty("*"))
            picker.FileTypeFilter.Add(e);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public static async Task<IReadOnlyList<string>> PickFilesAsync(params string[] extensions)
    {
        var picker = Bind(new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.MusicLibrary });
        foreach (var e in extensions.DefaultIfEmpty("*"))
            picker.FileTypeFilter.Add(e);
        var files = await picker.PickMultipleFilesAsync();
        return files.Select(f => f.Path).ToList();
    }

    public static async Task<string?> PickFolderAsync()
    {
        var picker = Bind(new FolderPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary });
        picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    public static async Task<string?> PickSaveFileAsync(string suggestedName, string typeLabel, string extension)
    {
        var picker = Bind(new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName) });
        picker.FileTypeChoices.Add(typeLabel, [extension]);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    public static string[] ImageTypes => [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];
    public static string[] AudioTypes => [".mp3", ".flac", ".ogg", ".wav", ".m4a", ".mid", ".opus", ".aac"];

    // ── shell ──

    /// opens the file's folder in Explorer with the file selected.
    public static void ShowInFolder(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception e)
        {
            CoreLog.Warn("ui", $"explorer failed: {e.Message}");
        }
    }

    public static void OpenPath(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            CoreLog.Warn("ui", $"failed to open {path}: {e.Message}");
        }
    }

    /// comma-separated list -> trimmed non-empty items.
    public static List<string> SplitList(string? s) =>
        (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static string TrackLine(Track t) => t.ToString();
}
