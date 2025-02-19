﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.Logging;
using StabilityMatrix.Avalonia.Animations;
using StabilityMatrix.Avalonia.Extensions;
using StabilityMatrix.Avalonia.Languages;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Dialogs;
using StabilityMatrix.Avalonia.Views.Dialogs;
using StabilityMatrix.Core.Extensions;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Helper.Factory;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Models.PackageModification;
using StabilityMatrix.Core.Models.Packages;
using StabilityMatrix.Core.Models.Progress;
using StabilityMatrix.Core.Processes;
using StabilityMatrix.Core.Services;

namespace StabilityMatrix.Avalonia.ViewModels.PackageManager;

public partial class PackageCardViewModel : ProgressViewModel
{
    private readonly ILogger<PackageCardViewModel> logger;
    private readonly IPackageFactory packageFactory;
    private readonly INotificationService notificationService;
    private readonly ISettingsManager settingsManager;
    private readonly INavigationService navigationService;
    private readonly ServiceManager<ViewModelBase> vmFactory;

    [ObservableProperty]
    private InstalledPackage? package;

    [ObservableProperty]
    private string? cardImageSource;

    [ObservableProperty]
    private bool isUpdateAvailable;

    [ObservableProperty]
    private string? installedVersion;

    [ObservableProperty]
    private bool isUnknownPackage;

    public PackageCardViewModel(
        ILogger<PackageCardViewModel> logger,
        IPackageFactory packageFactory,
        INotificationService notificationService,
        ISettingsManager settingsManager,
        INavigationService navigationService,
        ServiceManager<ViewModelBase> vmFactory
    )
    {
        this.logger = logger;
        this.packageFactory = packageFactory;
        this.notificationService = notificationService;
        this.settingsManager = settingsManager;
        this.navigationService = navigationService;
        this.vmFactory = vmFactory;
    }

    partial void OnPackageChanged(InstalledPackage? value)
    {
        if (string.IsNullOrWhiteSpace(value?.PackageName))
            return;

        if (value.PackageName == UnknownPackage.Key)
        {
            IsUnknownPackage = true;
            CardImageSource = "";
            InstalledVersion = "Unknown";
        }
        else
        {
            IsUnknownPackage = false;

            var basePackage = packageFactory[value.PackageName];
            CardImageSource = basePackage?.PreviewImageUri.ToString() ?? Assets.NoImage.ToString();
            InstalledVersion = value.Version?.DisplayVersion ?? "Unknown";
        }
    }

    public override async Task OnLoadedAsync()
    {
        IsUpdateAvailable = await HasUpdate();
    }

    public void Launch()
    {
        if (Package == null)
            return;

        settingsManager.Transaction(s => s.ActiveInstalledPackageId = Package.Id);

        navigationService.NavigateTo<LaunchPageViewModel>(new BetterDrillInNavigationTransition());
        EventManager.Instance.OnPackageLaunchRequested(Package.Id);
    }

    public async Task Uninstall()
    {
        if (Package?.LibraryPath == null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = Resources.Label_ConfirmDelete,
            Content = Resources.Text_PackageUninstall_Details,
            PrimaryButtonText = Resources.Action_OK,
            CloseButtonText = Resources.Action_Cancel,
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            Text = Resources.Progress_UninstallingPackage;
            IsIndeterminate = true;
            Value = -1;

            var packagePath = new DirectoryPath(settingsManager.LibraryDir, Package.LibraryPath);
            var deleteTask = packagePath.DeleteVerboseAsync(logger);

            var taskResult = await notificationService.TryAsync(
                deleteTask,
                Resources.Text_SomeFilesCouldNotBeDeleted
            );
            if (taskResult.IsSuccessful)
            {
                notificationService.Show(
                    new Notification(
                        Resources.Label_PackageUninstalled,
                        Package.DisplayName,
                        NotificationType.Success
                    )
                );

                if (!IsUnknownPackage)
                {
                    settingsManager.Transaction(settings =>
                    {
                        settings.RemoveInstalledPackageAndUpdateActive(Package);
                    });
                }

                EventManager.Instance.OnInstalledPackagesChanged();
            }
        }
    }

