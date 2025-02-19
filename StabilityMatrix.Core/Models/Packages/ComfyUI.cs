﻿using System.Diagnostics;
using System.Drawing;
using System.Text.RegularExpressions;
using NLog;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Helper.Cache;
using StabilityMatrix.Core.Models.FileInterfaces;
using StabilityMatrix.Core.Models.Progress;
using StabilityMatrix.Core.Processes;
using StabilityMatrix.Core.Python;
using StabilityMatrix.Core.Services;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization.TypeInspectors;

namespace StabilityMatrix.Core.Models.Packages;

public class ComfyUI : BaseGitPackage
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    public override string Name => "ComfyUI";
    public override string DisplayName { get; set; } = "ComfyUI";
    public override string Author => "comfyanonymous";
    public override string LicenseType => "GPL-3.0";
    public override string LicenseUrl =>
        "https://github.com/comfyanonymous/ComfyUI/blob/master/LICENSE";
    public override string Blurb => "A powerful and modular stable diffusion GUI and backend";
    public override string LaunchCommand => "main.py";

    public override Uri PreviewImageUri =>
        new("https://github.com/comfyanonymous/ComfyUI/raw/master/comfyui_screenshot.png");
    public override bool ShouldIgnoreReleases => true;
    public override SharedFolderMethod RecommendedSharedFolderMethod =>
        SharedFolderMethod.Configuration;

    public ComfyUI(
        IGithubApiCache githubApi,
        ISettingsManager settingsManager,
        IDownloadService downloadService,
        IPrerequisiteHelper prerequisiteHelper
    )
        : base(githubApi, settingsManager, downloadService, prerequisiteHelper) { }

    // https://github.com/comfyanonymous/ComfyUI/blob/master/folder_paths.py#L11
    public override Dictionary<SharedFolderType, IReadOnlyList<string>> SharedFolders =>
        new()
        {
            [SharedFolderType.StableDiffusion] = new[] { "models/checkpoints" },
            [SharedFolderType.Diffusers] = new[] { "models/diffusers" },
            [SharedFolderType.Lora] = new[] { "models/loras" },
            [SharedFolderType.CLIP] = new[] { "models/clip" },
            [SharedFolderType.TextualInversion] = new[] { "models/embeddings" },
            [SharedFolderType.VAE] = new[] { "models/vae" },
            [SharedFolderType.ApproxVAE] = new[] { "models/vae_approx" },
            [SharedFolderType.ControlNet] = new[] { "models/controlnet" },
            [SharedFolderType.GLIGEN] = new[] { "models/gligen" },
            [SharedFolderType.ESRGAN] = new[] { "models/upscale_models" },
            [SharedFolderType.Hypernetwork] = new[] { "models/hypernetworks" },
        };

    public override List<LaunchOptionDefinition> LaunchOptions =>
        new List<LaunchOptionDefinition>
        {
            new()
            {
                Name = "VRAM",
                Type = LaunchOptionType.Bool,
                InitialValue = HardwareHelper
                    .IterGpuInfo()
                    .Select(gpu => gpu.MemoryLevel)
                    .Max() switch
                {
                    Level.Low => "--lowvram",
                    Level.Medium => "--normalvram",
                    _ => null
                },
                Options = { "--highvram", "--normalvram", "--lowvram", "--novram" }
            },
            new()
            {
                Name = "Enable DirectML",
                Type = LaunchOptionType.Bool,
                InitialValue = HardwareHelper.PreferDirectML(),
                Options = { "--directml" }
            },
            new()
            {
                Name = "Use CPU only",
                Type = LaunchOptionType.Bool,
                InitialValue = !HardwareHelper.HasNvidiaGpu() && !HardwareHelper.HasAmdGpu(),
                Options = { "--cpu" }
            },
            new()
            {
                Name = "Disable Xformers",
                Type = LaunchOptionType.Bool,
                InitialValue = !HardwareHelper.HasNvidiaGpu(),
                Options = { "--disable-xformers" }
            },
            new()
            {
                Name = "Auto-Launch",
                Type = LaunchOptionType.Bool,
                Options = { "--auto-launch" }
            },
            LaunchOptionDefinition.Extras
        };

    public override Task<string> GetLatestVersion() => Task.FromResult("master");

    public override IEnumerable<TorchVersion> AvailableTorchVersions =>
        new[] { TorchVersion.Cpu, TorchVersion.Cuda, TorchVersion.DirectMl, TorchVersion.Rocm };

    public override async Task InstallPackage(
        string installLocation,
        TorchVersion torchVersion,
        IProgress<ProgressReport>? progress = null,
        Action<ProcessOutput>? onConsoleOutput = null
    )
    {
        await base.InstallPackage(installLocation, torchVersion, progress).ConfigureAwait(false);

        progress?.Report(new ProgressReport(-1, "Setting up venv", isIndeterminate: true));
        // Setup venv
        await using var venvRunner = new PyVenvRunner(Path.Combine(installLocation, "venv"));
        venvRunner.WorkingDirectory = installLocation;
        await venvRunner.Setup(true).ConfigureAwait(false);

        // Install torch / xformers based on gpu info
        switch (torchVersion)
        {
            case TorchVersion.Cpu:
                await InstallCpuTorch(venvRunner, progress, onConsoleOutput).ConfigureAwait(false);
                break;
            case TorchVersion.Cuda:
                await InstallCudaTorch(venvRunner, progress, onConsoleOutput).ConfigureAwait(false);
                break;
            case TorchVersion.Rocm:
                await InstallRocmTorch(venvRunner, progress, onConsoleOutput).ConfigureAwait(false);
                break;
            case TorchVersion.DirectMl:
                await InstallDirectMlTorch(venvRunner, progress, onConsoleOutput)
                    .ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(torchVersion), torchVersion, null);
        }

        // Install requirements file
        progress?.Report(
            new ProgressReport(-1, "Installing Package Requirements", isIndeterminate: true)
        );
        Logger.Info("Installing requirements.txt");
        await venvRunner.PipInstall($"-r requirements.txt", onConsoleOutput).ConfigureAwait(false);

        progress?.Report(
            new ProgressReport(1, "Installing Package Requirements", isIndeterminate: false)
        );
    }

    private async Task AutoDetectAndInstallTorch(
        PyVenvRunner venvRunner,
        IProgress<ProgressReport>? progress = null
    )
    {
        var gpus = HardwareHelper.IterGpuInfo().ToList();
        if (gpus.Any(g => g.IsNvidia))
        {
            await InstallCudaTorch(venvRunner, progress).ConfigureAwait(false);
        }
        else if (HardwareHelper.PreferRocm())
        {
            await InstallRocmTorch(venvRunner, progress).ConfigureAwait(false);
        }
        else if (HardwareHelper.PreferDirectML())
        {
            await InstallDirectMlTorch(venvRunner, progress).ConfigureAwait(false);
        }
        else
        {
            await InstallCpuTorch(venvRunner, progress).ConfigureAwait(false);
        }
    }

    public override async Task RunPackage(
        string installedPackagePath,
        string command,
        string arguments,
        Action<ProcessOutput>? onConsoleOutput
    )
    {
        await SetupVenv(installedPackagePath).ConfigureAwait(false);

        void HandleConsoleOutput(ProcessOutput s)
        {
            onConsoleOutput?.Invoke(s);

            if (s.Text.Contains("To see the GUI go to", StringComparison.OrdinalIgnoreCase))
            {
                var regex = new Regex(@"(https?:\/\/)([^:\s]+):(\d+)");
                var match = regex.Match(s.Text);
                if (match.Success)
                {
                    WebUrl = match.Value;
                }
                OnStartupComplete(WebUrl);
            }
        }

        void HandleExit(int i)
        {
            Debug.WriteLine($"Venv process exited with code {i}");
            OnExit(i);
        }

        var args = $"\"{Path.Combine(installedPackagePath, command)}\" {arguments}";

        VenvRunner?.RunDetached(args.TrimEnd(), HandleConsoleOutput, HandleExit);
    }

    public override Task SetupModelFolders(
        DirectoryPath installDirectory,
        SharedFolderMethod sharedFolderMethod
    )
    {
        switch (sharedFolderMethod)
        {
            case SharedFolderMethod.None:
                return Task.CompletedTask;
            case SharedFolderMethod.Symlink:
                return base.SetupModelFolders(installDirectory, sharedFolderMethod);
        }

        var extraPathsYamlPath = installDirectory + "extra_model_paths.yaml";
        var modelsDir = SettingsManager.ModelsDirectory;

        var exists = File.Exists(extraPathsYamlPath);
        if (!exists)
        {
            Logger.Info("Creating extra_model_paths.yaml");
            File.WriteAllText(extraPathsYamlPath, string.Empty);
        }
        var yaml = File.ReadAllText(extraPathsYamlPath);
        using var sr = new StringReader(yaml);
        var yamlStream = new YamlStream();
        yamlStream.Load(sr);

        if (!yamlStream.Documents.Any())
        {
            yamlStream.Documents.Add(new YamlDocument(new YamlMappingNode()));
        }

        var root = yamlStream.Documents[0].RootNode;
        if (root is not YamlMappingNode mappingNode)
        {
            throw new Exception("Invalid extra_model_paths.yaml");
        }
        // check if we have a child called "stability_matrix"
        var stabilityMatrixNode = mappingNode.Children.FirstOrDefault(
            c => c.Key.ToString() == "stability_matrix"
        );

        if (stabilityMatrixNode.Key != null)
        {
            if (stabilityMatrixNode.Value is not YamlMappingNode nodeValue)
                return Task.CompletedTask;

            nodeValue.Children["checkpoints"] = Path.Combine(modelsDir, "StableDiffusion");
            nodeValue.Children["vae"] = Path.Combine(modelsDir, "VAE");
            nodeValue.Children["loras"] =
                $"{Path.Combine(modelsDir, "Lora")}\n" + $"{Path.Combine(modelsDir, "LyCORIS")}";
            nodeValue.Children["upscale_models"] =
                $"{Path.Combine(modelsDir, "ESRGAN")}\n"
                + $"{Path.Combine(modelsDir, "RealESRGAN")}\n"
                + $"{Path.Combine(modelsDir, "SwinIR")}";
            nodeValue.Children["embeddings"] = Path.Combine(modelsDir, "TextualInversion");
            nodeValue.Children["hypernetworks"] = Path.Combine(modelsDir, "Hypernetwork");
            nodeValue.Children["controlnet"] = Path.Combine(modelsDir, "ControlNet");
            nodeValue.Children["clip"] = Path.Combine(modelsDir, "CLIP");
            nodeValue.Children["diffusers"] = Path.Combine(modelsDir, "Diffusers");
            nodeValue.Children["gligen"] = Path.Combine(modelsDir, "GLIGEN");
            nodeValue.Children["vae_approx"] = Path.Combine(modelsDir, "ApproxVAE");
        }
        else
        {
            stabilityMatrixNode = new KeyValuePair<YamlNode, YamlNode>(
                new YamlScalarNode("stability_matrix"),
                new YamlMappingNode
                {
                    { "checkpoints", Path.Combine(modelsDir, "StableDiffusion") },
                    { "vae", Path.Combine(modelsDir, "VAE") },
                    {
                        "loras",
                        $"{Path.Combine(modelsDir, "Lora")}\n{Path.Combine(modelsDir, "LyCORIS")}"
                    },
                    {
                        "upscale_models",
                        $"{Path.Combine(modelsDir, "ESRGAN")}\n{Path.Combine(modelsDir, "RealESRGAN")}\n{Path.Combine(modelsDir, "SwinIR")}"
                    },
                    { "embeddings", Path.Combine(modelsDir, "TextualInversion") },
                    { "hypernetworks", Path.Combine(modelsDir, "Hypernetwork") },
                    { "controlnet", Path.Combine(modelsDir, "ControlNet") },
                    { "clip", Path.Combine(modelsDir, "CLIP") },
                    { "diffusers", Path.Combine(modelsDir, "Diffusers") },
                    { "gligen", Path.Combine(modelsDir, "GLIGEN") },
                    { "vae_approx", Path.Combine(modelsDir, "ApproxVAE") }
                }
            );
        }

        var newRootNode = new YamlMappingNode();
        foreach (
            var child in mappingNode.Children.Where(c => c.Key.ToString() != "stability_matrix")
        )
        {
            newRootNode.Children.Add(child);
        }

        newRootNode.Children.Add(stabilityMatrixNode);

        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var yamlData = serializer.Serialize(newRootNode);
        File.WriteAllText(extraPathsYamlPath, yamlData);

        return Task.CompletedTask;
    }

    public override Task UpdateModelFolders(
        DirectoryPath installDirectory,
        SharedFolderMethod sharedFolderMethod
    ) => SetupModelFolders(installDirectory, sharedFolderMethod);

    public override Task RemoveModelFolderLinks(
        DirectoryPath installDirectory,
        SharedFolderMethod sharedFolderMethod
    )
    {
        return sharedFolderMethod switch
        {
            SharedFolderMethod.Configuration => Task.CompletedTask,
            SharedFolderMethod.None => Task.CompletedTask,
            SharedFolderMethod.Symlink
                => base.RemoveModelFolderLinks(installDirectory, sharedFolderMethod),
            _ => Task.CompletedTask
        };
    }

    private async Task InstallRocmTorch(
        PyVenvRunner venvRunner,
        IProgress<ProgressReport>? progress = null,
        Action<ProcessOutput>? onConsoleOutput = null
    )
    {
        progress?.Report(
            new ProgressReport(-1f, "Installing PyTorch for ROCm", isIndeterminate: true)
        );

        await venvRunner.PipInstall("--upgrade pip wheel", onConsoleOutput).ConfigureAwait(false);

        await venvRunner
            .PipInstall(PyVenvRunner.TorchPipInstallArgsRocm542, onConsoleOutput)
            .ConfigureAwait(false);
    }

    public class ComfyModelPathsYaml
    {
        public class SmData
        {
            public string Checkpoints { get; set; }
            public string Vae { get; set; }
            public string Loras { get; set; }
            public string UpscaleModels { get; set; }
            public string Embeddings { get; set; }
            public string Hypernetworks { get; set; }
            public string Controlnet { get; set; }
            public string Clip { get; set; }
            public string Diffusers { get; set; }
            public string Gligen { get; set; }
            public string VaeApprox { get; set; }
        }

        public SmData? StabilityMatrix { get; set; }
    }
}
