using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using WpfBrushes = System.Windows.Media.Brushes;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    TextBox? javDatabaseActorName;

    void InitializeJavDatabaseActorSearch(StackPanel body)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        javDatabaseActorName = new TextBox
        {
            Height = 35, Padding = new Thickness(9, 5, 9, 5),
            ToolTip = "按演员姓名检索 JAVDatabase 公开演员卡，逐项核对演员页和作品详情；不发送本地路径"
        };
        var find = CatalogButton("按演员姓名查找");
        find.Click += JavDatabaseActorSearch;
        DockPanel.SetDock(find, Dock.Right);
        row.Children.Add(find);
        row.Children.Add(javDatabaseActorName);
        body.Children.Add(row);
        body.Children.Add(new TextBlock
        {
            Text = "姓名查找最多核对 2 个同名演员卡和各自前 3 部作品；核对成功后可直接浏览该演员的公开作品。",
            Foreground = WpfBrushes.SlateGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
    }

    async void JavDatabaseActorSearch(object sender, RoutedEventArgs e)
    {
        if (javDatabaseCatalogBusy || javDatabaseActorName is null) return;
        var name = javDatabaseActorName.Text.Trim();
        if (!JavDatabaseCatalog.TryActorSearchName(name, out _))
        { javDatabaseCatalogStatus!.Text = "请填写 2–80 字的演员姓名，不含路径或网址字符。"; return; }
        await RunJavDatabaseCatalogAsync(async () =>
        {
            var links = await JavDatabaseCatalog.FindActorLinksByNameAsync(name);
            ClearJavDatabaseCatalogPages();
            javDatabaseCatalogIdols!.ItemsSource = links;
            javDatabaseCatalogIdols.SelectedIndex = links.Count > 0 ? 0 : -1;
            javDatabaseCatalogStatus!.Text = links.Count > 0
                ? $"已通过公开搜索、演员页及作品详情核实 {links.Count} 个同名演员链接。选择后浏览作品。"
                : "公开搜索没有核实到同名演员链接；只检查有限的结果，不代表演员不存在。";
        });
    }
}