    public async Task Update()
    {
        if (Package is null || IsUnknownPackage)
            return;

        var basePackage = packageFactory[Package.PackageName!];
        if (basePackage == null)
        {
            logger.LogWarning(
                "Could not find package {SelectedPackagePackageName}",
                Package.PackageName
            );
            notificationService.Show(
                Resources.Label_InvalidPackageType,
                Package.PackageName.ToRepr(),
                NotificationType.Error
            );
            return;
        }

        var packageName = Package.DisplayName ?? Package.PackageName ?? "";

        Text = $"Updating {packageName}";
        IsIndeterminate = true;

        try
        {
            var runner = new PackageModificationRunner
            {
                ModificationCompleteMessage = $"{packageName} Update Complete"
            };
            var updatePackageStep = new UpdatePackageStep(settingsManager, Package, basePackage);
            var steps = new List<IPackageStep> { updatePackageStep };

            EventManager.Instance.OnPackageInstallProgressAdded(runner);
            await runner.ExecuteSteps(steps);

            IsUpdateAvailable = false;
            InstalledVersion = Package.Version?.DisplayVersion ?? "Unknown";
            notificationService.Show(
                Resources.Progress_UpdateComplete,
                string.Format(Resources.TextTemplate_PackageUpdatedToLatest, packageName),
                NotificationType.Success
            );
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error Updating Package ({PackageName})", basePackage.Name);
            notificationService.ShowPersistent(
                string.Format(Resources.TextTemplate_ErrorUpdatingPackage, packageName),
                e.Message,
                NotificationType.Error
            );
        }
        finally
        {
            IsIndeterminate = false;
            Value = 0;
            Text = "";
        }
    }

    public async Task Import()
    {
        if (!IsUnknownPackage || Design.IsDesignMode)
            return;

        var viewModel = vmFactory.Get<PackageImportViewModel>(vm =>
        {
            vm.PackagePath = new DirectoryPath(
                Package?.FullPath ?? throw new InvalidOperationException()
            );
        });

        var dialog = new TaskDialog
        {
            Content = new PackageImportDialog { DataContext = viewModel },
            ShowProgressBar = false,
            Buttons = new List<TaskDialogButton>
            {
                new(Resources.Action_Import, TaskDialogStandardResult.Yes) { IsDefault = true },
                new(Resources.Action_Cancel, TaskDialogStandardResult.Cancel)
            }
        };

        dialog.Closing += async (sender, e) =>
        {
            // We only want to use the deferral on the 'Yes' Button
            if ((TaskDialogStandardResult)e.Result == TaskDialogStandardResult.Yes)
            {
                var deferral = e.GetDeferral();

                sender.ShowProgressBar = true;
                sender.SetProgressBarState(0, TaskDialogProgressState.Indeterminate);

                await using (new MinimumDelay(200, 300))
                {
                    var result = await notificationService.TryAsync(
                        viewModel.AddPackageWithCurrentInputs()
                    );
                    if (result.IsSuccessful)
                    {
                        EventManager.Instance.OnInstalledPackagesChanged();
                    }
                }

                deferral.Complete();
            }
        };

        dialog.XamlRoot = App.VisualRoot;

        await dialog.ShowAsync(true);
    }

    public async Task OpenFolder()
    {
        if (string.IsNullOrWhiteSpace(Package?.FullPath))
            return;

        await ProcessRunner.OpenFolderBrowser(Package.FullPath);
    }

    private async Task<bool> HasUpdate()
    {
        if (Package == null || IsUnknownPackage || Design.IsDesignMode)
            return false;

        var basePackage = packageFactory[Package.PackageName!];
        if (basePackage == null)
            return false;

        var canCheckUpdate =
            Package.LastUpdateCheck == null
            || Package.LastUpdateCheck < DateTime.Now.AddMinutes(-15);

        if (!canCheckUpdate)
        {
            return Package.UpdateAvailable;
        }

        try
        {
            var hasUpdate = await basePackage.CheckForUpdates(Package);
            Package.UpdateAvailable = hasUpdate;
            Package.LastUpdateCheck = DateTimeOffset.Now;
            settingsManager.SetLastUpdateCheck(Package);
            return hasUpdate;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error checking {PackageName} for updates", Package.PackageName);
            return false;
        }
    }
}
