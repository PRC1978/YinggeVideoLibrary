using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using StackPanel = System.Windows.Controls.StackPanel;

namespace YinggeVideoLibrary;

public partial class MainWindow
{
    ListBox? crossSourceRelationResults;
    TextBlock? crossSourceRelationStatus;
    TextBlock? crossSourceRelationEvidence;
    Button? crossSourcePreviewButton;
    Button? crossSourceReviewJavDbButton;
    Button? crossSourceReviewDatabaseButton;
    CrossSourceRelationPreview? crossSourceRelationPreview;
    bool crossSourceRelationBusy;

    void InitializeCrossSourceRelationBrowser(StackPanel parent)
    {
        var shell = new Border
        {
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(218, 227, 245)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12), Margin = new Thickness(0, 7, 0, 12)
        };
        var body = new StackPanel();
        shell.Child = body;
        body.Children.Add(new TextBlock { Text = "两站关系组合筛选", FontWeight = FontWeights.SemiBold, FontSize = 14 });
        body.Children.Add(new TextBlock
        {
            Text = "在上面的 JavDB 关系和这里的 JAVDatabase 关系中各选一项。分别读取每站最多前三页，按明确番号在本地求交集。先读取 JavDB 作品详情；演员姓名搜索卡不能直接作为组合证据。",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 8)
        });
        crossSourcePreviewButton = CatalogButton("预览两站共同作品", true);
        crossSourcePreviewButton.Click += CrossSourcePreview;
        body.Children.Add(crossSourcePreviewButton);
        crossSourceRelationStatus = new TextBlock
        {
            Text = "只显示两站已核对关系目录中共同出现的候选；未列出不代表作品不存在。",
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 7)
        };
        body.Children.Add(crossSourceRelationStatus);
        crossSourceRelationResults = new ListBox
        {
            Height = 140, DisplayMemberPath = nameof(CrossSourceRelationCandidate.DisplayName),
            BorderBrush = new SolidColorBrush(Color.FromRgb(218, 227, 245))
        };
        crossSourceRelationResults.SelectionChanged += (_, _) => ShowCrossSourceEvidence();
        body.Children.Add(crossSourceRelationResults);
        crossSourceRelationEvidence = new TextBlock
        {
            Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 8)
        };
        body.Children.Add(crossSourceRelationEvidence);
        var actions = new WrapPanel();
        crossSourceReviewJavDbButton = CatalogButton("按 JavDB 详情送审");
        crossSourceReviewJavDbButton.Click += async (_, _) => await CrossSourceSendToReviewAsync(useJavDb: true);
        crossSourceReviewDatabaseButton = CatalogButton("按 JAVDatabase 详情送审");
        crossSourceReviewDatabaseButton.Click += async (_, _) => await CrossSourceSendToReviewAsync(useJavDb: false);
        actions.Children.Add(crossSourceReviewJavDbButton);
        actions.Children.Add(crossSourceReviewDatabaseButton);
        body.Children.Add(actions);
        SetCrossSourceRelationButtons();
        parent.Children.Add(shell);
    }

    void SetCrossSourceRelationButtons()
    {
        if (crossSourcePreviewButton is not null)
            crossSourcePreviewButton.IsEnabled = !crossSourceRelationBusy && !javDbCatalogBusy && !javDatabaseCatalogBusy;
        var selected = crossSourceRelationResults?.SelectedItem is CrossSourceRelationCandidate;
        if (crossSourceReviewJavDbButton is not null)
            crossSourceReviewJavDbButton.IsEnabled = !crossSourceRelationBusy && selected;
        if (crossSourceReviewDatabaseButton is not null)
            crossSourceReviewDatabaseButton.IsEnabled = !crossSourceRelationBusy && selected;
    }

    async void CrossSourcePreview(object sender, RoutedEventArgs e)
    {
        if (crossSourceRelationBusy || javDbCatalogBusy || javDatabaseCatalogBusy) return;
        if (javDbCatalogVerifiedOrigin is not { } origin ||
            javDbCatalogLinks?.SelectedItem is not JavDbCatalogLink javDbLink ||
            javDatabaseCatalogIdols?.SelectedItem is not JavDatabaseRelationLink databaseLink)
        {
            crossSourceRelationStatus!.Text = "请先在两站各选一条来自已核对详情的演员、系列或标签关系。";
            return;
        }
        crossSourceRelationPreview = null;
        crossSourceRelationResults!.ItemsSource = null;
        crossSourceRelationEvidence!.Text = "";
        crossSourceRelationBusy = true;
        SetCrossSourceRelationButtons();
        crossSourceRelationStatus!.Text = "正在读取两站的公开关系目录…";
        try
        {
            var preview = await CrossSourceRelationBrowser.FetchPreviewAsync(origin, javDbLink, databaseLink);
            crossSourceRelationPreview = preview;
            crossSourceRelationResults.ItemsSource = preview.Candidates;
            crossSourceRelationResults.SelectedIndex = preview.Candidates.Count > 0 ? 0 : -1;
            var incomplete = preview.JavDbMayHaveMore || preview.JavDatabaseMayHaveMore;
            crossSourceRelationStatus.Text = $"JavDB {preview.JavDbPagesRead} 页、JAVDatabase {preview.JavDatabasePagesRead} 页；" +
                $"共同候选 {preview.Candidates.Count} 部。" +
                (incomplete ? "仅检查前几页，后续目录可能还有作品。" : "两个目录均已到达当前可见末页。") +
                "候选须读取详情并人工审核。";
            ShowCrossSourceEvidence();
        }
        catch (Exception ex)
        {
            crossSourceRelationStatus.Text = "组合预览失败：" + ex.Message + " 不会把它当作没有结果。";
            Log("跨来源关系组合预览失败：" + ex.Message);
        }
        finally { crossSourceRelationBusy = false; SetCrossSourceRelationButtons(); }
    }

    void ShowCrossSourceEvidence()
    {
        if (crossSourceRelationPreview is not { } preview || crossSourceRelationEvidence is null) return;
        if (crossSourceRelationResults?.SelectedItem is not CrossSourceRelationCandidate row)
        {
            crossSourceRelationEvidence.Text = "关系来源：" + preview.JavDbRelation.Url + "；" + preview.JavDatabaseRelation.Url;
            return;
        }
        crossSourceRelationEvidence.Text =
            $"关系：JavDB {preview.JavDbRelation.Kind}「{preview.JavDbRelation.Name}」{preview.JavDbRelation.Url}；" +
            $"JAVDatabase {preview.JavDatabaseRelation.Kind}「{preview.JavDatabaseRelation.Name}」{preview.JavDatabaseRelation.Url}\n" +
            $"候选：JavDB {row.JavDb.SourceUrl}；JAVDatabase {row.JavDatabase.SourceUrl}";
    }

    async Task CrossSourceSendToReviewAsync(bool useJavDb)
    {
        if (crossSourceRelationBusy || crossSourceRelationPreview is null ||
            crossSourceRelationResults?.SelectedItem is not CrossSourceRelationCandidate row) return;
        var video = ChooseVideoForTitleScrape();
        if (video is null) return;
        crossSourceRelationBusy = true;
        SetCrossSourceRelationButtons();
        crossSourceRelationStatus!.Text = "正在核对所选候选的完整详情…";
        try
        {
            MovieMetadata detail;
            if (useJavDb)
            {
                detail = (await ScrapeCandidateCache.GetOrFetchAsync(DataDir, "JavDB 详情", row.JavDb.SourceUrl,
                    async () => [await JavDbScraper.FetchCandidateDetailsAsync(row.JavDb)])).First();
                RequireVerifiedJavDbCatalogLinks(row.JavDb, detail);
            }
            else
            {
                detail = await JavDatabaseScraper.FetchCandidateDetailsAsync(row.JavDatabase);
                if (!ScrapeCandidateCache.DetailMatchesCandidate(row.JavDatabase, detail))
                    throw new InvalidOperationException("JAVDatabase 详情未核对为所选作品。 ");
            }
            if (!ScrapeCodeIdentity.Same(row.Code, detail.Code))
                throw new InvalidOperationException("作品番号与两站共同候选不一致。 ");
            await AdditionalScrapers.DownloadPosterAsync(detail, video.Id, DataDir);
            await StoreScrapeReviewAsync(video, detail, forcePending: true);
            RefreshReviewQueue(); RenderCards();
            crossSourceRelationStatus.Text = $"{row.Code} 已提交人工审核；通过前不会修改视频资料。";
        }
        catch (Exception ex)
        {
            crossSourceRelationStatus.Text = "提交失败：" + ex.Message + " 请检查审核队列后重试。";
            Log("跨来源候选送审失败：" + ex.Message);
        }
        finally { crossSourceRelationBusy = false; SetCrossSourceRelationButtons(); }
    }
}
