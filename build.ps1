﻿

function Get-Version() {
    if(Test-Path env:manualVersion) {
        Write-Host "Manually provided version detected!"
        $version = New-Object -TypeName PSCustomObject -Property @{ 
            "FullSemVer" = $env:manualVersion;
            "SemVer" = $env:manualVersion;
            "AssemblyVersion" = $env:manualVersion+".0";
        }
    } else {
        if($env:TF_BUILD -eq "True") {
            $version = git-flow-version --branch $env:BUILD_SOURCEBRANCHNAME | ConvertFrom-Json
        }
        else {
            $version = git-flow-version | ConvertFrom-Json
        }
    }
    $version
}

function New-SharedAssemblyInfo($version) {
    $assemblyInfoContent = @"
// <auto-generated/>
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyVersionAttribute("$($version.AssemblyVersion)")]
[assembly: AssemblyFileVersionAttribute("$($version.AssemblyVersion)")]
[assembly: AssemblyInformationalVersionAttribute("$($version.FullSemVer)")]
"@

    if (-not (Test-Path "built")) {
        New-Item -ItemType Directory "built"
    }
    $assemblyInfoContent | Out-File -Encoding utf8 (Join-Path "built" "SharedAssemblyInfo.cs") -Force
}

function Test-CIBuild() {
    $env:TF_BUILD -eq "True" 
}

function Test-WindowsCIBuild() {
    (Test-CIBuild) -and ($env:imageName -eq "windows-latest")
}

function New-Documentation() {
    Write-Host "Generating Documentation"
    Copy-Item README.md doc/index.md
    docfx ./doc/docfx.json
}

function Test-PullRequest() {
    (Test-Path env:Build_Reason) -and ($env:Build_Reason -eq "PullRequest")
}

function Test-FeatureBranch() {
    (Test-Path env:BUILD_SOURCEBRANCH) -and ($env:BUILD_SOURCEBRANCH -like "*/feature/*")
}

function Test-MasterBranch() {
    $env:BUILD_SOURCEBRANCHNAME -eq "master"
}

function Set-Tag($version) {
    Write-Host "Tagging build"
	git remote set-url origin git@github.com:hightechict/DashDashVersion.git
    git tag $version.SemVer
    try
    {
        git push --verbose origin $version.SemVer
    }
    catch [Exception]
    {
        Write-host "Tagging failed"
        PrintError $_ 
    }             
}

function New-Package($version) {
    New-SharedAssemblyInfo $version
    dotnet pack /p:PackageVersion="$($version.FullSemVer)" /p:NoPackageAnalysis=true
} 

function Export-Package() {
    Write-Host "Publishing NuGet package"
    pushd built
    dotnet nuget push *.nupkg --api-key $env:NuGet_APIKEY --no-symbols true --source https://api.nuget.org/v3/index.json 
    popd
}

function Publish-Documentation($version) {
    Write-Host "Publishing documentation"
    git config --global core.autocrlf false
    $PathOfOrigin = Get-Location;
    cd $env:Build_ArtifactStagingDirectory
    try
    {
        Write-Host "Try git clone"
        git clone git@github.com:hightechict/DashDashVersion_site.git --branch develop
        Write-Host "Git Repo cloned"
    }
    catch [Exception]
    {
        Write-host "Cloning failed"
        PrintError $_ 
    }
    cd DashDashVersion_site
    Write-Host "Git Repo Selected"
    $PathToDocumentationFolder = Get-Location;
    Remove-Item -recurse "$(Get-Location)\*" -exclude CNAME,*.git
    Write-Host "Git Repo Cleared"
    Copy-Item "$($PathOfOrigin)\doc\_site\*" -Destination $PathToDocumentationFolder -recurse -Force
    Write-Host "Files added to repo"
    try
    {
        git add .
    }
    catch [Exception]
    {
        Write-host "Cloning failed"
        PrintError $_ 
    }
    try
    {
        git commit -m "New documentation generated for version: $($version.SemVer)"
    }
    catch [Exception]
    {
        Write-host "Cloning failed"
        PrintError $_ 
    }
    try
    {
        Write-Host "Git commit complete"
        git push
    }
    catch [Exception]
    {
        Write-host "Cloning failed"
        PrintError $_ 
    }
    Write-host "Select repo"
    cd DashDashVersion_site
    
}

function PrintError($Error){
    Write-Host "Exception: $($Error.Exception)"
    Write-Host "ErrorDetails: $($Error.ErrorDetails)"
    Write-Host "StackTrace: $($Error.ScriptStackTrace)"
}

Remove-Item built -Force -Recurse -ErrorAction SilentlyContinue
Remove-Item doc/index.md -Force -Recurse -ErrorAction SilentlyContinue  
Remove-Item doc/_site -Force -Recurse -ErrorAction SilentlyContinue
Remove-Item doc/obj -Force -Recurse -ErrorAction SilentlyContinue    
dotnet clean 
dotnet restore
dotnet test /p:CollectCoverage=true /p:Exclude=[xunit.*]* /p:CoverletOutput='../../built/DashDashVersion.xml' /p:CoverletOutputFormat=cobertura

$version = Get-Version
Write-Host "calculated version:"
$version | Format-List
New-Package $version

if (Test-CIBuild) {
    if(-not (Test-PullRequest) -and (Test-WindowsCIBuild)) {
        Write-Host "Windows build detected"
        if (Test-Path "./.git/refs/tags/$($version.SemVer)") {
            Write-Host "Tag: $($version.SemVer) is already pressent in the repository!"
        }
        else
        {
            Set-Tag $version
        }

        if (-not (Test-FeatureBranch)) {
            Export-Package
        }

        New-Documentation
        Publish-Documentation $version     
    }
} else {
    New-Documentation
    Publish-Documentation $version
}
