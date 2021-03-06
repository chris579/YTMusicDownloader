﻿/*
    Copyright 2016 Christian Klemm

    Licensed under the Apache License, Version 2.0 (the "License");
    you may not use this file except in compliance with the License.
    You may obtain a copy of the License at

        http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law or agreed to in writing, software
    distributed under the License is distributed on an "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    See the License for the specific language governing permissions and
    limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Command;
using GalaSoft.MvvmLight.Messaging;
using NLog;
using YTMusicDownloader.Properties;
using YTMusicDownloader.ViewModel.Helpers;
using YTMusicDownloader.ViewModel.Messages;
using YTMusicDownloaderLib.DownloadManager;
using YTMusicDownloaderLib.Helpers;
using YTMusicDownloaderLib.Properties;
using YTMusicDownloaderLib.RetrieverEngine;
using YTMusicDownloaderLib.Workspaces;

namespace YTMusicDownloader.ViewModel
{
    public enum FilterMode
    {
        None,
        Downloaded,
        NotDownloaded,
        Downloading,
        Queued
    }

    internal class WorkspaceViewModel : ViewModelBase
    {
        #region Construction        

        /// <summary>
        ///     Initializes a new instance of the <see cref="WorkspaceViewModel" /> class.
        /// </summary>
        /// <param name="workspace">The workspace for the view model.</param>
        public WorkspaceViewModel(Workspace workspace)
        {
            Workspace = workspace;
            Name = Workspace.Name;
            PlaylistUrl = workspace.Settings.PlaylistUrl;
            DownloadingAllSongsText = Resources.MainWindow_CurrentWorkspace_SyncButtonLabel;
            SearchTextboxWatermark = Resources.MainWindow_CurrentWorkspace_SearchWatermarkDefault;

            Tracks = new ObservableImmutableList<PlaylistItemViewModel>();
            Tracks.CollectionChanged += TracksOnCollectionChanged;
            DisplayedTracks = new ObservableImmutableList<PlaylistItemViewModel>();
            PageSelectorViewModel = new PageSelectorViewModel(this);
            DownloadManager = new DownloadManager(Settings.Default.ParallelDownloads);
            WorkspaceSettingsViewModel = new WorkspaceSettingsViewModel(this);
            DisplayedTracksSource = new List<PlaylistItemViewModel>();
            Workspace.Settings.PropertyChanged += SettingsOnPropertyChanged;
            FilterModes = new Dictionary<FilterMode, string>();
            Settings.Default.PropertyChanged += ApplicationSettingsOnPropertyChanged;

            _watcher = new FileSystemWatcher
            {
                Path = Workspace.Path,
                NotifyFilter = NotifyFilters.FileName,
                Filter = "*.*"
            };

            _watcher.Created += WatcherOnCreated;
            _watcher.Deleted += WatcherOnDeleted;
            _watcher.Renamed += WatcherOnRenamed;
            _watcher.Error += WatcherOnError;

            SetupFilter();
        }

        #endregion

        #region Fields

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly FileSystemWatcher _watcher;
        private bool _initialized;

        private double _playlistFetchProgress;
        private bool _fetchingPlaylist;
        private bool _downloadingAllSongs;
        private int _downloadItemsRemaining;
        private string _downloadingAllSongsText;
        private int _downloadedTracks;
        private string _searchText;
        private string _searchTextboxWatermark;
        private bool _playlistUrlChanged;
        private FilterMode _selectedFilterMode;
        private bool _searchTextChangeInProgress;
        private int _downloadErrors;

        #endregion

        #region Properties

        public Workspace Workspace { get; }
        public DownloadManager DownloadManager { get; }
        public PageSelectorViewModel PageSelectorViewModel { get; }
        public WorkspaceSettingsViewModel WorkspaceSettingsViewModel { get; }

        public ObservableImmutableList<PlaylistItemViewModel> Tracks { get; }
        public ObservableImmutableList<PlaylistItemViewModel> DisplayedTracks { get; }
        public List<PlaylistItemViewModel> DisplayedTracksSource { get; }

        /// <summary>
        ///     Gets or sets the name.
        /// </summary>
        /// <value>
        ///     The name.
        /// </value>
        public string Name
        {
            get { return Workspace.Name; }
            set
            {
                Workspace.Name = value;
                RaisePropertyChanged(nameof(Name));
            }
        }

        /// <summary>
        ///     Gets or sets the playlist URL.
        /// </summary>
        /// <value>
        ///     The playlist URL.
        /// </value>
        public string PlaylistUrl
        {
            get { return Workspace.Settings.PlaylistUrl; }
            set
            {
                if (value != null)
                {
                    Workspace.SetPlaylistUrl(value);
                    RaisePropertyChanged(nameof(PlaylistUrl));
                    _playlistUrlChanged = true;
                }
            }
        }

        /// <summary>
        ///     Gets or sets the playlist fetch progress.
        /// </summary>
        /// <value>
        ///     The playlist fetch progress.
        /// </value>
        public double PlaylistFetchProgress
        {
            get { return _playlistFetchProgress; }
            set
            {
                _playlistFetchProgress = value;
                RaisePropertyChanged(nameof(PlaylistFetchProgress));
            }
        }

        /// <summary>
        ///     Gets or sets a value indicating whether the playlist items are fetched.
        /// </summary>
        /// <value>
        ///     <c>true</c> if is fetching the playlist items; otherwise, <c>false</c>.
        /// </value>
        public bool FetchingPlaylist
        {
            get { return _fetchingPlaylist; }
            set
            {
                _fetchingPlaylist = value;
                RaisePropertyChanged(nameof(FetchingPlaylist));
            }
        }

        /// <summary>
        ///     Gets or sets a value indicating whether the sync progress is active.
        /// </summary>
        /// <value>
        ///     <c>true</c> if progress is active; otherwise, <c>false</c>.
        /// </value>
        public bool DownloadingAllSongs
        {
            get { return _downloadingAllSongs; }
            set
            {
                _downloadingAllSongs = value;
                RaisePropertyChanged(nameof(DownloadingAllSongs));
                DownloadingAllSongsText = value
                    ? Resources.MainWindow_CurrentWorkspace_CancelSyncButtonLabel
                    : Resources.MainWindow_CurrentWorkspace_SyncButtonLabel;
            }
        }

        /// <summary>
        ///     Gets or sets the amount of items which are remaining to download.
        /// </summary>
        /// <value>
        ///     The download items remaining.
        /// </value>
        public int DownloadItemsRemaining
        {
            get { return _downloadItemsRemaining; }
            set
            {
                _downloadItemsRemaining = value;
                RaisePropertyChanged(nameof(DownloadItemsRemaining));
            }
        }

        public int DownloadedTracks
        {
            get { return _downloadedTracks; }
            set
            {
                _downloadedTracks = value;
                RaisePropertyChanged(nameof(DownloadedTracks));
            }
        }

        public int TrackCount => Tracks.Count;

        /// <summary>
        ///     Gets or sets the text displayed in the "Download all" button.
        /// </summary>
        public string DownloadingAllSongsText
        {
            get { return _downloadingAllSongsText; }
            set
            {
                _downloadingAllSongsText = value;
                RaisePropertyChanged(nameof(DownloadingAllSongsText));
            }
        }

        public string SearchText
        {
            get { return _searchText; }
            set
            {
                _searchText = value;
                RaisePropertyChanged(nameof(SearchText));
                SearchTextChanged();
            }
        }

        public Dictionary<FilterMode, string> FilterModes { get; }

        public string SearchTextboxWatermark
        {
            get { return _searchTextboxWatermark; }
            set
            {
                _searchTextboxWatermark = value;
                RaisePropertyChanged(nameof(SearchTextboxWatermark));
            }
        }

        public FilterMode SelectedFilterMode
        {
            get { return _selectedFilterMode; }
            set
            {
                _selectedFilterMode = value;
                RaisePropertyChanged(nameof(SelectedFilterMode));
                SearchTextChanged();
                OnPageNumberChanged();
            }
        }

        public string LastSync
        {
            get
            {
                return Workspace.Settings.LastSync == default(DateTime)
                    ? Resources.MainWindow_CurrentWorkspace_LastSyncNever
                    : Workspace.Settings.LastSync.ToString(CultureInfo.InstalledUICulture);
            }
            set
            {
                Workspace.Settings.LastSync = Convert.ToDateTime(value);
                RaisePropertyChanged(nameof(LastSync));
            }
        }

        /// <summary>
        ///     Gets the update playlist Url command.
        /// </summary>
        public RelayCommand UpdatePlaylistUrlCommand => new RelayCommand(async () => await UpdatePlaylistUrl());

        public RelayCommand OpenLocationCommand => new RelayCommand(() =>
        {
            try
            {
                Process.Start(Workspace.Path);
            }
            catch
            {
                /* ignore */
            }
        });

        public RelayCommand SyncCommand => new RelayCommand(() =>
        {
            if(FetchingPlaylist)
                return;

            if (DownloadingAllSongs)
                CancelDownload();
            else
                Sync();
        });

        /// <summary>
        ///     Gets the download all songs command for the "Download all" button.
        /// </summary>
        /// <value>
        ///     The download all songs command.
        /// </value>
        public RelayCommand DownloadAllSongsCommand => new RelayCommand(() =>
        {
            if (DownloadingAllSongs)
                CancelDownload();
            else
                DownloadAllSongs();
        });

        public RelayCommand SelectWorkspaceCommand
            => new RelayCommand(() => { Messenger.Default.Send(new SelectWorkspaceMessage(this)); });

        public RelayCommand RemoveWorkspaceCommand
            => new RelayCommand(() => { Messenger.Default.Send(new RemoveWorkspaceMessage(this)); });

        #endregion

        #region Methods        

        /// <summary>
        ///     Inits the workspace loading.
        ///     Called when the workspace was selected for the first time.
        /// </summary>
        public async Task Init()
        {
            await Task.Run(() =>
            {
#if DEBUG
                var watch = Stopwatch.StartNew();
#endif
                UpdateTracks();
                CleanupWorkspaceFolder();
#if DEBUG
                Logger.Trace("Initialized workspace view model for workspace {0}. Duration: {1} ms", Workspace,
                    watch.ElapsedMilliseconds);
#else
                Logger.Trace("Initialized workspace view model for workspace {0}.", Workspace);
#endif
                _watcher.EnableRaisingEvents = true;
            });
        }

        public async void Load()
        {
            await Task.Run(() =>
            {
                if (Workspace.Settings.AutoSync && !_initialized)
                {
                    Thread.Sleep(1000);
                    Sync();
                }

                _initialized = true;
            });
        }

        private void SetupFilter()
        {
            foreach (FilterMode mode in Enum.GetValues(typeof(FilterMode)))
                switch (mode)
                {
                    case FilterMode.None:
                        FilterModes.Add(mode, Resources.MainWindow_CurrentWorkspace_FilterMode_None);
                        break;

                    case FilterMode.Downloaded:
                        FilterModes.Add(mode, Resources.MainWindow_CurrentWorkspace_FilterMode_Downloaded);
                        break;

                    case FilterMode.NotDownloaded:
                        FilterModes.Add(mode, Resources.MainWindow_CurrentWorkspace_FilterMode_NotDownloaded);
                        break;

                    case FilterMode.Downloading:
                        FilterModes.Add(mode, Resources.MainWindow_CurrentWorkspace_FilterMode_Downloading);
                        break;

                    case FilterMode.Queued:
                        FilterModes.Add(mode, Resources.MainWindow_CurrentWorkspace_FilterMode_Queued);
                        break;
                }

            SelectedFilterMode = FilterMode.None;
        }

        private void ApplicationSettingsOnPropertyChanged(object sender,
            PropertyChangedEventArgs propertyChangedEventArgs)
        {
            if (propertyChangedEventArgs.PropertyName == nameof(Settings.Default.ParallelDownloads))
                DownloadManager.ParallelDownloads = Settings.Default.ParallelDownloads;
        }

        private void CleanupWorkspaceFolder()
        {
            if (!Workspace.Settings.DeleteNotSyncedItems || (Tracks.Count == 0))
                return;

            try
            {
                foreach (var file in Directory.GetFiles(Workspace.Path))
                {
                    var name = Path.GetFileNameWithoutExtension(file);

                    if ((!file.EndsWith(".m4a") && !file.EndsWith(".mp3")) ||
                        Tracks.All(item => item.Item.Title != name))
                        try
                        {
                            File.Delete(file);
                            Logger.Trace("Workspace cleanup for workspace {0}: Deleted file {1}", Workspace.Path, file);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn(ex, "Workspace cleanup for workspace {0}: Error deleting file {1}",
                                Workspace.Path, file);
                        }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Workspace cleanup for workspace {0}: Error obtaining files", Workspace.Path);
            }
        }

        private void SettingsOnPropertyChanged(object sender, PropertyChangedEventArgs propertyChangedEventArgs)
        {
            if (propertyChangedEventArgs.PropertyName == nameof(WorkspaceSettings.DownloadFormat))
            {
                foreach (var track in Tracks)
                    track.CheckForTrack();

                SearchTextChanged();

                Messenger.Default.Send(
                    new ShowMessageDialogMessage(
                        Resources.MainWindow_Settings_DownloadFormatChanged_Title,
                        string.Format(Resources.MainWindow_Settings_DownloadFormatChanged_Content,
                            Workspace.Settings.DownloadFormat))
                );
            }
        }

        /// <summary>
        ///     Called when a file was renamed in the workspace.
        ///     Searches for an existing song in the list and renames its title.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="renamedEventArgs">The <see cref="RenamedEventArgs" /> instance containing the event data.</param>
        private void WatcherOnRenamed(object sender, RenamedEventArgs renamedEventArgs)
        {
            var newTitle = Path.GetFileNameWithoutExtension(renamedEventArgs.Name);

            foreach (var track in Tracks)
                if (track.Title == newTitle)
                {
                    var previousState = track.DownloadState;

                    if (Path.GetExtension(renamedEventArgs.Name)?.Replace(".", "") !=
                        Workspace.Settings.DownloadFormat.ToString().ToLower())
                    {
                        track.DownloadState = DownloadState.NeedsConvertion;

                        if (previousState == DownloadState.Downloaded)
                            DownloadedTracks--;

                        if (SelectedFilterMode == FilterMode.NotDownloaded)
                        {
                            DisplayedTracksSource.Add(track);
                            DisplayedTracksSource.Sort();
                            OnPageNumberChanged();
                        }
                    }
                    else
                    {
                        track.DownloadState = DownloadState.Downloaded;

                        if (previousState == DownloadState.NotDownloaded)
                            DownloadedTracks++;

                        if (SelectedFilterMode == FilterMode.Downloaded)
                        {
                            DisplayedTracksSource.Add(track);
                            DisplayedTracksSource.Sort();
                            OnPageNumberChanged();
                        }
                    }

                    return;
                }
        }

        /// <summary>
        ///     Called when a file was deleted in the workspace.
        ///     Searches for an existing song in the list and sets its download status to false.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="fileSystemEventArgs">The <see cref="FileSystemEventArgs" /> instance containing the event data.</param>
        private void WatcherOnDeleted(object sender, FileSystemEventArgs fileSystemEventArgs)
        {
            if (Path.GetExtension(fileSystemEventArgs.Name)?.Replace(".", "") !=
                Workspace.Settings.DownloadFormat.ToString().ToLower())
                return;

            var title = Path.GetFileNameWithoutExtension(fileSystemEventArgs.Name);

            foreach (var track in Tracks)
            {
                if (track.Title != title) continue;

                DownloadedTracks--;
                track.DownloadState = DownloadState.NotDownloaded;

                if (SelectedFilterMode == FilterMode.NotDownloaded)
                {
                    DisplayedTracksSource.Add(track);
                    DisplayedTracksSource.Sort();
                    OnPageNumberChanged();
                }

                return;
            }
        }

        /// <summary>
        ///     Called when a file was created in the workspace folder.
        ///     Searches for an existing song and sets its download status to true.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="fileSystemEventArgs">The <see cref="FileSystemEventArgs" /> instance containing the event data.</param>
        private void WatcherOnCreated(object sender, FileSystemEventArgs fileSystemEventArgs)
        {
            var title = Path.GetFileNameWithoutExtension(fileSystemEventArgs.Name);
            var extension = Path.GetExtension(fileSystemEventArgs.Name)?.Replace(".", "");

            foreach (var track in Tracks)
            {
                if (track.Title != title) continue;
                if (track.Downloading) return;

                if (extension != Workspace.Settings.DownloadFormat.ToString().ToLower())
                {
                    track.DownloadState = DownloadState.NeedsConvertion;
                }
                else
                {
                    track.DownloadState = DownloadState.Downloaded;
                    DownloadedTracks++;
                }

                if (SelectedFilterMode == FilterMode.NotDownloaded)
                {
                    DisplayedTracksSource.Add(track);
                    DisplayedTracksSource.Sort();
                    OnPageNumberChanged();
                }

                return;
            }
        }

        private void WatcherOnError(object sender, ErrorEventArgs errorEventArgs)
        {
            Messenger.Default.Send(
                new ShowMessageDialogMessage(Resources.MainWindow_CurrentWorkspace_ProjectFolderError_Title,
                    string.Format(Resources.MainWindow_CurrentWorkspace_ProjectFolderError_Content, Workspace.Name)));
            Messenger.Default.Send(new WorkspaceErrorMessage(Workspace));
        }

        /// <summary>
        ///     Called when the tracks collection changed.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="notifyCollectionChangedEventArgs">
        ///     The <see cref="NotifyCollectionChangedEventArgs" /> instance containing
        ///     the event data.
        /// </param>
        private void TracksOnCollectionChanged(object sender,
            NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs)
        {
            RaisePropertyChanged(nameof(TrackCount));
        }

        private async void Sync()
        {
            if (string.IsNullOrEmpty(PlaylistUrl))
                return;

            DownloadingAllSongs = true;
            await UpdatePlaylistUrl();

            if (DownloadingAllSongs)
                DownloadAllSongs();
        }

        /// <summary>
        ///     Updates the playlist Url.
        /// </summary>
        private async Task UpdatePlaylistUrl()
        {
            if (_playlistUrlChanged)
            {
                Workspace.SetPlaylistUrl(PlaylistUrl);
                Tracks.Clear();
            }

            if(string.IsNullOrEmpty(Workspace.PlaylistId))
                return;

            await Task.Run(() =>
            {
                foreach (var track in Tracks)
                    track.CheckForTrack();
            });

            PlaylistFetchProgress = 0;
            FetchingPlaylist = true;

            var retreiver = new PlaylistItemsRetriever(Settings.Default.PlaylistReceiveMaximum);
            retreiver.PlaylistItemsRetrieverProgressChanged +=
                delegate(object sender, PlaylistItemRetreiverProgressChangedEventArgs args)
                {
                    PlaylistFetchProgress = args.Progress;
                };

            retreiver.PlaylistItemsRetrieverCompleted +=
                (sender, args) =>
                {
                    if (args.Cancelled)
                    {
                        Messenger.Default.Send(
                            new ShowMessageDialogMessage(Resources.MainWindow_CurrentWorkspace_PlaylistLoadError_Title,
                                Resources.MainWindow_CurrentWorkspace_PlaylistLoadError_Content));
                        FetchingPlaylist = false;
                        return;
                    }

                    Workspace.Settings.Items =
                        new HashSet<PlaylistItem>(List.Sync(Workspace.Settings.Items.ToList(), args.Result));

                    UpdateTracks();

                    FetchingPlaylist = false;

                    if (_playlistUrlChanged)
                    {
                        _playlistUrlChanged = false;
                        CleanupWorkspaceFolder();
                    }
                };

            await Task.Run(() => { retreiver.GetPlaylistItems(Workspace.PlaylistId); });
        }

        /// <summary>
        ///     Updates the tracklist.
        ///     Compares old and new songs. Removes all songs which are not in the fetched playlist and adds all songs which are
        ///     new in the playlist.
        /// </summary>
        private void UpdateTracks()
        {
            DisplayedTracksSource.Clear();
            // Get playlist items from the viewmodel collection
            var tracks = Tracks.Select(t => t.Item).ToList();

            // Determinite items that should be added and removed
            var playlistItems = tracks.ToArray();
            var addItems = Workspace.Settings.Items.Except(playlistItems).ToList();

            var removeItems = playlistItems.Except(Workspace.Settings.Items).ToList();
            
            // if (Tracks.Count == 0)
            addItems.Reverse();
            
            // Remove the specified items
            Tracks.RemoveAll(item => removeItems.Contains(item.Item));


            // Add the new items
            foreach (var item in addItems)
                Tracks.Insert(0, new PlaylistItemViewModel(item, this));

            var i = 1;
            DownloadedTracks = 0;

            foreach (var track in Tracks)
            {
                if (track.DownloadState == DownloadState.Downloaded)
                    DownloadedTracks++;

                track.Index = i++;
            }
            Workspace.SaveWorkspaceConfig();

            DisplayedTracksSource.AddRange(Tracks);
            OnPageNumberChanged();
            PageSelectorViewModel.UpdatePageview();
        }

        /// <summary>
        ///     Called when the page number was changed.
        /// </summary>
        internal void OnPageNumberChanged()
        {
            if (Tracks.Count == 0) return;

            DisplayedTracks.Clear();
            var pageItems = new List<PlaylistItemViewModel>();

            var startingIndex = (PageSelectorViewModel.PageNumber - 1)*PageSelectorViewModel.ItemsPerPage;
            var endingIndex = startingIndex +
                              Math.Min(DisplayedTracksSource.Count - startingIndex, PageSelectorViewModel.ItemsPerPage);

            for (var i = startingIndex; i < endingIndex; i++)
            {
                var current = DisplayedTracksSource[i];

                if (!DisplayedTracks.Contains(current))
                {
                    if (current.Thumbnail == null)
                        current.UpdateThumbnail();

                    pageItems.Add(current);
                }
            }

            DisplayedTracks.RemoveAll(x => !pageItems.Contains(x));
            DisplayedTracks.AddRange(pageItems);
            DisplayedTracks.Sort();
        }

        /// <summary>
        ///     Downloads all songs.
        /// </summary>
        private void DownloadAllSongs()
        {
            DownloadingAllSongs = true;
            DownloadItemsRemaining = 0;
            _downloadErrors = 0;

            foreach (var track in Tracks)
            {
                track.CheckForTrack();

                if ((track.DownloadState != DownloadState.Downloaded) && track.AutoDownload)
                {
                    DownloadItemsRemaining++;

                    var handler = track.DownloadSong(false);
                    if (handler == null) continue;

                    DownloadManager.AddToQueue(handler);
                }
            }

            if (DownloadItemsRemaining <= 0)
            {
                DownloadingAllSongs = false;
                Workspace.Settings.LastSync = DateTime.Now;
                RaisePropertyChanged(nameof(LastSync));
            }
        }

        internal void HandlerOnDownloadItemDownloadCompleted(object sender, DownloadCompletedEventArgs args)
        {
            if (args.Error != null)
                _downloadErrors++;

            var downloadManagerItem = (DownloadManagerItem) sender;
            downloadManagerItem.DownloadItemDownloadCompleted -= HandlerOnDownloadItemDownloadCompleted;

            if (DownloadingAllSongs && (--DownloadItemsRemaining <= 0))
            {
                Workspace.Settings.LastSync = DateTime.Now;
                RaisePropertyChanged(nameof(LastSync));

                DownloadingAllSongs = false;
                if (_downloadErrors > 0)
                    Messenger.Default.Send(
                        new ShowMessageDialogMessage(Resources.MainWindow_CurrentWorkspace_SyncError_Title,
                            string.Format(Resources.MainWindow_CurrentWorkspace_SyncError_Content, _downloadErrors)));

                _downloadErrors = 0;
            }

            if (args.Cancelled)
                return;

            DownloadedTracks++;

            SearchTextChanged();
        }

        internal void HandlerOnDownloadItemDownloadStarted(object sender, EventArgs args)
        {
            SearchTextChanged();
            ApplyFilter();
        }

        internal void HandlerOnDownloadItemConvertionStarted(object sender, EventArgs args)
        {
            SearchTextChanged();
            ApplyFilter();
        }

        private void SearchTextChanged()
        {
            if (_searchTextChangeInProgress)
                return;

            _searchTextChangeInProgress = true;
            var refreshPageView = false;
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                SearchTextboxWatermark = Resources.MainWindow_CurrentWorkspace_SearchWatermarkDefault;
                if (DisplayedTracksSource.Count != Tracks.Count)
                {
                    DisplayedTracksSource.Clear();
                    DisplayedTracksSource.AddRange(Tracks);
                    refreshPageView = true;
                }
            }
            else
            {
                var results = Tracks.Where(x => x.Item.Title.ToLower().Contains(SearchText.ToLower())).ToList();
                DisplayedTracksSource.Clear();
                DisplayedTracksSource.AddRange(results);

                SearchTextboxWatermark = string.Format(Resources.MainWindow_CurrentWorkspace_SearchWatermarkResults,
                    results.Count);

                refreshPageView = true;
            }

            ApplyFilter();

            if (refreshPageView)
                OnPageNumberChanged();

            PageSelectorViewModel.UpdatePageview();
            _searchTextChangeInProgress = false;
        }

        private void ApplyFilter()
        {
            switch (SelectedFilterMode)
            {
                case FilterMode.Downloaded:
                {
                    DisplayedTracksSource.RemoveAll(x => x.DownloadState != DownloadState.Downloaded);
                }
                    break;

                case FilterMode.NotDownloaded:
                {
                    DisplayedTracksSource.RemoveAll(x => x.DownloadState == DownloadState.Downloaded);
                }
                    break;

                case FilterMode.Downloading:
                {
                    DisplayedTracksSource.RemoveAll(
                        x =>
                            (x.DownloadState != DownloadState.Downloading) &&
                            (x.DownloadState != DownloadState.Converting));
                }
                    break;

                case FilterMode.Queued:
                {
                    DisplayedTracksSource.RemoveAll(x => x.DownloadState != DownloadState.Queued);
                }
                    break;
            }
        }

        /// <summary>
        ///     Cancels the download of all songs.
        /// </summary>
        private void CancelDownload()
        {
            DownloadManager.Abort();

            foreach (var track in Tracks)
                track.StopDownload();

            DownloadingAllSongs = false;
        }

        #endregion
    }
}