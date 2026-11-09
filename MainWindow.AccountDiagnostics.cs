using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Button = System.Windows.Controls.Button;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    internal async Task<int> WriteAccountAboutDiagnosticAsync(string outputPath)
    {
        var checks = new Dictionary<string, bool>();
        var popupEvidence = new List<object>();
        var originalTheme = UiTheme.Current;
        object? error = null;
        void Check(string name, bool value) => checks[name] = value;
        static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            yield return root;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (var nested in Descendants(child)) yield return nested;
        }
        try
        {
            Check("isolatedDirectory", AppPaths.IsRedirected && AppPaths.RedirectError is null);
            if (AppPaths.IsRedirected)
            {
                var fixtureImage = Path.Combine(AppPaths.DataDirectory, "avatar-diagnostic-source.png");
                Directory.CreateDirectory(AppPaths.DataDirectory);
                var pixels = Enumerable.Repeat((byte)180, 12 * 8 * 4).ToArray();
                var fixture = BitmapSource.Create(12, 8, 96, 96, PixelFormats.Bgra32,
                    null, pixels, 12 * 4);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(fixture));
                using (var output = File.Create(fixtureImage)) encoder.Save(output);
                const string endpoint = "https://reports.yingevideo.top";
                LocalAccountAvatarStore.Save(endpoint, "avatar-test-a", fixtureImage);
                var stored = LocalAccountAvatarStore.Load(endpoint, "avatar-test-a");
                Check("avatar.savedAndCropped", stored is not null &&
                    stored.PixelWidth == stored.PixelHeight && stored.PixelWidth > 0);
                Check("avatar.accountIsolation", LocalAccountAvatarStore.Load(endpoint, "avatar-test-b") is null);
                Check("avatar.noRawUsernameInPath", !LocalAccountAvatarStore.PathFor(endpoint, "avatar-test-a")
                    .Contains("avatar-test-a", StringComparison.Ordinal));
                LocalAccountAvatarStore.Delete(endpoint, "avatar-test-a");
                Check("avatar.reset", LocalAccountAvatarStore.Load(endpoint, "avatar-test-a") is null);
                File.Delete(fixtureImage);
            }
            foreach (var theme in new[] { UiTheme.Classic, UiTheme.Glass, UiTheme.Cinema })
            {
                UiTheme.Apply(theme);
                ApplyInterfaceMode();
                ShowView("account");
                var accountBody = AccountPanel.Child;
                Check(theme + ".accountRoute", view == "account" && PageTitle.Text == "账号"
                    && AccountPanel.Visibility == Visibility.Visible && SettingsPanel.Visibility == Visibility.Collapsed);
                Check(theme + ".navigationHighlight", Equals(AccountNav.Tag, "active"));
                var buttons = Descendants(AccountPanel).OfType<Button>().Select(button => button.Content?.ToString()).ToHashSet();
                Check(theme + ".accountActions", new[] { "登录", "退出登录", "用邀请码注册", "上传错误日志",
                    "更换头像", "恢复默认头像" }.All(buttons.Contains));
                var accountText = Descendants(AccountPanel).OfType<TextBlock>().Select(item => item.Text).ToArray();
                Check(theme + ".accountIdentity", accountText.Contains("登录映格账号") &&
                    Descendants(AccountPanel).OfType<Border>().Any(item =>
                        System.Windows.Automation.AutomationProperties.GetName(item) == "账号头像"));
                Check(theme + ".noServerInput", !accountText.Any(item => item.Contains("服务器地址", StringComparison.Ordinal)));
                Check(theme + ".noVideoLayout", Cards.Visibility == Visibility.Collapsed && AllListPanel.Visibility == Visibility.Collapsed);
                ShowView("settings");
                Check(theme + ".settingsReturn", AccountPanel.Visibility == Visibility.Collapsed && SettingsPanel.Visibility == Visibility.Visible);
                var settingsButtons = Descendants(SettingsPanel).OfType<Button>().Select(button => button.Content?.ToString()).ToHashSet();
                Check(theme + ".aboutLinks", new[] { "关于与数据目录", "隐私政策", "仓库地址", "更新记录", "许可与第三方声明" }.All(settingsButtons.Contains));
                Check(theme + ".accountJump", settingsButtons.Contains("打开账号页面 · 登录与错误报告"));
                ShowView("account");
                Check(theme + ".formPreservedOnReturn", ReferenceEquals(accountBody, AccountPanel.Child));
                Check(theme + ".readablePanel", AccountPanel.Background is SolidColorBrush brush && brush.Color.A > 0);
                // Exercise the production application template without opening
                // a native popup window or showing the isolated library.
                var combo = new System.Windows.Controls.ComboBox
                {
                    Style = (Style)System.Windows.Application.Current.FindResource(typeof(System.Windows.Controls.ComboBox)),
                    Width = 240
                };
                var item = new ComboBoxItem
                {
                    Content = "搜刮来源示例",
                    Style = (Style)System.Windows.Application.Current.FindResource(typeof(ComboBoxItem))
                };
                combo.Items.Add(item);
                var templateApplied = combo.ApplyTemplate();
                var surface = combo.Template?.FindName("PopupSurface", combo) as Border;
                surface?.GetBindingExpression(Border.BackgroundProperty)?.UpdateTarget();
                item.GetBindingExpression(System.Windows.Controls.Control.ForegroundProperty)?.UpdateTarget();
                var background = (surface?.Background as SolidColorBrush)?.Color;
                var foreground = (item.Foreground as SolidColorBrush)?.Color;
                var contrast = background is { } bg && foreground is { } fg
                    ? ThemeContrast(OpaqueOver(fg, bg), bg) : 0;
                Check(theme + ".popupTemplateApplied", templateApplied && surface is not null);
                Check(theme + ".popupSurfaceOpaque", background?.A == 255);
                Check(theme + ".popupTextContrast", contrast >= 4.5);
                popupEvidence.Add(new { theme, templateApplied, surfaceFound = surface is not null,
                    background = background?.ToString(), backgroundAlpha = background?.A,
                    foreground = foreground?.ToString(), contrastRatio = contrast,
                    minimumContrast = 4.5, popupOpened = false });
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally { UiTheme.Apply(originalTheme); }
        var passed = error is null && checks.Count > 0 && checks.Values.All(value => value);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
        {
            passed, realLibraryRead = false, networkRequests = 0,
            scope = "Unshown isolated window; route and control assertions only, no rendered screenshots or login interaction",
            checks, popupEvidence, error
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
}
