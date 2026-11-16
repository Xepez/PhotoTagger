using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhotoTagger.Models;
using PhotoTagger.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PhotoTagger
{
    public sealed partial class MainWindow : Window
    {
        // Data
        private readonly MediaDatabase _db = new();
        private readonly MediaRepository _repo;
        private readonly MediaScanner _scanner = new();

        // Services
        private readonly ThumbnailService _thumbnailService = new();
        private readonly PreviewService _previewService = new();
        private readonly SemaphoreSlim _thumbnailSemaphore = new(6);

        // UI state. The tag list is real data; the UI just displays it.
        private MediaItem _currentMediaSelected;

        private readonly ObservableCollection<string> _currentTags = new();
        private List<string> _allTagNames = new();
        private List<string> _allFolderNames = new();

        // The files of the folder currently selected in folder mode, in Explorer order.
        private List<MediaItem> _folderMedia = new();
        private int _folderIndex = -1;

        private bool _filtersReady;       // false while the window is starting / controls are being reset
        private bool _updatingTagList;    // true while we rebuild the sidebar tag list ourselves
        private bool _updatingFolderList; // true while we rebuild the sidebar folder list ourselves
        private bool _scanning;

        // Each bumps when a newer request starts, so a slow older one can tell it is stale.
        private int _filterVersion;
        private int _selectionVersion;

        public MainWindow()
        {
            _repo = new MediaRepository(_db);

            InitializeComponent();

            PreviewTagList.ItemsSource = _currentTags;
            MediaGrid.ContainerContentChanging += ThumbnailGrid_ContainerContentChanging;
            Closed += (_, _) => StopVideo();

            _ = LoadLibraryAsync();
        }

        #region Start Up
        private async Task LoadLibraryAsync()
        {
            try
            {
                // A failed backup must not stop the library from loading, but the user should know.
                try
                {
                    await Task.Run(() => _db.BackupIfDue());
                }
                catch (Exception ex)
                {
                    PopupMessage(true, "Daily backup failed: " + ex.Message);
                }

                await RefreshTagListAsync();
                await RefreshFolderListAsync();
                SelectAllFoldersFilter();
                await ApplyMediaListFilterAsync();

                var roots = await Task.Run(() => _repo.GetRoots());
                if (roots.Count == 0)
                    UpdateStatusText("Click \"Add Folder\" to start building your library.");
            }
            catch (Exception ex)
            {
                PopupMessage(true, "Startup error: " + ex.Message);
            }
            finally
            {
                _filtersReady = true;
            }
        }
        #endregion

        #region Header Buttons

        private async void SwitchViewButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO
            PopupMessage(true, "Switch View - WIP");
        }

        private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (_scanning) return;

            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder == null) return;

            string path = folder.Path;

            try
            {
                await Task.Run(() => _repo.AddRoot(path));
                await ScanAsync(new List<string> { path });
            }
            catch (Exception ex)
            {
                PopupMessage(true, "Could not add folder: " + ex.Message);
            }
        }

        private async void RescanButton_Click(object sender, RoutedEventArgs e)
        {
            if (_scanning) return;

            try
            {
                var roots = await Task.Run(() => _repo.GetRoots());
                if (roots.Count == 0)
                {
                    UpdateStatusText("No folders yet. Click \"Add Folder\" first.");
                    return;
                }

                await ScanAsync(roots);
            }
            catch (Exception ex)
            {
                PopupMessage(true, "Rescan error: " + ex.Message);
            }
        }

        private async Task ScanAsync(List<string> roots)
        {
            if (!_filtersReady) return;

            _scanning = true;
            AddFolderButton.IsEnabled = false;
            RescanButton.IsEnabled = false;
            SwitchViewButton.IsEnabled = false;
            ScanProgress.Visibility = Visibility.Visible;

            // Constructed on the UI thread, so the callback runs on the UI thread too.
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                ScanProgress.IsIndeterminate = false;
                ScanProgress.Maximum = Math.Max(1, p.Total);
                ScanProgress.Value = p.Done;
            });

            int added = 0, moved = 0, updated = 0, missing = 0, unreadable = 0;

            try
            {
                foreach (string root in roots)
                {
                    if (!Directory.Exists(root))
                    {
                        // Drive unplugged, etc. Leave its entries alone.
                        UpdateStatusText($"Skipped (not available): {root}", 4);
                        continue;
                    }

                    UpdateStatusText($"Scanning {root} ...");
                    ScanProgress.IsIndeterminate = true;

                    // TODO - Review
                    SyncResult result = await Task.Run(() =>
                    {
                        var files = _scanner.Scan(root);
                        return _repo.SyncRoot(root, files, progress);
                    });

                    added += result.Added;
                    moved += result.Moved;
                    updated += result.Updated;
                    missing += result.MarkedMissing;
                    unreadable += result.Unreadable;
                }

                UpdateStatusText($"Scan complete: {added} new, {moved} moved, {updated} changed, {missing} missing, {unreadable} unreadable.", 4);
            }
            catch (Exception ex)
            {
                PopupMessage(true, "Scan error: " + ex.Message);
            }
            finally
            {
                ScanProgress.Visibility = Visibility.Collapsed;
                AddFolderButton.IsEnabled = true;
                RescanButton.IsEnabled = true;
                SwitchViewButton.IsEnabled = true;
                _scanning = false;
            }

            await RefreshTagListAsync();
            await RefreshFolderListAsync();
            await ApplyMediaListFilterAsync();
        }
        #endregion

        #region Filtering
        private MediaFilter BuildMediaGridFilter()
        {
            MediaKind? mediaKind = KindFilter.SelectedIndex switch
            {
                1 => MediaKind.Image,
                2 => MediaKind.Video,
                3 => MediaKind.Folder,
                _ => null
            };

            return new MediaFilter
            {
                Text = SearchFileNameBox.Text,
                MediaKind = mediaKind,
                UntaggedOnly = UntaggedOnly.IsChecked == true,
                MatchAllTags = MatchAllToggle.IsOn,
                Tags = TagList.SelectedItems.OfType<TagInfo>().Select(t => t.Name).ToList(),
                FolderPath = FolderList.SelectedItems.OfType<FolderInfo>().Select(t => t.Label).ToList()
            };
        }

        private MediaFilter BuildCustomFilter(string? searchText = null, MediaKind? mediaKind = null, bool? untaggedOnly = null, bool? matchAllTags = null, List<string>? tags = null, List<string>? folderPaths = null, bool? isFolder = null)
        {
            return new MediaFilter
            {
                Text = searchText,
                MediaKind = mediaKind,
                UntaggedOnly = untaggedOnly,
                MatchAllTags = matchAllTags,
                Tags = tags,
                FolderPath = folderPaths,
                IsFolder = isFolder
            };
        }

        private async Task ApplyMediaListFilterAsync()
        {
            int version = ++_filterVersion;
            MediaFilter filter = BuildMediaGridFilter();

            try
            {
                List<MediaItem> items = await Task.Run(() => _repo.Query(filter));
                if (version != _filterVersion) return;

                // Capture scroll offset and check whether the list is unchanged
                var scrollViewer = FindChild<ScrollViewer>(MediaGrid);
                double offset = scrollViewer?.VerticalOffset ?? 0;

                bool sameList =
                    MediaGrid.ItemsSource is List<MediaItem> old &&
                    old.Count == items.Count &&
                    old.Select(i => i.FilePath).SequenceEqual(items.Select(i => i.FilePath));

                MediaGrid.ItemsSource = items;
                CountText.Text = $"{items.Count:N0} item(s)";

                // Restore only if the list matches
                if (sameList && scrollViewer is not null)
                {
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    {
                        MediaGrid.UpdateLayout();
                        scrollViewer.ChangeView(null, offset, null, disableAnimation: true);
                    });
                }
            }
            catch (Exception ex)
            {
                PopupMessage(true, "Media Filter error: " + ex.Message);
            }
        }

        private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var result = FindChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }
        #endregion

        #region On Change Functions
        private void SearchFileNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_filtersReady) return;
            _ = ApplyMediaListFilterAsync();
        }

        private void FilterTagsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_filtersReady) return;
            _ = RefreshTagListAsync();
        }

        private void FilterFolderSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_filtersReady) return;
            _ = RefreshFolderListAsync();
        }

        private void KindFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_filtersReady) return;
            _ = ApplyMediaListFilterAsync();
        }

        private void FilterTagToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_filtersReady) return;

            // "Untagged only" and a tag selection contradict each other, so disable the list.
            TagList.IsEnabled = UntaggedOnly.IsChecked != true;
            _ = ApplyMediaListFilterAsync();
        }

        private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_filtersReady || _updatingFolderList) return;
            _ = ApplyMediaListFilterAsync();
        }

        private void TagList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_filtersReady || _updatingTagList) return;
            _ = ApplyMediaListFilterAsync();
        }

        private void FilterSelectAllFoldersToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_filtersReady || _updatingFolderList) return;

            if (FilterSelectAllFolders.IsOn)
            {
                SelectAllFoldersFilter();
            }
            else
            {
                FolderList.SelectedItems.Clear();
            }
        }

        private void SaveFolderThumbnail_Click(object sender, RoutedEventArgs e)
        {
            if (!_filtersReady || _currentMediaSelected.Kind != MediaKind.Folder) return;

            _repo.UpdateFolderThumbnail(_currentMediaSelected.Id, _folderMedia[_folderIndex].FilePath);
            _ = ApplyMediaListFilterAsync();
        }

        private void SelectAllFoldersFilter()
        {
            FolderList.SelectedItems.Clear();

            foreach (FolderInfo folder in (List<FolderInfo>)FolderList.ItemsSource)
            {
                FolderList.SelectedItems.Add(folder);
            }
        }

        private async void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
        {
            _filtersReady = false;   // reset everything without firing a query per control
            try
            {
                SearchFileNameBox.Text = string.Empty;
                KindFilter.SelectedIndex = 0;
                UntaggedOnly.IsChecked = false;
                MatchAllToggle.IsOn = true;
                TagList.IsEnabled = true;
                TagList.SelectedItems.Clear();
                FolderList.SelectedItems.Clear();
            }
            finally
            {
                _filtersReady = true;
            }

            await ApplyMediaListFilterAsync();
        }

        #endregion

        // Rebuilds the sidebar tag list (names + counts for the current mode) and keeps the selection.
        private async Task RefreshTagListAsync()
        {
            var selectedNames = TagList.SelectedItems
                .OfType<TagInfo>()
                .Select(t => t.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            //TagTarget target = CurrentTarget;
            List<TagInfo> tags;
            List<string> allNames;
            string filter = FilterTagsSearchBox.Text;

            if (filter == default)
                filter = "";

            try
            {
                tags = await Task.Run(() => _repo.GetTagsWithCounts(filter));
                allNames = await Task.Run(() => _repo.GetAllTagNames());
            }
            catch (Exception ex)
            {
                PopupMessage(true, "Could not load tags: " + ex.Message);
                return;
            }

            _updatingTagList = true;
            try
            {
                _allTagNames = allNames;
                TagList.ItemsSource = tags;

                foreach (TagInfo tag in tags)
                {
                    if (selectedNames.Contains(tag.Name))
                        TagList.SelectedItems.Add(tag);
                }
            }
            finally
            {
                _updatingTagList = false;
            }

            // A selected tag that no longer exists (last use removed) changes the filter.
            if (_filtersReady && TagList.SelectedItems.Count != selectedNames.Count)
                await ApplyMediaListFilterAsync();
        }
        private async Task RefreshFolderListAsync()
        {
            var selectedNames = FolderList.SelectedItems
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<FolderInfo> folders;
            string filter = FilterFolderSearchBox.Text;

            if (filter == default)
                filter = "";

            try
            {
                folders = await Task.Run(() => _repo.GetFolders(filter));
            }
            catch (Exception ex)
            {
                PopupMessage(true, "Could not load folders: " + ex.Message);
                return;
            }

            _updatingFolderList = true;
            try
            {
                _allFolderNames = folders.Select(f => f.Label).ToList();
                FolderList.ItemsSource = folders;

                foreach (FolderInfo folder in folders)
                {
                    if (selectedNames.Contains(folder.Label))
                        FolderList.SelectedItems.Add(folder);
                }
            }
            finally
            {
                _updatingFolderList = false;
            }

            // A selected folder that no longer exists (last use removed) changes the filter.
            if (_filtersReady && FolderList.SelectedItems.Count != selectedNames.Count)
                await ApplyMediaListFilterAsync();
        }

        // ------------------------------------------------------------------
        // Thumbnails: loaded only for cards currently on screen (photos and folder covers)
        // ------------------------------------------------------------------

        private void ThumbnailGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is not IThumbnailItem item) return;

            if (args.InRecycleQueue)
            {
                item.Thumbnail = null;   // scrolled away: free the memory
                return;
            }

            if (item.Thumbnail == null)
                _ = LoadThumbnailAsync(item);
        }

        private async Task LoadThumbnailAsync(IThumbnailItem item)
        {
            string? path = item.ThumbnailSourcePath;
            if (path == null || item.IsLoadingThumbnail) return;
            item.IsLoadingThumbnail = true;

            await _thumbnailSemaphore.WaitAsync();
            try
            {
                item.Thumbnail = await _thumbnailService.GetThumbnailAsync(path, item.ThumbnailKind);
            }
            finally
            {
                _thumbnailSemaphore.Release();
                item.IsLoadingThumbnail = false;
            }
        }

        // ------------------------------------------------------------------
        // Selection and preview: photos & videos
        // ------------------------------------------------------------------

        private async void MediaGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ResetPreview();
            int version = _selectionVersion; // TODO - Needed _selectionVersion?

            var selected = MediaGrid.SelectedItems.OfType<MediaItem>().ToList();
            TagEditorContainer.IsEnabled = selected.Count > 0;

            if (selected.Count == 0)
                return;

            if (selected.Count > 1)
            {
                PreviewFileName.Text = $"{selected.Count} items selected";
                SelectionHint.Text = "A tag you add is applied to all selected items.";
                return;
            }

            MediaItem mediaItem = selected[0];
            _currentMediaSelected = mediaItem;
            PreviewFileName.Text = mediaItem.FileName;
            PreviewFilePath.Text = mediaItem.FilePath;
            SelectionHint.Text = string.Empty;

            if (mediaItem.Kind == MediaKind.Folder)
            {
                FolderGridSelected(mediaItem, version);
            }
            else
            {
                MediaGridSelected(mediaItem, version);
            }
        }

        private async void MediaGridSelected(MediaItem mediaItem, int version)
        {
            try
            {
                var tags = await Task.Run(() => _repo.GetTags(mediaItem.Id));
                if (version != _selectionVersion) return;   // user already clicked something else

                foreach (string tag in tags)
                    _currentTags.Add(tag);

                await ShowPreviewAsync(mediaItem, version);
            }
            catch (Exception ex)
            {
                PopupMessage(true, "Could not load item: " + ex.Message);
            }
        }

        private async void FolderGridSelected(MediaItem mediaItem, int version)
        {
            try
            {
                MediaFilter filter = BuildCustomFilter(folderPaths: new List<string>([mediaItem.FilePath]), isFolder: false);
                List<MediaItem> media = await Task.Run(() => _repo.Query(filter));
                if (version != _selectionVersion) return;

                _folderMedia = media;

                if (media.Count > 0)
                    await ShowFolderIndexAsync(0, version);
            }
            catch (Exception ex)
            {
                PopupMessage(true, "Could not open folder: " + ex.Message);
            }
        }

        // Clears the preview panel and invalidates any preview still loading.
        private void ResetPreview()
        {
            ++_selectionVersion;

            StopVideo();
            PreviewImage.Source = null;
            _currentTags.Clear();
            TagInput.Text = "";

            _folderMedia = new List<MediaItem>();
            _folderIndex = -1;
            UpdateNavigation();

            TagEditorContainer.IsEnabled = false;
            PreviewFileName.Text = "No selection";
            PreviewFilePath.Text = string.Empty;
            SelectionHint.Text = "Select an item to see details and add tags.";
        }

        private async Task ShowPreviewAsync(MediaItem item, int version)
        {
            StopVideo();

            switch (item.Kind)
            {
                case MediaKind.Image:
                    PreviewImage.Visibility = Visibility.Visible;
                    PreviewPlayer.Visibility = Visibility.Collapsed;

                    var preview = await _previewService.LoadPreviewAsync(item.FilePath);
                    if (version != _selectionVersion) return;

                    PreviewImage.Source = preview;

                    break;
                case MediaKind.Video:
                    PreviewImage.Visibility = Visibility.Collapsed;
                    PreviewPlayer.Visibility = Visibility.Visible;

                    try
                    {
                        StorageFile file = await StorageFile.GetFileFromPathAsync(item.FilePath);
                        if (version != _selectionVersion) return;

                        PreviewPlayer.Source = MediaSource.CreateFromStorageFile(file);   // paused until you press play
                    }
                    catch (Exception ex)
                    {
                        SelectionHint.Text = "Could not open video: " + ex.Message;
                    }

                    break;
                case MediaKind.Folder:
                    PreviewImage.Visibility = Visibility.Collapsed;
                    PreviewPlayer.Visibility = Visibility.Collapsed;
                    FolderNav.Visibility = Visibility.Visible;

                    await ShowFolderIndexAsync(0, version);

                    break;
                default:
                    PreviewImage.Visibility = Visibility.Visible;
                    PreviewPlayer.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        private void StopVideo()
        {
            if (PreviewPlayer.Source is MediaSource source)
            {
                PreviewPlayer.MediaPlayer?.Pause();
                PreviewPlayer.Source = null;
                source.Dispose();
            }
        }

        //// ------------------------------------------------------------------
        //// Selection and preview: folders (open the first item, then prev/next)
        //// ------------------------------------------------------------------

        // --------------------------- TODO - Continue working on preview here -------------------------------------------------------------------------------------------------------------------

        private async Task ShowFolderIndexAsync(int index, int version)
        {
            _folderIndex = index;
            UpdateNavigation();

            await ShowPreviewAsync(_folderMedia[index], version);
        }

        private void UpdateNavigation()
        {
            bool show = _folderMedia.Count > 0 && _folderIndex >= 0;

            FolderNav.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            PrevButton.IsEnabled = show && _folderIndex > 0;
            NextButton.IsEnabled = show && _folderIndex < _folderMedia.Count - 1;

            NavText.Text = show
                ? $"{_folderIndex + 1} / {_folderMedia.Count}   ·   {_folderMedia[_folderIndex].FileName}"
                : string.Empty;
        }

        private async Task NavigateAsync(int delta)
        {
            int newIndex = _folderIndex + delta;
            if (newIndex < 0 || newIndex >= _folderMedia.Count) return;

            int version = ++_selectionVersion;   // supersedes any preview still loading
            await ShowFolderIndexAsync(newIndex, version); // TODO - Fix
        }

        private async void PrevButton_Click(object sender, RoutedEventArgs e) => await NavigateAsync(-1);

        private async void NextButton_Click(object sender, RoutedEventArgs e) => await NavigateAsync(+1);

        // ------------------------------------------------------------------
        // Tagging (files or folders, depending on the browse mode)
        // ------------------------------------------------------------------

        private List<long> GetSelectedIds() => MediaGrid.SelectedItems.OfType<MediaItem>().Select(i => i.Id).ToList();

        private long? GetSingleSelectedId()
        {
            var ids = GetSelectedIds();
            return ids.Count == 1 ? ids[0] : null;
        }

        private void AddTagButton_Click(object sender, RoutedEventArgs e)
        {
            _ = SubmitTagAsync(TagInput.Text);
        }

        private void TagInput_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            string text = args.ChosenSuggestion as string ?? args.QueryText;
            _ = SubmitTagAsync(text);
        }

        private void TagInput_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

            string text = sender.Text.Trim();

            sender.ItemsSource = text.Length == 0
                ? null
                : _allTagNames
                    .Where(name => name.Contains(text, StringComparison.OrdinalIgnoreCase))
                    .Take(8)
                    .ToList();
        }

        private async Task SubmitTagAsync(string? rawText)
        {
            string tag = MediaRepository.NormalizeTag(rawText);
            if (tag.Length == 0) return;

            var ids = GetSelectedIds();
            if (ids.Count == 0) return;

            TagInput.Text = string.Empty;

            try
            {
                await Task.Run(() => _repo.AddTag(ids, tag));

                if (ids.Count == 1)
                {
                    long id = ids[0];
                    var tags = await Task.Run(() => _repo.GetTags(id));

                    // Only touch the tag list if the user is still looking at the same thing.
                    if (GetSingleSelectedId() == id)
                    {
                        _currentTags.Clear();
                        foreach (string t in tags)
                            _currentTags.Add(t);
                    }
                }
                else
                {
                    UpdateStatusText($"Added \"{tag}\" to {ids.Count} items.", 4);
                }

                await RefreshTagListAsync();
            }
            catch (Exception ex)
            {
                PopupMessage(true, "Could not add tag: " + ex.Message);
            }
        }

        private async void RemoveTag_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tag }) return;

            if (GetSingleSelectedId() is not long id) return;

            try
            {
                await Task.Run(() => _repo.RemoveTag(id, tag));

                if (GetSingleSelectedId() == id)
                {
                    string? existing = _currentTags.FirstOrDefault(
                        t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));

                    if (existing != null)
                        _currentTags.Remove(existing);
                }

                await RefreshTagListAsync();
            }
            catch (Exception ex)
            {
                PopupMessage(true, "Could not remove tag: " + ex.Message);
            }
        }

        #region Helpers

        private async void UpdateStatusText(string statusText, int secondsTimeout = 0)
        {
            StatusText.Text = statusText;

            if (secondsTimeout != 0)
            {
                await Task.Run(() =>
                {
                    Task.Delay(secondsTimeout * 1000).Wait();
                });

                StatusText.Text = default;
            }
        }

        private async void PopupMessage(bool isError, string statusText)
        {
            ContentDialog dialogPopup = new ContentDialog()
            {
                XamlRoot = Content?.XamlRoot,
                Title = isError ? "Error" : "Notice",
                Content = statusText,
                CloseButtonText = "Ok"
            };

            await dialogPopup.ShowAsync();
        }

        #endregion
    }
}