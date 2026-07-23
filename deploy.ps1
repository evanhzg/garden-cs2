$ErrorActionPreference = "Stop"

$ftpServer = "ftp://baroque.dathost.net:21"
$ftpUser = "67fd3fd5caae0fdc8408ff64"
$ftpPass = "iyoGJKy0aEQ"
$localDir = "C:\Users\evan\Documents\CS2 Mod Dev\Garden-retakes\RetakesPlugin\bin\Debug\net10.0"
$remoteDir = "/addons/counterstrikesharp/plugins/RetakesPlugin"

# Files to upload
$filesToUpload = @(
    "RetakesPlugin.dll",
    "GardenRetakesCore.dll",
    "GardenRankingsCore.dll",
    "RetakesAllocatorCore.dll",
    "RetakesAllocatorShared.dll",
    "RetakesPluginShared.dll",
    "Fleck.dll"
)

# 1. Build the project
Write-Host "Building project..." -ForegroundColor Cyan
dotnet build -c Debug "C:\Users\evan\Documents\CS2 Mod Dev\Garden-retakes\RetakesPlugin\RetakesPlugin.csproj"

# 2. Create the remote directory if it doesn't exist
Write-Host "`nEnsuring remote directory exists..." -ForegroundColor Cyan
try {
    $mkdirUri = New-Object System.Uri("$ftpServer$remoteDir")
    $mkdirReq = [System.Net.FtpWebRequest]::Create($mkdirUri)
    $mkdirReq.Credentials = New-Object System.Net.NetworkCredential($ftpUser, $ftpPass)
    $mkdirReq.Method = [System.Net.WebRequestMethods+Ftp]::MakeDirectory
    $mkdirResp = $mkdirReq.GetResponse()
    $mkdirResp.Close()
    Write-Host "Created remote directory."
} catch {
    Write-Host "Directory might already exist or could not be created."
}

# 3. Upload the DLLs
Write-Host "`nStarting FTP Upload..." -ForegroundColor Cyan

foreach ($file in $filesToUpload) {
    $localFile = Join-Path $localDir $file
    $remoteFile = "$ftpServer$remoteDir/$file"
    
    if (-not (Test-Path $localFile)) {
        Write-Host "Warning: $file not found locally. Skipping." -ForegroundColor Yellow
        continue
    }

    Write-Host "Uploading $file..."
    
    $uri = New-Object System.Uri($remoteFile)
    $webclient = New-Object System.Net.WebClient
    $webclient.Credentials = New-Object System.Net.NetworkCredential($ftpUser, $ftpPass)
    
    try {
        $webclient.UploadFile($uri, $localFile)
        Write-Host "  -> Successfully uploaded $file" -ForegroundColor Green
    } catch {
        Write-Host "  -> Failed to upload $file : $_" -ForegroundColor Red
    }
}

Write-Host "`nDeployment complete!" -ForegroundColor Cyan
