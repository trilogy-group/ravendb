param(
    $Repo = "ravendb/ravendb",
    $ArtifactsDir = "..\artifacts",
    $RavenDockerSettingsPath = "..\src\Raven.Server\Properties\Settings\settings.docker.windows.json",
    $DockerfileDir = "./ravendb-nanoserver")

$ErrorActionPreference = "Stop"

. ".\common.ps1"

function BuildWindowsDockerImage ($version) {
    $packageFileName = "RavenDB-$version-windows-x64.zip"
    $artifactsPackagePath = Join-Path -Path $ArtifactsDir -ChildPath $packageFileName

    if ([string]::IsNullOrEmpty($artifactsPackagePath))
    {
        throw "PackagePath cannot be empty."
    }

    if ($(Test-Path $artifactsPackagePath) -eq $False) {
        throw "Package file does not exist."
    }
    
    $dockerPackagePath = Join-Path -Path $DockerfileDir -ChildPath "RavenDB.zip"
    Copy-Item -Path $artifactsPackagePath -Destination $dockerPackagePath -Force
    Copy-Item -Path $RavenDockerSettingsPath -Destination $(Join-Path -Path $DockerfileDir -ChildPath "settings.json") -Force


    write-host "Build docker image: $version"
    write-host "Tags: $($repo):$version-windows-nanoserver $($repo):4.1-windows-nanoserver-latest"

    docker build $DockerfileDir `
        -t "$($repo):windows-nanoserver-latest" `
        -t "$($repo):$version-windows-nanoserver" `
        -t "$($repo):4.1-windows-nanoserver-latest"

    Remove-Item -Path $dockerPackagePath
}


BuildWindowsDockerImage $(GetVersionFromArtifactName)
