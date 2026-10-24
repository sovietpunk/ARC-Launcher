namespace ARC.Launcher.Core;

public interface ILaunchService : IMinecraftLaunchService
{
    Task<LaunchCheck> CheckAsync(LaunchRequest request, CancellationToken cancellationToken = default);
    Task<LaunchPreview> PreviewAsync(LaunchRequest request, CancellationToken cancellationToken = default);
    Task<ManagedGameProcess> RunAsync(LaunchRequest request, Action<LaunchLog>? onLog = null, CancellationToken cancellationToken = default);
    ProcessStatusReport Status();
}

public sealed class LaunchBlockedException(LaunchCheck check) : InvalidOperationException("启动准备未通过；请查看检查结果。")
{
    public LaunchCheck Check { get; } = check;
}

public sealed class LaunchService(MinecraftRoot root, LocalInstanceService instances,
    JavaService javaService, IAuthenticationService authentication) : ILaunchService
{
    private readonly GameProcessService processes = new(root);

    private async Task<PreparedLaunch> PrepareAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        var metadata = new VersionResolver(root).Resolve(request.InstanceName).Metadata;
        var major = LaunchPlanner.RequiredJavaMajor(metadata, instances.GetInstance(request.InstanceName).MinecraftVersion);
        var java = await javaService.DiscoverAsync(request.JavaExecutable, cancellationToken);
        var architecture = JavaService.NormalizeArchitecture(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString());
        var selected = java.Installations.FirstOrDefault(j => j.MajorVersion == major && j.Architecture == architecture);
        if (request.JavaExecutable is not null) selected ??= java.Installations.FirstOrDefault();
        var login = request.Offline is null ? await authentication.GetStateAsync(cancellationToken)
            : new LoginState(false, request.Offline.Info.Notice);
        var prepared = new LaunchPlanner(root, instances).Build(request.InstanceName, selected, login, request.Offline);
        if (java.Issues.Count == 0) return prepared;
        var check = prepared.Preview.Check;
        // Failed unrelated discovery candidates do not block an otherwise valid selected Java.
        if (selected is not null && request.JavaExecutable is null) return prepared;
        check = check with { DependenciesReady = false, CanLaunch = false,
            Issues = [.. check.Issues, .. java.Issues.Select(i => new LaunchIssue("JavaDetection", i))] };
        return prepared with { Preview = prepared.Preview with { Check = check } };
    }

    public async Task<LaunchCheck> CheckAsync(LaunchRequest request, CancellationToken cancellationToken = default) =>
        (await PrepareAsync(request, cancellationToken)).Preview.Check;
    public async Task<LaunchPreview> PreviewAsync(LaunchRequest request, CancellationToken cancellationToken = default) =>
        (await PrepareAsync(request, cancellationToken)).Preview;
    public ProcessStatusReport Status() => processes.Status();

    public async Task<ManagedGameProcess> RunAsync(LaunchRequest request, Action<LaunchLog>? onLog = null, CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(request, cancellationToken);
        if (!prepared.Preview.Check.CanLaunch) throw new LaunchBlockedException(prepared.Preview.Check);
        LaunchIdentity identity;
        if (request.Offline is not null) identity = request.Offline;
        else
        {
            var account = await authentication.GetSessionAsync(cancellationToken);
            if (account?.IsUsable != true)
                throw new LaunchBlockedException(prepared.Preview.Check with { CanLaunch = false,
                    Issues = [.. prepared.Preview.Check.Issues, new("AuthenticationRequired", "认证会话缺失或已过期。") ] });
            identity = new MicrosoftLaunchIdentity(account);
        }
        IReadOnlyList<string> arguments;
        try { arguments = IdentityArgumentBinder.Bind(prepared.RawArguments, identity); }
        catch (InvalidDataException)
        {
            throw new LaunchBlockedException(prepared.Preview.Check with { CanLaunch = false,
                Issues = [new("AuthenticationRequired", "认证服务未提供版本参数所需的完整正版登录信息。") ] });
        }
        cancellationToken.ThrowIfCancellationRequested();
        var preview = prepared.Preview;
        root.ValidateContained(preview.WorkingDirectory);
        return processes.Start(preview.Check.Java!.Executable, arguments, preview.WorkingDirectory, preview.RunId,
            new SecretRedactor(identity.Secrets), onLog,
            () => NativeExtractor.Extract(root, prepared.NativeArchives, preview.NativesDirectory), identity.Info);
    }

    public async Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken = default)
    {
        try { var process = await RunAsync(request, cancellationToken: cancellationToken); return new(true, null, process.InitialStatus.ProcessId); }
        catch (LaunchBlockedException) { return new(false, "启动准备未通过；请调用 CheckAsync 获取结构化问题。"); }
    }
}
