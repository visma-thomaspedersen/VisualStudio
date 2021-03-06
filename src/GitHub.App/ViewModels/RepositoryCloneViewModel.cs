﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Windows.Input;
using GitHub.App;
using GitHub.Exports;
using GitHub.Extensions;
using GitHub.Models;
using GitHub.Services;
using GitHub.Validation;
using NLog;
using NullGuard;
using ReactiveUI;
using Rothko;
using System.Collections.ObjectModel;
using GitHub.Collections;
using GitHub.UI;
using GitHub.Extensions.Reactive;

namespace GitHub.ViewModels
{
    [ExportViewModel(ViewType=UIViewType.Clone)]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    public class RepositoryCloneViewModel : BaseViewModel, IRepositoryCloneViewModel
    {
        static readonly Logger log = LogManager.GetCurrentClassLogger();

        readonly IRepositoryHost repositoryHost;
        readonly IRepositoryCloneService cloneService;
        readonly IOperatingSystem operatingSystem;
        readonly INotificationService notificationService;
        readonly IUsageTracker usageTracker;
        readonly ReactiveCommand<object> browseForDirectoryCommand = ReactiveCommand.Create();
        bool isLoading;
        bool noRepositoriesFound;
        readonly ObservableAsPropertyHelper<bool> canClone;
        string baseRepositoryPath;
        bool loadingFailed;

        [ImportingConstructor]
        RepositoryCloneViewModel(
            IConnectionRepositoryHostMap connectionRepositoryHostMap,
            IRepositoryCloneService repositoryCloneService,
            IOperatingSystem operatingSystem,
            INotificationService notificationService,
            IUsageTracker usageTracker)
            : this(connectionRepositoryHostMap.CurrentRepositoryHost, repositoryCloneService, operatingSystem, notificationService, usageTracker)
        { }


        public RepositoryCloneViewModel(
            IRepositoryHost repositoryHost,
            IRepositoryCloneService cloneService,
            IOperatingSystem operatingSystem,
            INotificationService notificationService,
            IUsageTracker usageTracker)
        {
            this.repositoryHost = repositoryHost;
            this.cloneService = cloneService;
            this.operatingSystem = operatingSystem;
            this.notificationService = notificationService;
            this.usageTracker = usageTracker;

            Title = string.Format(CultureInfo.CurrentCulture, Resources.CloneTitle, repositoryHost.Title);

            Repositories = new TrackingCollection<IRemoteRepositoryModel>();
            repositories.ProcessingDelay = TimeSpan.Zero;
            repositories.Comparer = OrderedComparer<IRemoteRepositoryModel>.OrderBy(x => x.Owner).ThenBy(x => x.Name).Compare;
            repositories.Filter = FilterRepository;
            repositories.NewerComparer = OrderedComparer<IRemoteRepositoryModel>.OrderByDescending(x => x.UpdatedAt).Compare;

            filterTextIsEnabled = this.WhenAny(x => x.IsLoading,
                loading => loading.Value || repositories.UnfilteredCount > 0 && !LoadingFailed)
                .ToProperty(this, x => x.FilterTextIsEnabled);

            this.WhenAny(
                x => x.repositories.UnfilteredCount,
                x => x.IsLoading,
                x => x.LoadingFailed,
                (unfilteredCount, loading, failed) =>
                {
                    if (loading.Value)
                        return false;

                    if (failed.Value)
                        return false;

                    return unfilteredCount.Value == 0;
                })
                .Subscribe(x =>
                {
                    NoRepositoriesFound = x;
                });

            this.WhenAny(x => x.FilterText, x => x.Value)
                .DistinctUntilChanged(StringComparer.OrdinalIgnoreCase)
                .Throttle(TimeSpan.FromMilliseconds(100), RxApp.MainThreadScheduler)
                .Subscribe(_ => repositories.Filter = FilterRepository);

            var baseRepositoryPath = this.WhenAny(
                x => x.BaseRepositoryPath,
                x => x.SelectedRepository,
                (x, y) => x.Value);

            BaseRepositoryPathValidator = ReactivePropertyValidator.ForObservable(baseRepositoryPath)
                .IfNullOrEmpty(Resources.RepositoryCreationClonePathEmpty)
                .IfTrue(x => x.Length > 200, Resources.RepositoryCreationClonePathTooLong)
                .IfContainsInvalidPathChars(Resources.RepositoryCreationClonePathInvalidCharacters)
                .IfPathNotRooted(Resources.RepositoryCreationClonePathInvalid)
                .IfTrue(IsAlreadyRepoAtPath, Resources.RepositoryNameValidatorAlreadyExists);

            var canCloneObservable = this.WhenAny(
                x => x.SelectedRepository,
                x => x.BaseRepositoryPathValidator.ValidationResult.IsValid,
                (x, y) => x.Value != null && y.Value);
            canClone = canCloneObservable.ToProperty(this, x => x.CanClone);
            CloneCommand = ReactiveCommand.CreateAsyncObservable(canCloneObservable, OnCloneRepository);

            browseForDirectoryCommand.Subscribe(_ => ShowBrowseForDirectoryDialog());
            this.WhenAny(x => x.BaseRepositoryPathValidator.ValidationResult, x => x.Value)
                .Subscribe();
            BaseRepositoryPath = cloneService.DefaultClonePath;
            NoRepositoriesFound = true;
        }

        public override void Initialize([AllowNull] ViewWithData data)
        {
            base.Initialize(data);

            IsLoading = true;
            repositoryHost.ModelService.GetRepositories(repositories);
            repositories.OriginalCompleted.Subscribe(
                _ => { }
                , ex =>
                {
                    LoadingFailed = true;
                    IsLoading = false;
                    log.Error("Error while loading repositories", ex);
                },
                () => IsLoading = false
            );
            repositories.Subscribe();
        }

        bool FilterRepository(IRemoteRepositoryModel repo, int position, IList<IRemoteRepositoryModel> list)
        {
            if (string.IsNullOrWhiteSpace(FilterText))
                return true;

            // Not matching on NameWithOwner here since that's already been filtered on by the selected account
            return repo.Name.IndexOf(FilterText ?? "", StringComparison.OrdinalIgnoreCase) != -1;
        }

        IObservable<Unit> OnCloneRepository(object state)
        {
            return Observable.Start(() =>
            {
                var repository = SelectedRepository;
                Debug.Assert(repository != null, "Should not be able to attempt to clone a repo when it's null");
                if (repository == null)
                {
                    notificationService.ShowError(Resources.RepositoryCloneFailedNoSelectedRepo);
                    return Observable.Return(Unit.Default);
                }

                // The following is a noop if the directory already exists.
                operatingSystem.Directory.CreateDirectory(BaseRepositoryPath);

                return cloneService.CloneRepository(repository.CloneUrl, repository.Name, BaseRepositoryPath)
                    .ContinueAfter(() =>
                    {
                        usageTracker.IncrementCloneCount().Forget();
                        return Observable.Return(Unit.Default);
                    });
            })
            .SelectMany(_ => _)
            .Catch<Unit, Exception>(e =>
            {
                var repository = SelectedRepository;
                Debug.Assert(repository != null, "Should not be able to attempt to clone a repo when it's null");
                notificationService.ShowError(e.GetUserFriendlyErrorMessage(ErrorType.ClonedFailed, repository.Name));
                return Observable.Return(Unit.Default);
            });
        }

        bool IsAlreadyRepoAtPath(string path)
        {
            Debug.Assert(path != null, "RepositoryCloneViewModel.IsAlreadyRepoAtPath cannot be passed null as a path parameter.");

            bool isAlreadyRepoAtPath = false;

            if (SelectedRepository != null)
            {
                string potentialPath = Path.Combine(path, SelectedRepository.Name);
                isAlreadyRepoAtPath = operatingSystem.Directory.Exists(potentialPath);
            }

            return isAlreadyRepoAtPath;
        }

        IObservable<Unit> ShowBrowseForDirectoryDialog()
        {
            return Observable.Start(() =>
            {
                // We store this in a local variable to prevent it changing underneath us while the
                // folder dialog is open.
                var localBaseRepositoryPath = BaseRepositoryPath;
                var browseResult = operatingSystem.Dialog.BrowseForDirectory(localBaseRepositoryPath, Resources.BrowseForDirectory);

                if (!browseResult.Success)
                    return;

                var directory = browseResult.DirectoryPath ?? localBaseRepositoryPath;

                try
                {
                    BaseRepositoryPath = directory;
                }
                catch (Exception e)
                {
                    // TODO: We really should limit this to exceptions we know how to handle.
                    log.Error(string.Format(CultureInfo.InvariantCulture,
                        "Failed to set base repository path.{0}localBaseRepositoryPath = \"{1}\"{0}BaseRepositoryPath = \"{2}\"{0}Chosen directory = \"{3}\"",
                        System.Environment.NewLine, localBaseRepositoryPath ?? "(null)", BaseRepositoryPath ?? "(null)", directory ?? "(null)"), e);
                }
            }, RxApp.MainThreadScheduler);
        }

        /// <summary>
        /// Path to clone repositories into
        /// </summary>
        public string BaseRepositoryPath
        {
            [return: AllowNull]
            get { return baseRepositoryPath; }
            set { this.RaiseAndSetIfChanged(ref baseRepositoryPath, value); }
        }

        /// <summary>
        /// Fires off the cloning process
        /// </summary>
        public IReactiveCommand<Unit> CloneCommand { get; private set; }

        TrackingCollection<IRemoteRepositoryModel> repositories;
        public ObservableCollection<IRemoteRepositoryModel> Repositories
        {
            [return: AllowNull]
            get { return repositories; }
            private set { this.RaiseAndSetIfChanged(ref repositories, (TrackingCollection<IRemoteRepositoryModel>)value); }
        }

        IRepositoryModel selectedRepository;
        /// <summary>
        /// Selected repository to clone
        /// </summary>
        [AllowNull]
        public IRepositoryModel SelectedRepository
        {
            [return: AllowNull]
            get { return selectedRepository; }
            set { this.RaiseAndSetIfChanged(ref selectedRepository, value); }
        }

        readonly ObservableAsPropertyHelper<bool> filterTextIsEnabled;
        /// <summary>
        /// True if there are repositories (otherwise no point in filtering)
        /// </summary>
        public bool FilterTextIsEnabled { get { return filterTextIsEnabled.Value; } }

        string filterText;
        /// <summary>
        /// User text to filter the repositories list
        /// </summary>
        [AllowNull]
        public string FilterText
        {
            [return: AllowNull]
            get { return filterText; }
            set { this.RaiseAndSetIfChanged(ref filterText, value); }
        }

        public bool IsLoading
        {
            get { return isLoading; }
            private set { this.RaiseAndSetIfChanged(ref isLoading, value); }
        }

        public bool LoadingFailed
        {
            get { return loadingFailed; }
            private set { this.RaiseAndSetIfChanged(ref loadingFailed, value); }
        }

        public bool NoRepositoriesFound
        {
            get { return noRepositoriesFound; }
            private set { this.RaiseAndSetIfChanged(ref noRepositoriesFound, value); }
        }

        public ICommand BrowseForDirectory
        {
            get { return browseForDirectoryCommand; }
        }

        public bool CanClone
        {
            get { return canClone.Value; }
        }

        public ReactivePropertyValidator<string> BaseRepositoryPathValidator
        {
            get;
            private set;
        }
    }
}
