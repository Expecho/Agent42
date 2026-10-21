namespace Agent42.Tests;

/// <summary>
/// The Azure Files share of copilot chats' workspaces (ADR 0107), as <c>infra/modules/container-app.bicep</c>
/// declares it. Nothing in this suite can mount a share, so this asserts the Bicep text, in the style of
/// <see cref="EasyAuthExclusionTests"/>. What it stops: the share reaching the SearXNG sidecar, a mount the app cannot
/// write to (an SMB mount is root-owned unless <c>mountOptions</c> says otherwise, and the image runs as
/// <c>$APP_UID</c>), and an app that never learns where the share is.
/// </summary>
public sealed class CopilotFilesMountTests
{
    // ADR 0107 AC11.
    [Fact]
    public void CopilotFilesMount_BackendOnly_OwnedByAppUid()
    {
        var bicep = Bicep("container-app.bicep");
        var backend = Between(bicep, "name: 'backend'", "name: 'searxng'");
        var sidecar = Between(bicep, "name: 'searxng'", "scale: {");

        Assert.Contains("{ volumeName: 'copilot-workspaces', mountPath: '/mnt/copilot' }", backend);
        Assert.Contains("{ name: 'Copilot__WorkspaceRoot', value: '/mnt/copilot' }", backend);
        Assert.DoesNotContain("volumeMounts", sidecar.Split("volumes: [")[0]);
        Assert.DoesNotContain("Copilot__", sidecar);

        var volumes = Between(bicep, "volumes: [", "scale: {");
        Assert.Contains("storageType: 'AzureFile'", volumes);
        Assert.Contains("mountOptions: copilotWorkspaceMountOptions", volumes);
        // 1654 is APP_UID in the aspnet base image (measured); the Dockerfile ends on USER $APP_UID.
        Assert.Contains("var copilotWorkspaceMountOptions = 'uid=1654,gid=1654,dir_mode=0700,file_mode=0600'", bicep);
        Assert.Contains("USER $APP_UID", File.ReadAllText(Path.Combine(RepoRoot(), "src", "Agent42.Backend", "Dockerfile")));
    }

    // ADR 0111 phase 3 (AC35): the signal-cli store is the backend's alone, owned by the app uid, and the app is told
    // where it is. The SearXNG sidecar must never see a Signal key.
    [Fact]
    public void SignalStoreMount_BackendOnly_OwnedByAppUid()
    {
        var bicep = Bicep("container-app.bicep");
        var backend = Between(bicep, "name: 'backend'", "name: 'searxng'");
        var sidecar = Between(bicep, "name: 'searxng'", "scale: {");

        Assert.Contains("{ volumeName: 'signal-data', mountPath: '/mnt/signal' }", backend);
        Assert.Contains("{ name: 'ChatGateway__Signal__DataDir', value: '/mnt/signal' }", backend);
        Assert.Contains("{ name: 'ChatGateway__Signal__Enabled', value: string(signalEnabled) }", backend);
        Assert.DoesNotContain("volumeMounts", sidecar.Split("volumes: [")[0]);
        Assert.DoesNotContain("Signal", sidecar.Split("volumes: [")[0]);

        var signalVolume = Between(bicep, "name: 'signal-data'", "}");
        Assert.Contains("storageName: signalEnvironmentStorageName", signalVolume);
        Assert.Contains("mountOptions: copilotWorkspaceMountOptions", signalVolume);
        Assert.Contains("name: signalShareName", Bicep("copilot-files.bicep"));
    }

    // The share's account is the one with shared-key access, so the key-free attachments account (ADR 0004) stays so.
    [Fact]
    public void CopilotFilesAccount_IsSeparateFromTheKeyFreeAccount()
    {
        Assert.Contains("allowSharedKeyAccess: true", Bicep("copilot-files.bicep"));
        Assert.Contains("allowSharedKeyAccess: false", Bicep("storage.bicep"));
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' not found");
        var to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, $"'{end}' not found after '{start}'");
        return text[from..to];
    }

    private static string Bicep(string module) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "infra", "modules", module)).ReplaceLineEndings("\n");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root (Agent42.slnx) from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
